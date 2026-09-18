# Orbit — remote control  |  Developed by Dr. Mohamed Shehata
# Shared module (paths, keys, devices, SSH tunnels, network scan)
# dot-sourced by orbit.ps1 (the UI) and orbit-cli.ps1 (the CLI).
Set-StrictMode -Off
Add-Type -AssemblyName System.Security -ErrorAction SilentlyContinue

$script:OrbitVersion = '1.0'
$script:KeyName      = 'orbit_primary'
$script:SshRoot      = Join-Path $env:SystemRoot 'System32\OpenSSH'
$script:SshExe       = Join-Path $script:SshRoot 'ssh.exe'
$script:KeygenExe    = Join-Path $script:SshRoot 'ssh-keygen.exe'
$script:HomeSsh      = Join-Path $env:USERPROFILE '.ssh'
$script:KeyPath      = Join-Path $script:HomeSsh $script:KeyName
$script:DataDir      = Join-Path $env:APPDATA 'Orbit'
$script:HomeDir      = Join-Path $env:LOCALAPPDATA 'Orbit'
$script:DevicesFile  = Join-Path $script:DataDir 'devices.json'
$script:Utf8NoBom    = New-Object System.Text.UTF8Encoding $false
$script:BannerUser   = 'orbit-probe'
$script:RoleNames    = @{ primary = 'Main computer'; secondary = 'Other computer' }

function Ensure-Dir([string]$p) { if (-not (Test-Path $p)) { New-Item -ItemType Directory -Force -Path $p | Out-Null } }

# -- Run an external program with a timeout (no stderr throwing) --
function Invoke-Native {
    param([string]$Exe, [string[]]$Argv, [int]$TimeoutMs = 20000, [string]$StdIn)
    $quoted = foreach ($a in $Argv) { if ($a -eq '' -or $a -match '[\s"]') { '"' + ($a -replace '"', '\"') + '"' } else { $a } }
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $Exe; $psi.Arguments = ($quoted -join ' ')
    $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true; $psi.RedirectStandardInput = $true
    $psi.StandardOutputEncoding = [Text.Encoding]::UTF8; $psi.StandardErrorEncoding = [Text.Encoding]::UTF8
    $p = [Diagnostics.Process]::Start($psi)
    if ($StdIn) { $p.StandardInput.Write($StdIn) }
    $p.StandardInput.Close()
    $so = $p.StandardOutput.ReadToEndAsync(); $se = $p.StandardError.ReadToEndAsync()
    $to = $false
    if (-not $p.WaitForExit($TimeoutMs)) { $to = $true; try { $p.Kill() } catch {}; $p.WaitForExit() }
    [pscustomobject]@{ Code = $p.ExitCode; Out = $so.Result; Err = $se.Result; TimedOut = $to }
}

# -- DPAPI (encryption bound to the current Windows account) --
function Protect-Secret([string]$plain) {
    $b = [Text.Encoding]::UTF8.GetBytes($plain)
    $enc = [Security.Cryptography.ProtectedData]::Protect($b, $null, 'CurrentUser')
    [Convert]::ToBase64String($enc)
}
function Unprotect-Secret([string]$b64) {
    try {
        $enc = [Convert]::FromBase64String($b64)
        $b = [Security.Cryptography.ProtectedData]::Unprotect($enc, $null, 'CurrentUser')
        [Text.Encoding]::UTF8.GetString($b)
    } catch { $null }
}

# -- Code / password: 8 unambiguous chars (VNC limit) --
function New-OrbitCode {
    $chars = 'ABCDEFGHJKLMNPQRSTUVWXYZ23456789'.ToCharArray()
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    $buf = New-Object byte[] 8; $rng.GetBytes($buf)
    -join ($buf | ForEach-Object { $chars[$_ % $chars.Length] })
}
function Format-Code([string]$c) { if ($c.Length -eq 8) { $c.Substring(0,4) + ' · ' + $c.Substring(4,4) } else { $c } }

# -- Device registry (on the main computer) --
function Get-Devices {
    if (-not (Test-Path $script:DevicesFile)) { return @() }
    $raw = Get-Content $script:DevicesFile -Raw -Encoding UTF8
    if (-not $raw -or -not $raw.Trim()) { return @() }
    try { $d = $raw | ConvertFrom-Json } catch { return @() }
    @(@($d) | Where-Object { $_ -and $_.ip -and $_.alias })
}
function Save-Devices($devices) {
    Ensure-Dir $script:DataDir
    $arr = @($devices | Where-Object { $_ -and $_.ip -and $_.alias })
    $json = if ($arr.Count -eq 0) { '[]' } else { ConvertTo-Json -InputObject $arr -Depth 6 }
    [IO.File]::WriteAllText($script:DevicesFile, $json, $script:Utf8NoBom)
}
function Get-Device([string]$name) {
    Get-Devices | Where-Object { $_.name -eq $name -or $_.alias -eq $name -or $_.alias -eq "orbit-$name" } | Select-Object -First 1
}
function Set-Device($dev) {
    $all = @(Get-Devices | Where-Object { $_.alias -ne $dev.alias })
    $all += $dev
    Save-Devices $all
    Write-SshConfig
}
function Remove-DeviceEntry([string]$name) {
    $all = @(Get-Devices | Where-Object { $_.name -ne $name -and $_.alias -ne $name -and $_.alias -ne "orbit-$name" })
    Save-Devices $all; Write-SshConfig
}

# -- Write the ~/.ssh/config block for devices --
function Write-SshConfig {
    Ensure-Dir $script:HomeSsh
    $cfgFile = Join-Path $script:HomeSsh 'config'
    $existing = ''
    if (Test-Path $cfgFile) { $existing = [IO.File]::ReadAllText($cfgFile, [Text.Encoding]::UTF8) }
    $existing = [regex]::Replace($existing, '(?s)# orbit begin.*?# orbit end\r?\n?', '')
    $block = "# orbit begin (auto-generated — re-run Orbit instead of editing)`n"
    foreach ($d in Get-Devices) {
        $block += "Host $($d.alias)`n  HostName $($d.ip)`n  User $($d.user)`n  Port $($d.sshPort)`n  IdentityFile ~/.ssh/$script:KeyName`n  IdentitiesOnly yes`n  StrictHostKeyChecking no`n  UserKnownHostsFile NUL`n  ConnectTimeout 6`n"
    }
    $block += "# orbit end`n"
    $text = ($existing.TrimEnd() + "`n`n" + $block).TrimStart()
    [IO.File]::WriteAllText($cfgFile, $text, $script:Utf8NoBom)
}

# -- Run a command on the other computer over SSH (key) --
function Invoke-DeviceSsh {
    param($dev, [string]$Command, [int]$TimeoutMs = 30000)
    Invoke-Native $script:SshExe @(
        '-i', $script:KeyPath, '-o', 'IdentitiesOnly=yes', '-o', 'BatchMode=yes',
        '-o', 'StrictHostKeyChecking=no', '-o', 'UserKnownHostsFile=NUL', '-o', "ConnectTimeout=8",
        '-p', "$($dev.sshPort)", '-l', $dev.user, $dev.ip, $Command
    ) $TimeoutMs
}

# -- SSH tunnel: local port -> 127.0.0.1:vnc on the other computer (VNC is loopback-only there) --
function Open-Tunnel {
    param($dev, [int]$LocalPort)
    if (-not $LocalPort) { $LocalPort = Get-Random -Minimum 55000 -Maximum 59000 }
    $args = @(
        '-i', $script:KeyPath, '-o', 'IdentitiesOnly=yes', '-o', 'BatchMode=yes',
        '-o', 'StrictHostKeyChecking=no', '-o', 'UserKnownHostsFile=NUL', '-o', 'ExitOnForwardFailure=yes',
        '-o', 'ServerAliveInterval=15', '-N', '-p', "$($dev.sshPort)",
        '-L', "127.0.0.1:${LocalPort}:127.0.0.1:$($dev.vncPort)", '-l', $dev.user, $dev.ip
    )
    $psi = New-Object Diagnostics.ProcessStartInfo
    $psi.FileName = $script:SshExe; $psi.Arguments = ($args | ForEach-Object { if ($_ -match '[\s"]') { '"'+$_+'"' } else { $_ } }) -join ' '
    $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true
    $p = [Diagnostics.Process]::Start($psi)
    # wait for the port to open
    $ok = $false
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 250
        if ($p.HasExited) { break }
        try { $c = New-Object Net.Sockets.TcpClient; $c.Connect('127.0.0.1', $LocalPort); $c.Close(); $ok = $true; break } catch {}
    }
    if (-not $ok) { try { if (-not $p.HasExited) { $p.Kill() } } catch {}; return $null }
    [pscustomobject]@{ Proc = $p; Port = $LocalPort }
}
function Close-Tunnel($t) { if ($t -and $t.Proc -and -not $t.Proc.HasExited) { try { $t.Proc.Kill() } catch {} } }

# -- Network scan + read the Orbit banner --
function Get-LanIPs {
    @(Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Where-Object { $_.IPAddress -notlike '169.254.*' -and $_.IPAddress -ne '127.0.0.1' -and $_.PrefixOrigin -in 'Dhcp','Manual' } |
        Select-Object -ExpandProperty IPAddress)
}
function Find-OpenSsh {
    param([int]$Port = 22)
    $bases = @(Get-LanIPs | ForEach-Object { ($_ -split '\.')[0..2] -join '.' } | Sort-Object -Unique)
    $targets = @(foreach ($b in $bases) { 1..254 | ForEach-Object { "$b.$_" } })
    $open = New-Object Collections.Generic.List[string]
    for ($i = 0; $i -lt $targets.Count; $i += 128) {
        $batch = $targets[$i..([Math]::Min($i + 127, $targets.Count - 1))]
        $pend = foreach ($t in $batch) {
            $c = New-Object Net.Sockets.TcpClient; $ar = $null
            try { $ar = $c.BeginConnect($t, $Port, $null, $null) } catch {}
            [pscustomobject]@{ T = $t; C = $c; Ar = $ar }
        }
        Start-Sleep -Milliseconds 1400
        foreach ($p in $pend) {
            if ($p.Ar -and $p.Ar.IsCompleted) { try { $p.C.EndConnect($p.Ar); if ($p.C.Connected) { $open.Add($p.T) } } catch {} }
            try { $p.C.Close() } catch {}
        }
    }
    @($open)
}
function Ensure-Vncdotool {
    $py = Get-Command python -EA SilentlyContinue; if (-not $py) { $py = Get-Command py -EA SilentlyContinue }
    if (-not $py) { return $false }
    if ((Invoke-Native $py.Source @('-c','import vncdotool') 8000).Code -eq 0) { return $true }
    Invoke-Native $py.Source @('-m','pip','install','--user','--quiet','vncdotool') 240000 | Out-Null
    return ((Invoke-Native $py.Source @('-c','import vncdotool') 8000).Code -eq 0)
}
function Install-Self([string]$SourceDir) {
    Ensure-Dir $script:HomeDir
    $src = (Resolve-Path $SourceDir -EA SilentlyContinue).Path
    $dst = (Resolve-Path $script:HomeDir -EA SilentlyContinue).Path
    if ($src -and $src -ne $dst) { Copy-Item (Join-Path $SourceDir '*') $script:HomeDir -Recurse -Force -EA SilentlyContinue }
    $shim = "@echo off`r`npowershell -NoProfile -ExecutionPolicy Bypass -File `"%~dp0orbit-cli.ps1`" %*`r`n"
    [IO.File]::WriteAllText((Join-Path $script:HomeDir 'orbit.cmd'), $shim, $script:Utf8NoBom)
}
function Read-OrbitBanner {
    param([string]$ip, [int]$Port = 22)
    $r = Invoke-Native $script:SshExe @('-n','-o','BatchMode=yes','-o','StrictHostKeyChecking=no','-o','UserKnownHostsFile=NUL','-o','ConnectTimeout=5','-o','PreferredAuthentications=none','-p',"$Port",'-l',$script:BannerUser,$ip,'exit') 12000
    $m = [regex]::Match(($r.Out + "`n" + $r.Err), 'orbit\|(\d+)\|(\w+)\|([^|\r\n]*)\|([^|\r\n]*)\|(\d+)')
    if ($m.Success) {
        [pscustomobject]@{ Ver=$m.Groups[1].Value; Role=$m.Groups[2].Value; Name=$m.Groups[3].Value.Trim(); User=$m.Groups[4].Value.Trim(); VncPort=[int]$m.Groups[5].Value; IP=$ip; SshPort=$Port }
    }
}
