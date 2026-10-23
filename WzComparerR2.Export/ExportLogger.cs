using System;
using System.Collections.Generic;

namespace WzComparerR2.Export
{
    /// <summary>
    /// 控制台输出通道。导出器的错误一律走这里，不写日志文件、不弹窗。
    /// </summary>
    internal sealed class ExportLogger
    {
        private readonly List<string> warnings = new List<string>();

        public ExportLogger(bool quiet, bool verbose)
        {
            this.Quiet = quiet;
            this.Verbose = verbose;
        }

        public bool Quiet { get; }

        public bool Verbose { get; }

        /// <summary>本次导出期间累积的警告（会同时写进 manifest.warnings）。</summary>
        public IReadOnlyList<string> Warnings => this.warnings;

        public void Info(string message)
        {
            if (!this.Quiet)
            {
                Console.Out.WriteLine(message);
            }
        }

        /// <summary>只在 --verbose 下输出的明细。</summary>
        public void Detail(string message)
        {
            if (this.Verbose && !this.Quiet)
            {
                Console.Out.WriteLine(message);
            }
        }

        public void Warn(string message)
        {
            this.warnings.Add(message);
            Console.Error.WriteLine("警告：" + message);
        }

        public void Error(string message)
        {
            Console.Error.WriteLine("错误：" + message);
        }
    }
}
