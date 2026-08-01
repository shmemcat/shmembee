[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot

git -C $repositoryRoot config core.hooksPath .githooks
if ($LASTEXITCODE -ne 0) {
    throw "Git hook configuration failed with exit code $LASTEXITCODE."
}

Write-Host "Configured repository hooks from .githooks."
