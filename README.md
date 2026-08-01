# Shmembee

Shmembee is a vibe-coded Windows-first MusicBee plugin for safe, bidirectional playlist
reconciliation between MusicBee and GoneMAD M3U/M3U8 playlists.

The repository is currently at its foundation stage. It contains the project
boundaries and a buildable `MB_Shmembee.dll` placeholder, but it does not yet
read or modify MusicBee playlists or phone files.

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
Successful compilation is not proof that MusicBee can load the plugin. The next
product gate will vendor the official API interface and validate a minimal
startup/shutdown cycle in MusicBee.

## License

No license has been selected yet. All rights are reserved until one is added.
