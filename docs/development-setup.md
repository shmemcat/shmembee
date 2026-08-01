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

Run the complete local check with:

```powershell
.\scripts\Check.ps1
```

Install the repository-managed pre-commit hook once per clone:

```powershell
.\scripts\Install-GitHooks.ps1
```

The hook runs the same restore, format, build, and test sequence. Windows CI
also runs that sequence and publishes the x86 `MB_Shmembee.dll` artifact.

## MusicBee compatibility boundary

The installed MusicBee executable is normally under:

```text
C:\Program Files (x86)\MusicBee\MusicBee.exe
```

The plugin host now includes the official MusicBee API interface and a minimal
lifecycle implementation. Close MusicBee, then deploy the Release build:

```powershell
.\scripts\Deploy-MusicBeePlugin.ps1
```

Start MusicBee and confirm Shmembee appears in Preferences > Plugins. Close
MusicBee normally, then inspect `Shmembee\lifecycle.log` under MusicBee's
persistent storage directory. A successful proof contains both `Initialise`
and `Close` entries.

The initial host proof passed on MusicBee 3.7.9704 (API revision 58) using the
current .NET Framework 4.8, x86 build. MusicBee 3.4 discovered the assembly and
called `Initialise`, but its API revision 55 was below the official interface's
declared minimum revision 57, so MusicBee must be updated before further plugin
development or testing.

The missing-track repair proof also passed: MusicBee imported a disposable M3U8
entry using the nonexistent phone-style path
`Music/ShmembeeFixture/05 Dreamcatcher - Alldaylong.mp3`. The plugin replaced it
through `Playlist_SetFiles` with the canonical indexed library URL
`D:\Music\Dreamcatcher\[Summer Holiday]\05 Dreamcatcher - Alldaylong.mp3`,
re-read the playlist successfully, and MusicBee recognized and played the track.

Remove the proof plugin while MusicBee is closed:

```powershell
.\scripts\Remove-MusicBeePlugin.ps1
```

Both scripts accept `-MusicBeePath` for portable or nonstandard installations.
MusicBee plugin behavior cannot be established by a clean build alone.
