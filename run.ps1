#Requires -Version 7.0
<#
.SYNOPSIS
    #4830 U13 (R15): the Attestree publish Action's one step. Downloads the attestree-publish build this version of the
    Action pins, checks its SHA-256, and runs it with the step's inputs.

.DESCRIPTION
    The inputs arrive as ATTESTREE_* environment variables (action.yml), never as script text. The CLI build is pinned by
    cli-sha256.txt, which ships with the Action: a version of the Action runs exactly the build it was released with, and a
    download whose hash differs is refused before it runs. The CLI's exit code is the step's.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$rid = switch ("$($env:RUNNER_OS)/$($env:RUNNER_ARCH)") {
    'Windows/X64' { 'win-x64' }
    'Linux/X64' { 'linux-x64' }
    'Linux/ARM64' { 'linux-arm64' }
    'macOS/ARM64' { 'osx-arm64' }
    default { throw "attestree-publish has no build for a $($env:RUNNER_OS) $($env:RUNNER_ARCH) runner." }
}
$name = if ($rid -like 'win-*') { "attestree-publish-$rid.exe" } else { "attestree-publish-$rid" }

$pins = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'cli-sha256.txt') | Where-Object { $_ -and -not $_.StartsWith('#') }
$version = ($pins | Where-Object { $_ -like 'version *' } | Select-Object -First 1) -replace '^version\s+', ''
$expected = ($pins | Where-Object { $_ -match "^[0-9a-f]{64}\s+$([regex]::Escape($name))$" } | Select-Object -First 1) -replace '\s.*$', ''
if (-not $version -or -not $expected) {
    throw "This version of the Action pins no attestree-publish build for $rid."
}

$cli = Join-Path ([IO.Path]::GetTempPath()) "attestree-publish-$version-$name"
Invoke-WebRequest -Uri "https://github.com/attestree/publish/releases/download/cli-v$version/$name" -OutFile $cli
$actual = (Get-FileHash -LiteralPath $cli -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actual -ne $expected) {
    Remove-Item -LiteralPath $cli -Force
    throw "The downloaded $name hashes to $actual, not the $expected this version of the Action pins. Nothing was run."
}
if (-not $IsWindows) {
    chmod +x $cli
}

$cliArgs = [System.Collections.Generic.List[string]]::new()
function Add-Option([string] $Option, [string] $Value) {
    if (-not [string]::IsNullOrWhiteSpace($Value)) {
        $cliArgs.Add($Option)
        $cliArgs.Add($Value.Trim())
    }
}

Add-Option '--package-id' $env:ATTESTREE_PACKAGE_ID
Add-Option '--version' $env:ATTESTREE_VERSION
foreach ($line in ($env:ATTESTREE_INSTALLERS -split "`r?`n")) {
    Add-Option '--installer' $line
}
Add-Option '--manifest' $env:ATTESTREE_MANIFEST
if ($env:ATTESTREE_TO_REPOSITORY) {
    Add-Option '--to-repository' $env:ATTESTREE_TO_REPOSITORY
    Add-Option '--branch' $env:ATTESTREE_BRANCH
    Add-Option '--manifests-path' $env:ATTESTREE_MANIFESTS_PATH
    Add-Option '--publisher' $env:ATTESTREE_PUBLISHER
    Add-Option '--installer-type' $env:ATTESTREE_INSTALLER_TYPE
}
else {
    Add-Option '--instance' $env:ATTESTREE_INSTANCE
    # The job's OIDC token is requested for the instance's audience (KTD8): GitHub mints it for exactly this value.
    Add-Option '--audience' $env:ATTESTREE_AUDIENCE
    if ($env:ATTESTREE_WAIT -eq 'true') {
        $cliArgs.Add('--wait')
        Add-Option '--wait-timeout' $env:ATTESTREE_WAIT_TIMEOUT
    }
}
if ($env:ATTESTREE_VERBOSE -eq 'true') {
    $cliArgs.Add('--verbose')
}

& $cli @cliArgs
exit $LASTEXITCODE
