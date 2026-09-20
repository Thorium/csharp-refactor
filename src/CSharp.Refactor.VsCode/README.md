# CSharp.Refactor for VS Code

Functional refactoring hints with one-click quick fixes for C#, delivered
through the C# extension's Roslyn language server.

The extension bundles the CSharp.Refactor analyzer assembly and, on first
activation, **asks** to wire it into every C# project on this machine
through MSBuild's user `ImportBefore` hook (one props file under your user
profile). Decline and nothing is written; `CSharp.Refactor: Wire analyzers`
re-offers it any time, and `Remove the machine-wide wiring` undoes it. A
project opts out with `<CSharpRefactorDisable>true</CSharpRefactorDisable>`.

After wiring, restart the C# language server (or reload the window) and open
a C# file: suggestions appear as `CR`-prefixed hints with a light-bulb fix.
`CSharp.Refactor: Status` reports the versions and the wiring.

Five more commands drive the `csharp-refactor` dotnet tool (offering to
install it when missing):

- `CSharp.Refactor: Run the tool on this workspace` — the light bulbs fix
  one finding at a time; this runs the sweep. Picks a solution or project,
  then `Report only` (`--dry-run`), `Apply fixes`, or `Apply fixes with
  --api-changes`, in the integrated terminal. Every pass is verified and
  put back on error, as on the command line.
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
