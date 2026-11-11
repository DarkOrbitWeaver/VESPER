# Fixture provenance

Every file in this folder is a **synthetic reconstruction** written by hand to match the documented or commonly
observed structure of the corresponding source. None of them is captured network traffic and none contains
copyrighted media, cookies or tokens. Host names end in `.example` or use `example.org/.com`.

Consequences:

* Passing fixture tests prove the parsing logic and the typed behaviour (errors, selection, security) — **not** that the
  live site still serves this shape. Live behaviour is checked separately by `LiveTests` (opt-in, `VESPER_LIVE_TESTS=1`).
* When a live check finds drift, update the fixture to the newly observed shape *and* record the regression in
  `COMPATIBILITY.md` (or a `CompatibilityRecord`).
* Expiry parameters use `4000000000` (year 2096) so fixtures do not rot.
