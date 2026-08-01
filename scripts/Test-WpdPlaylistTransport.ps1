[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [string]$Device = "MLE S24U",

    [string]$Storage = "Internal storage",

    [string]$Folder = "gmmp/playlists",

    [string]$Name = "Shmembee Phase 3 Test.m3u",

    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryRoot "src\Shmembee.WpdSidecar\Shmembee.WpdSidecar.csproj"
$sidecarPath = Join-Path $repositoryRoot `
    "src\Shmembee.WpdSidecar\bin\x86\$Configuration\net48\Shmembee.WpdSidecar.exe"

if (-not $SkipBuild) {
    dotnet build $projectPath --configuration $Configuration -p:Platform=x86
    if ($LASTEXITCODE -ne 0) {
        throw "The WPD sidecar build failed with exit code $LASTEXITCODE."
    }
}

if (-not (Test-Path $sidecarPath -PathType Leaf)) {
    throw "The WPD sidecar executable was not found: $sidecarPath"
}

function Invoke-WpdSidecar {
    param(
        [Parameter(Mandatory)]
        [string]$Operation,

        [Parameter(Mandatory)]
        [string]$ObjectName,

        [string]$ContentBase64
    )

    $operationId = [Guid]::NewGuid().ToString("N")
    $request = @{
        Operation = $Operation
        OperationId = $operationId
        Device = $Device
        Storage = $Storage
        Folder = $Folder
        Name = $ObjectName
        ContentBase64 = $ContentBase64
    } | ConvertTo-Json -Compress
    $responseJson = $request | & $sidecarPath
    $exitCode = $LASTEXITCODE
    if ([string]::IsNullOrWhiteSpace($responseJson)) {
        throw "The WPD sidecar returned no $Operation response."
    }

    $response = $responseJson | ConvertFrom-Json
    if ($response.OperationId -ne $operationId) {
        throw "The WPD $Operation response operation ID did not match its request."
    }
    if ($exitCode -ne 0 -or -not $response.Success) {
        throw "WPD $Operation failed at '$($response.Stage)' (HRESULT $($response.HResult)): $($response.Error)"
    }

    return $response
}

$response = Invoke-WpdSidecar -Operation "probe" -ObjectName $Name
if (-not $response.OriginalObjectId) {
    throw "The disposable source playlist was not found: $Name"
}
$source = Invoke-WpdSidecar -Operation "read" -ObjectName $Name
$candidateName = "Shmembee WPD Probe $([Guid]::NewGuid().ToString('N')).m3u"
try {
    $null = Invoke-WpdSidecar `
        -Operation "replace" `
        -ObjectName $candidateName `
        -ContentBase64 $source.ContentBase64
    $candidate = Invoke-WpdSidecar -Operation "read" -ObjectName $candidateName
    if ($candidate.ContentBase64 -ne $source.ContentBase64) {
        throw "The candidate readback bytes differ from the source playlist."
    }
}
finally {
    $null = Invoke-WpdSidecar -Operation "delete" -ObjectName $candidateName
}

$cleanup = Invoke-WpdSidecar -Operation "probe" -ObjectName $candidateName
if ($cleanup.OriginalObjectId) {
    throw "The WPD probe candidate was not cleaned up: $candidateName"
}

Write-Host "Non-destructive WPD probe passed."
Write-Host "Device object ID: $($response.DeviceId)"
Write-Host "Storage object ID: $($response.StorageId)"
Write-Host "Folder object ID: $($response.FolderId)"
Write-Host "Playlist object ID: $($response.OriginalObjectId)"
Write-Host "Playlist bytes: $($response.ByteCount)"
Write-Host "Playlist SHA-256: $($response.Sha256)"
Write-Host "Objects enumerated: $(@($response.Objects).Count)"
Write-Host "Candidate promotion/readback/cleanup: passed"
Write-Warning "Real apply remains disabled until this result is reviewed on the target device."
