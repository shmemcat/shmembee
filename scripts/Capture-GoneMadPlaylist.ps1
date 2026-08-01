[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$OutputPath,

    [string]$DeviceName = "MLE S24U",

    [string]$PlaylistName = "Shmembee Contract Test.m3u"
)

$ErrorActionPreference = "Stop"

function Get-PortableFolder {
    param(
        [Parameter(Mandatory)]
        [object]$ParentFolder,

        [Parameter(Mandatory)]
        [string]$Name
    )

    $item = $ParentFolder.Items() |
        Where-Object { $_.Name -eq $Name } |
        Select-Object -First 1
    if ($null -eq $item) {
        throw "Portable-device folder was not found: $Name"
    }

    return $item.GetFolder
}

$shell = New-Object -ComObject Shell.Application
$thisPc = $shell.Namespace(17)
$deviceItem = $thisPc.Items() |
    Where-Object { $_.Name -eq $DeviceName } |
    Select-Object -First 1
if ($null -eq $deviceItem) {
    throw "Portable device was not found: $DeviceName"
}

$device = $deviceItem.GetFolder
$storage = Get-PortableFolder $device "Internal storage"
$gmmp = Get-PortableFolder $storage "gmmp"
$playlists = Get-PortableFolder $gmmp "playlists"
$playlist = $playlists.Items() |
    Where-Object {
        $_.Name -eq $PlaylistName -or
        $_.Name -eq [IO.Path]::GetFileNameWithoutExtension($PlaylistName)
    } |
    Select-Object -First 1
if ($null -eq $playlist) {
    throw "GoneMAD playlist was not found: $PlaylistName"
}

$destination = [IO.Path]::GetFullPath($OutputPath)
$destinationDirectory = Split-Path -Parent $destination
New-Item $destinationDirectory -ItemType Directory -Force | Out-Null
$destinationFolder = $shell.Namespace($destinationDirectory)
$destinationFolder.CopyHere($playlist, 20)

$copiedPath = Join-Path $destinationDirectory $PlaylistName
$deadline = [DateTime]::UtcNow.AddSeconds(30)
do {
    Start-Sleep -Milliseconds 500
} while (-not (Test-Path $copiedPath) -and [DateTime]::UtcNow -lt $deadline)

if (-not (Test-Path $copiedPath)) {
    throw "The MTP capture did not complete within 30 seconds."
}

if ($copiedPath -ne $destination) {
    Move-Item $copiedPath $destination -Force
}

Write-Host "Captured GoneMAD playlist to $destination"
