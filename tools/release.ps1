param(
    [Parameter(Mandatory = $true)][string]$Version,
    [ValidateSet('win-x64', 'linux-x64', 'linux-arm64')][string[]]$Runtime = @('win-x64', 'linux-x64'),
    [string]$Output = "$PSScriptRoot\..\artifacts",
    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$outputRoot = if ([IO.Path]::IsPathRooted($Output)) {
    [IO.Path]::GetFullPath($Output)
} else {
    [IO.Path]::GetFullPath((Join-Path $root $Output))
}
if ($outputRoot -eq $root -or -not $outputRoot.StartsWith($root + [IO.Path]::DirectorySeparatorChar)) {
    throw 'Output must be inside the repository.'
}
if ($Version -notmatch '^[0-9A-Za-z][0-9A-Za-z._-]{0,63}$') { throw 'Invalid version.' }
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$env:APPDATA = Join-Path $outputRoot '.appdata'
New-Item -ItemType Directory -Path $env:APPDATA -Force | Out-Null

foreach ($rid in $Runtime) {
    $stage = Join-Path $outputRoot "stage-$Version-$rid"
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
    if (-not $NoRestore) {
        dotnet restore (Join-Path $root 'LyWaf.csproj') -r $rid --nologo
        if ($LASTEXITCODE -ne 0) { throw "Restore failed for $rid." }
    }
    dotnet publish (Join-Path $root 'LyWaf.csproj') -c Release -r $rid --self-contained false -o $stage `
        -p:Version=$Version -p:ContinuousIntegrationBuild=true -p:Deterministic=true --no-restore --nologo
    if ($LASTEXITCODE -ne 0) { throw "Publish failed for $rid." }
    Remove-Item -LiteralPath (Join-Path $stage 'config.ly') -Force -ErrorAction SilentlyContinue

    $manifest = Get-ChildItem -LiteralPath $stage -File -Recurse | Sort-Object FullName | ForEach-Object {
        $relative = [IO.Path]::GetRelativePath($stage, $_.FullName).Replace('\', '/')
        '{0}  {1}' -f (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash.ToLowerInvariant(), $relative
    }
    $manifest | Set-Content -LiteralPath (Join-Path $stage 'SHA256SUMS') -Encoding ascii
    $archive = Join-Path $outputRoot "lywaf-$Version-$rid.zip"
    if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
    Add-Type -AssemblyName System.IO.Compression
    $file = [IO.File]::Open($archive, [IO.FileMode]::CreateNew)
    try {
        $zip = [IO.Compression.ZipArchive]::new($file, [IO.Compression.ZipArchiveMode]::Create, $false)
        try {
            Get-ChildItem -LiteralPath $stage -File -Recurse | Sort-Object FullName | ForEach-Object {
                $relative = [IO.Path]::GetRelativePath($stage, $_.FullName).Replace('\', '/')
                $entry = $zip.CreateEntry($relative, [IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
                $source = [IO.File]::OpenRead($_.FullName)
                $target = $entry.Open()
                try { $source.CopyTo($target) } finally { $target.Dispose(); $source.Dispose() }
            }
        } finally { $zip.Dispose() }
    } finally { $file.Dispose() }
}

Get-ChildItem -LiteralPath $outputRoot -Filter "lywaf-$Version-*.zip" | Sort-Object Name | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash.ToLowerInvariant(), $_.Name
} | Set-Content -LiteralPath (Join-Path $outputRoot "lywaf-$Version-SHA256SUMS") -Encoding ascii
