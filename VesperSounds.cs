using System.Media;

namespace VESPER;

internal static class VesperSounds
{
    private static readonly Lazy<SoundPlayer> Completion = new(() => new SoundPlayer(CompletionWave()));

    internal static void PlayCompletion()
    {
        try { Completion.Value.Play(); }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException) { /* Visual feedback remains available. */ }
    }

    internal static void Stop()
    {
        if (Completion.IsValueCreated) Completion.Value.Stop();
    }

    private static MemoryStream CompletionWave()
    {
        const int rate = 24000;
        const int samples = rate * 3 / 10;
        const double peak = 1036; // 16-bit PCM at -30 dBFS
        var wave = new MemoryStream(44 + samples * 2);
        using (var writer = new BinaryWriter(wave, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            writer.Write("RIFF"u8);
            writer.Write(36 + samples * 2);
            writer.Write("WAVEfmt "u8);
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(rate);
            writer.Write(rate * 2);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write("data"u8);
            writer.Write(samples * 2);
            for (int i = 0; i < samples; i++)
            {
                double time = (double)i / rate;
                double envelope = Math.Min(1, time / .04) * Math.Pow(1 - (double)i / (samples - 1), 2);
                double tone = .72 * Math.Sin(Math.Tau * 440 * time) + .28 * Math.Sin(Math.Tau * 660 * time);
                writer.Write((short)Math.Round(peak * envelope * tone));
            }
        }
        wave.Position = 0;
        return wave;
    }
}
