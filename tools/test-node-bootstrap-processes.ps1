param([switch]$WindowsOnly)
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'test-node-interop-processes.ps1') -Suite bootstrap-suite -WindowsOnly:$WindowsOnly
