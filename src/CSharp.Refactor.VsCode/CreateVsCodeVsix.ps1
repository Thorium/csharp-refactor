# Builds and packages the VS Code extension: stages the analyzer assembly
# with FSharp.Core beside it, stamps the version from Directory.Build.props,
# compiles the TypeScript, and runs vsce package.
#
#     pwsh -File CreateVsCodeVsix.ps1
#     code --install-extension artifacts/csharp-refactor-vscode-<version>.vsix
param([string]$Configuration = "Release")

$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo = Resolve-Path (Join-Path $here "../..")

dotnet build (Join-Path $repo "src/CSharp.Refactor.Analyzers") -c $Configuration
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$analyzerDir = Join-Path $here "analyzers"
if (Test-Path $analyzerDir) { Remove-Item -Recurse -Force $analyzerDir }
New-Item -ItemType Directory -Force $analyzerDir | Out-Null
$bin = Join-Path $repo "src/CSharp.Refactor.Analyzers/bin/$Configuration/netstandard2.0"
foreach ($f in "CSharp.Refactor.Analyzers.dll", "CSharp.Refactor.Analyzers.xml", "FSharp.Core.dll") {
    Copy-Item (Join-Path $bin $f) $analyzerDir
}
Copy-Item (Join-Path $repo "LICENSE") $here -Force

$repoVersion = [regex]::Match(
    [IO.File]::ReadAllText((Join-Path $repo "Directory.Build.props")),
    '<Version>([^<]+)</Version>').Groups[1].Value
$packageJsonPath = Join-Path $here "package.json"
$packageJson = [IO.File]::ReadAllText($packageJsonPath)
$packageJson = $packageJson -replace '("version":\s*")[^"]+(")', "`${1}$repoVersion`${2}"
$built = (Get-Date).ToUniversalTime().ToString("yyyy-MM-dd HH:mm") + " UTC"
$packageJson = $packageJson -replace '("analyzers":\s*")[^"]*(")', "`${1}$repoVersion`${2}"
$packageJson = $packageJson -replace '("built":\s*")[^"]*(")', "`${1}$built`${2}"
[IO.File]::WriteAllText($packageJsonPath, $packageJson)
Write-Host "Stamped VS Code extension version $repoVersion (built $built)"

Push-Location $here
try {
    npm install --no-audit --no-fund
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    npm run compile
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    $extensionName = [regex]::Match($packageJson, '"name":\s*"([^"]+)"').Groups[1].Value
    $artifacts = Join-Path $here "artifacts"
    New-Item -ItemType Directory -Force $artifacts | Out-Null
    $output = Join-Path $artifacts "$extensionName-$repoVersion.vsix"
    npx vsce package --out $output
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    Write-Host "Created $output"
}
finally {
    Pop-Location
}
