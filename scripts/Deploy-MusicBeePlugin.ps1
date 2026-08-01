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

$assemblies = @(
    "MB_Shmembee.dll",
    "Shmembee.Application.dll",
    "Shmembee.Core.dll",
    "Shmembee.Infrastructure.dll",
    "Shmembee.Windows.dll"
)

foreach ($assembly in $assemblies) {
    $source = Join-Path $outputPath $assembly
    if (-not (Test-Path $source -PathType Leaf)) {
        throw "Expected build output was not found: $source"
    }

    $destination = Join-Path $pluginDirectory $assembly
    if ($PSCmdlet.ShouldProcess($destination, "Deploy $assembly")) {
        Copy-Item $source $destination -Force
    }
}

Write-Host "Deployed Shmembee to $pluginDirectory"
Write-Host "After MusicBee starts, inspect its persistent storage path for Shmembee\lifecycle.log."
