# Changelog

The analyzers package, the `csharp-refactor` tool and both editor extensions share one version. The NuGet packages carry the notes of the last six versions; this file keeps every one.

## 0.1.18

- CR0189 never takes the enclosing method as the token-taking overload: `WaitAsync(ct) => WaitAsync().WithCancellation(ct)` was rewritten to call itself.
- CR0060 counts a window shown non-modally (`form.Show(owner)`, `Application.Run(form)`) as handed on: a `using` there disposed the window on the spot.
- Two fixes of one file whose edits fall inside each other (CR0172's `const` into a declaration CR0177 hoists) no longer crash the run: the chooser keeps one, and a collision that still reaches the write leaves the file as it is for that pass.
- Notes printed inline carry their `note:` marker again; they read as applied fixes.
- An analyzer a project of the solution builds (an in-tree analyzer or generator) is loaded from a shadow copy: loaded in place, the tool held its dll open and the verification build of every project copying it failed with MSB3027 - no verdict on their fixes. The copy is this run's only: the workspace keeps its own references, so no `<Analyzer>` item is written into a project file.
- A project's baseline and after-fix errors are read as its build reports them: a compiler error one of the project's diagnostic suppressors withdraws (NUnit's for CS8618 on a field a `[SetUp]` assigns) no longer stops the project with "errors before any fix".
- A sweep's removals no longer leave two blank lines in a row, or one right after an opening or before a closing brace (StyleCop SA1507, SA1505, SA1508); a braced switch section is followed by a blank line (SA1513).
- Fixes are laid out as StyleCop expects, so a warnings-as-errors build no longer puts them back: CR0146 opens a blank line before the doc header and leaves a question alone; CR0153 and CR0177 take the removed declaration's blank line with it, and CR0153 stands down where the initialiser would follow a multi-line accessor list; CR0004 names its binder without the field's `_`/`m_` prefix; CR0164's check-then-assign is a braced `if`; CR0002/CR0003/CR0147 stand a `break;` off a closing brace; CR0100 drops the concatenation's parentheses inside a hole; CR0173 writes a conditional in the last arm without parentheses.

## 0.1.17

- A multi-targeted project is swept narrowest framework first, and a wider framework adds only the fixes in code the narrower ones do not compile (behind an `#if`, or in a file of its own): `init` (CR0083) is no longer offered under net8.0 for a file net48 builds too, in a dry run and in the editor alike. Frameworks compiling the same sources are swept once.
- A fix put back because another framework or a referencing project stopped compiling names that project and its errors, where the message said "0 error(s)".
- A cross-project reference search that failed once is tried again on the next ask instead of holding every later fix of that symbol for the rest of the run.

## 0.1.16

- New CR0190: a date format with the wrong specifier is corrected: `mm` among date parts to `MM`, `MM` among time parts to `mm`, `hh` without AM/PM to `HH` (swept only in a timestamp whose file renders no designator and parses no `h` format, otherwise an editor offer); editor offer only at `ParseExact` (F# twin FR0175).
- New CR0191: `new DateTime(now.Year, now.AddMonths(-1).Month, 25)` reads the year from the shifted instant too; the reverse mix is an editor offer, a day of another instant a note (F# twin FR0176).
- CR0105 also covers `Convert.ToDecimal`/`ToDouble`/`ToSingle`/`ToDateTime` on a string (F# twin FR0067).
- New CR0192 (note): `x != A || x != B` is always true, `x == A && x == B` and `x > e && x < e` always false; the editor offers the other operator (F# twin FR0177).
- New CR0193 (note): `x.Value` read in the branch where the nullable was tested empty throws every time (F# twin FR0178).
- New CR0194 (note): `list.Add(x);` on an immutable collection, or `s.Trim();`, drops the result and changes nothing; the editor offers the assignment (F# twin FR0179; the string shape yields to CA1806).
- New CR0195: an assignment standing as a condition becomes the comparison: `if (done = false)`, `if (a = b)`; an editor offer in lambdas, query clauses and loop conditions, a note for an assign-and-test.
- New CR0196: a `DateTime`, `decimal`, `Guid`, `int` or enum compared with `null` loses the dead test: the `if` removed or its live branch kept, `d != null ? a : b` to `a`, `d == null || p` to `p`.
- New CR0197 (note): a specific `catch` around `Wait()`, `Result` or `Task.WaitAll` never sees the task's exception; the editor offers an `AggregateException` clause filtered on the inner exception, or the removal of an unreachable clause.
- CR0031 also takes a clock-seeded `new Random(DateTime.Now.Millisecond)`, `new Random(Environment.TickCount)` and the like for `Random.Shared`; a note where the framework has none.
- CR0124 also notes a literal given to a parameter, field, property or local named as a credential (`password`, `clientSecret`, `apiKey`, `token`).
- CR0164 also reads the older `_cache ?? (_cache = new X())` on a static field.
- CR0195 and CR0196 are quiet where the compiler's CS0665, CS0472 or CS8073 is switched off by `#pragma` or `NoWarn`.
- Reports mask the source text of a CR0123 or CR0124 finding.

## 0.1.15

- CR0064 stays quiet on a catch-all with a filter, on the exception or on state (`catch when (_stopping) { }` lets every failure surface while not stopping); only a constant `when (true)` is still noted (F# twin FR0055).

## 0.1.14

- `--define <symbols>` (also `--define:A`, `-d:A`, repeatable or `;`-separated) and `csharp_refactor.defines` in `.editorconfig` define preprocessor symbols for the run, so code under `#if LOCAL_BUILD` is analysed, fixed and verified. They are added to each project's own `DefineConstants` (DEBUG and TRACE stay), and reach scripts and legacy projects too; a script whose `#r` sits under an undefined `#if` gets the hint (same spelling as fsharp-refactor). A resident host (`--mcp`) gives a script or legacy project it loaded earlier each run's symbols, not the first run's.

## 0.1.13

- A git worktree nested inside its own repository (`.claude/worktrees/...`) is no longer swept as more of the tree; a worktree beside its repository and a submodule still are.

## 0.1.12

- New CR0186: trivial backing-field property becomes an auto-property (F# twin FR0026).
- New CR0187: `throw ex;` in its own `catch` becomes `throw;` (F# twin FR0044).
- New CR0188: `x.ToLower() == "abc"` and friends become `StringComparison.OrdinalIgnoreCase` calls (F# twin FR0039; 20x faster, no allocation).
- New CR0189: pass the in-scope `CancellationToken` to a call that omits it, only for work that is waited for (F# twin FR0118).
- CR0080, CR0083 and CR0180 under `--api-changes` keep members that other projects of the solution write, hash or compare; CR0083 also keeps a setter set in a nested initializer.
- A project referenced from outside the run (a HintPath or ProjectReference in the same repository) keeps its public surface, as fsharp-refactor holds it.
- Guards ported from the F# twins: CR0182 takes only BCL dictionaries and keeps virtual receiver properties; CR0189 skips `Register` callbacks; CR0186 keeps accessors holding a comment or `#if`; CR0180 keeps fields whose comment mentions folding or inlining.
- `--jobs` works (default 4, clamped to 2-4) and also parallelises the per-file fix pass; one reference oracle per solution.
- Later sweep passes run in half the time or less; CR0149's `required` check is up to 3.5x faster.

## 0.1.11

- New CR0179: member-by-member setup after construction becomes an object initializer (F# twin FR0140; yields to IDE0017).
- New CR0180: static field nothing writes becomes `static readonly` (then `const` via CR0172).
- New CR0181: `switch` statement of returns, assignments or throws becomes a switch expression; `csharp_refactor.CR0181.drop_throwing_default = true` drops a throwing `default` over a fully named enum.
- New CR0182: `ContainsKey` plus indexer read becomes `TryGetValue` (F# twin FR0014; yields to CA1854).
- New CR0183: private `bool TryX(…, out T value)` returns `T?`, callers matching `is { } o`.
- New CR0184: private read-only `static readonly T[]` table becomes `ImmutableArray<T>`.
- New CR0185 (off by default): read-only `List<T>`/`IList<T>` result returns `IReadOnlyList<T>`; public methods only under `--api-changes`.
- CR0173 joins a bare declaration right above the `if` (`var xs = a ? F() : G();`), takes `if`/`else if` chains as a conditional ladder, and assigns only to a local, parameter or field by name.
- CR0083 lets System.Text.Json 8+ property attributes (`[JsonPropertyName]` etc.) take `init`, and keeps a setter with an initializer or constructor value.
- A query expression over `IQueryable` counts as an expression tree for the string, span, regex, culture and Select-fusion rules.
- Review fixes in CR0179, CR0180, CR0181, CR0182, CR0183 and CR0184; the query guard no longer covers the first `from`'s source.
- `--api-changes` on the command line now reaches rules whose fixes stay in one file (CR0080, CR0083, CR0180).
- Rules no longer stand down merely because a Microsoft twin has a severity in `.editorconfig`; `csharp_refactor.skip_microsoft_duplicates = true` restores that, and `--create-config` writes it.

## 0.1.10

- CR0032 counts a compound store's getter where it runs and follows ToString to the nearest base override.
- `--codes` and `--categories` skip the other rule modules entirely; later passes re-analyse only the files the previous pass touched; the header says `1 of 131 rules` when restricted.
- CR0023 leaves literals too short for a set to pay (measured floors); `csharp_refactor.CR0023.min_elements` overrides.
- CR0109 names a hoisted regex after the enclosing member (`PostcodeRegex`), strips escapes from pattern words, and never takes a name a base type, the type itself or the site holds; `csharp_refactor.CR0109.per_call = false` keeps only loop hoists.
- CR0040 offers the synchronous sibling for a `.Result` drain (`csharp_refactor.CR0040.sync_swap = true` for sweeps), bound where it stands and never under `catch (AggregateException)`.
- CR0125 offers to comment a retired protocol out of the flags being switched on (`csharp_refactor.CR0125.drop_legacy_protocols = true` for sweeps).
- `--create-config` writes every rule's knobs at their defaults.

## 0.1.9

- CR0032 keeps the `d.Keys` loop only where it sees the dictionary written before a lookup; otherwise it fixes.
- CR0166 keeps the catch only where the Parse argument can throw on its own, following user methods and getters three calls deep, and keeps a store into a possibly-null target.
- CR0011 (`Count > 0` → `Any`) and CR0022 stand down only on a throwing or effectful predicate or a source that runs user code when enumerated.
- CR0175's length guard proves the cut unless something visible writes the receiver between guard and cut.
- The visible-body proofs follow partial methods, delegate stores, `using` Dispose and ToString overrides, and treat `ToList()`/`ToArray()` copies as eager.
- CR0020, CR0021, CR0022 and CR0028 fire on conditions over auto-properties; CR0022, CR0028 and CR0171 spell LINQ only where no repository extension would take the call.
- Fixes: a `char` divisor no longer empties CR0011/CR0022 hints; top-level programs are read whole; CR0047 notes a top-level `lock` instead of throwing.
- A one-loop file analyses 12x faster (CollectionFixes split per operation kind); CR0022 compiles speculatively only for candidates it will fix.

## 0.1.8

- The speculative check binds only the touched members when every edit is inside member bodies: a full pass over a 21k-line file 30x faster, results unchanged.
- CR0041/CR0043, CR0023, CR0150, CR0152 and CR0069 search uses more narrowly.
- `CSR_SPEC_VERIFY` runs both checks side by side.

## 0.1.7

- CR0166 braces its replacement when the `try` is an unbraced `if` body followed by `else`.

## 0.1.6

- CR0054 leaves `Task.WhenAny`/`Task.WaitAny` of one task.
- CR0166 fixes a target that keeps its value on bad input through a fresh local.
- CR0049 fixes a `Task`-valued check-then-store; CR0164 uses `Interlocked.CompareExchange` for a factory that may return null.
- CR0178 sweeps a nullable integer, bool, enum or Guid column compared with a non-null value under no `!`.
- CR0177 never hoists a division or remainder by an integral `-1`.

## 0.1.5

- New CR0178 (performance, fix): filter in the query, not after `ToList()` - moves `Where`/`Select` of value columns before the copy (F# twin FR0174).
- Warnings-as-errors projects: the tool runs the project's escalated analyzers on fixes and holds a fix that raises one; a failing verification build is narrowed file by file.
- CR0011 no longer rewrites `Count() > 0` on types with their own `Count`/`Length`; CR0040 takes a completion probe in the same condition and the `WhenAny` winner as complete.
- Silent rewrites closed in CR0002, CR0005, CR0006, CR0015, CR0020, CR0029, CR0033, CR0040, CR0041, CR0043, CR0065, CR0081, CR0082, CR0089, CR0090, CR0102, CR0108, CR0109, CR0126, CR0149, CR0150, CR0151, CR0152, CR0154, CR0155, CR0166 and CR0173.
- Crashes fixed in `Text.isTestFile`, CR0107/CR0109 and the MCP server (malformed requests now answer an error).
- Tool: a failed verification puts back whole fix units; `<Sdk Name="…"/>` projects count as SDK-style.
- CR0063 leaves a `ref struct`'s `Dispose`; CR0168 notes `(int)MathF.Ceiling(items / pageSize)`; every rule has a guard test.

## 0.1.4

- CR0146 documents every enum member, not only the last.
- Rule regex patterns are built once at module level.
- `Guards.checked` renamed to `Guards.verified`; F# compiler warnings back to none.
- `StringClaimTests` checks its premises again.

## 0.1.3

- New CR0177 (performance, fix): hoist loop-invariant locals above the loop (F# twin FR0071; 4x on string concatenation).
- Editor offers ported from the F# side: CR0061 `IDisposable` implementation, CR0062 `field?.Dispose();`, CR0064 divisor guard / IO-filtered catch / log line, CR0070 `LoaderExceptions` messages.
- DESIGN.md names the F# twins of CR0160-CR0171.

## 0.1.2

- Visual Studio extension gains the Tools > CSharp.Refactor menu (run, dry run, notes, SARIF, `.editorconfig`).
- The tool prints run timing: load, compile, analysis and the slowest rule modules.
- Faster analysis: parallel on-demand cross-file index, cheaper CR0145 and CR0023, one speculative check per file for CR0083, CR0145 and CR0149 (24 s to 18 s wall on a large project).
- MSBuild warnings of a project that loaded are printed dim, not red.
- CR0108 rewrites `Regex.Matches(s, "lit").Count` to `s.AsSpan().Count("lit")` and `Regex.Split(s, "lit")` to `s.Split("lit")`; CR0109 never hoists a plain-text pattern.
- New CR0172: never-written constant local or static readonly becomes `const`; CR0173: common `return`/assignment of an `if` becomes a conditional.
- New CR0174: Substring handed to a span overload uses `AsSpan`; CR0175: cut-and-compare becomes ordinal `StartsWith`/`EndsWith`; CR0176: `foreach` walks the string, not `ToCharArray()`.
- PerfClaims measures every performance rule, enforced by a test.

## 0.1.1

- Package icon on both NuGet packages, the VSIX and the VS Code extension.
- Non-UTF-8 legacy-project and script files are decoded as MSBuildWorkspace does and round-trip on every platform.
- Fixes: CR0084, CR0109, CR0047, CR0009, CR0029 and CR0153 guards; the tool skips `<auto-generated>` files.
- Property suite covers 121 of 124 rules across real-file conventions and C# 7.3+; the MCP server (`--mcp`) is tested end to end.

## 0.1.0

- First version.
