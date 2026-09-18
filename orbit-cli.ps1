# Orbit — remote control  |  Developed by Dr. Mohamed Shehata
# Orbit CLI — the main computer controls the other computers.
# Examples:
#   orbit list
#   orbit status "Reception PC"
#   orbit ssh   "Reception PC" "Get-Content C:\path\to\app.log -Tail 50"
#   orbit shot  "Reception PC"  out.png
#   orbit click "Reception PC"  640 360
#   orbit type  "Reception PC"  "hello"
#   orbit key   "Reception PC"  enter
#   orbit tunnel "Reception PC" 3443 3443    # test a web app on the other computer from the main one
param([string]$Verb, [Parameter(ValueFromRemainingArguments = $true)]$Rest)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'orbit-common.ps1')

function Resolve-Py {
    foreach ($c in 'python','py') { $g = Get-Command $c -ErrorAction SilentlyContinue; if ($g) { return $g.Source } }
    throw 'Python is not installed on the main computer - it is needed for the screen channel (computer use).'
}
function Need-Device([string]$name) {
    if (-not $name) { throw 'Specify a device name.' }
    $d = Get-Device $name
    if (-not $d) { throw "No device named '$name'. Try: orbit list" }
    $d
}
function Invoke-Vnc {
    param($dev, [string[]]$VncArgs, [string]$OutFile)
    $code = Unprotect-Secret $dev.code
    if ($null -eq $code) { throw "Can't decrypt the code for '$($dev.name)' (must be the same Windows account that paired it)." }
    $py = Resolve-Py
    $drv = Join-Path $PSScriptRoot 'orbit-vnc.py'
    $t = Open-Tunnel $dev
    if (-not $t) { throw "Can't open an SSH tunnel to '$($dev.name)' - make sure it is on and on the network." }
    try {
        $server = "127.0.0.1::$($t.Port)"
        $env:ORBIT_VNC_PW = $code
        $argv = @($VncArgs[0], $server) + $VncArgs[1..($VncArgs.Count - 1)]
        $r = Invoke-Native $py (@($drv) + $argv) 40000
        $env:ORBIT_VNC_PW = $null
        if ($r.Code -ne 0) { throw "Screen channel failed: $($r.Err.Trim())" }
        return $r.Out.Trim()
    } finally { $env:ORBIT_VNC_PW = $null; Close-Tunnel $t }
}

switch ($Verb) {
    'list' {
        $devs = Get-Devices
        if (-not $devs -or $devs.Count -eq 0) { Write-Host 'No paired devices. Open Orbit on the main computer and pair one.'; break }
        foreach ($d in $devs) {
            $ping = Invoke-DeviceSsh $d 'echo ok' 10000
            $ok = ($ping.Out -match 'ok')
            $st = if ($ok) { 'online' } else { 'unreachable' }
            "{0,-22} {1,-16} {2}  ({3}@{4}:{5}, vnc {6})" -f $d.name, $d.alias, $st, $d.user, $d.ip, $d.sshPort, $d.vncPort
        }
    }
    'status' {
        $d = Need-Device $Rest[0]
        $ping = Invoke-DeviceSsh $d 'echo ok' 10000
        "SSH: $([bool]($ping.Out -match 'ok'))"
        try { $out = Invoke-Vnc $d @('probe') ; "VNC: True ($out)" } catch { "VNC: False — $($_.Exception.Message)" }
    }
    'ssh' {
        $d = Need-Device $Rest[0]
        $cmd = ($Rest[1..($Rest.Count - 1)] -join ' ')
        if (-not $cmd) { throw 'Type the command after the device name.' }
        $r = Invoke-DeviceSsh $d $cmd 60000
        if ($r.Out) { Write-Output $r.Out.TrimEnd() }
        if ($r.Err) { Write-Output $r.Err.TrimEnd() }
        exit $r.Code
    }
    'shot' {
        $d = Need-Device $Rest[0]
        $out = if ($Rest.Count -gt 1) { $Rest[1] } else { Join-Path $env:TEMP ("orbit-{0}-{1}.png" -f $d.alias, (Get-Date -Format 'HHmmss')) }
        $res = Invoke-Vnc $d @('capture', $out) $out
        Write-Output $res
    }
    'click' { $d = Need-Device $Rest[0]; Invoke-Vnc $d @('click', "$($Rest[1])", "$($Rest[2])", "$($Rest[3])") | Out-Null; "clicked $($Rest[1]),$($Rest[2])" }
    'move'  { $d = Need-Device $Rest[0]; Invoke-Vnc $d @('move', "$($Rest[1])", "$($Rest[2])") | Out-Null; "moved" }
    'dclick'{ $d = Need-Device $Rest[0]; Invoke-Vnc $d @('dclick', "$($Rest[1])", "$($Rest[2])") | Out-Null; "double-clicked" }
    'type'  { $d = Need-Device $Rest[0]; Invoke-Vnc $d (@('type') + $Rest[1..($Rest.Count-1)]) | Out-Null; "typed" }
    'key'   { $d = Need-Device $Rest[0]; Invoke-Vnc $d (@('key') + $Rest[1..($Rest.Count-1)]) | Out-Null; "sent keys" }
    'tunnel' {
        $d = Need-Device $Rest[0]; $lp = [int]$Rest[1]; $rp = [int]$Rest[2]
        $args = @('-i',$script:KeyPath,'-o','IdentitiesOnly=yes','-o','BatchMode=yes','-o','StrictHostKeyChecking=no','-o','UserKnownHostsFile=NUL','-o','ExitOnForwardFailure=yes','-N','-p',"$($d.sshPort)",'-L',"127.0.0.1:${lp}:127.0.0.1:${rp}",'-l',$d.user,$d.ip)
        "Tunnel open: http://127.0.0.1:$lp  ->  $($d.name):$rp  (Ctrl+C to close)"
        & $script:SshExe @args
    }
    'scan' {
        $found = @()
        foreach ($ip in (Find-OpenSsh)) { $b = Read-OrbitBanner $ip; if ($b) { $found += $b } }
        if (-not $found) { 'No Orbit devices on the network.'; break }
        foreach ($f in $found) { "{0,-18} {1,-10} {2}@{3}" -f $f.Name, $f.Role, $f.User, $f.IP }
    }
    default { "Orbit CLI $script:OrbitVersion`nCommands: list | status | ssh | shot | click | move | dclick | type | key | tunnel | scan" }
}
