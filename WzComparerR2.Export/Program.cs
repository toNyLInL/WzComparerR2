using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using WzComparerR2.WzLib;

namespace WzComparerR2.Export
{
    /// <summary>
    /// wzexport 入口。离线导出器：把 wz 里的 canvas 烘成 PNG + JSON，
    /// 运行时不依赖 wz、不依赖 WinForms/MonoGame（详见 docs/plan/wz-to-unity-export.md）。
    /// </summary>
    internal static class Program
    {
        private const int ExitOk = 0;
        private const int ExitUsage = 1;
        private const int ExitError = 2;
        private const int ExitVerifyFailed = 3;

        /// <summary>工具版本，写进 manifest.toolVersion。</summary>
        internal static string ToolVersion { get; private set; }

        private static int Main(string[] args)
        {
            // 老客户端的中文/韩文 img 字符串要按码页解码，与主程序 Program.Main 保持一致。
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            ToolVersion = GetToolVersion();

            ExportOptions options;
            try
            {
                options = ExportOptions.Parse(args);
            }
            catch (UsageException ex)
            {
                Console.Error.WriteLine(ex.Message);
                Console.Error.WriteLine();
                Console.Error.WriteLine(ExportOptions.GetUsageText());
                return ExitUsage;
            }

            if (options.ShowHelp)
            {
                Console.Out.WriteLine(ExportOptions.GetUsageText());
                return ExitOk;
            }

            try
            {
                return Run(options);
            }
            catch (UsageException ex)
            {
                Console.Error.WriteLine(ex.Message);
                return ExitUsage;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("导出失败：" + NodePathUtility.Describe(ex));
                if (options.Verbose)
                {
                    Console.Error.WriteLine(ex.ToString());
                }
                return ExitError;
            }
        }

        private static int Run(ExportOptions options)
        {
            var logger = new ExportLogger(options.Quiet, options.Verbose);

            using (var source = new WzAssetSource())
            {
                foreach (string path in options.WzPaths)
                {
                    source.Load(path, logger);
                }

                List<ExportJob> jobs = ResolveJobs(source, options, logger);
                if (jobs == null)
                {
                    return ExitUsage;
                }

                string rootPath = jobs.Count == 1
                    ? jobs[0].SymbolicPath
                    : NodePathUtility.GetSymbolicPath(source.Root);

                foreach (ExportJob job in jobs)
                {
                    logger.Info($"导出目标：{job.SymbolicPath}  ->  {options.Kind}/{job.RelativeRoot}");
                }

                if (options.Command == ExportCommand.List && options.ListDepth != null)
                {
                    var treeLister = new WzTreeLister(logger);
                    foreach (ExportJob job in jobs)
                    {
                        logger.Info($"== {job.SymbolicPath} ==");
                        treeLister.List(job.Node, options.ListDepth.Value);
                    }
                    return ExitOk;
                }

                if (options.Command == ExportCommand.List)
                {
                    var listExporter = new UiImageExporter(source, options, logger);
                    foreach (ExportJob job in jobs)
                    {
                        listExporter.ExportTarget(job.Node, job.RelativeRoot, dryRun: true);
                    }

                    ExportManifest listManifest = listExporter.CreateManifest(rootPath);
                    logger.Info($"统计：{listManifest.Groups} 个分组，{listManifest.Frames} 帧（list 模式不写文件）");
                    return ExitOk;
                }

                PrepareOutputDirectory(options);

                var exporter = new UiImageExporter(source, options, logger);
                foreach (ExportJob job in jobs)
                {
                    exporter.ExportTarget(job.Node, job.RelativeRoot, dryRun: false);
                }

                ExportManifest manifest = exporter.CreateManifest(rootPath);
                foreach (string warning in logger.Warnings)
                {
                    manifest.Warnings.Add(warning);
                }

                WriteManifest(options.OutputDirectory, manifest);
                logger.Info($"导出完成：{manifest.Groups} 个分组，{manifest.Frames} 帧  ->  {options.OutputDirectory}");

                if (!options.Verify)
                {
                    return ExitOk;
                }

                return Verify(options, source, jobs, rootPath, manifest, exporter, logger);
            }
        }

        /// <summary>
        /// 把 --node / --list 解析成「节点 + 输出相对目录」的工作列表。
        /// 单目标找不到节点算参数错误；批量清单里找不到的行只警告并跳过。
        /// </summary>
        private static List<ExportJob> ResolveJobs(WzAssetSource source, ExportOptions options, ExportLogger logger)
        {
            var jobs = new List<ExportJob>();

            if (options.ListFiles.Count == 0)
            {
                Wz_Node node = source.FindPrimaryNode(options.NodePath);
                if (node == null)
                {
                    string shown = string.IsNullOrEmpty(options.NodePath) ? "(wz 根)" : options.NodePath;
                    logger.Error($"找不到节点：{shown}");
                    return null;
                }

                jobs.Add(new ExportJob
                {
                    Node = node,
                    RelativeRoot = ExportNaming.GetRelativeRoot(options.NodePath, source.PrimaryPath),
                    SymbolicPath = NodePathUtility.GetSymbolicPath(node),
                });
                return jobs;
            }

            foreach (string listFile in options.ListFiles)
            {
                List<ExportTargetEntry> entries = ExportTargetList.Read(listFile, logger);
                logger.Info($"{Path.GetFileName(listFile)}：{entries.Count} 个目标");

                foreach (ExportTargetEntry entry in entries)
                {
                    Wz_Node node = source.FindPrimaryNode(entry.NodePath);
                    if (node == null)
                    {
                        logger.Warn($"{Path.GetFileName(listFile)} 第 {entry.LineNumber} 行：找不到节点 {entry.NodePath}，已跳过");
                        continue;
                    }

                    string relativeRoot = string.IsNullOrEmpty(entry.SubDirectory)
                        ? ExportNaming.GetRelativeRoot(entry.NodePath, source.PrimaryPath)
                        : ExportNaming.GetRelativeRoot(entry.SubDirectory, null);

                    jobs.Add(new ExportJob
                    {
                        Node = node,
                        RelativeRoot = relativeRoot,
                        SymbolicPath = NodePathUtility.GetSymbolicPath(node),
                    });
                }
            }

            if (jobs.Count == 0)
            {
                logger.Error("批量清单里没有可导出的节点。");
                return null;
            }
            return jobs;
        }

        private static int Verify(ExportOptions options, WzAssetSource source, IReadOnlyList<ExportJob> jobs,
            string rootPath, ExportManifest manifest, UiImageExporter exporter, ExportLogger logger)
        {
            logger.Info("开始校验…");

            var problems = ExportVerifier.VerifyOutput(manifest, exporter.Groups, exporter.ContentRoot);

            // 字节稳定性：同一输入再导出一份到临时目录，逐文件比对。
            string verifyDir = options.OutputDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + ".verify";
            if (Directory.Exists(verifyDir))
            {
                Directory.Delete(verifyDir, true);
            }

            try
            {
                Directory.CreateDirectory(verifyDir);
                var quietLogger = new ExportLogger(quiet: true, verbose: false);
                var verifyExporter = new UiImageExporter(source, options.CloneWithOutputDirectory(verifyDir), quietLogger);
                foreach (ExportJob job in jobs)
                {
                    verifyExporter.ExportTarget(job.Node, job.RelativeRoot, dryRun: false);
                }

                ExportManifest second = verifyExporter.CreateManifest(rootPath);
                problems.AddRange(ExportVerifier.VerifyOutput(second, verifyExporter.Groups, verifyExporter.ContentRoot));
                problems.AddRange(ExportVerifier.CompareTrees(options.OutputDirectory, verifyDir));
            }
            finally
            {
                try
                {
                    if (Directory.Exists(verifyDir))
                    {
                        Directory.Delete(verifyDir, true);
                    }
                }
                catch (Exception ex)
                {
                    logger.Warn($"临时校验目录删除失败：{verifyDir}（{NodePathUtility.Describe(ex)}）");
                }
            }

            if (problems.Count == 0)
            {
                logger.Info($"校验通过：{manifest.Groups} 个分组 / {manifest.Frames} 帧，二次导出逐字节一致。");
                return ExitOk;
            }

            logger.Error($"校验失败，共 {problems.Count} 项：");
            foreach (string problem in problems)
            {
                Console.Error.WriteLine("  - " + problem);
            }
            return ExitVerifyFailed;
        }

        private static void PrepareOutputDirectory(ExportOptions options)
        {
            string outputDirectory = options.OutputDirectory;

            if (!Directory.Exists(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
                return;
            }

            bool hasContent = Directory.EnumerateFileSystemEntries(outputDirectory).Any();
            if (hasContent && !options.Force)
            {
                throw new UsageException($"输出目录非空：{outputDirectory}（确认要写入请加 --force）");
            }
        }

        private static void WriteManifest(string outputDirectory, ExportManifest manifest)
        {
            string path = Path.Combine(outputDirectory, "manifest.json");
            File.WriteAllText(path, JsonExportWriter.WriteManifest(manifest), new UTF8Encoding(false));
        }

        private static string GetToolVersion()
        {
            Assembly assembly = typeof(Program).Assembly;
            string version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (string.IsNullOrEmpty(version))
            {
                version = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
            }
            if (string.IsNullOrEmpty(version))
            {
                version = assembly.GetName().Version?.ToString();
            }
            return version ?? "0.0.0";
        }

        /// <summary>一个导出目标：节点 + 输出相对目录 + 反查用符号路径。</summary>
        private sealed class ExportJob
        {
            public Wz_Node Node { get; set; }

            public string RelativeRoot { get; set; }

            public string SymbolicPath { get; set; }
        }
    }
}
