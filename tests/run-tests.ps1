# Runs the test suite. The end-to-end tests drive MSBuildWorkspace and the
# console, so they sit in the serialized "Tool" collection; everything else
# runs in parallel beside them. The FsCheck suite runs as a second process
# beside the first: it shares none of that state.
#
#     pwsh -File tests/run-tests.ps1                # Debug
#     pwsh -File tests/run-tests.ps1 -c Release
#     pwsh -File tests/run-tests.ps1 -NoBuild
param(
    [Alias("c")] [string]$Configuration = "Debug",
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $here "CSharp.Refactor.Tests"
$propertyProject = Join-Path $here "CSharp.Refactor.PropertyTests"

if (-not $NoBuild) {
    dotnet build $project -c $Configuration --nologo -v q
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    dotnet build $propertyProject -c $Configuration --nologo -v q
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

$sw = [Diagnostics.Stopwatch]::StartNew()
$examples = Start-Process -FilePath dotnet -ArgumentList @("test", $project, "-c", $Configuration, "--no-build", "--nologo") -NoNewWindow -PassThru
$properties = Start-Process -FilePath dotnet -ArgumentList @("test", $propertyProject, "-c", $Configuration, "--no-build", "--nologo") -NoNewWindow -PassThru
$examples.WaitForExit()
$properties.WaitForExit()
$sw.Stop()

Write-Host ("wall {0:n0} s" -f $sw.Elapsed.TotalSeconds)
if ($examples.ExitCode -ne 0 -or $properties.ExitCode -ne 0) {
    Write-Host "FAILED (examples: $($examples.ExitCode), properties: $($properties.ExitCode))"
    exit 1
}
