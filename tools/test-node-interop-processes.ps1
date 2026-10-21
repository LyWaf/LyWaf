param(
    [ValidateSet('interop-suite', 'bootstrap-suite', 'configuration-suite', 'v7-suite', 'reload-suite')][string]$Suite = 'interop-suite',
    [switch]$WindowsOnly
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$root = Join-Path ([System.IO.Path]::GetTempPath()) ('lywaf-interop-build-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
Write-Host "Isolated build: $root"
# Freeze only build sources/resources, never user configuration or runtime data.
$snapshot = Join-Path $root 'source'
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
$after = SourceManifest
if (($before | ConvertTo-Json) -ne ($after | ConvertTo-Json)) { throw 'Source changed during snapshot copy; no hosts started' }
foreach ($entry in $before) {
    if ((Get-FileHash -LiteralPath (Join-Path $snapshot $entry.Path) -Algorithm SHA256).Hash -ne $entry.Hash) { throw 'Snapshot hash mismatch' }
}
$before | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'source-hashes.json')
$project = Join-Path $snapshot 'benchmarks/NodeProcessHarness/NodeProcessHarness.csproj'
dotnet build $project --artifacts-path (Join-Path $root 'windows') --nologo -v minimal 2>&1 | Tee-Object -FilePath (Join-Path $root 'windows-build.log')
if ($LASTEXITCODE -ne 0) { throw 'Windows build failed' }
dotnet publish $project -r linux-x64 --self-contained true --artifacts-path (Join-Path $root 'linux-artifacts') -o (Join-Path $root 'linux') --nologo -v minimal 2>&1 | Tee-Object -FilePath (Join-Path $root 'linux-build.log')
if ($LASTEXITCODE -ne 0) { throw 'Linux self-contained publish failed' }
foreach ($entry in $before) {
    if ((Get-FileHash -LiteralPath (Join-Path $snapshot $entry.Path) -Algorithm SHA256).Hash -ne $entry.Hash) { throw 'Snapshot changed during build' }
}
Get-FileHash (Join-Path $root 'source-hashes.json'),(Join-Path $root 'windows/bin/NodeProcessHarness/debug/LyWaf.dll'),(Join-Path $root 'windows/bin/NodeProcessHarness/debug/NodeProcessHarness.dll'),(Join-Path $root 'linux/LyWaf.dll'),(Join-Path $root 'linux/NodeProcessHarness.dll') -Algorithm SHA256 |
    Select-Object Path,Hash | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'binary-hashes.json')
$runArguments = @($Suite, (Join-Path $root 'linux/NodeProcessHarness'))
if ($WindowsOnly) { $runArguments += '--windows-only' }
dotnet (Join-Path $root 'windows/bin/NodeProcessHarness/debug/NodeProcessHarness.dll') @runArguments
if ($LASTEXITCODE -ne 0) { throw 'Mixed-OS verification failed; inspect printed artifacts' }
