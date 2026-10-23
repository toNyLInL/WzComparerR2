using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace WzComparerR2.Export
{
    /// <summary>
    /// PNG 落盘。wz 里 canvas 的原生格式五花八门（ARGB4444/1555、RGB565、DXT、BC7、R16…），
    /// 首版统一烘成 RGBA32 PNG，避免引擎侧再猜通道序（见计划 §5.5）。
    /// </summary>
    internal static class PngSaver
    {
        /// <summary>把位图统一成 RGBA32 后编码为 PNG 字节；编码参数固定，保证同一输入字节一致。</summary>
        public static byte[] Encode(Bitmap bitmap)
        {
            if (bitmap == null)
            {
                throw new ArgumentNullException(nameof(bitmap));
            }

            Bitmap normalized = null;
            try
            {
                Bitmap source = bitmap;
                if (bitmap.PixelFormat != PixelFormat.Format32bppArgb)
                {
                    normalized = ToRgba32(bitmap);
                    source = normalized;
                }

                using (var stream = new MemoryStream(bitmap.Width * bitmap.Height * 4))
                {
                    source.Save(stream, ImageFormat.Png);
                    return stream.ToArray();
                }
            }
            finally
            {
                normalized?.Dispose();
            }
        }

        /// <summary>读回 PNG 尺寸，用于 --verify（同时验证产物能被 GDI+ 重新打开）。</summary>
        public static void ReadSize(string path, out int width, out int height)
        {
            using (var image = Image.FromFile(path))
            {
                width = image.Width;
                height = image.Height;
            }
        }

        private static Bitmap ToRgba32(Bitmap source)
        {
            var target = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
            using (Graphics graphics = Graphics.FromImage(target))
            {
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                graphics.PixelOffsetMode = PixelOffsetMode.Half;
                graphics.DrawImage(source, new Rectangle(0, 0, source.Width, source.Height), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel);
            }
            return target;
        }
    }
}
