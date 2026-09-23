# Rules

Every rule csharp-refactor knows. The table is the quick reference — one
line each: an example of the source it fires on and the fix it offers for
it — and [Rule details](#rule-details) below it explains each one in prose.
[DESIGN.md](DESIGN.md) carries the full planned catalog and the safety model
behind it; this file lists what the shipped build implements. The tool's own
tests keep this file complete: every code in the rule catalog has a row and
a section, and its category and default state match the code.

| ID | Category | Enabled * | API ** | Priority *** | Yields to **** | Fires on | Offered fix |
| -- | -- | -- | -- | -- | -- | -- | -- |
| CR0001 | Idiom | v | | | | `if (c) return true; return false;`, `if (c) x = true; else x = false;` | `return c;`, `x = c;` (`!c` for the swapped literals) |
| CR0002 | Idiom | v | | | | `if (k == 1) … else if (k == 2 \|\| k == 3) … else …` on one scrutinee | `k switch { 1 => …, 2 or 3 => …, _ => … }`, or `switch (k) { case 1: … }` for statement bodies |
| CR0003 | Idiom | v | | | | `if (s is Circle) { var c = (Circle)s; … } else if (s is Rect) …` | `switch (s) { case Circle c: … case Rect rect: … }` |
| CR0004 | Idiom | v | | | | `x.HasValue ? x.Value + 1 : 0`, `if (x.HasValue) Use(x.Value);`, `x.HasValue && p(x.Value)` | `x is { } v ? v + 1 : 0`, `if (x is { } v) Use(v);`, `x is { } v && p(v)` |
| CR0005 | Idiom | v | | | | `if (a) { if (b) X else E } else E`, `if (a) { if (b) X }` | `if (a && b) X else E` |
| CR0006 | Idiom | | | | | `if (ok) { twenty lines } else { return; }` | `if (!ok) { return; } twenty lines` |
| CR0007 | Idiom | v | | | | `x && true`, `true && x`, `x || false`, `false || x` | `x` |
| CR0008 | Idiom | v | | | | `a || a`, `a && a` | `a` |
| CR0009 | Idiom | v | | | | `case 1: return "x"; case 2: return "x";`, `1 => f(), 2 => f()` | `case 1: case 2: return "x";`, `1 or 2 => f()` |
| CR0010 | Idiom | v | | | | `case var x when x == "A":`, `var v when v == 3 =>` | `case "A":`, `3 =>` |
| CR0011 | Idiom | v | | | per hint: IDE0100, IDE0120, CA1827, IDE0083 | `!(a == b)`, `x == true`, `a.CompareTo(b) < 0`, `xs.Where(p).Count() > 0`, `!(x is T)` … | `a != b`, `x`, `a < b`, `xs.Any(p)`, `x is not T`; custom `lhs ===> rhs` hints from `csharp_refactor.hints` |
| CR0012 | Correctness | v | | | | `case X: // not supported yet` then `return null;` | `throw new NotImplementedException();` |
| CR0013 | Correctness | v | | | | `switch (c) { case Red: … default: … }` with `Blue` unnamed | note; editor: `case Color.Blue:` on the arm, `default: throw new ArgumentOutOfRangeException(nameof(c));` |
| CR0014 | Correctness | v | | | | `switch (c) { case Red: return …; case Green: throw …; }` with `Blue` unhandled | note; editor: `case Color.Blue: throw new NotImplementedException();` |
| CR0015 | Idiom | v | | | | `for (int i = 0; i < xs.Length; i++) Use(xs[i]);` | `foreach (var item in xs) Use(item);` |
| CR0016 | Idiom | | | | | `bool done = false; while (!done && …) { … done = true; … }` | note: `break` at the decision |
| CR0017 | Correctness | v | | | | `for (int i …) actions.Add(() => Use(i));` | note: every call sees the final `i`; copy into a loop-local |
| CR0020 | Performance | v | | | | `foreach (var x in xs.ToList())`, `xs.ToList().Any(p)`, `xs.ToList().Where(p)` | `foreach (var x in xs)`, `xs.Any(p)`, `xs.Where(p).ToList()` |
| CR0021 | Idiom | v | | | | `var total = 0m; foreach (var x in xs) total += x.Price;` (also a count, a string) | `var total = xs.Sum(x => x.Price);` / `.Count(p)` / `string.Concat(xs)` |
| CR0022 | Idiom | | | | | `bool found = false; foreach (var x in xs) if (p(x)) found = true;` | `bool found = xs.Any(x => p(x));` (`All` for the `true`-initialised dual) |
| CR0023 | Performance | v | | | | `static readonly string[] Allowed = { … };` probed by `Contains` per element | `FrozenSet<string>` (.NET 8+) / `HashSet<string>`; else note |
| CR0024 | Performance | v | | | | `foreach (var x in xs) acc.Add(x);` | `acc.AddRange(xs);` |
| CR0025 | Performance | v | | | | `arr = arr.Append(x).ToArray()`, `Array.Resize(ref arr, arr.Length + 1)`, `s += piece` in a loop | note: `List<T>` / `StringBuilder` / `string.Join` |
| CR0026 | Performance | v | | | CA1826, CA1829 | `xs.Count()`, `xs.ElementAt(i)`, `xs.Last()` on a bare `IEnumerable<T>` inside a loop | note: materialise once |
| CR0027 | Correctness | v | | | | `xs.Select(x => Log(x));`, `xs.Where(p);` as a statement | note: lazy, runs nothing |
| CR0028 | Idiom | | | | | `var r = new List<T>(); foreach (var x in xs) if (p(x)) r.Add(f(x));` then only read | `var r = xs.Where(p).Select(f).ToList();` |
| CR0029 | Idiom | v | | | | `xs.Select(x => x.A).Select(a => a.B)`, `xs.Select(x => x)` | `xs.Select(x => x.A.B)`, `xs` |
| CR0030 | Correctness | v | | | CA1851 | an `IEnumerable<T>` parameter enumerated twice on one path | note: a query or generator runs twice |
| CR0031 | Performance | v | | | | `new Random().Next(…)`, `var r = new Random();` used for calls | `Random.Shared` |
| CR0032 | Performance | v | | | | `foreach (var k in d.Keys) use(k, d[k])` | `foreach (var (k, v) in d) use(k, v)` |
| CR0033 | Performance | v | | | | `sb.Append(a + b + c)` | `sb.Append(a).Append(b).Append(c)` |
| CR0034 | Correctness | v | | v | | `foreach (var c in customers) foreach (var o in db.Orders.Where(o => o.CustomerId == c.Id))` | note: N+1 |
| CR0035 | Performance | v | | | | `foreach (var c in Walk(child)) yield return c;` in `Walk` | note: O(depth) per element; explicit stack |
| CR0040 | Correctness | v | | | CA1849 | `t.Result`, `t.Wait()`, `t.GetAwaiter().GetResult()`, `Task.WaitAll(a, b)` inside an `async` body | `await t`, `await Task.WhenAll(a, b)`; outside `async`: boundary note |
| CR0041 | Correctness | v | v | | | `private int Fetch() { return Load().Result; }` with every caller in an `async` body of this file | `private async Task<int> FetchAsync()`, drains `await`, callers `await` |
| CR0042 | Correctness | v | | | CA1849 | `reader.ReadLine()`, `File.ReadAllText(p)`, `Thread.Sleep(n)` inside an `async` body | `await reader.ReadLineAsync()`, `await File.ReadAllTextAsync(p)`, `await Task.Delay(n)` |
| CR0043 | Correctness | v | | | VSTHRD100 | `async void M()` that is not an event handler | `async Task M()`, callers `await` |
| CR0044 | Correctness | v | | | | `Task.Run(…);`, `SaveAsync();` as a statement in a non-`async` method | note: nobody observes a failure; `_ =` is a decision |
| CR0045 | Performance | v | | | | `[Fact] public void T() { var r = Load().Result; }`, `Assert.Throws<E>(() => t.Wait())` | `public async Task T() { var r = await Load(); }`, `await Assert.ThrowsAsync<E>(() => t)` |
| CR0046 | Performance | v | | | | `async Task<T> M(x) { return await Inner(x); }` with nothing else | `Task<T> M(x) { return Inner(x); }` |
| CR0047 | Correctness | v | | v | CA2002 | `lock (this)`, `lock ("cache")`, `lock (typeof(T))`, a boxed value | note; editor: a private `Lock`/`object` gate |
| CR0048 | Correctness | v | | | | `Monitor.Enter(x); try { … } finally { Monitor.Exit(x); }` | `lock (x) { … }` |
| CR0049 | Correctness | v | | | | `if (!cd.TryGetValue(k, out var v)) { v = Compute(); cd[k] = v; }` on a `ConcurrentDictionary` | the miss arm becomes `v = cd.GetOrAdd(k, _ => Compute());` |
| CR0050 | Correctness | v | | | | `cd.GetOrAdd(k, factory)` with a `Task`/`ValueTask`/`Lazy` value | note: a faulted value stays cached |
| CR0051 | Correctness | v | | | | `Task<T> M() { using var x = …; return DoAsync(x); }` | `async Task<T> M() { using var x = …; return await DoAsync(x); }` |
| CR0052 | Correctness | v | | | | `AppDomain.CurrentDomain.ProcessExit += (s, e) => this.Flush();` never removed | note: the object lives as long as the publisher |
| CR0053 | Correctness | v | | | VSTHRD101 | `list.ForEach(async x => …)`, `Parallel.ForEach(xs, async x => …)` | note: `async void` in disguise |
| CR0054 | Performance | v | | | CA1842, CA1843 | `Task.WhenAll(new[] { t })`, `Task.WaitAll(new[] { t })` | note; editor: the task itself / `t.Wait()` |
| CR0055 | Correctness | v | | | CA2016 | `Foo(x, CancellationToken.None)` / `Foo(x, default)` while `ct` is in scope | `Foo(x, ct)` |
| CR0060 | Correctness | v | | | CA2000 | `var s = new FileStream(…); … s.ReadByte();` never disposed, staying in scope | `using var s = …`; a note naming the destination when it escapes to an unknown owner |
| CR0061 | Correctness | v | | v | CA1001 | a type constructing a disposable field without `IDisposable` | note; editor: `: IDisposable` and a `Dispose` releasing the fields |
| CR0062 | Correctness | v | | v | CA2213 | a `Dispose` that never releases an owned disposable field | note (`Cancel` without `Dispose` named separately); editor: `field?.Dispose();` first in `Dispose` |
| CR0063 | Correctness | v | | | | `public void Dispose()` on a type not implementing `IDisposable` | note |
| CR0064 | Correctness | v | | | CA1031 | `catch { }`, `catch (Exception) { return null; }`, a filter never reading the exception | note; editor: a divisor guard, `catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)`, a log line in the file's idiom |
| CR0065 | Idiom | v | | | | `catch (E ex) { if (!Cond(ex)) throw; … }` | `catch (E ex) when (Cond(ex)) { … }` |
| CR0066 | Correctness | v | | v | CA2219 | `throw` inside `finally` | note |
| CR0067 | Correctness | v | | | CA1065 | `throw` inside `Equals`/`GetHashCode`/`ToString`/`Dispose`/a static constructor | note |
| CR0068 | Correctness | v | | | CA2201 | `new NullReferenceException()`, `new IndexOutOfRangeException()`, `new OutOfMemoryException()`… | note |
| CR0069 | Idiom | v | v | | | `throw new InvalidOperationException("Rejected");` with a constant message | `$"Rejected, calling {nameof(M)} with x: {x}"` |
| CR0070 | Correctness | v | | | | `catch (ReflectionTypeLoadException e) { Log(e.Message); }`, `AggregateException`, `WebException`, `SqlException` | note; editor: the `LoaderExceptions` messages joined in place of `e.Message` |
| CR0080 | Idiom | v | v | | | a `class` whose every instance member is a get-only/`init` property, a `readonly` field or a constructor | `record` |
| CR0081 | Performance | v | v | | | a `record` of at most four small unmanaged fields | `readonly record struct` |
| CR0082 | Performance | v | v | | | `Tuple<int, string>` in a private/internal signature, field or local; `Tuple.Create(a, b)` | `(int, string)`, `(a, b)` |
| CR0083 | Idiom | v | v | | | `{ get; set; }` assigned only in constructors and object initialisers of the type | `{ get; init; }` |
| CR0084 | Correctness | v | | | CA2211 | `public static int Counter;` assigned from two or more sites or from itself | note; editor: `private`/`readonly` |
| CR0085 | Correctness | v | | | | `x.GetType().Name == "Customer"`, `x.GetType() == typeof(T)` | note |
| CR0086 | Correctness | v | | v | CA2214 | a constructor calling a `virtual`/`abstract` member of its own type | note |
| CR0087 | Performance | v | | | | `status.ToString() == "Active"` on an enum | `status == Status.Active` |
| CR0089 | Idiom | | v | | | a private type's `DateTime` slot written from `DateTime.Now`/`UtcNow` and read through parity members | `DateTimeOffset` in one edit set |
| CR0090 | Correctness | v | | | | `new Guid()`, `Guid g = new();` | `Guid.Empty` (editor also offers `Guid.NewGuid()`) |
| CR0100 | Idiom | v | | | | `"Hello " + name + "!"` | `$"Hello {name}!"` |
| CR0101 | Idiom | v | | | | `string.Format("{0} of {1:N2}", a, b)` | `$"{a} of {b:N2}"` |
| CR0102 | Performance | v | | | | `$"{x.ToString()} items"`, `string.Join(", ", xs.Select(x => x.ToString()))` | `$"{x} items"`, `string.Join(", ", xs)` |
| CR0103 | Cosmetic | v | | | | `$"no holes"` | `"no holes"` |
| CR0104 | Idiom | v | | | | `x == null || x == ""`, `x is null || x.Length == 0`, `x == null || x.Trim() == ""` | `string.IsNullOrEmpty(x)`, `string.IsNullOrWhiteSpace(x)` |
| CR0105 | Correctness | v | | | CA1305 | `double.Parse(s)`, `DateTime.Parse(s)` without a provider | editor: `CultureInfo.InvariantCulture` (primary) / `CurrentCulture`; CLI under `invariant` |
| CR0106 | Correctness | v | | | | `DateTime.Now` as an instant; `DateTime.Today`, `DateTime.Now.Date` (notes) | `DateTime.UtcNow` under `utc_now`; editor always |
| CR0107 | Correctness | v | | v | | `new Regex("(unclosed")`, `Regex.IsMatch(s, "[")` | note: a guaranteed `ArgumentException` |
| CR0108 | Performance | v | | | | `Regex.IsMatch(s, "^abc")`, `Regex.Replace(s, "abcd", "x")`, `Regex.Matches(s, "ab").Count`, `Regex.Split(s, ", ")` with a plain literal | `s.StartsWith("abc", StringComparison.Ordinal)`, `s.Contains("abc")`, `s.Replace("abcd", "x")`, `s.AsSpan().Count("ab")` (.NET 8), `s.Split(", ")` |
| CR0109 | Performance | v | | | SYSLIB1045 | `new Regex(@"\d+")` or `Regex.Match(s, @"\d+")` inside a method body (a plain-text pattern is never hoisted: it is a string operation, CR0108's) | `[GeneratedRegex(@"\d+")] private static partial Regex DigitsRegex();` (.NET 7+, the type made `partial`) or a `static readonly Regex` field |
| CR0110 | Performance | v | | | CA1869, CA1870 | `new HttpClient()` per call or in a loop; `MD5.Create()`/`SHA256.Create()` in a loop | note |
| CR0111 | Idiom | v | | | | `dir + "\\" + file`, `dir + "/" + file` with path evidence | note: `Path.Combine`/`Path.Join` |
| CR0112 | Correctness | v | | | | bidi controls, the Unicode tag block, zero-width spaces, a mid-file BOM | `200B` escape inside a regular string; note elsewhere |
| CR0113 | Correctness | v | | | | `balance + 2_000_000_000`, `seconds * 1_000_000` on `int` | note; editor: `checked(…)` |
| CR0114 | Correctness | v | | | CA2017, CA2254 | duplicate placeholder names, an arity mismatch, or an interpolated string as a log template | note |
| CR0115 | Correctness | v | | | | `catch (Exception ex) { _log.LogError("sync failed {Id}", id); }` | `_log.LogError(ex, "sync failed {Id}", id);` |
| CR0120 | Correctness | v | | v | CA2100, CA3001 | `cmd.CommandText = $"SELECT … WHERE id={id}"`, `new SqlCommand("… " + name)`, `ExecuteSqlRaw($"…")`, Dapper `Query($"…")` | note: parameterise |
| CR0121 | Correctness | v | | | | a SQL command whose text carries no parameter at all | note |
| CR0122 | Correctness | v | | v | | `Process.Start("cmd", $"/c {input}")`, `psi.Arguments = "… " + input` | note: `ArgumentList` |
| CR0123 | Correctness | v | | v | | a literal matching a provider's documented key format (`sk-ant-`, `sk-`, `AIza`, `ghp_`, `AKIA`, `xoxb-`, PEM headers) | note |
| CR0124 | Correctness | v | | | | a credential in a `const` connection string on a non-loopback server | note |
| CR0125 | Correctness | v | | v | CA5350, CA5351, CA5359, CA5364, CA5386, CA5397 | `MD5.Create()`, `SHA1`, `DES`, `TripleDES`, `RC2`; `ServerCertificateCustomValidationCallback = (…) => true`; `SecurityProtocolType.Tls11`/`Ssl3` | note; editor: SHA-256 |
| CR0126 | Idiom | v | | | SYSLIB0021 | `new SHA256Managed()`, `new RNGCryptoServiceProvider()` | `SHA256.Create()`, `RandomNumberGenerator.Create()` |
| CR0140 | Cosmetic | v | | | | `[SerializableAttribute]` | `[Serializable]` |
| CR0141 | Cosmetic | v | | | | `[Foo()]` | `[Foo]` |
| CR0142 | Cosmetic | | | | | `[Fact] [Trait("a", "b")]` on one line | `[Fact, Trait("a", "b")]` |
| CR0143 | Cosmetic | v | | | | `@name` where `name` is no keyword | `name` |
| CR0144 | Cosmetic | v | | | | `else { if (c) { … } }` | `else if (c) { … }` |
| CR0145 | Idiom | v | | | | `System.Text.Json.JsonSerializer.Serialize(…)` spelled six times (four when three segments deep) | `using System.Text.Json;` among the usings, every use shortened |
| CR0146 | Idiom | v | | | | `public decimal Rate(int n) => …; // monthly, non-compounding rate` | `/// <summary>monthly, non-compounding rate</summary>` above it |
| CR0147 | Idiom | v | | | | `if (xs.Length == 0) … else if (xs.Length == 1) { var a = xs[0]; … } else …` (C# 11) | `switch (xs) { case []: … case [var a]: … default: … }` |
| CR0148 | Performance | v | | | | `Encoding.UTF8.GetBytes("literal")` on an ASCII constant (C# 11) | `"literal"u8.ToArray()`; bare `"literal"u8` for a `ReadOnlySpan<byte>` |
| CR0149 | Idiom | | v | | | an `init`/`set` property with no initialiser that every construction sets and no constructor assigns (C# 11) | `required` |
| CR0150 | Performance | v | v | | | `static readonly Dictionary<K,V>`/`HashSet<T>` filled in its initialiser and only read (.NET 8) | `FrozenDictionary<K,V>`/`FrozenSet<T>` via `.ToFrozenDictionary()`/`.ToFrozenSet()` |
| CR0151 | Performance | v | v | | | `params T[]` on a method whose body only enumerates, indexes or measures it (C# 13) | `params ReadOnlySpan<T>` |
| CR0152 | Performance | v | | | | `private readonly object _gate = new();` used only as a `lock` target (C# 13, .NET 9) | `private readonly Lock _gate = new();` |
| CR0153 | Idiom | v | | | | a property whose private backing field is referenced only inside its own accessors (C# 14) | the `field` keyword, the backing field removed |
| CR0154 | Idiom | v | | | IDE0031 | `if (x != null) x.P = v;`, `if (x is not null) x[i] = v;` (C# 14) | `x?.P = v;` |
| CR0155 | Idiom | | v | | | a static class holding only `this T`-extension methods on one receiver (C# 14) | an `extension(T x) { … }` block |
| CR0156 | Idiom | v | v | | | a memberless `abstract record Base;` whose only derived types are sealed records in the same assembly (C# 15) | `union Base(Case1, Case2);`, `: Base` dropped from each case |
| CR0157 | Idiom | v | | | | a `switch` expression over an enum or a sealed hierarchy listing every case, whose discard arm throws a parameterless `InvalidOperationException`/`SwitchExpressionException`/`ArgumentOutOfRangeException` (.NET 7) | `throw new UnreachableException()` |
| CR0158 | Idiom | v | | | | a `switch` expression over a native union listing every case that keeps a `_ =>` arm (C# 15) | note |
| CR0160 | Correctness | v | | v | | `for (int i = 0; …) tasks.Add(Task.Run(() => Use(i)));` — a closure created in a loop reads a local the loop changes and outlives the iteration (stored, returned, queued, subscribed, or kept alive by a lazy LINQ chain) | `var i1 = i;` before the statement, the closure reading `i1`; a note where the callee is unknown or the closure sits in the loop header |
| CR0161 | Correctness | v | | v | | `_readonlyCounter.Bump();`, `Prop.Bump();`, `list[0].Bump();`, a `foreach` variable or an `in` parameter — a mutating struct method on a receiver the compiler copies first | note: the call changes the copy, the original stays |
| CR0162 | Correctness | v | | v | | `new Timer(cb, null, 0, 1000);` as a statement, or a local timer that never leaves the method | note: nothing references the timer once the method returns, the collector takes it and its callbacks stop |
| CR0163 | Correctness | v | | v | | `await _gate.WaitAsync(); … _gate.Release();` (`Semaphore`/`Mutex.WaitOne`, `ReaderWriterLockSlim.Enter*Lock`) with statements between them and no `try` | `try { … } finally { _gate.Release(); }` |
| CR0164 | Correctness | v | | | | `if (_cache == null) _cache = new X(); return _cache;`, `_cache ??= new X()` on a `static` reference-typed field outside a `lock` | `return LazyInitializer.EnsureInitialized(ref _cache, () => new X());` |
| CR0165 | Correctness | v | | v | CA2200 | `catch (Exception ex) { throw new SyncException("failed"); }` — the wrapper has a `(…, Exception)` constructor and the caught exception is dropped | `throw new SyncException("failed", ex);` (an unnamed catch gains `ex`) |
| CR0166 | Performance | v | | | | `try { v = int.Parse(s); } catch (FormatException) { v = -1; }`, `try { return int.Parse(s); } catch { return 0; }` | `if (!int.TryParse(s, out v)) { v = -1; }`, `return int.TryParse(s, out var parsed) ? parsed : 0;` |
| CR0167 | Correctness | v | | | | `a == b`, `a != b` on `float`/`double`/`Half` operands, neither a literal, a constant or a rounded value | note: compare against a tolerance |
| CR0168 | Correctness | v | | | | `double avg = sum / count;`, `Use(a / b)` into a `double` parameter, `a / b * 1.0` — two integral operands whose quotient lands in a floating-point target | note; the editor offers `(double)sum / count` |
| CR0169 | Correctness | v | | v | | `DateTime.Now > _startedUtc`, `DateTime.UtcNow - DateTime.Today`, through locals and fields written once from `Now`/`Today`/`UtcNow` | note: the two kinds differ by the machine's offset |
| CR0170 | Correctness | v | | | | `Task A(…, CancellationToken ct) { foreach (var x in xs) { await Process(x); } }` — a loop that awaits, sleeps or blocks on a task and never reads the token | `ct.ThrowIfCancellationRequested();` at the top of the loop body |
| CR0171 | Correctness | v | | v | | `foreach (var x in xs) { if (x < 0) xs.Remove(x); }`, `foreach (var n in names) { … names.Add("fresh"); }` — the enumerated collection changed under its own `foreach` | `xs.RemoveAll(x => x < 0);` for the filter shape; `foreach (var n in names.ToList())` — a snapshot — otherwise |
| CR0172 | Idiom | v | v | | | `var schema = "app";`, `int retries = 3;`, `static readonly string Prefix = "v";` — initialised with a constant, never written | `const string schema = "app";`, `const int retries = 3;`, `const string Prefix = "v";` (a public or protected field under `--api-changes`) |
| CR0173 | Idiom | v | | | IDE0046, IDE0045 | `if (a) return "1"; else return "2";`, `if (a) return x; return y;`, `if (a) v = f(); else v = g();` | `return a ? "1" : "2";`, `v = a ? f() : g();` |
| CR0174 | Performance | v | | | CA1846 | `int.Parse(s.Substring(6, 5))`, `sb.Append(s.Substring(6))`, `writer.Write(s[6..])` | `int.Parse(s.AsSpan(6, 5))`, `sb.Append(s.AsSpan(6))`, `writer.Write(s.AsSpan(6))` |
| CR0175 | Performance | v | | | | `s.Length >= 6 && s.Substring(0, 6) == "ORDER-"`, `s[..6] == "ORDER-"`, `s[^3..] != "MED"` under a length guard | `s.StartsWith("ORDER-", StringComparison.Ordinal)`, `!s.EndsWith("MED", StringComparison.Ordinal)` |
| CR0176 | Performance | v | | | | `foreach (var c in s.ToCharArray())` | `foreach (var c in s)` |
| CR0177 | Performance | v | | | | `foreach (var x in xs) { var label = tag + ":"; Use(x, label); }` | `var label = tag + ":"; foreach (var x in xs) { Use(x, label); }` |
| CR0178 | Performance | v | | | | `db.Orders.ToList().Where(o => o.Total > 0).Select(o => o.Id)` | `db.Orders.Where(o => o.Total > 0).Select(o => o.Id).ToList()` |

\*) Enabled by default. A blank cell means the rule is off until
`.editorconfig` turns it on (`dotnet_diagnostic.CRxxxx.severity = suggestion`)
or a run asks for it with `--codes`.

\*\*) The fix changes a public API — a signature, a type's shape — and is
applied only with `--api-changes`; without it the rule reports and, where a
declaration is private or internal, fixes that. An assembly nothing links
against (an executable) is the exception, and a library says the same with
`csharp_refactor.public_api = false`.

\*\*\*) Priority: a likely defect too costly to hold back. These notes print
without `--notes`, editors show them as warnings, and SARIF carries them at
warning level.

\*\*\*\*) The Microsoft analyzer rules this one shadows: when any of them is
enabled in the file's effective `.editorconfig`, this rule stands down for
those shapes, so nothing is reported twice.

A `—` under Offered fix means the rule only reports.

## Rule details

### CR0013 — correctness

A `default:` or `_ =>` arm of a `switch` over an enum that stands in for
one or two named members: the reader cannot tell whether `Blue` was meant
to share the default's behaviour or was forgotten. Note only in the sweep —
C# enums are open (any underlying value converts), so expanding the default
into named arms changes what an unnamed value does. The editor's expansion
names the hidden members on the arm (`case Color.Blue:` stacked, or
`Color.Blue or Color.Alpha =>`) and lets a new `default`/`_` throw
`ArgumentOutOfRangeException(nameof(x))`. Guards: the scrutinee is typed as
an enum declared in the compilation (a BCL enum's `None`/`Undefined` is
what a default is for), not nullable (its `default` also covers `null`), not
`[Flags]`; every named arm is a plain member constant, an `or` of them, no
`when`, no other pattern — with a guard or a type pattern the coverage is
unknowable; members are counted by value, so an alias of a covered value is
covered. F# twin: FR0072.

### CR0014 — correctness

A `switch` statement over an enum with no `default` whose every arm exits
the member or the loop (`return`, `throw`, `continue`), with members
unhandled: a value that matches nothing falls out silently and the code
after the switch runs. The compiler warns on the expression form (CS8509)
and says nothing here. Note; the editor's fix adds
`case Color.Blue: throw new NotImplementedException();` per missing member,
at most three, adopting the last arm's layout. The shared guards of CR0013
apply; an arm ending in `break` means the switch handles some members and
lets the rest through on purpose, and stands the rule down. F# twin:
FR0110.

### CR0015 — idiom

An index that only ever reads `xs[i]` is a `foreach`: `for (int i = 0;
i < xs.Length; i++) Use(xs[i]);` becomes `foreach (var item in xs)
Use(item);`, and a leading `var x = xs[i];` names the element and goes
(`foreach (var x in xs)`). The element is `var` where the enumeration yields
what the indexer returns; where it does not (`MatchCollection`'s public
`GetEnumerator()` yields `object`, `foreach` binds that pattern before
`IEnumerable<Match>`) the indexer's type is spelled: `foreach (Match match
in matches)`. Guards: `i` starts at literal `0`, steps by one
(`i++`, `++i`, `i += 1`), is bounded by `i < xs.Length` / `i < xs.Count` /
`i <= xs.Length - 1` on the very expression the body indexes; `xs` is a
local, parameter or `readonly` field (a property may hand out a fresh list
per read, and the `for` read it per iteration) typed as an array, `string`,
a span, or a type implementing `IList<T>`/`IReadOnlyList<T>`; every use of
`i` in the body is `xs[i]` (an index used as a value wants
`Select((x, i) => …)` and stays the author's call); no `xs[i]` is written,
incremented, taken by `ref` or passed `ref`/`out`; a mutable struct element
is never dereferenced (`ps[i].Bump()` mutates the array's element, a copy
after the rewrite); `xs` is not assigned or mutated through a known
mutator, and a list is not passed to any method in the body (`foreach`
throws on a list modified under it); a list held in a field is walked by
a body whose every call is to a core member and which constructs nothing
(`for (…; i < _pending.Count; …) Visit(_pending[i]);` with `Visit`
appending to `_pending` is a worklist the `for` walks to its end); the alias binder is dropped only when
never reassigned, else it stays a copy; the element name is the
collection's singular (`pages` → `page`, `entries` → `entry`), else `item`,
`item2`…, unused in the enclosing member. `break`/`continue` stay. Measured:
`foreach` over an array or `List<T>` compiles to the same loop. F# twin:
FR0101.

### CR0017 — correctness

A closure that captures the `for` variable and outlives the iteration sees
the final value: the `for` variable is one storage location for the whole
loop, unlike a `foreach` variable, which is per iteration since C# 5.
`for (int i = 0; i < 3; i++) actions.Add(() => Console.Write(i));` prints
`3 3 3`. Note only; the remedy is a loop-local copy. Fires only where the
closure demonstrably escapes: returned or yielded; stored through `Add`,
`AddRange`, `Insert`, `Push`, `Enqueue`, `TryAdd`, `Register`, `Subscribe`,
an event `+=` or an assignment to a field or a local declared outside the
loop; handed to `Task.Run`, `Task.Factory.StartNew`,
`ThreadPool.QueueUserWorkItem`, `new Thread`/`new Task`; or given to a
deferred LINQ operator whose chain is not materialised in the same
statement. A local holding the closure is followed to its own sinks. An
awaited `Task.Run`, an immediately invoked delegate, `List<T>.ForEach`, an
unknown callee, and a chain ending in `ToList()`/`Count()`/`Sum()`/a
`foreach` complete inside the iteration and stay quiet. CR0160 fixes the
same shape (the loop-local copy) and covers `while`/`do` binders too; where
it reports, this note stands down, so what remains here is the `for` shape
CR0160 cannot place a copy for.

### CR0020 — performance

An eager copy that a lazy stage or a consumer follows moves or goes:
`foreach (var x in xs.ToList())` enumerates `xs`; `xs.ToList().Any(p)`
becomes `xs.Any(p)`; `xs.ToList().Where(p)` becomes `xs.Where(p).ToList()`.
The copy exists to keep enumeration and mutation apart (the snapshot idiom
`foreach (var x in list.ToList()) list.Remove(x)`), so the rule refuses
outright where anything could mutate. Guards: the copy is
`Enumerable.ToList`/`ToArray` over a source that is not an `IQueryable`
(a query's copy runs the query and closes its reader: without it the loop
body runs over an open reader, and a moved stage changes what the
provider translates);
the source is a local or parameter that nothing else in the member
captures, hands to a call, aliases or reassigns (owned — a field can be
written by anything the body calls); the body or lambda never names the
source, never calls a mutating method on any receiver (`Add`, `Remove`,
`Clear`, `Insert`, `Push`, `Enqueue`, `Set…`, `RemoveAt`…), never sets an
indexer or member, holds no `await`; a source already a list or array
gains nothing and is CA1829/CA1860's; a short-circuiting consumer sees
fewer elements after the move, so the source is typed as a materialised
collection, or is a local whose initializer is a `callsOnlyCore` view of
one (`var seq = Numbers(); seq.ToList().Any(p)` keeps the copy: the
generator's remaining effects ran); a lazy stage moved before the copy runs its lambda at copy
time, so the lambda is pure too. Measured (PerfClaims): dropping the copy
before a consumer or a `foreach` wins on allocation (85× less) and the
`Where` move holds parity on time with a third of the allocation; the
`Select` move measured 3.4× slower (a list-backed `Select` is the fast
path) and is not offered. F# twin: FR0004.

### CR0021 — idiom

A running sum, count or string is the aggregate: `var total = 0m;
foreach (var x in xs) total += x.Price;` becomes `var total = xs.Sum(x =>
x.Price);`, `int n = 0; foreach (var x in xs) if (p(x)) n++;` becomes
`int n = xs.Count(x => p(x));`, `var s = ""; foreach (var x in xs) s += x;`
becomes `var s = string.Concat(xs);` (with a `Select` for a projected
piece). When `return total;` immediately follows and nothing else reads
the accumulator, the three statements collapse to `return xs.Sum();`. A
`using System.Linq;` is added where the file lacks it. Guards: the
accumulator is a local declared with the identity (`0`, `0L`, `0m`, `0.0`,
`""`, `string.Empty`) immediately before the loop, assigned only by that
one statement, read nowhere in the loop, never reassigned after; the loop
is a `foreach` over a real generic `IEnumerable<T>` (a non-generic or
duck-typed source takes no LINQ operator) with a plain loop variable, its
body exactly the one statement — for a count, the one `if` without `else`
around `n++`; the selector and predicate mention neither the accumulator
nor an assignment, and the predicate is pure through `callsOnlyCore`; the
summed expression's type is exactly the accumulator's; a sum or count
rewrites only over an array or a `List<T>` — measured (PerfClaims): `Sum()`
is 3–4× faster there (vectorised) and 4.7× slower over a lazy
`IEnumerable<T>`, where the loop stays; `string.Concat` wins 60× over any
source. Overflow:
`Enumerable.Sum` on `int`/`long` throws where `+=` wraps, so an integral
sum rewrites only under `CheckOverflow` or in a `checked` block;
`float`/`double`/`decimal` always. `Max`/`Min` are deliberately absent: the
loop leaves its seed on an empty source where `Max()` throws, and a `>`
loop and `Max()` disagree on NaN. A general combine stays a loop —
`Aggregate` is not clearer than `foreach`. F# twin: FR0050, FR0041.

### CR0022 — idiom

A flag set by a loop is `Any`, a flag cleared by one is `All`:
`bool found = false; foreach (var x in xs) if (p(x)) found = true;` becomes
`bool found = xs.Any(x => p(x));`; `bool ok = true; foreach (var x in xs)
if (c) ok = false;` becomes `bool ok = xs.All(x => !c)` with the condition
negated by CR0001's rule. Guards as CR0021's for the flag, the loop and the
body: the one `if` without `else`, whose then-branch assigns the
initializer's opposite, optionally followed by `break;`; the predicate is
pure through `callsOnlyCore` (short-circuiting must not skip an effect),
mentions no assignment, and fits one line; the flag is read nowhere in the
loop and never reassigned after. Off by default, by measurement
(PerfClaims): `Enumerable.Any(pred)` pays a delegate call per element where
the loop's compare is inlined — 6.5× slower on an `int[]` and 4.5× on a
`List<int>` when nothing matches, and slower than a loop with `break` on an
early match; it wins only when a match sits early and the loop has no
`break`. `dotnet_diagnostic.CR0022.severity` wakes it. F# twin: FR0107.

### CR0023 — performance

A startup-built list probed by `Contains` inside a loop or a collection
callback is a linear scan per probe; a set is a hash lookup: `static
readonly string[] Allowed = { "a", "b", "c" };` becomes a
`FrozenSet<string>` (`new[] { … }.ToFrozenSet()`, .NET 8+) or a
`HashSet<string>` (`new HashSet<string> { … }`), the `using` added.
Guards: the field is `readonly`, of array or `List<T>` type, initialised
from a literal, never reassigned or mutated anywhere in the compilation
(no `Add`/`Remove`/indexer set, never passed by reference — every tree is
read); the probe is a `Contains` call inside a loop or a lambda; the
element type has value equality (`string`, an enum, a struct, a record, or
a class implementing `IEquatable<T>`); every use in the compilation is a
probe, and the field is private or internal (the friend check applies) —
otherwise a note names the companion set (a public field is API). Measured
with the build cost charged against the probes. F# twin: FR0035.

### CR0025 — performance

Growing by one inside a loop copies everything each time, O(n²):
`arr = arr.Append(x).ToArray()`, `arr = arr.Concat(…).ToArray()`,
`Array.Resize(ref arr, arr.Length + 1)`, `imm = imm.Add(x)` on an
`ImmutableArray<T>`, `s += piece` or `s = s + piece` on a `string`. Note
only; it names the builder — `List<T>`, `ImmutableArray.CreateBuilder`,
`StringBuilder`, `string.Join`. Guards: string-ness is tested first, being
the selective test, and a numeric literal operand (`i = i + 1`) is never
a string append; the exact string shape CR0021 rewrites gets no note (the
fix is about to remove it); `ImmutableArray<T>` typed; a single
`Concat`/`Append` outside a loop is not reported. F# twin: FR0051, FR0104.

### CR0028 — idiom

A list filled by one loop and then only read is the pipeline: `var r = new
List<string>(); foreach (var x in xs) if (x.Ok) r.Add(x.Name);` becomes
`var r = xs.Where(x => x.Ok).Select(x => x.Name).ToList();`, `Where`
spelled only where there is a condition and `Select` only where the
projection is not the element. Off by default, by measurement (PerfClaims):
parity with half the allocation over an array or a lazy sequence, but 3.8×
slower over a `List<int>` and 1.7× slower for a bare `Select`;
`dotnet_diagnostic.CR0028.severity` wakes it. Guards: the list is declared
empty (`new List<T>()`, `new()`, `[]`) immediately before the loop with `T`
the declared element type; the loop is a `foreach` over a real generic
`IEnumerable<T>` whose body is the one `Add`, optionally under one `if`
without `else`; the condition and projection are pure through
`callsOnlyCore` and mention neither the list nor an assignment; the
projected expression's type is exactly `T` (a `List<IShape>` filled with
`Circle`s would become a `List<Circle>`, an `int` into a `List<long>` would
not infer); after the loop the list is only read — enumerated, indexed,
counted, passed by value — never added to, cleared, sorted or passed by
reference; no `await` or `yield`; the lambda captures no `ref struct`; no
comment or directive in the loop; a `using System.Linq;` is added where
missing. F# twin: FR0156.

### CR0029 — idiom

Two `Select`s in a row are one, and an identity `Select` is none:
`xs.Select(x => x.A).Select(a => a.B)` becomes `xs.Select(x => x.A.B)`,
`xs.Select(x => x)` becomes `xs`. Both forms are lazy and per element in
order, so nothing runs sooner or later; measured (PerfClaims) a third
faster fused — the runtime does not compose consecutive `Select`s for
free — so parity holds with margin. Guards:
both calls resolve to `Enumerable.Select` outside an expression tree (an
`IQueryable` chain, or a lambda inside `Expression<…>`, is a query
the provider translates); both lambdas are expression lambdas of one
parameter; substituting the first body for the second's parameter
duplicates nothing (the parameter is used once, or the first body is a
pure atom — a mutable field read is not one) and captures nothing (the
second body never spells the first parameter's name, the first body never
spells the second's); a non-atomic body is bracketed where it lands; the
identity form goes only where the receiver is typed `IEnumerable<T>` (on a
`List<T>` it changes the static type and drops a defensive copy). F# twin:
FR0137.

### CR0024 — performance

`foreach (var x in xs) acc.Add(x);` is `acc.AddRange(xs);` — one grow
instead of one per element. Guards: `Add` resolves to `List<T>.Add`
(typed); the body is that one statement, its argument the loop variable
as-is (a projected body measured no faster and reads worse); the receiver
is the same list on every iteration (a name or dotted read not mentioning
the loop variable — `columns[x].Add(x)` picks a list per element); the
source is not the list itself; no comment in the loop; the call must bind
to `AddRange(IEnumerable<T>)` — .NET 10's `AddRange(params ReadOnlySpan<T>)`
would take a non-generic `Array` source as ONE element; the source is a
collection (an array or `ICollection<T>`) — measured (PerfClaims): 7× faster
and half the allocation there, 2.9× slower over a lazy sequence, where the
loop stays; the speculative check settles the element conversion. F# twin:
FR0030.

### CR0026 — performance

`xs.Count()`, `xs.LongCount()`, `xs.ElementAt(i)`, `xs.Last()` inside a
loop on a receiver typed as a bare `IEnumerable<T>` walks a query or a
generator once per iteration; `Count()` in a `for` condition is per
iteration too. Note: materialise once before the loop. Guards: the call
resolves to `Enumerable`; the receiver's type is a generic enumerable that
is not a collection (an array, `ICollection<T>`, `IReadOnlyCollection<T>`
have a cheap count and are CA1826/CA1829's); the receiver is declared
outside the loop, so it is the same sequence every time (a lambda parameter
inside the loop varies, and a `foreach` source runs once and is not the
loop). F# twin: FR0102.

### CR0027 — correctness

A deferred `Enumerable`/`Queryable` operator as a statement —
`xs.Select(x => Log(x));`, `xs.Where(p);` — builds a query and runs
nothing; the note names the call inside that never runs. `_ = xs.Select(…);`
is a decision and stays quiet, as does any consumer (`ToList()`,
`ForEach`). Guards: the statement's expression is an invocation of a
deferred operator resolving to `System.Linq.Enumerable` or `Queryable`
(a user extension named `Select` is not this). F# twin: FR0076, FR0017.

### CR0030 — correctness

A parameter typed exactly `IEnumerable<T>` enumerated twice on one path —
`if (xs.Any()) foreach (var x in xs)` — runs a query or a generator
twice, and the signature promised nothing better. Note; the second site's
line is named. Guards: enumerations are a `foreach` over the parameter or
a consuming `Enumerable` call on it (`Any`, `Count`, `First`, `ToList`, …);
a site inside a lambda or local function defers and does not count; two
sites in the two arms of one `if`, or in different `switch` sections, are
one path each (an `if` condition and its `else` are one path); a
collection interface as the parameter type is not this note. Yields to
CA1851.

### CR0031 — performance

`new Random()` per call is `Random.Shared` (.NET 6+): no allocation, no
seeding, thread-safe. `new Random().Next(10)` becomes
`Random.Shared.Next(10)`; `var r = new Random();` whose every use is a
call receiver becomes `var r = Random.Shared;`. Guards: parameterless (a
seed is a decision) and no initializer; the type exactly `System.Random`;
an instance stored in a field, returned or passed is the author's;
`Random.Shared` resolves in the compilation; the bare name is spelled
where `System` is imported, else qualified.

### CR0032 — performance

`foreach (var k in d.Keys) use(k, d[k])` looks every key up again;
`foreach (var (k, v) in d) use(k, v)` reads the pair. Guards: `d` typed
`Dictionary<K,V>`, `IDictionary<K,V>`, `IReadOnlyDictionary<K,V>` or
`SortedDictionary<K,V>` and a pure read (a name or dotted read); the body
reads `d[k]` at least once and never writes it, passes it by reference or
assigns `k` or `d`; the value name is `value`, else `v`, else `<k>Value`,
unused in the enclosing member; deconstruction needs C# 7 and
`KeyValuePair<,>.Deconstruct` (.NET Core 2.0+); the speculative check
settles the rest.

### CR0033 — performance

`sb.Append(a + b + c)` builds the string and then copies it;
`sb.Append(a).Append(b).Append(c)` copies once. Guards: `Append` on
`System.Text.StringBuilder` with one argument that is a `+` chain typed
`string` through the built-in concatenation; the chain splits only where
the node is a string concatenation, so `n + 1 + a` keeps `n + 1` as one
operand exactly as C# evaluates it; an interpolated-string argument is
left alone (.NET 6+ handles it without an intermediate); every piece
appends the characters `+` produced (`Append(int)`, `Append(char)`,
`Append(object)` format as concatenation does, `null` appends nothing
either way); no piece reads a `StringBuilder` (`sb.Append("Len:" +
sb.Length)` read the length before appending, the chain would read it
after the first piece); no comment in the argument.

### CR0034 — correctness

A query typed `IQueryable<T>` that mentions the outer loop variable,
enumerated inside the loop — `foreach (var c in customers) foreach (var o
in db.Orders.Where(o => o.CustomerId == c.Id))` — is one statement per
row, the N+1. Note, priority (a likely defect too costly to hold back):
join, or batch the ids. Guards: the outer iteration is a `foreach` or a
lambda handed to a call (`customers.Select(c => db.Orders…)`); the inner
enumeration is a `foreach`, a consuming call, or an awaited `…Async` call
on a queryable that mentions the outer variable; an outer source that is
chunked or paged (`Chunk`, `Skip`, `Take`, `Batch`, `Page`), or an inner
filter by the batch (`ids.Contains(o.Id)`, `Skip`/`Take` of the outer
element), is a batch loop and stays quiet; an in-memory outer sequence is
the loop whether spelled as `foreach` or query syntax. F# twin: FR0028.

### CR0035 — performance

An iterator that yields from a `foreach` over its own recursive call —
`foreach (var c in Walk(child)) yield return c;` — or from
`children.SelectMany(Walk)`, nests one enumerator per level: every
element passes through O(depth) `MoveNext`s. Note: walk with an explicit
stack. Guards: the method holds a `yield` outside any lambda; the
`foreach` source or the `SelectMany` argument is the method itself (a
method group counts); a tail `return Walk(child)` without `yield` is a
redirect and never fires. F# twin: FR0058.

### CR0040 — correctness

A blocking drain inside an `async` body is an `await`: `var x = t.Result;`
becomes `var x = await t;`, `t.Wait();` becomes `await t;`,
`t.GetAwaiter().GetResult()` becomes `await t`, `Task.WaitAll(a, b);`
becomes `await Task.WhenAll(a, b);`, and `Task.Run(() => t.Result)` is
`t` itself (a cancelled task stays cancelled instead of faulting, which is
the more correct of the two). Outside an `async` body the same drain is
the boundary between the async and sync worlds and gets a note; the
sync-twin swap is the author's, and `GetAwaiter().GetResult()` is never
emitted by any rule. Guards: the receiver is typed `Task`, `Task<T>`,
`ValueTask` or `ValueTask<T>`; the site is not inside a `lock`, a `catch`
filter, a `finally`, an `unsafe` block, a non-`async` lambda or a local
function (each is its own boundary; only the innermost function attributes
a site); a `catch (AggregateException)` around the site holds the fix to a
note — `.Result` and `Wait()` throw the wrapper, `await` the inner
exception, and the handler would go dead — as does a `catch (Exception)`
or bare `catch` whose filter or body reads the wrapper (`InnerException`,
`InnerExceptions`, `Flatten`, `is AggregateException`); a task known complete is a read,
not a block: under its own `IsCompleted` test, born of `Task.FromResult`/
`CompletedTask` (through a local or a `readonly` field), after its own
`Wait(timeout)`, an `await` of it or of a `Task.WhenAll` naming it, or a
`WaitForExit()` in the same block; a body that
choreographs a thread by hand (a `Thread`, a signal, `Interlocked`) gets the
note only, since a bind moves the continuation off the thread the wait
kept it on; `Task.WaitAll(tasks, timeout)` stays; the spine of `Main` and
top-level statements is the console's blocking point and gets no note; the
`await` takes parentheses where it is a receiver (`(await t).Length`).
F# twin: FR0049. Yields to CA1849.

### CR0041 — correctness

A sync method draining a task at its boundary — `return Foo().Result;`,
`t.Wait();` — whose every caller sits in an `async` context becomes
`async Task<T>` (`void` → `async Task`), its drains `await`, its callers
`await`ed, and its name gains the `Async` suffix
(`csharp_refactor.CR0041.async_suffix = false` keeps the name). Guards,
the F# taskify's in the editor's strict form: the method is private
(internal under the friend check, public never); every caller — in this
file, in another file of the compilation, or (through the reference
oracle a host with a solution gives, under the api pass) in a friend
project — is a bindable statement of an `async` body — `var x = M(…);`,
`return M(…);`, `M(…);`, `x = M(…);` — not inside a lambda, a local
function, a `lock`, a `catch` or a `finally`, nor inside a `try` whose
handler catches the `AggregateException` the sync call threw (CR0040's
handler test: the awaited call throws the inner exception, and the
handler would go dead); every drain in the body is
one CR0040 could await (no known-complete read, no `AggregateException`
handler, no no-bind zone, no thread-choreographed body) and none sits in
a lambda inside it; no `out`/`ref` parameters, no iterator, no `unsafe`,
no override, virtual or interface implementation, no recursion, no `ref
struct` local, no mention as a method group anywhere in the compilation
(a delegate built from it would silently carry a Task); no member of the
type already carries the new name; one
unconvertible caller vetoes everything; the speculative check re-binds
the file. F# twin: FR0049 (taskify).

### CR0042 — correctness

A synchronous call inside an `async` body whose `…Async` twin exists is
the awaited twin: `reader.ReadLine()` becomes `await
reader.ReadLineAsync()`, `File.ReadAllText(path)` becomes `await
File.ReadAllTextAsync(path)`, `Thread.Sleep(n)` becomes `await
Task.Delay(n)`. Guards: the twin is proven from the typed tree — same
name plus `Async`, on the same type (or an extension in scope), the same
parameter types in the same order, a trailing optional
`CancellationToken` tolerated (CA2016 hands it the token on the next pass),
static-ness matching, and a return type that wraps the original's,
compared structurally (`void` → `Task`/`ValueTask`, `T` → `Task<T>`/
`ValueTask<T>`); the site is a statement, a `return`, the initializer of a
local or the right side of an assignment statement, in an `async` body and
not inside a `lock`, `catch`, `finally` or `unsafe`; never `Dispose` →
`DisposeAsync` (a `ValueTask` twin with nothing to await behind it) nor
`CancellationTokenSource.Cancel` → `CancelAsync` (only who runs the
callbacks changes), nor a `System.Xml` twin (it throws unless the reader
or writer settings opted in with `Async = true`), nor a LINQ twin over
`IQueryable<T>` (EF's `ToListAsync`/`FirstAsync`/…: only a provider that
implements the async query interface runs it, and an in-memory
`AsQueryable()` source — a test fake — throws at the call); `Thread.Sleep(0)` and
`Sleep(1)` are thread yields and stay. F# twin: FR0119. Yields to CA1849.

### CR0043 — correctness

`async void M()` that is not an event handler: no caller can await it and
an exception in it crashes the process. It becomes `async Task M()` and
its callers gain `await`. Guards: not `(object, EventArgs)`-shaped, not
subscribed to any event in the compilation (`+=`, `-=`, a delegate
constructor, a method group handed to a call, `nameof`), not an
override, virtual, abstract, partial or interface implementation, no
attribute (a handler-style attribute would
hand the runtime a Task it does not await); at least one caller, and every
caller is in this file, in an `async` context, as a statement — a call
from a sync context, a lambda or another file would become a silent
fire-and-forget, and a method with no caller in sight is called by the
framework, reflection or another assembly, which would get a Task nobody
awaits, so either vetoes the fix and the rule notes instead; the scope
gate (a public or protected method needs the public shape open, an
internal one the friend check). Yields to VSTHRD100.

### CR0045 — performance

A test that blocks on a task — `[Fact] public void T() { var r =
Load().Result; … }`, `Assert.Throws<E>(() => t.Wait())` — holds a worker
thread for its whole run; it becomes `public async Task T()`, the drains
`await`, the assert `await Assert.ThrowsAsync<E>(() => t)`. Guards: the
test attribute's declaring assembly is xUnit, NUnit, MSTest or TUnit (a
home-grown attribute with a reflection runner would get a Task nobody
awaits); the method is `void` or a non-`async` `Task`; every drain is
bindable by CR0040's proofs and sits in the test's own body; a test whose
return carries a value (NUnit's `ExpectedResult`) stays; shared state
vetoes — a test that writes a `static` of another type, or a
process-global (`Environment.CurrentDirectory`,
`Environment.SetEnvironmentVariable`, `CurrentCulture`, `Console.SetOut`),
keeps its blocking shape since an awaited test may overlap another; a
file that installs global state by reflection (`BindingFlags`) converts no
test; `Assert.Throws<AggregateException>(() => t.Wait())` asserts the
wrapper and stays. F# twin: FR0142.

### CR0044 — correctness

A `Task`-returning call as a statement in a non-`async` method —
`Task.Run(…);`, `SaveAsync();` — starts work nobody observes: on .NET Core
an unobserved fault is silently lost (it killed the process only on .NET
Framework 4.0), and a `try/catch` around the start catches nothing of the
work, which the note says. Quiet where the task is discarded on purpose
(`_ = …`), continued with `TaskContinuationOptions.OnlyOnFaulted`, or the
started lambda's whole body is a `try/catch`; inside an `async` method the
compiler's CS4014 says it. F# twin: FR0149, FR0017.

### CR0046 — performance

`async Task<T> M(x) { return await Inner(x); }` with nothing else in the
body is `Task<T> M(x) { return Inner(x); }` — one state machine and its
allocation fewer; `await Inner();` as the whole body becomes `return
Inner();`. Guards: the body is exactly `return await e;`, `await e;` or
`=> await e`; `e` is an invocation of a method the typed tree marks `async`
(an async callee never throws synchronously, so the one observable
difference — a synchronous throw where the caller held a faulted task —
cannot occur) with pure-atom arguments; no `ConfigureAwait`; no `using`,
`try`, `lock`, `foreach` or `await using`; the callee's return type is
exactly the method's.

### CR0047 — correctness

`lock (this)`, `lock ("cache")`, `lock (typeof(T))`, `lock (x.GetType())`, a
lock on a boxed value: a lock on something any other code can lock too
(`this` and a `Type` are reachable from anywhere, a string may be interned),
or on a fresh box every time (a value type). Priority. Note; the editor
offers a `private readonly Lock _gate = new();` (`object` where
`System.Threading.Lock` does not resolve or the file is below C# 13, and
`new object()` below C# 9), static when the lock was on a
type, inserted before the enclosing member, when the locked thing belongs
to this file by nature (`this`, a literal, `typeof`); a process-wide
singleton from elsewhere (`Console.Out`) gets the note alone. F# twin:
FR0046. Yields to CA2002.

### CR0048 — correctness

`Monitor.Enter(x); try { … } finally { Monitor.Exit(x); }` is `lock (x)
{ … }`. Guards: the single-argument `Enter` resolving to
`System.Threading.Monitor` (the `ref bool taken` overload carries protocol
and stays); identical operand text in `Enter` and `Exit`; the
`try`/`finally` immediately follows the `Enter`, with no `catch`; the
`finally` holds nothing but the `Exit`; no `await` or `yield` in the body
(illegal in a `lock`); no `Monitor.Exit`/`Enter`/`TryEnter` in the body (a
`lock` releasing an already released monitor throws); no comment on the
`Enter` line or the `finally`, both of which go; no directive in the
region. A bare `Enter` with no `try` in the rest of the block is the leak
note. F# twin: FR0123.

### CR0049 — correctness

A check-then-store on a `ConcurrentDictionary` races — two threads miss,
both compute, the later store wins: the miss arm of `if
(!cd.TryGetValue(k, out var v)) { v = Compute(k); cd[k] = v; }` becomes
`v = cd.GetOrAdd(k, _ => Compute(k));`. The hit path keeps `TryGetValue`
(measured: 2.1 ns and no delegate on the hit against 7.3 ns and 64 B for
`GetOrAdd` with a lambda). Guards: the receiver is typed
`ConcurrentDictionary<K,V>`; the miss arm is exactly `v = <expr>;` then
the store — `cd[k] = v;`, `cd.TryAdd(k, v);` or `cd.AddOrUpdate(k, v, (_,
_) => v);` — with the same key text; the key is a pure atom or a tuple of
atoms; the value type is not `Lazy<T>` (CR0050's subject) nor a delegate
(the lambda would be ambiguous with the value overload); a `Task` value
gets the note alone (the first failure would be cached, CR0050); the
factory mentions neither the binder nor a `ref`/`out` parameter or a `ref
struct` local (a lambda cannot capture them); the message carries the
`Lazy<T>` hint when the factory calls something — two racing factories
still both run, only the stored value is one. F# twin: FR0154.

### CR0050 — correctness

`cd.GetOrAdd(k, factory)` whose value type is `Task`, `ValueTask` or
`Lazy` caches a faulted value for good: the first failure becomes every
later caller's answer. Note: evict on failure, or cache a factory that
retries. Gated on the dictionary's value type argument; a factory that
merely throws caches nothing and is not reported. F# twin: FR0152.

### CR0051 — correctness

`Task<T> M() { using var x = …; return DoAsync(x); }` disposes `x` when
`M` returns — before the task that uses it completes. The method becomes
`async` and the return `return await DoAsync(x);`, so the `using` spans
the work. Guards: the method is not `async` and returns a task type; a
`using` declaration or statement in the body binds a name the returned
expression mentions; the returned expression is typed as a task and is
not `Task.FromResult`/`CompletedTask` (complete already); every other
return of the method binds too (`await`, `return;` for
`Task.CompletedTask`, the value for `Task.FromResult(value)`), returns
inside lambdas untouched; the message states the timing change — an exception while evaluating the call's
arguments now faults the task instead of throwing synchronously. This is
a correctness fix; the F# editor-only stance was for a different remedy.
F# twin: FR0150.

### CR0052 — correctness

`AppDomain.CurrentDomain.ProcessExit += (s, e) => Flush();` — a handler
capturing `this` on a process-wide or static publisher, never removed —
keeps the object alive as long as the publisher, which is the process.
Note. Guards: the publisher is `AppDomain.CurrentDomain.*`,
`Console.CancelKeyPress`, `SystemEvents.*`, a `static event`, or
`Subscribe` on a static `IObservable` whose `IDisposable` is dropped; the
handler captures `this` — a lambda mentioning `this` or a bare instance
member, or a method group of an instance method, plain or wrapped in a
delegate constructor; no `-=` of the same method group on the same
publisher anywhere in the type (a lambda can never be removed); a lambda
parameter shadowing the captured name suppresses the note; a publisher the
object owns (its own event, an event of a field) is a cycle inside one
lifetime and stays quiet. F# twin: FR0027.

### CR0053 — correctness

An `async` lambda converted to a `void`-returning delegate —
`list.ForEach(async x => …)`, `Parallel.ForEach(xs, async x => …)` — is
`async void` in disguise: nothing awaits it and an exception crashes the
process. Note: take a `Func<Task>` overload or a `Task.WhenAll` of the
started tasks. Typed: the lambda's converted delegate type returns `void`;
`Task.Run(async () => …)` and any `Func<Task>` target are fine, and so is
an event subscription (`x.Click += async (s, e) => …`) — the one place
`async void` belongs. Yields to VSTHRD101.

### CR0054 — performance

`Task.WhenAll(new[] { t })`, `Task.WhenAll([t])`, `Task.WaitAll(new[] {
t })` combine one task; only a literal one-element collection matches (a
variable holding one task is out of scope). The direct form changes the
result type (`Task<T[]>` → `Task<T>`), so the editor offers it and the
sweep does not; for `WaitAll` the element alone would drop the wait, so
the editor's form is `t.Wait()`. F# twin: FR0079. Yields to CA1842/CA1843.

### CR0055 — correctness

`Foo(x, CancellationToken.None)` or `Foo(x, default)` while a token
parameter is in scope hands the callee nothing to cancel on: `Foo(x, ct)`.
Guards: exactly one `CancellationToken` parameter on the enclosing
function (method, lambda or local function); the call resolves to a method
whose parameter at that position is a `CancellationToken`; never inside a
`catch` or `finally` (a cancelled operation must still roll back); never on
`Task.Run`/`Task.Factory.StartNew`, where the token is a scheduling
condition and an explicit `None` is the author choosing to always start
the work; never where the token is already among the arguments; never in a
call with named arguments; never where the `None` is bound to a name; never where a comment on the statement names the
choice (`None`, "cancel") or the call is the sole statement of a `try` whose
`catch` swallows — a best-effort cleanup that a client disconnect must not
cancel. F# twin: FR0118. Yields to CA2016.

### CR0060 — correctness

A disposable constructed into a local (`new` of a disposable type, or a
BCL factory's result — `File.OpenRead`, `MD5.Create`, `Aes.Create`,
`CancellationTokenSource.CreateLinkedTokenSource`…) and
never disposed becomes `using var`. Three tiers, decided by where the
binder's mentions send the value. FIX when it provably stays inside the
scope: every mention is an invoked member or property read whose result
is a plain value (void, a primitive, a string, an array or tuple of
those), a comparison operand, or a local bound to such a value; nothing
pending — a task or a lazy sequence obtained through it — outlives the
scope (an awaited, `.Wait()`ed or `.Result`ed task is done). NOTHING when
an escape is an ownership transfer: returned (also inside a tuple, a
collection or an initializer), handed to another disposable's constructor
(`new StreamReader(stream)` adopts it, unless `leaveOpen: true`), stored
where a holder beyond the scope keeps it (a field, a property, a
collection `Add`/`TryAdd`/`Enqueue`/`Push`/`Insert`, an outer local), or
disposed by hand anywhere in scope (`x.Dispose()`, `x?.Dispose()`,
`x.Close()`, `((IDisposable)x).Dispose()`, `using (x)`). NOTE ONLY, naming the destination, when a
mention could move the value somewhere whose ownership is unknown: handed
to a method by name (a same-file method is read one hop — a parameter it
disposes, `using`s or wraps is a transfer, one it keeps is the leak; a BCL
call returning a plain value only reads it), captured by a lambda or local
function, a method group handed on, a task obtained through it and
dropped or handed on (in flight past the scope), a
`CancellationTokenSource` whose token reaches anything but an awaited BCL
call (a user function is opaque; `Task.Run(work, ct)` hands it on). Never
a candidate: a self-active object (a timer, a watcher, a listener,
anything constructed with a callback, started, or subscribed to — `using`
would stop it on the way out); the no-ownership types (`HttpClient`,
`HttpRequestMessage` and the string/form/byte contents — nothing unmanaged,
and a test's handler mock reads them back after the send —
`MemoryStream`, `StringReader`/`StringWriter`, `SemaphoreSlim`); a wrapper
over a foreign resource (`new StreamReader(parameter)`, a field, a
property); a value already under `using`; an `IAsyncDisposable`-only type
(`await using` is v2). Under `Main` or top-level statements only a
flush-sensitive type (a writer, a stream, a transaction, a connection) is
lost work — .NET runs no finalizers at exit — and only those are reported
there. Only a declaration directly in a block is rewritten (a `switch`
section needs braces first). `using var` needs C# 8; below it the rule
notes. F# twin: FR0075. Yields to CA2000.

### CR0061 — correctness

A type that constructs a disposable into a field and does not implement
`IDisposable` cannot be released by its owner. Priority; note. Guards: the
field (or auto-property) is assigned a `new` of a disposable type, or a
BCL factory's result (`File.OpenRead`, `MD5.Create`…), in its initializer
or a constructor — an injected constructor parameter is the injector's;
an interface inheriting `IDisposable` counts as implementing; a disposable
base class turns the note into "override `Dispose(bool)`"; a member that
itself calls `Dispose`/`Close` on the field, or hands it to a call, is
manual management, not an ownerless resource; a disposable built with the
object itself (`new X(this)`) registers with it; the no-ownership types
(`HttpClient`, a shared lifetime; `MemoryStream`, `StringReader`,
`StringWriter` over a caller's buffer; `SemaphoreSlim` and
`ManualResetEventSlim`, whose wait handle is created lazily and whose
disposal the BCL calls optional) are not noted. The editor offers the
interface and a `Dispose` releasing every owned field (`field?.Dispose();`)
above the closing brace, FR0032's offer; a disposable base wants its
`Dispose(bool)` overridden instead, which is the author's. F# twin:
FR0032. Yields to CA1001.

### CR0062 — correctness

A `Dispose` that never releases one of the type's own constructed
disposable fields. Priority; note. Release means `Dispose`,
`DisposeAsync` or `Close` on the field, directly or through a cast, or the
field passed as an argument (whatever received it may release it);
`Cancel()` touches and frees nothing and gets its own wording; the
`Dispose` and `DisposeAsync` bodies are both read; a `base.Dispose()`
hand-off and a `System.Reactive` file (unsubscribe, not release) stay
quiet. The editor offers the release as the first statement of the
block-bodied `Dispose()`, FR0047's offer. F# twin: FR0047. Yields to
CA2213.

### CR0063 — correctness

`public void Dispose()` on a type not implementing `IDisposable` — through
a base type either, typed — nothing can `using` it. Note. F# twin: FR0148.

### CR0064 — correctness

A catch-all that swallows — `catch { }`, `catch (Exception) { return
null; }`, `catch (Exception e) when (flag)` never reading `e` — hides
every failure, the ones it did not mean too. Note; the editor offers
FR0055's three repairs, never a sweep: a divisor guard where the body is
one integer division by a name (`if (b == 0) return d; return a / b;`),
`catch (Exception ex) when (ex is IOException or
UnauthorizedAccessException)` where the body does file IO, and a log line
in the file's own logging idiom (`_log.LogError(ex, "M failed");`,
Serilog's `Log.Error`) as the handler's first statement; `TryParse` is
CR0166's. Not a swallow: a handler that reads the exception (logs it,
inspects it, filters on it) or rethrows; a comment on the handler, the
author's own acknowledgement; the `bool` probe idiom, the try body
answering `true` and the handler `false`; a `Try…` method returning
`false`; a returned value that carries the failure (an `Exception`, a
`Result`/`OneOf` error); a catch-all after an arm that rethrows
cancellation, or followed by an unconditional failure (`throw`,
`Environment.Exit`, `FailFast`); the one-call teardown idiom (`try {
x.Dispose(); } catch { }`) and a try around a probe that answers instead of
throwing (`File.Exists`), each named for what it is; test files.
`catch (SystemException)` is not a catch-all. F# twin: FR0055. Yields to
CA1031.

### CR0065 — idiom

`catch (E ex) { if (!Cond(ex)) throw; … }` is `catch (E ex) when
(Cond(ex)) { … }` — the filter runs before any inner `finally` and never
unwinds for an exception it will not handle. Guards: the guard is the
first statement of the handler, its then-branch exactly `throw;` (not
`throw ex;` — CA2200's), no `else`; the condition mentions the exception
and reads only its members, literals, locals, parameters and constants,
calling nothing but the BCL (a filter runs before inner `finally` blocks,
so an effectful condition would reorder effects); no existing filter; the
catch is the last of its try (a rethrow skips the sibling catches, a
declining filter lets them see the exception); no comment on the guard
line; the condition cannot throw — an exception inside a filter is
swallowed and the filter declines, where the guard in the handler threw
(`if (!(ex.InnerException.HResult == 5)) throw;` on a null inner
exception): members are read only on the exception, a value, a type or a
receiver the flow analysis proves non-null, `?.` is fine, no element
access, cast, division by a variable or call beyond a `string` method with
constant arguments — else a note; the condition is negated by CR0001's rule.

### CR0066 — correctness

A `throw` inside `finally` replaces whatever exception was in flight.
Priority; note. Throws the block itself catches stay quiet. F# twin:
FR0063. Yields to CA2219.

### CR0067 — correctness

A `throw` inside `Equals`, `GetHashCode`, `ToString`, `Dispose`, a static
constructor, a finalizer, an implicit conversion or `==`/`!=` — members
the runtime and the BCL call without expecting one. Note. Throws the
member's own `try` catches stay quiet. F# twin: FR0054. Yields to CA1065.

### CR0068 — correctness

`throw new NullReferenceException()`, `IndexOutOfRangeException`,
`OutOfMemoryException`, `StackOverflowException`,
`AccessViolationException`, `ExecutionEngineException`,
`ArrayTypeMismatchException`, `COMException`, `SEHException` — the
runtime's own exceptions, which a catch cannot tell from the real thing.
Note. A `switch` whose arms throw three or more distinct types is a
fault-injection table and stays quiet; plain `Exception` is CA2201's. F#
twin: FR0064. Yields to CA2201.

### CR0069 — idiom

`throw new InvalidOperationException("Rejected");` with a constant
message becomes `$"Rejected, calling {nameof(M)} with count: {count},
name: {name}"` — the values that led here, spelled as the literal was
(escapes intact). Guards: a constant string message with no `{`/`}`, not
verbatim or raw, not the one argument of `ArgumentNullException`/
`ArgumentOutOfRangeException` (a parameter name, not a message); the enclosing method has
one to four parameters whose types print usefully (primitives, `string`,
enums, `DateTime`, `Guid`, `decimal`, nullable of those — a class, an
array or a delegate prints its type name and does not count, and a record
prints every member it carries, a dump, and does not count either);
the message mentions no parameter as a word, is not the method's own
name, is not an invariant ("unreachable", "not possible", "NYI",
"internal error", "invalid case", "should not happen", "unhandled",
"unexpected") nor thrown as an `UnreachableException`; no secret-smelling
name on the method, its type, its namespace or a parameter (auth, session,
crypt, token, password, secret, credential, key, connection); the literal appears nowhere else in the compilation (a test
asserting on it); not a test file. Exception text is observable
behaviour: private methods always, internal under the friend check, public
under the api gate. The message says the rewrite puts argument values
into the text — a PII decision where the data is personal. F# twin:
FR0092.

### CR0070 — correctness

`catch (ReflectionTypeLoadException e) { Log(e.Message); }` — the
informative member goes unread: `LoaderExceptions`/`Types`;
`AggregateException` → `InnerExceptions`/`Flatten()`/`InnerException`;
`WebException` → `Response`/`Status`; `SqlException` → `Errors`/`Number`
(unverified in C#); `FileNotFoundException` → `FusionLog`/`FileName`.
Note; for the loader failure the editor offers every `LoaderExceptions`
message joined (`string.Join("; ", e.LoaderExceptions.Where(x => x !=
null).Select(x => x.Message))`, `using System.Linq` added) in place of
the one `e.Message` read, FR0151's offer. Reading the member anywhere in
the handler or its filter counts as informed; the tested type is resolved
by symbol, so a user type of the same short name never matches. F# twin:
FR0151.

### CR0080 — idiom

A `class` whose every instance member is a get-only or `init`
auto-property, a `readonly` field or a constructor assigning them — a data
holder, no methods and no accessor bodies (a type with behaviour is a
service or a domain object; a getter that lazily fills a static or computes
a value is behaviour) — is a `record`: value equality and `with` for free. The fix changes `class` to
`record` and nothing else (C# 9). Guards: no mutable instance state (a
`readonly` field and a get-only property cannot be assigned outside
construction, so no method can); no events; no base class other than
`object` (a record cannot derive from a class) and nothing deriving from
it (a class cannot derive from a record); no `Equals`/`GetHashCode`/
`ToString`/operator override; no attribute, no `static`, `unsafe` or
`partial`; reference identity is not relied on anywhere in the
compilation — `==`/`!=` between two instances, `ReferenceEquals`, `lock`
on an instance, use as a `Dictionary`/`HashSet` key, an argument or
element of a call that compares by equality (`Remove`, `Contains`,
`IndexOf`, `Distinct`, `Union`, `Except`, `Intersect`, `GroupBy`,
`ToLookup`, `ToDictionary`, `ToHashSet`, `SequenceEqual`, `Equals`) — a
record changes equality, which is the point and the risk; no instance is formatted —
an interpolation hole, a string concatenation, an explicit `ToString()`,
a conversion to `object` such as a log or console argument (a record
prints its members where the class printed its name: the log line would
change, and may leak); the serializer heuristic and
the entity check of CR0083; the scope gate: a public type converts only
where the public shape is open (`--api-changes`, a leaf compilation), an
internal one where no friend sees it, a private one always.

### CR0081 — performance

A `record` of at most four small unmanaged fields, 32 bytes in all, is a
`readonly record struct` (C# 10): no allocation, value equality kept. The
fix inserts `readonly … struct` around the `record` keyword and nothing
else. Guards: every instance field an unmanaged struct or enum (`int`,
`long`, `double`, `decimal`, `bool`, `Guid`, `DateTime`, `DateTimeOffset`,
`TimeSpan`… — no string, no reference, no generic parameter); no base
record, nothing deriving from it, no interface beyond the compiler's
`IEquatable<T>`, no attribute, not `partial` or `abstract`; not used as
`T?` anywhere (that spelling would change from a nullable reference to
`Nullable<T>`), not compared with or assigned `null`, not the operand of
`as`, `??` or `?.`, not a type argument for a `class`-constrained
parameter — in any file of the compilation — not converted to `object` or an
interface (boxing), not `lock`ed, not inside an expression-tree lambda
(`IQueryable` providers translate struct members differently); the scope
gate of CR0080. F# twin: FR0070.

### CR0082 — performance

`Tuple<int, string>` in a signature, field, property or local, and
`Tuple.Create(a, b)`/`new Tuple<int, string>(a, b)`, are `(int, string)`
and `(a, b)` (C# 7). All or nothing per tuple type and file: every
declaration of the type in the file must be safe — its shape open under
the scope gate (a local always), and every use simple — in this
compilation through the index, in another project through the reference
oracle (a host with a solution; without one an exported shape stands
down): an `Item1..ItemN` read (shared with
`ValueTuple`), a deconstruction, a return, an argument to a parameter of
the same tuple type declared in this compilation (retyped with it) — an
argument to a library's `Tuple<…>`, a type parameter, `object` or a call
that does not bind vetoes; any operator on the tuple — a null comparison
(`t == null`, `t is null`, `t?.Item1`), `t != _last` (a reference
comparison on `Tuple<…>`, an element comparison on the value tuple),
`is`/`as`/`??` — or a delegate-style call vetoes; a
spelling inside a generic argument (`List<Tuple<…>>`) is a different
shape and vetoes the type; at most four elements (a struct tuple is
copied by value). The rewrite retypes every spelling of the type in every
file of the compilation that spells it and unwraps every construction, as
one edit set reported from the first declaring file; the speculative check
re-binds every touched file in one fork. F# twin: FR0093.

### CR0083 — idiom

`{ get; set; }` whose every assignment in the compilation is an object
initialiser, a `with` expression, or `this.P = …` in a constructor of the
declaring type is `{ get; init; }` (C# 9, `IsExternalInit` resolvable).
Guards: at least one such write (a property nobody assigns is one a
serializer, DI or reflection sets); no `++`/`--`, no `ref`/`out` passing,
no deconstruction into it (`(x.P, y) = …`), no `nameof(P)` (a reflection
shape may follow); the setter carries no
accessibility of its own (a private setter is a different contract); not
static, virtual, abstract, an override or an interface implementation; no
attribute on the property or the type naming a serializer or ORM
(`Json…`, `Xml…`, `Column`, `Table`, `Bson…`, `DataMember`, `Key`…) and
no Entity Framework entity (`DbSet<T>` of the type anywhere) — those set
properties by reflection after construction; the scope gate of CR0080 on
the property's effective accessibility.

### CR0084 — correctness

`public static int Counter;` — a public mutable static written from two
or more sites in the compilation, or updated from itself (`Counter++`,
`Counter += n`), is shared state anything can race on. Note; the editor
offers `private`, and `readonly` where every write is a constructor's (a
method's write is what the note is about, and `readonly` would not
compile there). A set-once seam (one assignment site)
stays quiet, as do `private`/`internal`, `readonly` and `const` fields.
F# twin: FR0062. Yields to CA2211.

### CR0085 — correctness

`x.GetType().Name == "Customer"`, `x.GetType().FullName == "…"` — a type
test by name breaks on rename and misses derived types; `x.GetType() ==
typeof(T)` misses derived types where `is T` would not (unless exactly
`T` is the point). Note. Quiet inside the `when` guard of a `case T x`
(there the exact-type narrowing is deliberate), wherever it sits in the
guard. F# twin: FR0036.

### CR0086 — correctness

A constructor calling a `virtual` or `abstract` member of its own type —
or a field or property initializer doing so, which runs even before the
base constructor — runs the override before the derived constructor has
initialised its state. Priority; note. Guards: the member is
`virtual`/`abstract` (or a non-sealed `override`) declared on the type or
a base; a `sealed` class stays quiet (nothing can override); every call
or read through `this` (explicit or implicit) in constructor-time code
counts — assignment right-hand sides, loops, try blocks, initialisers —
but not one inside a lambda or local function (it runs later, if ever).
F# twin: FR0020. Yields to CA2214.

### CR0087 — performance

`status.ToString() == "Active"` on an enum is `status == Status.Active`:
no allocation, no cache lookup, and a rename survives. Guards: the
receiver typed as an enum, the literal equal to a member name
(case-sensitive), the enum not `[Flags]` (a combined value prints a
list), `ToString()` parameterless; no two members share a value (the text names
one of them, the value both); `!=` stays `!=`; the enum is spelled as
the site sees it; never inside an expression tree.

### CR0089 — idiom

A private type's `DateTime` field or auto-property written from
`DateTime.Now`/`UtcNow` and read only through parity members becomes
`DateTimeOffset` in one edit set — the clock keeps its offset, and
nothing downstream re-interprets a `Kind`. Off by default: a
serialization-shape change (`csharp_refactor.CR0089 = true` or `--codes
CR0089`). The strict envelope of FR0134: the type is a private nested
one; every write to a slot is `System.DateTime.Now`/`UtcNow` (confirmed
by symbol — a shadowing fake-clock type would take the rewrite, compile,
and switch to the real clock) or another slot of the migration; at least
one real write pins the clock; `Now` and `UtcNow` never mix across the
migration; every read is a parity member — a comparison or subtraction
against a slot or a clock read, `.Ticks`, `.Year`…`.Millisecond`,
`.AddDays`…`.AddYears`, `.Subtract`, `.CompareTo` — never `.Date`
(returns `DateTime`), `.Kind`, `.ToLocalTime()`, `.ToString()` in any
form (a `DateTimeOffset` appends its offset, `… +02:00`, even with no
format), a binding to a local or the
value handed to a call; every mention in this file. The slots and their
clock writes change together. F# twin: FR0134.

### CR0090 — correctness

The zero-argument Guid constructor — `new Guid()`, `new System.Guid()`, a
target-typed `new()` — is the classic .NET slip: it reads like "a new guid"
but produces `00000000-…`. The fix states the value as `Guid.Empty`
(identical value and type, so the sweep applies it freely, keeping the
qualification the source spelled); the editor also offers `Guid.NewGuid()` —
the likely intent, but a behaviour change only a human confirms.
Typed-gated to `System.Guid`, so a user type named `Guid` never matches;
`new Guid(bytes)` and friends are deliberate and stay. A parameter default
(`void M(Guid g = new Guid())`) and an attribute argument stay: a default
must be a constant, and `Guid.Empty` is not one (CS1736). A target-typed
`new()` is spelled `Guid.Empty` only where the file resolves `Guid`, else
`System.Guid.Empty`; the speculative check proves the rewrite binds. F#
twin: FR0136.

### CR0100 — idiom

`"Hello " + name + "!"` is `$"Hello {name}!"`. Guards: three or more
operands with at least one literal and one non-literal (a two-term `path
+ ".bak"` reads fine as it is); every `+` is the built-in string
concatenation (a user-defined `+` never rewrites), and only those nodes
split — `1 + 2 + "x"` keeps `1 + 2` as one hole, as C# evaluates it;
literals are regular (`@""` and raw strings cannot share an interpolation
with a regular one) and hold no `{`/`}`; an interpolated operand `$"…"`
joins with its text; a hole's type prints as `+` did — a primitive, an
enum, a `System` type, or a user type without `IFormattable` (the handler
formats an `IFormattable` through `ToString(format, provider)`, which a
user type may spell differently from `ToString()`); the chain sits on one line and the line it lands on
stays within the wrap column (`max_line_length`, default 120); a hole
holding a `"""` or a brace is refused, one holding a `:` or a `?:` is
parenthesised; with `DefaultInterpolatedStringHandler`
resolvable any hole type is fine, without it (netstandard2.0, net4x) an
interpolation with a non-string hole lowers to `string.Format`, so only
all-string chains rewrite there; never inside an expression tree (a
provider translates `+`); as an argument, the call must bind to the same
member once the string is interpolated — an interpolated string converts
to `FormattableString`, `IFormattable` and handler types as a `string`
does not, so EF Core's `ExecuteSqlCommand(FormattableString)` would take
it over `(RawSqlString)` and parameterise the holes, a table name among
them. F# twin: FR0031.

### CR0101 — idiom

`string.Format("{0} of {1:N2}", a, b)` is `$"{a} of {b:N2}"`. Guards: a
literal format bound to the `string` overload (no `IFormatProvider` —
culture is a decision); the arguments are passed one per placeholder,
never as one `params` array (its count is invisible); every index within
range and every argument used (CA2241's business otherwise); format and
alignment carried into the hole, `{{`/`}}` kept, a verbatim template
stays verbatim (`$@"…"`); a regular string-literal argument of a regular
template is spliced in as text, not a hole (`{"label"}` reads as a
mistake; the two literal kinds share escapes only when neither is
verbatim, and a hole with alignment or format keeps its argument); an
argument used twice must be a pure atom; no named or `ref` arguments; a
call laid out over lines becomes one line, so it must fit the wrap column
(`csharp_refactor.CR0101.wrap_column`, else `max_line_length`, else 120);
the handler gate and the same-overload guard of CR0100. F# twin: FR0042.

### CR0102 — performance

`$"{x.ToString()} items"` is `$"{x} items"` and `string.Join(", ",
xs.Select(x => x.ToString()))` is `string.Join(", ", xs)`: the handler
and `Join<T>` format the value themselves, without the intermediate
string. Typed: the `ToString()` is parameterless, the hole has no format
or alignment, and the receiver cannot be null — a non-nullable value
type, or a reference the nullable context declares not null
(`null.ToString()` throws where `{null}` prints nothing) and of a type
that prints as `ToString()` does (a user `IFormattable` stays, as in
CR0100); the `Select`
resolves to `Enumerable.Select` and its lambda is `x => x.ToString()`.
F# twin: FR0021.

### CR0103 — cosmetic

`$"no holes"` → `"no holes"` — hole-free interpolation; the `$` does
nothing. The verbatim spellings `$@"…"` and `@$"…"` lose their `$` the
same way. Skipped when the text holds `{{` or `}}` (those escapes would
need unescaping), for raw string literals, and wherever the site expects
something other than a plain string: a `FormattableString`/`IFormattable`
target, or a parameter that only accepts an interpolated-string handler
(there a plain string is a different conversion, or none). A hole-free
`$"…"` is a constant string, so a method offering both a `string` and a
handler overload binds the `string` one and the fix is safe. With a
semantic model the converted type answers; without one, any argument,
`return`, typed declaration or assignment keeps its `$`. F# twin: FR0086.

### CR0104 — idiom

`x == null || x == ""` is `string.IsNullOrEmpty(x)`, `x == null ||
x.Trim() == ""` is `string.IsNullOrWhiteSpace(x)`, and the `&&`-negated
forms (`x != null && x != ""`, `x is not null && x.Length != 0`) are the
`!` of those. Guards: the subject is an identifier or a dotted pure read,
the same symbol on both sides; the emptiness test is `== ""`, `==
string.Empty` or `.Length == 0` (the `Trim()` spellings likewise,
`Trim()` with no arguments stripping exactly the `IsWhiteSpace` set); the
null test leads — a `Trim()` spelling that leads throws on null today, so
that order is editor-only; `string.IsNullOrEmpty(x.Trim())` is
`IsNullOrWhiteSpace(x)` editor-only for the same reason; never inside an
expression tree (the translator may know `||` and `==` but not
`IsNullOrEmpty`). F# twin: FR0138.

### CR0105 — correctness

`double.Parse(s)`, `DateTime.Parse(s)` without a provider read the
machine's culture — a decimal comma here, a day-first date there. The
editor offers `CultureInfo.InvariantCulture` (primary) and
`CultureInfo.CurrentCulture` (today's behaviour, spelled out); the CLI
applies the invariant one only under `csharp_refactor.CR0105.invariant =
true`, culture being a decision. Guards: `Parse`/`TryParse` on
`double`/`float`/`decimal`/`DateTime`/`DateTimeOffset`/`TimeSpan` (integer
parses stay quiet), bound to an overload without an `IFormatProvider`;
only the one-argument `Parse` gains the provider — a `TryParse` needs a
styles argument the rule will not guess and stays a note; `CultureInfo`
is spelled short under an existing `using System.Globalization` and fully
qualified otherwise; inside an expression tree the whole suggestion
stands down (a LINQ provider resolves `Parse` by signature, and the
two-argument overload can turn a translatable call into a runtime
`NotSupportedException` that compiles clean). F# twin: FR0067. Yields to
CA1305.

### CR0106 — correctness

`DateTime.Now` is a local clock that jumps at DST and differs per
machine; `DateTime.UtcNow` is the instant. Fix under
`csharp_refactor.CR0106.utc_now = true` (editor always) where `Now` is
read as an instant — compared, stored, subtracted, `.Ticks`,
`.ToBinary()`, `.ToFileTime()`, handed to a call. Notes only where `Now`
is read as a calendar (`.Date`, `.Day`, `.Month`, `.Year`, `.DayOfWeek`,
`.Hour`, `.ToString(…)`, `.ToShortDateString()`, `.AddDays(…)` — swapping
the clock underneath manufactures the first bug), and for
`DateTime.Today` and `DateTime.UtcNow.Date` (a calendar day cut at one
zone's midnight). Quiet: `DateTimeOffset.Now` (it carries its offset);
`dt.Date != DateTime.Today` where the other side is a `.Date` this
machine produced (the same clock on both sides); `Now` handed to a setter
that wants local time (`File.SetLastWriteTime`, `SetCreationTime`,
`SetLastAccessTime`); an expression-tree translator's arm — the nearest
enclosing `switch` or `if` naming `"Now"`/`"Today"` in its condition is a
translation table, not a clock read. F# twin: FR0121.

### CR0107 — correctness

`new Regex("(unclosed")`, `Regex.IsMatch(s, "[")` — a literal pattern the
engine rejects is a guaranteed `ArgumentException` at the first call.
Priority; note. The check constructs the pattern with the call's own
constant `RegexOptions` (a pattern legal under `IgnorePatternWhitespace`
may be illegal without it); non-constant options stand down; only the
engine's `ArgumentException` is proof — any other exception (a
`NotSupportedException` for a backreference under `NonBacktracking`)
leaves the pattern unproven, and CR0109 keeps it unhoisted. F# twin:
FR0122.

### CR0108 — performance

`Regex.IsMatch(s, "^abc")` is `s.StartsWith("abc",
StringComparison.Ordinal)`, `Regex.IsMatch(s, "abc")` is
`s.Contains("abc")`, and `Regex.Replace(s, "abcd", "x")` is
`s.Replace("abcd", "x")`, `Regex.Matches(s, "ab").Count` is
`s.AsSpan().Count("ab")` (where `MemoryExtensions.Count` resolves: .NET 8, and
the subject is known not null — `Regex.Matches(null, …)` throws where the
span of a null is empty)
and `Regex.Split(s, ", ")` is `s.Split(", ")` (where the single-string
`Split` resolves: .NET Core 2.0) — for a pattern that is plain text; the
span count and the split count and split on every non-overlapping
occurrence, empties kept, as the regex does. Guards: the
pattern (any literal spelling, `@"…"` included) holds no metacharacter and
no backslash, quote or control character (it is re-emitted verbatim, and
the decoded text would need re-escaping); an empty pattern is refused; a
`$` anchor anywhere is refused (it also matches before a final newline,
which `EndsWith` cannot say) and so is a `Replace` with an anchor or a
`$` in its replacement; no `RegexOptions`, `MatchEvaluator` or timeout
argument, no named or `ref` arguments; the `StartsWith` overload is the
ordinal one (the single-argument form is current-culture and differs on
ligatures, ignorable characters and Turkish i), `using System;` added
when needed; `Contains(string)` and `Replace(string, string)` are ordinal
already. The rewrite subsumes CR0109's hoist on the same site, and a
plain-text pattern under no options is never hoisted, rewritten or not
(`Regex.Replace(s, "ab", "$1")`, `Matches` enumerated): the regex over
plain text is the thing to lose, not to cement into a generated one. F#
twin: FR0015.

### CR0109 — performance

A `Regex` built from a literal inside a method, accessor, local function
or lambda body: a construction is parsed on every call (twelve times the
cost of the match and 2.6 KB, measured in PerfClaims), a static call is
served from the runtime's cache of fifteen patterns until the cache turns
over, and either runs the interpreter where the generated regex runs its
own code, nearly three times faster (a field initializer or a
constructor builds once per object and stays). With `[GeneratedRegex]`
resolvable and every type of the containing chain declared in this file,
the pattern becomes `[GeneratedRegex("lit")] private static partial Regex
LitRegex();` and each type in the chain gains `partial`; otherwise — and
under an interface or a generic container, where the generator does not
apply — a `private static readonly Regex LitRegex = new Regex("lit");`
field. The
declaration lands above the enclosing member's leading comment block and
never under a directive — a field, though, above the first static field
or property initializer that precedes the member (static initializers run
in text order, and one above could reach the member during type
initialization while the field is still null); a construction becomes the reference, a static
call an instance call with the pattern dropped (`Regex.Replace(s, "p",
"r")` → `PRegex().Replace(s, "r")`); a call carrying a timeout stays, a
construction carrying one hoists whole as a field. Guards: a literal
pattern the engine accepts (one it rejects is CR0107's: hoisted into a
generated regex it would fail the build, where the call only threw when
reached) that is not plain text under no options (CR0108's, rewritten or
not) and constant options, on one line, not inside an expression tree;
the name comes from the local the result is bound to (`var emitted =
Regex.Matches(…)` → `EmittedRegex`; a local named for the type itself —
`regex`, `rx`, `pattern` — names nothing), else the pattern's words of
three letters or more (`ErrorCodeRegex`), else the enclosing member's
name, numbered (`ARegex2`) where the type already has that member or
another hoist claimed it; one pattern gets one member — a second site of
the same pattern and options in the type refers to the first's, and a
static `Regex` field or generated method the type already declares for
the construction is used as it stands; a bare `Regex` resolves only
under a `using` that precedes the insertion point, else the name is
qualified. The generator supplies the partial method's body, so the
speculative check tolerates that one pending error. F# twins: FR0015
(hoist), FR0037. Yields to SYSLIB1045.

### CR0110 — performance

`new HttpClient()` per call or in a loop exhausts sockets and ignores DNS
changes — the note names the lifetime question (one instance per
application, or `IHttpClientFactory`) rather than a fix; a
`JsonSerializerOptions` built per iteration, and `MD5.Create()`/
`SHA256.Create()`/`Aes.Create()`/`SearchValues.Create(…)` inside a loop,
are allocated per pass. A field or property initializer is once per
object and stays quiet, as does a factory registered once
(`AddSingleton(_ => new HttpClient())`, a `Lazy<T>`) and a test file (a
client per test case is not the lifetime question). F# twin: FR0037. Yields to CA1869, CA1870.

### CR0111 — idiom

`dir + "\\" + file`, `dir + "/" + file` — a path joined by hand;
`Path.Combine` (or `Path.Join`) spells the join for the platform. Note
only: `Path.Combine` treats a rooted second argument as absolute and
discards the first, which the concatenation does not. Guards (FR0081's,
after its false positives): both separators need positive path evidence —
a path-flavoured name in the chain (`dir`, `path`, `file`, `folder`,
`directory`, `root`, `home`, `temp`), a rooted literal (`C:\…`,
`/usr/…`), an extension-bearing literal (`.txt`), or a literal naming a
path that exists on this machine; a separator only joins with text on
both sides (`dir + "/"` appends a marker, `"/" + name` prefixes a root);
dot-segments (`"./" + p`, `p + "../"`) are relative-path notation
`Path.Combine` cannot spell; a chain opening with a forward-slash literal
(`"/img/" + id`) is as likely a web route and needs the stronger evidence
— a name is too weak there; URL-shaped chains never fire, nor a chain a
local binds one hop to something URL-shaped, nor a file named for URLs,
routes or JSON; a chain only compared or searched for is a key; `const`
initialisers and attribute arguments never get the note. F# twin: FR0081.

### CR0112 — correctness

Invisible or direction-changing characters in source: the bidi controls
(U+202A–U+202E, U+2066–U+2069 — the "Trojan Source" shape, where a
comment or literal reads one way and compiles another), the Unicode tag
block (U+E0000–U+E007F — invisible text an LLM prompt can smuggle),
zero-width spaces and joiners in identifiers (U+200B, U+200C, U+200D,
U+2060, U+FEFF), and a byte-order mark anywhere but the very start.
Inside a regular string literal (or the text of a `$"…"`) the character
becomes its `\uXXXX` escape — the same string, spelled so a reader sees
it; everywhere else — a comment, an identifier, a verbatim or raw literal
where escapes do not exist — a note. ZWJ/ZWNJ inside a literal are exempt
(emoji sequences, Arabic and Persian text). F# twin: FR0125.

### CR0113 — correctness

`balance + 2_000_000_000`, `seconds * 1_000_000` on an `int` — a literal
within a factor of sixteen of the ceiling makes the wrap likely for
ordinary operands; `long` literals within a factor of sixteen of theirs
likewise (ten-digit `long`s are ids, eighteen-digit ones are magnitudes);
`int.MaxValue + e` / `MinValue - e` overflow for every `e` but zero, while
`MaxValue - e` is the sentinel arithmetic `Random` does on purpose and
stays quiet. Note; the editor offers `checked(…)` (the widening — every
operand to `long`, back through `checked((int)…)` — is the author's).
Decimal spellings only; unsigned skipped; an expression under `checked`,
or a compilation with `CheckOverflow` on, is left alone. F# twin: FR0105.

### CR0114 — correctness

A log message template that does not fit its arguments: a placeholder
name used twice (`"{Id} then {Id}"` — each occurrence binds the next
argument, so the second reads the wrong value or none), more placeholders
than arguments or more arguments than placeholders, or an interpolated
string where a template was expected (the holes are formatted before the
logger sees them, no property is captured, and every call is a new
template). Typed `Microsoft.Extensions.Logging` (`Log*`) and Serilog
(`Verbose`…`Fatal`), the template being the `message`/`messageTemplate`
parameter. The template may be a `"…" + "…"` chain of literals; a
trailing array literal is the params array spelled out; one trailing
array-typed argument may be the params array passed whole — its count is
invisible, so no arity claim; a hole-free `$"…"` compiles to a constant
and is not the interpolation defect; placeholder syntax `{@Name}`,
`{$Name}`, `{Name:format}`, `{Name,align}`, `{{` literal. Note. F# twin:
FR0124. Yields to CA2017, CA2254.

### CR0115 — correctness

`catch (Exception ex) { _log.LogError("sync failed {Id}", id); }` — the
exception is caught, the log line loses it. The exception-first overload
takes it: `_log.LogError(ex, "sync failed {Id}", id);`. Guards: typed
`Microsoft.Extensions.Logging` or Serilog; the call sits in a `catch`
that binds the exception, not inside a lambda within it; ANY mention of
the exception in the arguments counts as handled, `ex.Message` included
(a PII choice the rule must not escalate); the template is in first
position — an `EventId` first wants the exception second, and that shape
is a note; the overload with the exception first exists on the callee's
type; no named arguments. F# twin: FR0120.

### CR0001 — idiom

A condition spelled out as `true`/`false` in the statement shapes IDE0075
(the ternary rule) does not cover: `if (c) return true; return false;` and
`if (c) return true; else return false;` become `return c;`; the swapped
literals become `return !c;` (an existing `!` unwraps, a comparison flips
its operator where that is exact — never an ordering on a floating or
nullable operand, where NaN and null compare false both ways — and a
non-atomic condition gains parentheses); `if (c) x = true; else x = false;` becomes
`x = c;`. Gated on `c` being `bool` (a user-defined `true`/`false`
operator is a call), the `return false` or the `else` being the IMMEDIATE
sibling, the assignment target being the same local or field on both sides
(a property setter may act, and each arm ran it once), the `if` not being
another `if`'s `else` clause (the bare expression would glue onto the
chain), and no comment or directive between the two statements. F# twin:
FR0010.

### CR0002 — idiom

An `if`/`else if` chain comparing one scrutinee against constants is a
`switch`: the scrutinee is read once where the chain read it per
comparison, the arms read as a table, and the compiler rejects a repeated
constant. Where every branch is one `return e;` (or `x = e;` on one
target, or `throw e;`) and a terminal `else` closes the chain, the fix is
a `switch` expression (C# 8; `2 or 3 =>` from C# 9); otherwise a `switch`
statement with `case 2: case 3:` stacked and `default:` for the `else`,
each body ending in `break;` where control reached its end. Guards: the
scrutinee is a local, parameter or `readonly` field read by its plain
name; every comparison is `==` (either way round) against a compile-time
constant through the built-in operator, on an integral, `char`, `string`,
`bool` or enum scrutinee (nullable of those included); a chain link may
be an `||` of such comparisons; three comparisons at least — two read
fine as `if`/`else`; no constant repeats; the expression form keeps every
arm's conversion to the target (arms of one type, a natural type equal
to the target, or a target-typed switch: `1` and `2L` returned as
`object` keep the statement form). The shared switch guards of
CR0003 apply to the statement form. F# twin: FR0112.

### CR0003 — idiom

An `if`/`else if` chain of type tests on one subject, each branch casting
the subject to its tested type, is a `switch` over type patterns: the cast
becomes the pattern variable and the throwing accessor goes. A leading
`var c = (Circle)s;` names the binder and is dropped; an inline `((Rect)s)`
or `(s as Rect)` becomes a name from the type unused in the enclosing
member; `s is int n` keeps `n`; `s is null` and `s is not null` (C# 9)
become `case null:` / `case not null:`; a branch with no cast is
`case T:` (C# 9, `case T _:` below); a plain `else` is `default:`.
Guards: two links at least, every one on the same plain identifier; every
cast in a branch targets that branch's own type (a cross-cast keeps the
chain); the branches never assign the subject; the binders differ. Shared
switch guards, which CR0002 uses too: only the head of a chain fires; a
`break` not inside a nested loop or switch, or any `goto`, keeps the chain
(after the rewrite it would leave the switch); a body's reachable end
gains `break;` by the compiler's own flow analysis; locals declared in two
branches under one name keep their braces (two scopes would become one);
no comment or directive outside the bodies; no multi-line literal in a
body; the speculative check settles the rest. F# twin: FR0103.

### CR0004 — idiom

A `HasValue` test whose positive branch reads `.Value` is the pattern
`x is { } v`, which puts the payload in scope only where it exists and
retires the throwing accessor. Three shapes: the conditional
`x.HasValue ? x.Value + 1 : 0`, the statement `if (x.HasValue) Use(x.Value);`
(with or without `else`), and the chain `x.HasValue && p(x.Value)` — where
`||` wants the negated test and becomes `x is not { } v || p(v)` (C# 9).
`!x.HasValue`, `x == null` and `x != null` are the same test with the
branches swapped. Guards: the receiver is a name or dotted read typed
`Nullable<T>` (a user-defined `HasValue` never matches); the positive
branch reads `x.Value` at least once and the negative branch never (that
code throws today and is not ours); the binder is the `var name = x.Value;`
line heading the branch (which then goes), else `v` for a plain local or
the member's own name for a dotted read (`bound.Min` → `min`), then
`<name>Value`, then `value`, never a name the enclosing member uses and
never one an earlier site in the same member took (a pattern variable
declared in a statement is in scope for the rest of its block); the receiver
is not assigned in the branch; a pass-through payload
(`x.HasValue ? x.Value : d`) is IDE0270's `??` and stays; nothing moves
inside an expression tree, where patterns cannot go; C# 8 is required.
F# twin: FR0016.

### CR0005 — idiom

Nested ifs merge into one `&&`, in the two shapes that preserve semantics
exactly: identical `else` blocks (textually, comments included — one of
the branches runs either way, so even an effectful `else` is unchanged),
and no `else` at all. The tempting third shape — an inner `if` without
`else` while the outer has one — is deliberately absent: the merge would
run the `else` where the original ran nothing. The inner `if` must be the
only statement of the outer `then`; both conditions are `bool` through
built-in operators; an `||`-topped condition gains parentheses before
joining the `&&`; no comment or directive lies in the outer statement
outside what the replacement keeps (the inside of the two conditions and
the inner `if` after its `)`) — one beside the outer `)` in Allman style,
above an unbraced inner `if`, on a brace or on the outer `else` holds the
fix; the moved lines hold no
multi-line literal and move left by the block's indentation; a merged
condition that would run past the wrap column (`csharp_refactor.CR0005.wrap_column`,
else the file's `max_line_length`, else 120) keeps the nesting — two
`TryGetValue` guards joined made a 170-column line. F# twin: FR0113.

### CR0006 — idiom

A long happy path under an `if` whose short `else` exits reads better as
a guard clause: `if (ok) { twenty lines } else { return; }` becomes
`if (!ok) { return; }` followed by the twenty lines one level left. Off by
default — happy-path-first is a house style too; `csharp_refactor.CR0006.then_at_least`
(20) and `else_at_most` (3) set the line counts. Guards: a plain
`if`/`else` pair of two blocks (a chain is a different rewrite); the `else`
ends in `return`/`throw`/`continue`/`break`; the condition is `bool`, and
its negation unwraps an existing `!` or flips a comparison where that is
exact (CR0001's rule); the `if` is a statement of a block, so the freed
lines become its siblings; no comment or directive outside the two blocks
(the `else` line goes); no multi-line literal in the moved lines; no
`using var` declared directly in the `then` block (freed into the
enclosing block it would be disposed later, past the code after the old
`if`); the speculative check holds the fix where a local of the `then` block would
clash with a later sibling scope. F# twin: FR0114.

### CR0016 — idiom

A loop steered by a `bool` flag keeps running the rest of its body after
the decision is made: in `while (!done && …) { if (x) done = true;
Process(); }` the `Process()` runs once more. C# has `break`; the decision
is the place to leave. Off by default; note only. The note counts the
statements that still run after the raise — those after it in its block
and after each enclosing statement up to the loop body — and says nothing
when there are none. Guards: the flag is a `bool` local declared before
the loop; the loop condition names it negated (`!done`, `!(done || other)`,
`… && !done`); the body assigns it `true` as a statement. F# twin: FR0141,
whose remedy is recursion where C#'s is the keyword.

### CR0007 — idiom

`x && true`, `true && x`, `x || false`, `false || x`: the literal
contributes nothing, the expression IS the other operand. `x && false` and
`true || x` stay — their value is constant but `x`'s evaluation (and its
effects) changes. Both operands must be `bool` through the BUILT-IN
operator: a `dynamic` operand binds at run time, and `d && true` is
`dynamic` where `d` alone is too. Runs inside expression trees
deliberately: removing a node leaves a strictly simpler tree of shapes the
translator already accepted. F# twin: FR0108.

### CR0008 — idiom

`a || a` and `a && a` collapse to `a` — only when the operands are
textually identical AND provably pure (locals, fields, BCL properties,
array indexing, built-in operators; any call disqualifies):
short-circuiting means `a || a` evaluates `a` twice on the false path, and
`TryConnect() || TryConnect()` is the deliberate retry idiom. The message
also names the likelier truth: a duplicated operand is usually a copy-paste
that meant another name. Runs inside expression trees, as CR0007. F# twin:
FR0109.

### CR0009 — idiom

Adjacent switch sections with textually identical bodies stack their
labels; adjacent switch-expression arms with identical bodies fold into
one `or` pattern. Match order is semantics, so only a CONTIGUOUS run
merges, in place. Patterns must bind nothing (a designation or `var`
refuses; `default` is never stacked, nor the discard arm `_` of an
expression folded into `"A" or _` — the explicit arm before a catch-all is
usually deliberate), carry no `when`; a comment inside a
dropped body — or between dropped arms — holds the fix, while each kept
label keeps its own comment. A run of arms whose one-line fold would pass
the wrap column (`csharp_refactor.CR0009.wrap_column`, else `max_line_length`,
else 120) takes one pattern per line, the `or`s indented under the first.
F# twin: FR0117.

### CR0010 — idiom

A `when` guard that only equality-tests the arm's own `var` binder against
a constant IS the constant pattern: `case var x when x == "A":` becomes
`case "A":`, `var v when v == 3 =>` becomes `3 =>`; either operand order.
Gated on the guard being EXACTLY that comparison through the built-in
`==`, the constant being one a pattern can spell (a literal, a `const`, an
enum member), and the body never mentioning the binder — after the rewrite
it no longer exists. F# twin: FR0129.

### CR0011 — idiom

Term-rewriting hints over the syntax tree, in the FSharpLint tradition: a
hint is one line `lhs ===> rhs`, both sides ordinary C# expressions, where
a single lowercase letter binds any expression, a single uppercase letter a
type, and a lambda's single-letter parameter the name of the matched
lambda's parameter. The built-in set, curated for exactness, each with the
typed proof it needs:

- `!(a == b)` → `a != b`, `!(a != b)` → `a == b`; `!(a < b)` → `a >= b`
  and the other orderings, where neither operand is `float`/`double`/`Half`
  or a `Nullable<T>` (`!(x < 0)` is true for NaN and for a null `int?`,
  `x >= 0` is not — a lifted comparison is false both ways); `!(!x)` → `x` on a `bool`.
- `x == true` → `x`, `x == false` → `!x` and the six other spellings, where
  `x` is exactly `bool` — on `bool?` the comparison is a null-safe test
  (yields to IDE0100).
- `a.CompareTo(b) < 0` → `a < b` and the five other results, where both
  sides are primitive, enum, `decimal`, `DateTime`-like or `Guid` — types
  whose `CompareTo` and operators agree; never `string` (culture) or
  floating (NaN).
- `string.Compare(a, b, c) == 0` → `string.Equals(a, b, c)` with a
  `StringComparison` `c`; the culture-sensitive two-argument form stays.
- `xs.Select(f).Sum()` → `xs.Sum(f)`, likewise `Average`/`Min`/`Max`;
  `xs.Where(p).Any()` → `xs.Any(p)`, likewise `Count`/`First`/
  `FirstOrDefault`/`Last`/`LastOrDefault`/`Single`/`SingleOrDefault`
  (yield to IDE0120); `xs.Count() > 0` → `xs.Any()`, `xs.Count() == 0` →
  `!xs.Any()`, the `!= 0`/`>= 1`/`< 1` spellings, and the `Where(p)`/
  `Count(p)` forms (yield to CA1827).
- `!(x is T)` → `x is not T` (C# 9; yields to IDE0083).

Custom hints come from the file named by `csharp_refactor.hints`, one per
line, `#` comments allowed, relative paths resolved upward from the
analysed file; they name the repository's own functions and are their
author's to aim. Safety for every hint: unification sees through
parentheses and only the outermost of nested matches fires (the next pass
takes the rest); a substituted binding is bracketed where its precedence
needs it and so is the whole replacement in its parent; a metavariable the
right side drops or repeats fires only on a pure expression; nothing fires
inside an attribute argument or an expression tree; the speculative
re-bind settles the rest. For the built-in hints, additionally: every
operator the left side spells is the built-in one at the site (a
user-defined `==` is a call, `dynamic` binds at run time), every method
it spells resolves to the BCL (`Enumerable.Any`, not a repository's own),
and every method the right side spells has no non-BCL candidate in scope
for the receiver's type (a shadowing extension would win). Deliberately
absent: De Morgan (`!a && !b` → `!(a || b)` is taste, and reads worse on
`!string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b)`); `Aggregate((a, b) => a + b)` → `Sum()` (unchecked wrap versus
checked throw); `xs.Select(x => x)` → `xs` (the copy may be the point);
`OrderBy(f).First()` → `MinBy(f)` (on an empty sequence one throws and the
other returns `null`). F# twin: FR0012.

### CR0012 — correctness

An arm that says it is unfinished — a comment between the label and its
value reading "not supported", "unsupported", "not implemented", "not
yet", "NYI", "stub", "placeholder", "unfinished", "TBD" — and returns a
stand-in (`null`, `default`, `false`, `0`, `-1`, `""`, `string.Empty`, an
empty collection) becomes `throw new NotImplementedException()`, so the
gap reports itself instead of reaching callers as a real-looking result.
Only where sibling arms actually COMPUTE (a table of constants is data,
not a stub); a bare `TODO`/`FIXME` never accuses (they mark future work of
every kind); commented-out code (an identifier applied, a string literal,
a format hole, a `;`) is not a note; a `null` from a method whose return
type is nullable (`T?`) and a `false` from a `Try…(out …)` probe are the
legitimate "no result" and stay. The spelling of the exception follows
what resolves at the site. F# twin: FR0100.

### CR0120 — correctness

SQL text built from values — `cmd.CommandText = $"SELECT … WHERE
id={id}"`, `new SqlCommand("… " + name)`, `db.Database.ExecuteSqlRaw($"…")`,
Dapper's `Query($"…")` — is an injection. Priority; note: parameterise
(or use the interpolated `FormattableString` API, which parameterises the
holes). Sinks: `CommandText` setters, `*Command` constructors,
`FromSqlRaw`/`ExecuteSqlRaw`/`SqlQueryRaw` and their async forms, Dapper's
`Query*`/`Execute*`, and helpers named for SQL (`ExecuteSql`, `RunQuery`,
`QueryDb`); a `FormattableString`-typed parameter (`FromSqlInterpolated`,
`FromSql`) never fires (typed). "Built from values" is an interpolation
with a non-constant hole (`$"SET search_path = {Schema}"` over a `const`
is text the author wrote), a `+` chain with a non-constant operand,
`string.Format`/`Concat`, or a local or static field bound one hop to one;
a parameter resolves to nothing — the caller is where the string was
built. F# twin: FR0066. Yields to CA2100, CA3001.

### CR0121 — correctness

A SQL command whose constant text is a DML statement (`SELECT`/`INSERT`/
`UPDATE`/`DELETE`/`MERGE`) with a `WHERE`, `VALUES` or `SET` and no
parameter marker in any dialect (`@name`, `:name`, `?`, `$1`): the values
it filters or writes are coming from somewhere, and the next edit will
concatenate them. Note, at the same sinks as CR0120. F# twin: FR0146.

### CR0122 — correctness

`Process.Start("cmd", $"/c {input}")`, `psi.Arguments = "… " + input`,
`new ProcessStartInfo("sh", $"-c {cmd}")` — a command line built from a
value is an injection. Priority; note: `ProcessStartInfo.ArgumentList`
takes each argument unescaped. A fixed file name and a literal argument
string never fire; a dynamically built string (CR0120's definition)
reaching `FileName`, `Arguments`, the two-string `Start` or the
`ProcessStartInfo` constructor does. F# twin: FR0126.

### CR0123 — correctness

A literal matching a provider's documented key format: `sk-ant-` (an
Anthropic key), `sk-`/`sk_live_`/`rk_live_` (OpenAI, Stripe), `whsec_`, `AIza`
(Google), `ghp_`/`gho_`/`github_pat_`, `AKIA` (an AWS access key id),
`xoxb-`/`xoxp-` (Slack), a PEM private-key header, a three-segment JWT,
`Bearer eyJ…`, an Azure `AccountKey=`. Priority; note: a secret in source
is in every clone and every log of it — move it to configuration and
rotate it. Format anchoring, not entropy; a literal containing `test`,
`example`, `sample`, `dummy`, `fake`, `placeholder`, `xxxx` or `your`, an
elision (`...`), or `123456` (the standard made-up key; six specific digits
do not occur by chance in real key material) is a sample and stays quiet;
the literal parts of an interpolated string are scanned. F# twin: FR0127.

### CR0124 — correctness

A credential in a `const` (or `static readonly`) connection string to a
non-loopback server — `Password=`/`Pwd=` beside another connection key
(`Server=`, `Data Source=`, `Host=`, `User Id=`, `Database=`). Note.
Loopback servers (`localhost`, `127.0.0.1`, `::1`, `(local)`,
`(localdb)\…`, `.`) and placeholder passwords (`password`, `test`,
`changeme`, `secret`, `<…>`, `{…}`, `%…%`, `$(…)`, `${…}`) are never
reported. F# twin: FR0153.

### CR0125 — correctness

`MD5.Create()`, `SHA1.Create()`, `new MD5CryptoServiceProvider()`, `DES`,
`TripleDES`, `RC2`, `RIPEMD160` (typed to `System.Security.Cryptography`);
`ServerCertificateCustomValidationCallback = (…) => true` (or
`DangerousAcceptAnyServerCertificateValidator`); `SecurityProtocolType.Tls`/
`Tls11`/`Ssl3`. Priority; notes — the editor offers `SHA256.Create()` for
a hash (a checksum of non-hostile data can keep MD5, and should say so).
The WebSocket handshake SHA-1 in a file carrying RFC 6455's GUID
(`258EAFA5-…`) is quiet; a SHA-1 in a `switch` whose sibling arm
constructs SHA-256 or stronger is a caller's format option; retiring a
protocol changes what the wire negotiates, so that stays a note. F# twin:
FR0065. Yields to CA5350, CA5351, CA5359, CA5364, CA5386, CA5397.

### CR0126 — idiom

`new SHA256Managed()`, `new SHA256CryptoServiceProvider()`, `new
SHA512Cng()`, `new RNGCryptoServiceProvider()`, `new MD5CryptoServiceProvider()`,
`new AesManaged()`… are `SHA256.Create()`, `RandomNumberGenerator.Create()`,
`MD5.Create()`, `Aes.Create()`: the same algorithm, so behaviour is
preserved (a weak one keeps its CR0125 note). The RSA and DSA providers
stay: `RSA.Create()` generates a 2048-bit key where the provider made a
1024-bit one. Guards: zero-argument constructors only; the
factory returns the base type, so any other mention of the obsolete name
in the file — a declared type, `is`, `typeof`, a generic argument — vetoes
that name (`SHA512Managed sha = new SHA512Managed()` would no longer fit);
the factory is spelled bare under a `using`, qualified otherwise. F#
twin: FR0128. Yields to SYSLIB0021.

### CR0140 — cosmetic

`[SerializableAttribute]` → `[Serializable]`: the compiler resolves the
short form. Held where a type declared in this file under the short name
would win the lookup (attribute resolution tries the exact name before
appending `Attribute`); with a semantic model the speculative re-bind of
the patched file settles the rest. F# twin: FR0082.

### CR0141 — cosmetic

`[Foo()]` → `[Foo]`: an empty argument list on an attribute says nothing.
F# twin: FR0083.

### CR0142 — cosmetic

Attribute lists sharing one line merge into one bracket pair:
`[Fact] [Trait("a", "b")]` becomes `[Fact, Trait("a", "b")]`. Off by
default — one attribute per line is C#'s dominant style, and two bracket
pairs on one line are the author's choice as often as not. Guards: the
lists are adjacent on one line of the same declaration; none has a target
(`[assembly: …]`, `[return: …]`, `[field: …]` keep their own brackets); no
comment or directive between them; at most
`csharp_refactor.CR0142.max_attributes` (4) in the merged list; the merged
line fits the wrap column, else the rule stands down rather than inventing
a layout. F# twin: FR0060.

### CR0145 — idiom

A namespace spelled out at every use is a `using`:
`System.Text.Json.JsonSerializer.Serialize(x)` six times in a file becomes
`using System.Text.Json;` among the usings and `JsonSerializer.Serialize(x)`
at every use. Guards: the prefix binds to a namespace at each spelling and
the name after it to a type (a type nested in a type, or a namespace inside
another, is never the prefix); the namespace is not already imported — that
case is IDE0001's; at least `csharp_refactor.CR0145.uses` (6) spellings, or
`deep_uses` (4) when the namespace has three segments or more; a bare name
the file already uses in another sense holds the fix to a note (its own
declaration of the type does not); `global::`-qualified and
`using`-directive spellings are left alone; the speculative re-bind, with
the new `using` in place, settles every other resolution change, an
extension-method ambiguity included. The `using` goes among the file's
usings in alphabetical order within its family (`System` first), else after
the last one, else at the top; a namespace with its own usings takes it
there. F# twin: FR0147.

### CR0146 — idiom

A trailing `//` note on a public declaration's header line is the summary
its XML doc lacks: `public decimal Rate(int n) => …; // monthly,
non-compounding rate` gains `/// <summary>monthly, non-compounding
rate</summary>` above it and loses the note. Guards: the declaration is
`public` or `protected` (or an enum member) and has no XML doc; the
comment ends the header line — the line the identifier sits on — with real
code before it; it reads as a summary: twelve characters or more, more than
one word, starting with a letter or digit (not a marker like `^ index` or
`-- node`), not an instruction (`TODO`, `FIXME`, `HACK`, `NOTE`, `pragma`),
not a code fragment (`=>`, `();`, `return `, `var `), and holding no `<` or
`&` (XML text; escaping would break the original-text proof); the file is
not a test file (`*Tests.cs`, `*Test.cs`, `*Fixture*`), where a note labels
the fixture. The doc line goes above the whole declaration, attributes
included — a `///` between an attribute and its member is CS1587. An enum
member's note sits after the `,` that separates it from the next one, which
belongs to the enum rather than to the member: it counts as the member's
own, so every member of a list is documented, not only the last. F# twin:
FR0132.

### CR0143 — cosmetic

`@name` where `name` is neither a keyword nor a contextual keyword
(`var`, `async`, `record`, `field`…): the quoting does nothing at this
site, independently of any other. `@_` stays — bare `_` is a discard.
Each strip is independently valid. F# twin: FR0084.

### CR0144 — cosmetic

`else { if (c) … }` — an `else` block holding exactly one `if` statement
and nothing else is the `else if` that was meant. Only comments INSIDE the
inner `if`'s own span travel with it; one anywhere else in the block (its
leading trivia included) would be dropped and holds the fix; a multi-line
literal in the moved lines stands the rule down; the inner `if`'s
continuation lines move left by the block's indentation where they have
room. F# twin: FR0111.

### CR0147 — idiom

A chain of length tests on one countable with positional reads — `if
(xs.Length == 0) … else if (xs.Length == 1) { var a = xs[0]; … } else …`
— is a `switch` over list patterns (C# 11): `switch (xs) { case []: …
case [var a]: … default: … }`. Guards: the subject is a plain local or
parameter the nullable flow proves not null (`.Length` threw on null; a
list pattern would quietly match nothing, so a nullable-disabled file
stays), of a type the compiler accepts in a list pattern — an array,
`string`, `List<T>`, a span, anything with a `Length`/`Count` and an
indexer (the speculative check decides); every link compares that
`Length`/`Count` with `==` against a constant 0–4, each link a different
length; a branch reads `xs[i]` only at constant `i` below its own length,
and the leading `var a = xs[i];` declarations (spelled `var`: a typed
declaration would be retyped by the pattern) become the binders (`[var
a]`), the other positions `_`; three links at least, or two with a
terminal `else`; the shared switch guards of CR0002 (SwitchRewrite: no
escaping `break`, no comments outside the bodies, braces for clashing
locals, compiler reachability for `break;`). CR0002 stands down on the
same chain. F# twin: FR0112 (the list half).

### CR0148 — performance

`Encoding.UTF8.GetBytes("literal")` and `Encoding.ASCII.GetBytes(…)` on a
compile-time constant whose every character is below U+0080 (so UTF-8
and ASCII agree and `u8` is exact) are the `u8` literal (C# 11): bare
where a `ReadOnlySpan<byte>` is expected (no transcoding, no allocation),
`"literal"u8.ToArray()` where a `byte[]` is (the allocation stays, the
transcoding goes). `UTF8`/`ASCII` bound to the BCL properties, a regular
literal argument, never inside an expression tree.

### CR0149 — idiom

An `init`/`set` property with no initialiser that every construction of
its type in the compilation sets in an object initialiser, and nothing
else assigns, is `required` (C# 11): the compiler keeps it so. Guards: at
least one construction, every one with an initialiser naming the
property; every write an initialiser (a constructor or a method
assigning it vetoes); the property not static, virtual, abstract, an
override or an explicit interface implementation, its accessors plain;
no serializer attribute on the property or the type and no Entity
Framework entity (a serializer constructs without initialisers);
`required` is a demand on callers, so the scope gate of the shape rules
applies; every construction of a type DERIVED from it in the compilation
sets the property too (`new Derived()` would be CS9035); neither the type
nor a derived one is a type argument for a `new()`-constrained parameter
(`Make<Options>()` would be CS9040); a type seen beyond the compilation
(public in a library, internal with friends) needs the host's reference
oracle, and every site it finds there must be a construction setting the
property — never a base list or a type argument; without the oracle such
a type stands down. Off by default: `required` is also a runtime demand on every
deserializer of the type (System.Text.Json throws on a missing required
member), which no build can prove absent — `csharp_refactor.CR0149 =
true` or `--codes CR0149` turns it on for a compilation known not to be
deserialized. F# twin: FR0145 (the inverse).

### CR0150 — performance

A `static readonly Dictionary<K,V>` or `HashSet<T>` filled in its
initialiser and only ever read is a `FrozenDictionary<K,V>`/`FrozenSet<T>`
(.NET 8) via `.ToFrozenDictionary()`/`.ToFrozenSet()`, built for lookups.
Guards: a collection-initialiser construction; every reference a lookup
or an order-independent aggregate (`TryGetValue`, an indexer get,
`ContainsKey`, `Contains`, `Count`, `GetValueOrDefault`,
`Any`/`All`/`Sum`/`Min`/`Max`) — a write, an increment or a by-ref pass
breaks the build, and enumeration, `Keys`/`Values` and the ordered LINQ
readers veto, a frozen collection enumerating in its own order where a
`Dictionary` follows insertion. That holds in this file, in every other
file of the compilation (an internal field's `Registry.Map[k] = v`
elsewhere), and, for a field seen beyond the compilation (public in a
library, internal with friends), at every site the host's reference
oracle finds — without the oracle such a field stands down; a partial type spread over
files stands down; the scope gate
(a `Dictionary`-typed field is API); the comparer argument travels to the
converter; `System.Collections.Frozen` resolves and its `using` is added.
F# twin: FR0035.

### CR0151 — performance

`params T[]` on a method whose body only enumerates, indexes or measures
the array is `params ReadOnlySpan<T>` (C# 13): callers pass their
arguments without an array. Guards: the parameter is used only for
`foreach`, `.Length`, an indexer read, or passing to a `ReadOnlySpan<T>`
parameter; never stored, returned, captured by a lambda or local
function, passed to an array or `IEnumerable<T>` parameter, used with
LINQ; the method is not `async`, an iterator, virtual, abstract or an
override; the scope gate (a `params` type change is binary-breaking);
every reference to the method, in this file and through the host's
reference oracle in every other, is a plain call outside an expression
tree (a method group `Func<int[], int> f = H.Sum;` stops converting, an
expression tree cannot hold a span `params` call) — without an oracle the
compilation's own trees are read, complete only for a private method or
an internal one no friend assembly sees, so anything wider stands down;
the speculative check re-binds every call in the file and the files
referencing it.

### CR0152 — performance

`private readonly object _gate = new();` whose every reference in the
file is the operand of a `lock` statement is `private readonly Lock
_gate = new();` (C# 13, .NET 9): the dedicated type skips the
object-header path, and the `lock` statement binds to its scope.
`Monitor.*`, passing, comparing or storing it vetoes — in every part of
a partial type, the other files included; `System.Threading.Lock`
resolves, and its `using` is added.

### CR0153 — idiom

A property whose private backing field is referenced only inside that
property's own accessors uses the `field` keyword (C# 14): the mentions
become `field`, the field declaration goes, its initialiser moves to the
property. Guards: the field is private, unattributed, not `volatile`,
of the property's own type (`field` takes that type, and a wider
backing field would change what the accessors compute); a constructor or
any other member referencing it — in any part of a partial type, across
files — vetoes (a constructor writing it would have to target the
property, which changes when the setter runs); a `nameof` of the field or
a string literal spelling its name anywhere in the compilation (reflection
by name) vetoes; the
initialiser, which moves among the initialisers in textual order, is
pure; the name `field` is not already an identifier in the type (the
contextual keyword would shadow it); no comment on the field line.

### CR0154 — idiom

`if (x != null) x.P = v;`, `if (x is not null) x[i] = v;` is `x?.P = v;`
(C# 14). Guards: the condition is a null test of a pure read `x`
(identifier or dotted read) through the built-in reference test — a
user-defined `!=` (a Unity object's "destroyed is null") answers what
`?.` never asks, and holds the fix — the body exactly one assignment — compound
included — whose target is `x.P` or `x[i]` with `x` the same reference,
no `else`; `x` not assigned inside the body. The right-hand side is not
evaluated when `x` is null, which is what the original did too. Yields
to IDE0031.

### CR0155 — idiom

A static class holding only `this T`-extension methods on one receiver
type and name is an `extension(T x) { … }` block (C# 14), every method
body kept verbatim with the receiver parameter dropped. Off by default:
the class name disappears for reflection and for callers who invoked the
methods statically; the scope gate applies. A method with attributes, or
a receiver with attributes or `ref`/`in`/`scoped` beside `this`, holds the
class: the block renders neither, and `extension(T x)` would receive a
copy where `this ref T x` mutated the caller's.

### CR0156 — idiom

A memberless `abstract record Base;` whose only derived types are sealed
records in the same file is `union Base(Case1, Case2);` (C# 15), with `:
Base` dropped from each case — the compiler then proves every switch
exhaustive. Gated on the numeric language version (silent below C# 15
whatever the host) and on the syntax the compilation's compiler accepts
(the speculative check proves it); no base list, attributes, type
parameters or primary constructor on the base; the scope gate (the base
type changes kind). F# twins: FR0016, FR0022.

### CR0157 — idiom

A `switch` expression over an enum every member of which is listed, or
over an abstract type every derived type of which is sealed, in this
assembly and listed, whose discard arm throws a parameterless
`InvalidOperationException`/`SwitchExpressionException`/
`ArgumentOutOfRangeException`/`NotSupportedException`/
`NotImplementedException`, throws `UnreachableException` instead (.NET 7,
`System.Diagnostics` resolvable, its `using` added): the arm is
unreachable by construction, and the exception says so. A message
argument keeps the fix down to a note — the message may be a contract.
An enum can still hold an undefined value (`(Kind)42`): the rewrite
states that as the contract violation it is, and a `catch` of the old
type elsewhere would no longer see it.

### CR0158 — idiom

A `switch` expression over a native union that lists every case and
keeps a `_ =>` arm (C# 15): the compiler now proves exhaustiveness, and
the arm hides a missing case. Note; gated on the numeric language
version and verified against the shipped compiler before its default is
decided.

### CR0160 — correctness

A lambda, anonymous method or local function created inside a `for`,
`while` or `do` loop (or a `foreach` whose body assigns an outer local)
reads a local the loop changes — the loop variable, the `while ((line =
Read()) != null)` binder, a counter bumped in the body — and outlives the
iteration: it is added to a collection declared outside the loop,
assigned to a field, a property or an outer local, returned or yielded,
handed to `Task.Run`, `Task.Factory.StartNew`, `ThreadPool.QueueUserWorkItem`,
`Task.ContinueWith`, `CancellationToken.Register`, a `Thread`, a `Timer`,
a `Task` or a `Lazy<T>` constructor, subscribed with `+=`, or kept alive
by a lazy LINQ chain (`Where`, `Select`, …) that itself escapes. Every
such closure reads the shared cell when it runs, which is after the loop
moved on. The fix is the per-iteration copy the author meant: `var i1 =
i;` before the statement that creates the closure, the closure reading
`i1`; several closures in one statement share one copy. Guards: the
closure only reads the local (a closure that writes it wants the shared
cell, and so does any other closure of the loop that writes it — then
nothing is copied); the local is declared outside the body of the loop
that changes it (a body local is fresh each iteration; an outer loop's
variable read from an inner loop counts, the copy going right before the
closure in the inner loop); the closure is created in a statement of the loop
body (one in the loop's condition or incrementor is a note); the
statement itself does not change the local before the closure runs. A
closure passed to a callee the rule cannot read is a note — the callee
may run it before the next iteration — and so is a closure created
outside any loop over a local assigned again after its creation (late
binding is sometimes the point). A closure consumed at once (an awaited
call, an immediate LINQ operator such as `ToList`/`First`/`Count`, a
call into `System`/`System.Linq`/`System.Collections`) is quiet; a
`foreach` variable is fresh per iteration since C# 5. F# has no such
defect: a closure captures an immutable value.

### CR0161 — correctness

A method of a non-`readonly` struct that mutates it (`Value++`, a write
to a field or auto-property of `this`, a call to another mutator, `this`
passed by `ref`; from metadata `MoveNext`, `Reset`, `Enter`, `TryEnter`,
`Exit`, `Dispose`) called on a receiver the compiler copies first: a
`readonly` field outside its own constructors, any property getter, an
indexer that is not an array element (a `List<T>` element is returned by
value), a `foreach` iteration variable, an `in` parameter, a `using`
variable — and through a mutable struct field of such a copy. The call
runs on the copy and the original never changes; nothing in the language
says so. Note: call it on a variable (a local, a non-readonly field, a
`ref` local or return), or make the struct immutable. A local, an array
element, a `ref`/`out` parameter and `this` are variables and never
reported; `Dispose` is reported only on a `foreach` variable or an `in`
parameter. F# records and structs are immutable unless spelled `mutable`.

### CR0162 — correctness

`new System.Threading.Timer(…)` created as a statement of its own, or
bound to a local that never leaves the method — never assigned to a field
or property, never added to a collection, returned, passed as an
argument, captured by a closure or disposed. Once the method returns
nothing references the timer, the garbage collector takes it at the next
collection and its callbacks stop. Note: keep it in a field for as long
as it should fire, and dispose it when done. A `using var timer` is a
scoped intent. `System.Timers.Timer` is not this defect: once started it
is rooted through the timer queue by its own `Elapsed` callback and keeps
firing (measured: an unreferenced started one ran through five forced
collections; an unreferenced `System.Threading.Timer` stopped at the
first), so the rule never names it.

### CR0163 — correctness

`sem.Wait();` or `await sem.WaitAsync();` (also `sem.Wait(ct)`,
`Semaphore.WaitOne`, `Mutex.WaitOne`, `ReaderWriterLockSlim.EnterReadLock`/
`EnterWriteLock`/`EnterUpgradeableReadLock`) followed by statements and
the matching `Release()`/`ReleaseMutex()`/`Exit*Lock()` in the same
block, with no `try` between them: an exception or an early `return`
between the two keeps the semaphore held and every later caller waits
for good. The statements between become the `try` body and the release
the `finally` (a `return`, `break` or `continue` among them then
releases too, which is the correction). Guards: both are own-line
statements on the same receiver (by symbol) with no timeout and no
result used (`if (sem.Wait(100))` is a different contract); exactly one
release of that receiver in the block and no re-acquire between them;
no local declared between them is read after the release (the `try`
would scope it out — a note), no multi-line literal or directive between
them (re-indenting would change it — a note).

### CR0164 — correctness

`if (_cache == null) _cache = new X();` (also `is null`, and `_cache ??=
new X()`) on a `static` field of a reference type outside any `lock`:
two threads build two values, and on a weak memory model a reader may
see the reference before the object behind it is complete.
`LazyInitializer.EnsureInitialized(ref _cache, () => new X())` publishes
exactly one, and a following `return _cache;` folds in: `return
LazyInitializer.EnsureInitialized(ref _cache, () => new X());`. Guards:
the field is `static`, not `volatile`, `readonly` or `[ThreadStatic]`;
the assignment is the whole `if` body; the initialising expression is
provably non-null (`EnsureInitialized` throws on a null factory result)
— a `new`, an array, collection or interpolated string, a string literal,
a `??` with such a right side, or an expression whose flow state is
not-null under `#nullable enable`; the expression does not mention the
field; not inside a `lock` or a static constructor (both already
serialise); `System.Threading.LazyInitializer` resolves (its `using` is
added). Instance fields are not reported: a per-instance cache is usually
confined to one thread. F#'s `lazy` is the twin.

### CR0165 — correctness

`catch (Exception ex) { throw new SyncException("sync failed"); }` — the
wrapper type has a constructor taking the same arguments followed by an
`Exception`, and the caught exception is passed nowhere: its type,
message and stack trace are gone from every log and every outer handler.
The fix appends it: `throw new SyncException("sync failed", ex);`; an
unnamed catch (`catch (Exception)`, bare `catch`) gains a name (`ex`,
`e` or `exception`, the first that is free in the catch and not a local
or parameter of the member). Guards: the throw is a statement under the
catch, through blocks and `if`s only; the constructor's arguments do not
mention the caught exception in any way (`ex.Message` in the message is
the author's choice); the constructor with the trailing `Exception` is
accessible from the throw site. `throw ex;` itself is CA2200's
business, and this rule yields to it.

### CR0166 — performance

`try { v = int.Parse(s); } catch (FormatException) { v = -1; }` — a
parse whose failure path is an exception: a stack walk per bad input,
for a question `TryParse` answers with a bool. The fix: `if
(!int.TryParse(s, out v)) { v = -1; }`; bare `int.TryParse(s, out v);`
for an empty catch; `return int.TryParse(s, out var parsed) ? parsed :
0;` for `try { return int.Parse(s); } catch { return 0; }`. Guards: the
`try` block holds exactly the one parse statement (nothing else is under
the catch); the target is a local or a field, not a property; the parse
is `T.Parse(args)` for the numeric types, `bool`, `char`, `Guid`,
`DateTime`, `DateTimeOffset`, `TimeSpan`, or `Enum.Parse<E>(args)`, and
the `TryParse` twin with the same arguments binds (the speculative check
proves it, so a `NumberStyles`/`IFormatProvider` variant goes through
only where the framework has it); one filter-less catch, no `finally`,
that covers every failure `TryParse` would turn into `false`:
`Exception` or `SystemException` always; `FormatException` only around
a parse that cannot overflow (`bool`, `char`, `Guid`, `DateTime`,
`DateTimeOffset`, an enum) whose argument the flow analysis proves
non-null under `#nullable` — a numeric parse under `catch
(FormatException)` used to let an overflow propagate, and the rewrite
would swallow it; `OverflowException` alone, `ArgumentException` and
`ArgumentNullException` (which never caught a format error) are notes;
under a broad catch every argument (and the target) is one whose
evaluation cannot throw — a literal or constant, a local, a parameter, a
field, a static member named through its type — since `try { v =
int.Parse(parts[1]); } catch { v = 0; }` also caught the index out of
range that `TryParse` would let escape;
the catch variable, if named, is not read (the message would be lost). On
the failure path `TryParse` sets the target to `default` where `Parse`
left it untouched, so a local keeps the fix only when it was declared
without a value (or with `default`/`0`/`null`), the catch assigns it, or
the catch leaves (`return`/`throw`/`continue`); a field only when the
catch assigns it; for the `return` form the catch is exactly `return
expr;` with `expr` pure. Any other `try` whose catch names
`FormatException` around a `Parse` call is a note. A candidate for the F#
side too.

### CR0167 — correctness

`a == b` or `a != b` where both operands are `float`, `double` or `Half`
(nullable included), at least one of them computed here (arithmetic, a
call, a conversion) and neither a literal, a constant, `default` or a
`Math.Round`/`Floor`/`Ceiling`/`Truncate` call: two computations of the
same value rarely share a representation, so the comparison is true only
by accident of the arithmetic. Two stored copies of one value compare
exactly (a tie test after `>=`, a de-duplication) and are quiet. Note: compare `Math.Abs(a - b)` against a
tolerance, or use `decimal` where the values are exact. Test files are
exempt (an assertion on an exact value is the test's claim).

### CR0168 — correctness

`double avg = sum / count;` — two integral operands whose quotient lands
in a `double`, `float`, `decimal` or `Half`: the division truncates
before the conversion, and the fraction the destination type exists for
is gone. The typed tree says where the result goes (the expression's
converted type), so a declaration, an assignment, a `return`, an argument
into a floating parameter and an operand of a floating operator (`a / b
* 1.0`) all count. Note; the editor offers a cast on the left operand
(`(double)sum / count`), and a sweep never applies it — an integer
division into a floating target is occasionally meant. A division by the
literal `1`, a constant quotient, one under an explicit cast and one
inside `Math.Floor`/`Truncate`/`Round`/`Ceiling` are quiet.

### CR0169 — correctness

`DateTime.Now`/`DateTime.Today` compared with, subtracted from,
`CompareTo`'d, `Equals`'d or `DateTime.Compare`d against
`DateTime.UtcNow` — directly, through `.Date`/`.AddX(…)`/`.Subtract(…)`
on one, or through a local, field or property of this file whose only
write is one of them. The two kinds differ by the machine's UTC offset,
so the comparison flips with the timezone and twice a year with daylight
saving. Note: use one kind on both sides (`DateTime.UtcNow` throughout,
or `ToUniversalTime()` on the local one). `ToUniversalTime()`,
`ToLocalTime()`, `DateTime.SpecifyKind` and `new DateTime(…,
DateTimeKind.X)` make the kind explicit and stand the rule down; a
symbol written more than once, or written from something the rule cannot
classify, is unknown and quiet. A candidate for the F# side too.

### CR0170 — correctness

A method, lambda or local function that takes a `CancellationToken`
holds a loop (`for`, `foreach`, `while`, `do`) that awaits, sleeps or
blocks on a task (`.Wait()`, `.Result`, `GetAwaiter().GetResult()`), and
never reads the token — no
`ThrowIfCancellationRequested()`, no `IsCancellationRequested`, no call
that takes it, in the condition, the body or any closure inside. The
caller's cancellation is ignored for the loop's whole run. The fix puts
`ct.ThrowIfCancellationRequested();` (the parameter's name) at the top of
the loop body, which is what a caller passing a token expects: an
`OperationCanceledException` at the next iteration. Guards: the parameter
is the function's own, not a field; the loop is not nested inside another
loop of the same function (the outer loop is the author's granularity);
the body is a block with at least one statement (an expression-bodied
loop is a note); the loop is not inside a `finally` (nothing should throw
there). Iterators are included; a token parameter defaulted to
`default` is included (the caller who passes one expects it honoured).

### CR0171 — correctness

`foreach (var x in xs) { … xs.Remove(x); }` — the enumerated collection
changed (`Add`, `Insert`, `Remove`, `RemoveAt`, `Clear`, `Sort`, an
indexer set, …) under its own `foreach`: a `List<T>`, `Collection<T>`,
`ObservableCollection<T>`, `Queue<T>`, `Stack<T>`, `LinkedList<T>`,
`SortedSet<T>` (and on .NET Framework a `Dictionary`/`HashSet`) throws
`InvalidOperationException` on the next `MoveNext`. Two fixes. The filter
shape — the body is exactly `if (cond) xs.Remove(x);` on a `List<T>`
with `cond` pure and not mentioning `xs` — becomes `xs.RemoveAll(x =>
cond);`. Any other shape enumerates a snapshot and changes the original:
`foreach (var x in xs.ToList())` (`using System.Linq` added). Guards: the
source is an identifier or member access (a `.ToList()`/`.ToArray()` is
already another object; `d.Keys`/`d.Values` enumerate `d` itself — the
key and value collections' enumerators check the owning dictionary's
version, so `foreach (var k in d.Keys) d.Add(…)` throws just the same and
the reference watched is `d`, the snapshot `d.Keys.ToList()`); the mutating call is on the same
reference by symbol, outside nested closures; a mutation followed by
`break`, `return` or `throw` never reaches the next `MoveNext` and is the
accepted mutate-and-leave idiom; `Dictionary.Remove`/`HashSet.Remove` and
a `Dictionary` indexer set on CoreLib (.NET Core 3.0+) tolerate
enumeration and are not reported;
`System.Linq` importable for the snapshot.

### CR0172 — idiom

A local — or a `static readonly` field — initialised with a constant and
never written is a `const`: `var schema = "app";` becomes `const string
schema = "app";`, `int retries = 3;` gains the keyword, `static readonly
string Prefix = "v";` becomes `const string Prefix = "v";` — the value
the compiler folds, the declaration that says the name names a value,
not a slot. The F# twin is FR0130's `[<Literal>]` (and, for the local,
the nearest C# has to FR0007's `let mutable` strip). A field is API: a
consumer compiled against the field loads a slot the `const` no longer
is (a literal is read at the consumer's own compile time), so a public
or protected field waits for `--api-changes` (or a leaf project), an
internal one for the absence of `InternalsVisibleTo` friends, and a
private one is the file's own; the field must carry no attribute
(`[ThreadStatic]` means the slot) and no write anywhere in the
compilation (a static constructor could). What follows from the local: a hole filled from the constant
(`$"SET search_path = {schema}"`) reads as text to CR0120, where the
`var` read as a value a later line might have reassigned. Guards: every
declarator of the statement has an initialiser the compiler folds to a
constant (`GetConstantValue`: a literal, a `const`, arithmetic or
concatenation of those, an enum member, a C# 10 constant interpolation)
of a type a `const` may have (a primitive, `decimal`, `string`, an enum;
never `null`); no write to the local anywhere in the member — an
assignment, a compound assignment, `++`/`--`, a `ref`/`out`/`in`
argument, `&`, a `ref` local or return, a deconstruction target; a `var`
is replaced by the constant's type; `using`, `ref`, `scoped` and
already-`const` declarations, and one holding a comment or directive,
are left alone; the speculative re-bind settles the rest.

### CR0173 — idiom

A `return` — or an assignment to one target — that every branch of an
`if` performs, on a different value, is one `return` of a conditional:
`if (a) return "1"; else return "2";` and the else-less `if (a) return
"1"; return "2";` become `return a ? "1" : "2";`; `if (a) v = f(); else
v = g();` becomes `v = a ? f() : g();`. The bool-literal spellings
(`return true` / `return false`) are CR0001's, which returns the
condition itself; this rule stands down for them. Guards: each branch is
exactly one statement, a `return` with an expression or a simple
assignment to the same local, parameter or field (a property setter may
act, and the branches ran it once each); the else-less form takes the
`return` that immediately follows the `if` in its block; the two values
differ in text; no comment or directive inside is swallowed; an `if`
that is another `if`'s `else` is left to the chain; the result is one
line within the wrap column (`wrap_column`, else `max_line_length`, else
120: a conditional over two multi-line arms — an `Ok(new …)` against a
`NotFound(…)` of five lines — is no clearer than the `if`); an arm that is
itself a conditional, an assignment, a lambda, a switch expression or a
`throw` is parenthesised, and a `throw` statement is not an arm; the
speculative re-bind settles the conditional's typing (a natural common
type, or the target type from C# 9), and every arm still converts to the
type it converted to before — arms of one type, a natural type equal to
the target, or a target-typed conditional (`if (a) return 1; else return
2.0;` in an `object` method would box a double; `a ? i : f` into a
`double` rounds the int through `float`). No F# twin: F# has no `return`, and
its `if` is the expression already. Yields to IDE0046 and IDE0045.

### CR0174 — performance

`Substring` — or a C# 8 range, `s[6..]`, `s[6..11]` — handed to a
consumer whose `ReadOnlySpan<char>` overload means the same is `AsSpan`,
the same characters without the copy: `int.Parse(s.Substring(6, 5))` →
`int.Parse(s.AsSpan(6, 5))`, `sb.Append(s.Substring(6))` →
`sb.Append(s.AsSpan(6))`, `writer.Write(s[6..])` →
`writer.Write(s.AsSpan(6))`, `int.Parse(s[6..11])` →
`int.Parse(s.AsSpan()[6..11])`. The consumers: the BCL's `Parse`/`TryParse` (a type in
`System` or `System.Numerics`: the span overload takes the same culture and style; a user
type's two overloads are its author's to keep alike), `StringBuilder.Append`,
`TextWriter.Write` and `WriteLine` (and any writer derived from it),
`string.Concat` — a one-identifier swap with no culture question.
`StartsWith`, `Equals`, `IndexOf` and their kind stay out: the string
overloads are culture-sensitive and the span twins ordinal, so the swap
would change the comparison — safer not to fire. Measured in
benchmarks/PerfClaims. Guards: the receiver is a `string`; the copy is
directly the argument (one bound to a name may be read twice); the
consumer's own type declares the overload with `ReadOnlySpan<char>` at
that position, the other parameters unchanged (or spans a string converts
to, `string.Concat`), any extra ones optional (`int.Parse(ReadOnlySpan<char>,
NumberStyles = …, IFormatProvider = null)`); `MemoryExtensions` resolves
(`using System;` added); the speculative re-bind settles the overload. F#
twin: FR0106. Yields to CA1846.

### CR0175 — performance

A prefix or suffix cut out only to be compared with a literal is a
`StartsWith`/`EndsWith` that cuts nothing: `s.Substring(0, 6) == "ORDER-"`
and `s[..6] == "ORDER-"` → `s.StartsWith("ORDER-", StringComparison.Ordinal)`;
`s.Substring(s.Length - 3) != "MED"` and `s[^3..] != "MED"` →
`!s.EndsWith("MED", StringComparison.Ordinal)`. C#'s `==` on strings is
ordinal, so `StringComparison.Ordinal` is the same comparison spelled
out — the culture-sensitive `StartsWith(string)` is exactly what the
rewrite must not emit. Measured in benchmarks/PerfClaims. Guards: the
literal's length equals the cut's (`s.Substring(0, 3) == "ab"` can never
hold: a bug, not this rule's); a `Substring` or a range on a string
shorter than the cut throws where `StartsWith` answers false, so the site
must sit under a length guard on the same string — a conjunct evaluated
before it (`s.Length >= 6 && …`, `6 <= s.Length && …`, `s.Length > 5 &&
…`), a disjunct before a `!=` (`s.Length < 6 || …`), or the condition of
the enclosing `if` or `?:` whose true branch holds it, neither the string
nor any prefix of its chain (`p` under a guard on `p.S`) written, stepped
or passed by `ref`/`out` inside that `if` (a lambda, `foreach` or pattern
cannot rebind the name in C#, CS0136); the receiver a name or a chain of names (a call
or an indexer may answer a different string to the guard and the cut);
the built-in `==`, outside an expression tree. An unguarded site is
offered in the editor only — the author sees whether the string can be
short — and a sweep leaves it, as the F# twin does with its `Substring`
form (an F# slice clamps; a C# range is a `Substring` and throws).
`StringClaimTests` in the property suite checks the equivalence on random
strings. F# twin: FR0166.

### CR0176 — performance

`ToCharArray()` copies the whole string into an array a `foreach` then
reads once — a string already enumerates its characters: `foreach (var c
in s.ToCharArray())` → `foreach (var c in s)`. The `foreach` over a string
compiles to an indexed loop, faster than the array's copy and walk
(measured in benchmarks/PerfClaims). A LINQ consumer
(`s.ToCharArray().Any(…)`) stays: over a string, LINQ walks a boxed
`CharEnumerator`, measured slower than the array it would save (the F#
side found the same of `Seq.*`). Guards: the argument-free
`ToCharArray()` (the two-argument form slices), on a `string`, directly
the loop's source — one bound to a name may be indexed or written. F#
twin: FR0167.

### CR0177 — performance

A local computed inside a loop from nothing the loop changes is computed
once, above it: `foreach (var x in xs) { var label = tag + ":"; … }` →
`var label = tag + ":"; foreach (var x in xs) { … }`. Measured in
benchmarks/PerfClaims: a string concatenation hoisted is 4× faster and 48
KB → 48 B over a thousand iterations — the win; arithmetic over locals,
parameters and readonly fields is parity (the JIT hoists that itself) and
moves for the reading. A mutable field under a call would gain 8%, but a
call in the body can change it, so the rule never reads one. Guards: one `var`/typed local with an initializer, on one
line, a statement of the loop body reached through blocks, `if`/`else`,
`switch` sections, `try`, `lock`, `using`, `checked` — never through a
lambda, a local function or a nested loop (the innermost loop is the
target); the initializer is literals, `nameof`, reads of locals,
parameters, `const` and `readonly` fields (outside a constructor),
built-in operators over those (`/` and `%` by a non-zero literal only: an
empty loop never divided), `?:`, and an interpolated string whose holes
are such reads of primitives or strings; every local or parameter it reads
is assigned nowhere in the member but its own declaration, and declared
outside the loop; the hoisted name is spelled nowhere in the member
outside the loop and written nowhere in the loop; the loop statement
heads its line; no comment or directive rides on the declaration; the
speculative check re-binds. F# twin: FR0071.

### CR0178 — performance

A query copied before `Where` or `Select` runs them in the query, copying
after: `db.Orders.ToList().Where(o => o.Total > 0 || o.State == 0).Select(o => o.Id)`
→ `db.Orders.Where(o => o.Total > 0 || o.State == 0).Select(o => o.Id).ToList()`.
The copy (`ToList`, `ToArray`, `AsEnumerable`) on an `IQueryable<T>` loads
every row of the query and filters and projects them in memory; moved after
the stages, the database filters and sends only the columns asked for. No
runtime pair measures it: the win is rows not read off a server, not
nanoseconds. A stage moves only while a provider translates it exactly —
a function call, arithmetic or a nested object might not translate (a
runtime error) or translate to something else: a `Where` of `&&`, `||`
and `!` over comparisons (built-in or the BCL's own operator) and `bool`
columns, each comparison a column against a column, a literal, a local, a
parameter, a `const` or an enum member; a `Select` of a column or an
anonymous object of columns; a column an instance auto-property of the
lambda's parameter, not `[NotMapped]`. The first stage that does not
qualify stays in memory after the copy. A sweep moves only comparisons
SQL answers as C# does — integers, `bool`, enums, `Guid`, not nullable,
and `== null`/`!= null` on any column. A string compares under the
column's collation (case-insensitive on SQL Server's default), a
`decimal` or `DateTime` value is rounded to the column's scale, a floating
value is the server's, a nullable column is NULL-unknown under `!=` or `!`
where C# says true: those the editor offers and a sweep leaves as a note.
Guards: the captured locals and parameters are written nowhere after
their declaration (the in-memory stage read them when the result was
enumerated, the query reads them when it runs); no comment or directive
in the chain; a copy of the same kind right after the stages is the one
kept; the speculative check re-binds the lambdas as expression trees.
A column is a VALUE column: a navigation property (`o.Customer`) is an
auto-property too, but in memory it is whatever the copy loaded — null
without an `Include` — where the query joins it, so a null test or a
projection of one stays in memory. Moving `ToList()`/`ToArray()` to the end
changes the chain's static type from `IEnumerable<T>` to `List<T>`/`T[]`:
an overload taking `List<T>` would win, a generic argument would infer
differently, a `var` local would carry the new type on, and `.Reverse()`
would bind to `List<T>.Reverse()`, which reverses in place. So a sweep
moves the copy only where the value goes into a `foreach`, a further
Enumerable call (not `Reverse`), an argument of a method with one
candidate and a non-generic parameter, a member's `return` or expression
body, an explicitly typed declaration or an assignment, or a `var` local
used only so; elsewhere the editor offers it (`AsEnumerable()` keeps the
type, and always moves). One difference the rule does not guard: with EF
Core the copy materialised and TRACKED every entity; after the move only
the filtered rows (or, under a projection, none) are tracked, so code that
relied on the context having loaded the whole table — relationship
fix-up, `DbSet.Local` — sees less.
F# twin: FR0174.
