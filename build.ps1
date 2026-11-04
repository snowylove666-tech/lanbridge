# 轻桥 2.0 构建：生成 dist\LanBridge.exe（单个文件，系统自带 .NET Framework 4.8 即可运行）
# 需要 .NET SDK（8 及以上），第一次会从 NuGet 下载 WebView2 和 .NET 4.8 引用包。
param([string]$Out = 'dist')
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
dotnet build LanBridge.csproj -c Release -nologo -v q
if ($LASTEXITCODE -ne 0) { throw '编译失败' }
New-Item -ItemType Directory -Force $Out | Out-Null
Copy-Item bin\Release\LanBridge.exe "$Out\LanBridge.exe" -Force
$hash = (Get-FileHash "$Out\LanBridge.exe" -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  LanBridge.exe" | Out-File "$Out\SHA256SUMS.txt" -Encoding ascii
Write-Output "已生成 $Out\LanBridge.exe  ($([math]::Round((Get-Item "$Out\LanBridge.exe").Length/1KB)) KB)"
