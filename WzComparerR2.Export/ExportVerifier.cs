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

        /// <summary>逐文件比较两个导出目录（忽略 manifest.json，它含导出时间戳）。</summary>
        public static List<string> CompareTrees(string leftRoot, string rightRoot)
        {
            var problems = new List<string>();
            var left = CollectFiles(leftRoot);
            var right = CollectFiles(rightRoot);

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

        private static Dictionary<string, string> CollectFiles(string root)
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

                if (string.Equals(relative, "manifest.json", StringComparison.OrdinalIgnoreCase))
                {
                    // 含导出时间戳，不参与字节比对
                    continue;
                }
                files[relative] = full;
            }
            return files;
        }
    }
}
