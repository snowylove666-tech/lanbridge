$ErrorActionPreference = 'Stop'
$testRoot = $PSScriptRoot
$runPath = Join-Path $testRoot ('runtime-capture-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runPath | Out-Null
$captureExe = Join-Path $runPath 'CaptureTest.exe'
$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compilerPath /nologo /target:exe /codepage:65001 /utf8output "/out:$captureExe" (Join-Path $testRoot '../src/ClipboardCapture.cs') (Join-Path $testRoot 'ClipboardCaptureTest.cs')
if ($LASTEXITCODE -ne 0) { throw 'Capture test build failed' }
& $captureExe
if ($LASTEXITCODE -ne 0) { throw 'Capture test failed' }
