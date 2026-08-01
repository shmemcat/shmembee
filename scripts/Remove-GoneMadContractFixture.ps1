[CmdletBinding(SupportsShouldProcess)]
param(
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
    Write-Host "Disposable GoneMAD fixture is already absent."
    return
}

if ($PSCmdlet.ShouldProcess(
    "$DeviceName\Internal storage\gmmp\playlists\$PlaylistName",
    "Delete disposable GoneMAD contract fixture"
)) {
    $playlist.InvokeVerb("delete")
}

Write-Host "Removed disposable GoneMAD fixture: $PlaylistName"
