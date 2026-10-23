using System;
using System.Collections.Generic;
using System.Globalization;
using WzComparerR2.WzLib;

namespace WzComparerR2.Export
{
    /// <summary>
    /// `list --depth &lt;n&gt;` 的节点树打印：只读、不落盘、不解压超过深度的 img。
    /// 用来在大 wz 上先看清目录结构，再决定导出哪一段。
    /// </summary>
    internal sealed class WzTreeLister
    {
        private readonly ExportLogger logger;
        private int nodeCount;
        private int imgCount;
        private int canvasCount;

        public WzTreeLister(ExportLogger logger)
        {
            this.logger = logger;
        }

        public void List(Wz_Node startNode, int maxDepth)
        {
            this.nodeCount = 0;
            this.imgCount = 0;
            this.canvasCount = 0;

            this.ListNode(startNode, 0, maxDepth, string.Empty);

            this.logger.Info(string.Format(CultureInfo.InvariantCulture,
                "统计：{0} 个节点，{1} 个 img，{2} 个 canvas（深度上限 {3}）",
                this.nodeCount, this.imgCount, this.canvasCount, maxDepth));
        }

        private void ListNode(Wz_Node node, int depth, int maxDepth, string prefix)
        {
            this.nodeCount++;
            this.logger.Info(prefix + this.Describe(node));

            if (depth >= maxDepth)
            {
                if (node.Nodes.Count > 0)
                {
                    this.logger.Info(prefix + "  …（还有 " + node.Nodes.Count + " 个子节点，已到 --depth 上限）");
                }
                return;
            }

            // img 是懒解压的：要往下看必须解压，放在深度判断之后，避免浅层检索把大 wz 全解出来
            Wz_Node content = node;
            Wz_Image image = node.GetValue<Wz_Image>();
            if (image != null)
            {
                if (!image.TryExtract(out Exception ex))
                {
                    this.logger.Warn($"{NodePathUtility.GetSymbolicPath(node)}：img 解压失败（{NodePathUtility.Describe(ex)}）");
                    return;
                }
                content = image.Node ?? node;
            }

            foreach (Wz_Node child in content.Nodes)
            {
                this.ListNode(child, depth + 1, maxDepth, prefix + "  ");
            }
        }

        private string Describe(Wz_Node node)
        {
            object value = node.Value;
            string name = node.Text ?? "?";

            if (node.GetValue<Wz_Image>() != null)
            {
                this.imgCount++;
                return name + "  [img]";
            }

            switch (value)
            {
                case null:
                    return name + "  [dir, " + node.Nodes.Count + " 子节点]";

                case Wz_Png png:
                    this.canvasCount++;
                    return $"{name}  [canvas {png.Width}x{png.Height} {png.Format} scale={png.ActualScale} pages={png.ActualPages}]";

                case Wz_Vector vector:
                    return $"{name}  [vector {vector.X}, {vector.Y}]";

                case Wz_Uol uol:
                    return $"{name}  [uol {uol.Uol}]";

                case Wz_Convex convex:
                    return $"{name}  [convex {convex.Points.Length} 点]";

                case Wz_Sound sound:
                    return $"{name}  [sound {sound.DataLength} 字节 {sound.Ms}ms]";

                case Wz_Video video:
                    return $"{name}  [video {video.Length} 字节]";

                case Wz_RawData raw:
                    return $"{name}  [rawdata {raw.Length} 字节]";

                case string text:
                    return $"{name}  [string {Shorten(text)}]";

                default:
                    return $"{name}  [{value.GetType().Name} {Shorten(value.ToString())}]";
            }
        }

        private static string Shorten(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return "\"\"";
            }

            string flat = text.Replace("\r", " ").Replace("\n", " ");
            return flat.Length <= 40 ? "\"" + flat + "\"" : "\"" + flat.Substring(0, 40) + "…\"";
        }
    }
}
