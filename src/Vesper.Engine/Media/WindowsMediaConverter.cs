using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Vesper.Engine;

/// <summary>Streams through the Windows Media Foundation reader and MP4 writer. No codecs are shipped by VESPER.</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsMediaConverter
{
    private const uint AllStreams = 0xfffffffe, AnyStream = 0xfffffffe;
    private const uint EndOfStream = 2, StreamChanged = 0x30, StreamTick = 0x100;
    private static readonly Guid OriginName = new("fc358288-3cb6-460c-a424-b6681260375a");
    private static readonly Guid AdvancedVideoProcessing = new("0f81da2c-b537-4672-a8b2-a681b17307a3");
    private static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid Video = MediaGuid(0x73646976), Audio = MediaGuid(0x73647561);
    private static readonly Guid H264 = MediaGuid(0x34363248), Nv12 = MediaGuid(0x3231564e);
    private static readonly Guid I420 = MediaGuid(0x30323449), Yv12 = MediaGuid(0x32315659);
    private static readonly Guid Aac = MediaGuid(0x1610), Pcm = MediaGuid(1);
    private static readonly Guid FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid FrameRate = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    private static readonly Guid PixelAspect = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    private static readonly Guid AvgBitrate = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    private static readonly Guid InterlaceMode = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    private static readonly Guid InterlacedSample = new("b1d5830a-deb8-40e3-90fa-389943716461");
    private static readonly Guid TransferFunction = new("5fb0fce9-be5c-4935-a811-ec838f8eed93");
    private static readonly Guid VideoPrimaries = new("dbfbe4d7-0740-4ee0-8192-850ab0e21935");
    private static readonly Guid YuvMatrix = new("3e23d450-2c75-4d25-a00e-b91670d12327");
    private static readonly Guid NominalRange = new("c21b8ee5-b956-4071-8daf-325edf5cab11");
    private static readonly Guid Channels = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
    private static readonly Guid SampleRate = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
    private static readonly Guid BitsPerSample = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
    private static readonly Guid AvgBytesPerSecond = new("1aab75c8-cfef-451c-ab95-ac034b8e1731");

    private static Guid MediaGuid(int format) => new(format, 0, 0x0010, 0x80, 0, 0, 0xaa, 0, 0x38, 0x9b, 0x71);

    private readonly record struct TrackFormat(Guid Subtype, ulong FrameSize, uint SampleRate, uint Channels);
    private readonly record struct TrackWriter(uint Index, TrackFormat Format);

    public static void Convert(string source, string extension, string destination, MediaOutput output, CancellationToken ct,
        DemuxedTrack? timing = null)
    {
        ct.ThrowIfCancellationRequested();
        Check(CoInitializeEx(IntPtr.Zero, 0), "initializing COM");
        try
        {
            Check(MFStartup(0x20070, 1), "starting Media Foundation"); // MFSTARTUP_NOSOCKET
            try { Encode(source, extension, destination, output, timing, ct); }
            finally { MFShutdown(); }
        }
        finally { CoUninitialize(); }
    }

    private static void Encode(string source, string extension, string destination, MediaOutput output, DemuxedTrack? timing,
        CancellationToken ct)
    {
        IByteStream? input = null, target = null;
        IAttributes? settings = null;
        ISourceReader? reader = null;
        ISinkWriter? writer = null;
        try
        {
            Check(MFCreateFile(1, 0, 0, source, out input), "opening the source");
            if (input is not IAttributes attributes) throw new NotSupportedException("Windows could not identify the source container.");
            Check(attributes.SetString(OriginName, source + extension), "identifying the source container");
            if (output == MediaOutput.Mp4)
            {
                Check(MFCreateAttributes(out settings, 1), "creating decoder settings");
                Check(settings.SetUINT32(AdvancedVideoProcessing, 1), "preserving the source display size");
            }
            Check(MFCreateSourceReaderFromByteStream(input, settings, out reader), "opening a decoder for the source container");
            Check(MFCreateFile(3, 1, 0, destination, out target), "creating the MP4 output");
            Check(MFCreateSinkWriterFromURL(destination, target, IntPtr.Zero, out writer), "opening the MP4 writer");
            Check(reader.SetStreamSelection(AllStreams, false), "selecting the source tracks");

            var indexes = new Dictionary<uint, TrackWriter>();
            bool hasAudio = false, hasVideo = false;
            bool wantVideo = output == MediaOutput.Mp4;
            for (uint i = 0; reader.GetNativeMediaType(i, 0, out IMediaType type) >= 0; i++)
            {
                try
                {
                    Check(type.GetGUID(MajorType, out Guid major), "identifying a media track");
                    if (major == Video && wantVideo && !hasVideo)
                    {
                        indexes.Add(i, AddTrack(reader, writer, i, type, video: true, timing));
                        hasVideo = true;
                    }
                    if (major == Audio && !hasAudio)
                    {
                        indexes.Add(i, AddTrack(reader, writer, i, type, video: false));
                        hasAudio = true;
                    }
                }
                finally { Release(type); }
            }
            if (!hasAudio || hasVideo != wantVideo)
                throw new NotSupportedException("Windows could not open the selected audio and video tracks.");

            Check(writer.BeginWriting(), "starting conversion");
            while (indexes.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                int result = reader.ReadSample(AnyStream, 0, out uint index, out uint flags, out long time, out ISample? sample);
                try
                {
                    Check(result, "decoding a sample");
                    if ((flags & 1) != 0 || !indexes.TryGetValue(index, out TrackWriter stream))
                        throw new NotSupportedException("Windows could not decode a source track.");
                    if ((flags & StreamChanged) != 0)
                    {
                        TrackFormat current = ReadCurrentFormat(reader, index);
                        if (current != stream.Format)
                            throw new NotSupportedException($"The decoded track changed from {stream.Format} to {current}.");
                    }
                    if (sample is not null)
                    {
                        if (stream.Format.FrameSize != 0 && sample.GetUINT32(InterlacedSample, out uint interlaced) >= 0 && interlaced != 0)
                            throw new NotSupportedException("This interlaced source needs a different conversion path.");
                        Check(writer.WriteSample(stream.Index, sample), "encoding a sample");
                    }
                    else if ((flags & StreamTick) != 0) Check(writer.SendStreamTick(stream.Index, time), "preserving a timing gap");
                    if ((flags & EndOfStream) != 0)
                    {
                        Check(reader.SetStreamSelection(index, false), "closing a finished track");
                        indexes.Remove(index);
                    }
                }
                finally { Release(sample); }
            }
            ct.ThrowIfCancellationRequested();
            Check(writer.Finish(), "finalizing the MP4 output");
        }
        finally
        {
            Release(writer);
            Release(target);
            Release(reader);
            Release(input);
            Release(settings);
        }
    }

    private static TrackWriter AddTrack(ISourceReader reader, ISinkWriter writer, uint index, IMediaType native, bool video,
        DemuxedTrack? timing = null)
    {
        Check(reader.SetStreamSelection(index, true), "enabling a source track");
        Guid codec = video ? H264 : Aac;
        Check(native.GetGUID(Subtype, out Guid sourceCodec), "identifying the source codec");
        if (!video && sourceCodec == codec)
        {
            Check(writer.AddStream(native, out uint passthrough), "copying the compatible track");
            Check(writer.SetInputMediaType(passthrough, native, IntPtr.Zero), "connecting the compatible track");
            return new TrackWriter(passthrough, ReadFormat(native));
        }

        IMediaType? decoded = null;
        IMediaType? encoded = null;
        var request = CreateType(video ? Video : Audio, video ? Nv12 : Pcm);
        try
        {
            if (video && timing is { Width: > 0, Height: > 0 })
                Check(request.SetUINT64(FrameSize, (ulong)timing.Width << 32 | (uint)timing.Height), "setting the source display size");
            else if (!video) Check(request.SetUINT32(BitsPerSample, 16), "setting PCM bit depth");
            int selected = reader.SetCurrentMediaType(index, IntPtr.Zero, request);
            if (video && selected < 0)
            {
                foreach (Guid layout in new[] { I420, Yv12 })
                {
                    Check(request.SetGUID(Subtype, layout), "selecting a video pixel layout");
                    selected = reader.SetCurrentMediaType(index, IntPtr.Zero, request);
                    if (selected >= 0) break;
                }
            }
            Check(selected, "opening a decoder for a source track");
            Check(reader.GetCurrentMediaType(index, out decoded), "reading decoded track properties");
            encoded = CreateType(video ? Video : Audio, codec);
            if (video) ConfigureVideo(native, decoded, encoded, timing);
            else ConfigureAudio(decoded, encoded);
            Check(writer.AddStream(encoded, out uint stream), "configuring the encoder");
            Check(writer.SetInputMediaType(stream, decoded, IntPtr.Zero), "connecting decoder and encoder");
            return new TrackWriter(stream, ReadFormat(decoded));
        }
        finally
        {
            Release(encoded);
            Release(decoded);
            Release(request);
        }
    }

    private static TrackFormat ReadCurrentFormat(ISourceReader reader, uint index)
    {
        Check(reader.GetCurrentMediaType(index, out IMediaType type), "checking the current track properties");
        try { return ReadFormat(type); }
        finally { Release(type); }
    }

    private static TrackFormat ReadFormat(IMediaType type)
    {
        Check(type.GetGUID(Subtype, out Guid codec), "checking the track codec");
        type.GetUINT64(FrameSize, out ulong size);
        type.GetUINT32(SampleRate, out uint rate);
        type.GetUINT32(Channels, out uint channels);
        return new TrackFormat(codec, size, rate, channels);
    }

    private static void ConfigureVideo(IMediaType native, IMediaType decoded, IMediaType encoded, DemuxedTrack? timing)
    {
        Check(decoded.GetUINT64(FrameSize, out ulong size), "reading the video size");
        if (decoded.GetUINT64(FrameRate, out ulong rate) < 0 || (uint)rate == 0 || rate >> 32 == 0)
            if (native.GetUINT64(FrameRate, out rate) < 0 || (uint)rate == 0 || rate >> 32 == 0)
                rate = timing is { DurationSeconds: > 0 }
                    ? (ulong)Math.Clamp(Math.Round(timing.Samples.Count * 1_000d / timing.DurationSeconds), 1, uint.MaxValue) << 32 | 1_000
                    : 0;
        if ((uint)rate == 0 || rate >> 32 == 0)
            throw new NotSupportedException("Windows could not determine the frame rate.");
        if (native.GetUINT32(TransferFunction, out uint transfer) >= 0 && transfer is 15 or 16)
            throw new NotSupportedException("HDR conversion would change the source colors.");
        if (native.GetUINT32(InterlaceMode, out uint interlace) >= 0 && interlace is >= 3 and <= 6)
            throw new NotSupportedException("This interlaced source needs a different conversion path.");

        Check(encoded.SetUINT64(FrameSize, size), "setting the output video size");
        Check(encoded.SetUINT64(FrameRate, rate), "setting the output frame rate");
        Check(decoded.SetUINT64(FrameRate, rate), "setting the decoded frame rate");
        ulong aspect = decoded.GetUINT64(PixelAspect, out ulong value) >= 0 && value != 0 ? value : 0x100000001;
        Check(encoded.SetUINT64(PixelAspect, aspect), "preserving the pixel aspect ratio");
        Check(encoded.SetUINT32(InterlaceMode, 2), "setting progressive video output");
        double fps = (rate >> 32) / (double)(uint)rate;
        uint bitrate = (uint)Math.Clamp((size >> 32) * (uint)size * fps / 8, 500_000, 40_000_000);
        if (encoded.GetUINT32(AvgBitrate, out uint originalRate) < 0 || originalRate == 0)
            Check(encoded.SetUINT32(AvgBitrate, bitrate), "setting the video bitrate");
        foreach (Guid property in new[] { TransferFunction, VideoPrimaries, YuvMatrix, NominalRange })
        {
            if (decoded.GetUINT32(property, out uint color) >= 0 || native.GetUINT32(property, out color) >= 0)
                Check(encoded.SetUINT32(property, color), "preserving the video color information");
        }
    }

    private static void ConfigureAudio(IMediaType decoded, IMediaType encoded)
    {
        Check(decoded.GetUINT32(Channels, out uint channels), "reading the audio channels");
        Check(decoded.GetUINT32(SampleRate, out uint rate), "reading the audio sample rate");
        Check(encoded.SetUINT32(Channels, channels), "preserving the audio channels");
        Check(encoded.SetUINT32(SampleRate, rate), "preserving the audio sample rate");
        Check(encoded.SetUINT32(BitsPerSample, 16), "configuring the AAC encoder");
        Check(encoded.SetUINT32(AvgBytesPerSecond, channels == 1 ? 16_000u : 24_000u), "setting the AAC bitrate");
    }

    private static IMediaType CreateType(Guid major, Guid codec)
    {
        Check(MFCreateMediaType(out IMediaType type), "creating a media format");
        try
        {
            Check(type.SetGUID(MajorType, major), "setting the media type");
            Check(type.SetGUID(Subtype, codec), "setting the media codec");
            return type;
        }
        catch { Release(type); throw; }
    }

    private static void Check(int hresult, string operation)
    {
        if (hresult < 0) throw new COMException($"Windows Media Foundation failed while {operation} (0x{hresult:X8}).", hresult);
    }

    private static void Release(object? value)
    {
        if (value is not null) Marshal.ReleaseComObject(value);
    }

    [DllImport("ole32.dll", ExactSpelling = true)] private static extern int CoInitializeEx(IntPtr reserved, uint mode);
    [DllImport("ole32.dll", ExactSpelling = true)] private static extern void CoUninitialize();
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFShutdown();
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFCreateMediaType(out IMediaType type);
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFCreateAttributes(out IAttributes attributes, uint capacity);
    [DllImport("mfplat.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int MFCreateFile(int accessMode, int openMode, int flags, string path, out IByteStream stream);
    [DllImport("mfreadwrite.dll", ExactSpelling = true)]
    private static extern int MFCreateSourceReaderFromByteStream(IByteStream stream, IAttributes? attributes, out ISourceReader reader);
    [DllImport("mfreadwrite.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int MFCreateSinkWriterFromURL(string url, IByteStream stream, IntPtr attributes, out ISinkWriter writer);

    [ComImport, Guid("ad4c1b00-4bf7-422f-9175-756693d9130d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IByteStream { }

    [ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMediaType : IAttributes { }

    [ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISample : IAttributes { }

    // Unused methods only reserve their slots in the native COM vtables.
    [ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAttributes
    {
        void GetItem();
        void GetItemType();
        void CompareItem();
        void Compare();
        [PreserveSig] int GetUINT32(in Guid key, out uint value);
        [PreserveSig] int GetUINT64(in Guid key, out ulong value);
        void GetDouble();
        [PreserveSig] int GetGUID(in Guid key, out Guid value);
        void GetStringLength();
        void GetString();
        void GetAllocatedString();
        void GetBlobSize();
        void GetBlob();
        void GetAllocatedBlob();
        void GetUnknown();
        void SetItem();
        void DeleteItem();
        void DeleteAllItems();
        [PreserveSig] int SetUINT32(in Guid key, uint value);
        [PreserveSig] int SetUINT64(in Guid key, ulong value);
        void SetDouble();
        [PreserveSig] int SetGUID(in Guid key, in Guid value);
        [PreserveSig] int SetString(in Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
    }

    [ComImport, Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISourceReader
    {
        void GetStreamSelection();
        [PreserveSig] int SetStreamSelection(uint index, [MarshalAs(UnmanagedType.Bool)] bool selected);
        [PreserveSig] int GetNativeMediaType(uint index, uint typeIndex, out IMediaType type);
        [PreserveSig] int GetCurrentMediaType(uint index, out IMediaType type);
        [PreserveSig] int SetCurrentMediaType(uint index, IntPtr reserved, IMediaType type);
        void SetCurrentPosition();
        [PreserveSig] int ReadSample(uint index, uint flags, out uint actual, out uint resultFlags, out long timestamp, out ISample? sample);
    }

    [ComImport, Guid("3137f1cd-fe5e-4805-a5d8-fb477448cb3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISinkWriter
    {
        [PreserveSig] int AddStream(IMediaType type, out uint index);
        [PreserveSig] int SetInputMediaType(uint index, IMediaType type, IntPtr attributes);
        [PreserveSig] int BeginWriting();
        [PreserveSig] int WriteSample(uint index, ISample sample);
        [PreserveSig] int SendStreamTick(uint index, long timestamp);
        void PlaceMarker();
        void NotifyEndOfSegment();
        void Flush();
        [PreserveSig] int Finish();
    }
}
