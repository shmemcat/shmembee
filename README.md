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

## License

No license has been selected yet. All rights are reserved until one is added.
