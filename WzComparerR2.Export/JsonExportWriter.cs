using System;
using System.Collections.Generic;
using System.Globalization;

namespace WzComparerR2.Export
{
    /// <summary>
    /// meta.json / manifest.json 的写出。字段集合即中间格式契约，
    /// 完整说明见 docs/plan/spec/wz-export-schema.md。
    /// </summary>
    internal static class JsonExportWriter
    {
        public const int SchemaVersion = 1;

        public static string WriteMeta(MetaGroup group)
        {
            var writer = new JsonTextWriter();

            writer.StartObject();
            writer.Name("schema");
            writer.Value(SchemaVersion);
            writer.Name("kind");
            writer.Value(group.Kind);
            writer.Name("source");
            writer.Value(group.Source);
            writer.Name("uniform");
            writer.Value(group.Uniform);

            if (group.Uniform && group.Frames.Count > 0)
            {
                MetaFrame canvas = group.Frames[0];
                writer.Name("canvas");
                writer.StartObject();
                writer.Name("width");
                writer.Value(canvas.Width);
                writer.Name("height");
                writer.Value(canvas.Height);
                writer.Name("format");
                writer.Value(canvas.Format);
                writer.Name("scale");
                writer.Value(canvas.Scale);
                writer.Name("pages");
                writer.Value(canvas.Pages);
                writer.EndObject();
            }

            writer.Name("frames");
            writer.StartArray();
            foreach (MetaFrame frame in group.Frames)
            {
                WriteFrame(writer, frame);
            }
            writer.EndArray();

            writer.EndObject();
            return writer.ToString();
        }

        public static string WriteManifest(ExportManifest manifest)
        {
            var writer = new JsonTextWriter();

            writer.StartObject();
            writer.Name("schema");
            writer.Value(manifest.Schema);
            writer.Name("tool");
            writer.Value(manifest.Tool);
            writer.Name("toolVersion");
            writer.Value(manifest.ToolVersion);
            writer.Name("generatedAt");
            writer.Value(manifest.GeneratedAtUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
            writer.Name("kind");
            writer.Value(manifest.Kind);
            if (manifest.AudioOnly)
            {
                writer.Name("audioOnly");
                writer.Value(true);
            }
            writer.Name("root");
            writer.Value(manifest.Root);

            if (manifest.Targets.Count > 0)
            {
                writer.Name("targets");
                writer.StartArray();
                foreach (string target in manifest.Targets)
                {
                    writer.Value(target);
                }
                writer.EndArray();
            }

            writer.Name("files");
            writer.StartArray();
            foreach (WzFileInfo file in manifest.Files)
            {
                writer.StartObject();
                writer.Name("file");
                writer.Value(file.File);
                writer.Name("version");
                writer.Value(file.Version);
                writer.EndObject();
            }
            writer.EndArray();

            writer.Name("counts");
            writer.StartObject();
            writer.Name("groups");
            writer.Value(manifest.Groups);
            writer.Name("frames");
            writer.Value(manifest.Frames);
            if (!manifest.AudioOnly)
            {
                writer.Name("pngFiles");
                writer.Value(manifest.PngFiles);
            }
            if (manifest.HasAudio)
            {
                writer.Name("audioGroups");
                writer.Value(manifest.AudioGroups);
                writer.Name("audioFiles");
                writer.Value(manifest.AudioFiles);
                writer.Name("audioBytes");
                writer.Value(manifest.AudioBytes);
            }
            writer.EndObject();

            writer.Name(manifest.AudioOnly ? "audio" : "assets");
            writer.StartArray();
            if (manifest.AudioOnly)
            {
                foreach (ManifestAudio audio in manifest.Audio)
                {
                    writer.StartObject();
                    writer.Name("source");
                    writer.Value(audio.Source);
                    writer.Name("dir");
                    writer.Value(audio.Dir);
                    writer.Name("meta");
                    writer.Value(audio.Meta);
                    writer.Name("files");
                    writer.Value(audio.Files);
                    writer.Name("bytes");
                    writer.Value(audio.Bytes);
                    writer.Name("hash");
                    writer.Value(audio.Hash);
                    writer.EndObject();
                }
            }
            else
            {
                foreach (ManifestAsset asset in manifest.Assets)
                {
                    writer.StartObject();
                    writer.Name("source");
                    writer.Value(asset.Source);
                    writer.Name("dir");
                    writer.Value(asset.Dir);
                    writer.Name("meta");
                    writer.Value(asset.Meta);
                    writer.Name("frames");
                    writer.Value(asset.Frames);
                    if (asset.Width != null)
                    {
                        writer.Name("width");
                        writer.Value(asset.Width.Value);
                    }
                    if (asset.Height != null)
                    {
                        writer.Name("height");
                        writer.Value(asset.Height.Value);
                    }
                    writer.Name("hash");
                    writer.Value(asset.Hash);
                    writer.EndObject();
                }
            }
            writer.EndArray();

            if (manifest.Warnings.Count > 0)
            {
                writer.Name("warnings");
                writer.StartArray();
                foreach (string warning in manifest.Warnings)
                {
                    writer.Value(warning);
                }
                writer.EndArray();
            }

            writer.EndObject();
            return writer.ToString();
        }

        /// <summary>audio.json：结构见 docs/plan/spec/wz-export-schema.md §9.3。</summary>
        public static string WriteAudio(AudioGroup group)
        {
            var writer = new JsonTextWriter();

            writer.StartObject();
            writer.Name("schema");
            writer.Value(SchemaVersion);
            writer.Name("kind");
            writer.Value(group.Kind);
            writer.Name("source");
            writer.Value(group.Source);

            writer.Name("entries");
            writer.StartArray();
            foreach (AudioEntry entry in group.Entries)
            {
                WriteAudioEntry(writer, entry);
            }
            writer.EndArray();

            writer.EndObject();
            return writer.ToString();
        }

        private static void WriteAudioEntry(JsonTextWriter writer, AudioEntry entry)
        {
            writer.StartObject();

            writer.Name("index");
            writer.Value(entry.Index);
            writer.Name("name");
            writer.Value(entry.Name);
            writer.Name("file");
            writer.Value(entry.File);
            writer.Name("format");
            writer.Value(entry.Format);
            if (!string.IsNullOrEmpty(entry.Codec))
            {
                writer.Name("codec");
                writer.Value(entry.Codec);
            }
            if (entry.Channels != null)
            {
                writer.Name("channels");
                writer.Value(entry.Channels.Value);
            }
            if (entry.SampleRate != null)
            {
                writer.Name("sampleRate");
                writer.Value(entry.SampleRate.Value);
            }
            if (entry.BitsPerSample != null)
            {
                writer.Name("bitsPerSample");
                writer.Value(entry.BitsPerSample.Value);
            }
            if (entry.BlockAlign != null)
            {
                writer.Name("blockAlign");
                writer.Value(entry.BlockAlign.Value);
            }

            writer.Name("duration");
            writer.Value(entry.Duration);
            writer.Name("bytes");
            writer.Value(entry.Bytes);
            writer.Name("sha256");
            writer.Value(entry.Sha256);
            writer.Name("source");
            writer.Value(entry.Source);

            if (!string.IsNullOrEmpty(entry.Uol))
            {
                writer.Name("uol");
                writer.Value(entry.Uol);
            }
            if (!string.IsNullOrEmpty(entry.Link))
            {
                writer.Name("link");
                writer.Value(entry.Link);
                writer.Name("inlink");
                writer.Value(entry.Inlink);
            }
            if (!string.IsNullOrEmpty(entry.LinkError))
            {
                writer.Name("linkError");
                writer.Value(entry.LinkError);
            }

            writer.EndObject();
        }

        private static void WriteFrame(JsonTextWriter writer, MetaFrame frame)
        {
            writer.StartObject();

            writer.Name("index");
            writer.Value(frame.Index);
            writer.Name("name");
            writer.Value(frame.Name);
            writer.Name("png");
            writer.Value(frame.Png);
            writer.Name("page");
            writer.Value(frame.Page);

            writer.Name("width");
            writer.Value(frame.Width);
            writer.Name("height");
            writer.Value(frame.Height);
            if (frame.SourceWidth != null)
            {
                writer.Name("sourceWidth");
                writer.Value(frame.SourceWidth.Value);
            }
            if (frame.SourceHeight != null)
            {
                writer.Name("sourceHeight");
                writer.Value(frame.SourceHeight.Value);
            }

            writer.Name("format");
            writer.Value(frame.Format);
            writer.Name("scale");
            writer.Value(frame.Scale);
            writer.Name("pages");
            writer.Value(frame.Pages);

            writer.Name("origin");
            writer.ValueRaw($"[{frame.OriginX}, {frame.OriginY}]");

            writer.Name("z");
            writer.Value(frame.Z);
            writer.Name("delay");
            writer.Value(frame.Delay);
            writer.Name("a0");
            writer.Value(frame.A0);
            writer.Name("a1");
            writer.Value(frame.A1);

            writer.Name("source");
            writer.Value(frame.Source);

            if (!string.IsNullOrEmpty(frame.Uol))
            {
                writer.Name("uol");
                writer.Value(frame.Uol);
            }
            if (!string.IsNullOrEmpty(frame.Link))
            {
                writer.Name("link");
                writer.Value(frame.Link);
                writer.Name("inlink");
                writer.Value(frame.Inlink);
            }
            if (!string.IsNullOrEmpty(frame.LinkError))
            {
                writer.Name("linkError");
                writer.Value(frame.LinkError);
            }

            writer.EndObject();
        }
    }
}
