# CSharp.Refactor for Visual Studio

**[Get it from the Visual Studio Marketplace](https://marketplace.visualstudio.com/items?itemName=TuomasHietanen.cSharp-refactor)**

Tools > CSharp.Refactor -menu to drive the command line tool for the full project:

<img width="343" height="182" alt="image" src="https://github.com/user-attachments/assets/2f5cfa7b-be8d-42bd-84bb-6c366d169483" />

And IDE light bulbs while you type

<img width="1044" height="297" alt="image" src="https://github.com/user-attachments/assets/ad113d88-3536-47e1-b669-fd6e4ca4e776" />

A VSIX whose payload is the analyzer assembly with FSharp.Core beside it,
declared as a `Microsoft.VisualStudio.Analyzer` asset (the diagnostics, added
to every C# project VS opens) and a `Microsoft.VisualStudio.MefComponent`
asset (the code-fix provider) — Roslyn renders the squiggles and light bulbs,
no sidecar — plus one registered package (`CSharp.Refactor.Vsix.dll`,
`Commands.fs`) for the **Tools > CSharp.Refactor** menu, the same commands the
VS Code extension puts in its palette:

- Run the tool on this solution... (asks: apply, or report only)
- Run with --api-changes (rewrites the public surface)...
- Review advisory notes as a page
- Write a SARIF report (`csharp-refactor.sarif` beside the solution)
- Create or open the configuration file (the `.editorconfig` block)
- Status (versions and wiring), About

The commands drive the `csharp-refactor` global tool (`dotnet tool install -g
csharp-refactor`); its output goes to a CSharp.Refactor pane in the Output
window, and open documents are saved before a run rewrites files. The menu
package logs to `%TEMP%CSharpRefactor.Vsix.log`. Verified on Visual
Studio 2026 (18.10): the analyzer fires, the package loads and registers its
seven commands.

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

The menu is the one piece of shell plumbing. `CSharpRefactor.vsct` compiles
to a command table (VSCT.exe, from the tools-only `Microsoft.VSSDK.BuildTools`
reference; its MSBuild targets fight SDK-style fsproj and are excluded), and
`EmbedCto.ps1` puts the table INSIDE two managed resource sets embedded in the
package assembly — `UseManagedResourcesOnly` makes the shell look for
`Menus.ctmenu` as an entry in a resource set, and a standalone manifest
resource of that name merges nothing, silently. Registration is the
hand-written `CSharp.Refactor.Vsix.pkgdef` (CreatePkgDef.exe would have to
load the fresh assembly, which Application Control denies here); the package
GUID in it, in `Commands.fs` and in the `.vsct` must agree, and nothing
checks that but a menu that appears. The package project targets net48 and
sits outside `CSharp.Refactor.slnx`, which CI builds on Linux.

Smoke test in the experimental instance: install with `VSIXInstaller /quiet
/rootSuffix:Exp`, write an empty `Extensions\extensions.configurationchanged`
under the Exp hive (the installer stamps only the real instance's) and delete
the Exp `ComponentModelCache`, then open a C# project with `devenv /rootSuffix
Exp` and read `%TEMP%\CSharpRefactor.Vsix.log` for `7 commands registered`.
