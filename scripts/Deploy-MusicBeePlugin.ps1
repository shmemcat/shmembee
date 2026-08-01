[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [string]$MusicBeePath = "${env:ProgramFiles(x86)}\MusicBee",

    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryRoot "src\Shmembee.MusicBee\Shmembee.MusicBee.csproj"
$sidecarProjectPath = Join-Path $repositoryRoot "src\Shmembee.WpdSidecar\Shmembee.WpdSidecar.csproj"
$outputPath = Join-Path $repositoryRoot "src\Shmembee.MusicBee\bin\$Configuration\net48"
$sidecarOutputPath = Join-Path $repositoryRoot "src\Shmembee.WpdSidecar\bin\x86\$Configuration\net48"
$pluginDirectory = Join-Path $MusicBeePath "Plugins"
$sidecarDirectory = Join-Path $pluginDirectory "Shmembee.WpdSidecar"

if (-not $SkipBuild) {
    dotnet build $projectPath --configuration $Configuration
    if ($LASTEXITCODE -ne 0) {
        throw "The MusicBee plugin build failed with exit code $LASTEXITCODE."
    }

    dotnet build $sidecarProjectPath --configuration $Configuration -p:Platform=x86
    if ($LASTEXITCODE -ne 0) {
        throw "The WPD sidecar build failed with exit code $LASTEXITCODE."
    }
}

if (-not (Test-Path $pluginDirectory -PathType Container)) {
    throw "MusicBee plugin directory was not found: $pluginDirectory"
}

$assemblies = Get-ChildItem $outputPath -Filter "*.dll" -File
$nativeSqlite = Join-Path $outputPath "runtimes\win-x86\native\e_sqlite3.dll"
if (-not (Test-Path $nativeSqlite -PathType Leaf)) {
    throw "Expected x86 SQLite runtime was not found: $nativeSqlite"
}
$assemblies += Get-Item $nativeSqlite
$sidecarFiles = Get-ChildItem $sidecarOutputPath -File |
    Where-Object {
        $_.Extension -in @(".exe", ".dll", ".config") -or
        $_.Name -like "*.Interop.*"
    }
if (-not ($sidecarFiles | Where-Object Name -EQ "Shmembee.WpdSidecar.exe")) {
    throw "Expected WPD sidecar executable was not found: $sidecarOutputPath"
}
$assemblies = $assemblies | Sort-Object Name -Unique

foreach ($assembly in $assemblies) {
    $source = $assembly.FullName

    $destination = Join-Path $pluginDirectory $assembly.Name
    if ($PSCmdlet.ShouldProcess($destination, "Deploy $($assembly.Name)")) {
        Copy-Item $source $destination -Force
    }
}

if ($PSCmdlet.ShouldProcess($sidecarDirectory, "Create WPD sidecar directory")) {
    New-Item $sidecarDirectory -ItemType Directory -Force | Out-Null
}
foreach ($file in $sidecarFiles) {
    $destination = Join-Path $sidecarDirectory $file.Name
    if ($PSCmdlet.ShouldProcess($destination, "Deploy WPD sidecar $($file.Name)")) {
        Copy-Item $file.FullName $destination -Force
    }
}

Write-Host "Deployed Shmembee to $pluginDirectory"
Write-Host "Deployed the isolated WPD sidecar to $sidecarDirectory"
Write-Host "After MusicBee starts, inspect its persistent storage path for Shmembee\lifecycle.log."
