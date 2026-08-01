# Windows development setup

## Prerequisites

- Windows 10 or later
- Git for Windows
- Visual Studio 2022 Community with the **.NET desktop development** workload
- .NET SDK 9.0.316 (pinned by `global.json`)
- MusicBee installed for the later plugin-load spike

The Visual Studio workload supplies MSBuild and the .NET Framework 4.8
targeting pack required by the legacy MusicBee host projects. The .NET SDK
builds the portable projects and runs tests.

Verify the setup:

```powershell
dotnet --info
& "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" `
  -latest -products * -requires Microsoft.VisualStudio.Workload.ManagedDesktop `
  -property installationPath
```

## GitHub SSH

This machine's key is registered with GitHub, but Git for Windows may select
its bundled SSH client instead of the key loaded into Windows OpenSSH. Configure
Git once:

```powershell
git config --global core.sshCommand C:/Windows/System32/OpenSSH/ssh.exe
ssh -T git@github.com
git ls-remote origin
```

GitHub reports a successful authentication followed by “does not provide shell
access”; that message is expected.

## Restore, build, test, and format

From the repository root:

```powershell
dotnet restore Shmembee.sln
dotnet build Shmembee.sln -c Release
dotnet test Shmembee.sln -c Release --no-build
dotnet format Shmembee.sln --verify-no-changes --no-restore
```

Build properties are centralized in `Directory.Build.props`, and NuGet package
versions are centralized in `Directory.Packages.props`.

## MusicBee compatibility boundary

The installed MusicBee executable is normally under:

```text
C:\Program Files (x86)\MusicBee\MusicBee.exe
```

Do not copy the current placeholder DLL into MusicBee yet. The next proof gate
must add the official MusicBee API interface, implement its lifecycle contract,
deploy to a configurable plugin directory, and test startup and shutdown using
a disposable setup. MusicBee plugin behavior cannot be established by a clean
build alone.
