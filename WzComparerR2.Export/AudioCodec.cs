using System;
using System.IO;
using System.Text;
using WzComparerR2.WzLib;

namespace WzComparerR2.Export
{
    /// <summary>
    /// 一次音频解码的产物：容器格式、WAV 头参数、落盘字节与码流统计。
    /// 字段语义见 docs/plan/spec/wz-export-schema.md §9.4。
    /// </summary>
    internal sealed class AudioPayload
    {
        public const string FormatMp3 = "mp3";
        public const string FormatWav = "wav";
        public const string FormatBin = "bin";
        public const string FormatUnknown = "unknown";

        /// <summary>mp3 / wav / bin / unknown。</summary>
        public string Format { get; set; } = FormatUnknown;

        /// <summary>wz 媒体类型自带的名字（"MPEG Layer-3" 之类），识别不出时为空。</summary>
        public string Codec { get; set; }

        public int? Channels { get; set; }

        public int? SampleRate { get; set; }

        public int? BitsPerSample { get; set; }

        public int? BlockAlign { get; set; }

        /// <summary>WAV 头里写 AvgBytesPerSec 用的值。</summary>
        public int AvgBytesPerSec { get; set; }

        /// <summary>wz 自己记录的时长（毫秒），0 表示没记。</summary>
        public int SoundMs { get; set; }

        /// <summary>要落盘的完整文件字节（wav 已拼好 44 字节 RIFF 头）。</summary>
        public byte[] Data { get; set; }

        /// <summary>扩展名（不含点）。</summary>
        public string Extension => this.Format ?? FormatUnknown;
    }

    /// <summary>
    /// 音频格式的判定与落盘字节准备。判定只看 wz 的 AM_MEDIA_TYPE，不看 <see cref="Wz_Sound.SoundType"/>：
    /// 后者只认 Stream 大类，v271 里大量 MP3 是 MEDIATYPE_Audio，会被判成 Unknown 且 ExtractSound() 返回 null
    /// （详见 spec §9.4）。
    /// </summary>
    internal static class AudioCodec
    {
        // 子类型对照表。判定真正依据的是「PbFormat 里解出的 WAVEFORMATEX」+ 这里的子类型 + 码流探测；
        // v271 的 AM_MEDIA_TYPE 布局并不可靠（见 spec §9.4），不能只看 MajorType 就下结论。
        private static readonly Guid MEDIASUBTYPE_MPEG1Packet = Guid.Parse("{e436eb80-524f-11ce-9f53-0020af0ba770}");
        private static readonly Guid MEDIASUBTYPE_MPEG1Payload = Guid.Parse("{e436eb81-524f-11ce-9f53-0020af0ba770}");
        private static readonly Guid MEDIASUBTYPE_MPEG1System = Guid.Parse("{e436eb84-524f-11ce-9f53-0020af0ba770}");
        private static readonly Guid MEDIASUBTYPE_MPEG1Video = Guid.Parse("{e436eb86-524f-11ce-9f53-0020af0ba770}");
        private static readonly Guid MEDIASUBTYPE_MPEG1VideoCD = Guid.Parse("{e436eb85-524f-11ce-9f53-0020af0ba770}");
        private static readonly Guid MEDIASUBTYPE_ADPCM = Guid.Parse("{e436eb89-524f-11ce-9f53-0020af0ba770}");
        private static readonly Guid MEDIASUBTYPE_IEEE_FLOAT = Guid.Parse("{00000003-0000-0010-8000-00aa00389b71}");
        private static readonly Guid MEDIASUBTYPE_DTS = Guid.Parse("{e06d8034-db46-11cf-b6a7-00aa003aea30}");
        private static readonly Guid MEDIASUBTYPE_DRM_Audio = Guid.Parse("{00000009-0000-0010-8000-00aa00389b71}");
        private static readonly Guid MEDIASUBTYPE_WF_PCM = Guid.Parse("{00000001-0000-0010-8000-00aa00389b71}");
        private static readonly Guid MEDIASUBTYPE_MIDI = Guid.Parse("{e436eb8c-524f-11ce-9f53-0020af0ba770}");
        private static readonly Guid MEDIASUBTYPE_AIFF = Guid.Parse("{e436eb8d-524f-11ce-9f53-0020af0ba770}");
        private static readonly Guid MEDIASUBTYPE_AU = Guid.Parse("{e436eb88-524f-11ce-9f53-0020af0ba770}");
        private static readonly Guid MEDIASUBTYPE_ASF = Guid.Parse("{3db80f90-9412-11d1-ade4-0000f8754b99}");
        private static readonly Guid MEDIASUBTYPE_AVI = Guid.Parse("{e436eb88-524f-11ce-9f53-0020af0ba770}");

        private static readonly Guid[] PcmLikeSubTypes =
        {
            MEDIASUBTYPE_WF_PCM, MEDIASUBTYPE_ADPCM, MEDIASUBTYPE_IEEE_FLOAT, MEDIASUBTYPE_DTS, MEDIASUBTYPE_DRM_Audio,
        };

        private static readonly Guid[] Mpeg1SubTypes =
        {
            MEDIASUBTYPE_MPEG1Packet, MEDIASUBTYPE_MPEG1Payload, MEDIASUBTYPE_MPEG1System,
            MEDIASUBTYPE_MPEG1Video, MEDIASUBTYPE_MPEG1VideoCD,
        };

        private static readonly Guid[] ContainerSubTypes =
        {
            MEDIASUBTYPE_MIDI, MEDIASUBTYPE_AIFF, MEDIASUBTYPE_AU, MEDIASUBTYPE_ASF, MEDIASUBTYPE_AVI,
        };

        /// <summary>
        /// 把 <see cref="Wz_Sound"/> 解成落盘字节。判定顺序：先看媒体类型里的 WAVEFORMATEX（MP3 波格式），
        /// 再看子类型，最后按码流探测（v271 的媒体类型不可靠，这条兜底很关键）。
        /// </summary>
        public static AudioPayload Read(Wz_Sound sound, byte[] raw)
        {
            var payload = new AudioPayload { SoundMs = sound.Ms };

            Interop.AM_MEDIA_TYPE mediaType = sound.MediaType;
            bool mpegLayer3 = false;

            if (mediaType?.PbFormat is Interop.WAVEFORMATEX wfx)
            {
                payload.Codec = DescribeFormatTag(wfx.FormatTag);
                FillWaveFormat(payload, wfx);
                mpegLayer3 = wfx.FormatTag == Interop.WAVE_FORMAT_MPEGLAYER3;
            }
            else if (mediaType?.PbFormat is Interop.MPEGLAYER3WAVEFORMAT mp3Format)
            {
                payload.Codec = "MPEG Layer-3";
                FillWaveFormat(payload, mp3Format.Wfx);
                mpegLayer3 = true;
            }

            if (mpegLayer3)
            {
                payload.Format = AudioPayload.FormatMp3;
                payload.Data = raw;
                return payload;
            }

            if (payload.Codec == null && mediaType != null)
            {
                payload.Codec = DescribeSubType(mediaType.SubType);
            }
            if (payload.Format == AudioPayload.FormatUnknown && mediaType != null)
            {
                Guid subType = mediaType.SubType;
                if (subType == Interop.MEDIASUBTYPE_MPEG1Audio)
                {
                    payload.Format = AudioPayload.FormatMp3;
                }
                else if (subType == Interop.MEDIASUBTYPE_WAVE)
                {
                    // v271 最常见的流派：子类型是 WAVE，但 PbFormat 没解析成 WAVEFORMATEX。
                    // 交给码流探测（真正是 PCM 的会命中 RIFF/WAVE 或位宽自洽的 raw PCM，其余当 MP3）。
                    payload.Format = ProbeMp3Frame(raw) ? AudioPayload.FormatMp3 : AudioPayload.FormatWav;
                }
                else if (Array.IndexOf(PcmLikeSubTypes, subType) >= 0)
                {
                    payload.Format = AudioPayload.FormatWav;
                }
                else if (Array.IndexOf(Mpeg1SubTypes, subType) >= 0)
                {
                    payload.Format = AudioPayload.FormatMp3;
                }
                else if (Array.IndexOf(ContainerSubTypes, subType) >= 0)
                {
                    payload.Format = AudioPayload.FormatBin;
                }
            }

            if (payload.Format == AudioPayload.FormatWav && !HasStateableWaveFormat(payload))
            {
                payload.Format = ProbeWav(raw, payload) ? AudioPayload.FormatWav : ProbeMp3Frame(raw) ? AudioPayload.FormatMp3 : AudioPayload.FormatUnknown;
            }

            if (payload.Format == AudioPayload.FormatUnknown)
            {
                // 媒体类型什么都没给出时的最后兜底：数据本身就以 RIFF/WAVE 开头，或像一帧 MP3。
                if (StartsWithRiffWave(raw))
                {
                    payload.Format = AudioPayload.FormatWav;
                    payload.Data = raw;
                    return payload;
                }

                payload.Format = ProbeMp3Frame(raw) ? AudioPayload.FormatMp3 : AudioPayload.FormatBin;
            }

            switch (payload.Format)
            {
                case AudioPayload.FormatMp3:
                    payload.Data = raw;
                    break;

                case AudioPayload.FormatWav:
                    if (StartsWithRiffWave(raw))
                    {
                        payload.Data = raw;
                    }
                    else
                    {
                        EnsureWaveFormat(payload);
                        payload.Data = AudioWav.Wrap(raw, payload);
                    }
                    break;

                default:
                    payload.Format = AudioPayload.FormatBin;
                    payload.Data = raw;
                    break;
            }

            return payload;
        }

        /// <summary>判定结果里是否已经具备可写进 WAV 头的完整参数。</summary>
        private static bool HasStateableWaveFormat(AudioPayload payload)
        {
            return payload.Channels > 0 && payload.SampleRate > 0 && payload.BitsPerSample > 0 && payload.BlockAlign > 0;
        }

        /// <summary>按 blockAlign 与数据长度推断位宽/声道，补齐 WAV 头参数。实测 PCM 条目的这些字段都是对的。</summary>
        private static void EnsureWaveFormat(AudioPayload payload)
        {
            payload.Channels = payload.Channels ?? 1;
            payload.BitsPerSample = payload.BitsPerSample ?? 16;
            payload.SampleRate = payload.SampleRate ?? 22050;
            payload.BlockAlign = payload.BlockAlign ?? (payload.BitsPerSample.Value / 8 * payload.Channels.Value);
            if (payload.AvgBytesPerSec <= 0)
            {
                payload.AvgBytesPerSec = payload.SampleRate.Value * payload.BlockAlign.Value;
            }
        }

        private static void FillWaveFormat(AudioPayload payload, Interop.WAVEFORMATEX wfx)
        {
            if (wfx.Channels > 0)
            {
                payload.Channels = wfx.Channels;
            }
            if (wfx.SamplesPerSec > 0)
            {
                payload.SampleRate = (int)wfx.SamplesPerSec;
            }
            if (wfx.BitsPerSample > 0)
            {
                payload.BitsPerSample = wfx.BitsPerSample;
            }
            if (wfx.BlockAlign > 0)
            {
                payload.BlockAlign = wfx.BlockAlign;
            }
            if (wfx.AvgBytesPerSec > 0)
            {
                payload.AvgBytesPerSec = (int)wfx.AvgBytesPerSec;
            }
        }

        private static string DescribeFormatTag(ushort formatTag)
        {
            switch (formatTag)
            {
                case Interop.WAVE_FORMAT_PCM:
                    return "WAVE_FORMAT_PCM/0x0001";
                case Interop.WAVE_FORMAT_MPEGLAYER3:
                    return "MPEG Layer-3";
                case 0x0002:
                    return "WAVE_FORMAT_ADPCM/0x0002";
                case 0x0003:
                    return "WAVE_FORMAT_IEEE_FLOAT/0x0003";
                default:
                    return $"0x{formatTag:x4}";
            }
        }

        private static string DescribeSubType(Guid subType)
        {
            if (subType == Interop.MEDIASUBTYPE_MPEG1Audio) return "MPEG1AUDIO";
            if (subType == Interop.MEDIASUBTYPE_WAVE) return "WAVE";
            if (subType == MEDIASUBTYPE_WF_PCM) return "PCM";
            if (subType == MEDIASUBTYPE_ADPCM) return "ADPCM";
            if (subType == MEDIASUBTYPE_IEEE_FLOAT) return "IEEE_FLOAT";
            if (subType == MEDIASUBTYPE_DTS) return "DTS";
            if (subType == MEDIASUBTYPE_DRM_Audio) return "DRM_Audio";
            if (subType == MEDIASUBTYPE_MIDI) return "MIDI";
            if (subType == MEDIASUBTYPE_AIFF) return "AIFF";
            if (subType == MEDIASUBTYPE_AU) return "AU";
            if (subType == MEDIASUBTYPE_ASF) return "ASF";
            if (subType == MEDIASUBTYPE_AVI) return "AVI";
            if (subType == MEDIASUBTYPE_MPEG1System) return "MPEG1System";
            if (subType == MEDIASUBTYPE_MPEG1Video) return "MPEG1Video";
            if (subType == MEDIASUBTYPE_MPEG1VideoCD) return "MPEG1VideoCD";
            return null;
        }

        private static bool StartsWithRiffWave(byte[] raw)
        {
            return raw.Length >= 12
                && AudioSignatures.MatchFourCc(raw, 0, "RIFF")
                && AudioSignatures.MatchFourCc(raw, 8, "WAVE");
        }

        /// <summary>PCM 兜底：媒体类型没解析出 WAVEFORMATEX 时，用块对齐整除关系判断是不是裸 16 位 PCM。</summary>
        private static bool ProbeWav(byte[] raw, AudioPayload payload)
        {
            int channels = payload.Channels ?? 1;
            int sampleRate = payload.SampleRate ?? 22050;
            int bits = payload.BitsPerSample ?? 16;
            int blockAlign = payload.BlockAlign ?? (bits / 8 * channels);
            if (blockAlign <= 0 || raw.Length == 0 || raw.Length % blockAlign != 0)
            {
                return false;
            }

            payload.Channels = channels;
            payload.SampleRate = sampleRate;
            payload.BitsPerSample = bits;
            payload.BlockAlign = blockAlign;
            if (payload.AvgBytesPerSec <= 0)
            {
                payload.AvgBytesPerSec = sampleRate * blockAlign;
            }
            return true;
        }

        /// <summary>码流探测：前 512 字节里有没有一个能对上"同步字 + 合法层/采样率/比特率"的 MP3 帧头。</summary>
        internal static bool ProbeMp3Frame(byte[] raw)
        {
            if (raw == null)
            {
                return false;
            }

            int limit = Math.Min(raw.Length - 4, 512);
            for (int i = 0; i <= limit; i++)
            {
                if (AudioSignatures.IsMp3FrameHeader(raw, i))
                {
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>WAV 容器与音频文件签名。只做容器层面的事，不碰编解码。</summary>
    internal static class AudioWav
    {
        /// <summary>
        /// 拼 44 字节 RIFF 头。与 Wz_Sound.ExtractSound() 的 PCM 分支逐字段一致（小端、Pack=1）。
        /// </summary>
        public static byte[] Wrap(byte[] raw, AudioPayload format)
        {
            if (raw == null)
            {
                throw new ArgumentNullException(nameof(raw));
            }

            var data = new byte[raw.Length + 44];
            using (var stream = new MemoryStream(data, true))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write(Encoding.ASCII.GetBytes("RIFF"));
                writer.Write(raw.Length + 36);
                writer.Write(Encoding.ASCII.GetBytes("WAVE"));
                writer.Write(Encoding.ASCII.GetBytes("fmt "));
                writer.Write(16);
                writer.Write(Interop.WAVE_FORMAT_PCM);
                writer.Write((ushort)(format.Channels ?? 1));
                writer.Write((uint)(format.SampleRate ?? 22050));
                writer.Write((uint)format.AvgBytesPerSec);
                writer.Write((ushort)(format.BlockAlign ?? 2));
                writer.Write((ushort)(format.BitsPerSample ?? 16));
                writer.Write(Encoding.ASCII.GetBytes("data"));
                writer.Write(raw.Length);
                writer.Write(raw, 0, raw.Length);
            }
            return data;
        }
    }

    /// <summary>
    /// 音频文件签名识别：导出后 --verify 和 verify --out 用它判断"文件能不能被下游读"。
    /// </summary>
    internal static class AudioSignatures
    {
        public static bool MatchFourCc(byte[] data, int offset, string fourCc)
        {
            if (data == null || offset < 0 || offset + 4 > data.Length || fourCc.Length != 4)
            {
                return false;
            }
            for (int i = 0; i < 4; i++)
            {
                if (data[offset + i] != fourCc[i])
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>MP3 帧头：0xFF + 层指示位（10 = MPEG Layer III、01 = II、11 = I）+ 合法采样率/比特率索引。</summary>
        public static bool IsMp3FrameHeader(byte[] data, int offset)
        {
            if (data == null || offset < 0 || offset + 4 > data.Length)
            {
                return false;
            }
            if (data[offset] != 0xFF || (data[offset + 1] & 0xE0) != 0xE0)
            {
                return false;
            }

            int version = (data[offset + 1] >> 3) & 0x03;   // 0=2.5, 1=保留, 2=MPEG-2, 3=MPEG-1
            int layer = (data[offset + 1] >> 1) & 0x03;     // 0=保留
            if (version == 1 || layer == 0)
            {
                return false;
            }

            int bitrate = (data[offset + 2] >> 4) & 0x0F;   // 0=free, 15=无效
            int sampleRate = (data[offset + 2] >> 2) & 0x03; // 3=保留
            return bitrate != 15 && sampleRate != 3;
        }

        /// <summary>
        /// 检查落盘文件能不能被识别：wav 要 RIFF/WAVE + fmt  块且参数自洽；
        /// mp3 要首帧同步字；bin 只要求非空。返回 null 表示通过，否则是失败原因。
        /// </summary>
        public static string CheckFormat(string format, byte[] data)
        {
            if (data == null || data.Length == 0)
            {
                return "文件为空";
            }

            switch (format)
            {
                case AudioPayload.FormatWav:
                    return CheckWav(data);

                case AudioPayload.FormatMp3:
                case AudioPayload.FormatUnknown:
                    if (ProbeMp3Header(data))
                    {
                        return null;
                    }
                    return "MP3 首帧同步字无效";

                default:
                    // bin：客户端里是 MIDI/容器等不认识的格式，只要求非空。
                    return null;
            }
        }

        private static bool ProbeMp3Header(byte[] data)
        {
            if (AudioCodec.ProbeMp3Frame(data))
            {
                return true;
            }
            // 首帧可能被 ID3 标签挡住，容忍最前面的 ID3v2 头。
            if (data.Length > 10 && MatchFourCc(data, 0, "ID3"))
            {
                int size = ((data[6] & 0x7F) << 21) | ((data[7] & 0x7F) << 14) | ((data[8] & 0x7F) << 7) | (data[9] & 0x7F);
                int start = Math.Min(data.Length - 4, 10 + size);
                for (int i = 10; i <= start; i++)
                {
                    if (AudioSignatures.IsMp3FrameHeader(data, i))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private static string CheckWav(byte[] data)
        {
            if (data.Length < 44 || !MatchFourCc(data, 0, "RIFF") || !MatchFourCc(data, 8, "WAVE"))
            {
                return "不是 RIFF/WAVE 容器";
            }

            int riffSize = BitConverter.ToInt32(data, 4);
            if (riffSize != data.Length - 8)
            {
                return $"RIFF 长度与文件大小不符（{riffSize} != {data.Length - 8}）";
            }

            // 找 fmt  块（正常就在偏移 12，这里扫一遍以容忍额外块）
            int offset = 12;
            while (offset + 8 <= data.Length)
            {
                string id = Encoding.ASCII.GetString(data, offset, 4);
                int size = BitConverter.ToInt32(data, offset + 4);
                if (size < 0)
                {
                    return "块长度非法";
                }

                if (id == "fmt ")
                {
                    if (size < 16 || offset + 8 + 16 > data.Length)
                    {
                        return "fmt 块过短";
                    }
                    int channels = BitConverter.ToUInt16(data, offset + 10);
                    int sampleRate = BitConverter.ToInt32(data, offset + 12);
                    int blockAlign = BitConverter.ToUInt16(data, offset + 20);
                    int bits = BitConverter.ToUInt16(data, offset + 22);
                    if (channels < 1 || channels > 32)
                    {
                        return $"声道数非法（{channels}）";
                    }
                    if (sampleRate <= 0)
                    {
                        return $"采样率非法（{sampleRate}）";
                    }
                    if (bits <= 0 || bits > 64)
                    {
                        return $"位宽非法（{bits}）";
                    }
                    if (blockAlign <= 0)
                    {
                        return $"块对齐非法（{blockAlign}）";
                    }
                    return null;
                }

                offset += 8 + size;
            }
            return "缺少 fmt 块";
        }
    }
}
