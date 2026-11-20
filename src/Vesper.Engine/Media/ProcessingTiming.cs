using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Vesper.Engine;

public enum ProcessingPhase { Discovery, Probe, Transfer, DiskFlush, CopyQueue, EncoderQueue, Remux, Merge, Encode, Tags, Verify, Hash, Publish }

/// <summary>Opt-in local measurements. No names, addresses, headers or media content enter the trace.</summary>
public static class ProcessingTiming
{
    private static readonly bool Enabled = Environment.GetEnvironmentVariable("VESPER_PHASE_TIMINGS") == "1";
    private static readonly object Gate = new();
    private static FileStream? _file;
    private static bool _closed;

    [StructLayout(LayoutKind.Sequential)]
    internal struct IoCounts { public ulong Reads, Writes, Other, ReadBytes, WriteBytes, OtherBytes; }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessIoCounters(IntPtr process, out IoCounts counts);

    private static IoCounts Counters()
    {
        if (OperatingSystem.IsWindows() && GetProcessIoCounters(new IntPtr(-1), out var io)) return io;
        return default;
    }

    public readonly struct Scope : IDisposable
    {
        private readonly long _start, _bytes;
        private readonly Guid _job;
        private readonly ProcessingPhase _phase;
        private readonly IoCounts _io;
        internal Scope(Guid job, ProcessingPhase phase, long bytes)
        { _job = job; _phase = phase; _bytes = bytes; _io = Counters(); _start = Stopwatch.GetTimestamp(); }
        public void Dispose()
        {
            if (_start == 0) return;
            long end = Stopwatch.GetTimestamp();
            IoCounts io = Counters();
            Write(new { job = _job, phase = _phase.ToString(), startTicks = _start, endTicks = end,
                frequency = Stopwatch.Frequency, elapsedMs = Stopwatch.GetElapsedTime(_start, end).TotalMilliseconds,
                bytes = _bytes, processReadBytes = io.ReadBytes - _io.ReadBytes, processWriteBytes = io.WriteBytes - _io.WriteBytes });
        }
    }

    public static Scope Measure(Guid job, ProcessingPhase phase, long bytes = 0) => Enabled ? new(job, phase, bytes) : default;

    private static void Write(object row)
    {
        lock (Gate)
        {
            if (_closed) return;
            try
            {
                if (_file is null)
                {
                    string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VESPER", "Diagnostics");
                    Directory.CreateDirectory(folder);
                    _file = new FileStream(Path.Combine(folder, $"processing-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.jsonl"),
                        FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                }
                if (_file.Length >= 8 * 1024 * 1024) { _file.Dispose(); _closed = true; return; }
                JsonSerializer.Serialize(_file, row); _file.WriteByte(10); _file.Flush();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            { _closed = true; _file?.Dispose(); }
        }
    }
}
