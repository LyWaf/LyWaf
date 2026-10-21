$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '../benchmarks/NodeProcessHarness/NodeProcessHarness.csproj'
$buildRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('lywaf-transport-build-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $buildRoot | Out-Null
Write-Host "Isolated build artifacts: $buildRoot"
$repo = Split-Path $PSScriptRoot -Parent
$sources = Get-ChildItem -Path (Join-Path $repo 'Services/RuntimeState'), (Join-Path $repo 'Services/NodeCommunication') -Filter '*.cs' -File | Sort-Object FullName
$before = $sources | Get-FileHash -Algorithm SHA256 | Select-Object Path, Hash | ConvertTo-Json
$before | Set-Content -LiteralPath (Join-Path $buildRoot 'service-source-hashes.json')
dotnet build $project --artifacts-path $buildRoot --nologo -v minimal 2>&1 | Tee-Object -FilePath (Join-Path $buildRoot 'build.log')
if ($LASTEXITCODE -ne 0) { throw "Harness build failed: $LASTEXITCODE" }
$after = $sources | Get-FileHash -Algorithm SHA256 | Select-Object Path, Hash | ConvertTo-Json
if ($before -ne $after) { throw 'Service sources changed during build; rerun after the concurrent edit finishes.' }
$dll = Join-Path $buildRoot 'bin/NodeProcessHarness/debug/NodeProcessHarness.dll'
dotnet $dll transport-suite
if ($LASTEXITCODE -ne 0) { throw "Transport/capacity process verification failed: $LASTEXITCODE; inspect the printed artifacts directory" }
