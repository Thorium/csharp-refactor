# CSharp.Refactor for VS Code

**[Get it from the Visual Studio Marketplace](https://marketplace.visualstudio.com/items?itemName=TuomasHietanen.csharp-refactor-vscode)**

View -> Command Palette -> CSharp.Refactor to drive the command line tool for the full project:

<img width="493" height="185" alt="image" src="https://github.com/user-attachments/assets/1f14b4ed-5277-4d6a-be18-681fa8b14f32" />

And IDE light bulbs while you type

<img width="1227" height="215" alt="image" src="https://github.com/user-attachments/assets/1259c256-cad4-4f4c-8b41-1c6a0bdbd5e1" />


Functional refactoring hints with one-click quick fixes for C#, delivered
through the C# extension's Roslyn language server.

The extension bundles the CSharp.Refactor analyzer assembly and, on first
activation, **asks** to wire it into every C# project on this machine
through MSBuild's user `ImportBefore` hook (one props file under your user
profile). Decline and nothing is written; `CSharp.Refactor: Wire analyzers`
re-offers it any time, and `Remove the machine-wide wiring` undoes it. A
project opts out with `<CSharpRefactorDisable>true</CSharpRefactorDisable>`.

The props file adds the analyzers only for a compiler that can load them:
MSBuild 17.14 or newer (Visual Studio 17.14, .NET SDK 9.0.300). A project
built with an older SDK is left alone. A project that also references the
`CSharp.Refactor.Analyzers` package at the same version sees each finding
once.

VS Code runs nothing on uninstall, and a disabled extension keeps its
folder: run `Remove the machine-wide wiring` before disabling or
uninstalling the extension. After an uninstall the props file is inert,
since it names a folder that is gone, and can be deleted.

After wiring, restart the C# language server (or reload the window) and open
a C# file: suggestions appear as `CR`-prefixed hints with a light-bulb fix.
`CSharp.Refactor: Status` reports the versions and the wiring.

Five more commands drive the `csharp-refactor` dotnet tool (offering to
install it when missing):

- `CSharp.Refactor: Run the tool on this workspace` — the light bulbs fix
  one finding at a time; this runs the sweep. Picks a solution or project,
  then `Report only` (`--dry-run`), `Apply fixes`, or `Apply fixes with
  --api-changes`, in the integrated terminal. A run that writes asks to
  save unsaved editors first: the tool works on the files on disk. Every
  pass is verified and put back on error, as on the command line.
- `CSharp.Refactor: Run the tool with --api-changes` — the same sweep with
  the scope gate opened: public shapes and cross-file rewrites too.
- `CSharp.Refactor: Write a SARIF report for this workspace` — a dry run
  with `--report`, for code scanning or as the `--baseline` of a later run;
  `.csv` and `.html` paths are written in those formats instead.
- `CSharp.Refactor: Create or open the configuration` — appends the
  commented block of every rule and key at its default to the workspace's
  `.editorconfig` and opens it.
- `CSharp.Refactor: Review advisory notes as a page` — the fix-less
  findings as a self-contained HTML page in the browser.
