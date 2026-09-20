# Builds and packages the Visual Studio extension. A .vsix is an OPC zip:
# payload + extension.vsixmanifest + [Content_Types].xml — assembled by
# hand here, as the F# side's is, because the VSSDK packaging targets fight
# SDK-style projects. The payload is the analyzer assembly with FSharp.Core
# beside it: no package code, no MEF glue of our own — Visual Studio adds a
# `Microsoft.VisualStudio.Analyzer` asset to every C# project it opens and
# renders the diagnostics and light bulbs itself.
#
#     powershell -File CreateVsix.ps1
#     VSIXInstaller /rootSuffix:Exp artifacts\CSharp.Refactor.vsix   # test instance
#     VSIXInstaller artifacts\CSharp.Refactor.vsix                   # real VS
param([string]$Configuration = "Release")

$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo = Resolve-Path (Join-Path $here "..\..")

dotnet build (Join-Path $repo "src\CSharp.Refactor.Analyzers") -c $Configuration
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$staging = Join-Path $here "obj\vsix-staging"
if (Test-Path $staging) { Remove-Item -Recurse -Force $staging }
New-Item -ItemType Directory -Force $staging | Out-Null

$bin = Join-Path $repo "src\CSharp.Refactor.Analyzers\bin\$Configuration\netstandard2.0"
foreach ($f in "CSharp.Refactor.Analyzers.dll", "CSharp.Refactor.Analyzers.xml", "FSharp.Core.dll") {
    Copy-Item (Join-Path $bin $f) $staging
}
Copy-Item (Join-Path $repo "LICENSE") $staging
Copy-Item (Join-Path $repo "icon.png") $staging

# the packaged manifest is the PROCESSED form: VS's own build strips the
# design-time d: namespace before packaging, and the installer's reader
# rejects manifests that still carry it
$manifest = [IO.File]::ReadAllText((Join-Path $here "source.extension.vsixmanifest"))
$manifest = $manifest -replace '\s+xmlns:d="[^"]*"', ''
$manifest = $manifest -replace '\s+d:\w+="[^"]*"', ''
$manifest = $manifest -replace '<\?xml[^>]*\?>\s*', ''

# ONE version source: stamped from Directory.Build.props
$repoVersion = [regex]::Match(
    [IO.File]::ReadAllText((Join-Path $repo "Directory.Build.props")),
    '<Version>([^<]+)</Version>').Groups[1].Value
if (-not $repoVersion) {
    throw "No <Version> in Directory.Build.props; refusing to ship the manifest's 0.0.0 placeholder."
}
$manifest = $manifest -replace '(<Identity [^>]*Version=")[^"]+(")', "`${1}$repoVersion`${2}"
Write-Host "Stamped vsix version $repoVersion"

[IO.File]::WriteAllText((Join-Path $staging "extension.vsixmanifest"), $manifest)

# VSIX v3 servicing files: the modern installer engine refuses a package
# without manifest.json + catalog.json
$vsixId = [regex]::Match($manifest, 'Identity Id="([^"]+)"').Groups[1].Value
$version = [regex]::Match($manifest, 'Identity Id="[^"]+" Version="([^"]+)"').Groups[1].Value
$displayName = [regex]::Match($manifest, '<DisplayName>([^<]+)</DisplayName>').Groups[1].Value
$installDir = '[installdir]\\Common7\\IDE\\Extensions\\csrefact.vs'

$payloadFiles = Get-ChildItem -LiteralPath $staging -Recurse -File
$totalSize = ($payloadFiles | Measure-Object Length -Sum).Sum

$fileEntries = ($payloadFiles | ForEach-Object {
        $rel = '/' + $_.FullName.Substring($staging.Length + 1).Replace('\', '/')
        '{"fileName":"' + $rel + '","sha256":null}'
    }) -join ','

$manifestJson = '{"id":"' + $vsixId + '","version":"' + $version + '","type":"Vsix","vsixId":"' + $vsixId +
'","extensionDir":"' + $installDir + '","files":[' + $fileEntries +
'],"installSizes":{"targetDrive":' + $totalSize + '},"dependencies":{"Microsoft.VisualStudio.Component.CoreEditor":"17.0"}}'
[IO.File]::WriteAllText((Join-Path $staging "manifest.json"), $manifestJson)

$catalogJson = '{"manifestVersion":"1.1","info":{"id":"' + $vsixId + ',version=' + $version +
'","manifestType":"Extension"},"packages":[{"id":"Component.' + $vsixId + '","version":"' + $version +
'","type":"Component","extension":true,"dependencies":{"' + $vsixId + '":"' + $version +
'","Microsoft.VisualStudio.Component.CoreEditor":"17.0"},"localizedResources":[{"language":"en-US","title":"' +
$displayName + '","description":"' + $displayName + '"}]},{"id":"' + $vsixId + '","version":"' + $version +
'","type":"Vsix","payloads":[{"fileName":"CSharp.Refactor.vsix","size":' + $totalSize + '}],"vsixId":"' + $vsixId +
'","extensionDir":"' + $installDir + '","installSizes":{"targetDrive":' + $totalSize + '}}]}'
[IO.File]::WriteAllText((Join-Path $staging "catalog.json"), $catalogJson)

# enumerate AGAIN: manifest.json and catalog.json were written after the
# payload list above, and a part without a content type makes the whole
# OPC package "not a valid VSIX package" (the F# side's script only
# escapes this because its FsAutoComplete payload carries .json files)
$payloadFiles = Get-ChildItem -LiteralPath $staging -Recurse -File
$known = @{ "vsixmanifest" = "text/xml"; "json" = "application/json"; "xml" = "text/xml"; "dll" = "application/octet-stream" }
$extensions = $payloadFiles |
    ForEach-Object { $_.Extension.TrimStart('.').ToLowerInvariant() } |
    Where-Object { $_ } | Sort-Object -Unique
$defaults = ($extensions | ForEach-Object {
        $type = if ($known.ContainsKey($_)) { $known[$_] } else { "application/octet-stream" }
        "  <Default Extension=`"$_`" ContentType=`"$type`" />"
    }) -join "`n"
$overrides = ($payloadFiles | Where-Object { -not $_.Extension } | ForEach-Object {
        $rel = '/' + $_.FullName.Substring($staging.Length + 1).Replace('\', '/')
        "  <Override PartName=`"$rel`" ContentType=`"text/plain`" />"
    }) -join "`n"
$contentTypes = @"
<?xml version="1.0" encoding="utf-8"?>
<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
$defaults
$overrides
</Types>
"@
[IO.File]::WriteAllText((Join-Path $staging "[Content_Types].xml"), $contentTypes, (New-Object System.Text.UTF8Encoding $false))

$artifacts = Join-Path $here "artifacts"
New-Item -ItemType Directory -Force $artifacts | Out-Null
$vsix = Join-Path $artifacts "CSharp.Refactor.vsix"
if (Test-Path $vsix) { Remove-Item -Force $vsix }

# manual zip: part names must use '/'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::Open($vsix, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    Get-ChildItem -LiteralPath $staging -Recurse -File | ForEach-Object {
        $rel = $_.FullName.Substring($staging.Length + 1).Replace('\', '/')
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $_.FullName, $rel) | Out-Null
    }
}
finally {
    $archive.Dispose()
}

Write-Host "Created $vsix"
