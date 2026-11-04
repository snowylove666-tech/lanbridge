param([string]$ExecutableName = 'LanBridge.exe')
$ErrorActionPreference = 'Stop'
$projectDir = $PSScriptRoot
$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $compilerPath)) { throw '需要 Windows 10/11 自带的 .NET Framework 4.8。' }
if ([IO.Path]::GetFileName($ExecutableName) -ne $ExecutableName -or !$ExecutableName.EndsWith('.exe')) { throw '无效的 EXE 名称' }
$outputDir = Join-Path $projectDir 'dist'
New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
$sources = @(Get-ChildItem -LiteralPath (Join-Path $projectDir 'src') -Filter '*.cs' | ForEach-Object FullName)
$resources = @(Get-ChildItem -LiteralPath (Join-Path $projectDir 'web') -File | ForEach-Object { '/resource:{0},Web.{1}' -f $_.FullName,$_.Name })
$iconOptions = @()
if (Test-Path -LiteralPath "$projectDir\LanBridge.ico") { $iconOptions += "/win32icon:$projectDir\LanBridge.ico" }
& $compilerPath /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 /utf8output "/out:$outputDir\$ExecutableName" "/win32manifest:$projectDir\app.manifest" @iconOptions /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll /r:System.Security.dll $resources $sources
if ($LASTEXITCODE -ne 0) { throw '编译失败' }
if (Test-Path -LiteralPath "$projectDir\README.md") { Copy-Item -LiteralPath "$projectDir\README.md" -Destination $outputDir -Force }
Write-Output "已生成 $outputDir\$ExecutableName"
