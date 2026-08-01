[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$MusicBeePath = "${env:ProgramFiles(x86)}\MusicBee"
)

$ErrorActionPreference = "Stop"
$pluginDirectory = Join-Path $MusicBeePath "Plugins"
$assemblies = @(
    "MB_Shmembee.dll",
    "Shmembee.Application.dll",
    "Shmembee.Core.dll",
    "Shmembee.Infrastructure.dll",
    "Shmembee.Windows.dll"
)

foreach ($assembly in $assemblies) {
    $destination = Join-Path $pluginDirectory $assembly
    if ((Test-Path $destination -PathType Leaf) -and
        $PSCmdlet.ShouldProcess($destination, "Remove $assembly")) {
        Remove-Item $destination -Force
    }
}

Write-Host "Removed Shmembee assemblies from $pluginDirectory"
