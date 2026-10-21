param([ValidateSet('Snapshot', 'Cross', 'All', 'Receiver', 'Gap')][string]$Mode = 'All')
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$root = Join-Path ([System.IO.Path]::GetTempPath()) ('lywaf-capacity-build-' + [guid]::NewGuid().ToString('N'))
$snapshot = Join-Path $root 'source'
New-Item -ItemType Directory -Path $snapshot | Out-Null
Write-Host "Isolated frozen build: $root"
$files = & rg --files $repo -g '*.cs' -g '*.csproj' -g 'Directory.Build.*' -g 'Directory.Packages.props' -g 'global.json' -g 'nuget.config' -g 'config.ly.example'
if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate source snapshot' }
$files = @($files | Where-Object { $_ -notmatch '[\\/]tests[\\/]|[\\/]benchmarks[\\/](?!NodeProcessHarness[\\/])' })
$files += @(Get-ChildItem (Join-Path $repo 'Localization'),(Join-Path $repo 'Plugins') -Recurse -File -Filter '*.json' |
    Where-Object { $_.FullName -match '[\\/]Localization[\\/]' } | ForEach-Object FullName)
$files = @($files | Sort-Object -Unique)
function SourceManifest {
    @($files | ForEach-Object { [pscustomobject]@{ Path = [System.IO.Path]::GetRelativePath($repo, $_); Hash = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash } })
}
$before = SourceManifest
foreach ($file in $files) {
    $target = Join-Path $snapshot ([System.IO.Path]::GetRelativePath($repo, $file))
    New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
    Copy-Item -LiteralPath $file -Destination $target
}
if (($before | ConvertTo-Json) -ne ((SourceManifest) | ConvertTo-Json)) { throw 'Source changed during copy; no hosts started' }
foreach ($entry in $before) {
    if ((Get-FileHash -LiteralPath (Join-Path $snapshot $entry.Path) -Algorithm SHA256).Hash -ne $entry.Hash) { throw 'Snapshot hash mismatch' }
}
$before | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'source-hashes.json')
$project = Join-Path $snapshot 'benchmarks/NodeProcessHarness/NodeProcessHarness.csproj'
dotnet build $project -c Release --artifacts-path (Join-Path $root 'windows') --nologo -v minimal 2>&1 | Tee-Object -FilePath (Join-Path $root 'windows-build.log')
if ($LASTEXITCODE -ne 0) { throw 'Windows build failed' }
$hostDll = Join-Path $root 'windows/bin/NodeProcessHarness/release/NodeProcessHarness.dll'
$binaries = @($hostDll, (Join-Path $root 'windows/bin/NodeProcessHarness/release/LyWaf.dll'))
if ($Mode -ne 'Snapshot') {
    dotnet publish $project -c Release -r linux-x64 --self-contained true --artifacts-path (Join-Path $root 'linux-artifacts') -o (Join-Path $root 'linux') --nologo -v minimal 2>&1 | Tee-Object -FilePath (Join-Path $root 'linux-build.log')
    if ($LASTEXITCODE -ne 0) { throw 'Linux build failed' }
    $binaries += @((Join-Path $root 'linux/LyWaf.dll'), (Join-Path $root 'linux/NodeProcessHarness.dll'))
}
$hashes = @(Get-FileHash -LiteralPath $binaries -Algorithm SHA256 | Select-Object Path,Hash)
$hashes | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'binary-hashes.json')
foreach ($entry in $before) {
    if ((Get-FileHash -LiteralPath (Join-Path $snapshot $entry.Path) -Algorithm SHA256).Hash -ne $entry.Hash) { throw 'Snapshot changed during build' }
}
if ($Mode -in @('Snapshot', 'All')) {
    dotnet $hostDll capacity-snapshot-suite 2>&1 | Tee-Object -FilePath (Join-Path $root 'snapshot-run.log')
    if ($LASTEXITCODE -ne 0) { throw 'Snapshot capacity suite failed; evidence retained' }
}
if ($Mode -ne 'Snapshot') {
    if ($Mode -eq 'Gap') {
        dotnet $hostDll index-gap-suite (Join-Path $root 'linux/NodeProcessHarness') 2>&1 | Tee-Object -FilePath (Join-Path $root 'gap-run.log')
    } elseif ($Mode -eq 'Receiver') {
        dotnet $hostDll receiver-peak-suite (Join-Path $root 'linux/NodeProcessHarness') 2>&1 | Tee-Object -FilePath (Join-Path $root 'receiver-run.log')
    } else {
        dotnet $hostDll capacity-cross-suite (Join-Path $root 'linux/NodeProcessHarness') 2>&1 | Tee-Object -FilePath (Join-Path $root 'cross-run.log')
    }
    if ($LASTEXITCODE -ne 0) { throw 'Cross-process Linux capacity suite failed; evidence retained' }
}
foreach ($entry in $hashes) {
    if ((Get-FileHash -LiteralPath $entry.Path -Algorithm SHA256).Hash -ne $entry.Hash) { throw 'Binary changed during execution' }
}
Write-Host "PASS capacity suites $Mode; source and binary hashes retained at $root"
