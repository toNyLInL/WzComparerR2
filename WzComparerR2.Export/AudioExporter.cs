using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using WzComparerR2.WzLib;

namespace WzComparerR2.Export
{
    /// <summary>
    /// 音频导出器（&lt;Wz_Sound&gt; → 原始字节 + audio.json + manifest.json）。
    ///
    /// 分组规则与 UiImageExporter 完全一致（一个节点的直接 <see cref="Wz_Sound"/> 子节点 = 同组音频），
    /// 只是「帧」换成音频、产物落到 &lt;out&gt;/audio/&lt;kind&gt;/、元数据叫 audio.json。
    /// 格式判定与落盘字节见 <see cref="AudioCodec"/>，字段契约见 docs/plan/spec/wz-export-schema.md §9。
    /// </summary>
    internal sealed class AudioExporter
    {
        private readonly WzAssetSource source;
        private readonly ExportOptions options;
        private readonly ExportLogger logger;
        private readonly string contentRoot;
        private readonly HashSet<string> usedDirectories;
        private readonly List<ManifestAudio> assets;
        private readonly List<AudioGroup> groups;
        private readonly List<string> targetPaths;

        /// <summary>已占用的文件名：键 = 相对目录 + '/' + 文件名（大小写不敏感，与磁盘一致）。</summary>
        private readonly Dictionary<string, string> takenFiles;
        private readonly HashSet<string> absoluteUolCandidates;

        /// <summary>尚未落盘的单曲分组：键 = 父节点符号路径，保证同一 img 下的多首曲子合并成一组。</summary>
        private readonly Dictionary<string, PendingGroup> pendingSingles;
        private readonly List<PendingGroup> pendingOrder;

        private bool dryRun;

        public AudioExporter(WzAssetSource source, ExportOptions options, ExportLogger logger,
            HashSet<string> sharedDirectories = null)
        {
            this.source = source;
            this.options = options;
            this.logger = logger;
            this.contentRoot = string.IsNullOrEmpty(options.OutputDirectory)
                ? null
                : Path.Combine(options.OutputDirectory, "audio", options.Kind);
            this.usedDirectories = sharedDirectories ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            this.assets = new List<ManifestAudio>();
            this.groups = new List<AudioGroup>();
            this.targetPaths = new List<string>();
            this.takenFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            this.absoluteUolCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            this.pendingSingles = new Dictionary<string, PendingGroup>(StringComparer.Ordinal);
            this.pendingOrder = new List<PendingGroup>();
        }

        /// <summary>本次导出的全部分组（--verify 用它逐条断言）。</summary>
        public IReadOnlyList<AudioGroup> Groups => this.groups;

        /// <summary>已导出的目标节点符号路径（--list 批量时会多于一个）。</summary>
        public IReadOnlyList<string> TargetPaths => this.targetPaths;

        /// <summary>产物根：&lt;out&gt;/audio/&lt;kind&gt;。</summary>
        public string ContentRoot => this.contentRoot;

        /// <summary>manifest 里用的相对前缀（'/' 分隔）。</summary>
        private string AudioPrefix => "audio/" + this.options.Kind + "/";

        /// <summary>
        /// 导出一个目标节点。节点本身是音频时归到「父节点目录」那个分组（与 QuickView「Save as &lt;名字&gt;.mp3」
        /// 口径一致）；否则下钻收集直接子音频。
        ///
        /// 单曲目标**不立刻落盘**，而是先按父节点攒起来：`--list` 里同时列出同一 img 下的多首曲子时，
        /// 它们应该进同一个目录、共用一份 audio.json，而不是各占一个 `A~2`、`A~3` 目录。
        /// 攒起来的分组由 <see cref="CreateManifest"/> 统一 flush（顺序按首次出现）。
        /// </summary>
        public void ExportTarget(Wz_Node startNode, string relativeRoot, bool dryRun)
        {
            this.dryRun = dryRun;
            this.targetPaths.Add(NodePathUtility.GetSymbolicPath(startNode));

            if (!this.EnsureExtracted(startNode, out Wz_Node content))
            {
                return;
            }

            var link = new WzCanvasLink();
            Wz_Node resolved = this.source.ResolveSoundNode(content, link);

            if (resolved?.Value is Wz_Sound)
            {
                var candidate = new AudioCandidate
                {
                    SoundNode = content,
                    ResolvedNode = resolved,
                    Link = link,
                    SymbolicPath = NodePathUtility.GetSymbolicPath(content),
                };
                this.AddPendingSingle(startNode, content, candidate, relativeRoot);
                return;
            }

            this.Walk(content, relativeRoot);
        }

        /// <summary>把单曲目标攒到「父节点目录」对应的待导出分组里。</summary>
        private void AddPendingSingle(Wz_Node startNode, Wz_Node content, AudioCandidate candidate, string relativeRoot)
        {
            Wz_Node parent = NodePathUtility.GetParentOrOwner(content) ?? NodePathUtility.GetParentOrOwner(startNode);
            string parentSymbolic = NodePathUtility.GetSymbolicPath(parent);
            string directory = GetParentRelativeRoot(startNode, relativeRoot);

            // 键用父节点的符号路径：同一 img 下的多首曲子归一组，不同 img 即使目录同名也不合并。
            if (!this.pendingSingles.TryGetValue(parentSymbolic, out PendingGroup pending))
            {
                pending = new PendingGroup
                {
                    Directory = directory,
                    Source = string.IsNullOrEmpty(parentSymbolic) ? candidate.SymbolicPath : parentSymbolic,
                };
                this.pendingSingles[parentSymbolic] = pending;
                this.pendingOrder.Add(pending);
            }

            pending.Candidates.Add(candidate);
        }

        /// <summary>把攒起来的单曲分组依次导出（CreateManifest 之前调用，重复调用无副作用）。</summary>
        private void FlushPendingSingles()
        {
            if (this.pendingOrder.Count == 0)
            {
                return;
            }

            var pending = new List<PendingGroup>(this.pendingOrder);
            this.pendingOrder.Clear();
            this.pendingSingles.Clear();

            foreach (PendingGroup group in pending)
            {
                this.ExportGroup(group.Candidates, group.Source, group.Directory);
            }
        }

        /// <summary>把累加的结果组装成 manifest（音频分类，见 spec §5 的 audioOnly 段）。</summary>
        public ExportManifest CreateManifest(string rootPath)
        {
            this.FlushPendingSingles();

            // 计数从分组反推，dryRun（list 模式）也能给出正确统计；落盘计数只用于日志。
            int files = 0;
            int bytes = 0;
            foreach (AudioGroup group in this.groups)
            {
                files += group.Entries.Count;
                bytes += group.Bytes;
            }

            var manifest = new ExportManifest
            {
                Schema = JsonExportWriter.SchemaVersion,
                ToolVersion = Program.ToolVersion,
                GeneratedAtUtc = DateTime.UtcNow,
                Kind = this.options.Kind,
                Root = rootPath,
                AudioGroups = this.groups.Count,
                AudioFiles = files,
                AudioBytes = bytes,
            };

            foreach (string name in this.source.GetFileNames())
            {
                manifest.Files.Add(new WzFileInfo { File = name, Version = this.source.WzVersion });
            }
            manifest.Audio.AddRange(this.assets);
            manifest.Targets.AddRange(this.targetPaths);
            return manifest;
        }

        /// <summary>
        /// 纯音频清单（audioOnly）的写入位置：`<out>/manifest.json` 里已经有图片分类的清单时，
        /// 写到 `<out>/audio/manifest.json`，免得把图片那份盖掉；否则直接用 `<out>/manifest.json`。
        /// </summary>
        public string GetManifestPath(string outputDirectory)
        {
            string rootManifest = Path.Combine(outputDirectory, "manifest.json");
            if (!File.Exists(rootManifest))
            {
                return rootManifest;
            }

            try
            {
                JsonNode existing = JsonReader.Parse(File.ReadAllText(rootManifest, Encoding.UTF8));
                if (existing.GetBool("audioOnly") != true)
                {
                    // 已有的是图片清单：音频清单另存一份，verify --out 会两份都读。
                    return Path.Combine(outputDirectory, "audio", "manifest.json");
                }
            }
            catch (Exception)
            {
                // 旧清单损坏时按「不是音频清单」处理：宁可多写一个文件，也不要覆盖掉别人的产物。
                return Path.Combine(outputDirectory, "audio", "manifest.json");
            }

            return rootManifest;
        }

        /// <summary>
        /// 所有 --wz 都加载完之后再报告「绝对路径 UOL 解析不了」：候选里首段不是任何已加载 wz 名的，
        /// 多半是对应分组文件夹没一起传进来（v271 的 `/Item/Cash/...` 常省略 `.wz`），逐条报错会误导；
        /// 首段是已加载文件的真正死链，已经在 <see cref="GetSoundChildren"/> 里逐条警告过了。
        /// </summary>
        public void FlushAbsoluteUolCandidates()
        {
            if (this.absoluteUolCandidates.Count == 0)
            {
                return;
            }

            var unknownSegments = new List<string>();
            foreach (string path in this.absoluteUolCandidates)
            {
                string[] segments = NodePathUtility.Split(path.TrimStart('/'));
                if (segments.Length > 0 && !this.source.IsKnownFileSegment(segments[0]))
                {
                    unknownSegments.Add(path);
                }
            }

            if (unknownSegments.Count > 0)
            {
                int shown = Math.Min(3, unknownSegments.Count);
                var sample = new List<string>(shown);
                for (int i = 0; i < shown; i++)
                {
                    sample.Add(unknownSegments[i]);
                }

                this.logger.Warn($"有 {unknownSegments.Count} 处绝对路径 UOL 的首段不是已加载的 wz 名（例：{string.Join("、", sample)}）；" +
                    "把对应分组文件夹也加进 --wz 后重跑即可解析，本次按相对路径回退或跳过");
            }
        }

        private void Walk(Wz_Node node, string relativeDir)
        {
            if (!this.EnsureExtracted(node, out Wz_Node content))
            {
                return;
            }

            List<AudioCandidate> entries = this.GetSoundChildren(content);

            if (entries.Count > 0)
            {
                this.ExportGroup(entries, NodePathUtility.GetSymbolicPath(content), relativeDir);
                this.logger.Detail($"音频分组 {relativeDir}（{entries.Count} 个音频节点）<- {NodePathUtility.GetSymbolicPath(content)}");
            }

            foreach (Wz_Node child in content.Nodes)
            {
                if (ContainsNode(entries, child))
                {
                    // 已经是本组条目，不再当作目录下钻。
                    continue;
                }

                if (ExportTargetList.Match(child.Text, this.options.SkipPatterns))
                {
                    this.logger.Detail($"跳过 {CombinePath(relativeDir, child.Text)}（--skip）");
                    continue;
                }

                string childDir = CombinePath(relativeDir, ExportNaming.SanitizeSegment(NodePathUtility.StripImgSuffix(child.Text)));
                this.Walk(child, childDir);
            }
        }

        /// <summary>img 是懒解压的：下钻前必须解压，并换成内容根节点（与图片导出器同一套语义）。</summary>
        private bool EnsureExtracted(Wz_Node node, out Wz_Node content)
        {
            content = node;

            Wz_Image image = node.GetValue<Wz_Image>();
            if (image == null)
            {
                return true;
            }

            if (!image.TryExtract(out Exception ex))
            {
                this.logger.Warn($"img 解压失败：{NodePathUtility.GetSymbolicPath(node)}（{NodePathUtility.Describe(ex)}）");
                return false;
            }

            content = image.Node ?? node;
            return true;
        }

        private List<AudioCandidate> GetSoundChildren(Wz_Node node)
        {
            var entries = new List<AudioCandidate>();

            foreach (Wz_Node child in node.Nodes)
            {
                // 目录节点没有 Value；带 _inlink/_outlink 的「空节点」也可能是音频，交给解析器判断。
                if (child.Value == null && NodePathUtility.GetLinkPath(child) == null)
                {
                    continue;
                }

                if (ExportTargetList.Match(child.Text, this.options.SkipPatterns))
                {
                    continue;
                }

                var link = new WzCanvasLink();
                Wz_Node resolved = this.source.ResolveSoundNode(child, link);

                if (resolved?.Value is Wz_Sound)
                {
                    entries.Add(new AudioCandidate
                    {
                        SoundNode = child,
                        ResolvedNode = resolved,
                        Link = link,
                        SymbolicPath = NodePathUtility.GetSymbolicPath(child),
                    });
                }
                else if (link.Error != null)
                {
                    if (IsUnresolvedAbsoluteUol(link.Error))
                    {
                        this.absoluteUolCandidates.Add(link.UolPath ?? link.Error);
                    }
                    else
                    {
                        this.logger.Warn($"{NodePathUtility.GetSymbolicPath(child)}：{link.Error}，已跳过");
                    }
                }
            }

            return entries;
        }

        private static bool IsUnresolvedAbsoluteUol(string error)
        {
            return error != null && error.StartsWith("UOL 无法解析：/", StringComparison.Ordinal);
        }

        private void ExportGroup(IReadOnlyList<AudioCandidate> candidates, string groupSource, string relativeRoot)
        {
            string directory = this.AllocateDirectory(relativeRoot);
            string physicalDir = null;

            if (!this.dryRun)
            {
                physicalDir = ExportNaming.FitDirectoryPath(this.contentRoot, directory);
                Directory.CreateDirectory(physicalDir);
            }

            // 先整组解码、统一定名，再落盘：同目录去重要看「组内已经出现过哪些内容」。
            var decoded = new List<DecodedAudio>();
            foreach (AudioCandidate candidate in candidates)
            {
                try
                {
                    decoded.Add(this.Decode(candidate));
                }
                catch (Exception ex)
                {
                    this.logger.Warn($"{candidate.SymbolicPath} 解码失败：{NodePathUtility.Describe(ex)}");
                }
            }

            List<string> names = AllocateFileNames(directory, decoded, this.takenFiles, this.logger);

            var group = new AudioGroup
            {
                Kind = this.options.Kind,
                Source = groupSource,
                RelativeDir = directory,
            };

            for (int i = 0; i < decoded.Count; i++)
            {
                DecodedAudio item = decoded[i];
                AudioPayload payload = item.Payload;

                var entry = new AudioEntry
                {
                    Index = group.Entries.Count,
                    Name = item.Candidate.SoundNode.Text ?? string.Empty,
                    File = names[i],
                    Format = payload.Format,
                    Codec = payload.Codec,
                    Channels = payload.Channels,
                    SampleRate = payload.SampleRate,
                    BitsPerSample = payload.BitsPerSample,
                    BlockAlign = payload.BlockAlign,
                    Duration = payload.SoundMs > 0 ? payload.SoundMs : EstimateDuration(payload, item.Data.Length),
                    Bytes = item.Data.Length,
                    Sha256 = item.Hash,
                    Source = item.Candidate.SymbolicPath,
                    Uol = item.Candidate.Link?.UolPath,
                    Link = item.Candidate.Link?.LinkPath,
                    Inlink = item.Candidate.Link?.IsInlink ?? false,
                    LinkError = item.Candidate.Link?.Error,
                };

                if (!this.dryRun)
                {
                    if (physicalDir.Length + 1 + names[i].Length > ExportNaming.MaxFullPathLength)
                    {
                        physicalDir = ExportNaming.FitDirectoryPath(this.contentRoot, directory);
                    }
                    File.WriteAllBytes(Path.Combine(physicalDir, names[i]), item.Data);
                }

                group.Entries.Add(entry);
            }

            if (group.Entries.Count == 0)
            {
                this.logger.Detail($"跳过空音频分组：{groupSource}");
                return;
            }

            foreach (AudioEntry entry in group.Entries)
            {
                group.Bytes += entry.Bytes;
            }
            group.MetaRelativePath = this.AudioPrefix + directory + "/audio.json";

            string metaText = JsonExportWriter.WriteAudio(group);

            if (!this.dryRun)
            {
                File.WriteAllText(Path.Combine(physicalDir, "audio.json"), metaText, new UTF8Encoding(false));
            }

            this.groups.Add(group);
            this.assets.Add(new ManifestAudio
            {
                Source = groupSource,
                Dir = this.AudioPrefix + directory,
                Meta = group.MetaRelativePath,
                Files = group.Entries.Count,
                Bytes = group.Bytes,
                Hash = ExportNaming.Sha256(metaText),
            });

            if (this.dryRun)
            {
                this.logger.Info($"  {this.AudioPrefix}{directory}  <-  {groupSource}  [{group.Entries.Count} 个音频]");
            }
            else
            {
                this.logger.Detail($"导出音频 {directory}（{group.Entries.Count} 个）<- {groupSource}");
            }
        }

        /// <summary>取原始字节并判定容器：用 CopyTo 取字节，再交给 AudioCodec 决定 mp3 原样 / wav 补头 / bin 原样。</summary>
        private DecodedAudio Decode(AudioCandidate candidate)
        {
            Wz_Sound sound = candidate.ResolvedNode.Value as Wz_Sound;
            if (sound == null)
            {
                throw new InvalidDataException("解析后的节点不是 Wz_Sound。");
            }
            if (sound.DataLength <= 0)
            {
                throw new InvalidDataException("音频数据长度为 0。");
            }
            if (sound.DataLength > this.options.MaxAudioBytes)
            {
                throw new InvalidDataException($"音频过大（{sound.DataLength} 字节，上限 {this.options.MaxAudioBytes}，可用 --max-audio-bytes 调整）");
            }

            // 不用 ExtractSound()：MEDIATYPE_Audio 的 MP3 与 Binary 型它都返回 null（见 spec §9.4）。
            var raw = new byte[sound.DataLength];
            sound.CopyTo(raw, 0);

            AudioPayload payload = AudioCodec.Read(sound, raw);
            return new DecodedAudio
            {
                Candidate = candidate,
                Payload = payload,
                Data = payload.Data,
                Hash = ExportNaming.Sha256Bytes(payload.Data),
            };
        }

        /// <summary>
        /// 给一组音频分配文件名（规则见 spec §6、§9.4）：
        ///   * 默认名 = 清洗后的节点名 + 扩展名；
        ///   * 同一段音频（内容 hash 相同）在同目录里只落一份，共用同一个文件名；
        ///   * 不同内容撞上同一个文件名时追加 `~2`、`~3`，并记一条警告（只有 `--list` 把多个节点
        ///     重定向到同一目录时才可能发生，正常导出不会）。
        /// </summary>
        private static List<string> AllocateFileNames(string directory, IReadOnlyList<DecodedAudio> decoded,
            IDictionary<string, string> takenFiles, ExportLogger logger)
        {
            var names = new List<string>(decoded.Count);
            var contentName = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (DecodedAudio item in decoded)
            {
                if (contentName.TryGetValue(item.Hash, out string shared))
                {
                    names.Add(shared);
                    continue;
                }

                string baseName = GetBaseName(item);
                string extension = "." + item.Payload.Extension;
                string fileName = ReserveName(directory, baseName, extension, item.Hash, takenFiles, logger);
                contentName[item.Hash] = fileName;
                names.Add(fileName);
            }

            return names;
        }

        private static string GetBaseName(DecodedAudio item)
        {
            string name = ExportNaming.SanitizeSegment(NodePathUtility.StripImgSuffix(item.Candidate.SoundNode?.Text));
            return string.IsNullOrEmpty(name) || name == "_" ? "sound" : name;
        }

        private static string ReserveName(string directory, string baseName, string extension, string hash,
            IDictionary<string, string> takenFiles, ExportLogger logger)
        {
            for (int i = 1; ; i++)
            {
                string name = (i == 1 ? baseName : baseName + "~" + i) + extension;
                string key = directory + "/" + name;
                if (!takenFiles.ContainsKey(key))
                {
                    takenFiles[key] = hash;
                    if (i > 1)
                    {
                        logger.Warn($"音频同名不同内容，已改用 {directory}/{name}（原：{baseName}{extension}）");
                    }
                    return name;
                }
            }
        }

        /// <summary>wz 没记时长时按码流估算：PCM 用采样数，MP3 按 128kbps。</summary>
        private static int EstimateDuration(AudioPayload payload, int dataLength)
        {
            if (payload.Format == AudioPayload.FormatWav && payload.AvgBytesPerSec > 0)
            {
                return (int)((long)dataLength * 1000 / payload.AvgBytesPerSec);
            }
            if (payload.Format == AudioPayload.FormatMp3)
            {
                return dataLength * 8 / 128;
            }
            return 0;
        }

        private string AllocateDirectory(string relativeDir)
        {
            string directory = string.IsNullOrEmpty(relativeDir) ? "root" : relativeDir;
            if (this.usedDirectories.Add(directory))
            {
                return directory;
            }

            for (int i = 2; ; i++)
            {
                string candidate = directory + "~" + i;
                if (this.usedDirectories.Add(candidate))
                {
                    this.logger.Warn($"目录重名，已改用 {candidate}（原：{directory}）");
                    return candidate;
                }
            }
        }

        /// <summary>单节点音频的目录 = 父节点路径；父级解析不出来时退回目标节点自己的相对路径。</summary>
        private static string GetParentRelativeRoot(Wz_Node startNode, string relativeRoot)
        {
            Wz_Node parent = NodePathUtility.GetParentOrOwner(startNode);
            if (parent == null || string.IsNullOrEmpty(parent.Text))
            {
                return relativeRoot;
            }

            string[] segments = NodePathUtility.Split(NodePathUtility.GetSymbolicPath(parent));
            int start = 0;
            if (segments.Length > 0 && segments[0].IndexOf('.') >= 0)
            {
                start = 1;
            }

            var parts = new List<string>();
            for (int i = start; i < segments.Length; i++)
            {
                parts.Add(ExportNaming.SanitizeSegment(NodePathUtility.StripImgSuffix(segments[i])));
            }
            return parts.Count == 0 ? relativeRoot : string.Join("/", parts);
        }

        private static bool ContainsNode(List<AudioCandidate> entries, Wz_Node node)
        {
            foreach (AudioCandidate candidate in entries)
            {
                if (ReferenceEquals(candidate.SoundNode, node))
                {
                    return true;
                }
            }
            return false;
        }

        private static string CombinePath(string left, string right)
        {
            if (string.IsNullOrEmpty(left))
            {
                return right;
            }
            if (string.IsNullOrEmpty(right))
            {
                return left;
            }
            return left + "/" + right;
        }

        private sealed class AudioCandidate
        {
            public Wz_Node SoundNode { get; set; }

            public Wz_Node ResolvedNode { get; set; }

            public WzCanvasLink Link { get; set; }

            public string SymbolicPath { get; set; }
        }

        /// <summary>攒起来的一批单曲目标：同一父节点下的多首曲子合成一个分组。</summary>
        private sealed class PendingGroup
        {
            public PendingGroup()
            {
                this.Candidates = new List<AudioCandidate>();
            }

            public string Directory { get; set; }

            /// <summary>分组的 wz 符号路径，取父节点（音频自己的路径逐条记在 entries[].source）。</summary>
            public string Source { get; set; }

            public List<AudioCandidate> Candidates { get; }
        }

        private sealed class DecodedAudio
        {
            public AudioCandidate Candidate { get; set; }

            public AudioPayload Payload { get; set; }

            public byte[] Data { get; set; }

            public string Hash { get; set; }
        }
    }
}
