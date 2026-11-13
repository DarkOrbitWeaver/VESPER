# Synthetic media regression fixtures

These media files were generated for this change from FFmpeg's `testsrc2` and a generated 440 Hz sine wave.
They contain no captured user media, cookies, tokens or third-party footage. The newly generated fixture content is dedicated under CC0-1.0.
`SHA256.json` identifies the exact checked-in bytes. FFmpeg/ffprobe are **development validation tools only** and are not bundled or required to run these tests.

- `av-init.mp4`, `av-1.m4s` through `av-3.m4s`: 3-second source-muxed 640×360 H.264/AAC fMP4, 30 fps, one-second GOPs.
- `audio-init.mp4`, `audio-1.m4s`: 1-second AAC-only fragmented MP4.
- `ts-0.ts` through `ts-4.ts`: five actual H.264/AAC MPEG-TS HLS segments, replacing the old arbitrary pseudo-random `.ts` payloads.
- `video-only.mp4`: actual 128×72 H.264 file without audio; it must not be published as a media handoff with audio.
- `muxed.webm`, `video-only.webm`: VP9/Opus and VP9-only files for bounded EBML audio-presence checks.
- `quiet-track.mp3`: 0.444-second generated silent MP3 for testing ID3v2 metadata without changing MPEG audio frames.

Generation used an isolated FFmpeg 7.0.2 static tool, with `-movflags empty_moov+default_base_moof+frag_keyframe` for the fragmented MP4 sources.
The MP4 sources were split at top-level `ftyp`/`moov` and consecutive `moof`/`mdat` boundaries; the optional `mfra` trailer was omitted.
The assembled MP4 fixtures were independently probed and fully decoded before use in engine tests. WebM/TS source generation used the same synthetic signals.
`quiet-track.mp3` was generated separately with FFmpeg 8.1.1 and libmp3lame as a short silent MPEG Layer III source for tag-copy tests.

Representative generation arguments (supply an explicitly user-installed development FFmpeg if regenerating):

- Video input: `-f lavfi -i testsrc2=size=640x360:rate=30`
- Audio input: `-f lavfi -i sine=frequency=440:sample_rate=48000`
- Video encoding: `-c:v libx264 -preset veryfast -crf 22 -pix_fmt yuv420p -g 30 -keyint_min 30 -sc_threshold 0`
- Audio encoding: `-c:a aac -b:a 64k`
- HLS: `-bf 0 -t 5 -f hls -hls_time 1 -hls_playlist_type vod -hls_segment_filename ts-%d.ts`
- WebM: 128×72/5 fps, `-c:v libvpx-vp9 -threads 1 -c:a libopus -t 1`; add `-an` for the video-only case.

These fixtures validate supported structures and exact bytes. They do not establish live website compatibility or universal codec support.
