# Orbit — remote control  |  Developed by Dr. Mohamed Shehata
# Builds the Orbit distributable: dist\Orbit folder + Orbit.zip — plain scripts + font + a plain launcher.
# Important: do NOT ship a single self-extracting .cmd (encoded/compressed PowerShell) — Kaspersky deletes it on sight.
# Run:  powershell -ExecutionPolicy Bypass -File .\build.ps1
param([string]$KeyPath = (Join-Path $env:USERPROFILE '.ssh\orbit_primary'))
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if (-not (Test-Path "$KeyPath.pub")) { throw "No public key: $KeyPath.pub" }
$pub = (Get-Content "$KeyPath.pub" -Raw).Trim()
if ($pub -notmatch '^ssh-(ed25519|rsa) \S+') { throw "The key looks malformed" }

$dist = Join-Path $root 'dist\Orbit'
if (Test-Path (Join-Path $root 'dist')) { [IO.Directory]::Delete((Join-Path $root 'dist'), $true) }
New-Item -ItemType Directory -Force (Join-Path $dist 'fonts') | Out-Null

foreach ($f in 'orbit.ps1','orbit.xaml','orbit-common.ps1','orbit-cli.ps1','orbit-vnc.py','README.md','README.txt') {
    if (-not (Test-Path (Join-Path $root $f))) { throw "Missing: $f" }
    Copy-Item (Join-Path $root $f) (Join-Path $dist $f) -Force
}
foreach ($w in 400,600,700) { Copy-Item (Join-Path $root "fonts\Cairo-$w.ttf") (Join-Path $dist "fonts\Cairo-$w.ttf") -Force }
Copy-Item (Join-Path $root 'fonts\OFL.txt') (Join-Path $dist 'fonts\OFL.txt') -Force -EA SilentlyContinue

# inject the public key into the distributed copy
$op = [IO.File]::ReadAllText((Join-Path $dist 'orbit.ps1'), [Text.Encoding]::UTF8).Replace('__PUBKEY__', $pub)
if ($op -match '__PUBKEY__') { throw 'Key injection failed' }
[IO.File]::WriteAllText((Join-Path $dist 'orbit.ps1'), $op, (New-Object Text.UTF8Encoding $false))

# plain, readable launcher (not flagged as a virus)
$launcher = "@echo off`r`nrem Orbit remote control`r`ncd /d ""%~dp0""`r`npowershell -NoProfile -ExecutionPolicy Bypass -File ""%~dp0orbit.ps1""`r`n"
[IO.File]::WriteAllText((Join-Path $dist 'Run Orbit.cmd'), $launcher, (New-Object Text.UTF8Encoding $false))

$zip = Join-Path $root 'Orbit.zip'
if (Test-Path $zip) { [IO.File]::Delete($zip) }
Compress-Archive -Path $dist -DestinationPath $zip -Force
"OK -> $zip ($([math]::Round((Get-Item $zip).Length/1KB)) KB) + folder: $dist  pubkey=$($pub.Substring(0,24))..."
