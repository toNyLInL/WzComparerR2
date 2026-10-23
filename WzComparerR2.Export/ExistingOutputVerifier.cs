using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WzComparerR2.Export
{
    /// <summary>
    /// `wzexport verify --out &lt;目录&gt;`：只校验已经导出的产物，不读 wz、不重新导出。
    ///
    /// 断言：manifest 与磁盘一致（meta 的 hash / 帧数 / 计数）、逐帧 PNG 头尺寸与 meta 相符、
    /// 帧序号连续、z/delay/a0/a1 在合法区间；`--deep` 额外用 GDI+ 重新打开每张 PNG（慢）。
    /// 用于大包（30 万帧级）的复检：默认走 PNG 头，几秒到几十秒即可跑完。
    /// </summary>
    internal sealed class ExistingOutputVerifier
    {
        private const int MinZ = -9999;
        private const int MaxZ = 9999;
        private const int MaxReported = 50;

        private readonly string outputDirectory;
        private readonly ExportLogger logger;
        private readonly bool deep;
        private readonly ConcurrentBag<string> problems;
        private readonly object logLock = new object();

        private int groupCount;
        private int frameCount;
        private int deepChecked;

        public ExistingOutputVerifier(string outputDirectory, ExportLogger logger, bool deep)
        {
            this.outputDirectory = outputDirectory;
            this.logger = logger;
            this.deep = deep;
            this.problems = new ConcurrentBag<string>();
        }

        /// <summary>返回问题条数（0 表示通过）。</summary>
        public int Run()
        {
            var stopwatch = Stopwatch.StartNew();

            string manifestPath = Path.Combine(this.outputDirectory, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                this.logger.Error($"找不到 manifest.json：{manifestPath}");
                return 1;
            }

            JsonNode manifest;
            try
            {
                manifest = JsonReader.Parse(File.ReadAllText(manifestPath, Encoding.UTF8));
            }
            catch (Exception ex)
            {
                this.logger.Error($"manifest.json 解析失败：{NodePathUtility.Describe(ex)}");
                return 1;
            }

            int? schema = manifest.GetInt("schema");
            if (schema == null)
            {
                this.AddProblem("manifest 缺少 schema 字段");
            }
            else if (schema > JsonExportWriter.SchemaVersion)
            {
                this.AddProblem($"manifest schema={schema} 高于本工具的 {JsonExportWriter.SchemaVersion}，字段可能不认识");
            }

            IReadOnlyList<JsonNode> assets = manifest.GetArray("assets");
            this.logger.Info($"校验 {this.outputDirectory}");
            this.logger.Info($"  manifest: schema={schema} kind={manifest.GetString("kind")} root={manifest.GetString("root")} assets={assets.Count}");

            // 校验是纯读盘：按资产并行（默认 8 线程，或 CPU 数），大包上能快数倍。
            int parallelism = Math.Min(8, Math.Max(1, Environment.ProcessorCount));
            Parallel.ForEach(assets, new ParallelOptions { MaxDegreeOfParallelism = parallelism }, this.VerifyAsset);

            this.CheckCounts(manifest, "groups", this.groupCount, "分组");
            this.CheckCounts(manifest, "frames", this.frameCount, "帧");
            this.CheckCounts(manifest, "pngFiles", this.frameCount, "PNG");

            stopwatch.Stop();
            return this.Report(stopwatch.Elapsed);
        }

        private void VerifyAsset(JsonNode asset)
        {
            string metaRelative = asset.GetString("meta");
            string source = asset.GetString("source");
            string shown = string.IsNullOrEmpty(source) ? metaRelative : source;

            if (string.IsNullOrEmpty(metaRelative))
            {
                this.AddProblem($"manifest 条目缺少 meta 字段：{shown}");
                return;
            }

            string metaPath = this.ResolvePath(metaRelative);
            if (!File.Exists(metaPath))
            {
                this.AddProblem($"缺少 meta.json：{metaRelative}");
                return;
            }

            string expectedHash = asset.GetString("hash");
            if (!string.IsNullOrEmpty(expectedHash))
            {
                string actualHash = ExportNaming.Sha256File(metaPath);
                if (!string.Equals(expectedHash, actualHash, StringComparison.Ordinal))
                {
                    this.AddProblem($"meta.json 内容与 manifest 记录的 hash 不一致：{metaRelative}");
                }
            }

            JsonNode meta;
            try
            {
                meta = JsonReader.Parse(File.ReadAllText(metaPath, Encoding.UTF8));
            }
            catch (Exception ex)
            {
                this.AddProblem($"meta.json 解析失败：{metaRelative}（{NodePathUtility.Describe(ex)}）");
                return;
            }

            IReadOnlyList<JsonNode> frames = meta.GetArray("frames");
            int? expectedFrames = asset.GetInt("frames");
            if (expectedFrames != null && expectedFrames.Value != frames.Count)
            {
                this.AddProblem($"帧数与 manifest 不一致：{metaRelative}（manifest {expectedFrames}，meta {frames.Count}）");
            }

            Interlocked.Increment(ref this.groupCount);
            string directory = Path.GetDirectoryName(metaPath);

            for (int i = 0; i < frames.Count; i++)
            {
                JsonNode frame = frames[i];
                Interlocked.Increment(ref this.frameCount);

                int? index = frame.GetInt("index");
                if (index != i)
                {
                    this.AddProblem($"帧序号不连续：{metaRelative}#{i}（index={index}）");
                }

                string pngName = frame.GetString("png");
                if (string.IsNullOrEmpty(pngName))
                {
                    this.AddProblem($"帧缺少 png 字段：{metaRelative}#{i}");
                    continue;
                }

                this.VerifyFramePng(Path.Combine(directory, pngName), metaRelative, pngName, frame);
                this.CheckFrameRanges(frame, metaRelative, pngName);
            }
        }

        private void VerifyFramePng(string pngPath, string metaRelative, string pngName, JsonNode frame)
        {
            int? expectedWidth = frame.GetInt("width");
            int? expectedHeight = frame.GetInt("height");

            // 开心路径只开一次文件：失败时才再 stat 一次，用来区分「文件不存在」和「头部损坏」。
            if (!PngHeader.TryReadSize(pngPath, out int width, out int height, out string error))
            {
                if (!File.Exists(pngPath))
                {
                    this.AddProblem($"缺少 PNG：{metaRelative} -> {pngName}");
                }
                else
                {
                    this.AddProblem($"PNG 头无效：{metaRelative} -> {pngName}（{error}）");
                }
                return;
            }

            if (expectedWidth != null && expectedHeight != null
                && (width != expectedWidth.Value || height != expectedHeight.Value))
            {
                this.AddProblem($"PNG 尺寸与 meta 不一致：{metaRelative} -> {pngName}（meta {expectedWidth}x{expectedHeight}，实际 {width}x{height}）");
            }

            if (!this.deep)
            {
                return;
            }

            try
            {
                using (Image image = Image.FromFile(pngPath))
                {
                    Interlocked.Increment(ref this.deepChecked);
                }
            }
            catch (Exception ex)
            {
                this.AddProblem($"PNG 无法重新打开：{metaRelative} -> {pngName}（{NodePathUtility.Describe(ex)}）");
            }
        }

        private void CheckFrameRanges(JsonNode frame, string metaRelative, string pngName)
        {
            int? z = frame.GetInt("z");
            if (z != null && (z.Value < MinZ || z.Value > MaxZ))
            {
                this.AddProblem($"z 超出 [{MinZ}, {MaxZ}]：{metaRelative}/{pngName}（z={z}）");
            }

            int? delay = frame.GetInt("delay");
            if (delay == null || delay.Value <= 0)
            {
                this.AddProblem($"delay 必须为正数毫秒：{metaRelative}/{pngName}（delay={delay}）");
            }

            foreach (string alpha in new[] { "a0", "a1" })
            {
                int? value = frame.GetInt(alpha);
                if (value != null && (value.Value < 0 || value.Value > 255))
                {
                    this.AddProblem($"{alpha} 超出 [0, 255]：{metaRelative}/{pngName}（{alpha}={value}）");
                }
            }
        }

        private void CheckCounts(JsonNode manifest, string name, int actual, string label)
        {
            JsonNode counts = manifest["counts"];
            int? expected = counts?.GetInt(name);
            if (expected == null)
            {
                this.AddProblem($"manifest.counts 缺少 {name}");
                return;
            }
            if (expected.Value != actual)
            {
                this.AddProblem($"manifest.counts.{name}（{label}）与实际不一致：{expected} != {actual}");
            }
        }

        private int Report(TimeSpan elapsed)
        {
            if (this.problems.Count == 0)
            {
                this.logger.Info(string.Format(
                    "校验通过：{0} 组 / {1} 帧，PNG 头检查 {1} 张{2}，耗时 {3:0.0}s。",
                    this.groupCount,
                    this.frameCount,
                    this.deep ? $"，GDI+ 深度重开 {this.deepChecked} 张" : string.Empty,
                    elapsed.TotalSeconds));
                return 0;
            }

            this.logger.Error($"校验失败，共 {this.problems.Count} 项（耗时 {elapsed.TotalSeconds:0.0}s）：");
            int shown = 0;
            foreach (string problem in this.problems)
            {
                if (shown >= MaxReported)
                {
                    break;
                }
                Console.Error.WriteLine("  - " + problem);
                shown++;
            }
            if (this.problems.Count > shown)
            {
                Console.Error.WriteLine($"  …还有 {this.problems.Count - shown} 项未显示。");
            }
            return this.problems.Count;
        }

        private void AddProblem(string message)
        {
            this.problems.Add(message);
        }

        private string ResolvePath(string relativePath)
        {
            return Path.Combine(this.outputDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
        }
    }
}
