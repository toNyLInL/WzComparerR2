using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace WzComparerR2.Export
{
    /// <summary>
    /// 产物路径规范化：非法字符替换、Windows 保留名规避、长度保护、重名分配。
    /// 所有落盘路径都必须过这里，避免出现 "?" / 超长路径 / 大小写撞车。
    /// </summary>
    internal static class ExportNaming
    {
        /// <summary>Windows 上 UTF-16 路径的安全上限（含文件名），留出余量。</summary>
        public const int MaxFullPathLength = 240;

        private const int MaxSegmentLength = 64;
        private const int ShortenedSegmentLength = 24;

        private static readonly HashSet<string> ReservedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        };

        /// <summary>把 wz 节点名变成安全的单段目录/文件名。</summary>
        public static string SanitizeSegment(string text)
        {
            if (text == null)
            {
                return "_";
            }

            var sb = new StringBuilder(text.Length);
            foreach (char c in text)
            {
                if (c < 0x20 || Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0)
                {
                    sb.Append('_');
                }
                else
                {
                    sb.Append(c);
                }
            }

            string result = sb.ToString().TrimEnd(' ', '.');
            if (result.Length == 0)
            {
                return "_";
            }
            if (result == "." || result == "..")
            {
                return result.Replace('.', '_');
            }
            if (ReservedNames.Contains(result))
            {
                return "_" + result;
            }
            if (result.Length > MaxSegmentLength)
            {
                return result.Substring(0, ShortenedSegmentLength) + "~" + ShortHash(result);
            }
            return result;
        }

        /// <summary>
        /// 计算导出根的相对目录：--node 给了就用它（去掉 wz 文件名段与 .img 后缀），
        /// 否则用 wz 文件/文件夹名。返回 '/' 分隔的相对路径。
        /// </summary>
        public static string GetRelativeRoot(string nodePath, string primaryPath)
        {
            string[] segments = NodePathUtility.Split(nodePath);
            if (segments.Length > 0 && primaryPath != null && IsFileNameSegment(segments[0], primaryPath))
            {
                segments = segments.Length == 1 ? Array.Empty<string>() : Slice(segments, 1);
            }

            if (segments.Length == 0)
            {
                string stem = primaryPath == null ? "wz" : Path.GetFileNameWithoutExtension(primaryPath);
                return SanitizeSegment(stem);
            }

            var parts = new List<string>(segments.Length);
            foreach (string segment in segments)
            {
                parts.Add(SanitizeSegment(NodePathUtility.StripImgSuffix(segment)));
            }
            return string.Join("/", parts);
        }

        /// <summary>把相对目录拼到输出根下，必要时缩短过长的段以避开 Windows 路径长度限制。</summary>
        public static string FitDirectoryPath(string outputRoot, string relativeDir)
        {
            string full = CombineRelative(outputRoot, relativeDir);
            if (full.Length <= MaxFullPathLength)
            {
                return full;
            }

            string[] segments = relativeDir.Split('/');
            for (int i = segments.Length - 1; i >= 0 && full.Length > MaxFullPathLength; i--)
            {
                segments[i] = ShortenSegment(segments[i]);
                full = CombineRelative(outputRoot, string.Join("/", segments));
            }

            if (full.Length > MaxFullPathLength)
            {
                throw new IOException($"输出路径过长（{full.Length} 字符），请把 --out 指到更浅的目录：{full}");
            }
            return full;
        }

        public static int HashSeed(string text)
        {
            // FNV-1a，仅用于稳定排序键，不用于安全用途
            unchecked
            {
                uint hash = 2166136261;
                foreach (char c in text)
                {
                    hash = (hash ^ c) * 16777619;
                }
                return (int)hash;
            }
        }

        public static string ShortHash(string text)
        {
            using (var sha = SHA256.Create())
            {
                byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
                return ToHex(bytes, 4);
            }
        }

        public static string Sha256(string text)
        {
            using (var sha = SHA256.Create())
            {
                byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
                return ToHex(bytes, bytes.Length);
            }
        }

        public static string Sha256File(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
            {
                byte[] bytes = sha.ComputeHash(stream);
                return ToHex(bytes, bytes.Length);
            }
        }

        public static string ToHex(byte[] bytes, int count)
        {
            var sb = new StringBuilder(count * 2);
            for (int i = 0; i < count && i < bytes.Length; i++)
            {
                sb.Append(bytes[i].ToString("x2"));
            }
            return sb.ToString();
        }

        private static string ShortenSegment(string segment)
        {
            if (segment.Length <= ShortenedSegmentLength + 9)
            {
                return segment;
            }
            return segment.Substring(0, ShortenedSegmentLength) + "~" + ShortHash(segment);
        }

        private static string CombineRelative(string root, string relativeDir)
        {
            string normalized = relativeDir.Replace('/', Path.DirectorySeparatorChar);
            return Path.Combine(root, normalized);
        }

        private static bool IsFileNameSegment(string segment, string primaryPath)
        {
            string fileName = Path.GetFileName(primaryPath);
            string stem = Path.GetFileNameWithoutExtension(primaryPath);
            return string.Equals(segment, fileName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(segment, stem, StringComparison.OrdinalIgnoreCase);
        }

        private static string[] Slice(string[] source, int start)
        {
            if (start <= 0)
            {
                return source;
            }
            if (start >= source.Length)
            {
                return Array.Empty<string>();
            }

            var result = new string[source.Length - start];
            Array.Copy(source, start, result, 0, result.Length);
            return result;
        }
    }
}
