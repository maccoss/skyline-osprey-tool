<#
  Ship gate for the Skyline external tool: test -> package -> launch-verify, in that order.

  Use this rather than calling package.proj directly, so the tool is always exercised before it is shipped
  or installed. Any step failing aborts with a non-zero exit code and no zip is declared ready.

  Usage:
    pwsh -File dotnet/build/package-and-verify.ps1
    pwsh -File dotnet/build/package-and-verify.ps1 -Configuration Debug
#>
param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$dotnetDir = Resolve-Path (Join-Path $PSScriptRoot '..')

Write-Host '== 1/3  Full test suite ==' -ForegroundColor Cyan
dotnet test (Join-Path $dotnetDir 'OspreyTool.sln') -c $Configuration
if ($LASTEXITCODE -ne 0) { Write-Host 'ABORT: tests failed.' -ForegroundColor Red; exit 1 }

Write-Host '== 2/3  Package OspreyTool.zip ==' -ForegroundColor Cyan
dotnet build (Join-Path $dotnetDir 'build\package.proj') -c $Configuration
if ($LASTEXITCODE -ne 0) { Write-Host 'ABORT: packaging failed.' -ForegroundColor Red; exit 1 }

Write-Host '== 3/3  Launch smoke test (extract the zip and run the exe) ==' -ForegroundColor Cyan
pwsh -NoProfile -File (Join-Path $PSScriptRoot 'verify-tool.ps1')
if ($LASTEXITCODE -ne 0) { Write-Host 'ABORT: the packaged tool failed to launch.' -ForegroundColor Red; exit 1 }

Write-Host ''
Write-Host 'READY: OspreyTool.zip is tested, packaged, and launch-verified.' -ForegroundColor Green
Write-Host "  $(Join-Path $dotnetDir 'publish\OspreyTool.zip')"
