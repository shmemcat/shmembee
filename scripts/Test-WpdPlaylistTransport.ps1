[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [string]$Device = "MLE S24U",

    [string]$Storage = "Internal storage",

    [string]$Folder = "gmmp/playlists",

    [string]$Name = "Shmembee Phase 3 Test.m3u",

    [switch]$SkipBuild,

    [switch]$BackupProbe,

    [ValidateRange(0, 10000)]
    [int]$ReadOnlySoakCount = 0,

    [string]$DiagnosticsPath = (Join-Path $env:LOCALAPPDATA "Shmembee\diagnostics")
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

        [AllowNull()]
        [string]$ObjectName = $null,

        [string]$ContentBase64,

        [string]$RequestFolder = $Folder,

        [string]$BackupFolderName,

        [string[]]$CopiedNames
    )

    $operationId = [Guid]::NewGuid().ToString("N")
    $request = @{
        Operation = $Operation
        OperationId = $operationId
        Device = $Device
        Storage = $Storage
        Folder = $RequestFolder
        Name = $ObjectName
        ContentBase64 = $ContentBase64
        BackupFolderName = $BackupFolderName
        CopiedNames = $CopiedNames
        ActivityId = $script:activityId
        DiagnosticsPath = (Join-Path $DiagnosticsPath "wpd-diagnostics.jsonl")
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

$script:activityId = [Guid]::NewGuid().ToString("N")
if ($ReadOnlySoakCount -gt 0) {
    1..$ReadOnlySoakCount | ForEach-Object {
        $null = Invoke-WpdSidecar -Operation "probe" -ObjectName $Name
        $null = Invoke-WpdSidecar -Operation "read" -ObjectName $Name
        Write-Progress -Activity "Read-only WPD soak" `
            -Status "$_ / $ReadOnlySoakCount" `
            -PercentComplete (($_ / $ReadOnlySoakCount) * 100)
    }
    Write-Progress -Activity "Read-only WPD soak" -Completed
    Write-Host "Read-only soak passed ($ReadOnlySoakCount iterations). Diagnostics: $DiagnosticsPath"
    return
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

if ($BackupProbe) {
    $backupHandle = $null
    $preservedBackupObjects = @()
    try {
        $backupHandle = Invoke-WpdSidecar -Operation "create-playlist-backup"
        if ([string]::IsNullOrWhiteSpace($backupHandle.BackupFolderName)) {
            throw "The backup probe received no cleanup handle."
        }

        $copiedNames = @($backupHandle.CopiedNames)
        $backupRoot = $Folder.TrimEnd("/", "\") + "/backup"
        $backupFolder = $Folder.TrimEnd("/", "\") `
            + "/backup/" + $backupHandle.BackupFolderName
        $backupRootSnapshot = Invoke-WpdSidecar `
            -Operation "probe" `
            -RequestFolder $backupRoot
        $preservedBackupObjects = @($backupRootSnapshot.Objects | Where-Object {
            ($_ -split "\|", 2)[1] -ne $backupHandle.BackupFolderName
        })
        foreach ($copiedName in $copiedNames) {
            if ([string]::IsNullOrWhiteSpace($copiedName) `
                -or [IO.Path]::GetFileName($copiedName) -ne $copiedName `
                -or @(".m3u", ".m3u8") -notcontains [IO.Path]::GetExtension($copiedName).ToLowerInvariant()) {
                throw "The backup probe received an unsafe copied name: $copiedName"
            }

            $original = Invoke-WpdSidecar `
                -Operation "read" `
                -ObjectName $copiedName
            $copy = Invoke-WpdSidecar `
                -Operation "read" `
                -RequestFolder $backupFolder `
                -ObjectName $copiedName
            if ($copy.ContentBase64 -ne $original.ContentBase64) {
                throw "Backup readback differs from source playlist: $copiedName"
            }
        }
    }
    finally {
        if ($null -ne $backupHandle `
            -and -not [string]::IsNullOrWhiteSpace($backupHandle.BackupFolderName)) {
            $null = Invoke-WpdSidecar `
                -Operation "delete-playlist-backup" `
                -BackupFolderName $backupHandle.BackupFolderName `
                -CopiedNames @($backupHandle.CopiedNames)
        }
    }

    $cleanupSnapshot = Invoke-WpdSidecar `
        -Operation "probe" `
        -RequestFolder $backupRoot
    $remainingBackupObjects = @($cleanupSnapshot.Objects)
    if ($remainingBackupObjects | Where-Object {
        ($_ -split "\|", 2)[1] -eq $backupHandle.BackupFolderName
    }) {
        throw "The backup probe's returned folder was not cleaned up."
    }
    foreach ($preservedObject in $preservedBackupObjects) {
        if ($remainingBackupObjects -notcontains $preservedObject) {
            throw "Cleanup changed a pre-existing backup object: $preservedObject"
        }
    }

    Write-Host "Opt-in backup create/copy/verify/cleanup probe: passed"
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
