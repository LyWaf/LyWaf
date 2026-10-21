$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '../benchmarks/NodeProcessHarness/NodeProcessHarness.csproj'
$buildRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('lywaf-process-build-' + [guid]::NewGuid().ToString('N'))
Write-Host "Isolated build artifacts: $buildRoot"
dotnet build $project --artifacts-path $buildRoot --nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw "Harness build failed: $LASTEXITCODE" }
$dll = Join-Path $buildRoot 'bin/NodeProcessHarness/debug/NodeProcessHarness.dll'
dotnet $dll
if ($LASTEXITCODE -ne 0) { throw "Multi-process verification failed: $LASTEXITCODE" }
