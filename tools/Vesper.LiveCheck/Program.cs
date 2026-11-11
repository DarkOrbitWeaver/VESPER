using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Vesper.Engine;
using Vesper.MediaResolver.Core;
using Vesper.MediaResolver.Formats;
using Vesper.MediaResolver.Models;
using VESPER;

// On-demand live compatibility runner. Drives the same path as the desktop app:
//   DefaultMediaResolver -> MediaTransferBridge.Prepare (linked source) -> DownloadEngine job -> VESPER validation
// and, only when --ffmpeg-dir is given, an independent ffprobe/ffmpeg check (development tool, never bundled).
//
// Usage: dotnet run --project tools/Vesper.LiveCheck -- <corpus.tsv> <outDir> [--ffmpeg-dir DIR] [--timeout 900] [--only TEXT]
// corpus.tsv: platform<TAB>scenario<TAB>mode<TAB>url   ('#' comments). Modes: discover, collection, best, balanced,
// smallest, audio, audio:<lang>, h<height> (exact height, e.g. h1080, h2160).

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: Vesper.LiveCheck <corpus.tsv> <outDir> [--ffmpeg-dir DIR] [--timeout SECONDS] [--only TEXT]");
    return 2;
}
string corpus = args[0], outDir = Path.GetFullPath(args[1]);
string? ffmpegDir = Arg("--ffmpeg-dir");
int timeoutSeconds = int.TryParse(Arg("--timeout"), out int t) ? t : 900;
string? only = Arg("--only");
Directory.CreateDirectory(outDir);
string downloads = Path.Combine(outDir, "downloads"), data = Path.Combine(outDir, "engine-data");
Directory.CreateDirectory(downloads);

var rows = new List<Dictionary<string, object?>>();
using var engine = DownloadEngine.Start(new EngineOptions { DataDirectory = data, DefaultFolder = downloads });
foreach (string line in File.ReadLines(corpus))
{
    if (line.Length == 0 || line.StartsWith('#')) continue;
    string[] f = line.Split('\t');
    if (f.Length < 4) continue;
    if (only is not null && !line.Contains(only, StringComparison.OrdinalIgnoreCase)) continue;
    var row = await RunAsync(f[0], f[1], f[2], f[3].Trim());
    rows.Add(row);
    string json = JsonSerializer.Serialize(row);
    Console.WriteLine(json);
    File.AppendAllText(Path.Combine(outDir, "results.jsonl"), json + Environment.NewLine);
}
File.WriteAllText(Path.Combine(outDir, "summary.md"), Summary(rows));
return 0;

async Task<Dictionary<string, object?>> RunAsync(string platform, string scenario, string mode, string url)
{
    var row = new Dictionary<string, object?>
    {
        ["platform"] = platform, ["scenario"] = scenario, ["mode"] = mode, ["url"] = url,
        ["date"] = DateTime.UtcNow.ToString("yyyy-MM-dd"), ["discovery"] = "fail", ["selection"] = "n/a", ["download"] = "n/a",
        ["audio"] = "n/a", ["vesperValidation"] = "n/a", ["independentValidation"] = "n/a"
    };
    DiscoveryMeasurement? metrics = null;
    using var resolver = new DefaultMediaResolver(new ResolverOptions { Measurement = m => metrics = m });
    var sw = Stopwatch.StartNew();
    MediaDiscoveryResult result;
    try
    {
        result = await resolver.ResolveAsync(new MediaDiscoveryRequest
        {
            Url = new Uri(url),
            Options = new() { CollectionIntent = mode == "collection" ? CollectionIntent.CollectionOnly : CollectionIntent.ItemOnly }
        }, CancellationToken.None);
    }
    catch (Exception ex)
    {
        row["failure"] = $"resolver exception {ex.GetType().Name}: {ex.Message}";
        return row;
    }
    row["discoveryMs"] = sw.ElapsedMilliseconds;
    row["requests"] = metrics?.NetworkRequests;
    if (result.Collection is { } c)
    {
        row["discovery"] = "pass";
        row["entries"] = c.Entries.Count;
        row["complete"] = c.IsComplete;
        row["dynamic"] = c.IsDynamic;
        row["notes"] = string.Join(" | ", result.Warnings.Select(w => w.Diagnostic));
        return row;
    }
    if (result.Item is not { } item)
    {
        row["failure"] = $"{result.Error?.Code}: {result.Error?.Diagnostic}";
        return row;
    }
    row["discovery"] = "pass";
    row["title"] = item.Title;
    row["formats"] = item.Representations.Count;
    row["maxHeight"] = item.Representations.Max(r => r.Height ?? 0);
    row["hdr"] = item.Representations.Any(r => r.Hdr != HdrKind.Sdr);
    row["audioTracks"] = item.AudioTracks.Count;
    row["subtitles"] = item.Subtitles.Count;
    row["strategy"] = result.Notes.FirstOrDefault();
    if (mode == "discover") return row;

    FormatPreferences prefs = mode switch
    {
        "best" => new FormatPreferences { Mode = QualityMode.Best },
        "balanced" => new FormatPreferences { Mode = QualityMode.Balanced },
        "smallest" => new FormatPreferences { Mode = QualityMode.Smallest },
        "audio" => new FormatPreferences { Mode = QualityMode.AudioOnly },
        _ when mode.StartsWith("audio:") => new FormatPreferences { Mode = QualityMode.AudioOnly, AudioLanguage = mode[6..] },
        _ when mode.StartsWith('h') && int.TryParse(mode[1..], out int h) => new FormatPreferences { Mode = QualityMode.Custom, TargetHeight = h, Fallback = FallbackPolicy.Fail },
        _ => new FormatPreferences()
    };
    var (choice, error) = MediaTransferBridge.Prepare(item, prefs);
    if (choice is null)
    {
        row["selection"] = "fail";
        row["failure"] = error;
        return row;
    }
    row["selection"] = "pass";
    row["choice"] = $"{choice.Kind} {choice.FormatId} {choice.Quality}";
    sw.Restart();
    AddResult added = engine.Add(new AddRequest(choice.Url, downloads, choice.FileName, true, choice.Referer, choice.UserAgent,
        Kind: choice.Kind, OriginalPageUrl: choice.OriginalPageUrl, MediaProvider: choice.Provider, MediaId: choice.MediaId,
        MediaQuality: choice.Quality, MediaExpiresAt: choice.ExpiresAt, MediaPlanJson: choice.MediaPlanJson, MediaFormatId: choice.FormatId));
    if (!added.Ok)
    {
        row["download"] = "fail";
        row["failure"] = "engine refused job: " + added.Message;
        return row;
    }
    JobSnapshot? snap = null;
    while (sw.Elapsed.TotalSeconds < timeoutSeconds)
    {
        snap = engine.Snapshot(added.JobId);
        if (snap?.State is JobState.Completed or JobState.Failed or JobState.NeedsAttention or JobState.NeedsLink or JobState.Suspect or JobState.Cancelled) break;
        await Task.Delay(500);
    }
    row["downloadSeconds"] = Math.Round(sw.Elapsed.TotalSeconds, 1);
    if (snap is not { State: JobState.Completed, FinalPath: { } path })
    {
        row["download"] = "fail";
        row["failure"] = $"job {snap?.State} {snap?.Reason}: {snap?.Message}";
        return row;
    }
    row["download"] = "pass";
    row["file"] = Path.GetFileName(path);
    row["bytes"] = new FileInfo(path).Length;
    row["mbPerSecond"] = Math.Round(new FileInfo(path).Length / 1048576.0 / Math.Max(0.1, sw.Elapsed.TotalSeconds), 1);
    try
    {
        using var fs = File.OpenRead(path);
        var tracks = MediaMerger.Demux(fs, fs.Length, CancellationToken.None);
        row["tracks"] = string.Join(", ", tracks.Select(x => $"{x.Kind.ToString().ToLowerInvariant()}:{x.Codec}{(x.Kind == TrackKind.Video ? $" {x.Width}x{x.Height}" : "")} {x.DurationSeconds:0.0}s"));
        bool audio = tracks.Any(x => x.Kind == TrackKind.Audio);
        row["audio"] = audio ? "verified" : "MISSING";
        row["vesperValidation"] = "pass";
    }
    catch (Exception ex) { row["vesperValidation"] = "fail: " + ex.Message; }
    if (ffmpegDir is not null) row["independentValidation"] = Independent(path);
    return row;
}

string Independent(string path)
{
    var (code, probe, _) = Exec(Path.Combine(ffmpegDir!, "ffprobe"), $"-v error -show_entries stream=codec_type,codec_name,width,height:format=format_name,duration -of compact \"{path}\"", 120);
    if (code != 0) return "ffprobe failed";
    long size = new FileInfo(path).Length;
    // Full decode for files up to 400 MB; larger files: first 90 s and last 60 s plus container duration check.
    string decodeArgs = size <= 400L << 20 ? $"-v error -xerror -i \"{path}\" -map 0 -f null -" : $"-v error -xerror -t 90 -i \"{path}\" -map 0 -f null -";
    var (dcode, _, derr) = Exec(Path.Combine(ffmpegDir!, "ffmpeg"), decodeArgs, 1800);
    string tail = "";
    if (size > 400L << 20)
    {
        var (tcode, _, terr) = Exec(Path.Combine(ffmpegDir!, "ffmpeg"), $"-v error -xerror -sseof -60 -i \"{path}\" -map 0 -f null -", 900);
        tail = tcode == 0 && terr.Trim().Length == 0 ? "; tail decode ok" : "; tail decode FAILED";
    }
    string streams = string.Join(" ", probe.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Replace("stream|", "").Replace("format|", "")));
    return (dcode == 0 && derr.Trim().Length == 0 ? (size <= 400L << 20 ? "full decode ok" : "head decode ok") : "decode FAILED: " + derr.Trim().Split('\n')[0]) + tail + " | " + streams;
}

static (int Code, string Out, string Err) Exec(string file, string arguments, int seconds)
{
    var psi = new ProcessStartInfo(file, arguments) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    using var p = Process.Start(psi)!;
    var o = p.StandardOutput.ReadToEndAsync();
    var e = p.StandardError.ReadToEndAsync();
    if (!p.WaitForExit(seconds * 1000)) { p.Kill(true); return (-1, "", "timeout"); }
    return (p.ExitCode, o.Result, e.Result);
}

string? Arg(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static string Summary(List<Dictionary<string, object?>> rows)
{
    var sb = new StringBuilder("| Platform | Scenario | Mode | Discovery | Selection | Download | Audio | VESPER validation | Independent validation | Date | Details |\n|---|---|---|---|---|---|---|---|---|---|---|\n");
    foreach (var r in rows)
    {
        string details = r.TryGetValue("failure", out var f) && f is not null ? $"FAIL: {f}"
            : r.TryGetValue("tracks", out var tr) && tr is not null ? $"{r["choice"]}; {tr}; {r.GetValueOrDefault("bytes")} B in {r.GetValueOrDefault("downloadSeconds")} s"
            : r.TryGetValue("entries", out var en) && en is not null ? $"{en} entries, complete={r["complete"]}, dynamic={r["dynamic"]}"
            : $"{r.GetValueOrDefault("formats")} formats, max {r.GetValueOrDefault("maxHeight")}p, HDR={r.GetValueOrDefault("hdr")}, audio tracks={r.GetValueOrDefault("audioTracks")}, subs={r.GetValueOrDefault("subtitles")}";
        sb.Append($"| {r["platform"]} | {r["scenario"]} | {r["mode"]} | {r["discovery"]} | {r["selection"]} | {r["download"]} | {r["audio"]} | {r["vesperValidation"]} | {Esc(r["independentValidation"])} | {r["date"]} | {Esc(details)} |\n");
    }
    return sb.ToString();
}

static string Esc(object? v) => (v?.ToString() ?? "").Replace("|", "/").Replace("\n", " ");
