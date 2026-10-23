using System;
using System.Collections.Generic;
using WzComparerR2.WzLib;

namespace WzComparerR2.Export
{
    /// <summary>一个候选帧：帧节点本身 + 解析后真正持有 canvas 的节点 + 链接信息。</summary>
    internal sealed class FrameCandidate
    {
        public Wz_Node FrameNode { get; set; }

        public Wz_Node CanvasNode { get; set; }

        public WzCanvasLink Link { get; set; }

        public string SymbolicPath { get; set; }
    }

    /// <summary>meta.json 里的一帧。</summary>
    internal sealed class MetaFrame
    {
        public int Index { get; set; }

        public string Name { get; set; }

        public string Png { get; set; }

        public int Page { get; set; }

        public int Width { get; set; }

        public int Height { get; set; }

        /// <summary>wz 声明的原始尺寸；与导出的 PNG 尺寸不同（BC7 4 对齐裁边）时才写。</summary>
        public int? SourceWidth { get; set; }

        public int? SourceHeight { get; set; }

        public string Format { get; set; }

        public int Scale { get; set; }

        public int Pages { get; set; }

        public int OriginX { get; set; }

        public int OriginY { get; set; }

        public int Z { get; set; }

        public int Delay { get; set; }

        public int A0 { get; set; }

        public int A1 { get; set; }

        public string Source { get; set; }

        public string Uol { get; set; }

        public string Link { get; set; }

        public bool Inlink { get; set; }

        public string LinkError { get; set; }
    }

    /// <summary>一个导出分组（同属一个父节点的一组帧，落到一个目录 + meta.json）。</summary>
    internal sealed class MetaGroup
    {
        public MetaGroup()
        {
            this.Frames = new List<MetaFrame>();
        }

        public string Kind { get; set; }

        /// <summary>分组节点的 wz 符号路径，例如 "UI.wz/UIWindow.img/Main/backgrnd"。</summary>
        public string Source { get; set; }

        /// <summary>相对输出根的目录，'/' 分隔。</summary>
        public string RelativeDir { get; set; }

        public List<MetaFrame> Frames { get; }

        /// <summary>组内所有帧尺寸/格式/scale/pages 完全一致时为 true，此时 canvas 块有效。</summary>
        public bool Uniform { get; set; }

        public string MetaRelativePath { get; set; }
    }

    internal sealed class WzFileInfo
    {
        public string File { get; set; }

        public int Version { get; set; }
    }

    internal sealed class ManifestAsset
    {
        public string Source { get; set; }

        public string Dir { get; set; }

        public string Meta { get; set; }

        public int Frames { get; set; }

        public int? Width { get; set; }

        public int? Height { get; set; }

        /// <summary>meta.json 内容的 sha256，便于增量与去重。</summary>
        public string Hash { get; set; }
    }

    internal sealed class ExportManifest
    {
        public ExportManifest()
        {
            this.Files = new List<WzFileInfo>();
            this.Assets = new List<ManifestAsset>();
            this.Warnings = new List<string>();
        }

        public int Schema { get; set; } = 1;

        public string Tool { get; set; } = "wzexport";

        public string ToolVersion { get; set; }

        public DateTime GeneratedAtUtc { get; set; }

        public string Kind { get; set; }

        public string Root { get; set; }

        /// <summary>本次导出覆盖的目标节点符号路径（--list 批量时会多于一个）。</summary>
        public List<string> Targets { get; } = new List<string>();

        public List<WzFileInfo> Files { get; }

        public int Groups { get; set; }

        public int Frames { get; set; }

        public int PngFiles { get; set; }

        public List<ManifestAsset> Assets { get; }

        public List<string> Warnings { get; }
    }
}
