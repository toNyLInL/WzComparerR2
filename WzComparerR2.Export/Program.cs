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
                if (options.Command == ExportCommand.Verify)
                {
                    var verifier = new ExistingOutputVerifier(
                        options.OutputDirectory,
                        new ExportLogger(options.Quiet, options.Verbose),
                        options.Deep);
                    return verifier.Run() == 0 ? ExitOk : ExitVerifyFailed;
                }

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

                    var listAudio = new AudioExporter(source, options, logger);
                    foreach (ExportJob job in jobs)
                    {
                        listAudio.ExportTarget(job.Node, job.RelativeRoot, dryRun: true);
                    }

                    listAudio.FlushAbsoluteUolCandidates();
                    ExportManifest listAudioManifest = listAudio.CreateManifest(rootPath);
                    logger.Info($"音频统计：{listAudioManifest.AudioGroups} 个分组，{listAudioManifest.AudioFiles} 个音频，" +
                        $"{FormatBytes(listAudioManifest.AudioBytes)}（list 模式不写文件）");
                    return ExitOk;
                }

                PrepareOutputDirectory(options);

                if (options.Command == ExportCommand.Audio)
                {
                    var audioExporter = new AudioExporter(source, options, logger);
                    foreach (ExportJob job in jobs)
                    {
                        audioExporter.ExportTarget(job.Node, job.RelativeRoot, dryRun: false);
                    }

                    audioExporter.FlushAbsoluteUolCandidates();
                    ExportManifest audioManifest = audioExporter.CreateManifest(rootPath);
                    audioManifest.AudioOnly = true;
                    AddWarnings(logger, audioManifest);

                    string manifestPath = audioExporter.GetManifestPath(options.OutputDirectory);
                    WriteManifestFile(manifestPath, audioManifest);
                    logger.Info($"导出完成：{audioManifest.AudioGroups} 个音频分组，{audioManifest.AudioFiles} 个音频，" +
                        $"{FormatBytes(audioManifest.AudioBytes)}  ->  {options.OutputDirectory}（清单 {Path.GetFileName(manifestPath)}）");

                    if (!options.Verify)
                    {
                        return ExitOk;
                    }

                    return Verify(options, source, jobs, rootPath, audioManifest, null, audioExporter, logger);
                }

                var exporter = new UiImageExporter(source, options, logger);
                foreach (ExportJob job in jobs)
                {
                    exporter.ExportTarget(job.Node, job.RelativeRoot, dryRun: false);
                }

                ExportManifest manifest = exporter.CreateManifest(rootPath);
                AddWarnings(logger, manifest);

                WriteManifest(options.OutputDirectory, manifest);
                logger.Info($"导出完成：{manifest.Groups} 个分组，{manifest.Frames} 帧  ->  {options.OutputDirectory}");

                if (!options.Verify)
                {
                    return ExitOk;
                }

                return Verify(options, source, jobs, rootPath, manifest, exporter, null, logger);
            }
        }

        private static void AddWarnings(ExportLogger logger, ExportManifest manifest)
        {
            foreach (string warning in logger.Warnings)
            {
                manifest.Warnings.Add(warning);
            }
        }

        /// <summary>--verify 做二次导出比对时要跳过的清单文件（含时间戳 / 由另一种分类负责）。</summary>
        private static readonly string[] IgnoredManifestNames = { "manifest.json", "audio/manifest.json" };

        private static int CountAudioFiles(IReadOnlyList<AudioGroup> groups)
        {
            int count = 0;
            foreach (AudioGroup group in groups)
            {
                count += group.Entries.Count;
            }
            return count;
        }

        private static string FormatBytes(int bytes)
        {
            if (bytes >= 1024 * 1024)
            {
                return (bytes / (1024.0 * 1024.0)).ToString("0.0") + "MB";
            }
            if (bytes >= 1024)
            {
                return (bytes / 1024.0).ToString("0.0") + "KB";
            }
            return bytes + "B";
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
            string rootPath, ExportManifest manifest, UiImageExporter exporter, AudioExporter audioExporter, ExportLogger logger)
        {
            logger.Info("开始校验…");

            var problems = new List<string>();
            if (exporter != null)
            {
                problems.AddRange(ExportVerifier.VerifyOutput(manifest, exporter.Groups, exporter.ContentRoot));
            }
            if (audioExporter != null)
            {
                problems.AddRange(ExportVerifier.VerifyAudioOutput(manifest, audioExporter.Groups, audioExporter.ContentRoot));
            }

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
                ExportOptions cloneOptions = options.CloneWithOutputDirectory(verifyDir);
                var sharedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                UiImageExporter verifyExporter = null;
                if (exporter != null)
                {
                    verifyExporter = new UiImageExporter(source, cloneOptions, quietLogger, sharedDirectories);
                }
                AudioExporter verifyAudio = null;
                if (audioExporter != null)
                {
                    verifyAudio = new AudioExporter(source, cloneOptions, quietLogger, sharedDirectories);
                }

                foreach (ExportJob job in jobs)
                {
                    verifyExporter?.ExportTarget(job.Node, job.RelativeRoot, dryRun: false);
                    verifyAudio?.ExportTarget(job.Node, job.RelativeRoot, dryRun: false);
                }

                if (verifyExporter != null)
                {
                    ExportManifest second = verifyExporter.CreateManifest(rootPath);
                    problems.AddRange(ExportVerifier.VerifyOutput(second, verifyExporter.Groups, verifyExporter.ContentRoot));
                }
                if (verifyAudio != null)
                {
                    ExportManifest secondAudio = verifyAudio.CreateManifest(rootPath);
                    secondAudio.AudioOnly = true;
                    problems.AddRange(ExportVerifier.VerifyAudioOutput(secondAudio, verifyAudio.Groups, verifyAudio.ContentRoot));
                }

                problems.AddRange(ExportVerifier.CompareTrees(options.OutputDirectory, verifyDir, IgnoredManifestNames));
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
                var parts = new List<string>();
                if (exporter != null)
                {
                    parts.Add($"{manifest.Groups} 个分组 / {manifest.Frames} 帧");
                }
                if (audioExporter != null)
                {
                    parts.Add($"音频 {audioExporter.Groups.Count} 组 / {CountAudioFiles(audioExporter.Groups)} 个");
                }

                logger.Info($"校验通过：{string.Join("；", parts)}，二次导出逐字节一致。");
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

        /// <summary>写 &lt;outputDirectory&gt;/manifest.json。</summary>
        private static void WriteManifest(string outputDirectory, ExportManifest manifest)
        {
            WriteManifestFile(Path.Combine(outputDirectory, "manifest.json"), manifest);
        }

        /// <summary>写指定位置的清单文件（音频清单可能落到 &lt;out&gt;/audio/manifest.json）。</summary>
        private static void WriteManifestFile(string path, ExportManifest manifest)
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
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
