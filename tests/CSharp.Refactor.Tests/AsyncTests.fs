module CSharp.Refactor.Tests.AsyncTests

open Xunit
open CSharp.Refactor.Tests.Harness

// ---- CR0040 ----

[<Fact>]
let ``blocking drains inside an async body become awaits, complete tasks and no-bind zones stay`` () =
    let source =
        """
using System;
using System.Threading.Tasks;
class C
{
    readonly object gate = new();
    async Task<int> A(Task<int> t) { var x = t.Result; return x + 1; }
    async Task B(Task t) { t.Wait(); }
    async Task<int> D(Task<int> t) => t.GetAwaiter().GetResult() + 1;
    async Task E(Task a, Task b) { Task.WaitAll(a, b); }
    async Task<int> F(Task<int> t) => (await Task.Run(() => t.Result)) + 1;
    async Task<int> G(Task<int> t) => t.Result.ToString().Length;
    async Task<int> H(Task<int> t) { if (t.IsCompleted) return t.Result; return await t; }
    async Task<int> I() { var t = Task.FromResult(1); return t.Result; }
    async Task<int> J(Task<int> t) { lock (gate) { return t.Result; } }
    async Task<int> K(Task<int> t) { try { return t.Result; } catch (AggregateException) { return 0; } }
    async Task<int> L(Task<int> t) { Func<int> f = () => t.Result; return f(); }
    async Task M(Task a, Task b) { Task.WaitAll(new[] { a, b }, 100); }
    async Task<int> N(Task<int> a, Task<int> b) { await Task.WhenAll(a, b); return a.Result + b.Result; }
}
"""

    let fired = suggestCode "CR0040" source
    let fixedSource = fixAll "CR0040" source
    Assert.Contains("async Task<int> A(Task<int> t) { var x = await t; return x + 1; }", fixedSource)
    Assert.Contains("async Task B(Task t) { await t; }", fixedSource)
    Assert.Contains("async Task<int> D(Task<int> t) => await t + 1;", fixedSource)
    Assert.Contains("async Task E(Task a, Task b) { await Task.WhenAll(a, b); }", fixedSource)
    Assert.Contains("async Task<int> F(Task<int> t) => (await t) + 1;", fixedSource)
    Assert.Contains("async Task<int> G(Task<int> t) => (await t).ToString().Length;", fixedSource)
    Assert.Contains("if (t.IsCompleted) return t.Result;", fixedSource)
    Assert.Contains("var t = Task.FromResult(1); return t.Result;", fixedSource)
    Assert.Contains("lock (gate) { return t.Result; }", fixedSource)
    Assert.Contains("try { return t.Result; } catch (AggregateException)", fixedSource)
    // the AggregateException site is a note, the lambda site a boundary note
    Assert.Equal(
        1,
        fired
        |> List.filter (fun s -> s.Message.Contains "AggregateException")
        |> List.length
    )

    Assert.Equal(1, fired |> List.filter (fun s -> s.Message.Contains "boundary") |> List.length)
    Assert.Contains("Task.WaitAll(new[] { a, b }, 100);", fixedSource)
    Assert.Contains("await Task.WhenAll(a, b); return a.Result + b.Result;", fixedSource)

[<Fact>]
let ``a drain outside an async body is the boundary note, except on Main's spine`` () =
    let source =
        """
using System.Threading.Tasks;
class C
{
    int A(Task<int> t) => t.Result;
    static void Main() { var r = Task.FromResult(1).Result; Compute().Wait(); }
    static Task Compute() => Task.CompletedTask;
}
"""

    let fired = suggestCode "CR0040" source
    Assert.Equal<string list>([ "t.Result" ], firedText source fired)
    Assert.Empty(fired.Head.Fixes)

// ---- CR0044 / CR0053 / CR0054 / CR0055 ----

[<Fact>]
let ``forgotten tasks, async void lambdas and single-task combinators are noted; a token in scope is passed`` () =
    let source =
        """
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
class C
{
    Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;
    void A() { SaveAsync(); }
    void B() { _ = SaveAsync(); }
    void D() { SaveAsync().ContinueWith(t => Console.WriteLine(t.Exception), TaskContinuationOptions.OnlyOnFaulted); }
    void E() { Task.Run(async () => { try { await SaveAsync(); } catch (Exception) { } }); }
    async Task F() { SaveAsync(); }
    void G(List<int> xs) { xs.ForEach(async x => await SaveAsync()); }
    void H() { Task.Run(async () => await SaveAsync()); }
    Task I(Task t) => Task.WhenAll(new[] { t });
    Task J(Task a, Task b) => Task.WhenAll(a, b);
    Task K(CancellationToken ct) => SaveAsync(CancellationToken.None);
    Task L(CancellationToken ct) => SaveAsync(default);
    Task M(CancellationToken ct) => Task.Run(() => 1, CancellationToken.None);
    async Task N(CancellationToken ct) { try { await SaveAsync(ct); } finally { await SaveAsync(CancellationToken.None); } }
    Task O(CancellationToken a, CancellationToken b) => SaveAsync(CancellationToken.None);
    async Task P(CancellationToken ct)
    {
        // None on purpose: a client disconnect must not cancel this cleanup
        await SaveAsync(CancellationToken.None);
        try { await SaveAsync(CancellationToken.None); } catch (Exception) { }
    }
}
"""

    let texts (code: string) =
        firedText source (suggestCode code source)

    Assert.Equal<string list>([ "SaveAsync()"; "Task.Run(async () => await SaveAsync())" ], texts "CR0044")
    Assert.Equal<string list>([ "async x => await SaveAsync()" ], texts "CR0053")
    Assert.Equal<string list>([ "Task.WhenAll(new[] { t })" ], texts "CR0054")
    Assert.Equal<string list>([ "CancellationToken.None"; "default" ], texts "CR0055")
    let fixedSource = fixAll "CR0055" source
    Assert.Contains("Task K(CancellationToken ct) => SaveAsync(ct);", fixedSource)
    Assert.Contains("Task L(CancellationToken ct) => SaveAsync(ct);", fixedSource)

// ---- CR0046 ----

[<Fact>]
let ``a method that only awaits an async call returns the task, one with a using or ConfigureAwait stays`` () =
    let source =
        """
using System;
using System.IO;
using System.Threading.Tasks;
class C
{
    async Task<int> Inner(int x) { await Task.Yield(); return x; }
    async Task<int> A(int x) { return await Inner(x); }
    async Task<int> B(int x) => await Inner(x);
    async Task Plain() { await Task.Yield(); }
    async Task D() { await Plain(); }
    async Task<int> E(int x) { return await Inner(x).ConfigureAwait(false); }
    async Task<int> F(int x) { using var s = new MemoryStream(); return await Inner(x); }
    async Task<int> G(int x) { return await Inner(x) + 1; }
    Task<int> Sync(int x) => Task.FromResult(x);
    async Task<int> H(int x) { return await Sync(x); }
}
"""

    let fired = suggestCode "CR0046" source
    Assert.Equal(3, fired.Length)
    let fixedSource = fixAll "CR0046" source
    Assert.Contains("Task<int> A(int x) { return Inner(x); }", fixedSource)
    Assert.Contains("Task<int> B(int x) => Inner(x);", fixedSource)
    Assert.Contains("Task D() { return Plain(); }", fixedSource)

// ---- CR0047 / CR0048 ----

[<Fact>]
let ``weak locks are noted with a gate offer, Monitor.Enter with try/finally becomes lock`` () =
    let source =
        """
using System;
using System.Threading;
class C
{
    readonly object gate = new();
    int count;
    void A() { lock (this) { count++; } }
    void B() { lock ("cache") { count++; } }
    void D() { lock (typeof(C)) { count++; } }
    void E() { lock (gate) { count++; } }
    void F()
    {
        Monitor.Enter(gate);
        try
        {
            count++;
        }
        finally
        {
            Monitor.Exit(gate);
        }
    }
    void G() { Monitor.Enter(gate); count++; Monitor.Exit(gate); }
    void H() { bool taken = false; Monitor.Enter(gate, ref taken); try { count++; } finally { if (taken) Monitor.Exit(gate); } }
}
"""

    Assert.Equal<string list>([ "this"; "\"cache\""; "typeof(C)" ], firedText source (suggestCode "CR0047" source))
    let weak = suggestCode "CR0047" source
    Assert.True(weak.Head.Fixes.Head.EditorOnly)
    let gated = applyFix source weak.Head.Fixes.Head
    // System.Threading is imported, so the .NET 9+ Lock type is the gate
    Assert.Contains("private readonly Lock _gate = new();", gated)
    Assert.Contains("void A() { lock (_gate) { count++; } }", gated)

    let monitor = suggestCode "CR0048" source
    Assert.Equal(2, monitor.Length)
    let fixedSource = fixAll "CR0048" source

    Assert.Contains(
        normalize
            """    void F()
    {
        lock (gate)
        {
            count++;
        }
    }""",
        fixedSource
    )

    Assert.Contains("void G() { Monitor.Enter(gate); count++; Monitor.Exit(gate); }", fixedSource)
    Assert.Contains("ref taken", fixedSource)

// ---- CR0049 / CR0050 ----

[<Fact>]
let ``a check-then-store on a ConcurrentDictionary takes GetOrAdd on the miss; a Task value is a note`` () =
    let source =
        """
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
class C
{
    readonly ConcurrentDictionary<string, int> cache = new();
    readonly ConcurrentDictionary<string, Task<int>> tasks = new();
    readonly Dictionary<string, int> plain = new();
    int Compute(string k) => k.Length;
    int A(string k)
    {
        if (!cache.TryGetValue(k, out var v))
        {
            v = Compute(k);
            cache[k] = v;
        }
        return v;
    }
    int B(string k) { if (!cache.TryGetValue(k, out var v)) { v = k.Length; cache.TryAdd(k, v); } return v; }
    int D(string k) { if (!plain.TryGetValue(k, out var v)) { v = k.Length; plain[k] = v; } return v; }
    Task<int> E(string k) { if (!tasks.TryGetValue(k, out var t)) { t = Task.FromResult(1); tasks[k] = t; } return t; }
    Task<int> F(string k) => tasks.GetOrAdd(k, key => Task.FromResult(key.Length));
    int G(string k) => cache.GetOrAdd(k, key => key.Length);
}
"""

    let fired = suggestCode "CR0049" source
    Assert.Equal(3, fired.Length)
    let fixedSource = fixAll "CR0049" source

    Assert.Contains(
        normalize
            """        if (!cache.TryGetValue(k, out var v))
        {
            v = cache.GetOrAdd(k, _ => Compute(k));
        }""",
        fixedSource
    )

    Assert.Contains(
        "if (!cache.TryGetValue(k, out var v)) { v = cache.GetOrAdd(k, _ => k.Length); } return v;",
        fixedSource
    )

    Assert.Contains("plain[k] = v;", fixedSource)
    Assert.Contains("tasks[k] = t;", fixedSource)
    Assert.Contains("Lazy", (fired |> List.find (fun s -> s.Message.Contains "both factories")).Message)

    Assert.Equal<string list>(
        [ "tasks.GetOrAdd(k, key => Task.FromResult(key.Length))" ],
        firedText source (suggestCode "CR0050" source)
    )

// ---- CR0051 / CR0052 ----

[<Fact>]
let ``a using outlived by the returned task is awaited; a this-capturing handler on a static publisher is noted`` () =
    let source =
        """
using System;
using System.IO;
using System.Threading.Tasks;
class C
{
    event Action Own;
    Task<int> Read(Stream s) => Task.FromResult(1);
    Task<int> A(string path) { using var f = File.OpenRead(path); return Read(f); }
    Task<int> B(string path) { using (var f = File.OpenRead(path)) { return Read(f); } }
    Task<int> D(string path) { using var f = File.OpenRead(path); var n = f.ReadByte(); return Task.FromResult(n); }
    void Flush() { }
    void E() { AppDomain.CurrentDomain.ProcessExit += (s, e) => Flush(); }
    void F() { AppDomain.CurrentDomain.ProcessExit += OnExit2; }
    void OnExit(object s, EventArgs e) { }
    void OnExit2(object s, EventArgs e) { }
    void G() { AppDomain.CurrentDomain.ProcessExit += (s, e) => Console.WriteLine("bye"); }
    void H() { Own += Flush; }
    void I() { AppDomain.CurrentDomain.ProcessExit += OnExit; AppDomain.CurrentDomain.ProcessExit -= OnExit; }
}
"""

    let fired = suggestCode "CR0051" source
    Assert.Equal(2, fired.Length)
    let fixedSource = fixAll "CR0051" source

    Assert.Contains(
        "async Task<int> A(string path) { using var f = File.OpenRead(path); return await Read(f); }",
        fixedSource
    )

    Assert.Contains(
        "async Task<int> B(string path) { using (var f = File.OpenRead(path)) { return await Read(f); } }",
        fixedSource
    )

    Assert.Contains("Task<int> D(string path) { using var f", fixedSource)

    Assert.Equal<string list>(
        [
            "AppDomain.CurrentDomain.ProcessExit += (s, e) => Flush()"
            "AppDomain.CurrentDomain.ProcessExit += OnExit2"
        ],
        firedText source (suggestCode "CR0052" source)
    )

// ---- CR0041 ----

[<Fact>]
let ``a private method draining a task whose callers are all async becomes async and awaited`` () =
    let source =
        """
using System.Threading.Tasks;
class C
{
    Task<int> Load() => Task.FromResult(1);
    private int Fetch(int n) { var x = Load().Result; return x + n; }
    async Task<int> A() { var r = Fetch(1); return r; }
    async Task<int> B() { return Fetch(2); }
    private int Sync(int n) => Load().Result + n;
    int D() => Sync(1);
    async Task<int> E() => Sync(2);
    public int Pub(int n) => Load().Result + n;
    async Task<int> F() => Pub(1);
}
"""

    let fired = suggestCode "CR0041" source
    Assert.Equal<string list>([ "Fetch" ], firedText source fired)
    let fixedSource = fixAll "CR0041" source
    Assert.Contains("private async Task<int> FetchAsync(int n) { var x = await Load(); return x + n; }", fixedSource)
    Assert.Contains("async Task<int> A() { var r = await FetchAsync(1); return r; }", fixedSource)
    Assert.Contains("async Task<int> B() { return await FetchAsync(2); }", fixedSource)
    Assert.Contains("int D() => Sync(1);", fixedSource)

// ---- CR0043 ----

[<Fact>]
let ``async void becomes async Task when every caller is async, a handler or a sync caller keeps a note`` () =
    let source =
        """
using System;
using System.Threading.Tasks;
class C
{
    event EventHandler Fired;
    async void Work() { await Task.Yield(); }
    async Task A() { Work(); }
    async void OnFired(object s, EventArgs e) { await Task.Yield(); }
    async void Sub() { await Task.Yield(); }
    void B() { Sub(); }
    async void Handler(object s, string e) { await Task.Yield(); }
    void D() { Fired += (s, e) => Handler(s, ""); }
}
"""

    let fired = suggestCode "CR0043" source
    // Work is fixed; Sub (a sync caller) and Handler (called from a lambda) are notes
    Assert.Equal(3, fired.Length)
    let fixedSource = fixAll "CR0043" source
    Assert.Contains("async Task Work() { await Task.Yield(); }", fixedSource)
    Assert.Contains("async Task A() { await Work(); }", fixedSource)
    Assert.Contains("async void Sub()", fixedSource)

    Assert.Empty(
        (fired
         |> List.find (fun s -> firedText source [ s ] = [ "void" ] && s.Fixes.IsEmpty))
            .Fixes
    )

// ---- CR0042 ----

[<Fact>]
let ``a sync call with an Async twin inside an async body awaits the twin; Dispose and sync bodies stay`` () =
    let source =
        """
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
class C
{
    async Task<string> A(StreamReader reader) { var line = reader.ReadLine(); return line; }
    async Task B(Stream s) { s.Flush(); }
    async Task<string> D(string path) { return File.ReadAllText(path); }
    async Task E() { Thread.Sleep(100); }
    async Task F(Stream s) { s.Dispose(); }
    string G(StreamReader reader) => reader.ReadLine();
    // an EF-shaped twin over IQueryable<T>: only an async query provider can run it
    async Task<int> I(System.Linq.IQueryable<int> q) { var xs = q.ToList(); return xs.Count; }
}
static class QueryableExtensions
{
    public static Task<System.Collections.Generic.List<T>> ToListAsync<T>(this System.Linq.IQueryable<T> source) => Task.FromResult(System.Linq.Enumerable.ToList(source));
}
"""

    let fired = suggestCode "CR0042" source
    Assert.Equal(4, fired.Length)
    let fixedSource = fixAll "CR0042" source
    Assert.Contains("var line = await reader.ReadLineAsync(); return line;", fixedSource)
    Assert.Contains("async Task B(Stream s) { await s.FlushAsync(); }", fixedSource)
    Assert.Contains("return await File.ReadAllTextAsync(path);", fixedSource)
    Assert.Contains("async Task E() { await Task.Delay(100); }", fixedSource)
    Assert.Contains("async Task F(Stream s) { s.Dispose(); }", fixedSource)
    Assert.Contains("string G(StreamReader reader) => reader.ReadLine();", fixedSource)
    Assert.Contains("var xs = q.ToList();", fixedSource)

// ---- CR0045 ----

[<Fact>]
let ``a blocking xUnit test becomes async, the throw assert its async form; shared state holds`` () =
    let source =
        """
using System;
using System.Threading.Tasks;
using Xunit;
public class Tests
{
    static Task<int> Load() => Task.FromResult(1);
    static Task Fail() => Task.FromException(new InvalidOperationException());
    [Fact]
    public void A() { var r = Load().Result; Assert.Equal(1, r); }
    [Fact]
    public void B() { Assert.Throws<InvalidOperationException>(() => Fail().Wait()); }
    [Fact]
    public void D() { Environment.SetEnvironmentVariable("X", "1"); var r = Load().Result; Assert.Equal(1, r); }
    [Fact]
    public void E() { Assert.Equal(1, 1); }
    [Fact]
    public void F() { Assert.Throws<AggregateException>(() => Fail().Wait()); }
}
"""

    let fired = suggestCode "CR0045" source
    Assert.Equal<string list>([ "A"; "B" ], firedText source fired)
    let fixedSource = fixAll "CR0045" source
    Assert.Contains("public async Task A() { var r = await Load(); Assert.Equal(1, r); }", fixedSource)

    Assert.Contains(
        "public async Task B() { await Assert.ThrowsAsync<InvalidOperationException>(() => Fail()); }",
        fixedSource
    )

    Assert.Contains("public void D() { Environment.SetEnvironmentVariable", fixedSource)
