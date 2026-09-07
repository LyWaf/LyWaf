param(
    [Parameter(Mandatory = $true)][string]$Url,
    [int]$Hours = 8,
    [int]$Concurrency = 64,
    [string]$Protocol = '1.1',
    [string]$Output = "$PSScriptRoot\results"
)

$ErrorActionPreference = 'Stop'
if ($Hours -lt 1 -or $Hours -gt 168) { throw 'Hours must be between 1 and 168.' }
$project = Join-Path $PSScriptRoot 'LyWaf.Benchmark\LyWaf.Benchmark.csproj'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$artifact = Join-Path $PSScriptRoot ".artifacts\soak-$stamp"
New-Item -ItemType Directory -Path $artifact, $Output -Force | Out-Null
dotnet build $project -c Release --nologo -o $artifact | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Benchmark build failed.' }

$result = Join-Path $Output "$stamp-soak.json"
$started = Get-Date
try {
    & dotnet (Join-Path $artifact 'LyWaf.Benchmark.dll') load --url $Url --protocol $Protocol `
        --duration ($Hours * 3600) --concurrency $Concurrency | Set-Content -LiteralPath $result -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw "Soak load failed with exit code $LASTEXITCODE." }
} finally {
    [ordered]@{
        startedAt = $started.ToString('o')
        finishedAt = (Get-Date).ToString('o')
        url = $Url
        hours = $Hours
        concurrency = $Concurrency
        protocol = $Protocol
        dotnet = (& dotnet --version)
        os = [System.Runtime.InteropServices.RuntimeInformation]::OSDescription
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Output "$stamp-soak-environment.json") -Encoding utf8
}
Write-Host "Result: $result"
