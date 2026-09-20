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
