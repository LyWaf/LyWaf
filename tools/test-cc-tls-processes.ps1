$ErrorActionPreference = 'Stop'
$buildRoot = Join-Path ([IO.Path]::GetTempPath()) ('lywaf-cc-tls-build-' + [guid]::NewGuid().ToString('N'))
Write-Host "Isolated build: $buildRoot"
dotnet build (Join-Path $PSScriptRoot '../benchmarks/CcTlsProcessHarness/CcTlsProcessHarness.csproj') --artifacts-path $buildRoot --nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw 'TLS harness build failed' }
dotnet (Join-Path $buildRoot 'bin/CcTlsProcessHarness/debug/CcTlsProcessHarness.dll')
if ($LASTEXITCODE -ne 0) { throw 'TLS process verification failed' }
