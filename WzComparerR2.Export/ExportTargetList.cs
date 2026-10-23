using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace WzComparerR2.Export
{
    /// <summary>批量清单里的一行：要导出的节点 + 可选的输出子目录。</summary>
    internal sealed class ExportTargetEntry
    {
        public string NodePath { get; set; }

        /// <summary>可选的输出子目录（'/' 分隔），为空时按节点路径推导。</summary>
        public string SubDirectory { get; set; }

        public int LineNumber { get; set; }
    }

    /// <summary>
    /// `--list &lt;file&gt;` 的清单解析 + `--skip` 的通配符匹配。
    /// </summary>
    internal static class ExportTargetList
    {
        private static readonly char[] Separators = { ',', ';' };

        /// <summary>
        /// 读清单文件。每行一个节点路径，`#` 开头为注释；用 `-&gt;` 指定输出子目录：
        /// <code>
        /// # 注释
        /// UIWindow.img/Main
        /// Basic.img/BtOK -> buttons/ok
        /// </code>
        /// </summary>
        public static List<ExportTargetEntry> Read(string path, ExportLogger logger)
        {
            var entries = new List<ExportTargetEntry>();
            string[] lines = File.ReadAllLines(path, Encoding.UTF8);

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }

                string nodePath = line;
                string subDirectory = null;

                int arrow = line.IndexOf("->", StringComparison.Ordinal);
                if (arrow >= 0)
                {
                    nodePath = line.Substring(0, arrow).Trim();
                    subDirectory = line.Substring(arrow + 2).Trim();
                    if (subDirectory.Length == 0)
                    {
                        throw new UsageException($"{Path.GetFileName(path)} 第 {i + 1} 行的 '->' 后面没有子目录名。");
                    }
                }

                if (nodePath.Length == 0)
                {
                    logger.Warn($"{Path.GetFileName(path)} 第 {i + 1} 行没有节点路径，已跳过");
                    continue;
                }

                entries.Add(new ExportTargetEntry
                {
                    NodePath = nodePath,
                    SubDirectory = subDirectory,
                    LineNumber = i + 1,
                });
            }

            return entries;
        }

        /// <summary>把 `--skip` 的值切成模式列表（逗号/分号分隔，大小写不敏感）。</summary>
        public static List<string> SplitPatterns(IEnumerable<string> rawValues)
        {
            var patterns = new List<string>();
            foreach (string raw in rawValues)
            {
                foreach (string part in raw.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
                {
                    string pattern = part.Trim();
                    if (pattern.Length > 0)
                    {
                        patterns.Add(pattern);
                    }
                }
            }
            return patterns;
        }

        /// <summary>通配符匹配：支持 `*`（任意串）与 `?`（单个字符），大小写不敏感。</summary>
        public static bool Match(string text, IReadOnlyList<string> patterns)
        {
            if (patterns == null || patterns.Count == 0 || text == null)
            {
                return false;
            }

            foreach (string pattern in patterns)
            {
                if (WildcardMatch(text, pattern))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool WildcardMatch(string text, string pattern)
        {
            int textIndex = 0;
            int patternIndex = 0;
            int starIndex = -1;
            int starMatch = 0;

            while (textIndex < text.Length)
            {
                if (patternIndex < pattern.Length
                    && (pattern[patternIndex] == '?' || CharEqualsIgnoreCase(pattern[patternIndex], text[textIndex])))
                {
                    textIndex++;
                    patternIndex++;
                }
                else if (patternIndex < pattern.Length && pattern[patternIndex] == '*')
                {
                    starIndex = patternIndex;
                    starMatch = textIndex;
                    patternIndex++;
                }
                else if (starIndex >= 0)
                {
                    patternIndex = starIndex + 1;
                    starMatch++;
                    textIndex = starMatch;
                }
                else
                {
                    return false;
                }
            }

            while (patternIndex < pattern.Length && pattern[patternIndex] == '*')
            {
                patternIndex++;
            }
            return patternIndex == pattern.Length;
        }

        private static bool CharEqualsIgnoreCase(char a, char b)
        {
            return char.ToUpperInvariant(a) == char.ToUpperInvariant(b);
        }
    }
}
