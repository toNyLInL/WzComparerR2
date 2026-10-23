using System;
using System.IO;

namespace WzComparerR2.Export
{
    /// <summary>
    /// 只读 PNG 头（签名 + IHDR 尺寸），不经过 GDI+。
    /// verify 命令用它做逐帧尺寸断言：30 万张图时开文件读 24 字节，比 GDI+ 重新解码快两个数量级。
    /// </summary>
    internal static class PngHeader
    {
        private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        public static bool TryReadSize(string path, out int width, out int height, out string error)
        {
            width = 0;
            height = 0;
            error = null;

            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    Span<byte> header = stackalloc byte[24];
                    int read = 0;
                    while (read < header.Length)
                    {
                        int count = stream.Read(header.Slice(read));
                        if (count == 0)
                        {
                            break;
                        }
                        read += count;
                    }

                    if (read < header.Length)
                    {
                        error = $"文件只有 {read} 字节";
                        return false;
                    }

                    for (int i = 0; i < Signature.Length; i++)
                    {
                        if (header[i] != Signature[i])
                        {
                            error = "PNG 签名不符";
                            return false;
                        }
                    }

                    if (header[12] != 'I' || header[13] != 'H' || header[14] != 'D' || header[15] != 'R')
                    {
                        error = "首个块不是 IHDR";
                        return false;
                    }

                    width = (header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19];
                    height = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];

                    if (width <= 0 || height <= 0)
                    {
                        error = $"IHDR 尺寸非法：{width}x{height}";
                        return false;
                    }

                    return true;
                }
            }
            catch (Exception ex)
            {
                error = NodePathUtility.Describe(ex);
                return false;
            }
        }
    }
}
