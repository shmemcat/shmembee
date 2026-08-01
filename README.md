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

## License

No license has been selected yet. All rights are reserved until one is added.
