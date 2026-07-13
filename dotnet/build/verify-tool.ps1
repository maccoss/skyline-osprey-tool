<#
  Launch smoke test for the packaged Skyline external tool.

  A zip that BUILDS can still be broken: a missing native dependency or a XAML error kills the process
  during startup, and because Skyline launches the tool detached with no console, the user just sees
  "nothing happens". (That exact bug shipped once - a checkbox with IsChecked="True" raised its Checked
  handler during InitializeComponent, before the controls it touched existed.) So: extract the zip and
  actually run the exe.

  Passes when the process is still alive after -WaitSeconds with no assembly/XAML load errors on stderr.
  The tool is expected to start in "not connected" mode here - there is no Skyline to talk to - which is
  a normal, non-fatal state.

  Usage:
    pwsh -File dotnet/build/verify-tool.ps1
#>
param(
    [string]$ZipPath = (Join-Path $PSScriptRoot '..\publish\OspreyTool.zip'),
    [int]$WaitSeconds = 10
)
$ErrorActionPreference = 'Stop'

if (-not (Test-Path $ZipPath)) {
    Write-Host "VERIFY FAILED: no zip at $ZipPath. Build it with: dotnet build dotnet/build/package.proj" -ForegroundColor Red
    exit 1
}

$work = Join-Path ([System.IO.Path]::GetTempPath()) ("ospreytool-verify-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    Expand-Archive -Path $ZipPath -DestinationPath $work -Force

    $exe = Join-Path $work 'OspreyTool.exe'
    foreach ($required in @($exe, (Join-Path $work 'tool-inf\info.properties'), (Join-Path $work 'tool-inf\OspreyTool.properties'))) {
        if (-not (Test-Path $required)) {
            Write-Host "VERIFY FAILED: the zip is missing $required" -ForegroundColor Red
            exit 1
        }
    }

    $loadFailure = 'Could not load file or assembly|XamlParseException|TypeInitializationException|DllNotFoundException|BadImageFormatException'

    # ---- 1. Dependency check ------------------------------------------------------------------------
    # A plain launch is NOT sufficient: .NET loads assemblies lazily, so a zip missing OspreyTool.Scoring.dll
    # (or a native SQLite/SkiaSharp binary) still opens its window and only dies when the user presses Run.
    # --self-check forces the whole graph to load and runs the engine once. Verified to catch a removed DLL.
    $checkOut = Join-Path $work 'selfcheck-out.txt'
    $checkErr = Join-Path $work 'selfcheck-err.txt'
    Write-Host 'Running --self-check (loads the engine, RPC seam and plotting stack) ...'
    $check = Start-Process -FilePath $exe -ArgumentList '--self-check' -PassThru -Wait `
        -RedirectStandardOutput $checkOut -RedirectStandardError $checkErr
    $checkOutput = @(
        (Get-Content $checkOut -Raw -ErrorAction SilentlyContinue),
        (Get-Content $checkErr -Raw -ErrorAction SilentlyContinue)
    ) -join "`n"

    if ($check.ExitCode -ne 0 -or $checkOutput -notmatch 'SELF-CHECK OK') {
        Write-Host "VERIFY FAILED: --self-check exited $($check.ExitCode)." -ForegroundColor Red
        if ($checkOutput.Trim()) { Write-Host $checkOutput }
        exit 1
    }
    Write-Host ("  " + ($checkOutput -split "`n" | Where-Object { $_ -match 'SELF-CHECK OK' } | Select-Object -First 1).Trim())

    # ---- 2. UI launch check -------------------------------------------------------------------------
    # Catches XAML/startup crashes that the headless self-check cannot see (it never builds the window).
    $outLog = Join-Path $work 'stdout.txt'
    $errLog = Join-Path $work 'stderr.txt'
    Write-Host "Launching the UI (waiting ${WaitSeconds}s for it to stay up) ..."
    $proc = Start-Process -FilePath $exe -PassThru -RedirectStandardOutput $outLog -RedirectStandardError $errLog

    $exited = $proc.WaitForExit($WaitSeconds * 1000)
    $output = @(
        (Get-Content $outLog -Raw -ErrorAction SilentlyContinue),
        (Get-Content $errLog -Raw -ErrorAction SilentlyContinue)
    ) -join "`n"

    if ($exited) {
        # Startup crash: an unhandled exception in App.OnStartup / the MainWindow constructor.
        Write-Host "VERIFY FAILED: the tool exited on its own (code $($proc.ExitCode)) instead of staying up." -ForegroundColor Red
        if ($output.Trim()) { Write-Host $output }
        exit 1
    }

    Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue

    if ($output -match $loadFailure) {
        Write-Host 'VERIFY FAILED: the tool logged an assembly/XAML load error.' -ForegroundColor Red
        Write-Host $output
        exit 1
    }

    Write-Host 'VERIFY PASSED: the packaged tool loads its full dependency graph, runs the engine, and opens its UI.' -ForegroundColor Green
    exit 0
}
finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
