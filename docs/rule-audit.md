# Rule audit: F# implementations against the C# design

Read on 2026-09-19: every FSharp.Refactor analyzer module behind a CR rule's
twin, compared with the guard list DESIGN.md §8 gives that CR rule. Each
entry lists the conditions, alternative shapes and knobs the F# CODE
enforces (often only in comments, sometimes not even in Rules.md) that the
design left out, with the C# reading of each. Items marked **port** go
into DESIGN.md's guard list; items marked *n/a* do not apply to C#, and
are recorded so nobody re-derives them.

Reading method: the module's doc comments and inline reasoning, which in
this codebase carry the guards; the code was opened where a comment named
a corpus case.

## Family A — expressions and control flow

### CR0001 ← FR0010 (Simplification.fs, OptionMatch.fs, ExpressionTree.fs)

- **port** The negated form: `if c then false else true` → `not c`. C#:
  `return c ? false : true` / `if (c) return false; return true;` → `return !c;`.
- **port** An `else if` link: replacing the whole `if` node with the bare
  condition glues it onto the preceding branch (F#: an `elif` node's range
  starts at the keyword). C#: an `if` that is the `else` clause of another
  `if` is not rewritten to a bare expression statement.
- **port** A test whose branch reads the payload is a match in disguise:
  FR0010 never produces `x.IsSome` beside `x.Value` and leaves the shape to
  FR0034. C#: CR0001 stands down where the branch reads `x.Value` of a
  `Nullable<T>` tested by `HasValue`; CR0004 owns it.
- **port** Nothing is rewritten inside an argument the compiler quotes into
  a LINQ expression tree (typed callee). Already the rule for CR0011; make it
  the shared rule for every family-A rewrite except CR0007/CR0008, which the
  F# side runs inside quotations deliberately (a strictly simpler tree).
- *n/a* The unannotated-parameter case (`x.IsSome` is FS0072 where the type
  is not settled): C# has no left-to-right inference.
- *n/a* Emptiness rewrites (`List.length xs = 0` → `isEmpty`): CA1827/CA1860.

### CR0002 ← FR0112 (IfRestructure.fs)

- **port** Only chain HEADS fire; an `if` nested in another's `else` yields
  an inner suggestion the overlap hold-back resolves in the outer's favour.
- **port** An else-less trailing `if` means the chain has no terminal
  `else`: the F# rewrite needs one for the wildcard arm and stands down. C#:
  the statement form (`switch` statement) is fine without a `default`; the
  expression form (`switch` expression) needs `_ =>` and stands down.
- **port** Verbatim and raw literals are valid constant patterns and are
  spliced with their original text, prefix included (`@"x"`, `"""x"""`).
- **port** The rewrite splices ORIGINAL source text of literals and bodies,
  never re-serialised nodes.
- The F# side has no minimum arm count; the design's "at least three" is a
  C# decision (two `==` tests read fine as `if`/`else`) — keep, but say so.

### CR0003 ← FR0103 (TypeTestChain.fs)

- **port** Branch bodies never ASSIGN the subject (the pattern variable
  would then diverge from it).
- **port** The subject is textually identical in every test and every cast;
  a plain `else` closes the chain, or its absence makes every branch a
  statement (`default:` omitted).
- **port** Only the outermost `if` of a chain fires; the `else if` links are
  visited separately and must not each produce a garbled suggestion.
- **port** A parenthesised cast is replaced together with its parentheses;
  substitution runs right-to-left per body.
- *n/a* Single-line branches (F#'s column-based substitution); C# spans are
  exact, multi-line bodies are fine.

### CR0004 ← FR0034 (OptionMatch.fs)

- **port** The `None` arm must not itself read `.Value` (that code throws
  today and is not ours to rewrite); the `Some` arm must read it at least
  once.
- **port** The binder name is derived (`v`, else `<x>Value`, else `value`)
  and must not appear ANYWHERE in the expression, and the receiver must not
  be rebound inside the branch (lambda parameter, local, pattern, loop
  variable): substituting under a shadow changes what the binder refers to.
- **port** The comparison spellings are the same test: `x == null`,
  `x != null` on a `Nullable<T>` (either operand order) swap the branches
  like `!HasValue`.
- **port** The boolean-chain forms: `x.HasValue && p(x.Value) && q` →
  `x is { } v && p(v) && q`; `!x.HasValue || p(x.Value)` → `x is not { } v || p(v)`
  (F#: `Option.exists`/`forall` over the fabricated lambda). Only the
  OUTERMOST chain node fires. A `Span`/byref-like local inside the
  predicates cannot be captured by a lambda in F#; in C# the pattern
  variable is a plain local, so this veto is *n/a*.
- **port** An else-less `if` gains no `else` (unit); the `IsNone`/negated
  forms swap branches.
- **port** A `match` or `try` opening the `Some` arm would take the `None`
  clause in F#; C#'s braces make this *n/a*, but a `switch` statement body
  whose pattern variable name collides with the derived binder is the C#
  reading of the same collision — covered by the "nowhere in the expression"
  rule.
- **port** Inside an expression tree the `HasValue`/`Value` shape IS what
  the provider translates: no rewrite there.

### CR0005 ← FR0113 (IfRestructure.fs)

- Matches the design. **port** the "same else on both levels" check is
  TEXTUAL identity of the two `else` blocks, comments included.

### CR0006 ← FR0114 (IfRestructure.fs)

- **port** Flipping across an `else if` chain is a different rewrite: the
  rule needs a plain `if`/`else` pair.
- **port** Both branches must sit on their own lines at the same depth so
  the blocks swap verbatim. C#: both branches are blocks, or both are single
  statements; a mixed pair stands down.

### CR0007 ← FR0108 (BooleanSimplify.fs)

- **port** A CALL as the kept operand may owe its `bool` type to the
  literal beside it (F#: an SRTP function's return pinned by `&& true`).
  C# reading: an operand of type `dynamic` — `d && true` is `bool` where `d`
  alone is `dynamic` — stands the rule down; outside a bool-pinning context
  (`if`, `while`, a guard, another `&&`/`||`, `!`) any call operand keeps its
  literal.
- **port** Runs inside expression trees and query lambdas DELIBERATELY:
  removing a node leaves a strictly simpler tree of shapes the translator
  already accepted.

### CR0008 ← FR0109 (BooleanSimplify.fs)

- **port** The purity allowlist: operators, property chains, indexing and
  comparisons pass; ANY call disqualifies, with a tiny allowlist (`not`,
  `isNull`; C#: `!`, `is null`, `string.IsNullOrEmpty`-class BCL predicates
  by `callsOnlyCore`). C# indexing must be an array or BCL indexer: a
  user-defined indexer is a call.
- **port** Runs inside expression trees deliberately, as CR0007.

### CR0009 ← FR0117 (MissingCases.fs)

- **port** An arm that carries a COMMENT is an arm the author considered a
  distinct case: each merged arm's comment travels with its own pattern
  (C#: `case 1: // why` keeps its trailing comment on its label; `or`
  patterns in a `switch` expression take the comment between alternatives).
  A comment BETWEEN arms belongs to neither and holds the fix (the comment
  guard). A comment inside the pattern or the surviving body is spliced
  verbatim and never hoisted twice.
- **port** Bodies compared as TEXT, single-line; guards refused; a lone
  lowercase identifier is a binder and refused (C#: `case var x:` and
  declaration patterns bind, `case Foo:` does not).

### CR0010 ← FR0129 (MatchGuards.fs)

- **port** Either operand order (`x == "A"` and `"A" == x`).
- **port** The guard must be EXACTLY the equality (no `&&`); constants a
  pattern can spell exclude what only expressions can (F#: `nan`, `+`
  expressions; C#: a non-constant `static readonly`, a `new`, a `nameof` is
  fine).
- **port** Works on `switch` statements and expressions alike (F#: match,
  match!, function).

### CR0011 ← FR0012 (HintEngine.fs)

- Read separately below (the engine is 1.4k lines); see §"CR0011 engine".

### CR0012 ← FR0100 (UnimplementedBranch.fs)

- **port** The phrase list: "not supported", "not implemented", "unsupported",
  "not yet", "nyi", "stub", "placeholder", "unfinished"…; bare `TODO`/`FIXME`
  are deliberately absent (they mark future work of every kind).
- **port** Commented-OUT code is not a note: a comment opening with an
  identifier applied to a string or a parenthesised argument, or carrying a
  format hole or a print call, reads as code and does not accuse.
- **port** A `null`/`default` return of a method whose declared return type
  makes it the legitimate "no result" — F#: a declared `option` return or a
  partial active pattern. C#: a `bool Try…(out T)` returning `false`, a
  method declared `T?` returning `null`, a `default` in a `FirstOrDefault`-
  shaped method stays; the placeholder set is `null`, `default`, `false`,
  `0`, `-1`, `""`/`string.Empty`, an empty collection expression, `Array.Empty<T>()`.
- **port** Only where sibling arms actually COMPUTE: a table of constants is
  data, not a stub.
- **port** Inside an iterator or async body the raise rides the siblings'
  keyword (F#: `return raise …` in a CE). C#: `throw` is a statement and
  needs nothing; in a `switch` EXPRESSION the arm becomes `throw new …`
  (a throw expression is legal there).

### CR0013 ← FR0072 (ExpandWildcard.fs)

- **port** Every explicit arm must cover its case TOTALLY (no `when`, no
  literal payload); the wildcard is a plain `_`/`default`, is LAST, and hides
  at most two members. Enums are open sets in F# too — ExpandWildcard never
  fires on them, which is why the design keeps CR0013 note-only in the sweep
  and has the editor keep a throwing `default:`.
- **port** A hidden member whose bare name another type in scope also
  declares is written QUALIFIED (`Color.Blue`); C# enum members are always
  qualified, so *n/a* — but a `using static` on the enum makes bare names
  legal and ambiguous, so spell `Color.Blue` always.
- **port** Two short cases share the wildcard's line; past 100 columns each
  takes its own line under the arm.

### CR0014 ← FR0110 (MissingCases.fs)

- **port** Guarded arms must still be PLAIN case patterns, or coverage of
  the whole match is unknowable; any total pattern (`_`, `var x`) completes
  the match and stands the rule down.
- **port** Every covered name must belong to THIS type (a same-named member
  of another enum must not count).
- **port** Multi-line matches only, each arm starting its own line, nothing
  trailing the last arm; new arms adopt the last arm's `|`/`case` column.
- **port** The added arm's keyword follows the siblings (F#: `return raise`
  in a CE). C#: `case M: throw new NotImplementedException();` in a
  statement, `M => throw new …` in an expression.

### CR0015 ← FR0101 (ListIndexing.fs, IndexedLoop.fs)

- read below (§"loops").

### CR0016 ← FR0141 (GenerativeLoop.fs)

- read below (§"loops").

### CR0017 (no twin) — nothing to audit.

### CR0015 ← FR0101 (IndexedLoop.fs) and CR0026 ← FR0102 (ListIndexing.fs)

- **port** (CR0015) The bound is literally `0 .. xs.Length - 1` (F#) — C#:
  `i < xs.Length` / `i < xs.Count` / `i <= xs.Length - 1`, `i++`/`i += 1`,
  start `0`, over the SAME path the body indexes; a `Seq.length`-style
  spelling counts (C#: `xs.Count()` on a list is CA1829's, leave it).
- **port** Nothing in the body may REBIND either name: a nested `for i`, a
  lambda parameter, a local, a pattern shadowing `i` makes the inner `xs[i]`
  a different index; and no name bound on the path to the loop (a parameter,
  an outer loop, a local) may clash with the chosen element name, which is
  `item`, else `item2`, `item3`… (Mibo: an outer `for x in 0..4` was
  shadowed by the chosen `x`).
- **port** The body may not take the ADDRESS of the element: `ref xs[i]`,
  `&xs[i]` (C# `ref var e = ref xs[i]`, `Span<T>` slicing by `i`) wants an
  lvalue, and a `foreach` element is a copy (Nu's Renderer2d).
- **port** The alias line: `var e = xs[i];` as the body's first statement and
  the index's only use → the loop variable takes `e` and the line goes; the
  binder must be a plain name, unannotated (`var`), nothing around the loop
  already binds it.
- **port** A string source: on .NET a string enumerates its characters, so
  `for (int i…) s[i]` → `foreach (var c in s)` is fine; F#'s Fable gate is
  *n/a*.
- **port** (CR0026) Constant indexes (`xs[0]`) are a deliberate head access;
  a receiver bound inside the loop (a fresh short sequence per iteration) is
  not the quadratic; a SMALL BOUND keeps the walk constant — `xs[i % 13]`, a
  loop `for i in 0..3` — and stands the rule down (F# `smallBound = 64`).
- **port** (CR0026) A `while` condition IS per iteration and counts as body;
  a `for` header's bound evaluates once (`i < xs.Count()` is per iteration
  in C# — the note fires there).

### CR0016 ← FR0141 (GenerativeLoop.fs)

- **port** The flag must be RAISED in the body (an assignment of `true`),
  and named negated in the condition (`!done`, also `!(a || b)` naming two).
- **port** A name only WRITTEN in the loop is a result being filled in, not
  state; a `let mutable` inside the body dies each round and is not carried.
- **port** Any carried value whose every assignment is `x = x + <literal>`
  (a stride, `low = mid + 1`) is an INDEX, and the loop is the search loop:
  no note (measured 12× faster than the LINQ spelling on an early hit).
- **port** The note counts the statements after the flag-raising one; zero
  means no note. A `let` contributes its right-hand side AND what follows.
- *n/a* Silence inside `task`/`async` builders (no recursion there): the C#
  remedy is `break`, which is legal in an `async` method.

### CR0011 engine ← FR0012 (HintEngine.fs)

The engine's safety rules beyond FSharpLint, all of which port:

- **port** A right side that drops or duplicates a metavariable fires only
  on pure atoms; substituted bindings are parenthesised unless atomic; the
  whole replacement is parenthesised when the matched expression was an
  operand of an enclosing application; an operand of `&&`/`||` is bracketed
  by PRECEDENCE, not like an argument (`!(a && b)`, never `!((a) && (b))`);
  equal precedence on the LEFT is the grammar's own grouping and goes
  unbracketed, on the right the brackets stay.
- **port** Bool-literal rules (`x == true`) need typed proof the binding is
  `bool` (F#: `obj` subsumes the literal; C#: `dynamic`, and a user-defined
  `==(T, bool)` operator).
- **port** NaN-sensitive rules — an ordering flip (`!(a > b)` → `a <= b`),
  a `CompareTo` collapse, a sort-to-min — need typed proof the operands are
  not `float`/`double` (a unit-of-measure float is a float too; C#: `decimal`
  is fine, `double`/`float`/`Half` are not).
- **port** Every non-metavariable name a side spells must resolve to the BCL
  (or `Enumerable`) at the site — a repository's own `IsNullOrEmpty`,
  a shadowing extension method — or the hint stands down; without a clean
  typed tree, the untyped path stands down only inside a query expression
  (F#: a computation expression's custom operation is the one shape a bare
  core name can be mistaken for). Custom hints from the config name the
  repository's own functions and are its author's to aim.
- **port** Names a file puts in scope that a right side might spell — a
  local, a parameter, a pattern binder, a `using static` member, an
  extension method in an imported namespace — make the hint fail closed.
- **port** An `=` directly inside a call's argument list may be a NAMED
  ARGUMENT (F#: `Timer(period, AutoReset = true)`); C#: `Foo(flag: true)`
  is unambiguous, but an `=` inside an object initialiser or an attribute
  argument IS an assignment — equality-headed hints never fire inside an
  attribute or an initialiser.
- **port** No hint fires inside an attribute argument, an expression tree,
  or a quotation.
- **port** Map fusion (`xs.Select(f).Select(g)` → `xs.Select(x => g(f(x)))`)
  fuses only mappers whose calls are provably effect-free: BCL/`Enumerable`
  functions, constructors of records/tuples, lambdas that call nothing but
  those, read no property declared outside the BCL (a user getter runs its
  body; `Lazy<T>.Value` forces), hold no interpolated string with a hole
  (formatted by a ToString the type chooses), no statement, assignment,
  loop, handler, `new` of a class, `await`; a mapper that THROWS by design
  is an effect (a different exception can escape once the sweeps
  interleave); the in-place `Array` operations are effects. (This is CR0029's
  guard list; the design's "provably pure `fst`/`snd`/`id`" is the F# FR0137
  fixed list, and the engine's richer proof supersedes it.)
- **port** An overloaded METHOD GROUP used as a first-class value resolves
  only because a pipe pins its argument type first; a hint must never move
  such a binding to a position checked before the element type is known
  (C#: `xs.OrderByDescending(File.GetLastWriteTime).First()` → `xs.MaxBy(File.GetLastWriteTime)`
  is fine in C# since type inference sees the receiver first — *n/a*, but a
  method group whose overload the target delegate type picks must keep a
  target of the same delegate type).
- **port** Nested matches keep only the OUTERMOST; unification sees through
  parentheses, and a parenthesised node is not matched twice.
- **port** Rules deliberately ABSENT and why: `Aggregate((a, b) => a + b)` →
  `Sum()` (unchecked wrap vs checked throw; Mibo); `First(p)` for
  `Where(p).First()` is fine, but `Where(p).First()` → `First(p)` keeps
  the same exception; F#'s head-of-filter → `find` is absent because the
  empty-input exception types differ — C#'s `Where(p).First()` and
  `First(p)` both throw `InvalidOperationException`, so *n/a*.
- **port** A hint whose left side is a pipeline renders its result back as a
  pipeline so the receiver is typed before the lambda (F#-specific
  inference; in C# the receiver is always first — *n/a*).

## Family B — collections and LINQ

### CR0020 ← FR0004 (ConversionMove.fs)

- **port** Refuse outright any operation that MUTATES, and read the
  callback for it: the eager copy is what keeps enumeration and mutation
  apart (SQLProvider lost 19 tests to `Seq.toList |> List.iter (fun k -> dict[k] <- v)`).
  The callback may not: assign; call a mutating METHOD on ANY receiver
  (`Add`, `Remove`, `Insert`, `Clear`, `Push`, `Enqueue`, `Set…`, `RemoveAt`…
  — an alias of the source is indistinguishable from a stranger); call a
  LOCAL function that assigns; or MENTION the collection itself (a lambda
  as much as a named function handed over, `List.iter (register es)`).
  The source must be OWNED by the enclosing method (a local, parameter,
  loop or pattern variable): a field or a captured outer collection can be
  written by anything the callback names, and stands the rule down.
- **port** `mutatesSomething` reads the TEXT for `<-` (C#: `=`, `+=`,
  `++`, `--` inside the callback's span), over-approximating in the safe
  direction.
- **port** A source that is ALREADY the target kind gains nothing from the
  lazy detour: an array literal, `Split`, `ToArray`, `GetFiles`, `ToList`
  feeding `ToList()` — no move, not even for a consuming drop (`Count()`
  over an array walks what `Length` reads in O(1)). C#: `xs.ToList().Count`
  on a `List<T>` source, `arr.ToArray().Length` — stand down; CA1829/CA1860
  own the rest.
- **port** Movable operations are the ORDER-PRESERVING element transforms
  whose variants agree once the trailing conversion forces evaluation;
  `groupBy` is absent (the element type changes); `skip`/`take` are absent
  (List and Seq variants throw different exception types on short inputs —
  C#: `Skip`/`Take` never throw, so *n/a*); `item` is absent (exception
  types differ — C# `ElementAt` vs indexer: `ArgumentOutOfRange` vs
  `IndexOutOfRange`, so keep it absent); the SORT family never crosses an
  Array boundary (Array sorts are unstable; C#: `Array.Sort` is unstable
  where `OrderBy` is stable — never rewrite `OrderBy` to `Array.Sort`).
- **port** Consuming operations may short-circuit source enumeration
  (`Any`, `First`, `Contains`) — the point of the rewrite, identical for
  pure sources only; so the source must be pure (no `yield`-based generator
  with effects the shortened enumeration would skip) — the same
  `callsOnlyCore` question on the source expression.
- **port** Both stages single-line in F#; C#: the two stages keep the
  LAYOUT they had (one call per line stays one per line).
- **port** Moving INTO the eager kind is measured per operation
  (`filter` into Seq −4%/−13%, `map` into Seq +35% time — never offered;
  into Array always wins). C# PerfClaims must measure `ToList().Where` →
  `Where().ToList()` and `ToArray().Select` → `Select().ToArray()` per
  operation before shipping each pair.

### CR0021 ← FR0050 (Accumulation.fs)

- **port** `Sum` only for a FLOATING accumulator (F#: `sum` is checked
  where the loop wrapped) — the design has this; add: `decimal` is checked
  in both spellings and qualifies; a STRING accumulator (`s = s + piece`)
  becomes `string.Concat(xs)`/`string.Join` (F#: `String.concat`), and a
  LAZY source gets materialised first (`String.concat` over a lazy seq hit
  the slow IEnumerable path, 16× slower).
- **port** The source must be a real generic `IEnumerable<T>` (typed): a
  `foreach` also accepts the non-generic `IEnumerable` and duck-typed
  `GetEnumerator` sources, which no LINQ operator does.
- **port** The accumulator must not be re-assigned in the continuation;
  an annotated declaration keeps its annotation on the folded binding
  (Mibo inferred a different type once it was lost — C#: a declared type
  stays declared, `var` stays `var`).
- **port** When the loop is followed by nothing but the accumulator
  itself, the fold expression IS the result: binding and trailing use
  collapse (F#) — C#: `var total = 0; foreach… ; return total;` →
  `return xs.Sum();`.
- **port** A fold that lands as a 150-character line is not a cleanup:
  the general `Aggregate` form is not emitted (design agrees); F# emits
  the fold, C# does not.
- *n/a* The `fold` lambda is checked BEFORE `init` in F# (member lookup
  on the accumulator meets an indeterminate type).

### CR0022 ← FR0107 (Accumulation.fs)

- **port** A prefix of PURE single-line immutable `let` bindings folds
  into the lambda (`var t = f(l); if (t == "x") found = true;` →
  `xs.Any(l => f(l) == "x")`); each must be effect-free and silent about
  the flag.
- **port** The purity test FOLLOWS a same-file function three declarations
  deep (`if (IsValid(x)) found = true` with a same-file `IsValid` whose body
  passes) and takes a function already under test (recursion) as pure;
  a callee declared elsewhere, a user method of another type, a
  constructor, any other .NET method stands the rule down; naming a
  function without applying it counts too (`xs.Any(validate)` hands the
  effect to a core function).
- **port** The predicate must fit ONE line.
- **port** The assigned literal must be the initializer's OPPOSITE.

### CR0023 ← FR0035 (LoopPerf.fs)

- **port** `Set` asks `comparison` where `contains` asked `equality`: a
  `[<NoComparison>]` record or a function-typed field takes the HashSet
  companion instead of the in-place conversion. C#: `HashSet<T>` asks
  only equality, so the in-place `FrozenSet`/`HashSet` conversion needs a
  T with value equality (`record`, `struct`, `string`, enums, a class with
  `IEquatable<T>`); a class with reference equality still converts (the
  probe was reference-equality already).
- **port** The in-place conversion changes the binding's TYPE and is an API
  change on a PUBLIC static field (fsharplint's public list in a NuGet
  library was converted without `--api-changes`): public → companion
  set beside it, never in place; private/internal → in place when EVERY
  use is a probe; a LATER file of an executable reading the binding at its
  list type keeps it a companion too.
- **port** Any OTHER binder of the same name anywhere (a parameter, a loop
  local, a lambda argument) makes the name ambiguous to the scan — no fix.
- **port** A `let` between the loop and the probe, or a match-arm pattern,
  may rebind the collection per iteration — loop-local, not
  loop-invariant; a lambda handed to a collection function is a loop.
- **port** The ROOT identifier of a dotted path is what loop-invariance is
  judged on (`config.Excluded` varies per iteration exactly when `config`
  does); a dotted path's storage is not this file's to shadow, so it gets
  the note, never the fix.
- **port** Probes under one `#if` get a companion under the same `#if`;
  probes under different conditions get none.
- **port** One companion for the whole group, the edit set carried by the
  FIRST probe only, so the group applies once.

### CR0024 ← FR0030 (AddRange.fs)

- **port** The RECEIVER must be the same list on every iteration:
  `columns[tile.X].Add(tile)` picks a list per element (Nu's 2048) and
  stands down.
- **port** A projected body stays a loop: `AddRange(xs.Select(f))` measured
  no faster and read worse (suave).
- *n/a* the F# range-source spelling (`AddRange(a .. b)` parses as an
  indexer); C#: `Enumerable.Range` is a normal source.

### CR0025 ← FR0051 (Accumulation.fs) and FR0104 (RecursiveAppend.fs)

- **port** The ref-cell form `acc.Value <- acc.Value @ [x]`: C# analogue
  is a captured local or a field (`this.items = this.items.Append(x).ToArray()`).
- **port** The appended operand must not be a NUMERIC literal (`i = i + 1`
  is the commonest statement in any loop, and resolving it put the rule at
  the top of the slow-analyzer list): string-ness is checked FIRST, being
  the selective test.
- **port** FR0050's fix already rewrites the exact string-accumulator shape
  into one `String.concat`: no note on a site the fix is about to remove.
- **port** Recursive form (FR0104): only a SINGLETON literal appended in a
  SELF-CALL argument (`Collect(acc.Append(x).ToArray(), rest)`); a general
  `a @ b` merge runs once per call and stays.

### CR0027 ← FR0076 / FR0017 (MapIgnore.fs)

- **port** `map` and `ignore` typed-gated against shadowing (C#: the call
  must resolve to `Enumerable`, not a user extension named `Select`).
- **port** The DIRECT form (`Seq.map f xs |> ignore` and `Module.map f xs`
  unpiped) is the one that runs nothing; C#: `xs.Select(f);` and
  `Enumerable.Select(xs, f);` both. `_ = xs.Select(f);` is a decision
  (design has it).

### CR0028 ← FR0156 (AccumulatorLoop.fs)

- **port** The element type must be CLOSED — a value type, record, tuple,
  string, function or `sealed` class: `Add` upcasts its argument to an
  interface or base class where the collection expression fixes the type
  at the first element; a delegate is not closed either (`Add` converted a
  lambda). C#: `new List<T>()` declares `T`, and `xs.Select(x => e).ToList()`
  infers from `e` — a `List<IShape>` filled with `Circle`s becomes
  `List<Circle>`; the rewrite must spell `Select<Circle, IShape>` or `Cast`,
  or stand down.
- **port** Every OTHER statement of the loops must be unit-typed (typed): a
  discarded non-unit call (`d.TryAdd(x, x)`) would become a yield in F#;
  C#: the rewrite is a LINQ pipeline, so any statement other than
  `if`/`foreach`/the `Add` stands the rule down (design says this).
- **port** An empty construction only: `new List<T>(xs)` starts full.
- **port** The declared type argument becomes the list's annotation (`Add`
  converted an `int` literal to `long`); C#: `Select` must yield the
  declared element type, so a literal `Add(1)` into a `List<long>` needs
  `Select(x => (long)…)` — stand down unless the projected type equals the
  declared type exactly.
- **port** The loops read no `Span`/byref-like value (cannot be captured
  by the lambda) — this one IS C#-relevant for the LINQ rewrite (a lambda
  cannot capture a `ref struct`).
- **port** The declaration alone on its line goes, but not from between two
  `#if` directives; the loops span no `#if` and no multi-line literal.
- **port** A drain whose parentheses were an application's own keeps a
  pair (`Some(List.ofSeq acc)` → `Someacc` was the defect).
- **port** Knobs: `arrays` (array-shaped drains, slower, off) and
  `explicitYield` (older compilers) — C#: `csharp_refactor.CR0028.arrays`
  for `ToArray()`/index/`Count` drains, measured separately.
- **port** A `while` loop feeding the accumulator qualifies in F# (a list
  expression hosts it); C#: no LINQ shape for a `while` — stand down.

### CR0029 ← FR0137 (MapFusion.fs)

Superseded by the engine's purity proof (see CR0011); keep "both stages
the SAME module's map" (C#: both `Enumerable.Select`, never an
`IQueryable` stage) and "same line" as layout only.

### CR0030 ← FR0139 (SeqOnArray.fs)

The C# rule is CA1851's shape, but the F# module's measurements bear on
CR0021/CR0031:
- **port** `iter`/`iteri` are absent (a wash); `contains` on a REFERENCE
  array is a small LOSS for the array module; `contains` on `int`/`long`
  has TWO answers (vectorised `Enumerable.Contains` 5.4× is the CLI's,
  the idiomatic one is editor-only); numeric aggregates are FR0041's.
  C#: `Enumerable` IS the vectorised path, so CR0021's `Sum`/`Max`/`Min`
  claims for `int[]`/`long[]`/`List<int>` are measured there; on
  `double` the NaN semantics differ between a `>` loop and `Max()`.

### CR0034 ← FR0028 (QueryInLoop.fs)

- **port** The outer loop may be a COLLECTION-FUNCTION CALLBACK
  (`customers.ForEach(c => db.Orders…)`, a `Select` lambda), not only a
  `foreach`; the chunking suppressor is read anywhere in the outer
  pipeline, and by NAME when the chunking was bound first
  (`var pages = ids.Chunk(200); foreach (var page in pages)`).
- **port** Inside a query expression a nested `from` is a JOIN the provider
  translates into one statement — but only when the OUTER source is itself
  `IQueryable` (an in-memory outer sequence runs the inner query per
  element, query syntax or not).
- **port** A query under a loop that PAGES (`Skip`/`Take` of the outer
  element) or filters by the outer element's BATCH (`chunk.Contains(o.Id)`)
  runs one statement per batch on purpose — not N+1.
- **port** Emits nothing when the file has type errors.

### CR0035 ← FR0058 (RecursiveSeq.fs)

- **port** A `yield!` in TAIL position of the body (the last step, reached
  through sequencing, bindings, `if`, `match`) is turned into a jump by the
  F# compiler and does not count; C#: `foreach (var c in Walk(child)) yield return c;`
  always nests — no tail-call form exists, so every self-entry counts, but
  `return Walk(child)` (no `yield`) is a redirect and never fires.
- **port** Only a DIRECT self-call is the nested enumerator;
  `Seq.collect walk xs` nests one per level inside `collect` and counts
  too (C#: `children.SelectMany(Walk)` inside the iterator counts).

## Family C — async and concurrency

### CR0040 ← FR0049 (SyncOverAsync.fs, BlockingSites.fs)

- **port** The bind fix is legal only on the CE's own STATEMENT SPINE: a
  blocking call nested inside ANOTHER binding's right-hand side sits inside
  the body's range but not on its spine (`var pair = (t.Result, 1)`; F#'s
  `let pair = let x = t.Result in …` was 21 rollbacks in one repo). C#:
  `await` is legal in any expression of an `async` method, so the spine
  restriction is *n/a* — but NOT inside a `lock`, a `catch` filter
  (`when`), a `finally`, an `unsafe` block, a lambda that is not itself
  `async`, or a local function body (the local function is its own
  boundary; F# names local functions and object expressions as closures
  the Lambda-node scan misses).
- **port** A `try` body whose handler names `AggregateException`: `.Wait()`,
  `.Result` and `WaitAll` throw the wrapper, a bind throws the inner
  exception, and the handler would go dead (`WaitAll`'s other failures with
  it). `GetResult()` already unwraps and is unaffected.
- **port** Completion proofs: under the receiver's own
  `IsCompleted`/`IsCompletedSuccessfully` in the `then` branch (conjoined
  with anything, or negated in the `else`); complete from birth
  (`Task.FromResult`, `CompletedTask`, `ValueTask.FromResult`, the
  exception/cancellation siblings, also through a local `var t = Task.FromResult(1)`
  or a static readonly); the antecedent inside its own `ContinueWith`
  continuation (the lambda's parameter, or a named function's); after the
  receiver's own `Wait(timeout)` above; `t.Wait(timeout)` itself is the
  bounded idiom; a `.Result` read after `proc.WaitForExit()` in the same
  body (the Process stdout pattern, forty times over in one repo).
- **port** `Task.WaitAll(tasks, timeout|token)`: the final argument is proven
  not a task, so the pair is not params-style and stays.
- **port** The console's blocking point: the spine of `Main`, a runner
  ending in an exit-code literal, top-level statements — no boundary note;
  anything behind a lambda or nested function is a boundary of its own.
- **port** A thread-choreographed body (a signal, a `Thread`, `Interlocked`)
  gets the note but never the fix: a bind moves the continuation off the
  thread the wait kept it on. `Thread.Sleep` alone is a pause.
- **port** Synchronisation-primitive waits inside an async method
  (`ManualResetEventSlim.Wait`, `SemaphoreSlim.Wait`, `WaitHandle.WaitOne`,
  `Barrier.SignalAndWait`, `Thread.Join`, `Monitor.Wait`) are advice only
  (C#: `SemaphoreSlim.WaitAsync` exists — CR0042's twin logic can offer it).
- **port** `ContinueWith` reading `.Result`: only the PLAIN shape rewrites —
  one lambda, no scheduler/options, a single-line body whose every use of
  the antecedent is `a.Result`, a value-returning continuation, the call
  closing its line, the binder name free in the body; a body testing
  `IsFaulted`/`Status` handles the antecedent itself and stays a note.
- **port** The sync-sibling swap requires the SIBLING to be a method with
  the same argument count (a PROPERTY named like the sibling would turn a
  call into a value read).
- **port** `Task.Run(() => c.Wait())`-style: the one fix inside a lambda is
  the one that DELETES the lambda (F#: `Task.Run(fun () -> c |> Async.RunSynchronously)`
  → `Async.StartAsTask`). C#: `Task.Run(() => t.Result)` → `t` when the
  lambda is exactly the drain; the cancelled-vs-faulted difference the F#
  comment records (a cancelled computation gives a Cancelled task, not a
  Faulted one) applies to C# too and is the more correct of the two.
- **port** The replacement is spelled as an infix pipe / an `await`
  expression, which is atomic only in a bind's RHS or a statement: as the
  receiver of `.ConfigureAwait(false)`, an argument or under any other
  parent it takes parentheses (`(await t).Length`).
- **port** A `let x: T = <blocking>` with an annotation is not a shape to
  gamble on (F#: `let! x : T =`); C#: `T x = await t;` is fine — *n/a*.
- **port** The last statement of a block cannot be `let! _ =` (F#); C#:
  `await t;` as the last statement is fine — *n/a*. But a value-returning
  test's final blocking VALUE (NUnit's `ExpectedResult`) has no bind shape:
  the rewrite stops (CR0045).
- **port** `ValueTask`: a struct with no upcast to `Task` — `.AsTask()` is
  the spelling where a `Task` is expected (a `Func<Task>` delegate).
- **port** Only the INNERMOST enclosing async body attributes a site.
- **port** `Assert.Throws<AggregateException>(() => t.Wait())` asserts the
  WRAPPER throws; awaited, the inner exception escapes and the assertion
  fails — stays as written.

### CR0041 ← FR0049 taskify (Taskify.fs)

- **port** STRICTLY file-private in the editor (every use is in this file,
  so `FindReferences` is authoritative); `internal` widens to project-wide
  callers under `--api-changes` only; `InternalsVisibleTo` breaks
  "internal ⇒ every caller is in this project" and vetoes; a script or
  linked file compiled elsewhere vetoes; public stays out.
- **port** Every blocking site in the body is either the RHS of a simple
  `var x = <blocking>` (→ `var x = await …`) or a TAIL terminal (→
  `return await …`); every other tail terminal is `return`-prefixed (C#
  already has explicit returns — *n/a*); a site inside a lambda, `try`,
  nested async or any other shape vetoes; a parenthesised tail is one
  value; statement-shaped tails (`while`, an assignment) cannot take a
  return prefix (*n/a* in C#).
- **port** Every CALLER must be a full application forming the RHS of a
  simple `let` or a `return` payload, inside an async body, outside lambdas,
  nested CEs and no-bind zones; one unconvertible caller vetoes everything.
- **port** No return-type annotation (F#: needs a `Task<_>` rewrite; C#:
  the return type must be rewritten `T` → `Task<T>`, `void` → `Task` — a
  fix the design must spell out), not `inline` (*n/a*), no mutual
  recursion, no self-recursion (F#); C#: a self-recursive method can
  become async, but the recursive call site must be awaited — keep the
  veto for v1.
- **port** A thread-choreographed body vetoes; a `Span`/byref-like value in
  the body cannot become a state-machine field (C#: `ref struct` locals
  cannot cross an `await` — the compiler errors, so the speculative check
  catches it; keep the explicit veto).
- **port** No two edits may share a position (the `task {` line and the
  first bind).

### CR0042 ← FR0119 (AwaitableOverload.fs)

- **port** NEVER `Dispose` → `DisposeAsync`: a `ValueTask` twin with nothing
  to await behind it (fantomas's `File.Create(f).Dispose()` was rewritten to
  `do! File.Create(f).DisposeAsync()`).
- **port** Statement position takes `do!` only when the twin returns the
  NON-generic Task; C#: `await x.FlushAsync();` is fine either way — *n/a*,
  but a twin returning `Task<T>` in statement position discards a value
  the sync call also discarded — fine.
- **port** The twin's return must WRAP the original's, compared
  STRUCTURALLY (`T` → `Task<T>`/`ValueTask<T>`, `void` → `Task`/`ValueTask`),
  never by formatted name.
- **port** Control flow whose branches are statement positions of the same
  body (a `switch` arm, an `if` branch, a `try` body, a loop body) qualify;
  a `catch` handler and `finally` do not.
- **port** The juxtaposed-argument spelling keeps its argument shape (F#);
  C#: only the name gains its suffix and `await` is prefixed.

### CR0043 (no F# twin) — nothing to audit.

### CR0044 ← FR0149 / FR0017 (AsyncIgnore.fs)

- **port** (FR0017 half, `ValueTask` discarded) The head must be FULLY
  applied — a partial application ignores a function, a different mistake;
  a function-typed PARAMETER reports zero parameter groups and is not
  trusted. C#: a method group discarded (`_ = Foo;`) is a compile error —
  *n/a*; `xs.ForEach(Foo)` with a `ValueTask`-returning `Foo` IS the shape.
- **port** (FR0149 half) Handled means: the started body IS a `try/catch`
  covering everything it does (not a fragment), or `Async.Catch` appears
  AND a match consumes both `Choice1Of2` and `Choice2Of2` (producing the
  Choice is not handling it). C#: `Task.Run(async () => { try { … } catch { … } })`
  covering the whole body; a `.ContinueWith(…, OnlyOnFaulted)`; a stored
  task; an explicit discard.
- **port** Only a body this file can SEE is judged: an inline lambda, or a
  one-hop binding in the same file (`var listener = …; Task.Run(listener)`);
  a computation built elsewhere stays quiet.
- **port** A `try/catch` around the START catches nothing of the work and
  reads as protection — the note says so explicitly; when that `try`
  wraps the start and NOTHING else, the handler was written for this work
  and the EDITOR offers moving it inside verbatim (every clause, filters
  included); a token argument would be lost by the move, so only the
  single-argument start gets it; a rethrow inside the moved handler is a
  different rethrow (F#: `reraise ()` is FS0413 inside a closure; C#:
  `throw;` inside the lambda's catch is fine — *n/a*); the rewrite is
  rebuilt from body and handler alone, so a comment elsewhere in the `try`
  holds it (comment guard).
- **port** The body LOOPS: a handler around the whole computation ends the
  loop on the first failure, inside the loop it keeps polling — the note
  names that choice.
- **port** The starters differ in C#: `Task.Run(...)` discarded,
  `ThreadPool.QueueUserWorkItem`, an `async void` lambda handed to a
  `void` delegate (CR0053), and — the one the F# side measures — an
  unobserved faulted task does NOT kill a .NET Core process (it did on .NET
  Framework 4.0 only); the note's weight is "silently lost", not
  "process dies". CR0044's message must say that.

### CR0045 ← FR0142 (TestReturnsTask.fs)

- **port** SHARED STATE vetoes the conversion: a test that reads or writes
  state outliving it — a `static` mutable of another class or a module, a
  process-global setter (`Environment.CurrentDirectory`,
  `Environment.SetEnvironmentVariable`, `Thread.CurrentThread.CurrentCulture`),
  a settable static property of the library under test (resolved through
  the typed tree; an unresolvable assignment target counts as shared) —
  keeps its blocking shape, since an awaited test may overlap another. A
  `static` of the test's OWN class is fine: xUnit, NUnit and MSTest run one
  class's tests one after another (CarmelNet writes a payment id in one
  test and reads it in the next) — UNLESS the file opts into method-level
  parallelism (`[Parallelizable(ParallelScope.All|Children)]`,
  `[Parallelize(Scope = ExecutionScope.MethodLevel)]`, read off the text at
  assembly, class or method).
- **port** A file that installs global state by REFLECTION (`BindingFlags`
  in the file: a mock harness swapping a library's private static holders)
  converts NO test in it.
- **port** `obj.Property = v` on a local the test just built is not shared
  state (flagging every `sb.Capacity = 10` costs more than it saves).
- **port** The awaiting FRAMEWORKS are proven by the attribute's declaring
  assembly (xUnit, NUnit, MSTest, TUnit); a same-named home-grown attribute
  with a reflection runner would get a Task nobody awaits.
- **port** The body's FINAL blocking result: `do!` when the test returns
  unit; a final VALUE of a non-unit test is its result (NUnit's
  `ExpectedResult`) — no bind shape carries it, the rewrite stops. A
  `let mutable x = <blocking>` has no `let! mutable` form (C#: `var x = await`
  is assignable — *n/a*).
- **port** A prefix in front of a multi-line expression moves its first
  line; continuation lines (a pipe opening a line) must follow (F# offside;
  C# *n/a*).
- **port** The body is not already async; no return-type annotation
  (C#: `void` → `async Task`, `T` → `async Task<T>`; a `Task`-returning
  non-async test that blocks inside is also a shape).

### CR0046 (no F# twin) — nothing to audit.

### CR0047 ← FR0046 (WeakLock.fs)

- **port** `lock (Console.Out)`/`stdout`/`stderr`/`stdin`: a process-wide
  singleton from another module gets the note alone — its other lockers
  are not here to fix; the lock-object fix is offered only when the locked
  thing belongs to this file by nature (a literal, a type object, `this`,
  a value this file defines).
- **port** The lock object lands after the locked value's own declaration
  at its indentation when this file defines it, else before the enclosing
  member; a member of a type has no module-level slot in F# (C#: a
  `private static readonly object` field of the type, or an instance field
  when the lock guards instance state).
- **port** `lock x <| …` and `lock x (fun () -> …)` both; C#: only the
  `lock` statement — but `Monitor.Enter(this)` is the same shape and
  CR0048's business.

### CR0048 ← FR0123 (MonitorLock.fs)

- **port** The body wraps in a LAMBDA in F#, so computation binds
  (`do!`/`let!`) and a local mutable declared outside stop compiling
  (C#: the `lock` statement is a block, and `await` inside `lock` is a
  compile error — an `await` in the body vetoes; a `yield` too).
- **port** The `try`/`finally` may be the FIRST statement after the `Enter`
  with more following (FCS's `InlineDelayInit`), not only the rest of the
  block.
- **port** The `finally` holds NOTHING but the `Exit`; own-line statements;
  a directive in the region vetoes.
- **port** A bare `Enter` with no guarding `try` is the leak note; the
  `(x, ref taken)` overload carries protocol and is never rewritten.

### CR0049 ← FR0154 (DictTryGet.fs)

- **port** The `out`-parameter spelling `TryGetValue(key, out var v)` is the
  C# shape (F#'s tuple return is the match); the miss arm is `!d.TryGetValue(k, out var v) { v = …; d[k] = v; }`
  or the `if (d.TryGetValue(k, out var v)) return v; … d[k] = …` split —
  both shapes in the design, plus the `TryAdd(k, v)` (discarded) and
  `AddOrUpdate(k, v, (_, _) => v)` stores.
- **port** A `Lazy<T>` value type is refused (CR0050's subject behind
  `GetOrAdd`), a delegate value type is refused (the lambda argument would
  be ambiguous with the plain-value overload).
- **port** The key is a pure atom, or a TUPLE of atoms spelled the same in
  lookup and store; the hit arm does nothing but return the binder; the
  miss arm's several `let`s become the lambda's body with the store line
  dropped; the store shares its line with nothing.
- **port** A miss arm reading a mutable local or byref of the enclosing
  scope (FS0407), holding a `Span` or taking an address, or containing
  `reraise`, stands down (C#: a lambda capturing a `ref struct` or an
  `out`/`ref` parameter is a compile error — keep the veto; `throw;`
  inside the lambda is *n/a*).
- **port** The factory hint: a factory that CALLS something (`Compute()`,
  `Load(key)`; not `key * 2`) carries the `Lazy` hint; a `Task`/`ValueTask`
  value type gets the note alone (the value type would change).
- **port** (FR0014, deferred to CA1854 in C#) Map's `TryFind` option idiom;
  the elif-chain peel (one level per pass) — *n/a*.
- **port** (FR0018, deferred to CA1864) `TryAdd` evaluates the value ALWAYS
  where the original evaluated it only on absence: the value must be a
  pure atom — worth knowing when the CA rule is off and CR0049's sibling
  shape is wanted.

### CR0050 ← FR0152 (CachedFailure.fs)

- **port** Gated on `GetOrAdd`'s RETURN type (the dictionary's value type)
  — no receiver-generics reconstruction; `Task`, `ValueTask`, `Lazy`; a
  factory that throws is not reported. Matches the design.

### CR0051 ← FR0150 (UseBinding.fs — read under family D).

### CR0052 ← FR0027 (ClosureCapture.fs)

- **port** The capture is IMPLICIT too: a lambda referencing an instance
  field or property compiles to an access through `this`; a METHOD GROUP
  (`src.Changed += this.OnChanged`) and one wrapped in a delegate
  constructor (`new FileSystemEventHandler(this.OnCreated)`) pin `this`
  just as hard.
- **port** A publisher the object OWNS — its own event, or one held in its
  own field (`x.Disposing += … x …`) — cannot outlive the object: a cycle
  inside one lifetime, not a leak; a local `Event<_>`/`new` event source in
  the same scope neither.
- **port** Lambda parameters SHADOWING the captured name suppress the note.
- **port** Sinks are typed: `+=` on an `event`, `Subscribe` on
  `IObservable<T>`, `AddHandler`; a `List<T>.Add` of a delegate is not a sink.

### CR0053 (no F# twin) — nothing to audit.

### CR0054 ← FR0079 (SingleAwaitable.fs)

- **port** Only LITERAL one-element collections match (`new[] { t }`,
  `[t]`); a variable holding one task is out of scope.
- **port** `Task.WaitAll(new[] { t })` is a blocking unit statement: its
  element alone would DROP the wait, so the editor's fix keeps one
  (`t.Wait()`); the awaiting combinators unwrap to the element itself.
- **port** The editor's fix changes the result type (`Task<T[]>` →
  `Task<T>`): editor-only, never the sweep.

### CR0055 ← FR0118 (CancellationOverload.fs)

- **port** `Task.Run` and `Task.Factory.StartNew` take the token as a
  SCHEDULING condition: with an already-cancelled token the delegate
  never runs and its side effects silently never happen (suave binds a
  listening socket inside one) — never injected there, and an explicit
  `CancellationToken.None` on them is the author choosing to always start
  the work.
- **port** The token may already BE one of the arguments as PAYLOAD
  (`CreateLinkedTokenSource(ct)`); a params/two-token sibling overload
  would happily compile `(ct, ct)` — no injection where the token is
  already passed.
- **port** A NAMED argument in the call (`cancellationToken: ct`) makes
  positional counting meaningless — stand down (C#: the token can be
  appended by name instead: `, cancellationToken: ct`).
- **port** A trailing lambda/`switch`/conditional argument runs to the
  closing parenthesis in F# (`, ct` joined its body as a tuple); C#
  argument lists are unambiguous — *n/a*.
- **port** A stored `None` binding's RHS is never rewritten (intent this
  scan cannot see) — only an argument position.
- **port** Handlers and `finally` blocks are cleanup: `tx.RollbackAsync()`
  after a cancelled `CommitAsync(ct)` must not throw
  `OperationCanceledException` on the way out — no token injected there.

## Family D — resources and exceptions

### CR0060 ← FR0075 (UseBinding.fs, 2.1k lines)

The design gives eight guards; the F# module is a three-tier ownership
model. The full model, which ports as is (C#: `using var` / `using (...)`
for the fix, `IDisposable` and `IAsyncDisposable` both):

- **port** THREE TIERS decided by where the binder's bare mentions send the
  value. FIX when the value provably stays inside the scope: every mention
  is an INVOKED member (`x.Read(...)`) or a comparison operand, never
  inside a lambda, local function, object expression, `lazy`, or a
  computation that starts after the binding; no RESULT position mentions
  it (a plain-valued call excepted); no local bound to a value reached
  THROUGH it escapes either (`var cmd = conn.CreateCommand();` — aliases
  are followed, and through those in turn: a task, a command, a reader, a
  method group still needs the binder behind it; a plain-valued local
  (`var b = stream.ReadByte()`) is evaluated and done). NOTHING when an
  escape is an OWNERSHIP TRANSFER: the scope's result (also inside a
  tuple, record, upcast, wrapper case), an argument to another
  disposable's constructor (`new StreamReader(stream)` — the wrapper
  adopts it, `leaveOpen`/`disposeHandler` default to owning), or stored
  where a holder beyond the scope keeps it (a field, a property, a
  collection insert `Add`/`TryAdd`/`Enqueue`/`Push`/`Insert`, an indexer
  set, a static, an outer ref cell); the owner is decided whatever else
  the scope did on the way. NOTE ONLY when a mention could move the value
  somewhere whose ownership is unknown, and the note NAMES the
  destination: handed to a function by name (a same-file function is read
  ONE HOP: disposing the parameter — `using`, `.Dispose()`, handing it to
  another disposable's constructor — is a transfer, keeping it is the
  leak; the parameter is located by curried index or tuple element, and a
  tuple handed to a non-tuple parameter is unreadable); stored in a
  mutable local; captured by a closure; read by a result that may outlive
  the scope.
- **port** A method-group mention that is not the function of an
  application (`xs.Select(c.Convert)`, `changed += c.Refresh`) is a method
  group handed on: it runs after the scope on a disposed receiver.
- **port** A member access in RESULT position may hand out something still
  tied to the object (`client.GetAsync(url)` as the value); a PLAIN value
  (`md5.ComputeHash(bytes)`, `reader.ReadToEnd()`) — primitives, strings,
  arrays/tuples/immutable containers of those; a `seq`/`IEnumerable` is
  NOT plain (it runs when enumerated) — is computed before the scope exits;
  only a property read or an INVOKED member counts, a method group's
  return type says nothing about when it runs.
- **port** SELF-ACTIVE objects are owned by whoever stops them, never by
  the scope that started them: a timer, a watcher, a listener, anything
  constructed with a CALLBACK, an event of it the scope subscribes to, a
  token registration, a `Start()`/`Enable…` call — `using` would stop them
  on the way out.
- **port** PENDING WORK on the receiver: a `Task`/`ValueTask`/`Async`-returning
  member called and its result DROPPED (discarded, a bare statement of
  task type, `Async.Start`) or handed on as an ARGUMENT to anything
  (`tasks.Add(client.GetStringAsync(u))`, a constructor, an unreadable
  function; the aliased `var t = client.GetStringAsync(u); tasks.Add(t)` the
  same) is in flight past the scope; an awaited, bound, `.Wait()`ed or
  `.Result`ed one finished before the scope ends.
- **port** A `CancellationTokenSource` binder: the scope KEEPS the token
  only when `.Token` (or the source, or a local alias) goes to an argument
  of a BCL operation whose pending result is awaited on the statement
  spine (`Task.Delay(ms, ct)`, `GetAsync(url, ct)` awaited), a synchronous
  blocking wait (`Wait`, `WaitOne`, `Join`, `Sleep`, `WaitAll`), or a local
  alias followed in turn; EVERY other position hands it on — a user
  function (opaque), a starter (`Task.Run(work, ct)`), an unawaited pending
  call, a synchronous BCL call that may store it (`tokens.Add(ct)`,
  `CreateLinkedTokenSource(ct)` registers on it), a constructor argument
  (`new Worker(cts.Token)`: the worker's timer dies with the source), a
  field, a property set, a tuple/option built from it, a return.
- **port** WRAPPERS over a foreign resource are not the scope's to dispose:
  a `StreamReader`/`BinaryWriter`/`HttpClient` over a resource the scope did
  not create (a parameter, a field, a captured local, a property read),
  or over a local that itself ESCAPES the scope (Giraffe's tests: a
  `MemoryStream` wrapped in a `StreamWriter`, stored in `ctx.Request.Body`,
  read by a returned task — `using` on the writer closed the request
  body). Only a resource the SAME scope constructed and keeps is the
  scope's own; a construction outside a lambda is shared by every call of
  it and counts as foreign inside it.
- **port** Manual management exempts: `x.Dispose()`, `x.Close()` where
  `Close` IS `Dispose` (streams, writers, readers, sockets, `MemoryStream`s
  the compiler writes into), `((IDisposable)x).Dispose()` (the upcast hides
  the receiver).
- **port** `File.OpenRead(path)`, `MD5.Create()`, `SHA256.Create()`,
  `Aes.Create()`, `RandomNumberGenerator.Create()` and the other BCL
  FACTORIES whose result the caller owns count as constructions; a cheap
  PascalCase prefilter runs before symbol resolution (resolving every
  lowercase `var x = Load(y)` put the rule at the top of the slow list).
- **port** SCOPE CONTEXT changes the weight: under the entry point (`Main`,
  top-level statements) a leaked FLUSH-SENSITIVE type (a buffered writer or
  stream, a transaction) is lost work — .NET runs no finalizers at exit —
  and only such types are reported there (a handle the OS reclaims is no
  loss); in an ASP.NET action (`[HttpGet]`…, a `ControllerBase`/`Hub`
  base) the leak is per request and carries warning weight.
- **port** Inside a computation whose builder has no `Using` the `use`
  cannot bind (FS0708) — *n/a* in C#, but an iterator (`yield`) or `async`
  method with a `using` is fine; a `using` declaration inside a `switch`
  section needs braces — the fix must add them.
- **port** `using var` needs C# 8 (design has it); the capability gate is
  the language version, not the framework.

### CR0051 ← FR0150 (UseBinding.fs)

- **port** The computation must be the scope's RESULT (returned directly,
  or through a single binding of it), it must MENTION the binder, and
  nothing between the `using` and the computation may touch the binder —
  otherwise the note stands without the fix. G-Research's analyzer flags
  the shape whether or not the workflow reads the disposable; this one
  requires the mention. C#: the design's fix (make the method `async` and
  `await` the returned task) differs from the F# move-inside fix; both
  are legitimate — the F# one keeps the signature and is the editor-only
  offer, the C# `async` one changes the exception timing and must say so.

### CR0061 ← FR0032 (ObjectDesign.fs)

- **port** A disposable built WITH the object itself (`new GraphicsDeviceManager(this)`)
  registers with it: the base or the framework owns and disposes it.
- **port** A DISPOSABLE BASE class makes an added `IDisposable` a duplicate:
  the note then talks about overriding the base's `Dispose(bool)` (Kasino's
  MonoGame `Game` subclass); an unresolved base is not worth the guess.
- **port** A member that calls `field.Dispose()` itself — a
  `Close`/`Unsubscribe`/ref-count protocol — is manual management, not an
  ownerless resource.
- **port** The editor's fix appends the plain `IDisposable` implementation
  disposing every created field — deliberately no `Dispose(bool)`, no
  finalizer, no `GC.SuppressFinalize` (a type holding managed disposables
  needs none of that; CA1063's pattern is for unmanaged handles). One fix
  per type, carried by its first field.
- **port** A `Dispose` that delegates to `DisposeAsync` disposes through the
  async body; the interface `Dispose` is followed one hop into
  `this.Dispose()`/`Dispose(bool)` or a let-bound `dispose()`.
- **port** `MemoryStream` with a CAPACITY owns its buffer (and is not
  exempt); one over the caller's buffer is.

### CR0062 ← FR0047 (ObjectDesign.fs)

- **port** RELEASE vs TOUCH: `cts.Cancel()` mentions the field and frees
  nothing; releasing means `Dispose`/`DisposeAsync`/`Close`, directly or
  through an upcast. A field passed AS AN ARGUMENT leaves the body's sight
  (`cleanup(cts)`, `owned.Add(cts)`) and whatever received it may release
  it; being the RECEIVER is different.
- **port** The editor's fix puts `field.Dispose();` FIRST in the `Dispose`
  body — unless the body still USES the field (`cts.Dispose(); cts.Cancel()`
  throws `ObjectDisposedException` and compiles), then LAST, and only after
  a body whose closing statement is a plain call or assignment.
- **port** A `System.Reactive`/`Rx` file: `Dispose` that unsubscribes rather
  than releases is the design.

### CR0063 ← FR0148 (ObjectDesign.fs)

- **port** Implemented "through a base type" too (the entity's own
  interface list, typed). Matches the design.

### CR0064 ← FR0055 (SwallowedException.fs)

- **port** A COMMENT on the handler is the author acknowledging the swallow
  (`catch { } // best-effort icon`) — a decision, not an accident, and not
  worth a note. (The F# side's comment-based acknowledgement; keep it — it
  is not a suppression comment, it is the author's own prose.)
- **port** A `when` filter that never looks at the exception (`catch when (watch)`)
  still swallows every one; a filter on the exception itself is a
  decision.
- **port** Value fallbacks: a VARIABLE (`catch { return path; }` returns the
  input as if the work succeeded), a tuple or record carrying a default in
  one slot, a `MaxValue` sentinel; a union case that CARRIES the failure
  (`Error "x"`, `Failure`, a user `ParseError`) — C#: returning a
  `Result`/`OneOf` error, an `Exception` object, a `bool false` from a
  `Try…` probe — is quiet.
- **port** The `bool` probe idiom is checked against the try BODY: the
  body must answer with the OPPOSITE literal (`try { ping(); return true; } catch { return false; }`);
  `try { return Parse(s); } catch { return false; }` disguises the failure.
- **port** The GUARD offer: a body of pure arithmetic over names and
  literals with exactly one non-literal INTEGER or DECIMAL divisor — wait:
  decimal `+`/`*`/`/` throw `OverflowException`, so the catch guarded more
  than the division and no zero check can replace it — INTEGER only (the
  design says "integer or decimal": correct it); float division never
  throws; a dotted operand is only as pure as the typed tree proves
  (`opt.Value`, `lazy.Value`, `s.Length` are getters and throw); under
  `checked` context every `+`/`-`/`*` throws and no guard is offered; the
  zero is spelled in the divisor's own type (`0`, `0L`, `(byte)0`).
- **port** The IO-only narrowing needs the typed tree to agree that every
  CALL on the line is `System.IO`'s: the text smell alone narrowed
  `Some(Path.GetFileName d, Checkpoint.loadMetadata p)` and a JSON exception
  escaped (Fuuga); assembly-loading and font-lookup probes were put back
  by hand.
- **port** The LOG LINE offer needs a logger REACHABLE from the catch: a
  parameter of the enclosing method, a field, a local whose scope holds
  the site — never one from another method (a `logger` from one function
  was written into six that had none); the exception name is the
  handler's binder or a fresh `ex` (unless the enclosing declaration
  already binds `ex`); the logger itself is not a parameter worth logging.
- **port** Teardown idiom: one `Dispose`/`Close`/`Shutdown`/`Cancel`/`Complete`/
  `Delete`/`Reset` call, optionally followed by `x = null` — a lower note.
- **port** Probe APIs that answer for a missing path instead of throwing
  (`File.Exists`, `Directory.Exists`, `File.GetLastWriteTime`) — the advice
  is to delete the try.
- **port** A catch-all after an earlier arm that RETHROWS cancellation, or
  followed by an unconditional failure (`try { Environment.Exit(n); } catch { } throw …;`),
  is a decision.
- **port** The catch-all patterns are `catch`, `catch (Exception)`, a bare
  binder; `catch (SystemException)` is not a catch-all.

### CR0065 (no F# twin) — nothing to audit.

### CR0066 ← FR0063 and CR0068 ← FR0064 (ExceptionRules.fs)

- **port** (CR0066) Raises the `finally` itself catches (a nested try inside
  it) stay quiet.
- **port** (CR0068) The fault-injection dispatch table: a `switch` whose
  sibling arms raise three or more DISTINCT exception types (FCS's
  `SimulateException`) stays quiet — the design has it; add
  `AccessViolationException`, `ExecutionEngineException` to the list, and
  `throw new Exception(…)` itself is CA2201's; the F# rule does not report
  plain `Exception` (the design's CR0068 does — decide: CA2201 covers it,
  so CR0068 yields there).

### CR0067 ← FR0054 (ObjectRules.fs)

- **port** The members are `Equals`, `GetHashCode`, `ToString`, `Dispose`
  (and, for C#, a static constructor, `Finalize`, an implicit conversion,
  `==`/`!=` — the CA1065 list); raises inside the member's own `try` stay
  quiet; `throw` through `raise <| X()` / `X() |> raise` spellings — C#:
  a `throw` expression (`?? throw`) inside these members counts too.

### CR0069 ← FR0092 (FailwithContext.fs)

- **port** The parameter's TYPE must print usefully: primitives, strings,
  enums, records/tuples of those, options and lists of them; a class, an
  array, a function, a generic parameter, a compiler-tree node prints its
  type name — a method with no such parameter gets NO note at all.
- **port** The message mentions a parameter as a WORD (`x` inside "no text
  property" is not a mention); the message is not the method's own name
  (fslex's fallthrough); not an invariant ("unreachable", "not possible",
  "NYI", "internal error", "invalid case"); no `{`, `}` (or `%` in F#) in
  the literal; no verbatim/raw strings; 1–4 parameters; a secret-smelling
  name on the method, its type, its namespace or a parameter (auth,
  session, crypt, token, password, secret, credential) — Suave threw on
  freshly decrypted session data.
- **port** The rewrite puts argument VALUES into the message: the hint
  says so (a PII decision on personal data).
- **port** `let f = function … | _ -> failwith` — the wildcard arm is named
  to be quoted; C#: a `switch` expression's `_ => throw …` inside a method
  quotes the method's parameters — *n/a*.
- **port** Never in a test file (the runner names the test and inputs).

### CR0070 ← FR0151 (ExceptionDetail.fs)

- **port** A NESTED handler inside the clause may bind the same name to a
  different exception: reads inside it are never fixed (a fix that does
  not compile).
- **port** A `.Message` read inside an INTERPOLATED string hole is reported
  but never fixed (F#: the replacement's quotes cannot sit in a hole;
  C#: `$"{string.Join("; ", …)}"` is legal since C# 11 raw/nested quotes —
  still keep it note-only inside a hole for C# 10 and below).
- **port** The chain must STOP at `.Message`: `e.Message.Length` covers the
  whole chain and replacing it would drop `.Length`.
- **port** The rethrow the carry-on replaces must be `throw;` or
  `throw e;` — NOT `throw new Wrap("…", e)`: a deliberate wrap changes the
  thrown type; and only in a TAIL position of the handler (a `throw`
  inside a unit `if` followed by more statements is not a value position).
- **port** The tested type is resolved by ENTITY, so a user type of the
  same short name never matches; reading the informative member in the
  clause's `when` filter counts as informed.
- **port** The carrier table: `ReflectionTypeLoadException` →
  `LoaderExceptions`/`Types`, `WebException` → `Response`; the design adds
  `AggregateException` → `InnerExceptions`/`Flatten()` and `SqlException` →
  `Errors`/`Number` (F# lists them as future slots "once each has a real
  site to verify against") — mark those two as unverified in C# too, plus
  `FileNotFoundException` → `FusionLog`.

## Family E — types and immutability

### The scope gate ← Visibility.fs (all shape-changing rules)

- **port** Beside a SIGNATURE-like contract only a private declaration may
  change shape (F#: a `.fsi`; C#: a PUBLIC API baseline —
  `PublicAPI.Shipped.txt` of the PublicApiAnalyzers, or a reference
  assembly project — is the C# analogue; when one is present, an
  `internal`/`public` shape change stands down).
- **port** "Nothing outside the assembly can see it" is true of an
  executable, but a LATER FILE of the same executable can, and consumes
  the declaration at its current shape: a rule whose in-place rewrite
  changes a binding's TYPE (CR0023's set conversion) must ask the other
  files too. C# has no file order, so the question is "any other file of
  the compilation", answered by `FindReferences` — the same all-or-nothing
  edit set.
- **port** A script (F#) is the ultimate leaf; C#: top-level statements
  make the compilation an Exe — same answer.
- **port** `--api-changes` is held back, flag or no flag, when a consumer
  of another language references the compilation and no verification of
  this run can build it (`PublicSurfaceHeld`).

### CR0080 (no F# twin) — nothing to audit beyond the gate.

### CR0081 ← FR0070 / FR0016 (StructHints.fs, StructDu.fs)

- **port** A record whose fields are READ inside an expression tree
  (`Expression<Func<…>>` lambdas, query expressions over `IQueryable`)
  stays a class: a struct local captured in a quotation cannot have a
  field read (that takes its address, which a quotation may not do —
  Linq.Expression.Optimizer's `query { … sl.x = j.x … }`). C#: an
  expression tree CAN read a struct's field, but a `readonly record struct`
  captured in a closure inside an expression tree copies — behaviour
  change on mutation only, and the record is immutable; keep the veto
  for `IQueryable` lambdas since providers translate struct members
  differently (EF Core does not support struct entities).
- **port** (FR0016) 2–3 cases, every payload a whitelisted small immutable
  value type spelled as a PLAIN identifier — no strings (reference type is
  legal in a struct union but signals a bigger payload), no generics, no
  options, no recursion; when more than one case carries fields all fields
  must be NAMED and same-named fields across cases must agree on TYPE
  (FS3585). C#: no unions yet — *n/a* until C# unions land, then port.
- **port** The attribute goes below any XML doc, so it sits against the
  type it marks (C#: `readonly record struct` is a keyword change, but a
  `[StructLayout]`-style attribute insert would follow the same rule).
- **port** Only the TYPE's own visibility counts (a private representation
  still leaves a public class-vs-struct type visible).

### CR0082 ← FR0093 (StructHints.fs, StructTupleMigration.fs)

- **port** ALL-OR-NOTHING over the typed uses: every construction assigns
  a LITERAL tuple (an arbitrary expression of tuple type would change type
  under it), every read destructures into a literal tuple pattern or
  compares against a literal tuple; `Item1`/a binder/the field passed
  along whole starts dataflow the scan does not follow and keeps the
  field a note; a field with ZERO uses stays a note (nothing proves the
  shape); a `#load`ing script is a call site the project cannot see
  (C#: a linked file compiled into another project — the design's linked
  file rule).
- **port** Capped at four elements (a struct tuple is copied by value);
  already-struct tuples and unit-of-measure segments are left alone.

### CR0083 (no F# twin), CR0084 ← FR0062 (MiscRules.fs)

- **port** (CR0084) The accessibility of `let mutable private x` parses onto
  the pattern, not the binding (F# quirk — *n/a*); the uppercase
  no-argument binder shape counts too (*n/a*); assigned at most once in
  this file and never from itself → quiet (the design has it).

### CR0085 ← FR0036 (TypeChecks.fs)

- **port** `x.GetType().FullName == "…"` is the same shape as `.Name`.
- **port** Quiet inside a `when` guard of a `:? T as x` clause (C#: a
  `case T x when x.GetType() == typeof(T)` narrows to exactly T on purpose
  — FCS retries a locked file only on a plain `IOException`); the guard
  may sit deeper than the top-level comparison.

### CR0086 ← FR0020 (ObjectRules.fs)

- **port** Every `this.<slot>` reference inside a CONSTRUCTOR-TIME
  expression counts — assignment right-hand sides, loops, try blocks,
  field initialisers (C#: field initialisers run before the base
  constructor; a virtual call in one is the same hazard).
- **port** Scoped to the members declared in the SAME type definition
  (shadowing-proof, single-file); C#: partial classes span files —
  `FindReferences` on the type's members instead.

### CR0087 (no F# twin) — nothing to audit.

### CR0088 ← FR0157 (StringUnion.fs, 2.9k lines) — v2; the design's
summary of the flow proof is faithful. Extra conditions worth carrying:
- **port** A `Result<_, string>`'s `Error "…"` exits are sources and its
  `Error "…"` arms the consumer; the `Ok` side is not followed (C#: a
  `OneOf`/`Result` error string, or an `out string error`).
- **port** A variable pattern spelling a module-level constant's name
  beside later arms (`| us -> 2` under `let us = "…"`) is treated as the
  comparison the author meant; C#: `case var us:` under a `const string us`
  — same trap, same reading.
- **port** The union's name gets `Kind` appended where the project already
  has the type; two unions in one file never share a name (a second choice
  carries the record's name first).
- **port** A union added by the rule carries the WIDEST visibility of the
  slots it types — never public unless a public slot is among them.
- **port** The serializer guard reads the VALUE handed to a reflective
  head, not only a type argument: a record (or a collection of one)
  passed to `JsonSerializer.Serialize`, behind a field or property, or
  anywhere along a curried call.
- **port** A sentence of more than four spaces keeps its words behind
  double backticks in F#; C#: an enum member cannot be a sentence — a
  literal that makes no identifier (spaces, punctuation, a leading digit)
  stands the rule down or gets a `[Description]`-style attribute (a
  design decision to make at v2).
- **port** The `ToString` override returns the original text so logs read
  as before; C#: an enum cannot override `ToString` — the design's
  `ToText()` extension, and every print site (`$"{x}"`, `x.ToString()`,
  `string.Concat`) must call it explicitly, which is a larger edit than
  the F# one.

### CR0089 ← FR0134 (DateTimeOffsetMigration.fs)

- **port** The `DateTime` prefix must REALLY be `System.DateTime`: a
  shadowing fake-clock type would take the rewrite, compile, and switch
  to the real clock; the field is confirmed by SYMBOL, not name (a
  same-named field on another type must not vouch for it).
- **port** At least one real write pins the clock; `Now` and `UtcNow` must
  not mix across the WHOLE migration; `.Date` escapes (returns
  `DateTime`), `ToString` formats differently — bail.
- **port** File-private types only in this first cut.

### CR0090 ← FR0136 (EmptyGuid.fs) — implemented; matches.

## Family F — strings, culture, time

### CR0100 ← FR0031 (StringConcat.fs)

- **port** At least THREE operands — a two-term `path + ".bak"` reads fine
  as it is — and at least one literal AND one non-literal.
- **port** Cheap syntactic pre-gates run before symbol resolution.
- **port** An UNANNOTATED parameter the chain alone typed as a string (F#
  inference): *n/a* in C#.
- **port** String `+` translates in query expressions; `string.Concat`/
  interpolation may not: stand down inside an expression tree.
- **port** The editor's alternative is the explicit `string.Concat` call;
  the interpolation is the primary because it reads better and compiles
  to the same `Concat` at the rule's hole cap.

### CR0101 ← FR0042 (SprintfInterpolation.fs)

- **port** An even run of `%` before a specifier means the leading one is
  escaped (F# `%%`); C#: `{{`/`}}` in a `string.Format` template stay
  doubled in the interpolation — same escape, *n/a* for `%`.
- **port** Parentheses that only WRAPPED the call go with it (`(sprintf …)`
  became `($"…")`), but stay where they may be doing more — a method's
  argument list, a receiver, an indexer; and an operator touching the
  paren would swallow the `$` (`~~$"…"` is an operator name in F# —
  *n/a*, but `x+$"…"` reads fine in C#).
- **port** One SIMPLE argument per specifier: an identifier, a dotted path,
  a non-string constant — an argument holding braces or nested quotes
  stays.

### CR0102 ← FR0021 (InterpToString.fs)

- **port** A fill under a TYPED hole (`%s{x.ToString()}`) is left alone —
  the specifier pins the type; C#: a hole with a format (`{x.ToString():N2}`)
  is already excluded by the design; a hole whose type is `object` and
  whose `ToString()` is the only conversion the target-typed handler would
  not do differently — *n/a*.

### CR0103 ← FR0086 (RedundantSyntax.fs) — implemented; extra:
- **port** A `FormattableString`/`IFormattable` EXPECTED at the site keeps
  the `$`: a type annotation, or a method parameter of that type (Fable's
  `let s: FormattableString = $"…"` lost its `$` and stopped compiling;
  Ionide's `Log.setMessageI $"…"`). With the typed tree the callee's
  signature answers; WITHOUT it any application argument keeps its `$`.
  C#: `FormattableString`, `IFormattable`, and a custom
  `InterpolatedStringHandler` parameter (`ILogger`'s, `Debug.Assert`'s
  handlers!) — an interpolated string handed to one is NOT a plain string
  conversion; the speculative check catches the conversion error, and the
  typed rule must check the target parameter type. **This is a live
  defect in the shipped CR0103**: it is parse-only and never checks the
  target type.
- **port** A `%%` in the text (F#) — *n/a*; `$$"""…"""` raw with no hole
  loses every `$` (F#); C#: a raw interpolated string `$"""…"""` with no
  hole is skipped by the shipped rule — fine, but `$$"""…"""` (C# 11)
  should be treated the same.

### CR0104 ← FR0138 (StringEmptiness.fs)

- **port** Operand ORDER decides exactness: `isNull x || x = ""` (null
  check LEADS) is exact; `x = "" || isNull x` is exact only while the
  leading test cannot throw — `x.Trim() == "" || x == null` throws on null
  in the original, so the Trim spellings in that order ride editor-only.
- **port** `x.Trim().Length == 0` is a Trim spelling too;
  `string.IsNullOrEmpty(x.Trim())` → `IsNullOrWhiteSpace(x)` (editor).
- **port** A backticked/escaped identifier subject is respelled from its
  source text (C#: `@name` keeps its `@`).
- **port** Inside a query expression/expression tree the whole rule stands
  down (the translator may know `||` and `==` but not `IsNullOrEmpty`).

### CR0105 ← FR0067 (MiscRules.fs)

- **port** Inside an expression tree the WHOLE suggestion stands down, note
  included: a LINQ provider resolves `Parse` by signature and the
  two-argument overload can turn a translatable call into a runtime
  `NotSupportedException` that compiles clean.
- **port** The short `CultureInfo.X` spelling under an existing `using
  System.Globalization`, fully qualified otherwise; a juxtaposed `Parse s`
  gains parentheses (*n/a*).

### CR0106 ← FR0121 (DateTimeRules.fs)

- **port** `dt.Date != DateTime.Today` — a same-day test against a date
  THIS MACHINE produced — is the same clock on both sides, not the
  calendar-cut bug.
- **port** An expression-tree TRANSLATOR reproduces the clock on purpose:
  the arm that maps a member NAMED `Now`/`Today` to the call (SQLProvider's
  evaluator) is a translation table, not a clock read — the nearest
  enclosing `switch`/`if` names the member in its condition.
- **port** `DateTimeOffset.Now` stays quiet entirely (it carries its
  offset); `Now` read as an INSTANT (`.Ticks` as a version number goes
  backwards at DST fall-back, `.ToBinary`, `.ToFileTime`) gets the rewrite;
  `Now.ToString(…)` renders the local calendar and gets the note only.
- **port** Only a clock read pays for the scan of its enclosing arm
  (performance of the rule itself).

### CR0107 ← FR0122 (RegexUsage.fs) — matches; the check is construction
of the pattern with the call's own `RegexOptions` (a pattern legal under
`IgnorePatternWhitespace` may be illegal without it).

### CR0108 ← FR0015 (RegexUsage.fs)

- **port** ANY string literal spelling qualifies (`@"\d+"` is the ordinary
  way to write a regex); the literal is re-emitted VERBATIM, so a pattern
  carrying a quote, a control character or a backslash is refused for the
  string-operation rewrite (the decoded text would need re-escaping).
- **port** A `Replace` with a `$` anywhere in the replacement, an empty
  pattern, an anchored pattern, a backslash in either, or a
  `RegexOptions`/`MatchEvaluator` argument is refused (design has most;
  add backslash and the verbatim re-emission rule).
- **port** `StartsWith(string)` is CURRENT-CULTURE and differs on
  ligatures, ignorable characters and Turkish i — the `Ordinal` overload
  says what the regex did; `Contains(string)` is ordinal already.

### CR0109 ← FR0015 hoist / FR0037 (RegexUsage.fs, LoopPerf.fs)

- **port** A hoisted binding lands above the enclosing declaration's
  comment block (contiguous `//` lines at the declaration's column,
  never a comment trailing the declaration above), and under the same
  `#if` as the call; the name is derived from the pattern text and a
  second hoist deriving the same NAME collides (only the first keeps its
  fix); a bare `Regex` resolves only under a `using` that precedes the
  insertion point (a `using` further down or in a sibling scope is no help).
- **port** Only a parenthesised method call hoists (`Regex.Match(x, "p").Success`
  is the call's receiver; a juxtaposed F# spelling would hand the
  continuation to the argument — *n/a*).
- **port** A construction spanning lines carries its indentation into the
  binding: single-line only; a `let regex = azAZRegex` alias left behind is
  harmless.
- **port** The string-operation rewrite SUBSUMES the hoisting advice on the
  same site; a construction the hoist declines is FR0037's note (CR0110),
  and one it fixes silences that note.
- **port** Hoistable methods: `IsMatch`, `Match`, `Matches`, `Split`
  (`Replace` too in C#).

### CR0110 ← FR0037 (LoopPerf.fs)

- **port** The set: `ConcurrentDictionary`, `HttpClient`,
  `JsonSerializerOptions`, `Regex`, `SearchValues.Create`; a probe of the
  loop variable itself never fires; a lambda handed to a collection
  function is a loop; a `let` between the loop and the site may rebind
  per iteration.

### CR0111 ← FR0081 (PathSeparator.fs)

- **port** BOTH separators need positive evidence now (a lone backslash
  used to be path-ish on its own until FsAutoComplete's escape-sequence
  building — `result + "\\" + c` — showed where that goes wrong); the
  design says `\\` fires alone: correct it.
- **port** A separator only JOINS when the chain has text on BOTH sides:
  `dir + "/"` appends a marker and `"/" + name` prefixes a root — neither
  is a `Path.Combine`; dot-segments (`"./" + p`, `p + "../"`) are
  relative-path notation `Path.Combine` cannot spell.
- **port** A chain opening with a forward-slash literal (`"/img/" + id`) is
  as likely a web route and needs the STRONGER evidence (a rooted or
  extension-bearing literal, or one that exists on disk); a path-flavoured
  NAME is too weak there (`fileId` matches "file").
- **port** The file's own name says what it builds (`JsonRuntime.cs` joins
  JSON pointers); a name bound one hop away to something URL-shaped
  (`gitHome = "https://…" + owner`) makes the chain a URL; a chain
  compared or searched for (`"content/" + n.file == page`, `set.Contains(chain)`)
  is a key.
- **port** Contexts that must stay compile-time constants — `const`
  initialisers, attribute arguments — never get the note (`Path.Combine`
  is a call).
- **port** `Path.Combine` treats a ROOTED second argument as absolute (the
  first is discarded) — the concatenation does not; hence note-only.

### CR0112 ← FR0125 (UnicodeHygiene.fs) — matches; the escape fix only in
a REGULAR string literal (comments, identifiers, verbatim and raw strings
where escapes do not exist stay notes).

### CR0113 ← FR0105 (CheckedArithmetic.fs)

- **port** `int64` within a factor of SIXTEEN of the ceiling (ten-digit
  int64s are ids, eighteen-digit ones are magnitudes); `MaxValue + e` /
  `MinValue - e` overflow for every e but zero, while `MaxValue - e` is the
  sentinel arithmetic `Random` does on purpose and stays quiet.
- **port** The widening offer casts EVERY operand to `long`, spells every
  literal with `L`, runs the arithmetic wide and narrows back with
  `checked((int)…)`; the `checked(…)` offer second.

### CR0114 ← FR0124 (LogTemplates.fs)

- **port** The template may be a `"…" + "…"` chain of literals; a trailing
  array LITERAL is the params array spelled out; one trailing IDENT
  argument may be the params ARRAY passed whole — its count is invisible,
  so no arity claim; a hole-free `$"…"` compiles to a constant and is not
  the interpolation defect; placeholder syntax `{@Name}`, `{$Name}`,
  `{Name:format}`, `{Name,align}`, `{{` literal.
- *n/a* Logary pipelines.

### CR0115 ← FR0120 (CatchLogException.fs)

- **port** The exception-first overload only pairs with a template in
  FIRST position — `LogError(eventId, "msg")` wants the exception SECOND
  (insert after the `EventId`); Serilog's `Error(ex, …)` and the
  Microsoft.Extensions.Logging methods by their own level names.

## Family G — security

### CR0120 ← FR0066 (SecurityRules.fs)

- **port** The text is followed ONE HOP through the nearest enclosing
  local or a static field of this file; a parameter resolves to nothing —
  the caller is where the string was built and the caller's site is the
  one reported.
- **port** Sinks named for SQL (`executeSql`, `runQuery`, `sqlExec`,
  `queryDb`) count; the `FormattableString` APIs never fire (design has
  it).

### CR0121 ← FR0146 — a DML statement (`SELECT/INSERT/UPDATE/DELETE`) as a
plain literal with no parameter marker in ANY dialect (`@name`, `:name`,
`?`, `$1`).

### CR0122 ← FR0126 (SecurityRules.fs)

- **port** The editor's alternative splits an interpolated template into
  the ARGUMENT LIST a shell would see — whitespace separates, a quote pair
  keeps its contents together and is dropped (`+x "{path}"` is two
  arguments); a format specifier in a hole (`{n:N2}`) is not followed; a
  SHELL executable (`cmd`, `cmd.exe`, `sh`, `bash`, `zsh`) takes a command
  line by design, and an argument list would change what runs — note only
  there.

### CR0123 ← FR0127 and CR0124 ← FR0153 (SecretLiterals.fs)

- **port** The pattern set includes Stripe/Svix (`sk_live_`, `whsec_`), a
  three-segment JWT (`eyJ….eyJ….sig`), `Bearer eyJ…`, an Azure storage
  `AccountKey=`, and a connection string only when another connection
  KEY sits beside the password; placeholder passwords (`password`, `test`,
  `changeme`, `<…>`, `{…}`, `%…%`, `$(…)`) are samples; the literal parts
  of an interpolated string are scanned (a key with a hole in its middle
  is still a key); C#: `const string` is the `[<Literal>]` (CR0124), and a
  source-generator/analyzer attribute argument the type-provider static
  argument analogue.

### CR0125 ← FR0065 (SecurityRules.fs) — the WebSocket RFC 6455 GUID and
the sibling-arm SHA-256 exemptions are in the design; add: SHA-1 in one
arm of a `switch` whose sibling constructs SHA-256 or stronger is a format
option the caller chose (the F# compiler's `--checksumalgorithm`).

### CR0126 ← FR0128 (ObsoleteCrypto.fs)

- **port** The factories return the BASE type (`SHA256.Create()` is
  `SHA256`, not `SHA256Managed`): any OTHER mention of the obsolete name in
  the file — a type annotation, an `is` test, `typeof`, a generic
  argument — can break the build or change a type test's meaning; each
  rewrite site mentions the name exactly once, and any surplus textual
  mention VETOES that name.

## Family H — cosmetic and redundancy

### CR0140 ← FR0082 (RedundantSyntax.fs)

- **port** A type declared in THIS FILE under the short name would win the
  attribute lookup after trimming (attribute resolution tries the exact
  name before appending `Attribute`) — the speculative check covers it;
  the syntactic guard is cheaper.

### CR0142 ← FR0060 (AttributeMerge.fs)

- **port** Knobs `maxAttributes` (4) and `wrapColumn` (110): past either
  the rule stands down rather than inventing a wrapped layout; a comment
  between the brackets suppresses; attributes with a TARGET
  (`[assembly: …]`, `[return: …]`, `[field: …]`) keep their own brackets.

### CR0143 ← FR0084 — each strip is independently valid; an
underscore-only name stays quoted (`@_`? — C# `_` is a discard: keep `@_`).

### CR0145 ← FR0147 (QualifiedNames.fs)

- **port** The namespace comes from the ENTITY at each qualified name, so
  the longest namespace wins by construction and a module/type prefix is
  never mistaken for a namespace (toro's `Toro.noGrad` — the module
  `Toro` of namespace `Toro`, already open — was once left a bare
  `noGrad`); a constructor is spelled as its type; an ASSIGNMENT TARGET
  is a spelling too (`System.Diagnostics.Trace.AutoFlush = true` kept its
  prefix while the reads lost theirs); a pattern head that stopped
  resolving would silently become a binder — only a spelling that resolves
  is shortened.
- **port** Every top-level namespace block of the file has its own usings
  and its own place for a new one; the insert goes among the file's
  usings in alphabetical order within its family (`System.Collections.Generic`
  after `System.Collections`, `System` above both), else after the last
  using, else under the namespace header, never between a declaration's
  doc comment and the declaration; a file-scoped namespace and
  `global using`s are the C# wrinkles.
- **port** A namespace the file ALREADY opens shortens at any count (that
  case is IDE0001's in C# — the design defers it).
- **port** Early-out before the typed check: a namespace can only qualify
  when its first segment recurs at least as often as the smaller
  threshold; one typed lookup per DISTINCT spelling, not per occurrence.
- **port** The clash check is by MECHANISM against every namespace alike
  (the file's own definitions, every name it uses unqualified, other
  opened namespaces and modules, an extension method the new namespace
  exports named like a method the file calls); every prefix of the file's
  own namespace is "own" too (a file in `Company.Product.Data` need not
  open `Company.Product`).

### CR0146 ← FR0132 (CommentDoc.fs)

- **port** A trailing note must READ as a summary: a punctuation marker
  (`// ^ index`, `// -- node`), a single word (`// unused`), fewer than 12
  characters, or a code fragment (`// fun x -> x`) is a margin annotation,
  and a doc line made of it is worse than none; `<` and `&` are XML syntax
  (escaping would break the contains-the-original comment-loss proof).
- **port** The comment must END its line, real code must precede it on the
  line, and it must sit on the HEADER line (the one the declaration
  keyword starts); with attributes above, the insert lands at the keyword
  line and a `///` between an attribute line and its declaration is a
  misplaced-doc warning (C#: `CS1587`).
- **port** Test files: a fixture's trailing note labels the fixture and
  documents nothing.
