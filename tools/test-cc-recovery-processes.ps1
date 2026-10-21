param([switch]$ReleaseBootstrap)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$root = Join-Path ([IO.Path]::GetTempPath()) ('lywaf-cc-recovery-build-' + [guid]::NewGuid().ToString('N'))
$snapshot = Join-Path $root 'source'
New-Item -ItemType Directory -Path $snapshot -Force | Out-Null
Write-Host "FIXED BUILD $root"
# Copy build input only, excluding user configuration and runtime state.
$files = & rg --files $repo -g '*.cs' -g '*.csproj' -g 'Directory.Build.*' -g 'Directory.Packages.props' -g 'global.json' -g 'nuget.config' -g 'config.ly.example'
if ($LASTEXITCODE -ne 0) { throw 'Source enumeration failed' }
$files = @($files | Where-Object { $_ -notmatch '[\\/]tests[\\/]|[\\/]benchmarks[\\/](?!CcRecoveryHarness[\\/])' })
$files += @(Get-ChildItem (Join-Path $repo 'Localization'),(Join-Path $repo 'Plugins') -Recurse -File -Filter '*.json' |
    Where-Object { $_.FullName -match '[\\/]Localization[\\/]' } | ForEach-Object FullName)
$files = @($files | Sort-Object -Unique)
function Manifest {
    @($files | ForEach-Object { [pscustomobject]@{ Path = [IO.Path]::GetRelativePath($repo, $_); Hash = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash } })
}
$before = Manifest
foreach ($file in $files) {
    $target = Join-Path $snapshot ([IO.Path]::GetRelativePath($repo, $file))
    New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
    Copy-Item -LiteralPath $file -Destination $target
}
$after = Manifest
if (($before | ConvertTo-Json) -ne ($after | ConvertTo-Json)) { throw 'Concurrent source change; no hosts started' }
foreach ($entry in $before) {
    if ((Get-FileHash -LiteralPath (Join-Path $snapshot $entry.Path) -Algorithm SHA256).Hash -ne $entry.Hash) { throw 'Snapshot mismatch' }
}
$before | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'source-hashes.json')
dotnet build (Join-Path $snapshot 'benchmarks/CcRecoveryHarness/CcRecoveryHarness.csproj') --artifacts-path (Join-Path $root 'build') -v minimal 2>&1 | Tee-Object -FilePath (Join-Path $root 'build.log')
if ($LASTEXITCODE -ne 0) { throw 'Independent build failed' }
$dll = Join-Path $root 'build/bin/CcRecoveryHarness/debug/CcRecoveryHarness.dll'
$run = Join-Path $root 'run'
$runArgs = @($dll, $run)
if ($ReleaseBootstrap) { $runArgs += '--release' }
dotnet @runArgs 2>&1 | Tee-Object -FilePath (Join-Path $root 'execution.log')
if ($LASTEXITCODE -ne 0) { throw "Process matrix failed: $root" }
