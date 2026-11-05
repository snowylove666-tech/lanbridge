# 轻桥常驻中转 + 小云：装到 Windows 服务器（阿里云等）。和电脑上的轻桥是同一个 LanBridge.exe，用 --relay-server 只当中转。
# 不开窗口、不碰剪贴板，约 30 MB 内存；开机自动运行（计划任务，LOCAL SERVICE 身份，低优先级）。
#
# 用法（管理员 PowerShell）：
#   powershell -ExecutionPolicy Bypass -File install-windows.ps1 -Url https://github.com/snowylove666-tech/lanbridge/releases/download/v2.2.0/LanBridge.exe -Sha256 <哈希>
#   powershell -ExecutionPolicy Bypass -File install-windows.ps1 -Exe C:\路径\LanBridge.exe
# 选项：-Port 49330  -PoolMB 1024（小云大小，0 关掉）  -MaxMB 300（单次上限）  -Uninstall
param(
  [string]$Url, [string]$Exe, [string]$Sha256,
  [int]$Port = 49330, [int]$PoolMB = 1024, [int]$MaxMB = 300,
  [switch]$Uninstall
)
$ErrorActionPreference = 'Stop'
$Dir = Join-Path $env:ProgramData 'LanBridgeRelay'
$Task = 'LanBridgeRelay'
$Rule = 'LanBridge relay TCP ' + $Port

if ($Uninstall) {
  Stop-ScheduledTask -TaskName $Task -ErrorAction SilentlyContinue
  Unregister-ScheduledTask -TaskName $Task -Confirm:$false -ErrorAction SilentlyContinue
  Get-Process LanBridge -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$Dir*" } | Stop-Process -Force
  Remove-NetFirewallRule -DisplayName $Rule -ErrorAction SilentlyContinue
  Remove-Item $Dir -Recurse -Force -ErrorAction SilentlyContinue
  'LANBRIDGE_RELAY_UNINSTALLED'; exit 0
}

New-Item -ItemType Directory -Force $Dir, (Join-Path $Dir 'pool') | Out-Null
$target = Join-Path $Dir 'LanBridge.exe'
$new = Join-Path $Dir 'LanBridge.new'
if ($Url) {
  [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
  Invoke-WebRequest -Uri $Url -OutFile $new -UseBasicParsing
} elseif ($Exe) { Copy-Item $Exe $new -Force }
elseif (-not (Test-Path $target)) { throw '要给 -Url 或 -Exe' }
if (Test-Path $new) {
  if ($Sha256 -and (Get-FileHash $new -Algorithm SHA256).Hash -ne $Sha256.ToUpperInvariant()) { Remove-Item $new -Force; throw '下载的文件校验不对' }
  Stop-ScheduledTask -TaskName $Task -ErrorAction SilentlyContinue
  Get-Process LanBridge -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $target } | Stop-Process -Force
  Start-Sleep -Milliseconds 800
  Move-Item $new $target -Force
}

# LOCAL SERVICE 只需要能写这个目录（小云文件、日志）。
icacls $Dir /grant '*S-1-5-19:(OI)(CI)M' /T /Q | Out-Null

$argLine = "--relay-server --port $Port --pool `"$Dir\pool`" --pool-mb $PoolMB --relay-max-mb $MaxMB --log `"$Dir`""
$action = New-ScheduledTaskAction -Execute $target -Argument $argLine -WorkingDirectory $Dir
$trigger = New-ScheduledTaskTrigger -AtStartup
$principal = New-ScheduledTaskPrincipal -UserId 'NT AUTHORITY\LOCAL SERVICE' -LogonType ServiceAccount
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1) -Priority 7 -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable
Register-ScheduledTask -TaskName $Task -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null

if (-not (Get-NetFirewallRule -DisplayName $Rule -ErrorAction SilentlyContinue)) {
  New-NetFirewallRule -DisplayName $Rule -Direction Inbound -Protocol TCP -LocalPort $Port -Action Allow -Program $target | Out-Null
}
Start-ScheduledTask -TaskName $Task

$ok = $false
foreach ($i in 1..40) {
  Start-Sleep -Milliseconds 500
  try { $h = Invoke-RestMethod "http://127.0.0.1:$Port/health" -TimeoutSec 3; if ($h.app -eq 'LanBridge relay') { $ok = $true; break } } catch { }
}
if (-not $ok) { Get-Content (Join-Path $Dir 'lanbridge.log') -Tail 20 -ErrorAction SilentlyContinue; throw '中转没起来（上面是日志）' }
$p = Get-Process LanBridge -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $target } | Select-Object -First 1
'LANBRIDGE_RELAY_OK version=' + $h.ver + ' port=' + $Port + ' poolMB=' + $h.pool.capMB + ' memMB=' + [math]::Round($p.WorkingSet64 / 1MB)
'还要在云服务器控制台的安全组里放行 TCP ' + $Port + '（入方向）。然后在轻桥里填 http://服务器公网IP:' + $Port
