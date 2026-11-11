#!/usr/bin/env bash
# On-demand live run: VESPER corpus with independent validation, then VESPER vs yt-dlp on identical formats.
# Requires network. Dev tools (ffmpeg/ffprobe, yt-dlp) are supplied via TOOLS and are never committed or bundled.
set -u
TOOLS=${TOOLS:-/home/user/tools}; OUT=${OUT:-/tmp/vesper-live}; LC="dotnet tools/Vesper.LiveCheck/bin/Release/net10.0/Vesper.LiveCheck.dll"
rm -rf "$OUT"; mkdir -p "$OUT"
$LC tests/live/corpus.tsv "$OUT/corpus" --ffmpeg-dir "$TOOLS" --timeout 900 > "$OUT/corpus.log" 2>&1
grep -v '^#' tests/live/corpus.tsv | grep -E "jNQXAC9IVRw	|shorts/" | head -2 > "$OUT/bench.tsv"
grep -E "h1080" tests/live/corpus.tsv >> "$OUT/bench.tsv"
: > "$OUT/bench.txt"
while IFS=$'\t' read -r platform scenario mode url; do
  key=$(echo "$url" | md5sum | cut -c1-8); runs=3; [ "$mode" = "h1080" ] && runs=1
  for i in $(seq 1 $runs); do
    printf '%s\t%s\t%s\n' "$platform" "$scenario" "$mode	$url" > "$OUT/one.tsv"
    r=$(python3 scripts/measure.py $LC "$OUT/one.tsv" "$OUT/bench-vesper-$key-$i" --timeout 900)
    fmt=$(jq -r '.choice' "$OUT/bench-vesper-$key-$i/results.jsonl" | awk '{print $2}' | sed 's/yt-//g; s/-[a-z][a-z.0-9-]*//g')
    echo "vesper	$mode	$url	run$i	$fmt	$r	$(jq -r '.download+" "+(.bytes|tostring)' "$OUT/bench-vesper-$key-$i/results.jsonl")" >> "$OUT/bench.txt"
    y=$(python3 scripts/measure.py python3 "$TOOLS/yt-dlp" --no-config --no-playlist -f "$fmt" --ffmpeg-location "$TOOLS" -o "$OUT/bench-ytdlp-$key-$i/%(id)s.%(ext)s" "$url")
    yf=$(ls "$OUT/bench-ytdlp-$key-$i"/* 2>/dev/null | head -1)
    echo "yt-dlp	$mode	$url	run$i	$fmt	$y	$( [ -n "$yf" ] && stat -c %s "$yf")" >> "$OUT/bench.txt"
  done
done < "$OUT/bench.tsv"
echo DONE > "$OUT/done"
