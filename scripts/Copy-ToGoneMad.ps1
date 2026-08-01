[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$SourcePath = (
        Join-Path (Split-Path -Parent $PSScriptRoot) `
            "tests\fixtures\gonemad\contract-input.m3u8"
    ),

    [string]$DeviceName = "MLE S24U",

    [string]$DestinationName = "Shmembee Contract Test.m3u"
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

$existing = $playlists.Items() |
    Where-Object {
        $_.Name -eq $DestinationName -or
        $_.Name -eq [IO.Path]::GetFileNameWithoutExtension($DestinationName)
    } |
    Select-Object -First 1
if ($null -ne $existing) {
    throw "The disposable destination already exists: $DestinationName"
}

if ($PSCmdlet.ShouldProcess(
    "$DeviceName\Internal storage\gmmp\playlists\$DestinationName",
    "Copy disposable GoneMAD contract fixture"
)) {
    $temporaryDirectory = Join-Path ([IO.Path]::GetTempPath()) (
        "shmembee-" + [Guid]::NewGuid().ToString("N")
    )
    New-Item $temporaryDirectory -ItemType Directory | Out-Null

    try {
        $renamedSource = Join-Path $temporaryDirectory $DestinationName
        Copy-Item $source.FullName $renamedSource
        $temporaryFolder = $shell.Namespace($temporaryDirectory)
        $temporaryItem = $temporaryFolder.ParseName($DestinationName)
        $playlists.CopyHere($temporaryItem, 20)

        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        do {
            Start-Sleep -Milliseconds 500
            $copied = $playlists.Items() |
                Where-Object {
                    $_.Name -eq $DestinationName -or
                    $_.Name -eq [IO.Path]::GetFileNameWithoutExtension($DestinationName)
                } |
                Select-Object -First 1
        } while ($null -eq $copied -and [DateTime]::UtcNow -lt $deadline)

        if ($null -eq $copied) {
            throw "The MTP copy did not become visible within 30 seconds."
        }
    }
    finally {
        Remove-Item $temporaryDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host "Copied disposable fixture to GoneMAD: $DestinationName"
