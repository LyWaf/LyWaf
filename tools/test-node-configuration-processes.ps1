$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'test-node-interop-processes.ps1') -Suite configuration-suite
