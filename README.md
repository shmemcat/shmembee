# Shmembee

Shmembee is a vibe-coded Windows-first MusicBee plugin for safe, bidirectional playlist
reconciliation between MusicBee and GoneMAD M3U/M3U8 playlists.

The repository is currently at its contract-spike stage. It contains the
project boundaries and a minimal `MB_Shmembee.dll` lifecycle plugin, but it
does not yet modify MusicBee playlists or phone files.

## Architecture

- `Shmembee.Core`: track identity, ordered diff, merge, and path rules.
- `Shmembee.Application`: synchronization use cases and external ports.
- `Shmembee.Infrastructure`: persistence, M3U, staging, backups, and logging.
- `Shmembee.Windows`: Windows phone transport and platform adapters.
- `Shmembee.MusicBee`: x86 MusicBee plugin host and UI adapter.
- `tests`: automated contract and unit tests.

Dependencies point inward: host and infrastructure projects depend on the
application boundary, which depends on the core.

## Build

Install the prerequisites in [docs/development-setup.md](docs/development-setup.md),
then run:

```powershell
dotnet restore Shmembee.sln
dotnet build Shmembee.sln -c Release
dotnet test Shmembee.sln -c Release --no-build
dotnet format Shmembee.sln --verify-no-changes --no-restore
```

The plugin placeholder is emitted as:

```text
src/Shmembee.MusicBee/bin/Release/net48/MB_Shmembee.dll
```

## Compatibility status

MusicBee documents managed plugins as .NET Framework, 32-bit assemblies named
`MB_*.dll`. The host project therefore targets .NET Framework 4.8 and x86.
The official API interface is vendored in the host project. Successful
compilation is not proof that MusicBee can load the plugin; follow
[the development setup](docs/development-setup.md) to deploy it and validate a
minimal startup/shutdown cycle.

The MusicBee host and missing-track repair proofs have passed, as has the
[GoneMAD playlist contract](docs/gonemad-contract.md). Current development is
building the durable state and read-only reconciliation layers on those proven
boundaries.

## Read-only reconciliation

Phase 2 provides the non-mutating engine used before any synchronization is
approved:

- Immutable ordered snapshots retain duplicate occurrences independently.
- M3U/M3U8 parsing accepts GoneMAD relative and absolute Android paths.
- Track resolution ranks approved mappings, canonical URLs, known phone paths,
  unique suffixes, filenames, and strong metadata.
- Ambiguous and unmatched tracks block reconciliation instead of being guessed.
- Three-way reconciliation automatically proposes unchanged, one-sided, and
  identical concurrent results; different concurrent edits require review.
- SQLite stores versioned snapshots, stable identities, aliases, and operation
  history.

The current engine computes proposals only. It does not write approved results
to MusicBee or the phone.

## Transactional synchronization

Phase 3 adds an explicit apply boundary for reviewed proposals:

- Both inputs are re-read and checksum-checked before mutation.
- MusicBee writes use only canonical indexed URLs and `Playlist_SetFiles`.
- Phone M3Us are deterministic UTF-8, LF-delimited files with ordered duplicate
  occurrences preserved.
- The previous phone file and MusicBee sequence are retained for rollback.
- Both sides are re-read and verified before an accepted baseline is committed.
- Failed or cancelled operations restore both sides and do not advance the
  baseline.
- SQLite records started, completed, and failed operations plus the latest
  accepted ordered baseline.

The Windows transport currently operates on a staged playlist directory. The
existing MTP capture/deployment scripts remain the proven device-transfer
boundary while native WPD transfer is implemented behind the same interface.

## License

No license has been selected yet. All rights are reserved until one is added.
