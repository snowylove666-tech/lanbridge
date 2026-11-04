param([string]$Executable,[string]$ReadyFile)
$ErrorActionPreference = 'Stop'
$bridgeAssembly = [Reflection.Assembly]::LoadFrom($Executable)
$bridgeChildInfo = [Diagnostics.ProcessStartInfo]::new('powershell.exe','-NoProfile -NonInteractive -Command Start-Sleep -Seconds 60')
$bridgeChildInfo.UseShellExecute = $false
$bridgeChildInfo.CreateNoWindow = $true
$bridgeChildInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
$bridgeChild = [Diagnostics.Process]::Start($bridgeChildInfo)
$bridgeJobType = $bridgeAssembly.GetType('LanBridge.ChildJob',$true)
$bridgeJob = [Activator]::CreateInstance($bridgeJobType,[object[]]@($bridgeChild))
try {
 [IO.File]::WriteAllText($ReadyFile,[string]$bridgeChild.Id)
 Start-Sleep -Seconds 60
} finally {
 $bridgeJob.Dispose()
 $bridgeChild.Dispose()
}
