# PerfClaims results

`dotnet run -c Release` in this directory, .NET 10.0.302, x64, ShortRun job,
in-process toolchain, 2026-09-19. Fixtures: 1000 `int`s / 1000 words;
`IntSeq` is a `Select`-backed `IEnumerable<int>` (a real lazy sequence, not
a collection). Means in ns, allocation in bytes. A performance rule's
rewrite must win on one axis; an idiom rule's must hold parity; a shape
that loses goes behind a default-off switch.

## CR0020 — conversion move (performance)

| Shape | Before | After | Verdict |
|---|---|---|---|
| `seq.ToList().Any(p)` → `seq.Any(p)` | 346 ns, 4104 B | 523 ns, 48 B | allocation win (85×), time −50% — ships |
| `foreach (x in seq.ToList())` → `foreach (x in seq)` | 586 ns, 4104 B | 559 ns, 48 B | wins both — ships |
| `seq.ToList().Where(p)` → `seq.Where(p).ToList()` | 882 ns, 6232 B | 891 ns, 2160 B | parity time, allocation win — ships |
| `seq.ToList().Select(f)` → `seq.Select(f).ToList()` | 626 ns, 8232 B | 2130 ns, 4248 B | 3.4× slower — **not offered** (`Select` is not a movable stage) |

## CR0021 — aggregate (idiom, parity wanted)

| Shape | Loop | Aggregate | Verdict |
|---|---|---|---|
| `int` sum over `int[]` | 193 ns | 59 ns (`Sum()`) | 3.2× win (vectorised) |
| `int` sum over `List<int>` | 273 ns | 61 ns | 4.5× win |
| `long` sum with a selector over `int[]` | 198 ns | 322 ns | −60%: a selector sum is not vectorised |
| `int` sum over a lazy `IEnumerable<int>` | 572 ns | 2680 ns | 4.7× slower — **the source must be an array or `List<T>`** |
| count with a predicate over `int[]` | 532 ns | 533 ns | parity |
| string `+=` over 1000 words → `string.Concat` | 138.9 µs, 3.8 MB | 2.2 µs, 7.8 KB | 60× win, 490× less allocation |

Sum and Count rewrite only where the source is an array or a `List<T>`;
`string.Concat` for any source.

## CR0022 — flag loop → Any/All (idiom, parity wanted)

| Shape | Loop | `Any` | Verdict |
|---|---|---|---|
| no match, `int[]`, no `break` | 209 ns | 1367 ns | 6.5× slower |
| no match, `List<int>` | 298 ns | 1334 ns | 4.5× slower (no boxing on .NET 10) |
| early match, loop with `break` | 0.65 ns | 4.2 ns | slower |

`Enumerable.Any(pred)` pays a delegate call per element where the loop's
compare is inlined; it wins only when a match sits early and the loop has
no `break`. By the policy the rule ships **off by default**
(`dotnet_diagnostic.CR0022.severity` wakes it); the `lists` knob is moot.

## CR0023 — Contains on a literal array → set (performance)

| Probe over 1000 words | `string[]` | `HashSet` | `FrozenSet` |
|---|---|---|---|
| 3 elements | 7.0 µs | 4.8 µs | 4.0 µs |
| 20 elements | 56.9 µs | 4.7 µs | 2.3 µs |
| build once (20 elements) | — | 106 ns, 576 B | 1055 ns, 2848 B |

A win from three elements up; the one-time build is repaid by the first
few probes. `FrozenSet` where it resolves.

## CR0024 — Add per element → AddRange (performance)

| Source | `Add` loop | `AddRange` | Verdict |
|---|---|---|---|
| `int[]` | 925 ns, 8424 B | 135 ns, 4056 B | wins both |
| lazy `IEnumerable<int>` | 1277 ns, 8472 B | 3723 ns, 8472 B | 2.9× slower — **the source must be a collection** |

## CR0028 — fill loop → pipeline (idiom, default decided here)

| Source | Loop | Pipeline | Verdict |
|---|---|---|---|
| `int[]`, Where + Select | 605 ns, 4304 B | 592 ns, 2160 B | parity, allocation halved |
| `List<int>`, Where + Select | 763 ns, 4304 B | 2890 ns, 2208 B | 3.8× slower |
| lazy sequence, Where + Select | 1069 ns, 4352 B | 1054 ns, 2224 B | parity, allocation halved |
| `int[]`, Select only | 1040 ns, 8424 B | 1780 ns, 4104 B | 1.7× slower |

Mixed: no parity on `List<T>`, so the rule stays **off by default** as the
design expected.

## CR0029 — Select fusion (idiom, parity wanted)

| Source | Two `Select`s | One | Verdict |
|---|---|---|---|
| `int[]` | 6257 ns | 4146 ns | 34% win |
| lazy sequence | 7546 ns | 5121 ns | 32% win |

The runtime does not compose consecutive `Select`s for free; parity holds
with margin.

## CR0031 — `new Random()` → `Random.Shared` (performance)

68.7 ns, 72 B → 0.6 ns, 0 B.

## CR0032 — Keys loop → pairs (performance)

4127 ns → 704 ns (5.9×), no allocation either way.

## CR0033 — Append of a concatenation → pieces (performance)

1864 ns, 19192 B → 1030 ns, 8904 B per 100 appends: wins both.

---

The pairs after CR0033 (LaterClaims.cs), same machine and job, 2026-09-21.
Two performance rules have no runtime pair, and say why at the top of that
file: CR0045 (a test blocking on a task is a thread-pool hazard, not a
micro-cost) and CR0054 (`WhenAll` of one task: one array and one wrapper
task, not worth a pair). `RulesMdTests` fails the suite for a performance
rule in neither place.

## CR0025 — growing an array per element → `List<T>` (performance)

| Shape, 1000 elements | Time | Allocation |
|---|---|---|
| `arr = arr.Append(x).ToArray()` | 85.3 µs | 2035 KB |
| `Array.Resize(ref arr, arr.Length + 1)` | 72.8 µs | 1980 KB |
| `list.Add(x)` | 0.9 µs | 8.2 KB |
| `list.Add(x)` then `ToArray()` | 1.0 µs | 12.2 KB |

Quadratic against linear: 80× and 250× less allocation.

## CR0026 — `Count()`/`ElementAt()` on a bare `IEnumerable<T>` in a loop → materialised once (performance)

`for (i < seq.Count()) … seq.ElementAt(i)` over a lazy sequence of 1000:
279 µs → 0.66 µs with one `ToList()` (420×); the one list is 4 KB the
loop never allocated, the price of not walking the source 2000 times.

## CR0035 — recursive `yield` → explicit stack (performance)

A tree of 781 nodes, five levels: 23.8 µs, 62 KB → 4.6 µs, 0.7 KB (5.1×,
93× less allocation). Each level of a recursive iterator is an enumerator
every element passes through.

## CR0046 — `async` that only `await`s and returns → the task through (performance)

100 calls: 1010 ns, 12.9 KB → 315 ns, 6.5 KB (3.2×, half the allocation:
the state machine and its box are what go).

## CR0081 — small `record` → `readonly record struct` (performance)

1000 constructions of two and one equality each: 3874 ns, 48 KB → 196 ns,
0 B (20×, no allocation).

## CR0082 — `Tuple<…>` → `ValueTuple` (performance)

1000 constructions and element reads: 2796 ns, 32 KB → 391 ns, 0 B (7×).

## CR0087 — `enum.ToString() == "Name"` → `enum == Enum.Name` (performance)

1000 comparisons: 5228 ns, 24 KB → 218 ns, 0 B (24×): a name lookup and
a string per comparison against an integer compare.

## CR0102 — `ToString()` in an interpolation hole → the bare hole (performance)

| Shape | `ToString()` | bare hole | Verdict |
|---|---|---|---|
| `$"{count.ToString()} items"`, an `int` | 10.9 ns, 80 B | 15.0 ns, 48 B | −38% time, 40% less allocation |
| `$"id {id.ToString()}"`, a `Guid` | 13.2 ns, 200 B | 18.8 ns, 104 B | −42% time, 48% less allocation |
| `string.Join(", ", xs.Select(x => x.ToString()))` → `string.Join(", ", xs)` | 13.3 µs, 32.2 KB | 7.9 µs, 9.8 KB | wins both |

The hole formats through `ISpanFormattable` into the handler's buffer,
which is the allocation win; the buffer's book-keeping costs a few
nanoseconds against a string already made. The rule's claim is the
allocation (the F# twin FR0021's is the same), and the `Join` shape wins
both; ships.

## CR0108 — a regex over plain text → the string operation (performance)

| Shape | Regex | String | Verdict |
|---|---|---|---|
| `IsMatch(s, "lit")` → `Contains` | 40.9 ns | 5.2 ns | 8× |
| `IsMatch(s, "^lit")` → `StartsWith(…, Ordinal)` | 33.7 ns | 0.03 ns | folded away |
| `Replace(s, "lit", "x")` → `Replace` | 92.6 ns, 200 B | 31.4 ns, 200 B | 3× |
| `Matches(s, "lit").Count` → `AsSpan().Count` | 163.6 ns, 504 B | 24.0 ns, 0 B | 7×, no allocation |
| `Split(s, "; ")` → `Split` | 97.8 ns, 360 B | 34.2 ns, 272 B | 3× |

Every shape wins both axes. (The runtime's regex cache is what keeps the
regex side this close: see CR0109.) `RegexClaimTests` in the property
suite checks the equivalence itself — both sides answer alike on random
subjects with overlaps, newlines, non-ASCII, empties.

## CR0109 — a regex built per call → hoisted or generated (performance)

| Shape | Time | Allocation |
|---|---|---|
| `new Regex(@"Error Code (\d+)").IsMatch(s)` per call | 707 ns | 2656 B |
| `Regex.IsMatch(s, @"Error Code (\d+)")` (static call) | 58 ns | 0 B |
| `static readonly Regex` field | 56 ns | 0 B |
| `[GeneratedRegex]` | 21 ns | 0 B |

The construction parses every call: 12× and 2.6 KB against the field. The
static call is served from the runtime's cache of fifteen patterns, at the
field's speed — until a sixteenth pattern turns the cache over, when it
pays the construction's price. The generated regex runs its own code
instead of the interpreter: 2.7× over the field, 34× over the
construction. The rule's wording says so (it once said "compiled on every
call" of the static call too, which this table corrected).

## CR0110 — a build-once object created per iteration (performance, note)

| Shape, 100 iterations | Per iteration | Hoisted | Verdict |
|---|---|---|---|
| `SHA256.Create()` | 29.2 µs, 24.2 KB | 12.9 µs, 11.1 KB | 2.3×; `SHA256.HashData` 14.0 µs, 5.5 KB |
| `new JsonSerializerOptions()` | 40.3 µs, 22.4 KB | 4.8 µs, 3.2 KB | 8.4×: the options cache their metadata |

`new HttpClient()` per call is a socket-exhaustion question, not a
micro-benchmark; the note names the lifetime.

## CR0148 — `Encoding.UTF8.GetBytes("literal")` → `"literal"u8` (performance)

12.2 ns, 56 B → 3.3 ns, 56 B for `u8.ToArray()` (3.7×, the array kept for
the caller that needs one); the bare `u8` span is 0 ns and 0 B.

## CR0150 — read-only static `Dictionary`/`HashSet` → frozen (performance)

| 1000 probes into 50 entries | Mutable | Frozen |
|---|---|---|
| `Dictionary.TryGetValue` | 2924 ns | 784 ns (3.7×) |
| `HashSet.Contains` | 4298 ns | 2268 ns (1.9×) |
| build once, 50 entries | — | 2051 ns, 5.3 KB |

The build is repaid by the first thousand probes.

## CR0151 — `params T[]` → `params ReadOnlySpan<T>` (performance)

100 calls of three arguments: 1228 ns, 4800 B → 983 ns, 0 B (1.25×, no
allocation: the arguments live in an inline array on the caller's stack).

## CR0152 — `lock (object)` → `Lock` (performance)

1000 uncontended acquisitions: 13.7 µs → 12.8 µs (7%; within noise, no
allocation either way). The claim is the type's: no monitor sync block,
no boxing hazard, a `Scope` the compiler checks. Parity is what it needs.

## CR0166 — `Parse` under `catch` → `TryParse` (performance)

| Input | `Parse` + `catch` | `TryParse` |
|---|---|---|
| failing (the case the `catch` is for) | 1591 ns, 496 B | 3.1 ns, 0 B (500×) |
| passing | 5.3 ns | 5.0 ns |

An exception is five hundred parses.

## CR0174 — `Substring` handed to a span-reading consumer → `AsSpan` (performance)

| Shape | `Substring` | `AsSpan` |
|---|---|---|
| `int.Parse(s.Substring(6, 6))` | 9.7 ns, 40 B | 5.8 ns, 0 B |
| `sb.Append(s.Substring(6))` × 100 | 1438 ns, 16.0 KB | 517 ns, 8.8 KB |

The copy is the whole cost of the parse; the builder keeps only its own.

## CR0175 — a prefix cut out to be compared → `StartsWith(…, Ordinal)` (performance)

| Shape | `Substring ==` | `StartsWith`/`EndsWith` |
|---|---|---|
| prefix, a hit | 8.0 ns, 40 B | 0.07 ns, 0 B |
| prefix, a miss | 8.5 ns, 40 B | 0.09 ns, 0 B |
| suffix, a hit | 6.2 ns, 32 B | 0.28 ns, 0 B |

The ordinal comparison is a few instructions the JIT folds against a
literal; the cut was an allocation per comparison. (Both sides under the
same `Length` guard: the rewrite is offered only there.)

## CR0176 — `ToCharArray()` under a `foreach` → the string (performance)

| Shape, 148 characters | Array | String | Verdict |
|---|---|---|---|
| `foreach` | 58.7 ns, 320 B | 42.9 ns, 0 B | wins both — ships |
| `.Any(char.IsDigit)` (an early hit) | 23.3 ns, 320 B | 25.2 ns, 32 B | parity |
| `.Count(c => c == '1')` (the whole walk) | 71.3 ns, 320 B | 611 ns, 32 B | 8.6× slower — **LINQ stays on the array** |

LINQ over a `char[]` runs on the array's span fast path; over a `string`
it walks a boxed `CharEnumerator` an interface call at a time. The rule
rewrites the `foreach` only, as the F# twin keeps `Seq.*` on the array.

## CR0177 — a loop-invariant local hoisted above the loop (performance)

| Shape, 1000 iterations, a call in the body | In the loop | Hoisted | Verdict |
|---|---|---|---|
| `var c = a * 3 + 1;` (a readonly field) | 1.42 µs | 1.37 µs | parity — the JIT hoists it |
| `var c = field * 3 + 1;` (a mutable field) | 1.62 µs | 1.48 µs | −8%: a field read cannot move across the call |
| `var label = prefix + ":";` | 5.90 µs, 48 000 B | 1.49 µs, 48 B | 4× and the allocation gone — ships |

The rule takes the concatenation (the win) and the readonly-field or local
arithmetic (parity, moved for the reading); the mutable field it never
reads — a call in the body can change it (2026-09-21).
