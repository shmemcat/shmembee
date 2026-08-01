[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$SourcePath = (
        Join-Path (Split-Path -Parent $PSScriptRoot) `
            "tests\fixtures\gonemad\contract-external-replacement.m3u"
    ),

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

function Find-PlaylistItem {
    param(
        [Parameter(Mandatory)]
        [object]$PlaylistFolder,

        [Parameter(Mandatory)]
        [string]$Name
    )

    return $PlaylistFolder.Items() |
        Where-Object {
            $_.Name -eq $Name -or
            $_.Name -eq [IO.Path]::GetFileNameWithoutExtension($Name)
        } |
        Select-Object -First 1
}

$source = Get-Item $SourcePath
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
$existing = Find-PlaylistItem $playlists $PlaylistName
if ($null -eq $existing) {
    throw "GoneMAD playlist was not found: $PlaylistName"
}

if ($PSCmdlet.ShouldProcess(
    "$DeviceName\Internal storage\gmmp\playlists\$PlaylistName",
    "Replace disposable GoneMAD contract playlist"
)) {
    $temporaryDirectory = Join-Path ([IO.Path]::GetTempPath()) (
        "shmembee-" + [Guid]::NewGuid().ToString("N")
    )
    New-Item $temporaryDirectory -ItemType Directory | Out-Null

    try {
        $renamedSource = Join-Path $temporaryDirectory $PlaylistName
        Copy-Item $source.FullName $renamedSource
        $temporaryFolder = $shell.Namespace($temporaryDirectory)
        $temporaryItem = $temporaryFolder.ParseName($PlaylistName)

        $existing.InvokeVerb("delete")
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        do {
            Start-Sleep -Milliseconds 500
            $existing = Find-PlaylistItem $playlists $PlaylistName
        } while ($null -ne $existing -and [DateTime]::UtcNow -lt $deadline)
        if ($null -ne $existing) {
            throw "The old MTP playlist did not disappear within 30 seconds."
        }

        $playlists.CopyHere($temporaryItem, 20)
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        do {
            Start-Sleep -Milliseconds 500
            $replacement = Find-PlaylistItem $playlists $PlaylistName
        } while ($null -eq $replacement -and [DateTime]::UtcNow -lt $deadline)
        if ($null -eq $replacement) {
            throw "The replacement MTP playlist did not appear within 30 seconds."
        }
    }
    finally {
        Remove-Item $temporaryDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host "Replaced disposable GoneMAD playlist: $PlaylistName"
