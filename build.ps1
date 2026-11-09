# 轻桥 2.0 构建：生成 dist\LanBridge.exe（单个文件，系统自带 .NET Framework 4.8 即可运行）
# 需要 .NET SDK（8 及以上），第一次会从 NuGet 下载 WebView2 和 .NET 4.8 引用包。
param([string]$Out = 'dist')
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
# 中转安装脚本里带着 relay.mjs，先同步一下。
if (Get-Command node -ErrorAction SilentlyContinue) { node relay/make-install.mjs }
dotnet build LanBridge.csproj -c Release -nologo -v q
if ($LASTEXITCODE -ne 0) { throw '编译失败' }
New-Item -ItemType Directory -Force $Out | Out-Null
# 只留一个最新版：正在运行的轻桥先退出（它占着文件），换完再按原来的方式启动。
$exe = (Resolve-Path $Out).Path + '\LanBridge.exe'
$running = Get-CimInstance Win32_Process | Where-Object { $_.Name -like 'LanBridge*.exe' -and $_.ExecutablePath -eq $exe }
foreach ($p in $running) { Stop-Process -Id $p.ProcessId -Force }
if ($running) { Start-Sleep -Milliseconds 1200 }
# 刚退出的轻桥有时还占着文件一小会儿：多试几次，免得换不上又没重启。
foreach ($i in 1..20) { try { Copy-Item bin\Release\LanBridge.exe $exe -Force -ErrorAction Stop; break } catch { if ($i -eq 20) { if ($running) { Start-Process $exe -ArgumentList '--background' }; throw } ; Start-Sleep -Milliseconds 500 } }
# 编译中间目录里的副本删掉，免得 Windows 搜索里出现好几个“LanBridge.exe”。
Remove-Item bin\Release\LanBridge.exe, obj\Release\LanBridge.exe -Force -ErrorAction SilentlyContinue
Get-ChildItem $Out -Filter 'LanBridge*.exe' | Where-Object { $_.Name -notin 'LanBridge.exe', 'LanBridgeRelay.exe' } | Remove-Item -Force
if ($running) { Start-Process $exe -ArgumentList '--background' }
# 服务器版（只有常驻中转 + 小云，约 75 KB）：装到云服务器用，见 relay/README.md。
dotnet build relay/server/LanBridgeRelay.csproj -c Release -nologo -v q
if ($LASTEXITCODE -ne 0) { throw '服务器版编译失败' }
Copy-Item relay\server\bin\Release\LanBridgeRelay.exe "$Out\LanBridgeRelay.exe" -Force
$sums = foreach ($n in 'LanBridge.exe', 'LanBridgeRelay.exe') { (Get-FileHash "$Out\$n" -Algorithm SHA256).Hash.ToLowerInvariant() + "  $n" }
$sums | Out-File "$Out\SHA256SUMS.txt" -Encoding ascii
Write-Output "已生成 $Out\LanBridge.exe  ($([math]::Round((Get-Item "$Out\LanBridge.exe").Length/1KB)) KB)"
