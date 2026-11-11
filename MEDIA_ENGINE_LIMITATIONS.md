# Remaining limitations and security notes

This is a tested, incremental update to the specified VESPER baseline, **not completion of yt-dlp parity**.

## Deliberately unavailable media operations

- **No general audio/video muxer was implemented.** Source-muxed fMP4 is validated and retained; two independent streams are not joined.
  Correct muxing requires reconciled movie/track IDs and defaults, sample-description indices, moof/trun offsets, timeline/timescale and edit-list handling,
  and correct audio/video presentation. Concatenating independently initialized files is not a safe substitute. No FFmpeg is bundled or invoked by VESPER.
- Clear fMP4 subset only: one audio track, one video track, or a source-provided pair; moof-relative offsets, explicit trun offsets and contiguous tfdt timelines.
  Absolute offsets, edit lists in fragmented initialization, track changes, unsupported sample entries and encrypted fragments stop with an explanation.
- DASH is single-period VOD. Multi-period, WebM fragment assembly and unsupported initialization/container layouts remain unavailable.
  SegmentBase index expansion is not added. Unsupported source representations may be discovered by the library but are not selectable in the native handoff.
- HLS supports completed VOD TS/fMP4, relative URLs, initialization maps, exact byte ranges and identity AES-128 CBC.
  Discontinuities, gaps, I-frame-only media, session keys, raw packed AAC renditions, changed maps and required remux operations are refused.
  MPEG-TS support is single-program, clear, recognized audio/video streams with CRC-checked PAT/PMT and PES structure. It is not a full codec decoder.
- HTTP MP4/WebM checks confirm bounded container layout and declared audio presence, not that every compressed sample decodes.
  Existing byte coverage, content/range checks and trusted hash verification remain authoritative for ordinary HTTP transfers.

## Access and websites

- Live YouTube resolution did not succeed in this environment. The player/signature/n/SABR/access-context limitations are real; metadata/artwork is not proof of playback access.
  The code does not impersonate privileged clients, manufacture verification tokens, bypass account restrictions or execute downloaded player scripts.
- The extension can provide validated observed candidates. Desktop transfer still refuses credentials, Origin and Accept-Language contexts that the current handoff cannot safely reproduce.
  Cookie-dependent TikTok and other signed-in workflows are therefore not advertised as completed support.
- Existing YouTube collection handling remains first-page/snapshot only; no channel crawler or continuation implementation was added.
  The native selected-item batch is bounded to 200 and resolves up to four selected entries concurrently. No automatic whole-playlist download is introduced.
- See the compatibility matrix for every verified operation and the sites that remain untested/unsupported.

## Recovery and data

- HTTP source refresh is **explicit and on demand**, from the existing Rescue dialog. Provider, media ID and format ID must match, and existing Rescue byte-identity checks still decide whether append is safe.
  An identity match without byte proof never authorizes mixing partial files. Older jobs without format identity must use ordinary trusted-identity Rescue.
- HLS resume requires unchanged resolved manifest identity plus the committed prefix SHA-256. DASH uses a versioned immutable plan fingerprint and committed prefix SHA-256.
  A prefix is re-read on resume; this is intentional integrity I/O, not spot-check proof. Hashes protect local state consistency; they are not an external content-authenticity guarantee.
- Signed **fragment-plan re-resolution is not implemented**. Expired fragment URLs stop and preserve data. Changed plans, legacy cursors without identity, mismatched media and incompatible timelines require a separate restart/copy.
- SQLite migration 3 appends nullable plan/format columns; existing rows/settings/history stay intact. Migration backups and release artifacts are no-overwrite.
  Downgrading to an older engine is not supported on a newer schema; restore an appropriate backup rather than editing the schema number.
- On Windows, source URLs, previous URLs, referrer, original page, thumbnail URL, plans and cursors are DPAPI-protected for the current Windows user.
  Legacy plaintext remains readable, and old backups may still contain it. This is not retroactive secure deletion of old database pages/backups.
  A DPAPI database copied to another Windows account/machine may not decrypt; restore using the original account/key backup. Decryption failure does not trigger database quarantine/replacement.
- Headless **non-Windows developer tooling has no DPAPI and stores plaintext**. Do not use it with sensitive signed-in sources.
  Cookies, authorization headers and AES key material are not added to persisted media plans. AES keys are bounded in memory and cleared after use.

## Performance and validation boundaries

- HLS still buffers one bounded segment (64 MiB maximum; CBC decryption can require an additional buffer). DASH buffers one bounded object; initialization is capped at 8 MiB.
  Streaming decryption, pipelined parallel fragment fetch and universal low-allocation processing are not claimed.
- Short cache lifetime, connection reuse, bounded HTTP body concurrency and four-way selected-batch discovery are implemented. General cross-caller in-flight HTTP deduplication is not added.
- Browser candidates and ordinary metadata have different cache/security contexts. Successful metadata caching has a signed-URL expiry margin and excludes credential-bearing nested results.
- Full Windows UI execution, live extension/native messaging registration and Windows DPAPI integration could not run on Linux. Two DPAPI tests are explicitly skipped here and run automatically on Windows.
- PowerShell release/packaging scripts and the added Windows CI workflow were not executed in this Linux sandbox. `Package-Source.ps1` uses PowerShell 7/.NET APIs.
- No comparable current yt-dlp executable benchmark was run. There is no basis for claiming VESPER is faster than yt-dlp.
- Peak working-set measurements are process-lifetime peaks, not isolated per-resolve memory peaks. Thumbnail timings measure bytes available, not WinForms pixels rendered.
