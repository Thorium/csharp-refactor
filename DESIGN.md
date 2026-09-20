# CSharp.Refactor — design

> The C# twin (or little sister) of [FSharp.Refactor](../FSharp.Refactor/README.md): functional
> refactoring suggestions for C#.


## 1. Purpose and spirit

The same product as FSharp.Refactor, for C#:

- light-bulb quick fixes in the editor (VS Code, Visual Studio 2022/2026),
- a command-line tool that applies them in bulk, build-verified, with
  reports, a baseline ratchet and an MCP server for agents.

What the rules care about, in the same order as the F# side:

1. **Correctness** — the code does something other than what it looks like
   it does: a race, a swallowed exception, a disposable that leaks, a task
   nobody observes, a comparison that never holds.
2. **Performance** — correct, but doing work it need not: allocations that
   need not happen, repeated work, a scan where a lookup would do. Every
   performance claim is measured in `benchmarks/PerfClaims`.
3. **Idiom** — the same behaviour written the way clear, functional C# writes
   it: expressions over statements, patterns over casts, immutable records
   over mutable bags, LINQ over flag loops, `await` over `.Result`. Opinionated
   towards F# and functional programming, without pretending C# is F#.
4. **Cosmetic** — punctuation and spelling. Few, and mostly off by default,
   because C# already has `dotnet format` and the IDE rules for that.
   We don't want to do rules that would contradict with dotnet format.

What it deliberately does NOT care about: naming, file layout, member
ordering, `var` versus explicit types, braces, `this.`, method length,
cyclomatic complexity. Those are house style and other tools' business
(IDExxxx, StyleCop, SonarQube). Agent-generated code makes structure rules
matter less every year; what it does not make matter less is whether the
code is right and whether it does needless work.

Two things change when the language is C#, and they shape everything below:

- **Roslyn is the host.** C# already has a first-class analyzer and code-fix
  pipeline, loaded by every editor and by the compiler itself. There is no
  sidecar, no analyzer-SDK version pairing, no `analyzersPath` setting. The
  analyzers ship as an ordinary Roslyn analyzer NuGet package (`analyzers/dotnet/cs`)
  and light up in VS, VS Code (C# Dev Kit / C# extension), Rider and
  `dotnet build` by reference alone. Both IDE plugins are therefore much
  thinner than their F# counterparts.
- **Microsoft already ships hundreds of C# rules** (CAxxxx, IDExxxx, SYSLIBxxxx).
  Many F# rules exist only because those never ran on F#. On C# we do not
  duplicate a rule that has a fix and is on by default; we implement a rule
  Microsoft has note-only or off by default, and we stand down automatically
  when the user has turned the Microsoft rule on. See
  [§7](#7-relationship-to-microsoft-analyzers).

Rule codes are `CR0001`… (`CR` is unused by Microsoft, StyleCop `SA`, Sonar
`S`, Roslynator `RCS`, Meziantou `MA`, CodeRush `CRR`). Codes are assigned in
order of introduction and never reused; the v1 set below is numbered by
family for readability, later rules append.

## 2. Non-goals

- Formatting. Fixes are minimal range-based text edits; the tool never runs
  the Roslyn formatter over a file, and nothing it emits fights `dotnet format`
  or `.editorconfig` layout rules.
- A second configuration system. Rules are configured the way every Roslyn
  analyzer is: `.editorconfig`. See [§6](#6-configuration-and-suppression).
- Reimplementing Microsoft's fixes. Where CA/IDE has a fix and is on by
  default, the row in the [overlap table](#72-overlap-table) says "defer".
- Rules that move code AWAY from idiomatic C#, or reversible matters of taste
  (`switch` statement ↔ expression where both read fine, query ↔ method
  syntax, expression-bodied ↔ block-bodied). Those belong to Roslyn's
  user-invoked refactorings, not to a diagnostic.
- Naming, structure, conventions. See §1 and [§10](#10-rules-deliberately-absent).

## 3. Product surface

| Piece | Package / id | What it is |
|---|---|---|
| Analyzers | `CSharp.Refactor.Analyzers` (NuGet, `DevelopmentDependency`) | Roslyn `DiagnosticAnalyzer` + `CodeFixProvider` assembly, F#, `netstandard2.0`, with `FSharp.Core.dll` beside it in `analyzers/dotnet/cs`. Loads in csc, VS, VS Code, Rider, `dotnet format`. |
| Apply tool | `csharp-refactor` (dotnet tool) | `MSBuildWorkspace` host: analyses a project/solution/directory/glob, applies non-overlapping fixes bottom-up, recompiles in memory per pass, builds at the end, reports (SARIF/HTML/CSV/JSON), ratchets (`--baseline`), serves MCP (`--mcp`). Same flag contract as `fsharp-refactor`. |
| VS Code | `csharp-refactor-vscode` | Bundles the analyzer assembly; with consent wires it machine-wide (an MSBuild user `ImportBefore` props) or per project (`PackageReference`); commands to run the tool, write reports, review notes, create config. |
| Visual Studio 2022/2026 | `CSharp.Refactor.vsix` | Ships the analyzer as a VSIX analyzer asset, so every C# project in the IDE gets the light bulbs with no project change and no build impact; a Tools menu drives the tool. No sidecar process. |
| MCP | `csharp-refactor --mcp` | `analyze` (target, codes/categories, parseOnly, apply) and `list_rules` over stdio; one warm workspace across calls. |

One version for all of them, in `Directory.Build.props`, released in lockstep.

Severity: every finding is `Info` (VS: "suggestion" dots + light bulb; not
in build output at normal verbosity; never gates a build). Priority rules are
`Warning`, as on the F# side. Every message ends with its category in
brackets, because editors truncate from the right. Cosmetic rules that
remove text also carry `WellKnownDiagnosticTags.Unnecessary`, so the
redundant text is faded rather than squiggled.

## 4. Architecture

### 4.1 Repository layout

```
CSharp.Refactor/
  Directory.Build.props            one <Version> for the whole solution
  CSharp.Refactor.slnx
  README.md  Rules.md  CHANGELOG.md  DESIGN.md (this file)
  src/
    CSharp.Refactor.Analyzers/     F#, netstandard2.0; one module per rule family; Analyzers.fs + CodeFixes.fs adapters
    CSharp.Refactor.Tool/          F#, net10.0 (rolls forward); MSBuildWorkspace host, apply loop, reports, MCP
    CSharp.Refactor.VsCode/        TypeScript extension
    CSharp.Refactor.Vsix/          VSIX manifest + F# package for the Tools menu
  tests/
    CSharp.Refactor.Tests/         xunit; string-literal inputs, compile-verified fixes, Rules.md kept in step
    Sample/                        a C# project the sweep must leave clean (idempotency, from-scratch compare)
  benchmarks/
    PerfClaims/                    BenchmarkDotNet; one before/after pair per performance claim
```

### 4.2 F# on Roslyn

The analyzer assembly is F# because the rule logic is pattern matching over
trees, which is F#'s home ground, and because the shared vocabulary with
FSharp.Refactor (Suggestion, Fix, Scope, Visibility, Configuration, the
purity proofs) ports as modules rather than as ideas. Constraints this
imposes, all verified by the M0 spike:

- **`netstandard2.0`.** Visual Studio's devenv process hosts Roslyn on .NET
  Framework, so a Roslyn analyzer must target `netstandard2.0` to load
  everywhere (csc under `dotnet build` is .NET Core and would accept more;
  VS would not). FSharp.Core supports netstandard2.0; the analyzer uses no
  API above it.
- **Roslyn version pin.** An analyzer compiles against ONE
  `Microsoft.CodeAnalysis.CSharp` version and loads in any host with that
  version or newer, binding at runtime to the host's copy. Pin the lowest
  version whose syntax model covers the C# we need to read (C# 13: 4.12–4.14;
  VS 17.12+, .NET 9 SDK+). The tool hosts its own newer Roslyn (5.x from the
  .NET 10 SDK) and loads the same analyzer assembly. Syntax kinds the pinned
  version does not name (C# 14 `field`, extension members) arrive as nodes
  whose kind the rule code has no case for; every walker treats an unknown
  node as opaque, never as a match, and a test feeds a C# 14 file through
  every rule.
- **FSharp.Core beside the analyzer.** The compiler's analyzer loader
  resolves an analyzer's dependencies from its own directory, so
  `FSharp.Core.dll` is packed into `analyzers/dotnet/cs` next to the
  analyzer (`IncludeBuildOutput=false`, explicit `None Pack` items, as the
  F# package does). Risk: two F# analyzers in one compilation wanting
  different FSharp.Core versions; the loader keeps one per assembly name.
  Mitigation: a widely used FSharp.Core pin, and a test that loads the
  package through `csc` rather than through the test host.
- **MEF export attributes from F#.** `[<DiagnosticAnalyzer(LanguageNames.CSharp)>]`
  on a class inheriting `DiagnosticAnalyzer`, and
  `[<ExportCodeFixProvider(LanguageNames.CSharp, Name = "…")>] [<Shared>]` on
  a `CodeFixProvider` subclass. Plain CLR attributes; F# closures compile to
  nested classes MEF ignores. Verified in M0 in all three hosts.
- **`Microsoft.CodeAnalysis.Analyzers` (RS1xxx) do not analyse F# source**, so
  the analyzer-correctness checks a C# analyzer project gets for free are
  replaced by tests: every code in the catalog has a descriptor, every
  descriptor is in `SupportedDiagnostics`, every fixable code has a fix
  provider registered for it, every message ends in its category.

### 4.3 Pure core, thin adapters

Identical to the F# side's third design principle. Each rule is a pure function

```fsharp
SyntaxTree -> SemanticModel option -> RuleContext -> Suggestion list
```

where `Suggestion` carries the code, message, span, and zero or more `Fix`es,
each a list of `(TextSpan * replacement)` text changes plus an editor-only
flag and an equivalence key. `Analyzers.fs` turns suggestions into Roslyn
`Diagnostic`s; `CodeFixes.fs` turns `Fix`es into `CodeAction`s whose only
operation is `document.WithText(text.WithChanges changes)`. No
`SyntaxGenerator`, no `Formatter.Format`, no `ReplaceNode` that re-serialises
trivia: the edited span is what changes, nothing else. The tool calls the
pure core directly and never depends on MEF.

One memoised walk per file version: the analyzer registers a single
`SemanticModelAction` (and a `SyntaxTreeAction` for the parse-only rules),
builds one indexed view of the tree (the `AstIndex` twin: nodes by kind, by
enclosing member, invocations by callee name), and every enabled rule reads
that. A disabled rule costs nothing. `EnableConcurrentExecution` is on;
`ConfigureGeneratedCodeAnalysis(None)` lets Roslyn skip generated files
(`.g.cs`, `<auto-generated>`, `[GeneratedCode]`) before we see them.

Deep trees: the F# analyzers run on 64 MB worker threads because
`a + b + c + …` nests one node per operand and a `StackOverflowException`
ends the host. Roslyn's own walkers use explicit stacks, but our recursive
rule functions do not; the `DeepStack` worker pool is kept.

### 4.4 Typed and untyped rules

Every rule declares whether it needs the semantic model. The parse-only
rules (`--parse-only`, and the editor before a project has loaded) run from
syntax alone; the typed rules never guess: a receiver whose type the model
cannot settle stands the rule down. Roslyn makes the typed gate cheaper than
FCS did — `GetSymbolInfo`, `GetTypeInfo`, `GetOperation` are per-node and
lazy — so the default is typed, and a rule is parse-only only when its shape
is decidable from syntax alone (attribute suffix, `$"no holes"`, invisible
Unicode, `else { if }`).

`IOperation` is used where it removes syntax casework (conversions,
deconstruction, pattern semantics, `checked` context); syntax is used where
the edit is spelled, since every fix is a text edit at a syntax span.

### 4.5 Capability gates

A fix that needs a newer API or language level asks the compilation, never
the target framework moniker: `Random.Shared` is offered when the member
resolves in the references, `FrozenSet<T>` when the type does, `init` when
`IsExternalInit` does, `[GeneratedRegex]` when the attribute does, `using var`
when `LanguageVersion >= CSharp8`. Multi-targeted projects are swept
narrowest first; on the wider passes a capability fix is guarded with the
SDK-defined constant (`#if NET8_0_OR_GREATER`) only in a file that already
uses `#if`, and a plain fix elsewhere with the all-frameworks build as the
arbiter. `--no-if-defs` turns the pairing off. Unlike the F# tool there is no
hunt for a project-defined constant: the SDK defines `NETx_0_OR_GREATER`
for every C# project.

## 5. Safety model

The F# principles, restated for Roslyn. None are relaxed; one is added (the
speculative check).

1. **Never break user code.** A fix is offered only where it is provably
   safe; the borderline shape produces no suggestion. A fix must be right
   WITHOUT a compile: the same fix runs in the editor, where nothing verifies
   it and nothing puts it back. The tool's per-pass recompile and final build
   are a backstop against our own bugs, never a licence.
2. **Guards over removal.** When a rewrite is unsafe on some shape, the rule
   gains a typed proof that the shape is safe and stands down where the proof
   fails; the rule is never deleted or blanket-disabled. Purity proofs are
   shared, not per rule: `isPureExpression` (literals, locals and parameters
   not assigned in the scope in question, `readonly` field and get-only
   property reads, `nameof`/`typeof`/`sizeof`, tuple construction, operators
   on those with no user-defined overload) and `callsOnlyCore` (invocations of
   members known effect-free: `System.String`, `System.Math`, `System.MathF`,
   `System.Char`, `Enumerable` operators over pure lambdas, `Path`
   string functions, tuple and `Nullable` members).
3. **The speculative check.** Roslyn can re-bind one file in a forked
   compilation cheaply: `compilation.ReplaceSyntaxTree(old, patched)` then a
   fresh `SemanticModel`. Every fix that can change name or overload
   resolution — adding a `using`, changing a type, a signature, a receiver, a
   `+` operand, a lambda substitution — is applied to a copy of the tree
   before it is offered, and stands down if the patched file has any new
   error, or if any symbol resolved at a site the fix did not touch now
   resolves differently. This turns "a fix must be right without a build"
   from a discipline into a per-fix proof, at the cost of one file re-bind
   per candidate finding. It never replaces a semantic guard (a rewrite can
   compile and mean something else); it catches the resolution class of bug
   that the F# side's 0.8.23 audit found by hand.
4. **Minimal edits.** Trivia outside the edited span is untouched. Roslyn
   trivia makes the "comment guard" exact: a fix whose removed span contains
   a comment, a `#region`, a `#pragma` or a `#if` line stands down
   (multi-line fixes never cross a preprocessor directive), and the edited
   text's comments are compared with the original's before an edit is
   offered.
5. **Scope gates.** A fix that changes a declaration's compiled shape
   (`class` → `record`, `record` → `readonly record struct`, `Tuple` →
   value tuple, `set` → `init`, a signature made `async`) fires on
   `private`/`internal`/`file` declarations, and on public ones only under
   `--api-changes` or `csharp_refactor.public_api = false`. An `OutputType`
   of `Exe`/`WinExe` is a leaf and opens the gate by itself; a library with
   `InternalsVisibleTo` closes it for `internal` too. Editors offer the
   public-declaration fix anyway, with the "CHANGES THE PUBLIC SHAPE" clause —
   per-site consent from the one person who can answer.
6. **Serialization cannot be detected**, so no rule infers that a shape
   change is safe to serialize. The heuristic from FR0157 is kept — a type
   named as a `Serialize<T>`/`Deserialize<T>` argument or in a `DbSet<T>`,
   carrying `[DataContract]`/`[Serializable]`/`[JsonSerializable]` or a
   `[Json*]`/`[DataMember]` property attribute, stands the shape rules
   down — and the message says what changes.
7. **Editor offers.** Roslyn gives one diagnostic several `CodeAction`s
   natively. The first is the auto-applicable fix the CLI takes; the others
   (`Guid.NewGuid()` for `new Guid()`, `CurrentCulture` beside
   `InvariantCulture`, the positional form of a record) are editor-only. A
   rule whose only safe remedy is a design decision is a note (no fix) and
   prints under `--notes`.
8. **Performance claims are measured**, on both axes, in PerfClaims: a
   performance rule's rewrite wins on wall clock or allocation, an idiom
   rule's holds parity, and a slower-but-idiomatic shape goes behind a
   default-off knob, never into the default. Interned literals and tier-0
   JIT are controlled for (runtime-built strings, 5M iterations).
9. **Nothing is exempted by convention.** A rule that is noisy against a
   common C# coding standard gets a lower category or default-off, decided
   from a sweep over every C# checkout under `C:\git`, never a special case
   for "Try-named methods" and never advice to add a `#pragma`.
10. **What a gate holds back is reported, not hidden.** The run ends with the
    held-back counts and the config key that would release them.

## 6. Configuration and suppression

`.editorconfig`, because every host already reads it and a C# repository
already has one:

```ini
[*.cs]
# per rule: the standard Roslyn switch; none turns the rule off entirely
dotnet_diagnostic.CR0006.severity = suggestion
dotnet_diagnostic.CR0142.severity = none

# rule knobs
csharp_refactor.CR0006.then_at_least = 30
csharp_refactor.CR0006.else_at_most  = 2
csharp_refactor.CR0040.sync_swap     = true
csharp_refactor.CR0106.utc_now       = true
csharp_refactor.CR0145.uses          = 6

# run-level keys (read by the tool and the analyzers alike)
csharp_refactor.public_api   = false          # nothing links to this assembly
csharp_refactor.api_changes  = true           # --api-changes as a standing decision (implies public_api = false)
csharp_refactor.suppressions = no-correctness # all | no-correctness | none
csharp_refactor.ignore_paths = generated;external/imported
csharp_refactor.hints        = csharprefactor.hints   # extra CR0011 rules, one per line
```

- Default-off rules are enabled with `dotnet_diagnostic.CRxxxx.severity = suggestion`
  (or `--codes CRxxxx`, which outranks both the default and a `none`, as on
  the F# side; `--categories` never wakes a default-off rule).
- `csharp-refactor --create-config` appends a commented block of every rule
  and key at its default to the nearest `.editorconfig` (or writes one) and
  never overwrites a line that exists.
- Suppression is Roslyn's: `#pragma warning disable CR0064`,
  `[SuppressMessage("CSharp.Refactor", "CR0064")]`, severity `none`. The
  tool runs `CompilationWithAnalyzers` with `reportSuppressedDiagnostics`,
  so the `suppressions` policy (`no-correctness`: a pragma on a correctness
  rule is reported anyway and never auto-fixed; `none`: every pragma is
  reported) works as on the F# side, and the run summary counts what pragmas
  silenced. Editors honour pragmas natively regardless.
- `ignore_paths`: additive over the built-in defaults (`node_modules`,
  `bin`, `obj`, `*.g.cs`, `*.designer.cs`, `*.generated.cs`); a bare name
  matches a path segment, a slash matches anywhere, `*`/`**` are globs.
- Tunables are integers or booleans (`true`/`false`/`on`/`off`/`1`/`0`).
- A rule that lays out a rewritten line reads its own `wrap_column` knob,
  else the file's `max_line_length` (the key formatters read), else 120.
  A malformed value fails open to the default and never breaks the editor.
- Precedence is Roslyn's: the nearest `.editorconfig` section wins; the tool
  reads the same `AnalyzerConfigOptions` the compiler does, so there is one
  answer per file.

## 7. Relationship to Microsoft analyzers

### 7.1 The yields-to gate

Every CR rule that shadows a Microsoft rule declares it
(`YieldsTo = ["CA2213"]`). Per file, the analyzer reads the effective
severity of each shadowed id from the analyzer config options (the SDK
writes a global config with `dotnet_diagnostic.CAxxxx.severity` for every
NetAnalyzers rule according to `AnalysisLevel`/`AnalysisMode`, so the value
is present whenever the analyzers are on):

| Shadowed rule's effective severity | CR rule |
|---|---|
| absent (NetAnalyzers off, or not a NetAnalyzers id) or `none` | runs |
| `silent`, `suggestion`, `warning`, `error` | stands down for that id's shapes |

A repository that turns CA2213 on never sees the same finding twice; one
that leaves it at its default (off) gets ours; nothing oscillates. Analyzers
outside the SDK (Roslynator, SonarLint, Microsoft.VisualStudio.Threading.Analyzers,
Meziantou) are not tracked; their overlap is named per rule and left to
`.editorconfig`.

The tool could also load Microsoft's fix providers from the SDK and apply
CA/IDE fixes in the same verified pass (`--with-microsoft`). That is v2: it
is what `dotnet format analyzers` does today without verification; v1
applies CR rules only.

### 7.2 Overlap table

F# rules whose C# shape Microsoft already reports WITH a fix, on by default
at the SDK's default analysis level. These get no CR rule; the tool defers.
(Defaults as of the .NET 9/10 SDK; the gate above is what matters.)

| F# rule | C# shape | Microsoft rule (with fix) | Decision |
|---|---|---|---|
| FR0010 (part) | `c ? true : false` | IDE0075 | defer; CR0001 keeps the statement shape |
| FR0012 (part) | `x == true`, `.Where(p).Any()`, `.Count() == 0` | IDE0100, IDE0120, CA1827 | yields per hint |
| FR0014 | `ContainsKey` + indexer | CA1854 | defer |
| FR0015 (hoist) | regex construction hoisted | SYSLIB1045 (`[GeneratedRegex]`) | yields; CR0109 covers targets below .NET 7 |
| FR0018 | check-then-add | CA1864 | defer |
| FR0019 | Equals without GetHashCode | CS0659 (compiler) | n/a |
| FR0026 | backing field + trivial get/set | IDE0032 (auto-property) | defer |
| FR0033 | member touching no instance state | CA1822 | defer |
| FR0034 (part) | `HasValue ? Value : d` | IDE0029/IDE0030/IDE0270 | defer; CR0004 keeps the payload-use shapes |
| FR0037 (part) | `JsonSerializerOptions`, `SearchValues` in loops | CA1869, CA1870 | yields |
| FR0038 | char overloads | CA1834, CA1847, CA1865–CA1867 | defer |
| FR0039 | `ToLower() ==` | CA1862 | defer |
| FR0040 | redundant `ContainsKey` before `Remove` | CA1853, CA1868 | defer |
| FR0044 | `throw ex;` | CA2200 | defer |
| FR0045 | `x == double.NaN` | CA2242 | defer |
| FR0048 | placeholder without argument | CA2241 | defer |
| FR0052 | `Count == 0` on concurrent collections | CA1836 | defer |
| FR0053 | `BitConverter.ToString().Replace` | CA1872 | defer |
| FR0061 | wrong parameter name in `ArgumentException` | CA2208 | defer |
| FR0068 | duplicate enum values | CA1069 | defer |
| FR0077 | missing interface members | CS0535 + "Implement interface" | n/a |
| FR0085 | redundant `new` | `new T()` → `new()` IDE0090 | defer |
| FR0095 | lambda restating a method | IDE0200 (method group) | defer |
| FR0097 | redundant parentheses | IDE0047 | defer |
| FR0098 | `System.Int32` → `int` | IDE0049 | defer |
| FR0106 | `Substring` → `AsSpan` | CA1846 | defer |
| FR0118 (omitted token) | forward the `CancellationToken` | CA2016 | defer; CR0055 keeps the explicit `None` |
| FR0124 (count, interpolated template) | template/argument mismatch | CA2017, CA2254 | defer; CR0114 keeps the remainder |
| FR0130 | `static readonly` constant → `const` | CA1802 | defer |
| FR0139 | LINQ where a property exists | CA1826, CA1829, CA1860 | defer |
| FR0140 | construct-then-assign → object initializer | IDE0017 | defer |
| FR0145 | unassigned `required` members | CS9035 | n/a |
| FR0155 | seal a class nothing inherits | CA1852 | defer |

F# rules whose C# shape Microsoft reports note-only or off by default get a
CR rule WITH the F# guards and, where the F# side has one, a fix; each such
rule yields to its Microsoft twin when that twin is on: CA2000 (CR0060),
CA1001 (CR0061), CA2213 (CR0062), CA1031 (CR0064), CA2219 (CR0066), CA1065
(CR0067), CA2201 (CR0068), CA2211 (CR0084), CA2214 (CR0086), CA2002 (CR0047),
CA1849 (CR0042), CA1851 (CR0030), CA1842/CA1843 (CR0054), CA2016 (CR0055),
CA1305 (CR0105), CA2100/CA3001 (CR0120), CA5350/CA5351/CA5359/CA5364/CA5386/CA5397
(CR0125).

Rules with no Microsoft twin at all are the product: the rest of §8.

## 8. Rule catalog (v1)

Columns: category; **On** = enabled by default; **API** = fix changes a
public shape, applied only under `--api-changes` (private/internal always);
**Pri** = priority (warning severity, printed without `--notes`); the F#
twin; the Microsoft rule it yields to. "note" in the fix column means the
rule only reports. Guards follow each table; a rule ships with every guard
listed, and a guard that cannot be proven from the typed tree stands the
rule down rather than being dropped.

Totals: 125 rules — correctness 57, performance 25, idiom 37, cosmetic 6;
119 on by default (off: CR0006, CR0016, CR0089, CR0142, CR0155, and CR0088
as v2; CR0028 decided by measurement; the §8.I rules are on but silent
below their language level).
Priority: CR0034, CR0047, CR0061, CR0062, CR0066, CR0086, CR0107, CR0120,
CR0122, CR0123, CR0125, CR0160, CR0161, CR0162, CR0163, CR0165, CR0169,
CR0171.

**The guard lists below are the summary.** The full conditions come from the
F# implementations, read module by module on 2026-09-19 against this
section: [docs/rule-audit.md](docs/rule-audit.md) records, per CR rule, every
condition, alternative shape and knob the F# code enforces that the list
here leaves out (308 items marked **port**, each with its C# reading), and
the F#-only ones marked *n/a* so nobody re-derives them. A rule is
implemented against the audit entry plus its guard list here, never the
list alone. Where the audit corrected this section (CR0002's minimum arm
count is a C# decision, CR0064's zero guard is for integer divisors only,
CR0111 needs evidence for both separators, CR0068 leaves plain
`Exception` to CA2201, CR0103 must check the target type), this section
has been updated in place.

### 8.A Expressions and control flow

| Code | Cat | On | API | Pri | Fires on | Fix | F# | Yields to |
|---|---|---|---|---|---|---|---|---|
| CR0001 | idiom | v | | | `if (c) return true; return false;` / `if (c) x = true; else x = false;` | `return c;` / `x = c;` | FR0010 | IDE0075 (ternary) |
| CR0002 | idiom | v | | | `if (k == 1) … else if (k == 2) … else …` on one scrutinee | `switch (k) { case 1: … }` or `k switch { 1 => …, _ => … }` | FR0112 | — |
| CR0003 | idiom | v | | | `if (s is Circle) { var c = (Circle)s; … } else if (s is Rect) …` | `switch (s) { case Circle c: … case Rect r: … }` | FR0103 | — |
| CR0004 | idiom | v | | | `x.HasValue ? x.Value + 1 : 0`, `if (x.HasValue) use(x.Value)` | `x is { } v ? v + 1 : 0`, `if (x is { } v) use(v)` | FR0034 | IDE0029/IDE0030 |
| CR0005 | idiom | v | | | `if (a) { if (b) X else E } else E`, `if (a) { if (b) X }` | `if (a && b) X else E` | FR0113 | — |
| CR0006 | idiom | | | | `if (ok) { twenty lines } else { return; }` | `if (!ok) { return; } twenty lines` | FR0114 | — |
| CR0007 | idiom | v | | | `x && true`, `true && x`, `x \|\| false`, `false \|\| x` | `x` | FR0108 | — |
| CR0008 | idiom | v | | | `a \|\| a`, `a && a` | `a` | FR0109 | — |
| CR0009 | idiom | v | | | adjacent `case 1: return "x"; case 2: return "x";` / `1 => f(), 2 => f()` | `case 1: case 2:` / `1 or 2 => f()` | FR0117 | — |
| CR0010 | idiom | v | | | `case var x when x == "A":`, `var x when x == 3 =>` | `case "A":`, `3 =>` | FR0129 | — |
| CR0011 | idiom | v | | | term-rewriting hints: `!(a == b)` → `a != b`, De Morgan, `string.Compare(a, b) == 0` → `string.Equals(a, b)`, `a.CompareTo(b) == 0`, `xs.Select(f).Sum()` → `xs.Sum(f)`, `xs.Where(p).Count() > 0` → `xs.Any(p)`, `xs.Select(x => x)` → `xs`, `!xs.Any(p)` ↔ `xs.All(!p)`… | per hint; extensible per repository | FR0012 | IDE0100, IDE0120, CA1827 per hint |
| CR0012 | correctness | v | | | `case Jordan: // not supported yet` `return null;` | `throw new NotImplementedException();` | FR0100 | — |
| CR0013 | correctness | v | | | `switch` on an enum whose `default:` stands in for 1–2 named members | note; editor: `case D:` arms plus `default: throw new ArgumentOutOfRangeException(nameof(x))` | FR0072 | — |
| CR0014 | correctness | v | | | `switch` statement on an enum with no `default`, every arm exiting, members unhandled | note; editor: `case M: throw new NotImplementedException();` per missing member (≤3) | FR0110 | CS8509 (expressions) |
| CR0015 | idiom | v | | | `for (int i = 0; i < xs.Length; i++) use(xs[i]);` | `foreach (var x in xs) use(x);` | FR0101 | — |
| CR0016 | idiom | | | | `bool done = false; while (!done && …) { … done = true; … }` | note: `break`/`return` at the decision | FR0141 | — |
| CR0017 | correctness | v | | | `for (int i …) actions.Add(() => use(i));` | note: every closure sees the final `i`; copy into a loop-local | — | — |

Guards.

- **CR0001** — `c` is typed `bool` with no user-defined `true`/`false`/`!`
  operator; the statement form requires the `return false;` (or the `else`)
  to be the immediate sibling; the assignment form requires the same target
  on both sides, a local or a field (a property setter may have effects the
  order of evaluation would move).
- **CR0002** — the scrutinee is a local, parameter or `readonly` field read
  (evaluated once after the rewrite, where the chain evaluated it per
  comparison); every comparison is `==` against a constant of `int`/`long`/
  `char`/`string`/`bool`/enum type with built-in equality (a user-defined
  `==` is a call, and a constant pattern would call `Equals` instead); at
  least three comparisons (a C# decision — the F# twin has no minimum; two
  `==` tests read fine as `if`/`else`); branches are single-line `return`/assignment to
  one target for the expression form, arbitrary blocks for the statement
  form; a comparison against a non-constant, a `when`-shaped extra condition
  or a `goto` keeps the chain.
- **CR0003** — two or more `is T` tests on the same plain identifier; every
  cast in a branch targets that branch's own `T`; no compound conditions; the
  branches keep their statements verbatim (the cast line becomes the pattern
  variable, its name kept); a cross-cast or a null check between tests keeps
  the chain. The `is not null` / `is null` arms of C# 9 are accepted as
  patterns of their own.
- **CR0004** — receiver typed `System.Nullable<T>` (a custom `HasValue`
  never matches); the pattern variable name is derived from the receiver and
  checked against every name in scope; withheld where IDE0029/IDE0030 would
  offer `??` instead (payload passed through unchanged).
- **CR0005** — exactly the two semantics-preserving shapes (identical `else`
  blocks, textually and with no comments lost, or no `else` at all); an
  `||`-topped condition gains parentheses; the third shape (inner `if` without
  `else` under an outer `else`) is deliberately absent.
- **CR0006** — `then_at_least` (20) and `else_at_most` (3) lines; the
  negation unwraps an existing `!`; the `else` must exit (`return`/`throw`/
  `continue`/`break`) for the flip to preserve flow. Off by default: happy-path-first
  is a house style too.
- **CR0007/CR0008** — operands typed `bool` with no user-defined `&`/`|`
  operator (the `&&`/`||` on a user type calls `true`/`false` and `&`/`|`);
  CR0007 leaves `x && false` and `true || x` (the value is constant but `x`'s
  evaluation goes); CR0008 requires textually identical operands containing
  no invocation, `await`, `out`/`ref` argument, increment or assignment —
  `TryConnect() || TryConnect()` is the retry idiom.
- **CR0009** — contiguous arms only (order is semantics); no `when`; no
  pattern variables (`or` patterns cannot bind; `case int n:` stays);
  bodies textually identical; neither `default` nor the discard arm `_` joins
  a run (`"A" or _` reads as a mistake); the merged form keeps the first
  arm's position.
- **CR0010** — the body never mentions the binder; the compared value is a
  constant the pattern language can spell (literal, `const`, enum member);
  `==` is built-in for the scrutinee's type.
- **CR0011** — the engine is FR0012's: `lhs ===> rhs` with single-letter
  metavariables over Roslyn syntax; a right side that drops or duplicates a
  metavariable fires only on pure atoms; a hint that changes operator
  precedence brackets only where needed; each built-in hint names the
  Microsoft rule it yields to, and a hint is silent inside expression trees
  (`Expression<Func<…>>` arguments, typed) where the shape is what a LINQ
  provider translates. Custom hints come from the file named by
  `csharp_refactor.hints`, one per line.
- **CR0012** — the comment sits inside the arm, between label and value;
  `null`/`default`/`false`/`0`/`-1`/`string.Empty`/empty collection literal
  stand-ins only under such a comment; only where sibling arms compute.
- **CR0013** — typed enum, not `[Flags]`, at most two members unnamed. C#
  enums are open (any underlying value converts), so expanding `default`
  into named cases changes what an unnamed value does; hence note-only in
  the sweep, and the editor's expansion keeps a `default:` that throws.
- **CR0014** — switch statements only (the compiler warns on expressions);
  every existing arm ends in `return`/`throw`/`continue`/`break`-to-exit so
  that a missing member silently falls out; typed enum, not `[Flags]`; at
  most three missing.
- **CR0015** — `i` starts at 0, steps by one, bounded by `xs.Length`/`xs.Count`
  of one array/`IList<T>` local, parameter or `readonly` field; every use of
  `i` is `xs[i]` (a use as a value wants `Select((x, i) => …)` and stays the
  author's call); no `xs[i] = …`; `xs` not assigned, resized or cleared in the
  body; a `break`/`continue` stays as it is; the element is `var` only where
  `foreach`'s own binding (the public `GetEnumerator()` pattern first, then
  `IEnumerable<T>`) yields the indexer's type, else that type is spelled
  (`MatchCollection` enumerates `object`), and the speculative check
  re-binds the loop. Measured: `foreach` over an
  array or `List<T>` compiles to the same loop.
- **CR0016** — off by default; the note counts the statements that still
  run after the flag is raised and says nothing when there are none. C# has
  `break`, so unlike FR0141 the remedy is the keyword, not recursion.
- **CR0017** — a lambda or local function that mentions the `for` variable
  and ESCAPES the iteration: stored (`Add`, assignment to a field or outer
  local), returned, passed to a deferred LINQ operator (`Where`, `Select`,
  …, materialised outside the loop), passed to `Task.Run`/`ThreadPool`/an
  event. An immediately invoked delegate, `List<T>.ForEach`, `Array.ForEach`,
  `Parallel.*` and a LINQ chain materialised in the same statement complete
  inside the iteration and stay quiet. `foreach` variables are per-iteration
  since C# 5 and never fire. CR0160 (§8.J) fixes the shape with the loop-local
  copy and wins where both report (`Rules.overlapWinners`).

### 8.B Collections and LINQ

| Code | Cat | On | API | Pri | Fires on | Fix | F# | Yields to |
|---|---|---|---|---|---|---|---|---|
| CR0020 | performance | v | | | `xs.ToList().Where(p)`, `xs.ToArray().Select(f).ToList()`, `foreach (var x in xs.ToList())` | `xs.Where(p).ToList()`, `xs.Select(f).ToList()`, `foreach (var x in xs)` | FR0004 | — |
| CR0021 | idiom | v | | | `var total = 0; foreach (var x in xs) total += x;` (also `Math.Max` / `>` running max, min, count) | `var total = xs.Sum();` / `.Max()` / `.Min()` / `.Count(p)` | FR0050, FR0041 | — |
| CR0022 | idiom | | | | `bool found = false; foreach (var x in xs) if (p(x)) found = true;` | `bool found = xs.Any(x => p(x));` (`All` for the `true`-initialised dual) | FR0107 | — |
| CR0023 | performance | v | | | `xs.Where(x => allowed.Contains(x))` where `allowed` is a startup-built `static readonly` array/list | `allowed` becomes `FrozenSet<T>` (.NET 8+) / `HashSet<T>`, or a private companion set when other uses pin the type; else note | FR0035 | — |
| CR0024 | performance | v | | | `foreach (var x in xs) acc.Add(x);` | `acc.AddRange(xs);` | FR0030 | — |
| CR0025 | performance | v | | | `arr = arr.Append(x).ToArray()`, `arr = arr.Concat(new[]{x}).ToArray()`, `Array.Resize(ref arr, arr.Length + 1)`, `imm = imm.Add(x)` on `ImmutableArray<T>`, `s += piece` on a string, all in a loop | note: `List<T>`/`ImmutableArray.CreateBuilder`/`StringBuilder`/`string.Join` | FR0051, FR0104 | — |
| CR0026 | performance | v | | | `xs.ElementAt(i)`, `xs.Count()`, `xs.Last()` on a non-collection `IEnumerable<T>` inside a loop | note: materialise once | FR0102 | CA1826/CA1829 (collections) |
| CR0027 | correctness | v | | | `xs.Select(x => Log(x));` as a statement; `xs.Where(p);` discarded | note: lazy, runs nothing; `foreach` | FR0076, FR0017 | — |
| CR0028 | idiom | * | | | `var r = new List<T>(); foreach (var x in xs) if (p(x)) r.Add(f(x));` then only read | `var r = xs.Where(p).Select(f).ToList();` | FR0156 | — |
| CR0029 | idiom | v | | | `xs.Select(x => x.A).Select(a => a.B)`, `xs.Select(x => x)` | `xs.Select(x => x.A.B)`, `xs` | FR0137 | — |
| CR0030 | correctness | v | | | an `IEnumerable<T>` parameter enumerated twice on one path (`if (xs.Any()) foreach (var x in xs)`) | note: a query or generator runs twice | — | CA1851 |
| CR0031 | performance | v | | | `new Random().Next(…)` per call or in a loop | `Random.Shared.Next(…)` (.NET 6+) | — | — |
| CR0032 | performance | v | | | `foreach (var k in d.Keys) use(k, d[k])` | `foreach (var (k, v) in d) use(k, v)` | — | — |
| CR0033 | performance | v | | | `sb.Append(a + b + c)` | `sb.Append(a).Append(b).Append(c)` | — | — |
| CR0034 | correctness | v | | v | `foreach (var c in customers) foreach (var o in db.Orders.Where(o => o.CustomerId == c.Id))` | note: N+1 | FR0028 | — |
| CR0035 | performance | v | | | `IEnumerable<T> Walk(Node n) { … foreach (var c in Walk(child)) yield return c; }` | note: O(depth) per element; explicit stack | FR0058 | — |

`*` CR0028's default was decided by PerfClaims at M2 (benchmarks/PerfClaims/RESULTS.md):
parity on arrays and lazy sequences, 3.8× slower on `List<T>`, so it ships
default-off. The same run put CR0022 off by default (`Any(pred)` is 4.5–6.5×
slower than the flag loop when nothing matches), gated CR0021's `Sum`/`Count`
to array and `List<T>` sources, CR0024 to collection sources, and dropped
`Select` from CR0020's movable stages.

Guards.

- **CR0020** — typed `Enumerable` calls over a source that is not an
  `IQueryable` (a query's copy runs the query; the loop would run over an
  open reader, a moved stage would be translated by the provider); the moved
  operation must shrink or consume (`Where`, `First`, `Any`, `Count`, `Take`,
  `foreach`), each pair measured; the lambda must not mutate the source or
  name it (the snapshot idiom: `foreach (var x in list.ToList()) list.Remove(x)`
  keeps its copy — a body that calls `Add`/`Remove`/`Clear`/an indexer set on
  the source, or passes it to a method, stands the rule down); a `ToList()`
  whose result is enumerated more than once stays.
- **CR0021** — the accumulator is a local initialised to the identity and
  assigned only by the one `+=`/`Math.Max`/`>`-compare in the loop body and
  read only after; the source is an array, `List<T>` or `IEnumerable<T>` local.
  Overflow: `Enumerable.Sum` is checked where `+=` wraps, so `int`/`long`
  sums rewrite only when the compilation's `CheckOverflow` option is on or
  the loop sits in a `checked` block; `float`/`double`/`decimal` always
  (`decimal` throws either way). `Max`/`Min` on floating types never (the
  `>` loop and `Enumerable.Max` disagree on NaN). Measured: .NET 8+
  vectorises `Sum`/`Min`/`Max` on `int[]`/`long[]`/`List<int>`, so this is
  a win there and parity elsewhere. A general combine stays a loop:
  `Aggregate` is not clearer than `foreach`.
- **CR0022** — the loop body is the one `if` with no `else`, optionally
  after pure single-line `var` bindings that fold into the lambda; the
  predicate is `isPureExpression`/`callsOnlyCore` (short-circuiting must not
  skip an effect); the flag is not read inside the loop and not reassigned
  after; measured: parity on arrays; on `List<T>` `Enumerable.Any(pred)`
  boxes the struct enumerator — if PerfClaims shows that allocation the
  `List<T>` shape goes behind a knob (`csharp_refactor.CR0022.lists`).
- **CR0023** — the collection is a `static readonly` field or a local
  initialised once from a literal, never reassigned or mutated (typed:
  no `Add`/`Remove`/indexer set anywhere in the compilation); the probe is
  `Contains` inside a loop or a lambda handed to a collection operator; the
  element type has value equality; when every use is a `Contains` the
  declaration becomes `FrozenSet<T>` (capability-gated) or `HashSet<T>`,
  otherwise a companion set is declared beside it; measured with the build
  cost charged; probing the loop variable itself never fires.
- **CR0024** — `Add` resolves to `List<T>.Add` (typed), the body is that one
  statement of the loop variable, the source is not the list itself.
- **CR0025** — note only; the string form requires the typed `string` `+=`
  and any loop; `ImmutableArray<T>` typed; a single-element `Concat`/`Append`
  outside a loop is not reported.
- **CR0026** — receiver typed as an `IEnumerable<T>` that is NOT
  `IList<T>`/`ICollection<T>`/`IReadOnlyCollection<T>` (those get CA1826/
  CA1829); the loop is not the one that produced the enumerable; `Count()`
  in a `for` condition counts.
- **CR0027** — expression statement whose value is a typed `IEnumerable<T>`
  from a deferred `Enumerable` operator, or a discarded call result of one;
  `_ = xs.Select(…)` is a decision and stays quiet; the note names the
  effect the lambda contains.
- **CR0028** — every use of the list between declaration and loop is none,
  inside the loops is a statement-position `Add` (never under `try`, a
  lambda or a `Count` cap), after the loops is a read (`foreach`, LINQ,
  `Count`, indexer read, passing to a parameter typed `IEnumerable<T>`/
  `IReadOnlyList<T>`/`List<T>`); the loop bodies are `if`/`foreach` nests
  over pure conditions and projections; `await`, `yield`, a second
  collection filled, or a mutation after the loop stands it down; the
  `Select` lambda must not close over a `ref`/`Span` (cannot be captured).
- **CR0029** — both calls resolve to `Enumerable.Select` (an `IQueryable`
  chain is a query the provider translates), and the chain is not inside an
  expression tree (there too the lambdas are the provider's as written);
  both lambdas are expression lambdas; substituting the first body for the second's parameter
  duplicates nothing (the parameter is used once, or the first body is a
  pure atom) and captures nothing (names checked); `Select(x => x)` is
  removed only where the result type is unchanged (typed). Both forms are
  already lazy and per-element, so this is an idiom rule; measured parity
  (the runtime already composes consecutive `Select`s).
- **CR0030** — the parameter's declared type is `IEnumerable<T>` (not a
  collection interface); two enumerations on one control-flow path
  (`foreach`, a materialising or consuming LINQ call, `GetEnumerator`);
  an argument that is a literal array or a collection at every call site
  in the compilation does not rescue the method — the note is about the
  signature's promise.
- **CR0031** — parameterless `new Random()` (a seed is a decision) whose
  instance is used only for calls in the same expression or scope and never
  stored; `Random.Shared` resolvable; measured (allocation, and `Random`'s
  seeding cost).
- **CR0032** — `d` typed `Dictionary<K,V>`/`IDictionary<K,V>`, the body
  reads `d[k]` and never writes `d`; the deconstruction form needs C# 7.
- **CR0033** — `Append` on `System.Text.StringBuilder` with a `+` chain of
  typed strings (a `+` on other types converts through `ToString()` inside
  `Append` anyway); `$""` arguments are left alone (.NET 6+ handles them
  without an intermediate); measured.
- **CR0034** — typed `IQueryable<T>` enumerated in a loop nested in another;
  an outer loop over a chunked source (`Chunk`, a `Skip/Take` page) is a
  batch and stays quiet.
- **CR0035** — an iterator method that yields from a `foreach` over its own
  recursive call; a tail `return Walk(child)` (no `yield`) is a redirect
  and does not count; note only.

### 8.C Async and concurrency

| Code | Cat | On | API | Pri | Fires on | Fix | F# | Yields to |
|---|---|---|---|---|---|---|---|---|
| CR0040 | correctness | v | | | inside an `async` method: `t.Result`, `t.Wait()`, `t.GetAwaiter().GetResult()`, `Task.WaitAll(a, b)`, `a.Result` inside `t.ContinueWith(a => …)` | `await t`, `await Task.WhenAll(a, b)`, the continuation as an `await` bind; outside `async`: boundary note, sync-twin swap editor-only or `sync_swap` | FR0049 | CA1849 (`Thread.Sleep`) |
| CR0041 | correctness | v | v | | a sync method draining a task at its boundary (`return Foo().Result;`) whose every caller sits in an `async` context | method becomes `async Task<T>` (`Async` suffix unless configured off), drains become `await`, every caller awaits | FR0049 (taskify) | — |
| CR0042 | correctness | v | | | inside an `async` method: `reader.ReadLine()`, `stream.Read(…)`, `File.ReadAllText(…)` where an `…Async` twin exists | `await reader.ReadLineAsync()` | FR0119 | CA1849 |
| CR0043 | correctness | v | | | `async void M()` that is not an event handler | `async Task M()`; call sites in `async` contexts gain `await` | — | VSTHRD100 |
| CR0044 | correctness | v | | | a `Task`-returning call as an expression statement in a NON-async method, `Task.Run(…);` discarded | note: nobody observes a failure; `_ = …` is a decision | FR0149, FR0017 | CS4014 (async methods) |
| CR0045 | performance | v | | | `[Fact] public void T() { var r = Load().Result; … }`, `Assert.Throws<E>(() => t.Wait())`, `Task.WaitAll(a, b)` in a test | `public async Task T() { var r = await Load(); … }`, `await Assert.ThrowsAsync<E>(() => t)`, `await Task.WhenAll(a, b)` | FR0142 | — |
| CR0046 | performance | v | | | `async Task<T> M(x) { return await Inner(x); }` with nothing else in the body | `Task<T> M(x) { return Inner(x); }` | — | — |
| CR0047 | correctness | v | | v | `lock (this)`, `lock ("cache")`, `lock (typeof(T))`, `lock (x.GetType())` | note; editor: a `private readonly object _gate = new();` (or `System.Threading.Lock` on .NET 9+) beside the locked value | FR0046 | CA2002 |
| CR0048 | correctness | v | | | `Monitor.Enter(x); try { … } finally { Monitor.Exit(x); }` | `lock (x) { … }` | FR0123 | — |
| CR0049 | correctness | v | | | `if (!cd.TryGetValue(k, out var v)) { v = Compute(); cd[k] = v; }` on a `ConcurrentDictionary` | the miss arm becomes `v = cd.GetOrAdd(k, _ => Compute());` | FR0154 | — |
| CR0050 | correctness | v | | | `cd.GetOrAdd(k, factory)` whose value type is `Task`/`ValueTask`/`Lazy` | note: a faulted value stays cached | FR0152 | — |
| CR0051 | correctness | v | | | `Task<T> M() { using var x = …; return DoAsync(x); }` | `async Task<T> M() { using var x = …; return await DoAsync(x); }` | FR0150 | — |
| CR0052 | correctness | v | | | `AppDomain.CurrentDomain.ProcessExit += (s, e) => this.Flush();` (a `this`-capturing handler on a process-wide or static publisher, never removed) | note: the object lives as long as the publisher | FR0027 | — |
| CR0053 | correctness | v | | | `list.ForEach(async x => …)`, `Parallel.ForEach(xs, async x => …)`: an `async` lambda converted to a `void`-returning delegate | note: `async void` in disguise | — | VSTHRD101 |
| CR0054 | performance | v | | | `Task.WhenAll(new[] { t })`, `Task.WaitAll(t)` | note: the direct form changes the result type | FR0079 | CA1842/CA1843 |
| CR0055 | correctness | v | | | `Foo(x, CancellationToken.None)` / `Foo(x, default)` while a token parameter is in scope | `Foo(x, ct)` | FR0118 | CA2016 |

Guards.

- **CR0040** — receivers typed `Task`/`Task<T>`/`ValueTask`/`ValueTask<T>`;
  the bind fix needs a direct statement of the `async` body (`var x = t.Result;`,
  `t.Wait();`, `Task.WaitAll(…)` in statement position), never inside a
  lambda, a `lock` (await is illegal there), a `catch`/`finally`, or a body
  choreographed around a thread (`Thread`, a signal, `Interlocked`);
  `.Result` wraps in `AggregateException` where `await` unwraps, so a
  `catch (AggregateException)` around the site stands the fix down and the
  note says why; quiet when the task is known complete (`IsCompleted`
  probe, `Task.FromResult`, `CompletedTask`, after its own `Wait(timeout)`);
  the spine of a non-async `Main` and of a `.csx` top level is the console's
  blocking point and gets no boundary note; the sync-twin swap
  (`X.FooAsync().GetAwaiter().GetResult()` → `X.Foo()` when the typed tree
  proves the sibling) is editor-only or `sync_swap`, never auto-applied: the
  tool does not walk code backward from async. `GetAwaiter().GetResult()` is
  never emitted by any rule.
- **CR0041** — `private` methods always, `internal` under the scope gate,
  public only with `--api-changes` and every referencing project in the
  run being C#/VB; every caller must be a bindable statement in an `async`
  body (or a test that CR0045 converts in the same pass); one unconvertible
  caller vetoes everything; the speculative check re-binds every touched
  file; the suffix is added when the name lacks it and no clash results
  (`csharp_refactor.CR0041.async_suffix = false` keeps the name); `out`/`ref`
  parameters, iterators, `unsafe` and overrides veto (an override's shape is
  the base's).
- **CR0042** — the twin is proven from the typed tree: same name plus
  `Async`, same parameter prefix, `T` → `Task<T>`/`ValueTask<T>` return, a
  trailing optional `CancellationToken` tolerated (CA2016 hands it the token
  on the next pass); statement position or `var x = …` only; not in a
  `lock`/`catch`/`finally`/lambda/thread-choreographed body; never a LINQ
  twin over `IQueryable<T>` (EF's async query extensions need an async
  query provider; an in-memory source throws).
- **CR0043** — not `(object, EventArgs)`-shaped, not subscribed to any event
  in the compilation, not an override or interface implementation, no
  `[…Handler]`-style attribute; the fix changes the signature and adds
  `await` at call sites in `async` contexts; a call site in a sync context
  would become a silent fire-and-forget, so it vetoes the fix and the rule
  notes instead.
- **CR0044** — typed `Task`-returning expression statement; a
  `.ContinueWith(…, TaskContinuationOptions.OnlyOnFaulted)`, a stored task,
  or an explicit discard is handled; the note distinguishes .NET Core's
  silent unobserved exception from the F# side's process kill.
- **CR0045** — test attributes of xUnit, NUnit, MSTest, TUnit; `public void`
  → `public async Task`; spine-level blocking sites only; the framework's
  async assert is spelled per framework (`Assert.ThrowsAsync` for xUnit and
  NUnit, `Assert.ThrowsExceptionAsync`/`ThrowsAsync` for MSTest by version);
  a test whose last expression is a value stays; a body choreographed
  around a thread stays.
- **CR0046** — the body is exactly `return await e;` (or `=> await e`); `e`
  is an invocation of a method the typed tree marks `async` (an async callee
  never throws synchronously, so the only observable difference — a sync
  throw where the caller got a faulted task — cannot occur) with pure-atom
  arguments; no `ConfigureAwait`; no `using`, `try`, `lock`, `foreach` or
  `await using` in the method; return types match exactly. Measured: one
  state machine and its allocation fewer.
- **CR0047** — typed: the operand is `this`, a `string`, a `Type`, or a
  boxed value type; the editor's lock object lands beside the field the body
  guards or before the enclosing member; `System.Threading.Lock` is offered
  where it resolves and the file is C# 13; the initialiser is `new()` from
  C# 9 and `new object()` below.
  (The gate's language version came out of the property suite: an offer
  spelled C# 13 into a C# 10 file.)
- **CR0048** — single-argument `Enter` (the `ref bool taken` overload
  carries protocol), identical operand text in `Enter` and `Exit`, the
  `try`/`finally` immediately follows; a bare `Enter` with no `try` is the
  leak note.
- **CR0049** — typed `ConcurrentDictionary`; the miss arm computes and
  stores through the indexer, `TryAdd(k, v)` discarded, or `AddOrUpdate`
  returning the same value; the hit path keeps `TryGetValue` (measured:
  2.1 ns and no delegate on the hit against 7.3 ns and 64 B for `GetOrAdd`
  with a lambda); a key that is not a pure atom, a factory reading a `ref`/
  `Span`/`out` local (cannot be captured), or a `Lazy`/delegate value type
  stands it down; the message carries the `Lazy<T>` hint when the factory
  calls something.
- **CR0050** — gated on the value type argument; a factory that merely
  throws caches nothing and is not reported.
- **CR0051** — the shape only exists in non-`async` methods (an `async`
  method cannot `return` a task); the disposable is read by the returned
  task's arguments or captured by its lambda; the method becomes `async` and
  the message states the timing change (argument evaluation exceptions now
  fault the task). This is a correctness fix; the F# editor-only stance
  was for a different remedy (moving the binding).
- **CR0052** — the publisher is `AppDomain.CurrentDomain.*`,
  `Console.CancelKeyPress`, `SystemEvents.*`, a `static event`, or an
  `IObservable` from a static/singleton member; no matching `-=`/`Dispose`
  of the subscription in the type; a handler on an event of an object the
  same scope created stays quiet.
- **CR0053** — typed: an `async` lambda whose target delegate type returns
  `void` (`Action<T>`, an event handler type); `Task.Run(async () => …)` and
  `Func<Task>` targets are fine.
- **CR0055** — exactly one token parameter on the enclosing member; the
  call resolves to a method whose parameter at that position is
  `CancellationToken`; never inside a `catch`/`finally` (a cancelled operation
  must still roll back), and never where the `None` is bound to a name.

### 8.D Resources and exceptions

| Code | Cat | On | API | Pri | Fires on | Fix | F# | Yields to |
|---|---|---|---|---|---|---|---|---|
| CR0060 | correctness | v | | | `var s = new FileStream(…);` never disposed, every mention in scope | `using var s = new FileStream(…);` (C# 8+); advice naming the destination when handed off | FR0075 | CA2000 |
| CR0061 | correctness | v | | v | a type that constructs a disposable field and does not implement `IDisposable` | note | FR0032 | CA1001 |
| CR0062 | correctness | v | | v | a `Dispose` that never releases one of the type's own `new`-constructed disposable fields | note (`Cancel` without `Dispose` named separately) | FR0047 | CA2213 |
| CR0063 | correctness | v | | | `public void Dispose()` on a type not implementing `IDisposable` | note: nothing can `using` it | FR0148 | — |
| CR0064 | correctness | v | | | `catch { }`, `catch (Exception) { return null; }`, `catch (Exception e) when (false)`… | note; editor: `TryParse` for a one-call `Parse` body, a zero guard for a pure division, an IO-only catch for file IO, a log line in the file's logging idiom | FR0055 | CA1031 |
| CR0065 | idiom | v | | | `catch (E ex) { if (!Cond(ex)) throw; … }` | `catch (E ex) when (Cond(ex)) { … }` | — | — |
| CR0066 | correctness | v | | v | `throw` inside `finally` | note | FR0063 | CA2219 |
| CR0067 | correctness | v | | | `throw` inside `Equals`/`GetHashCode`/`ToString`/`Dispose`/a static constructor | note | FR0054 | CA1065 |
| CR0068 | correctness | v | | | `new NullReferenceException()`, `new IndexOutOfRangeException()`, `new OutOfMemoryException()`… | note | FR0064 | CA2201 |
| CR0069 | idiom | v | v | | `throw new InvalidOperationException("Error");` with a constant message | `throw new InvalidOperationException($"Error, calling {nameof(M)} with x: {x}");` | FR0092 | — |
| CR0070 | correctness | v | | | `catch (ReflectionTypeLoadException e) { Log(e.Message); }`, `catch (AggregateException e) { Log(e.Message); }`, `WebException`, `SqlException` | note; editor for `ReflectionTypeLoadException`: use `e.Types` filtered of nulls, or join `LoaderExceptions` | FR0151 | — |

Guards.

- **CR0060** — typed `IDisposable` construction (`new`, and the
  `Create()` factories of `MD5`/`SHA*`/`Aes`/`RandomNumberGenerator`);
  `using var` needs `LanguageVersion >= 8`, otherwise a `using (…) { }` block
  is offered only when the binding heads its block; every mention stays in
  scope; ownership transfers are not leaks: returned (also inside a tuple,
  record, `Some`-like wrapper), stored in a field/property/collection/static,
  passed to another disposable's constructor (`new StreamReader(stream)`),
  passed to a same-file method that disposes its parameter (read one hop);
  handed to a method that keeps it names the leak; a `Close()`/`Dispose()`
  call anywhere in scope exempts; `HttpClient` is never a `using` candidate
  (a shared instance or `IHttpClientFactory` is the right lifetime — CR0110's
  note); `StringReader`/`StringWriter`/`MemoryStream` over a caller's buffer
  own nothing; a task the disposable produced and handed to any call is in
  flight; a `CancellationTokenSource` whose token reaches anything but an
  awaited BCL call is in flight; in an ASP.NET controller/hub member the
  leak is per request and carries warning weight.
- **CR0061** — injected constructor parameters do not count (the injector
  owns them); an interface inheriting `IDisposable` counts; the
  no-ownership types above are not noted.
- **CR0062** — the interface `Dispose` is followed one hop into
  `Dispose(bool)`; `base.Dispose()` hand-off and a `System.Reactive` file
  (unsubscribe-not-release) stay quiet; `Cancel`/`Close` without `Dispose`
  gets its own wording.
- **CR0064** — catch-all means `catch`, `catch (Exception)`, or a filter
  that never reads the exception; a specific type ignored is a decision;
  the `bool` probe idiom (`try { …; return true; } catch { return false; }`)
  is quiet; a catch-all after a cancellation rethrow arm, or followed by an
  unconditional failure, is a decision; the one-call teardown idiom
  (`try { x.Dispose(); } catch { }`) and a try around a non-throwing probe
  (`File.Exists`) are named for what they are; test files (a test framework
  `using` or attribute — the one predicate CR0064, CR0069 and CR0146 share)
  are quiet. The editor's IO-only narrowing requires the typed tree to agree
  every call in the body is `System.IO`'s. The zero-guard offer is for an
  INTEGER divisor only: `decimal` `+`/`*`/`/` throw `OverflowException`, so
  the catch guarded more than the division, and float division never throws.
- **CR0065** — the condition reads only the exception's members, literals
  and pure atoms (a filter runs BEFORE inner `finally` blocks, so an effectful
  condition would reorder effects); the rethrow is `throw;` (not `throw ex;`
  — CA2200's business); the guard is the first statement of the handler.
- **CR0066/CR0067/CR0068** — throws the block itself catches stay quiet; a
  switch whose arms throw three or more distinct types is a fault table and
  stays quiet (CR0068).
- **CR0069** — constant message only; quotes only arguments whose type
  prints (primitives, strings, enums — not a record, which prints every
  member it carries); quiet on invariant messages
  (unreachable, not possible, NYI, internal error), in security-named scopes,
  in test files, where the same literal appears elsewhere in the compilation
  (a test asserting on it), and where the message is the member's own name.
  Exception text is observable behaviour: API-gated.
- **CR0070** — a table by exception type: `ReflectionTypeLoadException` →
  `LoaderExceptions`/`Types`, `AggregateException` → `InnerExceptions`/
  `Flatten()`, `WebException` → `Response`, `SqlException` → `Errors`/
  `Number`; reading the informative member anywhere in the handler or its
  filter counts as informed; the RTLE editor fix keeps the rethrow for the
  nothing-loaded case and appends a `// TODO: log e.LoaderExceptions`.

### 8.E Types and immutability

| Code | Cat | On | API | Pri | Fires on | Fix | F# | Yields to |
|---|---|---|---|---|---|---|---|---|
| CR0080 | idiom | v | v | | a `class` whose every instance member is a get-only/`init` property or a constructor assigning them | `record` (positional form as an editor alternative) | — | — |
| CR0081 | performance | v | v | | a `record` (or CR0080 class) of at most four small unmanaged fields | `readonly record struct` | FR0070 | — |
| CR0082 | performance | v | v | | `Tuple<int, string>` in a private/internal signature or field, `Tuple.Create(a, b)` | `(int, string)`, `(a, b)` | FR0093 | — |
| CR0083 | idiom | v | v | | `{ get; set; }` assigned only in constructors and object initialisers of the type across the compilation | `{ get; init; }` (C# 9+, `IsExternalInit` resolvable) | — | — |
| CR0084 | correctness | v | | | `public static int Counter;` assigned from two or more sites or from itself | note; editor: `private`/`readonly` | FR0062 | CA2211 |
| CR0085 | correctness | v | | | `x.GetType().Name == "Customer"`, `x.GetType() == typeof(T)` | note | FR0036 | — |
| CR0086 | correctness | v | | v | a constructor calling a `virtual`/`abstract` member of its own type | note | FR0020 | CA2214 |
| CR0087 | performance | v | | | `status.ToString() == "Active"` on an enum | `status == Status.Active` | — | — |
| CR0088 | idiom | v2 | v | | a closed set of string literals matched by name across parameters, fields and call sites | an `enum` plus a `ToText()` extension returning the original literal, every slot retyped | FR0157 | — |
| CR0089 | idiom | | v | | a private type's `DateTime` field written from `DateTime.Now`/`UtcNow` and read through parity members | `DateTimeOffset` in one edit set | FR0134 | — |
| CR0090 | correctness | v | | | `new Guid()`, `default(Guid)` spelled as a constructor call | `Guid.Empty`; editor: `Guid.NewGuid()` | FR0136 | — |

Guards.

- **CR0080** — no mutable instance state (every field `readonly`, every
  property get-only or `init`, every property an auto-property — a getter with a
  body is behaviour, a lazy initialiser or a computed value, not data held;
  no method assigns `this` state); no base class
  other than `object` or a record; no `Equals`/`GetHashCode`/`==` override;
  reference identity is not relied on anywhere in the compilation (`==`/`!=`
  on two instances, `ReferenceEquals`, `lock` on an instance, use as a
  dictionary key or set element with the default comparer — a record changes
  equality, which is the point and the risk); the serializer heuristic; no
  `[StructLayout]`/`unsafe`; scope-gated because equality is API. The fix
  changes `class` to `record` and nothing else; the positional rewrite is an
  editor alternative.
- **CR0081** — fields all small unmanaged structs or enums, total ≤ 32
  bytes; no inheritance either way; no interface beyond the compiler's
  `IEquatable<T>`; not used as `T?` anywhere (that spelling would change from
  a nullable reference to `Nullable<T>`), not compared with `null`, not boxed
  to `object`/an interface, not `lock`ed; `readonly record struct` needs C# 10;
  measured on the allocation axis.
- **CR0082** — `Tuple<…>` typed; private/internal members and fields (a
  public signature is API); `Item1..ItemN` names are shared by `ValueTuple`
  and stay; null comparisons of the tuple veto; use through `ITuple` or
  reflection vetoes; an argument use is simple only where the call binds and
  the parameter it lands in (of the callee's definition, not its
  instantiation) is a source declaration of the same tuple type in the
  compilation, retyped together — a library's `Tuple<…>`, a type parameter,
  `object` or an unbound callee vetoes; the serializer heuristic (a `ValueTuple`'s elements are
  fields, which System.Text.Json ignores by default — a shape change);
  the speculative check re-binds every touched file; measured on allocation.
- **CR0083** — every assignment to the property in the compilation is in an
  object initialiser, a `with` expression, or a constructor of the declaring
  type; no `PropertyInfo.SetValue`/`nameof(P)` reflection shape; `init`
  needs `IsExternalInit` (resolvable, or the project polyfills it); public
  setters are API (an external caller assigning after construction breaks);
  Entity Framework entities (`DbSet<T>` heuristic) stand down.
- **CR0084** — churn gate: two or more assignment sites or a self-update;
  a set-once seam stays quiet; `private`/`internal` types stay quiet. The
  editor offers `private` always and `readonly` only where every write is
  a constructor's (a method's write is what the note is about, and
  `readonly` would not compile there).
- **CR0086** — the callee is `virtual`/`abstract` on the constructed type or
  a base, not `sealed`-overridden in this type; a `sealed` class stays quiet
  (nothing can override).
- **CR0087** — typed enum, literal equal to a member name (case-sensitive),
  not `[Flags]`, `ToString()` parameterless; measured (an enum `ToString()`
  allocates and consults a cache).
- **CR0088** — v2. The flow proof and adapters of FR0157 port; the C#
  landing shape is an `enum` (an enum cannot override `ToString`, so the
  original text lives in a generated `ToText()` extension and every print
  site calls it), which is a larger edit than the F# union; editor-only
  until the sweep proves it.
- **CR0089** — off by default (a serialization-shape change); the strict
  envelope of FR0134.
- **CR0090** — typed `System.Guid`; `Guid(bytes)` and friends are deliberate.

### 8.F Strings, culture, time

| Code | Cat | On | API | Pri | Fires on | Fix | F# | Yields to |
|---|---|---|---|---|---|---|---|---|
| CR0100 | idiom | v | | | `"Hello " + name + "!"` | `$"Hello {name}!"` | FR0031 | — |
| CR0101 | idiom | v | | | `string.Format("{0} of {1:N2}", a, b)` | `$"{a} of {b:N2}"` | FR0042 | — |
| CR0102 | performance | v | | | `$"{x.ToString()} items"`, `string.Join(", ", xs.Select(x => x.ToString()))` | `$"{x} items"`, `string.Join(", ", xs)` | FR0021 | — |
| CR0103 | cosmetic | v | | | `$"no holes"` | `"no holes"` | FR0086 | — |
| CR0104 | idiom | v | | | `x == null \|\| x == ""`, `x is null \|\| x.Length == 0`, `x == null \|\| x.Trim() == ""` | `string.IsNullOrEmpty(x)`, `string.IsNullOrWhiteSpace(x)` | FR0138 | — |
| CR0105 | correctness | v | | | `double.Parse(s)`, `DateTime.Parse(s)` without a provider | editor: `CultureInfo.InvariantCulture` (primary) / `CurrentCulture`; CLI under `invariant` | FR0067 | CA1305 |
| CR0106 | correctness | v | | | `DateTime.UtcNow.Date`, `DateTime.Today` (notes); `DateTime.Now` | `DateTime.UtcNow` under `utc_now`; editor always | FR0121 | — |
| CR0107 | correctness | v | | v | `new Regex("(unclosed")`, `Regex.IsMatch(s, "[")` | note: guaranteed `ArgumentException` | FR0122 | — |
| CR0108 | performance | v | | | `Regex.IsMatch(s, "^abc")`, `Regex.Replace(s, "abcd", "x")` with a plain literal | `s.StartsWith("abc", StringComparison.Ordinal)`, `s.Replace("abcd", "x")` | FR0015 | — |
| CR0109 | performance | v | | | `new Regex("lit")` or `Regex.Match(s, "lit")` inside a loop or a per-call method | `[GeneratedRegex("lit")] private static partial Regex LitRegex();` (.NET 7+, the type made `partial`) or a `static readonly Regex` field below that | FR0015, FR0037 | SYSLIB1045 |
| CR0110 | performance | v | | | `new HttpClient()` per call or in a loop; `MD5.Create()`/`SHA256.Create()` in a loop | note | FR0037 | CA1869/CA1870 (their types) |
| CR0111 | idiom | v | | | `dir + "\\" + file`, `dir + "/" + file` with path evidence | note: `Path.Combine`/`Path.Join` | FR0081 | — |
| CR0112 | correctness | v | | | bidi controls, Unicode tag block, zero-width spaces, mid-file BOM in source | `\u200B` escape inside a regular string; note elsewhere | FR0125 | — |
| CR0113 | correctness | v | | | `balance + 2_000_000_000`, `seconds * 1_000_000` on `int` | note; editor: widen to `long`, or `checked(…)` | FR0105 | — |
| CR0114 | correctness | v | | | duplicate placeholder names in a log template; Serilog templates with the shapes CA2017/CA2254 do not cover | note | FR0124 | CA2017, CA2254 |
| CR0115 | correctness | v | | | `catch (Exception ex) { _log.LogError("sync failed {Id}", id); }` | `_log.LogError(ex, "sync failed {Id}", id);` | FR0120 | — |

Guards.

- **CR0100** — every operand a literal or a typed `string` expression, the
  `+` resolving to string concatenation (a user-defined `+` never rewrites);
  literals containing `{`/`}` stand down; with `DefaultInterpolatedStringHandler`
  resolvable any hole type is fine; without it (netstandard2.0, net4x) an
  interpolation with a non-string hole lowers to `string.Format`, so only
  all-string chains rewrite there; as an argument, the call must bind to
  the same member once the string is interpolated (an interpolated string
  converts to `FormattableString`, `IFormattable` and handler types too —
  EF Core's `ExecuteSqlCommand(FormattableString)` would take it over
  `(RawSqlString)` and parameterise a table name; `Guards.bindingKept`);
  measured on .NET 8+ (the handler beats `Concat` past two parts and
  matches it below).
- **CR0101** — a literal format, no `IFormatProvider` argument (culture is a
  decision), no `params object[]` passed as one array, every placeholder
  index within range (CA2241's business otherwise); format and alignment
  carried into the hole; `{{`/`}}` kept; an argument used twice must be a
  pure atom; a call laid out over lines must fit the wrap column as one;
  the same handler gate and the same-overload guard as CR0100.
- **CR0102** — typed: the `ToString()` is parameterless and the hole has no
  format/alignment, or the `Join` receives an `IEnumerable<T>` whose `T` is
  the projected element (the generic `Join<T>` overload resolvable); on
  .NET 6+ the handler formats without boxing, which is the measured win.
- **CR0103** — the site must convert the string to `string` (or `object`):
  a `FormattableString`/`IFormattable` target, or a parameter that only
  accepts an interpolated-string HANDLER, wants the `$` (the F# twin lost
  `let s: FormattableString = $"…"` this way). A hole-free `$"…"` is a
  constant string, so a method with both a `string` and a handler overload
  binds the `string` one and the fix is safe there. Typed: the converted
  type answers; parse-only: any argument position, `return`, typed
  declaration or assignment keeps its `$`, `var x = $"…"` and a `+` operand
  do not.
- **CR0104** — subjects are identifiers or dotted pure reads; `Trim()` with
  no arguments strips exactly the `IsWhiteSpace` set; the bare
  `x.Trim() == ""` without a null guard is editor-only (null throws today).
- **CR0105** — `double`/`float`/`decimal`/`DateTime`/`DateTimeOffset`/
  `TimeSpan` parses; integer parses stay quiet; the primary is spelled short
  under an existing `using System.Globalization`.
- **CR0106** — `DateTime.Now.Date`-style calendar reads are excluded from
  the `UtcNow` fix (swapping underneath one manufactures the first bug);
  `Now` handed to a non-UTC timestamp setter (`File.SetLastWriteTime`) stays.
- **CR0108** — declined wherever `Regex` and `String` differ: a `$` in the
  replacement, an empty pattern, an anchor `Replace` cannot carry, a `$`
  anchor before a final newline, `RegexOptions` or `MatchEvaluator` overloads;
  the ordinal `StartsWith` overload is spelled.
- **CR0109** — literal pattern, constant options, single-line construction;
  a pattern the engine rejects is CR0107's (hoisted into a generated regex
  it would fail the build, where the call only threw when reached);
  `[GeneratedRegex]` resolvable and the containing type chain declared in
  this file (every part gains `partial`); otherwise a `private static readonly
  Regex` field above the member's doc comment; a name clash is a note; one
  member per pattern and options in a type (a second site refers to the
  first's member, an existing static field or generated method is reused);
  a binder named `regex`/`rx`/`pattern` does not name the member.
- **CR0110** — `HttpClient`'s note names the lifetime question rather than a
  fix; the crypto factories fire only inside loops.
- **CR0111** — BOTH separators need positive path evidence (path-ish names,
  rooted or extension literals, a literal existing on disk): a lone backslash
  used to fire alone until escape-sequence building (`result + "\\" + c`)
  showed where that goes wrong; a chain opening with `/` needs the stronger
  evidence (a web route); URL-shaped chains never fire; a join only compared
  or searched for is a key; a separator only joins with text on both sides.
- **CR0112** — ZWJ/ZWNJ are exempt (emoji, Arabic and Persian text).
- **CR0113** — decimal spellings only; unsigned skipped; a file inside a
  `checked` context or a compilation with `CheckOverflow` on is left alone.
- **CR0114/CR0115** — typed `Microsoft.Extensions.Logging` and Serilog;
  ANY mention of the exception in the arguments counts as handled, `ex.Message`
  included (a PII choice the rule must not escalate).

### 8.G Security and hygiene

| Code | Cat | On | API | Pri | Fires on | Fix | F# | Yields to |
|---|---|---|---|---|---|---|---|---|
| CR0120 | correctness | v | | v | `cmd.CommandText = $"SELECT … WHERE id={id}"`, `new SqlCommand("… " + name)`, `db.Database.ExecuteSqlRaw($"…")`, Dapper `Query($"…")` | note: parameterise | FR0066 | CA2100, CA3001 |
| CR0121 | correctness | v | | | a SQL command whose text carries no parameter at all | note | FR0146 | — |
| CR0122 | correctness | v | | v | `Process.Start("cmd", $"/c {input}")`, `psi.Arguments = "… " + input` | note: `ArgumentList` | FR0126 | — |
| CR0123 | correctness | v | | v | a literal matching a provider's documented key format (`sk-ant-`, `sk-`, `AIza`, `ghp_`, `AKIA`, `xoxb-`, PEM headers) | note | FR0127 | — |
| CR0124 | correctness | v | | | a credential in a `const` connection string on a non-loopback server | note | FR0153 | — |
| CR0125 | correctness | v | | v | `MD5.Create()`, `SHA1`, `DES`, `TripleDES`, `RC2`; `ServerCertificateCustomValidationCallback = (…) => true`; `SecurityProtocolType.Tls11`/`Ssl3` | note; editor: SHA256/SHA512, comment out or `Tls12` | FR0065 | CA5350/CA5351/CA5359/CA5364/CA5386/CA5397 |
| CR0126 | idiom | v | | | `new SHA256Managed()`, `new RNGCryptoServiceProvider()` | `SHA256.Create()`, `RandomNumberGenerator.Create()` | FR0128 | SYSLIB0021 (warning, no fix) |

Guards.

- **CR0120** — sinks are `CommandText` setters, `*Command` constructors,
  `FromSqlRaw`/`ExecuteSqlRaw`/`SqlQueryRaw`, Dapper's `Query*`/`Execute*`,
  and helpers named for SQL; a `FormattableString`-typed parameter
  (`FromSqlInterpolated`, `FromSql`) is the safe API and never fires (typed);
  text followed one hop through a local.
- **CR0122** — `ProcessStartInfo.ArgumentList` and a fixed file name never
  fire; a dynamically built string reaching `FileName`/`Arguments`/the
  two-string `Start` does.
- **CR0123** — format anchoring, not entropy; a literal containing `test`
  is a test credential and stays quiet.
- **CR0124** — loopback servers (`localhost`, `127.0.0.1`, `::1`, `(local)`,
  `(localdb)\…`, `.`) are never reported.
- **CR0125** — protocols from a curated list AND from `[Obsolete]`; the
  WebSocket handshake SHA-1 beside RFC 6455's GUID is quiet; a SHA-1 in a
  switch arm whose sibling constructs SHA-256 is a caller's format option;
  retiring a protocol changes what the wire negotiates, so it is editor-only
  or `drop_legacy_protocols`, never granted by `--api-changes`.
- **CR0126** — zero-argument constructors only; same algorithm, so
  behaviour is preserved; a weak algorithm keeps its CR0125 note separately.

### 8.H Cosmetic and redundancy

| Code | Cat | On | API | Pri | Fires on | Fix | F# | Yields to |
|---|---|---|---|---|---|---|---|---|
| CR0140 | cosmetic | v | | | `[SerializableAttribute]` | `[Serializable]` | FR0082 | — |
| CR0141 | cosmetic | v | | | `[Foo()]` | `[Foo]` | FR0083 | — |
| CR0142 | cosmetic | | | | `[A] [B]` on one line | `[A, B]` | FR0060 | — |
| CR0143 | cosmetic | v | | | `@name` where `name` is no keyword | `name` | FR0084 | — |
| CR0144 | cosmetic | v | | | `else { if (c) { … } }` | `else if (c) { … }` | FR0111 | — |
| CR0145 | idiom | v | | | `System.Text.Json.JsonSerializer.Serialize(…)` spelled six times in a file (four when three segments deep) | `using System.Text.Json;` after the last using, every use shortened | FR0147 | IDE0001 (once the using exists) |
| CR0146 | idiom | v | | | `public decimal Rate(int n) => …; // monthly, non-compounding` | `/// <summary>monthly, non-compounding</summary>` above it | FR0132 | — |

Guards.

- **CR0140/CR0141** — the short form resolves to the same attribute class
  (speculative check: an `Attribute`-suffixed name can be a different type
  from the unsuffixed one).
- **CR0142** — off by default: one attribute per line is C#'s dominant style.
- **CR0143** — not a reserved keyword and not a contextual keyword of the
  file's language version (`field` in C# 14 property accessors, `record`,
  `var`, `async`, `nameof`…), checked against the speculative re-bind.
- **CR0144** — the `else` block holds exactly the `if` and no comments are
  lost.
- **CR0145** — thresholds `uses` (6) and `deep_uses` (4); the namespace is
  not already imported (a `using`, a `global using`, or the SDK's implicit
  usings, read from the compilation) — that case is IDE0001's; a clash with
  a name the file defines or uses unqualified is a note; extension-method
  ambiguity and every other resolution change is caught by the speculative
  check, which is the guard here.
- **CR0146** — public declarations without XML docs; instruction comments
  (`TODO`, `HACK`, `pragma`-like), comments spelling code, and comments
  containing `<`/`&` (invalid XML text) are left alone; test files quiet.

### 8.I The language ladder (C# 11 – C# 15)

The families above are what clear C# has said since C# 8. Each version
since has closed a gap toward the functional style the tool is opinionated
for — records, exhaustive switches, list patterns, `field`, extension
blocks, and in C# 15 native unions — and every rule here is the rewrite a
newer level makes possible. Every one is gated on the file's effective
language version, read as a number (`int LanguageVersion >= 1100`), so
the analyzer pinned to Roslyn 4.14 gates C# 15 without naming it; `Latest`
and `Preview` are mapped through the host compiler's
`MapSpecifiedToEffectiveVersion`, so a project on `<LangVersion>preview`
under the .NET 11 SDK sees the C# 15 rules and one on the .NET 10 SDK does
not. Where a fix also needs a type (`UnreachableException`,
`FrozenDictionary`, `System.Threading.Lock`), §4.5's capability gate asks
the compilation.

*Verified against Roslyn 5.12 preview (2026-09):* `LanguageVersion.CSharp15
= 1500` exists and `Latest` maps to it; the compiler parses a
`UnionDeclaration` as `union Shape(Circle, Rect);` over existing types, or
`union Shape { record Circle(double R); record Rect(double W); }` — not
the comma-separated case list the early write-ups show. CR0156 targets
the first form and re-verifies its syntax against the shipped compiler
before its default is decided; until then it is gated behind C# 15 and
silent everywhere else.

| Code | Cat | On | API | Pri | Needs | Fires on | Fix | F# | Yields to |
|---|---|---|---|---|---|---|---|---|---|
| CR0147 | idiom | v | | | C# 11 | `if (xs.Length == 0) … else if (xs.Length == 1) { var a = xs[0]; … } else …` — a chain of length tests on one countable with positional reads | `xs switch { [] => …, [var a] => …, _ => … }` (or the `if` form with list patterns) | FR0112 (list half) | — |
| CR0148 | performance | v | | | C# 11 | `Encoding.UTF8.GetBytes("literal")`, `Encoding.ASCII.GetBytes("literal")` on a constant | `"literal"u8.ToArray()`; bare `"literal"u8` where a `ReadOnlySpan<byte>` is expected | — | — |
| CR0149 | idiom | | v | | C# 11 | an `init`/`set` property with no initialiser that every object initialiser in the compilation sets and no constructor assigns | `required` | FR0145 (the inverse) | — |
| CR0150 | performance | v | v | | .NET 8 | `static readonly Dictionary<K,V>` / `HashSet<T>` filled in its initialiser or static constructor and only ever read | `FrozenDictionary<K,V>` / `FrozenSet<T>` via `.ToFrozenDictionary()` / `.ToFrozenSet()` | FR0035 | — |
| CR0151 | performance | v | v | | C# 13 | `params T[]` on a method whose body only enumerates, indexes or measures the array | `params ReadOnlySpan<T>` | — | — |
| CR0152 | performance | v | | | C# 13, .NET 9 | `private readonly object _gate = new();` used only as a `lock` target | `private readonly Lock _gate = new();` | — | — |
| CR0153 | idiom | v | | | C# 14 | a property whose private backing field is referenced only inside that property's own accessors | the `field` keyword, the backing field removed | — | — |
| CR0154 | idiom | v | | | C# 14 | `if (x != null) x.P = v;`, `if (x is not null) x[i] = v;` | `x?.P = v;` | — | IDE0031 |
| CR0155 | idiom | | v | | C# 14 | a static class holding only `this T`-extension methods on one receiver type | an `extension(T x) { … }` block | — | — |
| CR0156 | idiom | v | v | | C# 15 | a memberless `abstract record Base;` whose only derived types are sealed records in the same assembly | `union Base(Case1, Case2);` with `: Base` dropped from each case | FR0016, FR0022 | — |
| CR0157 | idiom | v | | | C# 8, .NET 7 | a `switch` expression over an enum, a union or a sealed hierarchy whose discard arm throws `InvalidOperationException`/`SwitchExpressionException`/`ArgumentOutOfRangeException` without arguments | `throw new UnreachableException()` | — | — |
| CR0158 | idiom | v | | | C# 15 | a `switch` expression over a native union that lists every case and keeps a `_ =>` arm | note: the compiler now proves exhaustiveness, and the arm hides a missing case | — | — |

Guards.

- **CR0147** — the subject is a plain identifier typed as an array,
  `string`, `List<T>`, `Span<T>`/`ReadOnlySpan<T>` or any type with both a
  `Length`/`Count` and an indexer the compiler accepts in a list pattern
  (the speculative check decides); every link compares that length with
  `==` against an integer constant (0, 1, 2 …, ascending order not
  required); a branch reads `xs[i]` only for `i` below its own length, each
  read becoming a `var` binder named from the read's declaration (`var a =
  xs[0];` gives `[var a]`) or `_`; the shared switch guards of CR0002 apply;
  three links at least, or two with a terminal `else`; CR0002 stands down
  on the same chain (a length is a constant comparison too — CR0147 is the
  better spelling and wins the overlap).
- **CR0148** — the argument is a compile-time constant string whose every
  character is below U+0080 (so UTF-8 and ASCII agree and `u8` is exact);
  `Encoding.UTF8`/`Encoding.ASCII` bound to the BCL properties; a
  `byte[]` target keeps `.ToArray()` — the allocation stays but the
  transcoding goes; a `ReadOnlySpan<byte>` target drops it; measured on
  the allocation axis for the span case.
- **CR0149** — every construction site of the type in the compilation is an
  object initialiser or `with` that sets the property; no constructor of
  the type assigns it; no initialiser on the declaration; the type is not
  deserialized by the shape heuristic (a serializer constructs without
  initialisers); `required` is an API demand on callers, so the public
  shape is gated on `--api-changes` and the internal one on the friend
  check; `SetsRequiredMembers` constructors are respected. *Off by default
  (decided at M3):* `required` is also a runtime demand on every
  deserializer of the type — System.Text.Json throws on a missing required
  member — and no build can prove a type is never deserialized.
- **CR0150** — the field is `static readonly`, written exactly once (its
  initialiser, or a static constructor that fills it and nothing else
  touches it after); every other reference is a read (`TryGetValue`,
  indexer get, `ContainsKey`, `Contains`, `Count`, enumeration,
  `Keys`/`Values`); the field is private, or internal under the friend
  check — a `Dictionary` typed field is API; `FrozenDictionary` resolves
  in the compilation; the comparer argument travels; measured on lookup
  throughput and on the build cost against the number of reads.
- **CR0151** — the array parameter is used only for `foreach`, `Length`,
  indexing, or passing to a `ReadOnlySpan<T>`/`params ReadOnlySpan<T>`
  parameter; never stored, returned, captured by a lambda, passed to an
  array or `IEnumerable<T>` parameter, or used with LINQ; the method is
  private/internal or `--api-changes` is on (a `params` type change is
  binary-breaking); overload resolution re-checked speculatively at every
  call site in the compilation.
- **CR0152** — the field is `private readonly object` with a `new()`/`new
  object()` initialiser; every reference is the operand of a `lock`
  statement (`Monitor.Enter`, `Wait`, `Pulse`, passing or comparing it
  vetoes); `System.Threading.Lock` resolves; measured (the dedicated type
  skips the object-header lock path).
- **CR0153** — the backing field is private, unattributed, and referenced
  only inside the getter and setter of one property of the same type —
  in every part of a partial type, across files; a `nameof` of the field or
  a string literal spelling its name anywhere in the compilation
  (reflection by name) vetoes
  (a constructor writing it vetoes — that constructor would have to
  target the property, which changes when the setter runs); no
  `[ThreadStatic]`/`volatile`; the field's initialiser moves to the
  property; the name `field` is not already an identifier in the type
  (the contextual keyword would shadow it).
- **CR0154** — the condition is a null test of a pure read `x` (identifier
  or dotted read through CR0004's receiver test), the body is exactly one
  assignment whose target is `x.P`/`x[i]` with `x` the same text, no
  `else`; the right-hand side of `x?.P = v` is not evaluated when `x` is
  null, which is what the original did too, so `v` may be anything;
  compound assignments (`+=`) included; `x` not assigned inside the body.
- **CR0155** — off by default, v2: the class is `static`, every member a
  `this T`-extension method on the same first parameter type, no fields or
  nested types; the block keeps every method body verbatim with `this T
  x` becoming the receiver; callers do not change; a public static class
  is API (the class name disappears from reflection and from callers who
  invoked the methods statically), so the api gate applies and
  the tool re-binds every call site.
- **CR0156** — the base is an `abstract record` (or `abstract class` with
  no members beyond an implicit constructor) with no members, no
  interfaces and no attributes; every derived type is a `sealed record` in
  the same assembly and derives from nothing else; nothing references the
  base as a type argument constraint or through reflection; the union
  syntax is the one the compilation's compiler accepts (the speculative
  check proves it); every `switch` over the base still compiles; the
  public shape is API (the base type changes kind) and gated.
- **CR0157** — the scrutinee's type is an enum, a native union or a
  sealed hierarchy every case of which the switch lists (so the discard
  is unreachable by construction); the thrown exception is
  parameterless; `System.Diagnostics.UnreachableException` resolves; a
  message argument stands the fix down to a note (the message may be a
  contract).
- **CR0158** — C# 15 only; verified against the shipped compiler before
  its default is decided (the compiler may warn on the arm itself, in which
  case this rule yields to that warning id).

### 8.J Defects F# does not have (captures, copies, races, lost values)

The families above port the F# rules and add what clear C# says. This
family is the other direction: defects a C# program can have that an F#
program cannot by construction — a `for` variable captured by a closure
and read after the loop moved on (F# closes over an immutable value), a
mutating call on a struct the compiler copied first (F# structs are
immutable unless spelled `mutable`), a rethrow that drops the inner
exception, a collection mutated under its own `foreach`, a token accepted
and never observed. The fixes here are the corrections, not equivalences: 
a rule fixes only where the defect is
proven from the typed tree, and where the intent could go either way it
reports.

| Code | Cat | On | API | Pri | Fires on | Fix | F# | Yields to |
|---|---|---|---|---|---|---|---|---|
| CR0160 | correctness | v | | v | a lambda or local function created inside a loop reads a local the loop writes (the `for` variable, or a local assigned in the body), and the closure outlives the iteration — stored in a collection, a field, an outer local; returned or yielded; handed to `Task.Run`, `Task.Factory.StartNew`, `ThreadPool.QueueUserWorkItem`, a `Thread`, a `Timer`, an event `+=`, or kept alive through a lazy LINQ chain that escapes | `var i1 = i;` before the statement, the closure reading `i1` (the per-iteration copy the author meant) | — (immutable bindings) | — |
| CR0161 | correctness | v | | v | a call to a mutating method of a non-`readonly` struct on a receiver the compiler copies first: a `readonly` field, a property, a `List<T>`/`IList<T>` indexer, a `foreach` variable, an `in` parameter | note: the call mutates a copy | — (immutable records) | — |
| CR0162 | correctness | v | | v | `new System.Threading.Timer(…)` whose result is dropped or bound to a local that never leaves the method | note: the timer is collected with its last reference and stops firing | — | — |
| CR0163 | correctness | v | | v | `sem.Wait()` / `await sem.WaitAsync()` (`Semaphore.WaitOne`, `Mutex.WaitOne`, `ReaderWriterLockSlim.Enter*Lock`) followed by statements and a matching `Release()`/`ReleaseMutex()`/`Exit*Lock()` in the same block with no `try` between them | `try { … } finally { sem.Release(); }` | — | — |
| CR0164 | correctness | v | | | `if (_cache == null) _cache = expr;` / `_cache ??= expr` on a `static` reference-typed field outside any `lock` | `LazyInitializer.EnsureInitialized(ref _cache, () => expr)` | — (`lazy`) | — |
| CR0165 | correctness | v | | v | `catch (Exception ex) { throw new MyException("…"); }` — a wrapping throw that drops the caught exception, where the wrapper has a `(string, Exception)` constructor | `throw new MyException("…", ex);` (naming an unnamed catch `ex`) | — | CA2200 |
| CR0166 | performance | v | | | `try { v = int.Parse(s); } catch (FormatException) { … }` and `try { return int.Parse(s); } catch (…) { return d; }` — a parse driven by its exception, for a type with the matching `TryParse` | `if (!int.TryParse(s, out v)) { … }` / `return int.TryParse(s, out var v) ? v : d;` | FR candidate | — |
| CR0167 | correctness | v | | | `a == b` / `a != b` where both sides are `float`/`double`/`Half` and neither is a literal | note: floating-point equality; compare a difference against a tolerance | FR candidate | — |
| CR0168 | correctness | v | | | `a / b` with two integral operands whose result lands in a `double`/`float`/`decimal` — a declaration, an assignment, a return, an argument | note; editor offers `(double)a / b` | — | — |
| CR0169 | correctness | v | | v | `DateTime.Now`/`Today` compared with, subtracted from or assigned beside `DateTime.UtcNow` (directly, or through a local, field or property set from one of them once) | note: the two kinds differ by the machine's offset | FR candidate | — |
| CR0170 | correctness | v | | | a method with a `CancellationToken` parameter holding a loop that awaits, sleeps or blocks on a task and never reads the token — no `ThrowIfCancellationRequested`, no `IsCancellationRequested`, no call taking it | `ct.ThrowIfCancellationRequested();` as the loop's first statement | — | — |
| CR0171 | correctness | v | | v | `foreach (var x in xs) { … xs.Remove(x) … }` — the enumerated collection mutated under its own enumeration (`Add`/`Insert`/`Remove`/`RemoveAt`/`Clear`) | `xs.RemoveAll(x => cond)` when the body is exactly `if (cond) xs.Remove(x);` on a `List<T>`; otherwise a note | — | — |

Guards:

- **CR0160** — the closure is a lambda, anonymous method or local
  function; the captured local is declared outside the closure and inside
  the method; the loop is `for`, `while` or `do` (a `foreach` variable is
  fresh per iteration since C# 5) or the local is assigned inside the
  loop body outside the closure; the closure only READS the local (a
  write inside it, or in any other closure of the loop, makes the shared
  cell the point); an outer loop's variable read from an inner loop is
  as captured, the copy landing right before the closure in the inner
  loop; the closure escapes
  the iteration through one of the listed channels, climbed through
  parentheses, casts, `?.`, conditional expressions and lazy LINQ chains
  (a callee returning `IEnumerable<T>`/`IQueryable<T>`/`IOrderedEnumerable<T>`
  keeps the closure alive; one returning anything else consumed it); a
  closure passed to any other callee is a note, never a fix (the callee
  may invoke it before the next iteration); the copy takes the local's
  name with the first free numeric suffix, declared with `var` before the
  statement holding the closure (in the loop body, at statement level —
  a closure in the loop condition or incrementor is a note); the same
  statement creating several closures over the same local shares one
  copy; the outer scope is not an expression tree.
  Outside a loop, a local assigned after the closure's creation and read
  by an escaped closure is a note only (late binding is sometimes meant).
- **CR0161** — the receiver's struct type is not `readonly struct`, the
  method is not a `readonly` member, and the method is proven to mutate:
  in source, it assigns a field or auto-property of `this`, calls another
  proven mutator on `this`, or passes `this` by `ref`; from metadata only
  `MoveNext`, `Reset`, `Enter`, `TryEnter`, `Exit`, `Dispose` (the
  enumerators and `SpinLock`). Receivers: a `readonly` field (instance or
  static) of the enclosing or another type, a property (any getter), an
  indexer whose containing type is not an array (an array element is a
  variable), a `foreach` iteration variable, an `in` parameter, a `using`
  variable. A local, an array element, a `ref` return, a `ref`/`out`
  parameter, `this` and a non-readonly field are variables and never
  reported. The `Dispose` case is reported only on a `foreach` variable
  or `in` parameter (a property's disposable is the property's).
- **CR0162** — the constructor is `System.Threading.Timer` (not
  `System.Timers.Timer`, which a started timer roots through the timer
  queue by its own `Elapsed` callback — measured, it keeps firing); the
  expression is a statement of its own, or the initialiser of a local that
  is never assigned to a field, a property, an outer collection, never
  returned, never passed as an argument, never captured by a closure and
  never disposed (a `using var timer` is a scoped intent).
- **CR0163** — the acquire is a statement of its own (`sem.Wait();`,
  `await sem.WaitAsync();`, `sem.Wait(ct);`) with no timeout argument and
  no result used (`if (sem.Wait(100))` is a different contract); the
  release is a statement on the same receiver (by symbol) later in the
  same block; no statement between them is a `try` whose `finally`
  releases the same receiver; the receiver is not released anywhere else
  in the block (two releases mean a counting semaphore) and not
  re-acquired between the two; nothing between them declares a local
  used after the release (the `try` would scope it out — the rule moves
  the release only, keeping declarations outside is not attempted: such
  a shape is a note); `Release` takes no argument; the statements moved
  may contain `return`/`break`/`continue` (the `finally` runs on each,
  which is the correction). The statements between become the `try`
  body, the release the `finally`.
- **CR0164** — the field is `static`, of a reference type, not `volatile`,
  not `[ThreadStatic]`, not `readonly`; the test is `== null`, `is null`
  or the `??=` form; the assignment is the whole `if` body (or the `??=`
  is the whole expression of a statement or a `return`); the initialising
  expression is provably non-null — a `new`, an array or collection
  expression, a string literal, an interpolated string, a `??` whose
  right side is one of these, or (under `#nullable enable`) a call whose
  flow state is not-null; the expression does not mention the field;
  neither the statement nor an ancestor is inside a `lock`, and the
  enclosing method is not a static constructor (both already serialise);
  `System.Threading.LazyInitializer` resolves. The `??=` form is rewritten
  in place; the `if` form to a statement, keeping a following `return
  field;` as `return LazyInitializer.EnsureInitialized(…)`. Instance
  fields are not reported (a per-instance cache is usually confined).
- **CR0165** — the `throw` is a statement directly in the catch block (or
  under an `if` in it), throwing `new T(args)` where every argument is a
  string-typed expression (one or more) and `T` has an accessible
  constructor whose parameters are the same arguments followed by one
  `System.Exception`; the caught exception (named or not) is mentioned
  nowhere in the arguments; `T` is not the caught type re-thrown
  identically (`throw;` is CA2200's business). An unnamed catch
  (`catch (Exception)`) or a bare `catch` gains the name `ex` (first free
  of `ex`, `e`, `exception`); a bare `catch` becomes `catch (Exception ex)`.
- **CR0166** — the `try` block holds exactly one statement: `target =
  T.Parse(args);` where the target is a local or field (not a property,
  not an indexer — `out` needs a variable) or `return T.Parse(args);`;
  `T` is `int`/`long`/`short`/`byte`/`sbyte`/`uint`/`ulong`/`ushort`/
  `float`/`double`/`decimal`/`bool`/`char`/`Guid`/`DateTime`/
  `DateTimeOffset`/`TimeSpan`/`Enum.Parse<E>` (→ `Enum.TryParse<E>`); the
  `TryParse` overload with the same arguments plus `out` resolves (proven
  by the speculative bind, so a `NumberStyles`/`IFormatProvider` variant
  goes through only where the framework has it); exactly one catch
  clause, no filter, no `finally`, covering every failure `TryParse`
  would answer `false` to: `Exception`/`SystemException` always,
  `FormatException` only for a parse that cannot overflow (`bool`,
  `char`, `Guid`, `DateTime`, `DateTimeOffset`, an enum) whose argument
  is not-null by flow analysis — a numeric parse under
  `catch (FormatException)` let an overflow propagate; `OverflowException`
  alone, `ArgumentException`, `ArgumentNullException` are notes; the catch variable, if named, is not
  read (the message would be lost); for the `return` form the catch body
  is exactly `return expr;` with `expr` pure. Any other `try` whose
  catch names `FormatException` and whose body calls a `Parse` of the
  list is a note.
- **CR0167** — both operands' types are `float`, `double` or `Half`
  (nullable lifted included); at least one operand is computed here —
  arithmetic, a call, a conversion — two stored copies of one value (a tie
  test after `>=`) compare exactly; neither operand is a literal, a constant
  (`double.NaN` tests read `IsNaN` anyway), `default` or a `Math.Round`/
  `Math.Floor`/`Math.Ceiling`/`Math.Truncate` call (whole numbers compare
  exactly); not inside a test method; not a `switch` pattern.
- **CR0168** — the division's operands are both integral typed (`int`,
  `long`, `short`, `byte`, their unsigned kinds, or `char`), neither is a
  literal `1`, the division is not itself under a cast, and the
  destination is `double`/`float`/`decimal`/`Half`: the declared type of
  a local or field being initialised, the type of an assignment target,
  the method's return type of a `return`, or the parameter type of the
  argument position (a `params` element type counts); a `Math.Floor`
  around it, or the result feeding an integral variable first, is not
  reported. The editor offer casts the left operand to the destination
  type (`(double)a / b`); a sweep never applies it (integer division
  into a floating target is occasionally meant).
- **CR0169** — an operand chain of `==`, `!=`, `<`, `<=`, `>`, `>=`, `-`,
  `CompareTo`, `Equals`, `Subtract` where one side derives from
  `DateTime.Now`/`DateTime.Today` and the other from `DateTime.UtcNow`,
  "derives" meaning the expression itself, a `.Date`/`.AddX(…)` on it, or
  a local/field/property (in this file) whose only write is one of those;
  a `.ToUniversalTime()`/`.ToLocalTime()` on either side stands the rule
  down; `DateTime.SpecifyKind` and `new DateTime(…, DateTimeKind.X)` make
  the kind explicit and stand it down.
- **CR0170** — the enclosing method, lambda or local function declares a
  parameter of type `CancellationToken` (its own parameter, not a field);
  the loop (`for`, `foreach`, `while`, `do`) body contains an `await`, a
  `Thread.Sleep`, or a block on a task (`.Wait()`, `.Result`,
  `GetAwaiter().GetResult()`) — an in-memory loop over a user call is not
  long by itself; neither the loop's condition
  nor its body nor any nested closure mentions the parameter; the loop is
  not nested inside another loop of the same method that observes the
  token (the outer check is the author's granularity) and not inside a
  `finally`; the loop body is a block (an expression-bodied loop is a
  note); the insertion is
  `ct.ThrowIfCancellationRequested();` with the parameter's name, at the
  block's first statement. Iterators (`yield`) are included. A token
  parameter declared `= default` is included (the caller who passes one
  expects it honoured).
- **CR0171** — the `foreach` source is an identifier or member access (a
  `.ToList()`/`.ToArray()` copy is another object; `d.Keys`/`d.Values`
  enumerate `d` itself — the key and value collections' enumerators check
  the owning dictionary's version — so the reference watched is `d`); a call
  `xs.Add`/`AddRange`/`Insert`/`Remove`/`RemoveAt`/`RemoveAll`/`Clear`
  on the same reference (by symbol) inside the loop body outside any
  nested closure, not followed by `break`/`return`/`throw` as the next
  statement in the same block (mutate-and-leave is the accepted idiom);
  the source's type is `List<T>`, `IList<T>`, `ICollection<T>`,
  `Collection<T>`, `ObservableCollection<T>`, `HashSet<T>`, `ISet<T>`,
  `Dictionary<K,V>`, `IDictionary<K,V>`, `SortedSet<T>`, `Queue<T>`,
  `Stack<T>`, `LinkedList<T>` — a concurrent collection is built for it;
  `Dictionary`/`HashSet.Remove` and a `Dictionary` indexer set on CoreLib
  tolerate enumeration (.NET Core 3.0+) and are quiet.
  The fix, only for a source typed `List<T>` in source or metadata: the
  body is exactly `if (cond) xs.Remove(x);` (block or not, no `else`) with
  `x` the iteration variable and `cond` pure, not mentioning `xs`; then
  `xs.RemoveAll(x => cond);` replaces the loop (`Remove(x)` removes the
  first element equal to `x`, which the enumeration reached before any
  duplicate — the same set of elements go).

## 9. F# rules with no C# twin

| F# rule | Why not |
|---|---|
| FR0002, FR0009, FR0025, FR0034 (module forms), FR0059, FR0069 | No `Option`/`Result` types. `Nullable<T>` is already a struct, so FR0069's boxing does not exist in C#; a `T?` reference is a flow annotation, not a container. |
| FR0003, FR0008, FR0023, FR0090, FR0091, FR0095 | No currying, composition operator, or data-last convention. Extension methods are data-first by construction. |
| FR0005, FR0073, FR0078, FR0029, FR0149's CE half | No computation expressions; C# async state machines have no resumable-code dynamic fallback (FS3511). CR0046 is the one C# state-machine shape worth a rule. |
| FR0006, FR0011, FR0087, FR0088, FR0096, FR0115, FR0129 (`function`) | No active patterns; C# patterns are covered by CR0003/CR0009/CR0010. |
| FR0007 | C# has no keyword for an immutable local. Fields are IDE0044's business (`readonly`). |
| FR0016, FR0022, FR0072 (DU half), FR0110 (DU half) | No discriminated unions below C# 15; CR0013/CR0014 cover enums, and the C# 15 `union` unlocks CR0156/CR0158 (§8.I). |
| FR0013, FR0094, FR0097 | Parentheses are load-bearing in C# (IDE0047 handles the redundant ones). |
| FR0024, FR0092 (the `failwith` half) | No `failwith`; CR0069 covers the message. |
| FR0031's three-hole limit, FR0042, FR0043 | The `printf` path does not exist; C# interpolation lowers to a handler on .NET 6+ and to `string.Format` before, which CR0100/CR0101 gate on. |
| FR0057 | XML doc drift is CS1573's when docs are generated, and a convention otherwise. |
| FR0080 | Tabs are `dotnet format`'s. |
| FR0089 | No list/tuple literal ambiguity. |
| FR0093 | C# tuples are `ValueTuple` already; CR0082 covers `Tuple<>`. |
| FR0099, FR0133, FR0135, FR0143, FR0144 | Light syntax, backtick names, literate scripts and `#load`/`#r` do not exist. |
| FR0102 | `List<T>` indexing is O(1); CR0026 covers the enumerable case. |
| FR0116 | No `let rec … and` groups. |
| FR0131, FR0158 | C# guarantees no tail calls; the loop is the right spelling. |
| FR0074, FR0140 | `with { A = r.A with { B = v } }` is how C# spells it; IDE0017 covers initialisers. |
| FR0145 | `required` members are a compile error when missing. |
| FR0077 | Missing interface members are a compile error with a Roslyn fix. |

## 10. Rules deliberately absent

Not by oversight; by the same policy that keeps FSharp.Refactor out of
naming and layout:

- Naming (IDE1006, CA1707–CA1725), file and member layout (SA1xxx), `var`
  (IDE0007/IDE0008), braces (IDE0011), `this.` (IDE0003), expression-bodied
  members (IDE0021–IDE0027), accessibility modifiers (IDE0040), `readonly`
  (IDE0044), unused parameters and variables (IDE0059/IDE0060): style, and
  owned by the IDE rules and `dotnet format`.
- Method length, parameter count, cyclomatic complexity, class coupling
  (CA1502, CA1505, CA1506): structure metrics agents make less relevant.
- `ConfigureAwait(false)` (CA2007): a library policy, not a defect.
- Exposing `List<T>` (CA1002), collection property setters (CA2227),
  `IEnumerable` return preferences: API design taste.
- Primary constructors (IDE0290), collection expressions (IDE0300–IDE0305),
  `is not null` (IDE0041), pattern-matching preferences (IDE0019/IDE0020/
  IDE0038/IDE0078/IDE0083), `switch` statement → expression (IDE0066),
  `using` statement → declaration (IDE0063), `new()` (IDE0090): Microsoft
  ships them with fixes; enabling them is the user's `.editorconfig`.
- `async`/`await` elision in general (only the exact CR0046 shape is safe),
  `ValueTask` misuse (CA2012 covers it), `BinaryFormatter` (SYSLIB0011),
  insecure randomness (CA5394), `GC.Collect()`.

## 11. The apply tool

The `fsharp-refactor` contract, with the pipeline simplified by Roslyn.

**Targets** — `X.csproj`, `X.cs` (its project found through the nearest
solution or directory walk-up; only that file is edited), `X.sln`/`X.slnx`,
`src/`, `"src/**/*.csproj"`, and a workspace directory of checkouts (each
analysed on its own into one report, paths relative to the workspace).

**Flags** — `--dry-run`, `--codes`, `--categories`, `--jobs`, `--framework`,
`--max-passes`, `--api-changes`, `--no-if-defs`, `--report <sarif|json|html|csv>`,
`--baseline`, `--fail-on-findings`, `--notes [on|off|only]`, `--format json`,
`--rules`, `--create-config`, `--mcp`, `--parse-only`, `--honor-suppressions`,
`--help`. Exit codes 0 clean, 1 failure, 2 usage, 3 findings (with
`--fail-on-findings`).

**Pipeline** per target:

1. `Microsoft.Build.Locator` registers the SDK `global.json` resolves;
   `MSBuildWorkspace` opens the solution or project. A multi-targeted project
   appears once per framework; passes run narrowest first. Workspace
   diagnostics (a project that failed to load) are printed and the project
   skipped, never guessed at. A C# script (`.csx`, the fsx twin) needs no
   MSBuild: `Scripts.fs` reads it into the adhoc workspace as a project of
   one `SourceCodeKind.Script` document with the running runtime as its
   references, `#r` resolved against the script's directory, the runtime
   directory and the NuGet cache (`#r "nuget: Name, Version"`, the newest
   matching version, the best `lib` framework), `#load` handed to the
   compiler's `SourceFileResolver` for context (a loaded file is a target
   of its own; a directory sweep lists every loose `*.csx`), dotnet-script's
   default `using`s, `ConsoleApplication` output so the script is a leaf.
   Nothing builds a script: the in-memory error count is the arbiter, and
   a host's globals (`Args`) are errors on both sides of it.
2. Refuse a compilation with errors (`GetDiagnostics()` at error severity),
   as today. `--parse-only` skips MSBuild: sources come from the SDK's
   default globs and the csproj's `Compile` items, no references, syntax
   rules only, the same skew warning as the F# README.
3. Per pass: `CompilationWithAnalyzers` (with `reportSuppressedDiagnostics`
   so the suppression policy can see pragmas) → findings → fixes taken from
   the pure core → grouped per file, overlaps held for the next pass →
   applied bottom-up as text changes → `compilation.ReplaceSyntaxTree` for
   every edited file → the in-memory error count must not rise. A rise
   bisects the pass per file: the file whose fixes raised it is put back and
   its fixes recorded as defects of this tool (the log line is the bug
   report), the rest stand. Files are written only after the pass verifies.
   Repeat until a pass applies nothing or `--max-passes`. This replaces the
   F# tool's per-pass `dotnet build` with a check that takes seconds and is
   exact for C#.
4. The api pass (`--api-changes`, `api_changes = true`, or a test project):
   cross-project fixes (CR0041, CR0069, CR0080–CR0083, CR0088) use
   `SymbolFinder.FindReferencesAsync` over the whole `Solution`, C# and VB
   projects alike, in one atomic edit set per finding, each touched file
   re-bound by the speculative check. *Shipped 2026-09-20:* a `TextEdit`
   carries an optional `File` (another file of the solution); the
   `RuleContext` carries an optional reference oracle (`References.fs`,
   `SymbolFinder` over the solution, a VB site read-only) that the tool and
   the fix provider supply and the compiler leaves `None`; a rule reaches
   the compilation's other trees without it (CR0041's callers, CR0082's
   spellings) and other projects through it; `Guards.speculativeCheckAcross`
   patches every touched tree of the compilation into one fork; the fix
   provider applies a set as a `Solution` change; the sweep applies each
   file's edits to its own document (a file reached this pass holds its own
   fixes to the next), verifies every touched project's error count,
   bisects per edit-set unit (a document's fixes with every file they
   reached stand or fall together), and builds the other projects touched.
   A cross-project fix only the oracle can see (the analyzer never reported
   it) becomes a finding of its own under the api pass, where the rule is
   on for the tree. A referencing F# project cannot be
   loaded by `MSBuildWorkspace`, so a C# project with an F# consumer keeps
   its public surface (`PublicSurfaceHeld`) unless the final build proves
   the consumer — the mirror of the F# tool's C#-consumer rule.
5. Final arbiter: `dotnet build` of every changed project in every
   framework, and of every project of another language that references
   one; a failure puts the run's fixes back file by file as unverified
   (never kept as pre-existing breakage) and says so. Child builds run with
   `MSBUILDDISABLENODEREUSE=1` and a bounded wait on their pipes.
6. Reports: SARIF 2.1.0 with the rule's help link, three lines of context,
   the fix as a `fixes` entry, the invocation record and a stable fingerprint
   (rule + file name + normalised surrounding source, the scheme of the F#
   tool, key `csrefContextHash/v1`) so `--baseline` ratchets identically;
   HTML with per-rule grouping and before/after; CSV; `--format json` on
   stdout. The README also documents the zero-tool CI path:
   `dotnet build -p:ErrorLog=findings.sarif` writes the analyzer's own
   findings as SARIF, since the analyzers run inside the compiler.
7. `--mcp`: `analyze` and `list_rules` over stdio, one resident workspace,
   the compilation cache warm across calls.

**Held-back accounting** — every gate (scope, capability, speculative
check, a shadowed Microsoft rule being on, a pragma under a `no-correctness`
policy) counts what it held and the run prints the counts with the key or
flag that would release them.

## 12. IDE plugins

### 12.1 VS Code

The C# extension (and C# Dev Kit) hosts Roslyn as a language server that
loads analyzers from the project. The extension therefore ships no language
client; it does three things:

- **Wire, with consent.** On first activation it offers to make the bundled
  analyzers apply machine-wide by writing an MSBuild user extension props:
  `%LOCALAPPDATA%\Microsoft\MSBuild\Current\Imports\Microsoft.Common.props\ImportBefore\CSharp.Refactor.props`
  (the `$(MSBuildUserExtensionsPath)` hook MSBuild imports for every
  project) containing an `<Analyzer Include="…/CSharp.Refactor.Analyzers.dll"/>`
  item under `Condition="'$(Language)' == 'C#' and '$(CSharpRefactorDisable)' != 'true'"`.
  Every project the machine opens or builds then runs the analyzers: in the
  VS Code language server, in Visual Studio, and in `dotnet build` (as
  `Info` diagnostics, invisible at normal verbosity). Decline and nothing is
  written; `Wire`/`Remove` commands re-offer and undo, and `Status` reports
  the wiring, the extension and analyzer versions, the C# extension's
  server version and whether the tool is installed. The per-project
  alternative — `Add to this project` writes a `PackageReference` with
  `PrivateAssets="all"` — is for teams that want the analyzers in source
  control.
- **Run the tool.** `Run on this workspace` (report-only / apply / apply with
  `--api-changes`), `Write a SARIF report`, `Review advisory notes as a
  page`, `Create or open the configuration` — the F# extension's commands,
  in the integrated terminal, offering to install the tool when missing.
- **Nothing else.** Squiggles, light bulbs and fix-all come from Roslyn.

### 12.2 Visual Studio 2022 / 2026

A VSIX whose payload is the analyzer assembly with `FSharp.Core.dll`,
declared as `<Asset Type="Microsoft.VisualStudio.Analyzer">` (the diagnostics)
and `<Asset Type="Microsoft.VisualStudio.MefComponent">` (the code-fix
providers), so every C# project opened in the IDE gets the light bulbs with
no project change and no effect on builds. The Tools > CSharp.Refactor menu
drives the tool exactly as the F# VSIX does (the command table must live
inside a managed `.resources` set — `EmbedCto.ps1` and the checklist in the
F# VSIX README apply verbatim). There is no error tagger, no suggested-actions
source, no LSP client: Roslyn renders everything.

VS 2026's Roslyn runs out of process; VSIX analyzer assets are forwarded to
it, but this is the M0 spike's job to prove on 18.x. Fallback if it fails:
the same `ImportBefore` props the VS Code extension writes, which VS honours
through MSBuild.

### 12.3 Rider and others

A `PackageReference` to the NuGet package. Nothing to build.

## 13. Testing and measurement

- **Unit tests** are the F# side's shape: a string-literal input, compiled
  against reference assemblies (`Basic.Reference.Assemblies` for .NET 10 and
  for netstandard2.0, so capability gates are tested from both sides), the
  rule run as a pure function, the suggestions asserted, every fix applied
  as text, the result recompiled: no new error, and for exactness-claiming
  rules an identical diagnostic set. One test per shape and one per guard;
  the stand-down cases are the ones that matter.
- **Host tests** drive the real `DiagnosticAnalyzer` through
  `CompilationWithAnalyzers`: descriptor completeness, `.editorconfig`
  reading, the yields-to gate, generated-code skipping, pragma handling and
  the suppression policy, the deep-stack worker.
- **Property tests** (`tests/CSharp.Refactor.PropertyTests`, FsCheck): a
  generated program — a class of members, one shape per rule, 121 of the
  124 rules reached (CR0017 is CR0160's wherever both read a shape;
  CR0156/CR0158 need C# 15) — under the invariants every rule promises:
  every fix compiles, no rule throws on damaged text, fixes reach a fixed
  point, the boolean rewrites keep the truth table. Where a wrong rewrite
  would compile, a shape carries a decoy that makes it fail to (an
  `[Obsolete(error)]` `FormattableString` overload for CR0100/CR0101, a
  tuple handed to `object` for CR0082). The conventions of real files are
  properties too: `#region`s, a `#if` and a comment inside a member, CRLF,
  tabs, a `#pragma` — every fix compiles and leaves what was there; and the
  language ladder — at every version from C# 7.3 to 13, a fix uses no syntax
  newer than the file's. Each runs over random programs and, deterministically,
  over every shape alone. The cross-cutting conditions the F# suite tries rule
  by rule (shadowing, reflection and `nameof`, expression trees, `dynamic`,
  `unsafe`, partial types across files, interface and override members,
  generated files, a framework lacking the type a fix introduces) are the
  example suite's `CrossCuttingTests`. The 2026-09-20 pass that built this
  found and fixed: CR0084's `readonly` offer on a method-written field,
  CR0109 hoisting a pattern the engine rejects, CR0047 spelling `Lock` and
  `new()` below their versions, CR0009's `or` pattern below C# 9 and a bare
  LF into a CRLF file, CR0029 fusing inside an expression tree, CR0153 blind
  to a partial type's other file and to reflection by name, and the tool
  rewriting files with an `<auto-generated>` header.
- **Catalog tests** keep `Rules.md` complete: every code has a row and a
  section; category, default, API and priority match the catalog; every F#
  twin named exists in FSharp.Refactor's `Rules.md`.
- **PerfClaims**: BenchmarkDotNet on .NET 10, one pair per claim, the
  contract of §5.8; losing shapes stay labelled NOT emitted; a flapping gate
  is isolated in its own process before it is called noise.
- **Sample project**: `tests/Sample` is C# the sweep must leave with zero
  findings and no diff (the analyzers are F#, so the dog-food run is over
  the sample and the tool's own C# test fixtures, not over the analyzers).
- **IDE smoke**: the VS experimental instance recipe from the F# VSIX
  (touch the `configurationchanged` marker, watch the log, stop only that
  `devenv`), and VS Code with the C# extension on a fixture workspace.

## 14. Risks and spikes

M0 is one week of spikes, each with a pass/fail:

1. **F# analyzer in three hosts.** A two-rule analyzer (one parse-only, one
   typed, one fix) packed with FSharp.Core loads and fires under `dotnet build`,
   in VS 2026 (VSIX asset AND `PackageReference`), in VS Code's C# extension,
   and in Rider. Fail → the fallback is a C# adapter assembly that exports the
   analyzer and fix provider and calls the F# core, keeping the rules in F#.
   *2026-09-19, partly verified:* the spike analyzer (CR0090, CR0103) built
   against Roslyn 4.14 on netstandard2.0 with FSharp.Core 9.0.303 beside it
   fires under `dotnet build` of the .NET 10 SDK both as an `<Analyzer>` item
   and as a `PackageReference` from the packed `.nupkg` (nothing leaks into
   the consumer's `bin`), Info findings reach `-p:ErrorLog` SARIF, an
   `.editorconfig` severity override is honoured, and the F# fix provider
   registers both code actions and applies them through an `AdhocWorkspace`
   (`tests/CSharp.Refactor.Tests/HostTests.fs`). *Later the same day:* the
   VS Code extension (bundled analyzers wired through the MSBuild user
   `ImportBefore` props) and the VS 2026 VSIX (analyzer + MEF assets, no
   package code) both show the hints and fixes in the IDE. Rider untried.
2. **VS 2026 out-of-process analyzers from a VSIX asset.** *Passed
   2026-09-19* on VS 18.10 Professional in the Exp instance. One packaging
   trap: `[Content_Types].xml` must list every part's extension, including
   the `json` of the v3 servicing files written after the payload was
   staged, or the installer says "not a valid VSIX package".
3. **Roslyn pin.** The analyzer built against 4.14 runs inside the 5.x host
   over a C# 14 file without throwing; unknown kinds are opaque.
4. **Editor latency.** The memoised index over a 10k-line file stays under
   the per-keystroke budget the C# extension allows analyzers; measure with
   every v1 rule on.
5. **`MSBuildWorkspace` loading** across SDK pins, `global.json`,
   `Directory.Build.props` and multi-targeting; workspace failure
   diagnostics surfaced, never swallowed.
6. **Speculative check cost** — one file re-bind per candidate finding; if
   it dominates on files with hundreds of findings, batch candidates per
   file into one re-bind.

## 15. Milestones

| | Deliverable |
|---|---|
| M0 | The spikes of §14; repository skeleton; `Directory.Build.props`; catalog module with the 101 descriptors; `Rules.md` generated from it. |
| M1 | *Infrastructure done 2026-09-19:* tool with reports, baseline, suppression policy, `--create-config`, MCP, in-memory verification and final build; Rules.md kept by tests; CI; both extensions with the tool commands. *Open:* shared guards (`isPureExpression`, `callsOnlyCore`, speculative check, comment/directive guards, Scope/Visibility, Configuration); families A and H (the parse-only and simplest typed rules); tool in `--dry-run` with reports; unit and host tests. |
| M2 | Families B, C, D; the apply loop with in-memory verification, final build, held-back accounting, MCP; PerfClaims with every performance claim of those families; CR0028/CR0022 defaults decided. |
| M3 | *Rules done 2026-09-20:* families E, F, G and the language ladder I (§8.I, C# 11–15 gates), then family J (§8.J, the defects F# has no room for — CR0160–CR0171, 2026-09-20), 124 rules in the catalog; the api pass as the tool sees it (complete project graph, referenced executables not leaves, foreign consumers hold the surface, dependents verified and rolled back). *Done 2026-09-20:* cross-project `FindReferences` edit sets (step 4 of §11), C# scripts (`.csx`). *Open:* VS Code and VS extensions; NuGet, VSIX and Marketplace publishing scripts; CHANGELOG. |
| M4 | Corpus sweep over every C# checkout under `C:\git`; category and default decisions from the counts (§5.9); CR0088 editor-only; 0.1.0. |
