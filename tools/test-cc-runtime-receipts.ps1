param([string]$EvidenceName = ('cc-runtime-receipts-' + (Get-Date -Format 'yyyyMMdd-HHmmss')))
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$root = Join-Path ([IO.Path]::GetTempPath()) ('lywaf-cc-receipts-' + [guid]::NewGuid().ToString('N'))
$snapshot = Join-Path $root 'source'
New-Item -ItemType Directory -Path $snapshot | Out-Null
$files = @(& rg --files $repo -g '*.cs' -g '*.csproj' -g 'Directory.Build.*' -g 'Directory.Packages.props' -g 'global.json' -g 'nuget.config' -g 'config.ly.example' |
    Where-Object { $_ -notmatch '[\\/]tests[\\/]|[\\/]benchmarks[\\/](?!CcDecisionHarness[\\/])' })
$files += @(Get-ChildItem (Join-Path $repo 'Localization'),(Join-Path $repo 'Plugins') -Recurse -File -Filter '*.json' |
    Where-Object { $_.FullName -match '[\\/]Localization[\\/]' } | ForEach-Object FullName)
$files = @($files | Sort-Object -Unique)
$manifest = @($files | ForEach-Object { [pscustomobject]@{ Path = [IO.Path]::GetRelativePath($repo, $_); Hash = (Get-FileHash -LiteralPath $_).Hash } })
foreach ($entry in $manifest) {
    $target = Join-Path $snapshot $entry.Path
    New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $repo $entry.Path) -Destination $target
    if ((Get-FileHash -LiteralPath $target).Hash -ne $entry.Hash) { throw 'Source changed during snapshot' }
}
$manifest | ConvertTo-Json | Set-Content (Join-Path $root 'source-hashes.json')
Write-Host "Frozen source: $root"
dotnet build (Join-Path $snapshot 'benchmarks/CcDecisionHarness/CcDecisionHarness.csproj') -c Release --artifacts-path (Join-Path $root 'build') --nologo -v quiet 2>&1 | Tee-Object (Join-Path $root 'build.log')
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
$binary = Join-Path $root 'build/bin/CcDecisionHarness/release/CcDecisionHarness.dll'
$hashes = @(Get-ChildItem (Split-Path $binary) -Filter '*.dll' | Get-FileHash)
$hashes | Select-Object Path,Hash | ConvertTo-Json | Set-Content (Join-Path $root 'binary-hashes.json')
$evidence = Join-Path $repo ('artifacts/' + $EvidenceName)
dotnet $binary e3 $evidence 10 64 2>&1 | Tee-Object (Join-Path $root 'run.log')
$result = $LASTEXITCODE
foreach ($entry in $hashes) {
    if ((Get-FileHash -LiteralPath $entry.Path).Hash -ne $entry.Hash) { throw 'Binary changed during run' }
}
Write-Host "Evidence: $evidence; frozen build: $root; exit: $result"
exit $result
