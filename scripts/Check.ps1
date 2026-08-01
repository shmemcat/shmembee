[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repositoryRoot

try {
    dotnet restore Shmembee.sln
    if ($LASTEXITCODE -ne 0) {
        throw "Restore failed with exit code $LASTEXITCODE."
    }

    dotnet format Shmembee.sln --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw "Formatting failed with exit code $LASTEXITCODE."
    }

    dotnet build Shmembee.sln --configuration $Configuration --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw "Build failed with exit code $LASTEXITCODE."
    }

    dotnet test Shmembee.sln --configuration $Configuration --no-build --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw "Tests failed with exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}

Write-Host "Shmembee checks passed."
