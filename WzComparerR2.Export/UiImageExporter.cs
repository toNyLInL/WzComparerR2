using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using WzComparerR2.WzLib;

namespace WzComparerR2.Export
{
    /// <summary>
    /// UI 类静态图片导出器（canvas → PNG + meta.json + manifest.json）。
    ///
    /// 分组规则与 FrameAnimationData/Frame 的语义保持一致：
    ///   * 一个节点的直接子节点里，凡是能解析到 Wz_Png 的，都是同一个分组里的帧（0、1、2…）；
    ///   * 帧自己的 origin/z/delay/a0/a1 子属性就是这一帧的元数据；
    ///   * 分组目录 = 该节点在 wz 里的相对路径，帧文件名 = 子节点名。
    /// 这样 UI.wz 的 "backgrnd/0"、"backgrnd/1" 与 "Ani/0"、"Ani/1" 两种常见结构都能落到 §4 的目录形态。
    /// </summary>
    internal sealed class UiImageExporter
    {
        private readonly WzAssetSource source;
        private readonly ExportOptions options;
        private readonly ExportLogger logger;
        private readonly string contentRoot;
        private readonly HashSet<string> usedDirectories;
        private readonly List<ManifestAsset> assets;
        private readonly List<MetaGroup> groups;

        private bool dryRun;
        private int groupCount;
        private int frameCount;
        private int pngFileCount;
        private int longDelayFrameCount;

        public UiImageExporter(WzAssetSource source, ExportOptions options, ExportLogger logger)
        {
            this.source = source;
            this.options = options;
            this.logger = logger;
            this.contentRoot = string.IsNullOrEmpty(options.OutputDirectory)
                ? null
                : Path.Combine(options.OutputDirectory, "sprite", options.Kind);
            this.usedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            this.assets = new List<ManifestAsset>();
            this.groups = new List<MetaGroup>();
        }

        /// <summary>本次导出的全部分组（--verify 用它做逐帧断言）。</summary>
        public IReadOnlyList<MetaGroup> Groups => this.groups;

        /// <summary>产物根：&lt;out&gt;/sprite/&lt;kind&gt;。</summary>
        public string ContentRoot => this.contentRoot;

        /// <summary>manifest 里用的相对前缀（'/' 分隔）。</summary>
        private string SpritePrefix => "sprite/" + this.options.Kind + "/";

        public ExportManifest Export(Wz_Node startNode, string relativeRoot, bool dryRun)
        {
            this.dryRun = dryRun;
            this.groupCount = 0;
            this.frameCount = 0;
            this.pngFileCount = 0;
            this.longDelayFrameCount = 0;

            // img 节点只有解压之后才有子节点，先展开导出根；解压失败就只产出空 manifest。
            bool extracted = this.EnsureExtracted(startNode, out Wz_Node startContent);

            var startLink = new WzCanvasLink();
            Wz_Node startCanvas = extracted ? this.source.ResolveCanvasNode(startContent, startLink) : null;

            if (startCanvas?.Value is Wz_Png)
            {
                // 导出根本身就是一张 canvas：单独成组，目录即导出根。
                var candidate = new FrameCandidate
                {
                    FrameNode = startContent,
                    CanvasNode = startCanvas,
                    Link = startLink,
                    SymbolicPath = NodePathUtility.GetSymbolicPath(startContent),
                };
                this.ExportGroup(new[] { candidate }, NodePathUtility.GetSymbolicPath(startContent), relativeRoot);
            }
            else if (extracted)
            {
                this.Walk(startContent, relativeRoot);
            }

            var manifest = new ExportManifest
            {
                Schema = JsonExportWriter.SchemaVersion,
                ToolVersion = Program.ToolVersion,
                GeneratedAtUtc = DateTime.UtcNow,
                Kind = this.options.Kind,
                Root = NodePathUtility.GetSymbolicPath(startNode),
                Groups = this.groupCount,
                Frames = this.frameCount,
                PngFiles = this.pngFileCount,
            };

            foreach (string name in this.source.GetFileNames())
            {
                manifest.Files.Add(new WzFileInfo { File = name, Version = this.source.WzVersion });
            }
            manifest.Assets.AddRange(this.assets);

            if (this.longDelayFrameCount > 0)
            {
                // 客户端常用 600000ms 这类超长 delay 表示"定格"，不是异常，汇总提示一次。
                this.logger.Warn($"有 {this.longDelayFrameCount} 帧 delay > 60000ms（客户端用于定格，属正常数据）");
            }

            return manifest;
        }

        private void Walk(Wz_Node node, string relativeDir)
        {
            if (!this.EnsureExtracted(node, out Wz_Node content))
            {
                return;
            }

            List<FrameCandidate> frames = this.GetCanvasChildren(content);

            if (frames.Count > 0)
            {
                this.ExportGroup(frames, NodePathUtility.GetSymbolicPath(content), relativeDir);
                this.logger.Detail($"分组 {relativeDir}（{frames.Count} 个帧节点）<- {NodePathUtility.GetSymbolicPath(content)}");
            }

            foreach (Wz_Node child in content.Nodes)
            {
                if (IsFrameNode(frames, child))
                {
                    // 已经是上一层的帧，不再当作目录下钻。
                    continue;
                }

                string childDir = CombinePath(relativeDir, ExportNaming.SanitizeSegment(NodePathUtility.StripImgSuffix(child.Text)));
                this.Walk(child, childDir);
            }
        }

        /// <summary>
        /// img 节点是懒解压的，walk 之前必须确保已展开，并把节点换成 img 的内容根
        /// （Wz_Image.Node，它才是挂着子节点的那个节点）。解压失败就整棵子树跳过。
        /// </summary>
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

        /// <summary>收集该节点下所有能解析到 canvas 的直接子节点（即一个分组的帧）。</summary>
        private List<FrameCandidate> GetCanvasChildren(Wz_Node node)
        {
            var frames = new List<FrameCandidate>();

            foreach (Wz_Node child in node.Nodes)
            {
                // 目录节点没有 Value；带 _inlink/_outlink 的“空节点”也可能是帧，交给解析器判断。
                if (child.Value == null && NodePathUtility.GetLinkPath(child) == null)
                {
                    continue;
                }

                var link = new WzCanvasLink();
                Wz_Node canvasNode = this.source.ResolveCanvasNode(child, link);

                if (canvasNode?.Value is Wz_Png)
                {
                    frames.Add(new FrameCandidate
                    {
                        FrameNode = child,
                        CanvasNode = canvasNode,
                        Link = link,
                        SymbolicPath = NodePathUtility.GetSymbolicPath(child),
                    });
                }
                else if (link.Error != null)
                {
                    this.logger.Warn($"{NodePathUtility.GetSymbolicPath(child)}：{link.Error}，已跳过");
                }
            }

            return frames;
        }

        private void ExportGroup(IReadOnlyList<FrameCandidate> candidates, string groupSource, string relativeDir)
        {
            string directory = this.AllocateDirectory(relativeDir);
            string physicalDir = null;

            if (!this.dryRun)
            {
                physicalDir = ExportNaming.FitDirectoryPath(this.contentRoot, directory);
                Directory.CreateDirectory(physicalDir);
            }

            var group = new MetaGroup
            {
                Kind = this.options.Kind,
                Source = groupSource,
                RelativeDir = directory,
            };
            var usedFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (FrameCandidate candidate in candidates)
            {
                var png = candidate.CanvasNode.Value as Wz_Png;
                if (png == null)
                {
                    continue;
                }

                int pages = png.ActualPages;
                for (int page = 0; page < pages; page++)
                {
                    byte[] pngBytes = null;
                    MetaFrame frame;

                    try
                    {
                        frame = this.CreateFrame(candidate, png, page, out pngBytes);
                    }
                    catch (Exception ex)
                    {
                        this.logger.Warn($"{candidate.SymbolicPath}#{page} 解码失败：{NodePathUtility.Describe(ex)}");
                        continue;
                    }

                    frame.Index = group.Frames.Count;
                    frame.Png = AllocatePngFileName(usedFileNames, frame.Name, page, pages);

                    if (!this.dryRun)
                    {
                        File.WriteAllBytes(Path.Combine(physicalDir, frame.Png), pngBytes);
                        this.pngFileCount++;
                    }

                    group.Frames.Add(frame);
                }
            }

            if (group.Frames.Count == 0)
            {
                this.logger.Detail($"跳过空分组：{groupSource}");
                return;
            }

            group.Uniform = IsUniform(group.Frames);
            group.MetaRelativePath = SpritePrefix + directory + "/meta.json";

            string metaText = JsonExportWriter.WriteMeta(group);

            if (!this.dryRun)
            {
                string metaPath = Path.Combine(physicalDir, "meta.json");
                File.WriteAllText(metaPath, metaText, new UTF8Encoding(false));
            }

            this.groupCount++;
            this.frameCount += group.Frames.Count;
            this.groups.Add(group);
            this.assets.Add(new ManifestAsset
            {
                Source = groupSource,
                Dir = SpritePrefix + directory,
                Meta = group.MetaRelativePath,
                Frames = group.Frames.Count,
                Width = group.Uniform ? group.Frames[0].Width : (int?)null,
                Height = group.Uniform ? group.Frames[0].Height : (int?)null,
                Hash = ExportNaming.Sha256(metaText),
            });

            if (this.dryRun)
            {
                this.logger.Info($"  {directory}  <-  {groupSource}  [{group.Frames.Count} 帧]");
            }
            else
            {
                this.logger.Detail($"导出 {directory}（{group.Frames.Count} 帧）<- {groupSource}");
            }
        }

        private MetaFrame CreateFrame(FrameCandidate candidate, Wz_Png png, int page, out byte[] pngBytes)
        {
            var frame = new MetaFrame
            {
                Name = string.IsNullOrEmpty(candidate.FrameNode.Text) ? "frame" : candidate.FrameNode.Text,
                Page = page,
                Format = png.Format.ToString(),
                Scale = png.ActualScale,
                Pages = png.ActualPages,
                Source = candidate.SymbolicPath,
                Uol = candidate.Link?.UolPath,
                Link = candidate.Link?.LinkPath,
                Inlink = candidate.Link?.IsInlink ?? false,
                LinkError = candidate.Link?.Error,
            };

            FrameProps props = FrameProps.Read(candidate.FrameNode);
            props.FillFrom(FrameProps.Read(candidate.CanvasNode));

            frame.OriginX = props.OriginX ?? 0;
            frame.OriginY = props.OriginY ?? 0;
            frame.Z = props.Z ?? 0;
            frame.Delay = props.Delay != null && props.Delay.Value > 0 ? props.Delay.Value : DefaultDelay;
            if (frame.Delay > LongDelayThreshold)
            {
                this.longDelayFrameCount++;
            }
            frame.A0 = props.A0 ?? 255;
            frame.A1 = props.A1 ?? 255;

            if (this.dryRun)
            {
                frame.Width = png.Width;
                frame.Height = png.Height;
                pngBytes = null;
                return frame;
            }

            using (Bitmap bitmap = png.ExtractPng(page))
            {
                frame.Width = bitmap.Width;
                frame.Height = bitmap.Height;
                pngBytes = PngSaver.Encode(bitmap);
            }

            if (frame.Width != png.Width || frame.Height != png.Height)
            {
                // BC7 会按 4 对齐裁边，原始尺寸单独记录，避免下游以为图片被改小。
                frame.SourceWidth = png.Width;
                frame.SourceHeight = png.Height;
            }

            return frame;
        }

        /// <summary>
        /// 读 origin/z/delay/a0/a1。值用可空类型保存，先读帧节点、再让 canvas 节点补缺失项
        /// （_inlink 结构的属性常挂在链接节点上，实体节点只有图）。
        /// </summary>
        private struct FrameProps
        {
            public int? OriginX;
            public int? OriginY;
            public int? Z;
            public int? Delay;
            public int? A0;
            public int? A1;

            public static FrameProps Read(Wz_Node node)
            {
                var props = new FrameProps();
                if (node == null)
                {
                    return props;
                }

                Wz_Node originNode = node.Nodes["origin"]?.ResolveUol();
                if (originNode?.Value is Wz_Vector vector)
                {
                    props.OriginX = vector.X;
                    props.OriginY = vector.Y;
                }

                props.Z = TryReadInt(node, "z");
                props.Delay = TryReadInt(node, "delay");
                props.A0 = TryReadInt(node, "a0");
                props.A1 = TryReadInt(node, "a1");
                return props;
            }

            public void FillFrom(FrameProps other)
            {
                this.OriginX = this.OriginX ?? other.OriginX;
                this.OriginY = this.OriginY ?? other.OriginY;
                this.Z = this.Z ?? other.Z;
                this.Delay = this.Delay ?? other.Delay;
                this.A0 = this.A0 ?? other.A0;
                this.A1 = this.A1 ?? other.A1;
            }

            private static int? TryReadInt(Wz_Node node, string name)
            {
                Wz_Node propNode = node.Nodes[name]?.ResolveUol();
                if (propNode == null || propNode.Value == null)
                {
                    return null;
                }
                return propNode.GetValueEx<int>(0);
            }
        }

        private const int DefaultDelay = 120;

        /// <summary>超过这个值的 delay 视为"长定格"，只统计提示、不做断言。</summary>
        private const int LongDelayThreshold = 60000;

        private static bool IsUniform(List<MetaFrame> frames)
        {
            MetaFrame first = frames[0];
            foreach (MetaFrame frame in frames)
            {
                if (frame.Width != first.Width
                    || frame.Height != first.Height
                    || frame.Scale != first.Scale
                    || frame.Pages != first.Pages
                    || !string.Equals(frame.Format, first.Format, StringComparison.Ordinal))
                {
                    return false;
                }
            }
            return true;
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

        private static string AllocatePngFileName(HashSet<string> used, string frameName, int page, int pages)
        {
            string baseName = ExportNaming.SanitizeSegment(NodePathUtility.StripImgSuffix(frameName));
            if (baseName == "_")
            {
                baseName = "frame";
            }

            string candidate = pages > 1 ? $"{baseName}__p{page}" : baseName;
            string finalName = candidate;
            for (int i = 2; used.Contains(finalName); i++)
            {
                finalName = candidate + "~" + i;
            }
            used.Add(finalName);
            return finalName + ".png";
        }

        private static bool IsFrameNode(List<FrameCandidate> frames, Wz_Node node)
        {
            foreach (FrameCandidate candidate in frames)
            {
                if (ReferenceEquals(candidate.FrameNode, node))
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
    }
}
