using System;
using System.Collections.Generic;
using System.IO;

namespace WzComparerR2.Export
{
    /// <summary>
    /// 导出器的子命令。
    /// </summary>
    internal enum ExportCommand
    {
        /// <summary>把指定节点下的 canvas 导出为 PNG + meta.json + manifest.json。</summary>
        Ui = 0,

        /// <summary>只统计并打印将要导出的内容，不写任何文件。</summary>
        List = 1,
    }

    /// <summary>
    /// 命令行参数错误。抛出后由 <see cref="Program"/> 打印用法并以退出码 1 结束。
    /// </summary>
    internal sealed class UsageException : Exception
    {
        public UsageException(string message)
            : base(message)
        {
        }
    }

    /// <summary>
    /// 命令行解析结果。字段语义见 <see cref="GetUsageText"/>。
    /// </summary>
    internal sealed class ExportOptions
    {
        private ExportOptions()
        {
            this.NodePath = string.Empty;
            this.Kind = "ui";
        }

        public ExportCommand Command { get; private set; }

        /// <summary>wz 文件 / wz 文件夹 / 独立 .img 文件，第一个是导出根，其余用于解析跨文件链接。</summary>
        public List<string> WzPaths { get; } = new List<string>();

        /// <summary>起始节点路径（'/' 或 '\' 分隔），空表示整个 wz。</summary>
        public string NodePath { get; private set; }

        /// <summary>输出目录，list 命令可为空。</summary>
        public string OutputDirectory { get; private set; }

        /// <summary>产物分类名，落到 sprite/&lt;kind&gt;/ 与 manifest.kind。</summary>
        public string Kind { get; private set; }

        public bool Force { get; private set; }

        public bool Verify { get; private set; }

        public bool Quiet { get; private set; }

        public bool Verbose { get; private set; }

        public bool ShowHelp { get; private set; }

        /// <summary>list 命令的树深度上限；为空时按导出口径统计分组。</summary>
        public int? ListDepth { get; private set; }

        public static ExportOptions Parse(string[] args)
        {
            var options = new ExportOptions();
            bool commandFixed = false;

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (string.IsNullOrEmpty(arg))
                {
                    continue;
                }

                if (!arg.StartsWith("-", StringComparison.Ordinal))
                {
                    if (commandFixed)
                    {
                        throw new UsageException($"无法识别的参数：{arg}");
                    }
                    options.Command = ParseCommand(arg);
                    commandFixed = true;
                    continue;
                }

                switch (arg)
                {
                    case "-h":
                    case "--help":
                    case "-?":
                        options.ShowHelp = true;
                        return options;

                    case "--wz":
                        options.WzPaths.Add(RequireValue(args, ref i, arg));
                        break;

                    case "--node":
                        options.NodePath = NormalizeNodePath(RequireValue(args, ref i, arg));
                        break;

                    case "--out":
                        options.OutputDirectory = RequireValue(args, ref i, arg);
                        break;

                    case "--kind":
                        options.Kind = RequireValue(args, ref i, arg);
                        break;

                    case "--depth":
                        options.ListDepth = ParseDepth(RequireValue(args, ref i, arg));
                        break;

                    case "--force":
                        options.Force = true;
                        break;

                    case "--verify":
                        options.Verify = true;
                        break;

                    case "--quiet":
                        options.Quiet = true;
                        break;

                    case "--verbose":
                        options.Verbose = true;
                        break;

                    default:
                        throw new UsageException($"无法识别的参数：{arg}");
                }
            }

            options.Validate();
            return options;
        }

        /// <summary>--verify 用：复制一份选项，只换输出目录。</summary>
        public ExportOptions CloneWithOutputDirectory(string directory)
        {
            var clone = (ExportOptions)this.MemberwiseClone();
            clone.OutputDirectory = directory;
            return clone;
        }

        public static string GetUsageText()        {
            return string.Join(Environment.NewLine, new[]
            {
                "wzexport — WZ 资源离线导出器（引擎无关中间格式）",
                "",
                "用法：",
                "  wzexport ui   --wz <路径> [--wz <路径>...] [--node <节点路径>] --out <目录> [选项]",
                "  wzexport list --wz <路径> [--wz <路径>...] [--node <节点路径>] [选项]",
                "",
                "命令：",
                "  ui     把节点下的 canvas 导出为 <out>/sprite/<kind>/<相对路径>/{<帧>.png, meta.json}，",
                "         并写 <out>/manifest.json。省略命令时默认执行 ui。",
                "  list   只打印将要导出的分组、帧数与尺寸，不写文件；用于导出前核对。",
                "         加 --depth <n> 则改为打印节点树（只看结构，不导出）。",
                "",
                "选项：",
                "  --wz <路径>   wz 文件、wz 文件夹（含 _000 分卷）或独立 .img 文件。可重复：",
                "                第一个是导出根，其余只用于解析 _outlink/source 跨文件引用。",
                "  --node <路径> 起始节点，'/' 或 '\\' 分隔，例如 \"UIWindow.img/Main\"；缺省为整个 wz。",
                "  --out <目录>  输出目录。已存在且非空时必须加 --force。",
                "  --kind <名字> 产物分类名，默认 ui（决定 sprite/ui/ 与 manifest.kind）。",
                "  --depth <n>   list 专用：只展开到第 n 层（0=只看起点），逐节点打印类型/尺寸。",
                "  --force       允许写入非空输出目录。",
                "  --verify      导出后再跑一遍断言：PNG 可重新打开且尺寸与 meta 一致、帧数一致、",
                "                z/delay 在合法区间、二次导出逐字节一致。",
                "  --quiet       只输出错误。",
                "  --verbose     输出每个分组的明细与异常堆栈。",
                "  -h, --help    显示本帮助。",
                "",
                "示例：",
                "  wzexport ui --wz \"D:\\Maple\\UI.wz\" --node \"UIWindow.img\" --out D:\\export\\ui",
                "  wzexport list --wz \"D:\\Maple\\UI.wz\" --node \"UIWindow.img/Main\"",
                "",
                "退出码：0 成功；1 参数错误；2 导出失败；3 --verify 断言失败。",
            });
        }

        private static ExportCommand ParseCommand(string arg)
        {
            switch (arg.ToLowerInvariant())
            {
                case "ui":
                    return ExportCommand.Ui;
                case "list":
                    return ExportCommand.List;
                default:
                    throw new UsageException($"无法识别的命令：{arg}（可用：ui、list）");
            }
        }

        private static int ParseDepth(string value)
        {
            if (!int.TryParse(value, out int depth) || depth < 0 || depth > 64)
            {
                throw new UsageException($"--depth 只接受 0~64 的整数：{value}");
            }
            return depth;
        }

        private static string RequireValue(string[] args, ref int index, string option)
        {
            if (index + 1 >= args.Length)
            {
                throw new UsageException($"选项 {option} 缺少参数值。");
            }
            string value = args[++index];
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new UsageException($"选项 {option} 的参数值为空。");
            }
            return value;
        }

        private static string NormalizeNodePath(string path)
        {
            string[] segments = NodePathUtility.Split(path);
            return string.Join("/", segments);
        }

        private void Validate()
        {
            if (this.ShowHelp)
            {
                return;
            }

            if (this.WzPaths.Count == 0)
            {
                throw new UsageException("必须用 --wz 至少指定一个 wz 文件、wz 文件夹或 .img 文件。");
            }

            for (int i = 0; i < this.WzPaths.Count; i++)
            {
                string path = this.WzPaths[i];
                if (!File.Exists(path) && !Directory.Exists(path))
                {
                    throw new UsageException($"找不到 wz 路径：{path}");
                }
                this.WzPaths[i] = Path.GetFullPath(path);
            }

            if (string.IsNullOrWhiteSpace(this.Kind) || this.Kind.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new UsageException($"非法的 --kind 值：{this.Kind}");
            }

            if (this.ListDepth != null && this.Command != ExportCommand.List)
            {
                throw new UsageException("--depth 只能和 list 命令一起用。");
            }

            if (this.Command == ExportCommand.Ui)
            {
                if (string.IsNullOrWhiteSpace(this.OutputDirectory))
                {
                    throw new UsageException("ui 命令必须用 --out 指定输出目录。");
                }
                this.OutputDirectory = Path.GetFullPath(this.OutputDirectory);
            }
            else if (!string.IsNullOrWhiteSpace(this.OutputDirectory))
            {
                this.OutputDirectory = Path.GetFullPath(this.OutputDirectory);
            }
        }
    }
}
