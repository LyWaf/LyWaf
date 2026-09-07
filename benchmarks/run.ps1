param(
    [string]$Targets = "$PSScriptRoot\targets.json",
    [int]$Duration = 15,
    [int]$Concurrency = 64,
    [string]$Output = "$PSScriptRoot\results"
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'LyWaf.Benchmark\LyWaf.Benchmark.csproj'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$runnerDirectory = Join-Path $PSScriptRoot ".artifacts\runner-$stamp"
$runner = Join-Path $runnerDirectory 'LyWaf.Benchmark.dll'
$env:APPDATA = Join-Path $PSScriptRoot '.appdata'
New-Item -ItemType Directory -Path $env:APPDATA -Force | Out-Null
dotnet build $project -c Release --nologo --configfile (Join-Path $PSScriptRoot 'NuGet.Config') -o $runnerDirectory | Out-Host
if (-not (Test-Path -LiteralPath $Targets)) {
    throw "Target file not found: $Targets. Copy targets.example.json and set only services that are running."
}

New-Item -ItemType Directory -Path $Output -Force | Out-Null
$jsonl = Join-Path $Output "$stamp-results.jsonl"
$environment = [ordered]@{
    timestamp = (Get-Date).ToString('o')
    machine = $env:COMPUTERNAME
    os = [System.Runtime.InteropServices.RuntimeInformation]::OSDescription
    architecture = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
    processors = [Environment]::ProcessorCount
    dotnet = (& dotnet --version)
    durationSeconds = $Duration
    concurrency = $Concurrency
    targetsFile = (Resolve-Path $Targets).Path
}
$environment | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Output "$stamp-environment.json") -Encoding utf8

function Get-Metrics([string]$Url) {
    if ([string]::IsNullOrWhiteSpace($Url)) { return $null }
    try {
        $body = (Invoke-WebRequest -UseBasicParsing -Uri $Url -TimeoutSec 5).Content
        $values = @{}
        foreach ($line in ($body -split "`n")) {
            if ($line -match '^(lywaf_process_cpu_seconds_total|lywaf_process_working_set_bytes|lywaf_dotnet_gc_heap_bytes|lywaf_process_threads|lywaf_process_handles)\s+([0-9.]+)$') {
                $values[$matches[1]] = [double]$matches[2]
            } elseif ($line -match '^lywaf_dotnet_gc_collections_total\{generation="([0-9]+)"\}\s+([0-9.]+)$') {
                $values["gc$($matches[1])"] = [double]$matches[2]
            }
        }
        return $values
    } catch { return @{ error = $_.Exception.Message } }
}

function Get-ProcessSnapshot($ProcessId) {
    if (-not $ProcessId) { return $null }
    try {
        $process = Get-Process -Id ([int]$ProcessId) -ErrorAction Stop
        return [ordered]@{
            cpuSeconds = $process.TotalProcessorTime.TotalSeconds
            workingSetBytes = $process.WorkingSet64
            privateMemoryBytes = $process.PrivateMemorySize64
            threads = $process.Threads.Count
            handles = $process.HandleCount
        }
    } catch { return @{ error = $_.Exception.Message } }
}

$scenarios = @(
    @{ name = 'small'; path = '/small'; concurrency = $Concurrency; slow = 0; websocket = $false },
    @{ name = 'large'; path = '/large'; concurrency = [Math]::Max(1, [int]($Concurrency / 4)); slow = 0; websocket = $false },
    @{ name = 'slow-upstream'; path = '/slow'; concurrency = $Concurrency; slow = 0; websocket = $false },
    @{ name = 'slow-client'; path = '/large'; concurrency = [Math]::Max(1, [int]($Concurrency / 8)); slow = 10; websocket = $false },
    @{ name = 'websocket'; path = '/ws'; concurrency = [Math]::Max(1, [int]($Concurrency / 4)); slow = 0; websocket = $true }
)

foreach ($target in (Get-Content -LiteralPath $Targets -Raw | ConvertFrom-Json)) {
    foreach ($scenario in $scenarios) {
        $before = Get-Metrics $target.metricsUrl
        $processBefore = Get-ProcessSnapshot $target.processId
        $arguments = @($runner, 'load', '--url', "$($target.url)$($scenario.path)", '--protocol', $target.protocol,
            '--duration', $Duration, '--concurrency', $scenario.concurrency, '--slow-ms', $scenario.slow)
        if ($scenario.websocket) { $arguments += '--websocket' }
        if ($target.insecure) { $arguments += '--insecure' }
        $raw = & dotnet @arguments
        $exitCode = $LASTEXITCODE
        $after = Get-Metrics $target.metricsUrl
        $processAfter = Get-ProcessSnapshot $target.processId
        $result = $raw | ConvertFrom-Json
        $record = [ordered]@{
            target = $target.name
            scenario = $scenario.name
            exitCode = $exitCode
            result = $result
            metricsBefore = $before
            metricsAfter = $after
            processBefore = $processBefore
            processAfter = $processAfter
        }
        ($record | ConvertTo-Json -Depth 8 -Compress) | Add-Content -LiteralPath $jsonl -Encoding utf8
        Write-Host ("{0,-24} {1,-14} {2,10:N0} req/s  p99 {3,7:N0} ms  errors {4}" -f
            $target.name, $scenario.name, $result.requestsPerSecond, $result.p99Ms, $result.errors)
    }
}

Write-Host "Results: $jsonl"
