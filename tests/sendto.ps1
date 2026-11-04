param([string]$Executable = "$PSScriptRoot\..\dist\LanBridge-v1.1.0.exe")
$ErrorActionPreference = 'Stop'
$taskLinkFolder = Join-Path $PSScriptRoot 'runtime-sendto'
New-Item -ItemType Directory -Path $taskLinkFolder -Force | Out-Null
$taskExecutable = (Resolve-Path -LiteralPath $Executable).Path
[void][Reflection.Assembly]::LoadFrom($taskExecutable)
[LanBridge.DesktopCommands]::InstallSendTo($taskExecutable,49329,$taskLinkFolder)
$taskShell = New-Object -ComObject WScript.Shell
$taskLink = $taskShell.CreateShortcut((Join-Path $taskLinkFolder '轻桥 · 发到 iPhone.lnk'))
try {
 if ($taskLink.TargetPath -ne $taskExecutable) { throw '快捷方式目标错误' }
 if ($taskLink.Arguments -ne '--port 49329 --send') { throw '快捷方式参数错误' }
 Write-Output 'PASS SendTo shortcut target and arguments in isolated workspace folder; real SendTo unchanged.'
} finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($taskLink); [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($taskShell) }
