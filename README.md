# CSharp.Refactor

> The C# twin of [FSharp.Refactor](https://github.com/Thorium/fsharp-refactor)

Functional refactoring suggestions for C#, implemented in F# on Roslyn.
The rules care about correctness, measured performance and clear
functional idiom — and deliberately not about naming, layout or
conventions.

- light bulb quick fixes in your editor (Visual Studio, VS Code, Rider,
  anything that hosts Roslyn analyzers)
- a command-line tool that applies them in bulk, build-verified

Suggestions are `Info` severity: they mark an opportunity, not a defect, and
never gate your build.

Status: pre-release. Every rule of the v1 catalog (124 rules across
families A–J) is implemented and swept over real repositories; the
cross-project edit sets, the editor extensions and the publishing scripts
are still open. The design and the rule catalog with its guards are in
[DESIGN.md](DESIGN.md); [Rules.md](Rules.md) documents every shipped rule
(kept complete by tests, and the target of every finding's help link, e.g.
`Rules.md#cr0025--performance`); [CHANGELOG.md](CHANGELOG.md) has every
version's notes.

---

# Using it

## Quick start

Nothing to configure — the tool reads your project, reports what it would
change, and only edits when you tell it to:

```bash
dotnet tool install --global csharp-refactor
csharp-refactor Your.csproj --dry-run
```

That prints every fix it would make, with file and position, and writes
nothing. When the list looks right, drop the flag to apply them:

```bash
csharp-refactor Your.csproj
```

It refuses a compilation that does not already build, recompiles in memory
after every pass, and builds the project for real at the end, putting the
fixes back if that fails.

Point it at whatever you have — the kind is read off the path:

| | |
|---|---|
| `Your.csproj` | one project |
| `Thing.cs` | one source file — its project is found and analysed, but only that file is edited |
| `Your.sln`, `Your.slnx` | every C# project the solution lists |
| `src/` | the solution in that directory, or the projects beneath it |
| `"src/**/*.csproj"` | everything the glob matches |
| `build.csx` | one C# script — its own compilation (`#r`, `#load`, the NuGet cache), no MSBuild at all; a directory picks its loose scripts up too |
| `C:\git` | a workspace of checkouts: each analysed on its own into one report |

## Editor and CI setup

The analyzers ship as
[`CSharp.Refactor.Analyzers`](https://www.nuget.org/packages/CSharp.Refactor.Analyzers),
an ordinary Roslyn analyzer package. Reference it from the project you want
analysed and every host — `dotnet build`, Visual Studio, VS Code's C#
extension, Rider — loads it by itself:

```xml
<PackageReference Include="CSharp.Refactor.Analyzers" Version="*" PrivateAssets="all" />
```

The package is a development dependency: it only produces hints and quick
fixes, and nothing from it reaches your compiled output or your `bin`.

### Visual Studio 2022 / 2026

Install the [CSharp.Refactor extension](src/CSharp.Refactor.Vsix/README.md):
every C# project opened in the IDE gets the suggestions and `Ctrl+.` fixes,
with no project change and no effect on builds. Or use the package reference
above; both work, and a project with both sees each finding once.

### VS Code

Install the [CSharp.Refactor extension](src/CSharp.Refactor.VsCode/README.md).
It bundles the analyzers and, with your consent, wires them into every C#
project on the machine through MSBuild's user `ImportBefore` hook (one props
file under your profile, undone by a command). Or use the package reference.

### CI

The analyzers run inside the compiler, so a build can write the findings as
SARIF with no extra tool:

```bash
dotnet build -p:ErrorLog=findings.sarif
```

The tool's own report carries more — fixes as suggested changes, context
snippets, stable fingerprints — and pairs with `--dry-run` for a lint gate:

```yaml
      - run: dotnet tool install --global csharp-refactor
      - run: csharp-refactor src/Your.csproj --dry-run --report findings.sarif
      - uses: github/codeql-action/upload-sarif@v3
        if: always()
        with:
          sarif_file: findings.sarif
          category: csharp-refactor
```

A dry run exits 0 whether or not it found anything; `--fail-on-findings`
makes it exit 3 when a finding survives the filters, and `--baseline` turns
that into a ratchet: findings already in an earlier report are neither
reported nor fixed, only new ones surface.

## Applying fixes from the command line

```bash
csharp-refactor Your.csproj [--dry-run] [--codes CR0090,CR0103] [--categories correctness,performance] [--api-changes]
```

| Flag | |
|---|---|
| `--dry-run` | Report only: lists every fix it would make, writes nothing. |
| `--codes CR0090,CR0103` | Restrict the run to chosen rules. Naming a rule is an ask: it outranks the rule's default-off status and a config `none`. |
| `--categories <list>` | Restrict to kinds of rule: `correctness`, `performance`, `idiom`, `cosmetic`. For a repository you do not maintain, `correctness,performance` is the set worth a pull request. |
| `--api-changes` | Also apply fixes that change internal or public signatures and shapes, rewriting call sites across the solution (the reference oracle finds them in every project; a caller in a VB project holds the fix, an F# consumer holds the surface). Held back and counted without it. |
| `--report <file>` | Write every finding: `.sarif`, `.html` (a self-contained page) or `.csv`. |
| `--baseline <sarif>` | The ratchet: findings whose fingerprints appear in this earlier report are neither reported nor fixed. |
| `--fail-on-findings` | Exit 3 when any finding survives the filters. Exit contract: 0 clean, 1 failure, 2 usage, 3 findings. |
| `--notes [on\|off\|only]` | List fix-less advisory notes inline; `only` is the review pass, nothing written. By default a run prints its fixes and one per-category note count; reports and JSON always carry the notes. |
| `--format json` | Machine-readable stdout; progress prose moves to stderr. |
| `--rules` | Print the rule catalog. |
| `--create-config` | Append a commented block of every rule and key at its default to the directory's `.editorconfig`. |
| `--mcp` | Serve `analyze` and `list_rules` as an MCP server over stdio, one warm workspace across calls. |
| `--parse-only` | No references: syntax-only rules. Not a substitute for a real run. |
| `--max-passes <n>` | Fix-then-reanalyse iterations (default 5). |

Projects are loaded through `MSBuildWorkspace` — SDK-style projects of any
target, `net48` included, in every framework flavour they list. A legacy
project (`<Project ToolsVersion="4.0" …>` with explicit `<Compile>` items)
is read from its project file when the .NET Framework build host does not
answer: compile items, defines, `HintPath` references that exist, the
targeting pack's assemblies and project references. Without the build tree
(a reference a custom `.targets` adds, a sibling `bin`) some types stay
unresolved; the run says how many, the typed rules stand down where a type
is unknown, the in-memory check holds the error count, and the final build
runs through Visual Studio's `MSBuild.exe` where a baseline build succeeds
— otherwise the run is verified in memory only, and says so. An executable
another project of the solution references (its tests) is not a leaf: its
public shape stays unless `--api-changes` says otherwise, and every
dependent of a changed project is built too.
A C# script (`.csx`) is a compilation of its own: the file parsed as a
script, the running runtime as its references, `#r` resolved against the
script's directory, the runtime and the NuGet cache (`#r "nuget: Name,
Version"`), `#load` read by the compiler for context (a loaded file is
edited when it is swept as a target of its own, and a directory sweeps
every loose script). Nothing builds a script, so the run is verified in
memory: the error count may not grow, and a script host's globals (`Args`)
count as errors on both sides.

## Configuration

Rules are configured the way every Roslyn analyzer is, in `.editorconfig`,
nearest section winning:

```ini
[*.cs]
dotnet_diagnostic.CR0103.severity = none          # off
dotnet_diagnostic.CR0006.severity = suggestion    # a default-off rule, on
csharp_refactor.CR0006.then_at_least = 30         # a rule's knob

csharp_refactor.public_api   = false              # nothing links to this assembly
csharp_refactor.api_changes  = true               # --api-changes as a standing decision
csharp_refactor.suppressions = no-correctness     # all | no-correctness | none
csharp_refactor.ignore_paths = generated;external/imported
```

`csharp-refactor --create-config` writes the block for you, every value at
this build's default, so it changes nothing until you edit a line.

Individual findings are silenced with Roslyn's own means — `#pragma warning
disable CR0090`, `[SuppressMessage]`, a severity of `none` — and editors honour
those natively. The `suppressions` policy governs the tool: `no-correctness`
reports a suppressed correctness finding anyway (and never auto-fixes it),
`none` reports every suppressed finding, `--honor-suppressions` overrides
either for one run. The run summary counts what suppressions silenced.

A rule that shadows a Microsoft analyzer rule (CA2213, CA1031, CA2000 …)
stands down for that rule's shapes wherever the Microsoft rule is enabled in
the file's effective config, so nothing is reported twice and nothing
oscillates. [Rules.md](Rules.md) names each rule's twins.

---

# Improving it

## Building and testing

```bash
dotnet build
dotnet test
```

Before committing:

```bash
dotnet tool restore
dotnet fantomas src tests
dotnet dotnet-fsharplint lint src/CSharp.Refactor.Analyzers/CSharp.Refactor.Analyzers.fsproj
```

The test harness compiles C# string literals against the running framework,
runs the pure rules, applies every fix as text and recompiles; the host tests
drive the real `DiagnosticAnalyzer` and `CodeFixProvider` through Roslyn;
the tool tests sweep a synthetic project through `MSBuildWorkspace`.
`tests/run-tests.ps1` runs the lot. `benchmarks/PerfClaims` holds the
before/after pair behind every performance claim.

Beside the example-based suite, `tests/CSharp.Refactor.PropertyTests` is an
FsCheck suite over generated programs: a class of members each shaped for a
rule, and method bodies around boolean terms over three integers, with an
interpreter over the Roslyn tree as the oracle. Its properties are the
invariants the rules promise - every fix, editor-only alternatives included,
keeps the program compiling; no rule throws on a compilation recovered from
damaged text; applying fixes one at a time reaches a fixed point; and the
boolean and control-flow rewrites (CR0001, CR0005, CR0007, CR0008, CR0011)
keep the method's truth table at every step. A coverage test holds the
generators to their rules: if a shape stops firing the rule it was written
for, it says which. A rewrite that would compile and mean something else is
out of the compile invariant's reach, so a shape carries a decoy where it
can: the CR0100/CR0101 shape calls a method whose `FormattableString`
overload is `[Obsolete(error)]`, so an interpolation that re-binds the call
is a compile error; the CR0082 shape hands one tuple to `object`, where a
retype would not compile. What no decoy can express (a record made of a
type with behaviour, a record dumped into an exception message) stays with
the example suite.

## Design principles

1. **Never break user code.** A fix is only offered when it is provably safe;
   the borderline case produces no suggestion. A fix must be right without
   a compile: the same fix runs in the editor, where nothing verifies it.
2. **Guards over removal.** An unsafe rewrite gets a typed proof that the
   shape is safe and stands down where the proof fails; the rule is never
   deleted or blanket-disabled.
3. **Minimal edits.** Fixes are range-based text edits; formatting outside
   the edited range is untouched.
4. **Pure core, thin adapters.** Each rule is a pure function from a syntax
   tree and semantic model to suggestions; the Roslyn analyzer, the fix
   provider and the tool are one-line adapters over it.
5. **Hints point toward idiomatic C# only.** Reversible matters of taste
   belong to the IDE's refactorings, not to a diagnostic.

See [DESIGN.md](DESIGN.md) for the full safety model, the yields-to gate,
the planned catalog and the relationship to Microsoft's analyzers.
