$ErrorActionPreference = 'Stop'
$invitationRoot = $PSScriptRoot
$runPath = Join-Path $invitationRoot ('runtime-invitation-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runPath | Out-Null
$testExe = Join-Path $runPath 'InvitationTests.exe'
$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compilerPath /nologo /target:exe /codepage:65001 /utf8output "/out:$testExe" (Join-Path $invitationRoot '../src/ConnectionInvitation.cs') (Join-Path $invitationRoot 'invitation-tests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Invitation test build failed' }
& $testExe
if ($LASTEXITCODE -ne 0) { throw 'Invitation tests failed' }
