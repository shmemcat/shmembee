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
$outputPath = Join-Path $repositoryRoot "src\Shmembee.MusicBee\bin\$Configuration\net48"
$pluginDirectory = Join-Path $MusicBeePath "Plugins"

if (-not $SkipBuild) {
    dotnet build $projectPath --configuration $Configuration
    if ($LASTEXITCODE -ne 0) {
        throw "The MusicBee plugin build failed with exit code $LASTEXITCODE."
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

foreach ($assembly in $assemblies) {
    $source = $assembly.FullName

    $destination = Join-Path $pluginDirectory $assembly.Name
    if ($PSCmdlet.ShouldProcess($destination, "Deploy $($assembly.Name)")) {
        Copy-Item $source $destination -Force
    }
}

Write-Host "Deployed Shmembee to $pluginDirectory"
Write-Host "After MusicBee starts, inspect its persistent storage path for Shmembee\lifecycle.log."
