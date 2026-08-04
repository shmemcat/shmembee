# Cross-repository contract fixtures

This directory holds versioned, platform-neutral fixtures consumed by both
Shmemplaylist (Kotlin) and Shmembee (C#). `manifest.json` is the fixture-set
index. Category manifests reference exact byte files; paths are relative to
this directory so consumers do not depend on the host filesystem layout.

- `parser/`: encoded M3U inputs and expected source records.
- `normalization/`: path normalization and comparison cases.
- `writer/`: deterministic writer or source-preserving editor cases.
- `checksums/`: semantic checksum vectors and existence state.
- `operations/`: add, remove, stale-state, rollback, recovery, and undo cases.
- `gonemad/`: redacted, reproducible GoneMAD-generated and rewritten examples.

Phase 2 fixtures cover `m3u-parser-v1`, `phone-path-v1`,
`semantic-checksum-v1`, `m3u-writer-v1`,
`canonical-gonemad-profile-v1`, and `playlist-operations-v1`.
