namespace Vesper.Engine;

// Verified source timeline for a complete raw audio file. Unlike a demuxed
// track, this does not pretend to own contiguous elementary sample offsets.
internal sealed record RawAudioTimeline(string Extension, string Codec, int Channels, int SampleRate, double DurationSeconds)
{
    internal static bool IsRawFile(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() is ".mp3" or ".ogg" or ".oga" or ".opus";

    internal static RawAudioTimeline Read(Stream source, string fileName, CancellationToken ct) =>
        Path.GetExtension(fileName).Equals(".mp3", StringComparison.OrdinalIgnoreCase)
            ? Mp3MetadataWriter.ReadTimeline(source, ct)
            : OggAudioMetadataWriter.ReadTimeline(source, ct);
}
