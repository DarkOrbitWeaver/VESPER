using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Vesper.Engine;

/// <summary>
/// Which decoders and encoders this Windows build actually has. Opus and Vorbis decoding depend on the Store
/// "Web Media Extensions"; VP9 and AV1 on their own extensions. Asked once (about 100 ms, so callers prime it
/// off the UI thread) and cached. When the probe itself cannot run, nothing is claimed either way and the
/// planner keeps today's behaviour of trying the conversion.
/// </summary>
public sealed record CodecCapabilities(bool Known, bool Opus, bool Vorbis, bool Flac, bool Vp9, bool Av1, bool Hevc,
    bool AacEncoder, bool Mp3Encoder)
{
    public static readonly CodecCapabilities Unknown = new(false, false, false, false, false, false, false, false, false);
    private static readonly Lazy<CodecCapabilities> Probed = new(Probe, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Tests and the planner can pin a value; null means "probe the machine".</summary>
    public static CodecCapabilities? Override { get; set; }

    public static CodecCapabilities Current => Override ?? Probed.Value;

    /// <summary>Starts the probe on a worker thread so the first picker does not pay for it.</summary>
    public static void Prime()
    {
        if (!OperatingSystem.IsWindows() || Probed.IsValueCreated) return;
        _ = Task.Run(() => Probed.Value);
    }

    /// <summary>True unless we know for sure this machine cannot decode the codec.</summary>
    public bool CanDecode(string? codec) => !Known || codec?.ToLowerInvariant() switch
    {
        "opus" => Opus,
        "vorbis" => Vorbis,
        "flac" => Flac,
        "vp9" or "vp09" => Vp9,
        "av1" or "av01" => Av1,
        "h265" or "hevc" or "hvc1" or "hev1" => Hevc,
        _ => true                                   // AAC, MP3, H.264 ship with Windows
    };

    /// <summary>Plain words for the picker when a conversion is not available on this PC.</summary>
    public static string MissingDecoderHint(string? codec) => codec?.ToLowerInvariant() switch
    {
        "opus" or "vorbis" => "This PC can't decode this audio. Microsoft's free Web Media Extensions (Store) adds it.",
        "vp9" or "vp09" => "This PC can't decode VP9 video. The free VP9 Video Extensions (Store) adds it.",
        "av1" or "av01" => "This PC can't decode AV1 video. The free AV1 Video Extension (Store) adds it.",
        "h265" or "hevc" => "This PC can't decode HEVC video. The HEVC Video Extensions (Store) adds it.",
        _ => "This PC can't decode this media."
    };

    private static CodecCapabilities Probe()
    {
        if (!OperatingSystem.IsWindows()) return Unknown;
        try { return ProbeWindows(); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or COMException or BadImageFormatException)
        { return Unknown; }
    }

    // Media Foundation interop (same GUID conventions as WindowsMediaConverter)

    private static Guid MediaGuid(int format) => new(format, 0, 0x0010, 0x80, 0, 0, 0xaa, 0, 0x38, 0x9b, 0x71);
    private static readonly Guid MajorAudio = MediaGuid(0x73647561), MajorVideo = MediaGuid(0x73646976);
    private static readonly Guid OpusType = MediaGuid(0x7375704F), FlacType = MediaGuid(0xF1AC), Mp3Type = MediaGuid(0x55), AacType = MediaGuid(0x1610);
    private static readonly Guid VorbisType = new("8D2FD10B-5841-4a6b-8905-588FEC1ADED9");
    private static readonly Guid Vp90Type = MediaGuid(0x30395056), Av1Type = MediaGuid(0x31305641), HevcType = MediaGuid(0x43564548);
    private static readonly Guid AudioDecoders = new("9ea73fb4-ef7a-4559-8d5d-719d8f0426c7");
    private static readonly Guid VideoDecoders = new("d6c02d4b-6833-45b4-971a-05a4b04bab91");
    private static readonly Guid AudioEncoders = new("91c64bd0-f91e-4d8c-9276-db248279d975");
    private const uint EnumFlags = 0x1 | 0x2 | 0x4 | 0x40;   // sync, async, hardware, sort-and-filter

    [StructLayout(LayoutKind.Sequential)]
    private struct RegisterTypeInfo { public Guid Major; public Guid Subtype; }

    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFShutdown();
    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFTEnumEx(Guid category, uint flags, ref RegisterTypeInfo input, IntPtr output, out IntPtr activates, out uint count);
    [DllImport("mfplat.dll", ExactSpelling = true, EntryPoint = "MFTEnumEx")]
    private static extern int MFTEnumExOutput(Guid category, uint flags, IntPtr input, ref RegisterTypeInfo output, out IntPtr activates, out uint count);

    [SupportedOSPlatform("windows")]
    private static CodecCapabilities ProbeWindows()
    {
        if (MFStartup(0x20070, 1) < 0) return Unknown;   // MFSTARTUP_NOSOCKET
        try
        {
            return new CodecCapabilities(true,
                Opus: HasDecoder(AudioDecoders, MajorAudio, OpusType),
                Vorbis: HasDecoder(AudioDecoders, MajorAudio, VorbisType),
                Flac: HasDecoder(AudioDecoders, MajorAudio, FlacType),
                Vp9: HasDecoder(VideoDecoders, MajorVideo, Vp90Type),
                Av1: HasDecoder(VideoDecoders, MajorVideo, Av1Type),
                Hevc: HasDecoder(VideoDecoders, MajorVideo, HevcType),
                AacEncoder: HasEncoder(AudioEncoders, MajorAudio, AacType),
                Mp3Encoder: HasEncoder(AudioEncoders, MajorAudio, Mp3Type));
        }
        finally { MFShutdown(); }
    }

    private static bool HasDecoder(Guid category, Guid major, Guid subtype)
    {
        var input = new RegisterTypeInfo { Major = major, Subtype = subtype };
        int hr = MFTEnumEx(category, EnumFlags, ref input, IntPtr.Zero, out IntPtr activates, out uint count);
        return ReleaseAndCount(hr, activates, count);
    }

    private static bool HasEncoder(Guid category, Guid major, Guid subtype)
    {
        var output = new RegisterTypeInfo { Major = major, Subtype = subtype };
        int hr = MFTEnumExOutput(category, EnumFlags, IntPtr.Zero, ref output, out IntPtr activates, out uint count);
        return ReleaseAndCount(hr, activates, count);
    }

    private static bool ReleaseAndCount(int hr, IntPtr activates, uint count)
    {
        if (hr < 0) return false;
        if (activates != IntPtr.Zero)
        {
            for (int i = 0; i < count; i++)
            {
                IntPtr activate = Marshal.ReadIntPtr(activates, i * IntPtr.Size);
                if (activate != IntPtr.Zero) Marshal.Release(activate);
            }
            Marshal.FreeCoTaskMem(activates);
        }
        return count > 0;
    }
}
