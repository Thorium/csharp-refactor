# CSharp.Refactor for Visual Studio

**[Get it from the Visual Studio Marketplace](https://marketplace.visualstudio.com/items?itemName=TuomasHietanen.cSharp-refactor)**

Tools > CSharp.Refactor -menu to drive the command line tool for the full project:

<img width="343" height="182" alt="image" src="https://github.com/user-attachments/assets/2f5cfa7b-be8d-42bd-84bb-6c366d169483" />

And IDE light bulbs while you type

<img width="1044" height="297" alt="image" src="https://github.com/user-attachments/assets/ad113d88-3536-47e1-b669-fd6e4ca4e776" />

A VSIX whose payload is the analyzer assembly with FSharp.Core beside it,
declared as a `Microsoft.VisualStudio.Analyzer` asset (the diagnostics, added
to every C# project VS opens) and a `Microsoft.VisualStudio.MefComponent`
asset (the code-fix provider). No package code, no sidecar: Roslyn renders
the squiggles and light bulbs. Verified on Visual Studio 2026 (18.10).

## Build + try

```powershell
powershell -File src/CSharp.Refactor.Vsix/CreateVsix.ps1
& "C:\Program Files\Microsoft Visual Studio\18\Professional\Common7\IDE\VSIXInstaller.exe" "/rootSuffix:Exp" src\CSharp.Refactor.Vsix\artifacts\CSharp.Refactor.vsix
& "C:\Program Files\Microsoft Visual Studio\18\Professional\Common7\IDE\devenv.exe" /rootSuffix Exp
```

Open any C# project and look for the three-dot suggestions; `Ctrl+.` on one
shows the fixes.

## Packaging notes

A `.vsix` is an OPC zip assembled by hand in `CreateVsix.ps1`: payload,
`extension.vsixmanifest` (the source manifest with the design-time `d:`
namespace stripped), the v3 servicing files `manifest.json` and
`catalog.json`, and `[Content_Types].xml`. Every part's extension needs a
`Default` entry there — including the `json` of the servicing files, which
are written AFTER the payload is staged. Enumerate the payload again before
writing the content types, or the installer says "The file is not a valid
VSIX package" and nothing else.
