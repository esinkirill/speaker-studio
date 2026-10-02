[CmdletBinding()]
param(
    [ValidateSet('x64', 'x86')]
    [string]$Platform = 'x64',
    [switch]$IncludeVisualUi
)

$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    throw 'The .NET Framework 4.x C# compiler was not found. Run on Windows with .NET Framework installed.'
}
$sourceFiles = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' |
    ForEach-Object { $_.FullName })
$outputDirectory = Join-Path (Join-Path $PSScriptRoot 'build\tests') $Platform
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$testNames = @('TimingChecks', 'SequenceDataChecks', 'PlaybackTests', 'ProcessTests')
if ($IncludeVisualUi) { $testNames += @('TimelineVisualChecks', 'MenuLifecycleChecks') }
$results = @()

foreach ($testName in $testNames) {
    $testSource = Join-Path $PSScriptRoot "tests\$testName.cs"
    $testDirectory = Join-Path $outputDirectory $testName
    New-Item -ItemType Directory -Path $testDirectory -Force | Out-Null
    $testExecutable = Join-Path $testDirectory "$testName.exe"
    & $compiler /nologo /target:exe "/platform:$Platform" /codepage:65001 "/main:$testName" "/out:$testExecutable" /r:System.dll /r:System.Core.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll $sourceFiles $testSource
    if ($LASTEXITCODE -ne 0) { throw "Compilation failed: $testName" }
    $arguments = @()
    if ($testName -eq 'SequenceDataChecks') { $arguments = @($testDirectory) }
    elseif ($testName -eq 'ProcessTests') { $arguments = @($testDirectory) }
    elseif ($testName -eq 'TimelineVisualChecks') { $arguments = @($PSScriptRoot, $testDirectory) }
    elseif ($testName -eq 'MenuLifecycleChecks') { $arguments = @($testDirectory) }
    $testOutput = @(& $testExecutable @arguments 2>&1)
    $testExitCode = $LASTEXITCODE
    foreach ($line in $testOutput) { Write-Output $line }
    $results += [pscustomobject]@{
        name = $testName
        platform = $Platform
        exitCode = $testExitCode
        output = @($testOutput | ForEach-Object { $_.ToString() })
    }
    $report = [pscustomobject]@{
        timestampUtc = [DateTime]::UtcNow.ToString('o')
        passed = $testExitCode -eq 0
        platform = $Platform
        visualUiIncluded = [bool]$IncludeVisualUi
        audioOrHardwareOutput = $false
        results = $results
    }
    $report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $outputDirectory 'test-results.json') -Encoding UTF8
    if ($testExitCode -ne 0) { throw "Test failed: $testName (exit $testExitCode)" }
}

Write-Output "PASS: $($testNames.Count) test programs. Generated fixtures only; no audio or hardware output."
Write-Output "Report: $(Join-Path $outputDirectory 'test-results.json')"
