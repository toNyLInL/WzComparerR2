using System;
using System.Collections.Generic;
using System.Text;
using WzComparerR2.WzLib;

namespace WzComparerR2.Export
{
    /// <summary>
    /// wz 节点路径与链接属性的读写工具。纯函数，不持有状态。
    /// </summary>
    internal static class NodePathUtility
    {
        private static readonly char[] PathSeparators = { '/', '\\' };

        /// <summary>按 '/' 或 '\' 切分路径，丢弃空段。</summary>
        public static string[] Split(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return Array.Empty<string>();
            }

            var parts = new List<string>();
            foreach (string raw in path.Split(PathSeparators))
            {
                string segment = raw.Trim();
                if (segment.Length > 0)
                {
                    parts.Add(segment);
                }
            }
            return parts.ToArray();
        }

        /// <summary>把节点反推成 "UI.wz/UIWindow.img/Main" 这样的符号路径（'/' 分隔）。</summary>
        public static string GetSymbolicPath(Wz_Node node)
        {
            if (node == null)
            {
                return string.Empty;
            }

            var parts = new List<string>();
            string deeperText = null;
            int guard = 0;

            for (Wz_Node current = node; current != null && guard < 256; guard++)
            {
                if (string.IsNullOrEmpty(current.Text))
                {
                    current = GetParentOrOwner(current);
                    continue;
                }

                // img 的树节点与文件根节点会和内容节点同名：路径里只保留一层
                // （独立 .img 文件、wz 内的 img 都走这条规则）。
                bool isContainerBoundary = current.Value is Wz_File || current.GetValue<Wz_Image>() != null;
                if (isContainerBoundary && string.Equals(current.Text, deeperText, StringComparison.OrdinalIgnoreCase))
                {
                    current = GetParentOrOwner(current);
                    continue;
                }

                parts.Add(current.Text);
                deeperText = current.Text;
                current = GetParentOrOwner(current);
            }

            parts.Reverse();
            return string.Join("/", parts);
        }

        /// <summary>
        /// 上一级节点。img 的内容根（Wz_Image.Node）是游离节点，ParentNode 为空，
        /// 要靠 Wz_Image.OwnerNode 跳回树里，否则路径会在 img 边界断掉。
        /// </summary>
        public static Wz_Node GetParentOrOwner(Wz_Node node)
        {
            if (node == null)
            {
                return null;
            }
            if (node.ParentNode != null)
            {
                return node.ParentNode;
            }

            Wz_Image image = node.GetValue<Wz_Image>();
            Wz_Node owner = image?.OwnerNode;
            return owner != null && !ReferenceEquals(owner, node) ? owner : null;
        }

        /// <summary>
        /// 取节点的链接属性值（优先级与 Common/Wz_NodeExtension2.GetLinkedSourceNode 一致：
        /// source &gt; _inlink &gt; _outlink）。没有链接时返回 null。
        /// </summary>
        public static string GetLinkPath(Wz_Node node)
        {
            if (node == null)
            {
                return null;
            }

            string path = node.Nodes["source"].GetValueEx<string>(null);
            if (!string.IsNullOrEmpty(path))
            {
                return path;
            }

            path = node.Nodes["_inlink"].GetValueEx<string>(null);
            if (!string.IsNullOrEmpty(path))
            {
                return path;
            }

            path = node.Nodes["_outlink"].GetValueEx<string>(null);
            if (!string.IsNullOrEmpty(path))
            {
                return path;
            }

            return null;
        }

        /// <summary>判断是否是 _inlink（同 img 相对路径），否则视为跨文件链接。</summary>
        public static bool IsInlink(Wz_Node node)
        {
            if (node == null)
            {
                return false;
            }
            if (!string.IsNullOrEmpty(node.Nodes["source"].GetValueEx<string>(null)))
            {
                return false;
            }
            return !string.IsNullOrEmpty(node.Nodes["_inlink"].GetValueEx<string>(null));
        }

        /// <summary>
        /// 把目录名用的节点名规范化：去掉 .img 后缀（大小写不敏感）。
        /// </summary>
        public static string StripImgSuffix(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text;
            }
            if (text.EndsWith(".img", StringComparison.OrdinalIgnoreCase))
            {
                return text.Substring(0, text.Length - 4);
            }
            return text;
        }

        /// <summary>取节点的祖先里最近的 <see cref="Wz_Image"/>（img 内容节点自身也能拿到）。</summary>
        public static Wz_Image GetOwnerImage(Wz_Node node)
        {
            for (Wz_Node current = node; current != null; current = current.ParentNode)
            {
                Wz_Image image = current.GetValue<Wz_Image>();
                if (image != null)
                {
                    return image;
                }
            }
            return null;
        }

        /// <summary>内部异常信息拼进单行提示用。</summary>
        public static string Describe(Exception ex)
        {
            var sb = new StringBuilder();
            for (Exception current = ex; current != null; current = current.InnerException)
            {
                if (sb.Length > 0)
                {
                    sb.Append(" <- ");
                }
                sb.Append(current.Message);
            }
            return sb.ToString();
        }
    }
}
