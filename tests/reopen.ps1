# 打开窗口 → 关掉 → 再打开（两轮），看是否每次都是独立窗口、日志里没有 WebView2 出错
param([string]$Exe)
$ErrorActionPreference = 'Stop'
Add-Type @"
using System; using System.Runtime.InteropServices; using System.Text;
public static class W {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc f, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  public static IntPtr Find(uint pid) {
    IntPtr found = IntPtr.Zero;
    EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p == pid && IsWindowVisible(h)) { var sb = new StringBuilder(64); GetWindowText(h, sb, 64); if (sb.ToString() == "轻桥") { found = h; return false; } } return true; }, IntPtr.Zero);
    return found;
  }
}
"@
$data = Join-Path $env:TEMP ("lb-reopen-" + [guid]::NewGuid().ToString('N').Substring(0,6))
$port = 49480
$args1 = @('--data', $data, '--port', $port, '--loopback', '--fake-clipboard', '--name', '窗口测试')
$p = Start-Process $Exe -ArgumentList $args1 -PassThru
function WaitWin([int]$ms) { $end = (Get-Date).AddMilliseconds($ms); while ((Get-Date) -lt $end) { $h = [W]::Find([uint32]$p.Id); if ($h -ne [IntPtr]::Zero) { return $h }; Start-Sleep -Milliseconds 200 }; return [IntPtr]::Zero }
function WaitGone([int]$ms) { $end = (Get-Date).AddMilliseconds($ms); while ((Get-Date) -lt $end) { if ([W]::Find([uint32]$p.Id) -eq [IntPtr]::Zero) { return $true }; Start-Sleep -Milliseconds 200 }; return $false }
$results = @()
try {
  $h = WaitWin 15000
  $results += "第 1 次打开：" + ($(if ($h -ne [IntPtr]::Zero) { '独立窗口 ✓' } else { '没有窗口 ✗' }))
  Start-Sleep -Seconds 3
  for ($i = 2; $i -le 3; $i++) {
    [void][W]::PostMessage($h, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)   # WM_CLOSE
    $gone = WaitGone 5000
    Start-Sleep -Milliseconds 800
    Start-Process $Exe -ArgumentList $args1 -Wait   # 第二次启动 = 托盘“打开”，把命令转给正在运行的那个
    $h = WaitWin 15000
    Start-Sleep -Seconds 3
    $h2 = [W]::Find([uint32]$p.Id)
    $results += "关掉后第 $i 次打开：" + ($(if ($gone -and $h2 -ne [IntPtr]::Zero) { '独立窗口 ✓' } else { "✗ (关掉=$gone 窗口=$h2)" }))
    $h = $h2
  }
  $log = Get-Content (Join-Path $data 'lanbridge.log') -Encoding UTF8
  $bad = $log | Where-Object { $_ -like '*WebView2*' }
  $results += "日志里 WebView2 出错：" + $(if ($bad) { "$($bad.Count) 条 ✗：" + ($bad -join ' | ') } else { '0 条 ✓' })
} finally {
  try { Stop-Process -Id $p.Id -Force } catch { }
}
$results
