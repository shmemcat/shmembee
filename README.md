# Shmembee

Shmembee is a vibe-coded Windows-first MusicBee plugin for safe, bidirectional playlist
reconciliation between MusicBee and GoneMAD M3U/M3U8 playlists.

The repository contains the core reconciliation and transactional apply
boundaries plus a MusicBee-hosted playlist catalog and reviewed diff workflow.

## Architecture

- `Shmembee.Core`: track identity, multiset membership diff, ordering, merge,
  and path rules.
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

The MusicBee host validation has passed, as has the
[GoneMAD playlist contract](docs/gonemad-contract.md). Current development is
building the durable state and read-only reconciliation layers on those
validated boundaries.

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

The general engine computes reviewed proposals; the MusicBee host exposes
playlist pairing, per-track membership choices, order selection, and batch
application.

## Snapshot review and local export

Keep the PC library and phone files unchanged by other applications while
reviewing in Shmembee. Opening the main window or explicitly choosing Refresh
reads the phone playlists and media paths once. Reviews and Apply use that
snapshot; the phone can be disconnected after loading. Refresh requires reviews
to be confirmed again, including reviews restored from a previous session.

- Phone access is read-only and confined to loading the snapshot. There is no
  automatic phone refresh after Apply.
- Original phone playlist bytes are saved locally under
  `<backup root>/Backups/Mobile Playlist Backups/<timestamp>` during loading.
- MusicBee playlists are backed up under
  `<backup root>/MusicBee Playlists/<date>/<time>` before applying a batch.
- Apply uses the reviewed track matching and ordering, updates MusicBee through
  `Playlist_SetFiles`, and verifies the local results. Unchanged MusicBee contents
  are not rewritten.
- Mobile M3Us are generated under `<mobile export root>/<timestamp>` as UTF-8,
  LF-delimited files. Order, duplicates, and Android paths are preserved.
- Copy those M3Us to the phone manually. `TRANSFER.txt` lists any phone playlists
  to delete manually; an absent export file does not delete a phone playlist.
- A failed or cancelled playlist restores its MusicBee contents and removes its
  incomplete export. Earlier successful changes and exports remain available.
  A rollback failure stops the batch and directs the user to the local backup.
- Successfully processed rows are disabled until the next Refresh, preventing
  accidental reapplication against the old phone snapshot. Other reviewed rows
  can be applied in another batch while offline.
- Local exports are recorded as `exported`, not as an accepted phone baseline.
  A later explicit Refresh accepts the baseline only when the observed phone and
  MusicBee checksums both match the pending export. Pending exports survive restart.

The apply log records per-playlist and total elapsed time. Controller integration
tests simulate phone disconnection, partial failure, cancellation, and manual copy;
they run as part of the solution tests without accessing a real device or MusicBee.

## License

All rights reserved.
