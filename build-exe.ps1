# Orbit - remote control | Developed by Dr. Mohamed Shehata
# Compiles the standalone Orbit.exe (WPF, C#) with the built-in .NET Framework compiler.
# Embeds orbit.xaml + Cairo fonts + orbit-vnc.py; injects the main computer's public key.
# Run:  powershell -ExecutionPolicy Bypass -File .\build-exe.ps1
param([string]$KeyPath = (Join-Path $env:USERPROFILE '.ssh\orbit_primary'))
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$fw = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$csc = Join-Path $fw 'csc.exe'
if (-not (Test-Path $csc)) { throw "csc.exe not found ($csc)" }
if (-not (Test-Path "$KeyPath.pub")) { throw "No public key: $KeyPath.pub" }
$pub = (Get-Content "$KeyPath.pub" -Raw).Trim()

# stage INSIDE the (AV-excluded) project folder so the fresh exe isn't quarantined mid-build.
# csc runs from the stage dir with relative names, so the parent path's spaces don't matter.
$stage = Join-Path $root ('.build_' + [guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Force $stage | Out-Null
try {
    Copy-Item "$root\exe\Orbit.cs","$root\exe\OrbitWorker.cs","$root\exe\OrbitMain.cs" $stage -Force
    Copy-Item "$root\orbit.xaml","$root\orbit-vnc.py" $stage -Force
    foreach ($w in 400,600,700) { Copy-Item "$root\fonts\Cairo-$w.ttf" $stage -Force }
    # offline server bundle (so setup needs no internet)
    if (-not (Test-Path "$root\exe\bundle\openssh.zip")) { throw "Missing exe\bundle\openssh.zip (Win32-OpenSSH portable)" }
    if (-not (Test-Path "$root\exe\bundle\tvnc.msi"))   { throw "Missing exe\bundle\tvnc.msi (TightVNC installer)" }
    Copy-Item "$root\exe\bundle\openssh.zip","$root\exe\bundle\tvnc.msi" $stage -Force
    # inject key
    $oc = [IO.File]::ReadAllText((Join-Path $stage 'Orbit.cs'), [Text.Encoding]::UTF8).Replace('__PUBKEY__', $pub)
    if ($oc -match '__PUBKEY__') { throw 'Key injection failed' }
    [IO.File]::WriteAllText((Join-Path $stage 'Orbit.cs'), $oc, (New-Object Text.UTF8Encoding $false))

    $refs = 'PresentationFramework','PresentationCore','WindowsBase' | ForEach-Object { "/reference:$fw\WPF\$_.dll" }
    $refs += 'System.Xaml','System','System.Core','System.Xml','System.Web.Extensions','System.ServiceProcess','System.Security','System.IO.Compression','System.IO.Compression.FileSystem' | ForEach-Object { "/reference:$fw\$_.dll" }
    $res = @('/resource:orbit.xaml,Orbit.orbit.xaml','/resource:orbit-vnc.py,Orbit.orbit-vnc.py',
             '/resource:Cairo-400.ttf,Orbit.Cairo-400.ttf','/resource:Cairo-600.ttf,Orbit.Cairo-600.ttf','/resource:Cairo-700.ttf,Orbit.Cairo-700.ttf',
             '/resource:openssh.zip,Orbit.openssh.zip','/resource:tvnc.msi,Orbit.tvnc.msi')
    Push-Location $stage
    $o = & $csc /nologo /target:winexe /out:Orbit.exe @refs @res Orbit.cs OrbitWorker.cs OrbitMain.cs 2>&1
    Pop-Location
    if ($o | Where-Object { $_ -match 'error CS' }) { $o | Where-Object { $_ -match 'error CS' } | Select-Object -First 20; throw 'compile failed' }
    Copy-Item (Join-Path $stage 'Orbit.exe') (Join-Path $root 'Orbit.exe') -Force
    "OK -> $root\Orbit.exe  ($([math]::Round((Get-Item "$root\Orbit.exe").Length/1KB)) KB)  pubkey=$($pub.Substring(0,24))..."
}
finally { try { [IO.Directory]::Delete($stage, $true) } catch {} }
