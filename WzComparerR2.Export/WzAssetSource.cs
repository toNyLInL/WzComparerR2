using System;
using System.Collections.Generic;
using System.IO;
using WzComparerR2.WzLib;

namespace WzComparerR2.Export
{
    /// <summary>
    /// 一个 canvas 帧在 wz 里的“来历”：UOL 跳转、_inlink/_outlink 链接、以及解析失败原因。
    /// 这些信息会写进 meta.json，方便反查。
    /// </summary>
    internal sealed class WzCanvasLink
    {
        public string UolPath { get; set; }

        public string LinkPath { get; set; }

        public bool IsInlink { get; set; }

        public string Error { get; set; }
    }

    /// <summary>
    /// 已加载的 wz 集合。第一个 <see cref="Load"/> 的路径是导出根，
    /// 其余文件只用于解析 _outlink/source 这类跨文件引用。
    /// </summary>
    internal sealed class WzAssetSource : IDisposable
    {
        private readonly Wz_Structure structure;
        private readonly List<Wz_Node> roots;
        private readonly List<string> loadedPaths;

        public WzAssetSource()
        {
            this.structure = new Wz_Structure();
            this.roots = new List<Wz_Node>();
            this.loadedPaths = new List<string>();
        }

        /// <summary>导出根节点（第一个 --wz 的根）。</summary>
        public Wz_Node Root => this.roots.Count > 0 ? this.roots[0] : null;

        /// <summary>导出根的物理路径。</summary>
        public string PrimaryPath => this.loadedPaths.Count > 0 ? this.loadedPaths[0] : null;

        public IReadOnlyList<string> LoadedPaths => this.loadedPaths;

        /// <summary>wz 版本号（取第一个非零的合并版本），读不到时为 0。</summary>
        public int WzVersion
        {
            get
            {
                foreach (Wz_File file in this.structure.wz_files)
                {
                    int version = file.GetMergedVersion();
                    if (version != 0)
                    {
                        return version;
                    }
                }
                return 0;
            }
        }

        /// <summary>已加载的 wz 文件名（不含路径）。</summary>
        public IReadOnlyList<string> GetFileNames()
        {
            var names = new List<string>();
            foreach (string path in this.loadedPaths)
            {
                names.Add(Path.GetFileName(path));
            }
            return names;
        }

        public void Load(string path, ExportLogger logger)
        {
            Wz_Node node = null;
            if (Directory.Exists(path))
            {
                // wz 文件夹：入口 wz + _000/_001 分卷
                this.structure.LoadWzFolder(path, ref node, false);
            }
            else if (string.Equals(Path.GetExtension(path), ".img", StringComparison.OrdinalIgnoreCase))
            {
                // 独立 img：文件根下面还有一层同名 img 节点，直接拿内容根，避免产出多一层目录
                this.structure.LoadImg(path);
                node = GetImageContentRoot(this.structure.WzNode);
            }
            else
            {
                this.structure.Load(path);
                node = this.structure.WzNode;
            }

            if (node == null)
            {
                throw new InvalidOperationException($"无法打开 wz：{path}");
            }

            this.roots.Add(node);
            this.loadedPaths.Add(path);
            logger.Info($"已加载 {Path.GetFileName(path)}（wz 版本 {this.WzVersion}）");
        }

        /// <summary>
        /// 在导出根上按路径找节点。<paramref name="nodePath"/> 为空或 "." 时返回根节点本身；
        /// 允许路径首段写 wz 文件名（会被忽略）。
        /// </summary>
        public Wz_Node FindPrimaryNode(string nodePath)
        {
            Wz_Node root = this.Root;
            if (root == null)
            {
                return null;
            }

            string[] segments = NodePathUtility.Split(nodePath);
            if (segments.Length == 0 || (segments.Length == 1 && segments[0] == "."))
            {
                return root;
            }

            if (this.IsPrimaryFileName(segments[0]))
            {
                if (segments.Length == 1)
                {
                    return root;
                }
                segments = Slice(segments, 1);
            }

            return root.FindNodeByPath(true, segments);
        }

        /// <summary>按 "UI.wz/UIWindow.img/Main/0" 这样的全局路径找节点，用于 _outlink/source。</summary>
        public Wz_Node FindGlobalNode(string fullPath)
        {
            string[] segments = NodePathUtility.Split(fullPath);
            if (segments.Length == 0)
            {
                return null;
            }

            string fileSegment = segments[0];
            string[] rest = Slice(segments, 1);

            // 先按已加载的根找（独立 .img 的根就是内容节点，路径不额外多一层）
            foreach (Wz_Node root in this.roots)
            {
                if (!this.MatchesFileSegment(root, fileSegment))
                {
                    continue;
                }

                Wz_Node found = rest.Length == 0 ? root : root.FindNodeByPath(true, rest);
                if (found != null)
                {
                    return found;
                }
            }

            // 再退到 wz_files 的文件节点（多分卷合并后，子文件的节点挂在 entry 文件下）
            foreach (Wz_File wzFile in this.structure.wz_files)
            {
                Wz_Node root = wzFile.Node;
                if (root == null || !this.MatchesFileSegment(root, fileSegment))
                {
                    continue;
                }

                Wz_Node found = rest.Length == 0 ? root : root.FindNodeByPath(true, rest);
                if (found != null)
                {
                    return found;
                }
            }

            // 兜底：有些 outlink 省略了 wz 文件名，直接在任一文件根上按原路径找。
            foreach (Wz_File wzFile in this.structure.wz_files)
            {
                Wz_Node root = wzFile.Node;
                if (root == null)
                {
                    continue;
                }

                Wz_Node found = root.FindNodeByPath(true, segments);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        private static Wz_Node GetImageContentRoot(Wz_Node fileRoot)
        {
            if (fileRoot == null)
            {
                return null;
            }

            Wz_Node only = null;
            int count = 0;
            foreach (Wz_Node child in fileRoot.Nodes)
            {
                count++;
                only = child;
                if (count > 1)
                {
                    return fileRoot;
                }
            }

            if (count == 1 && only.GetValue<Wz_Image>() != null)
            {
                return only;
            }
            return fileRoot;
        }

        /// <summary>
        /// 把帧节点解析到真正持有 <see cref="Wz_Png"/> 的节点：
        /// 先走 Wz_Uol 链，再跟 source/_inlink/_outlink。解析失败时退回原节点并记录原因。
        /// </summary>
        public Wz_Node ResolveCanvasNode(Wz_Node node, WzCanvasLink link)
        {
            if (node == null)
            {
                return null;
            }

            Wz_Node current = node;

            // 1. UOL 链（HandleUol 自身不递归，这里限深防止环）
            for (int i = 0; i < 32 && current != null && current.Value is Wz_Uol uol; i++)
            {
                if (link != null)
                {
                    link.UolPath = uol.Uol;
                }

                Wz_Node next = uol.HandleUol(current);
                if (next == null)
                {
                    if (link != null)
                    {
                        link.Error = $"UOL 无法解析：{uol.Uol}";
                    }
                    return current;
                }
                current = next;
            }

            // 2. 链接属性
            string linkPath = NodePathUtility.GetLinkPath(current);
            if (linkPath == null)
            {
                return current;
            }

            if (link != null)
            {
                link.LinkPath = linkPath;
                link.IsInlink = NodePathUtility.IsInlink(current);
            }

            Wz_Node target = this.ResolveLinkedNode(current, linkPath);
            if (target == null)
            {
                if (link != null)
                {
                    link.Error = $"链接目标不存在：{linkPath}";
                }
                return current;
            }

            Wz_Node resolved = target.ResolveUol();
            return resolved ?? target;
        }

        private Wz_Node ResolveLinkedNode(Wz_Node node, string linkPath)
        {
            if (NodePathUtility.IsInlink(node))
            {
                Wz_Image image = NodePathUtility.GetOwnerImage(node);
                Wz_Node contents = image?.Node;
                if (contents == null)
                {
                    return null;
                }
                return contents.FindNodeByPath(true, NodePathUtility.Split(linkPath));
            }

            return this.FindGlobalNode(linkPath);
        }

        private bool IsPrimaryFileName(string segment)
        {
            string primary = this.PrimaryPath;
            if (primary == null)
            {
                return false;
            }

            string fileName = Path.GetFileName(primary);
            string stem = Path.GetFileNameWithoutExtension(primary);

            return string.Equals(segment, fileName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(segment, stem, StringComparison.OrdinalIgnoreCase)
                || string.Equals(segment, Path.GetFileName(this.Root?.Text), StringComparison.OrdinalIgnoreCase);
        }

        private bool MatchesFileSegment(Wz_Node root, string segment)
        {
            string text = root.Text;
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            return string.Equals(segment, text, StringComparison.OrdinalIgnoreCase)
                || string.Equals(segment, Path.GetFileNameWithoutExtension(text), StringComparison.OrdinalIgnoreCase);
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

        public void Dispose()
        {
            this.structure.Clear();
        }
    }
}
