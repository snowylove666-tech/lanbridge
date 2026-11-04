$ErrorActionPreference = 'Stop'
$runPath = Join-Path $PSScriptRoot ('runtime-attachment-storage-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runPath | Out-Null
$testExe = Join-Path $runPath 'AttachmentStorageTest.exe'
$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compilerPath /nologo /target:exe /codepage:65001 /utf8output /r:System.Drawing.dll /r:System.Windows.Forms.dll "/out:$testExe" (Join-Path $PSScriptRoot '../src/ClipboardAttachmentStorage.cs') (Join-Path $PSScriptRoot 'ClipboardAttachmentTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Attachment storage test build failed' }
& $testExe $runPath
if ($LASTEXITCODE -ne 0) { throw 'Attachment storage test failed' }
