#!/usr/bin/env pwsh
<#
.SYNOPSIS
    SwiftGet Master Verification Workflow: runs build, format/lint, test suite, and benchmarks.
.DESCRIPTION
    Executes all gates required before accepting any milestone or commit in SwiftGet.
.PARAMETER IncludeBenchmarks
    Switch to include execution of BenchmarkDotNet micro-benchmarks.
.PARAMETER SkipLint
    Switch to skip format/lint checks.
#>
[CmdletBinding()]
param(
    [switch]$IncludeBenchmarks,
    [switch]$FullBenchmarks,
    [switch]$SkipLint
)

$ErrorActionPreference = "Stop"
$sw = [System.Diagnostics.Stopwatch]::StartNew()

# Ensure local .NET SDK is prioritised
if (Test-Path "C:\Users\SPARK\AppData\Local\Microsoft\dotnet\dotnet.exe") {
    $env:DOTNET_ROOT = "C:\Users\SPARK\AppData\Local\Microsoft\dotnet"
    $env:PATH = "C:\Users\SPARK\AppData\Local\Microsoft\dotnet;$env:PATH"
}

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host " [SwiftGet] Starting Master Verification Pipeline         " -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

$Root = Split-Path -Parent $PSScriptRoot
Set-Location $Root

# 1. Format & Lint Verification
if (-not $SkipLint) {
    Write-Host "`n[1/4] Running format and lint verification..." -ForegroundColor Yellow
    if (Test-Path "$Root\SwiftGet.sln") {
        dotnet format "$Root\SwiftGet.sln" --verify-no-changes
        if ($LASTEXITCODE -ne 0) {
            Write-Error "Format/Lint verification failed. Run 'dotnet format' to fix."
            exit $LASTEXITCODE
        }
    } else {
        Write-Host "No solution file present yet, skipping dotnet format." -ForegroundColor Gray
    }
} else {
    Write-Host "`n[1/4] Skipping lint verification (-SkipLint specified)." -ForegroundColor DarkGray
}

# 2. Build Solution
Write-Host "`n[2/4] Building solution (Release configuration)..." -ForegroundColor Yellow
if (Test-Path "$Root\SwiftGet.sln") {
    dotnet build "$Root\SwiftGet.sln" --configuration Release
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Solution build failed."
        exit $LASTEXITCODE
    }
} else {
    Write-Host "No solution file present yet, skipping build." -ForegroundColor Gray
}

# 3. Run Test Suite
Write-Host "`n[3/4] Running automated test suites..." -ForegroundColor Yellow
if (Test-Path "$Root\SwiftGet.sln") {
    dotnet test "$Root\SwiftGet.sln" --configuration Release --no-build --logger "console;verbosity=normal"
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Test suite failed."
        exit $LASTEXITCODE
    }
} else {
    Write-Host "No solution file present yet, skipping tests." -ForegroundColor Gray
}

# 4. Benchmarks (Optional)
if ($IncludeBenchmarks -or $FullBenchmarks) {
    Write-Host "`n[4/4] Running performance benchmarks..." -ForegroundColor Yellow
    $benchProj = "$Root\benchmarks\SwiftGet.Benchmarks\SwiftGet.Benchmarks.csproj"
    if (Test-Path $benchProj) {
        $benchArgs = if ($FullBenchmarks) { @("--filter", "*") } else { @("--smoke") }
        dotnet run --project $benchProj --configuration Release --no-build -- $benchArgs
        if ($LASTEXITCODE -ne 0) {
            Write-Error "Benchmarks failed."
            exit $LASTEXITCODE
        }
    } else {
        Write-Host "Benchmark project not found at $benchProj, skipping." -ForegroundColor Gray
    }
} else {
    Write-Host "`n[4/4] Skipping benchmarks (pass -IncludeBenchmarks to execute)." -ForegroundColor DarkGray
}

$sw.Stop()
Write-Host "`n==========================================================" -ForegroundColor Green
Write-Host " [SwiftGet] All gates PASSED in $($sw.Elapsed.TotalSeconds.ToString("0.00"))s! " -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Green
