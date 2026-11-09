using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WzComparerR2.Export
{
    /// <summary>
    /// 导出后的断言（计划 §8 的第 1、2 条）：
    ///   * 尺寸 = meta 声明值、帧数 = 分组帧数、z/delay/a0/a1 在合法区间、PNG 能被 GDI+ 重新打开；
    ///   * 同一输入二次导出逐字节一致（manifest.json 除外，它带时间戳）。
    /// </summary>
    internal static class ExportVerifier
    {
        private const int MinZ = -9999;
        private const int MaxZ = 9999;

        public static List<string> VerifyOutput(ExportManifest manifest, IReadOnlyList<MetaGroup> groups, string contentRoot)
        {
            var problems = new List<string>();
            int frameTotal = 0;

            foreach (MetaGroup group in groups)
            {
                string dir = ExportNaming.FitDirectoryPath(contentRoot, group.RelativeDir);
                string dirKey = "sprite/" + group.Kind + "/" + group.RelativeDir;
                string metaPath = Path.Combine(dir, "meta.json");

                if (!File.Exists(metaPath))
                {
                    problems.Add($"缺少 meta.json：{dir}");
                }
                else
                {
                    ManifestAsset asset = manifest.Assets.FirstOrDefault(a => string.Equals(a.Dir, dirKey, StringComparison.OrdinalIgnoreCase));
                    if (asset == null)
                    {
                        problems.Add($"manifest 里没有登记：{dirKey}");
                    }
                    else
                    {
                        string actualHash = ExportNaming.Sha256File(metaPath);
                        if (!string.Equals(asset.Hash, actualHash, StringComparison.Ordinal))
                        {
                            problems.Add($"meta.json 与 manifest 记录的 hash 不一致：{dirKey}");
                        }
                        if (asset.Frames != group.Frames.Count)
                        {
                            problems.Add($"manifest 帧数与 meta 不一致：{dirKey}");
                        }
                    }
                }

                for (int i = 0; i < group.Frames.Count; i++)
                {
                    MetaFrame frame = group.Frames[i];
                    string pngPath = Path.Combine(dir, frame.Png);

                    if (frame.Index != i)
                    {
                        problems.Add($"帧序号不连续：{dirKey}/{frame.Png}（index={frame.Index}，位置={i}）");
                    }
                    if (!File.Exists(pngPath))
                    {
                        problems.Add($"缺少 PNG：{dirKey}/{frame.Png}");
                        continue;
                    }

                    try
                    {
                        PngSaver.ReadSize(pngPath, out int width, out int height);
                        if (width != frame.Width || height != frame.Height)
                        {
                            problems.Add($"PNG 尺寸与 meta 不一致：{dirKey}/{frame.Png}（meta {frame.Width}x{frame.Height}，实际 {width}x{height}）");
                        }
                    }
                    catch (Exception ex)
                    {
                        problems.Add($"PNG 无法重新打开：{dirKey}/{frame.Png}（{NodePathUtility.Describe(ex)}）");
                    }

                    if (frame.Z < MinZ || frame.Z > MaxZ)
                    {
                        problems.Add($"z 超出 [{MinZ}, {MaxZ}]：{dirKey}/{frame.Png}（z={frame.Z}）");
                    }
                    // 真实客户端会用 600000、180000 这类超长 delay 表示"定格"，
                    // 这里只保证是正数毫秒，上界不做断言（见 docs/plan/spec 的字段说明）。
                    if (frame.Delay <= 0)
                    {
                        problems.Add($"delay 必须为正数毫秒：{dirKey}/{frame.Png}（delay={frame.Delay}）");
                    }
                    if (frame.A0 < 0 || frame.A0 > 255 || frame.A1 < 0 || frame.A1 > 255)
                    {
                        problems.Add($"a0/a1 超出 [0, 255]：{dirKey}/{frame.Png}（a0={frame.A0}，a1={frame.A1}）");
                    }
                }

                frameTotal += group.Frames.Count;
            }

            if (manifest.Groups != groups.Count)
            {
                problems.Add($"manifest.groups 与实际分组数不一致：{manifest.Groups} != {groups.Count}");
            }
            if (manifest.Frames != frameTotal)
            {
                problems.Add($"manifest.frames 与实际帧数不一致：{manifest.Frames} != {frameTotal}");
            }

            return problems;
        }

        /// <summary>
        /// 音频的导出后断言（spec §9.5）：audio.json 的 hash 与 manifest 一致、每个文件大小与 SHA-256 对得上、
        /// 容器签名可识别、时长/声道/采样率在合法区间、计数一致。
        /// </summary>
        public static List<string> VerifyAudioOutput(ExportManifest manifest, IReadOnlyList<AudioGroup> groups, string contentRoot)
        {
            var problems = new List<string>();
            int fileTotal = 0;

            foreach (AudioGroup group in groups)
            {
                string dir = ExportNaming.FitDirectoryPath(contentRoot, group.RelativeDir);
                string dirKey = "audio/" + group.Kind + "/" + group.RelativeDir;
                string metaPath = Path.Combine(dir, "audio.json");

                if (!File.Exists(metaPath))
                {
                    problems.Add($"缺少 audio.json：{dir}");
                }
                else
                {
                    ManifestAudio asset = manifest.Audio.FirstOrDefault(a => string.Equals(a.Dir, dirKey, StringComparison.OrdinalIgnoreCase));
                    if (asset == null)
                    {
                        problems.Add($"manifest 里没有登记：{dirKey}");
                    }
                    else
                    {
                        if (!string.Equals(asset.Hash, ExportNaming.Sha256File(metaPath), StringComparison.Ordinal))
                        {
                            problems.Add($"audio.json 与 manifest 记录的 hash 不一致：{dirKey}");
                        }
                        if (asset.Files != group.Entries.Count)
                        {
                            problems.Add($"manifest 音频文件数与 audio.json 不一致：{dirKey}");
                        }
                        if (asset.Bytes != group.Bytes)
                        {
                            problems.Add($"manifest 音频字节数与 audio.json 不一致：{dirKey}");
                        }
                    }
                }

                for (int i = 0; i < group.Entries.Count; i++)
                {
                    AudioEntry entry = group.Entries[i];
                    string filePath = Path.Combine(dir, entry.File);
                    string label = $"{dirKey}/{entry.File}";

                    if (fileTotal >= 0)
                    {
                        fileTotal++;
                    }
                    if (entry.Index != i)
                    {
                        problems.Add($"音频序号不连续：{label}（index={entry.Index}，位置={i}）");
                    }
                    if (!File.Exists(filePath))
                    {
                        problems.Add($"缺少音频文件：{label}");
                        continue;
                    }

                    var info = new FileInfo(filePath);
                    if (info.Length != entry.Bytes)
                    {
                        problems.Add($"音频文件大小与 audio.json 不一致：{label}（{info.Length} != {entry.Bytes}）");
                    }
                    if (!string.Equals(ExportNaming.Sha256File(filePath), entry.Sha256, StringComparison.Ordinal))
                    {
                        problems.Add($"音频文件内容与 audio.json 的 sha256 不一致：{label}");
                    }

                    byte[] data = File.ReadAllBytes(filePath);
                    string formatProblem = AudioSignatures.CheckFormat(entry.Format, data);
                    if (formatProblem != null)
                    {
                        problems.Add($"音频格式不可识别：{label}（{formatProblem}）");
                    }

                    CheckAudioRanges(entry, label, problems);
                }
            }

            if (manifest.AudioGroups != groups.Count)
            {
                problems.Add($"manifest.audioGroups 与实际音频分组数不一致：{manifest.AudioGroups} != {groups.Count}");
            }
            if (manifest.AudioFiles != fileTotal)
            {
                problems.Add($"manifest.audioFiles 与实际音频数不一致：{manifest.AudioFiles} != {fileTotal}");
            }

            return problems;
        }

        /// <summary>音频字段区间：声道 1~32、采样率 8k~192k（mp3 的后端上报值不校验）、时长 &gt; 0。</summary>
        public static void CheckAudioRanges(AudioEntry entry, string label, List<string> problems)
        {
            if (entry.Channels != null && (entry.Channels.Value < 1 || entry.Channels.Value > 32))
            {
                problems.Add($"声道数超出 [1, 32]：{label}（channels={entry.Channels}）");
            }
            if (entry.SampleRate != null && entry.Format != AudioPayload.FormatMp3
                && (entry.SampleRate.Value < 8000 || entry.SampleRate.Value > 192000))
            {
                problems.Add($"采样率超出 [8000, 192000]：{label}（sampleRate={entry.SampleRate}）");
            }
            if (entry.Duration <= 0)
            {
                problems.Add($"duration 必须为正数毫秒：{label}（duration={entry.Duration}）");
            }
            if (entry.Bytes <= 0)
            {
                problems.Add($"音频文件为空：{label}");
            }
            if (string.IsNullOrEmpty(entry.Sha256) || entry.Sha256.Length != 64)
            {
                problems.Add($"sha256 字段无效：{label}");
            }
        }

        /// <summary>逐文件比较两个导出目录（忽略 manifest.json，它含导出时间戳）。</summary>
        public static List<string> CompareTrees(string leftRoot, string rightRoot)
        {
            return CompareTrees(leftRoot, rightRoot, new[] { "manifest.json" });
        }

        public static List<string> CompareTrees(string leftRoot, string rightRoot, IReadOnlyList<string> ignoredNames)
        {
            var problems = new List<string>();
            var left = CollectFiles(leftRoot, ignoredNames);
            var right = CollectFiles(rightRoot, ignoredNames);

            foreach (KeyValuePair<string, string> pair in left)
            {
                if (!right.TryGetValue(pair.Key, out string otherPath))
                {
                    problems.Add($"二次导出缺少文件：{pair.Key}");
                    continue;
                }

                var leftInfo = new FileInfo(pair.Value);
                var rightInfo = new FileInfo(otherPath);
                if (leftInfo.Length != rightInfo.Length)
                {
                    problems.Add($"文件长度不一致：{pair.Key}（{leftInfo.Length} != {rightInfo.Length}）");
                    continue;
                }

                if (!string.Equals(ExportNaming.Sha256File(pair.Value), ExportNaming.Sha256File(otherPath), StringComparison.Ordinal))
                {
                    problems.Add($"文件内容不一致：{pair.Key}");
                }
            }

            foreach (string name in right.Keys)
            {
                if (!left.ContainsKey(name))
                {
                    problems.Add($"二次导出多出文件：{name}");
                }
            }

            return problems;
        }

        private static Dictionary<string, string> CollectFiles(string root, IReadOnlyList<string> ignoredNames)
        {
            var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!Directory.Exists(root))
            {
                return files;
            }

            string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                string full = Path.GetFullPath(path);
                string relative = full.Substring(prefix.Length).Replace('\\', '/');

                if (IsIgnoredManifest(relative, ignoredNames))
                {
                    // 含导出时间戳（或由另一种分类的清单负责），不参与字节比对
                    continue;
                }
                files[relative] = full;
            }
            return files;
        }

        private static bool IsIgnoredManifest(string relative, IReadOnlyList<string> ignoredNames)
        {
            foreach (string name in ignoredNames)
            {
                if (string.Equals(relative, name, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
