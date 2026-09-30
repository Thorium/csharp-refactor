# CSharp.Refactor

> The C# little-sister of [FSharp.Refactor](https://github.com/Thorium/fsharp-refactor)

Functional refactoring suggestions for C#, implemented in F# on Roslyn.
The rules care about correctness, measured performance and clear idiomatic C# — and deliberately not about naming, layout or conventions.

- light bulb quick fixes in your editor (Visual Studio, VS Code, Rider,
  anything that hosts Roslyn analyzers)
- a command-line tool that applies them in bulk, build-verified

Suggestions are `Info` severity: they mark an opportunity, not a defect, and
never gate your build.

---

# Using it

The tool is fully offline: AI has been used to make it, but it doesn't use AI.

## Quick start

Nothing to configure — the tool reads your project, reports what it would
change, and only edits when you tell it to:

```bash
dotnet tool install --global csharp-refactor
csharp-refactor Your.csproj --dry-run --api-changes
```

That prints every fix it would make, with file and position, and writes
nothing. When the list looks right, drop the flag to apply them:

```bash
csharp-refactor Your.csproj --api-changes
```

If you don't want change public methods / API, drop `--api-changes`:

```bash
csharp-refactor Your.fsproj
```

It refuses a compilation that does not already build, recompiles in memory
after every pass, and builds the project for real at the end, putting the
fixes back if that fails.

#### For light bulbs while you type, see [VS Code / Ionide](#vs-code) and [Visual Studio](#visual-studio-2022--2026) IDE-plugin instructions below.

<img width="1044" height="297" alt="image" src="https://github.com/user-attachments/assets/762ff17d-2c58-4c8a-ae27-569117b88d4e" />

#### For agentic scenarios the dotnet tool supports MCP.

Using the tool:

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
with no project change and no effect on builds. Its Tools > CSharp.Refactor
menu runs the `csharp-refactor` tool on the open solution (apply or dry run,
with or without `--api-changes`), writes the SARIF report, opens the notes
page and the `.editorconfig` block — the VS Code extension's commands, in
Visual Studio. Or use the package reference
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
| `--codes CR0090,CR0103` | Restrict the run to chosen rules; the other rules do not run at all. Naming a rule is an ask: it outranks the rule's default-off status and a config `none`. |
| `--categories <list>` | Restrict to kinds of rule: `correctness`, `performance`, `idiom`, `cosmetic`. The rules of the other kinds do not run. For a repository you do not maintain, `correctness,performance` is the set worth a pull request. |
| `--define <symbols>` | Preprocessor symbols the run defines, like `csc -define:` or a `DefineConstants` entry: repeatable, or `;`/`,`-separated (`--define:A` and `-d:A` work too). Added to each project's own `DefineConstants` — DEBUG, TRACE and the project's constants stay. See [Code under `#if`](#code-under-if-local_build). |
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
| `--max-passes <n>` | Fix-then-reanalyse iterations (default 5). A pass after the first re-analyses only the files the previous pass edited or held a fix in. |

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

### Code under `#if LOCAL_BUILD`

Code behind a preprocessor symbol nothing defines is not in the parse tree
at all: the tool neither analyses it nor sees what a fix elsewhere does to
it, and the verification build compiles without it too. A repository that
keeps, say, a local-development path under `#if LOCAL_BUILD` names the
symbol for the run:

```bash
csharp-refactor Your.sln --define LOCAL_BUILD        # or --define:LOCAL_BUILD, -d:LOCAL_BUILD
csharp-refactor Your.sln --define "LOCAL_BUILD;CI"   # several at once
```

or once for every run, in `.editorconfig` (`csharp_refactor.defines =
LOCAL_BUILD`, below). The run uses both, and says at the start which symbols
are active and where each came from. The symbols are **added** to what each
project defines, exactly as `dotnet build` would see them with
`DefineConstants` set in the environment: the SDK's `DEBUG`/`TRACE`, the
target framework's `NET8_0_OR_GREATER` and the project's own
`$(DefineConstants);FOO` all stay. (`-p:DefineConstants=LOCAL_BUILD` would
not do: a global property replaces every one of them.) Every MSBuild the run
starts — the workspace's design-time build, restores, the verification
builds — gets them that way, and the loaded C# projects, legacy projects and
`.csx` scripts get them in their parse options as well, so a project that
assigns `DefineConstants` outright is still analysed with them. A script
whose `#r` cannot be resolved while another `#r` sits under `#if LOCAL_BUILD`
gets the hint to pass `--define LOCAL_BUILD`.

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
csharp_refactor.skip_microsoft_duplicates = true  # the Microsoft analyzers run here: skip what they already report
csharp_refactor.ignore_paths = generated;external/imported
csharp_refactor.defines = LOCAL_BUILD             # like --define on every run
```

`csharp_refactor.defines` is a run-level key: it is read once per run, from
the `.editorconfig` nearest the target (the walk stops at `root = true`),
and adds to `--define`, never replacing it.

`csharp-refactor --create-config` writes the block for you, every value at
this build's default, so it changes nothing until you edit a line. Each
rule's knobs follow its severity line, at their defaults; a knob whose
default is not one value (CR0023's measured floor, a rule's `wrap_column`,
which falls back to the file's `max_line_length`) is written commented out.

A generated file is never touched, in the editor or by the tool: one under
`bin`, `obj`, `node_modules` or `.git`, one named `*.g.cs`, `*.designer.cs`,
`*.generated.cs` or `*.g.i.cs`, one listed in `ignore_paths`, and one whose
header says `<auto-generated>` (a T4 template's, a resx designer's, the
SDK's AssemblyInfo) or "This code was generated by".

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

**Warnings as errors**: The tool uses the same settings as your CI build 
(TreatWarningsAsErrors, WarningsAsErrors, .editorconfig severities). 
If a fix would trigger an analyzer error, the tool leaves that file alone, 
names the analyzer and the rule, and tells you what to change. 
Use <WarningsNotAsErrors> to take those fixes anyway (the warning stays visible), 
or <NoWarn> to silence the analyzer for everyone.

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

A multi-line input is an indented block through the `csharp` helper
(tests/CSharp.Refactor.Tests/TestSource.fs): the closing quotes'
indentation is cut from every line, so the rule sees the class at column 0,
and Fantomas can move the block without changing it.

```fsharp
let source =
    csharp
        """
        class C
        {
            int F(int x) => x + 1;
        }
        """
```

`fires 3 "CR0046" source` returns a rule's suggestions and asserts their
count, and `assertFired` does the same for a list filtered by hand. A
mismatch lists the text each suggestion fired on, so the failure names the
site that was missed, where a bare count would not.

A literal that must be a constant (an attribute argument, a `[<Literal>]`,
a printf format) or holds a tab, a `\r` or trailing whitespace stays an
escaped `"...\n..."` string.

Beside the example-based suite, `tests/CSharp.Refactor.PropertyTests` is an
FsCheck suite over generated programs: a class of members each shaped for a
rule, and method bodies around boolean terms over three integers, with an
interpreter over the Roslyn tree as the oracle. Its properties are the
invariants the rules promise - every fix, editor-only alternatives included,
keeps the program compiling; no rule throws on a compilation recovered from
damaged text; applying fixes one at a time reaches a fixed point; and the
boolean and control-flow rewrites (CR0001, CR0005, CR0007, CR0008, CR0011)
keep the method's truth table at every step. The generators reach 121 of
the 124 rules (CR0017 is subsumed by CR0160 wherever both read a shape;
CR0156 and CR0158 need C# 15, which the harness cannot parse), and a
coverage test tries every shape on its own and all of them together: if a
shape stops firing the rule it was written for, it says which. Two more
properties dress the programs the way real files come — `#region`s around
members, a `#if` and a comment inside a member, CRLF, tabs, a `#pragma` —
and require every fix to compile and to leave what was there (no swallowed
directive or comment, no bare LF in a CRLF file); and compile them at every
language version from C# 7.3 to 13, requiring a fix to use no syntax newer
than the file's own. Both run deterministically over every shape as well
as over random programs. A rewrite that would compile and mean something else is
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
