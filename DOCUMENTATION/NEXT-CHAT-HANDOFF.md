# VESPER engineering handoff

Work in the primary checkout on `main`. Check local changes, HEAD, and the live GitHub tip before editing. Preserve local work. The owner authorizes normal fast-forward pushes of verified changes. Extra branches and worktrees require an explicit request.

## Start here

- [Master TODO](MASTER-TODO.md) and [status register](STATUS.tsv) define approved scope and outstanding work. Read the relevant rows. Run `scripts/Verify-EngineeringBacklog.ps1` after tracker changes.
- [Release gates](QUALITY-RELEASE-GATES.md) and the [Windows checklist](MANUAL_WINDOWS_TEST_CHECKLIST.md) define candidate acceptance. Q5 is the owner's optional accessibility deferral.
- [Owner design requirements](OWNER-DESIGN-REQUIRED.md) describes remaining architectural contracts. [The audit ledger](AUDIT-RECONCILIATION.md), [original source index](CLAUDE-AUDIT-SOURCE-INDEX.md), and [independent audit matrix](VSP-INDEPENDENT-RECONCILIATION.md) preserve traceability. Claim-by-claim reconciliation is still open, including unnumbered findings. Record inaccessible original sources explicitly.
- Use [media limits](../MEDIA_ENGINE_LIMITATIONS.md) for supported behavior. The [dated implementation log](IMPLEMENTATION-2026-11.md) and local `release/audit-evidence` are evidence to consult for the feature under work.
- Earlier demo notes, patch files, the resolver snapshot, old build reports, and their raw results are available in [Git history](https://github.com/DarkOrbitWeaver/VESPER/tree/ad9bd982a3cbc570fad2413bd7eff14575ccc127). Consult them when an audit or product decision needs the original context. Results apply to the builds named in those reports.

## Current work

Review and merge viable frontier patches in coherent batches. Codex owns focused regression checks, integration, the final Release suites, real Windows and Chrome validation, performance measurement, and packaging. Preserve the eight First Flight pictures and the fixes already on main for damaged inputs, HLS quality, YouTube retries, and desktop trim. The settings and scroll milestone simplified the popup, added About, and separated the grid's fixed header from its scrolling rows. The Windows desktop suite and extension suite passed. Confirm the packaged scroll behavior and reproduce the reported TikTok and Instagram picker failures in disposable Chrome with its registered helper. The owner's example TikTok video passed the native URL workflow; that result covers desktop only.

The tracker verifier is the source for the current item count. At a work checkpoint report the commit and checkout state, evidence, open tracker IDs and gates, blockers, and next concrete action. Use disposable Windows environments, Chrome profiles, accounts, and files. Leave the owner's regular profile, production installation, registration, downloads, and original audit files alone.

## Product contracts

- Cancel removes only the native job and its owned unfinished files. Pause preserves progress. Failure keeps an actionable entry. Chrome keeps bytes it already owns. Use native readiness and source identity before accepting a fresh handoff.
- Keep the existing temporary-file layout, recoverable persistence, output validation, per-origin consent, privacy, and no-overwrite publication. Keep C#/.NET 10, native WinForms, Chrome MV3, the custom download engine, and existing botanical/butterfly assets.
- The extension popup holds essential settings, Animate and Off for butterfly motion, and a small extension version. Video sign-in defaults on and uses the current supported video site's HTTPS session after Chrome grants access. File sign-in still requires exact-host approval. Action-specific feedback belongs with the action. The in-page picker handles media and the desktop manages downloads.
- Apply Humanify. Verify the actual behavior before closing a requirement. Use the exact candidate commit for release claims. The owner performs the final audit before public distribution.

## Reminder after the complete product ships

Bring these follow-ups back to the owner once VESPER has shipped with the approved scope complete:

- Improve conversion further, especially post-download speed, compatibility, memory, and disk use.
- Review and overhaul the transfer, recovery, media, resolver, and browser engines where real usage and measurements justify it.
- Run a fresh whole-product audit across engines, site compatibility, Windows UX, reliability, and performance.

These are additional post-release improvements. The existing approved implementation and release checks still apply to the first shipment.
