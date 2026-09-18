# Orbit — remote control  |  Developed by Dr. Mohamed Shehata
# UI + role logic. The .cmd runs this file with no args (the UI).
#   ORBIT_AUTOROLE = secondary|remove   -> elevated run for setup/removal (started by the button)
#   ORBIT_CODE / ORBIT_NAME / ORBIT_PROGRESS  -> inputs for the elevated run
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'orbit-common.ps1')

$script:PubKey = '__PUBKEY__'   # main computer public key (injected at build time)
$script:TightUrl = @(
  'https://www.tightvnc.com/download/2.8.85/tightvnc-2.8.85-gpl-setup-64bit.msi',
  'https://web.archive.org/web/2023/https://www.tightvnc.com/download/2.8.85/tightvnc-2.8.85-gpl-setup-64bit.msi'
)
$script:VncPort = 5900
$sshdDir    = Join-Path $env:ProgramData 'ssh'
$sshdConfig = Join-Path $sshdDir 'sshd_config'
$adminKeys  = Join-Path $sshdDir 'administrators_authorized_keys'
$bannerFile = Join-Path $sshdDir 'orbit-banner.txt'
$stateDir   = Join-Path $env:ProgramData 'Orbit'
$stateFile  = Join-Path $stateDir 'state.json'
$fwSsh      = 'Orbit-SSH'
$tag        = '# orbit'
$keyComment = 'orbit-primary'

function Is-Admin { ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) }

# ====================== Role logic (called from the UI or elevated) ======================

function Write-Progress-Line([string]$m) {
    if ($env:ORBIT_PROGRESS) { try { [IO.File]::AppendAllText($env:ORBIT_PROGRESS, $m + "`r`n", $script:Utf8NoBom) } catch {} }
}
function Add-KeyLine([string]$file, [string]$pub) {
    $dir = Split-Path $file; Ensure-Dir $dir
    $lines = @(); if (Test-Path $file) { $lines = @(Get-Content $file -EA SilentlyContinue) }
    $lines = @($lines | Where-Object { $_ -and $_ -notmatch (' ' + [regex]::Escape($keyComment) + '\s*$') })
    $lines += $pub
    [IO.File]::WriteAllText($file, (($lines -join "`n") + "`n"), $script:Utf8NoBom)
}
function Remove-KeyLine([string]$file) {
    if (-not (Test-Path $file)) { return }
    $keep = @(Get-Content $file -EA SilentlyContinue | Where-Object { $_ -and $_ -notmatch (' ' + [regex]::Escape($keyComment) + '\s*$') })
    if ($keep.Count) { [IO.File]::WriteAllText($file, (($keep -join "`n") + "`n"), $script:Utf8NoBom) } else { Remove-Item $file -Force }
}
function Set-SshdConfig([string[]]$wanted) {
    $lines = @(); if (Test-Path $sshdConfig) { $lines = @(Get-Content $sshdConfig | Where-Object { $_ -notlike "*$tag*" }) }
    $new = @($wanted | ForEach-Object { "$_ $tag" }) + $lines
    [IO.File]::WriteAllText($sshdConfig, (($new -join "`r`n") + "`r`n"), $script:Utf8NoBom)
}

function Install-Secondary {
    param([string]$Code, [string]$Name)
    if (-not (Is-Admin)) { throw 'Setup needs administrator rights.' }
    Ensure-Dir $stateDir
    $state = @{ role='secondary'; name=$Name; user=$env:USERNAME; host=$env:COMPUTERNAME; vncPort=$script:VncPort; installedSsh=$false; installedVnc=$false; fw=$false; when=(Get-Date).ToString('s') }
    if (Test-Path $stateFile) { try { $old = Get-Content $stateFile -Raw | ConvertFrom-Json; foreach ($k in 'installedSsh','installedVnc','fw') { if ($old.$k) { $state[$k] = $true } } } catch {} }

    Write-Progress-Line 'STATUS Setting up the command channel...'
    $svc = Get-Service sshd -EA SilentlyContinue
    if (-not $svc) {
        Write-Progress-Line 'LOG - Downloading OpenSSH Server (needs internet once)...'
        $cap = Get-WindowsCapability -Online | Where-Object Name -like 'OpenSSH.Server*' | Select-Object -First 1
        if (-not $cap) { throw 'This Windows edition has no OpenSSH Server.' }
        if ($cap.State -ne 'Installed') { Add-WindowsCapability -Online -Name $cap.Name | Out-Null; $state.installedSsh = $true }
        $svc = Get-Service sshd -EA SilentlyContinue
        if (-not $svc) { throw 'OpenSSH installed but the service is missing - restart and try again.' }
    }
    Set-Service sshd -StartupType Automatic
    if ((Get-Service sshd).Status -ne 'Running') { Start-Service sshd }
    $t0 = Get-Date; while (-not (Test-Path $sshdConfig) -and ((Get-Date)-$t0).TotalSeconds -lt 30) { Start-Sleep -Milliseconds 500 }
    if (-not (Test-Path $sshdConfig)) { throw 'The sshd config file was not created.' }

    Write-Progress-Line 'LOG - Registering the main computer key (one-way - no key goes to this computer)'
    Add-KeyLine $adminKeys $script:PubKey
    & icacls.exe $adminKeys /inheritance:r /grant '*S-1-5-32-544:F' /grant '*S-1-5-18:F' | Out-Null
    Add-KeyLine (Join-Path $env:USERPROFILE '.ssh\authorized_keys') $script:PubKey
    $bp = ($bannerFile -replace '\\','/')
    Set-SshdConfig @("Banner $bp", 'PubkeyAuthentication yes', 'PasswordAuthentication no')
    [IO.File]::WriteAllText($bannerFile, "orbit|1|secondary|$Name|$($env:USERNAME)|$($script:VncPort)`n", $script:Utf8NoBom)
    $reg = 'HKLM:\SOFTWARE\OpenSSH'; if (-not (Test-Path $reg)) { New-Item $reg -Force | Out-Null }
    New-ItemProperty $reg -Name DefaultShell -Value (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') -PropertyType String -Force | Out-Null

    if (-not (Get-NetFirewallRule -Name $fwSsh -EA SilentlyContinue)) {
        New-NetFirewallRule -Name $fwSsh -DisplayName 'Orbit (SSH)' -Direction Inbound -Protocol TCP -LocalPort 22 -Action Allow -Profile Private,Domain | Out-Null
        $state.fw = $true
    }
    Restart-Service sshd

    Write-Progress-Line 'STATUS Setting up the screen channel (computer use)...'
    Install-Vnc -Code $Code -State $state

    Save-State $state
    Write-Progress-Line 'STATUS Ready - waiting for the main computer'
    Write-Progress-Line 'READY'
}

function Install-Vnc {
    param([string]$Code, $State)
    $tvnserver = Join-Path ${env:ProgramFiles} 'TightVNC\tvnserver.exe'
    if (-not (Test-Path $tvnserver)) {
        Write-Progress-Line 'LOG - Downloading the small screen server (~2 MB)...'
        $msi = Join-Path $env:TEMP 'orbit-tvnc.msi'
        $done = $false
        foreach ($u in $script:TightUrl) { try { Invoke-WebRequest $u -OutFile $msi -UseBasicParsing -TimeoutSec 90; if ((Get-Item $msi).Length -gt 500000) { $done = $true; break } } catch {} }
        if (-not $done) { throw 'Could not download the screen server - make sure this computer has internet once.' }
        $p = @(
            '/i', $msi, '/quiet', '/norestart', 'ADDLOCAL=Server',
            'SERVER_REGISTER_AS_SERVICE=1', 'SERVER_ADD_FIREWALL_EXCEPTION=0', 'SERVER_ALLOW_SAS=1',
            'SET_USECONTROLAUTHENTICATION=1','VALUE_OF_USECONTROLAUTHENTICATION=1',
            'SET_CONTROLPASSWORD=1',"VALUE_OF_CONTROLPASSWORD=$Code",
            'SET_USEVNCAUTHENTICATION=1','VALUE_OF_USEVNCAUTHENTICATION=1',
            'SET_PASSWORD=1',"VALUE_OF_PASSWORD=$Code",
            'SET_LOOPBACKONLY=1','VALUE_OF_LOOPBACKONLY=1',
            'SET_ALLOWLOOPBACK=1','VALUE_OF_ALLOWLOOPBACK=1',
            'SET_RFBPORT=1',"VALUE_OF_RFBPORT=$($script:VncPort)"
        )
        $r = Start-Process msiexec.exe -ArgumentList $p -Wait -PassThru
        if ($r.ExitCode -ne 0) { throw "Screen server install failed (msiexec $($r.ExitCode))." }
        $State.installedVnc = $true
    } else {
        # already installed - just set password, port and loopback via the registry
        $reg = 'HKLM:\SOFTWARE\TightVNC\Server'
        Ensure-Reg $reg
        Set-ItemProperty $reg -Name 'RfbPort' -Value $script:VncPort -Type DWord
        Set-ItemProperty $reg -Name 'LoopbackOnly' -Value 1 -Type DWord
        Set-ItemProperty $reg -Name 'UseControlAuthentication' -Value 1 -Type DWord
        Set-TightPassword -Reg $reg -Code $Code
        try { Restart-Service tvnserver -EA SilentlyContinue } catch {}
    }
    Set-Service tvnserver -StartupType Automatic -EA SilentlyContinue
    if ((Get-Service tvnserver -EA SilentlyContinue).Status -ne 'Running') { Start-Service tvnserver -EA SilentlyContinue }
}
function Ensure-Reg([string]$p) { if (-not (Test-Path $p)) { New-Item $p -Force | Out-Null } }
function Set-TightPassword { param([string]$Reg, [string]$Code)
    # TightVNC stores the password as fixed-key DES; the msi does it, so we leave it to the msi. Fallback only.
}

function Save-State($s) { Ensure-Dir $stateDir; [IO.File]::WriteAllText($stateFile, ($s | ConvertTo-Json), $script:Utf8NoBom) }

function Remove-Secondary {
    if (-not (Is-Admin)) { throw 'Removal needs administrator rights.' }
    $state = $null; if (Test-Path $stateFile) { try { $state = Get-Content $stateFile -Raw | ConvertFrom-Json } catch {} }
    Write-Progress-Line 'STATUS Removing the command channel...'
    Remove-KeyLine $adminKeys
    Remove-KeyLine (Join-Path $env:USERPROFILE '.ssh\authorized_keys')
    if (Test-Path $sshdConfig) { $keep = @(Get-Content $sshdConfig | Where-Object { $_ -notlike "*$tag*" }); [IO.File]::WriteAllText($sshdConfig, (($keep -join "`r`n") + "`r`n"), $script:Utf8NoBom) }
    Remove-Item $bannerFile -Force -EA SilentlyContinue
    Remove-NetFirewallRule -Name $fwSsh -EA SilentlyContinue
    if (Get-Service sshd -EA SilentlyContinue) {
        Stop-Service sshd -Force -EA SilentlyContinue; Set-Service sshd -StartupType Disabled -EA SilentlyContinue
        if ($state -and $state.installedSsh) { $c = Get-WindowsCapability -Online | Where-Object { $_.Name -like 'OpenSSH.Server*' -and $_.State -eq 'Installed' } | Select-Object -First 1; if ($c) { Remove-WindowsCapability -Online -Name $c.Name | Out-Null } }
    }
    Write-Progress-Line 'STATUS Removing the screen channel...'
    if ($state -and $state.installedVnc) {
        $app = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*','HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*' -EA SilentlyContinue | Where-Object DisplayName -like 'TightVNC*' | Select-Object -First 1
        if ($app -and $app.PSChildName -match '^\{') { Start-Process msiexec.exe -ArgumentList "/x $($app.PSChildName) /quiet /norestart" -Wait }
    } else { try { Stop-Service tvnserver -Force -EA SilentlyContinue } catch {} }
    Remove-Item $stateDir -Recurse -Force -EA SilentlyContinue
    Write-Progress-Line 'READY'
}

# -- Main computer: pair a device (Install-Self/Ensure-Vncdotool live in orbit-common) --
function Pair-Device {
    param($Banner, [string]$Code)
    $dev = [pscustomobject]@{ name=$Banner.Name; alias=("orbit-" + ($Banner.Name -replace '\s','-')); ip=$Banner.IP; user=$Banner.User; sshPort=$Banner.SshPort; vncPort=$Banner.VncPort; code=(Protect-Secret $Code) }
    $ping = Invoke-DeviceSsh $dev 'echo ok' 12000
    if ($ping.Out -notmatch 'ok') { throw "Cant log in with the key - make sure it was set up as the other computer." }
    $t = Open-Tunnel $dev; if (-not $t) { throw 'Could not open the screen tunnel.' }
    try {
        $py = (Get-Command python -EA SilentlyContinue).Source
        $env:ORBIT_VNC_PW = $Code
        $r = Invoke-Native $py @((Join-Path $PSScriptRoot 'orbit-vnc.py'),'probe',"127.0.0.1::$($t.Port)") 25000
        $env:ORBIT_VNC_PW = $null
        if ($r.Code -ne 0) { throw "Wrong code, or the screen channel is not running: $($r.Err.Trim())" }
    } finally { $env:ORBIT_VNC_PW = $null; Close-Tunnel $t }
    Set-Device $dev
    $dev
}

# ====================== Elevated modes (headless) ======================
if ($env:ORBIT_AUTOROLE) {
    try {
        switch ($env:ORBIT_AUTOROLE) {
            'secondary' { Install-Secondary -Code $env:ORBIT_CODE -Name $env:ORBIT_NAME }
            'remove'    { Remove-Secondary }
        }
    } catch { Write-Progress-Line "FAIL $($_.Exception.Message)" }
    return
}

# ====================== UI ======================
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase, System.Drawing, System.Windows.Forms
$dwm = @'
using System;using System.Runtime.InteropServices;
public static class OrbitDwm{
 [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h,int a,ref int v,int s);
 public static void Glass(IntPtr h,int b){int d=1;DwmSetWindowAttribute(h,20,ref d,4);int r=2;DwmSetWindowAttribute(h,33,ref r,4);DwmSetWindowAttribute(h,38,ref b,4);}}
'@
if (-not ('OrbitDwm' -as [type])) { Add-Type -TypeDefinition $dwm }

$xamlPath = Join-Path $PSScriptRoot 'orbit.xaml'
$xaml = [IO.File]::ReadAllText($xamlPath, [Text.Encoding]::UTF8)
$win = [Windows.Markup.XamlReader]::Load((New-Object Xml.XmlNodeReader ([xml]$xaml)))
$fontDir = Join-Path $PSScriptRoot 'fonts'
if (Test-Path (Join-Path $fontDir 'Cairo-400.ttf')) { $win.FontFamily = New-Object Windows.Media.FontFamily((New-Object Uri (($fontDir.TrimEnd('\')+'\'))), './#Cairo') }

# elements
$g = {param($n) $win.FindName($n)}
$scRole=&$g scRole; $scSecondary=&$g scSecondary; $scPrimary=&$g scPrimary; $btnBack=&$g btnBack
$stConnected=&$g stConnected; $stNeedCode=&$g stNeedCode; $lblCode=&$g lblCode; $lblSecStatus=&$g lblSecStatus
$cmb=&$g cmbDevices; $lblFound=&$g lblFound; $txtCode=&$g txtCode; $lblConnName=&$g lblConnName
$script:CurCode=$null; $script:Banners=@()

function Show([string]$n){
  foreach($s in $scRole,$scSecondary,$scPrimary){ $s.Visibility='Collapsed' }
  $btnBack.Visibility = $(if($n -eq 'role'){'Collapsed'}else{'Visible'})
  switch($n){ 'role'{$scRole.Visibility='Visible'} 'secondary'{$scSecondary.Visibility='Visible'} 'primary'{$scPrimary.Visibility='Visible'} }
}
function UI([scriptblock]$b){ $win.Dispatcher.Invoke($b) }

# -- Other computer: make a code, show it, run elevated setup, follow progress --
function Start-Secondary {
  $script:CurCode = New-OrbitCode
  $lblCode.Text = Format-Code $script:CurCode
  $lblSecStatus.Text = 'Starting setup...'
  Show 'secondary'
  $name = (&$g txtName).Text; if (-not $name) { $name = $env:COMPUTERNAME }
  $prog = Join-Path $env:TEMP ("orbit-setup-{0}.log" -f ([guid]::NewGuid().ToString('N').Substring(0,8)))
  '' | Set-Content $prog
  $self = Join-Path $PSScriptRoot 'orbit.ps1'
  $psi = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$self`""
  $p = Start-Process powershell.exe -Verb RunAs -WindowStyle Hidden -PassThru -ArgumentList $psi -Environment @{ ORBIT_AUTOROLE='secondary'; ORBIT_CODE=$script:CurCode; ORBIT_NAME=$name; ORBIT_PROGRESS=$prog } 2>$null
  Watch-Progress $prog $p
}
# follow the progress file from the elevated process
function Watch-Progress([string]$file, $proc) {
  $script:progPos = 0
  $timer = New-Object Windows.Threading.DispatcherTimer; $timer.Interval = [TimeSpan]::FromMilliseconds(300)
  $timer.Add_Tick({
    if (Test-Path $file) {
      $all = Get-Content $file -EA SilentlyContinue
      for ($i=$script:progPos; $i -lt $all.Count; $i++) {
        $line = $all[$i]
        if ($line -like 'STATUS *') { $lblSecStatus.Text = $line.Substring(7) }
        elseif ($line -like 'READY*') { $lblSecStatus.Text = 'Ready - waiting for the main computer'; $timer.Stop() }
        elseif ($line -like 'FAIL *') { $lblSecStatus.Text = '⚠ ' + $line.Substring(5); $timer.Stop() }
      }
      $script:progPos = $all.Count
    }
    if ($proc -and $proc.HasExited -and $script:progPos -gt 0) { } # keep going until READY/FAIL
  })
  $timer.Start()
}

# -- Main computer: prepare, scan the network, fill the list --
function Start-Primary {
  Show 'primary'; $lblFound.Text='Setting up this computer...'; $cmb.Items.Clear()
  $rs=[runspacefactory]::CreateRunspace(); $rs.ApartmentState='MTA'; $rs.Open()
  $rs.SessionStateProxy.SetVariable('root',$PSScriptRoot)
  $ps=[powershell]::Create(); $ps.Runspace=$rs
  $ps.AddScript({ param($root)
    . (Join-Path $root 'orbit-common.ps1')
    try { Install-Self $root } catch {}
    try { Ensure-Vncdotool | Out-Null } catch {}
    $found=@(); foreach($ip in (Find-OpenSsh)){ $b=Read-OrbitBanner $ip; if($b -and $b.Role -eq 'secondary'){ $found+=$b } }
    ,$found
  }).AddArgument($PSScriptRoot) | Out-Null
  $async=$ps.BeginInvoke()
  $timer=New-Object Windows.Threading.DispatcherTimer; $timer.Interval=[TimeSpan]::FromMilliseconds(400)
  $timer.Add_Tick({
    if($async.IsCompleted){
      $timer.Stop()
      try { $res=@($ps.EndInvoke($async)) } catch { $res=@() }
      $ps.Dispose(); $rs.Dispose()
      $script:Banners=@($res)
      $cmb.Items.Clear()
      if($script:Banners.Count -eq 0){ $lblFound.Text='No other computers found on the network'; }
      else{
        $lblFound.Text = "Found $($script:Banners.Count) computer(s) on the network"
        foreach($b in $script:Banners){
          $paired = [bool](Get-Device $b.Name)
          $it=New-Object Windows.Controls.ComboBoxItem
          $sp=New-Object Windows.Controls.StackPanel; $sp.Orientation='Horizontal'
          $dot=New-Object Windows.Shapes.Ellipse; $dot.Width=9;$dot.Height=9;$dot.VerticalAlignment='Center'
          $dot.Fill=(New-Object Windows.Media.SolidColorBrush ([Windows.Media.Color]::FromRgb($(if($paired){0x22}else{0xF0}),$(if($paired){0xD3}else{0xB0}),$(if($paired){0xB0}else{0x3C}))))
          $t1=New-Object Windows.Controls.TextBlock; $t1.Text=$b.Name; $t1.Margin='10,0,0,0'; $t1.FontSize=15
          $t2=New-Object Windows.Controls.TextBlock; $t2.Text=$(if($paired){'Connected'}else{'Needs code'}); $t2.FontSize=11.5; $t2.Margin='10,0,0,0'; $t2.VerticalAlignment='Center'
          $t2.Foreground=(New-Object Windows.Media.SolidColorBrush ([Windows.Media.Color]::FromRgb($(if($paired){0x22}else{0xF0}),$(if($paired){0xD3}else{0xB0}),$(if($paired){0xB0}else{0x3C}))))
          [void]$sp.Children.Add($dot);[void]$sp.Children.Add($t1);[void]$sp.Children.Add($t2)
          $it.Content=$sp; $it.Tag=$b; [void]$cmb.Items.Add($it)
        }
        $cmb.SelectedIndex=0; Update-DeviceCard
      }
    }
  }); $timer.Start()
}
function Update-DeviceCard {
  if($cmb.SelectedItem -eq $null){ return }
  $b=$cmb.SelectedItem.Tag
  if(Get-Device $b.Name){ $stConnected.Visibility='Visible'; $stNeedCode.Visibility='Collapsed'; (&$g lblConnName).Text=$b.Name }
  else { $stConnected.Visibility='Collapsed'; $stNeedCode.Visibility='Visible'; $txtCode.Text='' }
}
function Do-Connect {
  $b=$cmb.SelectedItem.Tag; $code=($txtCode.Text -replace '[^A-Za-z0-9]','').ToUpper()
  if($code.Length -ne 8){ (&$g lblConnErr).Text='The code is 8 letters/digits.'; return }
  (&$g lblConnErr).Text='Connecting...'
  try { Pair-Device $b $code | Out-Null; Update-DeviceCard; (&$g lblConnErr).Text='' }
  catch { (&$g lblConnErr).Text = '⚠ ' + $_.Exception.Message }
}

(&$g btnPrimary).Add_Click({ Start-Primary })
(&$g btnSecondary).Add_Click({ Start-Secondary })
$btnBack.Add_Click({ Show 'role' })
(&$g btnClose).Add_Click({ $win.Close() })
(&$g btnCopyCode).Add_Click({ if($script:CurCode){ [Windows.Clipboard]::SetText($script:CurCode) } })
$cmb.Add_SelectionChanged({ Update-DeviceCard })
(&$g btnConnect).Add_Click({ Do-Connect })
$win.Add_MouseLeftButtonDown({ if($_.ButtonState -eq 'Pressed'){ try{$win.DragMove()}catch{} } })
$win.Add_SourceInitialized({ try{ [OrbitDwm]::Glass((New-Object Windows.Interop.WindowInteropHelper $win).Handle,3) }catch{} })

Show 'role'
$win.ShowDialog() | Out-Null
