/// Shapes the 2026-11-17 sweep of seven public repositories (PowerToys,
/// shadowsocks-windows, Files, ShareX, RevokeMsgPatcher, Polly,
/// azure-sdk-for-net) found a rule or the tool getting wrong.
module CSharp.Refactor.Tests.CorpusSweep1117Tests

open System
open System.IO
open Xunit
open CSharp.Refactor.Tool
open CSharp.Refactor.Tool.Options
open CSharp.Refactor.Tests.Harness

let private fixed' (suggestions: CSharp.Refactor.Suggestion list) =
    suggestions |> List.filter (fun s -> not s.Fixes.IsEmpty)

// ---- CR0042 ----

[<Fact>]
let ``CR0042 never awaits the enclosing method itself`` () =
    // ShareX CaptureLastRegion: `return Execute(s);` inside `ExecuteAsync(s)` became
    // `return await ExecuteAsync(s);`, infinite recursion
    let source =
        csharp
            """
            using System.Threading.Tasks;
            abstract class CaptureBase
            {
                protected abstract int Execute(int s);
                protected virtual Task<int> ExecuteAsync(int s) => Task.FromResult(Execute(s));
            }
            class CaptureLast : CaptureBase
            {
                protected override async Task<int> ExecuteAsync(int s)
                {
                    if (s == 0)
                    {
                        await Task.Yield();
                        return 1;
                    }
                    if (s == 1)
                    {
                        // inside a lambda of ExecuteAsync: the twin is still the method itself
                        return await Task.Run(async () =>
                        {
                            await Task.Yield();
                            var r = Execute(s);
                            return r;
                        });
                    }
                    return Execute(s);
                }
                protected override int Execute(int s) => s;
            }
            """

    Assert.Empty(fixed' (suggestCode "CR0042" source))

[<Fact>]
let ``CR0042 leaves a fire-and-forget wrapper and still awaits a blocking one`` () =
    // RevokeMsgPatcher GAHelper: `RequestPageView` is `Task.Run(() => RequestPageViewAsync(…))`
    let source =
        csharp
            """
            using System.Threading.Tasks;
            class Ga
            {
                public async Task RequestPageViewAsync(string page) { await Task.Delay(1); }
                public void RequestPageView(string page) { Task.Run(() => RequestPageViewAsync(page)); }
                public async Task FlushAsync(string page) { await Task.Delay(1); }
                public void Flush(string page) { FlushAsync(page).GetAwaiter().GetResult(); }
            }
            class Form
            {
                readonly Ga ga = new Ga();
                async Task LoadAsync()
                {
                    await Task.Yield();
                    ga.RequestPageView("/main");
                    ga.Flush("/main");
                }
            }
            """

    Assert.Equal<string list>([ "ga.Flush(\"/main\")" ], firedText source (fixed' (suggestCode "CR0042" source)))

[<Fact>]
let ``CR0042 leaves the sync branch of a dual sync-async method and a call commented as sync`` () =
    // Azure.Core's `bool async` policies; MultipartReaderStream's `// Sync, it's already buffered`
    let source =
        csharp
            """
            using System.IO;
            using System.Threading;
            using System.Threading.Tasks;
            class Policy
            {
                public void ProcessNext(int m) { }
                public Task ProcessNextAsync(int m) => Task.CompletedTask;
                async ValueTask ProcessAsync(int m, bool async)
                {
                    if (async)
                    {
                        await ProcessNextAsync(m).ConfigureAwait(false);
                    }
                    else
                    {
                        ProcessNext(m);
                    }
                    if (async) await Task.Delay(10).ConfigureAwait(false); else Thread.Sleep(10);
                }
                async Task<int> Buffered(Stream s, byte[] b)
                {
                    await Task.Yield();
                    // Sync, it's already buffered
                    var read = s.Read(b, 0, 1);
                    return read;
                }
                async Task Plain(int m)
                {
                    await Task.Yield();
                    ProcessNext(m);
                }
                async Task Kept(int k)
                {
                    await Task.Yield();
                    // keep in sync with Writer.Flush
                    ProcessNext(k);
                }
            }
            """

    Assert.Equal<string list>(
        [ "ProcessNext(m)"; "ProcessNext(k)" ],
        firedText source (fixed' (suggestCode "CR0042" source))
    )

// ---- CR0040 ----

[<Fact>]
let ``CR0040 leaves a dual-path drain and a task a WhenAny early return proved complete`` () =
    let source =
        csharp
            """
            using System.Threading.Tasks;
            class C
            {
                async Task<int> Dual(Task<int> t, bool async) => async ? await t.ConfigureAwait(false) : t.Result;
                async Task<int> Raced(Task<int> t)
                {
                    if (await Task.WhenAny(t, Task.Delay(100)) != t)
                        return 0;
                    return t.Result;
                }
                async Task<int> Blocking(Task<int> t)
                {
                    await Task.Yield();
                    return t.Result;
                }
                async Task<int> Unrelated(Task<int> u, bool wait)
                {
                    if (wait)
                    {
                        await Task.Delay(100);
                        return 0;
                    }
                    else
                    {
                        return u.Result;
                    }
                }
            }
            """

    // an unrelated await in the other branch is no dual path
    Assert.Equal<string list>([ "t.Result"; "u.Result" ], firedText source (fixed' (suggestCode "CR0040" source)))

[<Fact>]
let ``CR0040 keeps a drain whose comment opens with sync but asks for the change`` () =
    let source =
        csharp
            """
            using System.Threading.Tasks;
            class C
            {
                async Task<int> Asked(Task<int> t)
                {
                    await Task.Yield();
                    // sync over async here, fix later
                    return t.Result;
                }
                async Task<int> Todo(Task<int> u)
                {
                    await Task.Yield();
                    // Synchronous for now: TODO make it async
                    return u.Result;
                }
                async Task<int> Meant(Task<int> v)
                {
                    await Task.Yield();
                    // Sync on purpose: the caller holds the gate
                    return v.Result;
                }
            }
            """

    Assert.Equal<string list>([ "t.Result"; "u.Result" ], firedText source (fixed' (suggestCode "CR0040" source)))

[<Fact>]
let ``CR0040 reads the antecedent of a continuation as complete`` () =
    // a continuation runs once its task completed: the first parameter of a
    // lambda, or of a method handed over as a method group (with a state object)
    let source =
        csharp
            """
            using System.Threading.Tasks;
            class C
            {
                static readonly int[] results = new int[4];
                static void Store(Task<int> xs, object state)
                {
                    if (state is int j) results[j] = xs.Result;
                }
                static Task Schedule(Task<int> t) => t.ContinueWith(Store, 0);
                static Task<int> Lambda(Task<int> t) => t.ContinueWith(a => a.Result + 1);
                static async Task<int> InAsync(Task<int> t)
                {
                    await Task.Yield();
                    return await t.ContinueWith(c => c.Result + 1);
                }
                static int Helper(Task<int> h) => h.Result;
                static int Direct(Task<int> d) => Helper(d);
                static int Blocks(Task<int> b) => b.Result;
            }
            """

    Assert.Equal<string list>([ "h.Result"; "b.Result" ], firedText source (suggestCode "CR0040" source))

// ---- CR0170 / CR0055 ----

[<Fact>]
let ``CR0170 leaves a loop reading the token through a local, a catch rollback and an explicit None`` () =
    // Polly CircuitBreakerManualControl; ShareX AmazonS3RemoteStorageProvider
    let source =
        csharp
            """
            using System;
            using System.Collections.Generic;
            using System.Threading;
            using System.Threading.Tasks;
            class Ctx
            {
                public CancellationToken CancellationToken { get; private set; }
                public static Ctx Get(CancellationToken ct) => new Ctx { CancellationToken = ct };
            }
            class M
            {
                async Task Isolate(Func<Ctx, Task>[] callbacks, CancellationToken cancellationToken)
                {
                    var context = Ctx.Get(cancellationToken);
                    foreach (var action in callbacks)
                    {
                        await action(context);
                    }
                }
                async Task Rename(string[] objs, CancellationToken cancellationToken)
                {
                    var copied = new List<string>();
                    try
                    {
                        foreach (var o in objs)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            await Copy(o, cancellationToken);
                            copied.Add(o);
                        }
                    }
                    catch
                    {
                        foreach (var c in copied)
                        {
                            await Delete(c, CancellationToken.None);
                        }
                        throw;
                    }
                }
                async Task Uncancelled(string[] objs, CancellationToken ct)
                {
                    foreach (var o in objs)
                    {
                        await Delete(o, CancellationToken.None);
                    }
                }
                async Task Ignored(string[] objs, CancellationToken ct)
                {
                    foreach (var o in objs)
                    {
                        await Task.Delay(1);
                    }
                }
                async Task Fetched(CancellationToken token)
                {
                    // built from the token, holding none: the loop still ignores it
                    var items = await GetAllAsync(token);
                    foreach (var item in items)
                    {
                        await Task.Delay(1);
                    }
                }
                async Task Enumerated(IAsyncEnumerable<int> source, CancellationToken ct)
                {
                    // the enumerator carries the token: MoveNextAsync observes it
                    var e = source.GetAsyncEnumerator(ct);
                    while (await e.MoveNextAsync())
                    {
                        await Task.Delay(1);
                    }
                }
                static Task<List<string>> GetAllAsync(CancellationToken ct) => Task.FromResult(new List<string>());
                static Task Copy(string o, CancellationToken ct) => Task.CompletedTask;
                static Task Delete(string o, CancellationToken ct) => Task.CompletedTask;
            }
            """

    let count (needle: string) (text: string) = text.Split(needle).Length - 1
    Assert.Equal(2, (fixed' (suggestCode "CR0170" source)).Length)
    let fixedSource = fixAll "CR0170" source
    Assert.Equal(1, count "ct.ThrowIfCancellationRequested();" fixedSource)
    Assert.Equal(1, count "token.ThrowIfCancellationRequested();" fixedSource)
    Assert.Equal(1, count "cancellationToken.ThrowIfCancellationRequested();" fixedSource)
    Assert.Equal(2, count "CancellationToken.None" fixedSource)

[<Fact>]
let ``CR0170 keeps a loop over data derived from a value computed with the token`` () =
    // `ys` is data; a token read off a linked source carries it
    let source =
        csharp
            """
            using System;
            using System.Collections.Generic;
            using System.Linq;
            using System.Threading;
            using System.Threading.Tasks;
            class C
            {
                static List<int> Compute(CancellationToken ct) => new List<int>();
                async Task Derived(Func<int, Task> work, CancellationToken ct)
                {
                    var xs = Compute(ct);
                    var ys = xs.Where(x => x > 0).ToList();
                    foreach (var y in ys)
                    {
                        await work(y);
                    }
                }
                async Task Linked(List<int> xs, Func<int, CancellationToken, Task> work, CancellationToken ct)
                {
                    using var source = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    var token = source.Token;
                    foreach (var x in xs)
                    {
                        await work(x, token);
                    }
                }
            }
            """

    let fired = fixed' (suggestCode "CR0170" source)
    Assert.Single fired |> ignore
    Assert.True(fired.Head.Span.Start < source.IndexOf "async Task Linked")

[<Fact>]
let ``CR0055 keeps the None of a compensation before a throw`` () =
    let source =
        csharp
            """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            class M
            {
                async Task Rename(string a, string b, CancellationToken ct)
                {
                    if (!await Delete(a, ct))
                    {
                        await Delete(b, CancellationToken.None);
                        throw new InvalidOperationException("x");
                    }
                    await Delete(b, CancellationToken.None);
                }
                static Task<bool> Delete(string o, CancellationToken ct) => Task.FromResult(true);
            }
            """

    Assert.Single(fixed' (suggestCode "CR0055" source)) |> ignore
    let fixedSource = fixAll "CR0055" source
    Assert.Equal(1, fixedSource.Split("CancellationToken.None").Length - 1)
    Assert.Contains("await Delete(b, ct);", fixedSource)

// ---- CR0160 ----

[<Fact>]
let ``CR0160 reads Parallel.For as running the body before it returns`` () =
    // ShareX Retouch: `Parallel.For` inside the iteration loop got per-iteration copies
    let source =
        csharp
            """
            using System;
            using System.Collections.Generic;
            using System.Threading.Tasks;
            class C
            {
                static void Run(int rw)
                {
                    bool[] fg = new bool[rw];
                    int[] model = { 1 };
                    for (int it = 0; it < 5; it++)
                    {
                        Parallel.For(0, rw, y => { fg[y] = model[0] > y; });
                        fg = new bool[rw];
                        model = new[] { it };
                    }
                }
                static List<Action> Later(int n)
                {
                    var actions = new List<Action>();
                    int[] model = { 1 };
                    for (int it = 0; it < n; it++)
                    {
                        actions.Add(() => Console.WriteLine(model[0]));
                        model = new[] { it };
                    }
                    return actions;
                }
            }
            """

    let fired = suggestCode "CR0160" source
    Assert.Single fired |> ignore
    Assert.Equal<string list>([ "() => Console.WriteLine(model[0])" ], firedText source fired)

[<Fact>]
let ``CR0160 keeps a Parallel.ForEachAsync whose task is not awaited`` () =
    // it returns the task: dropped, the bodies run after the loop moved on
    let source =
        csharp
            """
            using System;
            using System.Collections.Generic;
            using System.Threading.Tasks;
            class C
            {
                static void Dropped(List<List<int>> batches)
                {
                    for (int i = 0; i < batches.Count; i++)
                    {
                        Parallel.ForEachAsync(batches[i], async (x, ct) => { await Task.Delay(1, ct); Console.WriteLine(i + x); });
                    }
                }
                static async Task Awaited(List<List<int>> batches)
                {
                    for (int i = 0; i < batches.Count; i++)
                    {
                        await Parallel.ForEachAsync(batches[i], async (x, ct) => { await Task.Delay(1, ct); Console.WriteLine(i + x); });
                    }
                }
            }
            """

    let fired = suggestCode "CR0160" source
    Assert.Single fired |> ignore
    Assert.True(fired.Head.Span.Start < source.IndexOf "static async Task Awaited")

// ---- CR0051 ----

[<Fact>]
let ``CR0051 leaves a using inside the returned lambda and one under a CA2025 pragma`` () =
    // ShareX TaskHelpers: `return Task.Run(() => { using Bitmap output = …; … });`
    let source =
        csharp
            """
            using System.IO;
            using System.Threading.Tasks;
            class C
            {
                static Task<int> Preview()
                {
                    return Task.Run(() =>
                    {
                        using var ms = new MemoryStream();
                        return (int)ms.Length;
                    });
                }
                static Task<int> Outer()
                {
                    using var ms = new MemoryStream();
                    return Task.Run(() => (int)ms.Length);
                }
                static Task<int> Suppressed()
                {
            #pragma warning disable CA2025
                    using var ms = new MemoryStream();
                    return Task.Run(() => (int)ms.Length);
            #pragma warning restore CA2025
                }
            }
            """

    let fired = fixed' (suggestCode "CR0051" source)
    Assert.Single fired |> ignore
    Assert.Equal(source.IndexOf "return Task.Run(() => (int)ms.Length);", fired.Head.Span.Start)

// ---- CR0060 pragma ----

[<Fact>]
let ``CR0060 stands down where CA2000 is disabled by pragma`` () =
    let source =
        csharp
            """
            using System.IO;
            class C
            {
                static int Suppressed(string p)
                {
            #pragma warning disable CA2000
                    var s = new FileStream(p, FileMode.Open);
            #pragma warning restore CA2000
                    return s.ReadByte();
                }
                static int Open(string p)
                {
                    var s = new FileStream(p, FileMode.Open);
                    return s.ReadByte();
                }
            }
            """

    let fired = suggestCode "CR0060" source
    Assert.Single fired |> ignore
    Assert.True(fired.Head.Span.Start > source.IndexOf "static int Open")

[<Fact>]
let ``a pragma that names no warning is not a decision about the twin`` () =
    // `#pragma warning disable` silences the diagnostic itself, where the run
    // counts it; the rule still reports, and a bare restore ends a named disable
    let source =
        csharp
            """
            using System.IO;
            class C
            {
            #pragma warning disable
                static int Blanket(string p)
                {
                    var s = new FileStream(p, FileMode.Open);
                    return s.ReadByte();
                }
            #pragma warning restore
            #pragma warning disable CA2000
                static int Named(string p)
                {
                    var s = new FileStream(p, FileMode.Open);
                    return s.ReadByte();
                }
            #pragma warning restore
                static int Open(string p)
                {
                    var s = new FileStream(p, FileMode.Open);
                    return s.ReadByte();
                }
            }
            """

    let starts = suggestCode "CR0060" source |> List.map (fun s -> s.Span.Start)
    Assert.Equal(2, starts.Length)
    Assert.True(starts.[0] < source.IndexOf "static int Named")
    Assert.True(starts.[1] > source.IndexOf "static int Open")

// ---- CR0177 ----

[<Fact>]
let ``CR0177 leaves a struct the loop writes and still hoists a value`` () =
    // Files: `MENUITEMINFOW mii = default;` filled field by field and passed by address
    let source =
        csharp
            """
            struct Info { public int Size; public int Id; }
            class C
            {
                static int Sum(int n, int k)
                {
                    int total = 0;
                    for (int i = 0; i < n; i++)
                    {
                        Info info = new Info { Size = k };
                        info.Size = 4;
                        total += Fill(ref info) + info.Id;
                    }
                    for (int i = 0; i < n; i++)
                    {
                        Info zero = default;
                        total += zero.Size + i;
                    }
                    for (int i = 0; i < n; i++)
                    {
                        var label = "#" + k;
                        total += label.Length + i;
                    }
                    return total;
                }
                static int Fill(ref Info x) { x.Id++; return x.Size; }
            }
            """

    let fired = fixed' (suggestCode "CR0177" source)
    Assert.Equal<string list>([ "var label = \"#\" + k;" ], firedText source fired)

// ---- CR0164 ----

[<Fact>]
let ``CR0164 leaves a field written elsewhere and keeps one with several lazy sites`` () =
    // shadowsocks: `_Masterkey` resized by ref, `_userSettings` deserialised elsewhere
    let source =
        csharp
            """
            using System;
            using System.Collections.Generic;
            class C
            {
                static byte[] _key;
                static object _settings;
                static List<int> _cache;
                static List<int> _reset;
                static void Init(int n)
                {
                    if (_key == null) _key = new byte[n];
                    if (_key.Length != n) Array.Resize(ref _key, n);
                }
                static void Read()
                {
                    try { _settings = Load(); } catch (InvalidOperationException) { }
                    finally { if (_settings == null) _settings = new object(); }
                }
                static List<int> Cache()
                {
                    if (_cache == null) _cache = new List<int>();
                    return _cache;
                }
                static int Count() => (_cache ??= new List<int>()).Count;
                static List<int> Reset()
                {
                    if (_reset == null) _reset = new List<int>();
                    return _reset;
                }
                static void Clear() => _reset = null;
                static object Load() => new object();
            }
            """

    // `_cache`'s other write, `(_cache ??= …)`, is a lazy site too (a shape the rule
    // does not rewrite inside parentheses): the `if` still converts
    let fired = firedText source (suggestCode "CR0164" source)
    Assert.Equal<string list>([ "if (_cache == null) _cache = new List<int>();" ], fired)

// ---- CR0069 ----

[<Fact>]
let ``CR0069 trims a full-width full stop before the arguments`` () =
    let source =
        csharp
            """
            using System;
            class C
            {
                private void Update(string resourcePath)
                {
                    if (resourcePath.Length == 0)
                        throw new InvalidOperationException("未找到资源项。");
                }
            }
            """

    let fixedSource = fixAll "CR0069" source
    Assert.Contains("未找到资源项, calling", fixedSource)

// ---- tool ----

[<Fact>]
let ``a project whose nearest Platforms lacks AnyCPU builds for x64`` () =
    let dir =
        Path.Combine(Path.GetTempPath(), "csref-platform-" + Guid.NewGuid().ToString("N"))

    let write (rel: string) (text: string) =
        let path = Path.Combine(dir, rel)
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        File.WriteAllText(path, text)
        path

    try
        Directory.CreateDirectory(Path.Combine(dir, ".git")) |> ignore

        write
            "Directory.Build.props"
            "<Project><PropertyGroup><Platforms>ARM64;x64</Platforms></PropertyGroup></Project>"
        |> ignore

        let inherited =
            write "src/A/A.csproj" """<Project Sdk="Microsoft.NET.Sdk"></Project>"""

        let own =
            write
                "src/B/B.csproj"
                """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><Platforms>AnyCPU;x64</Platforms></PropertyGroup></Project>"""

        let arm =
            write
                "src/C/C.csproj"
                """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><Platforms>ARM64</Platforms></PropertyGroup></Project>"""

        Assert.Equal(Some "x64", Sweep.projectPlatform inherited)
        Assert.Equal(None, Sweep.projectPlatform own)
        Assert.Equal(Some "ARM64", Sweep.projectPlatform arm)
    finally
        try
            Directory.Delete(dir, true)
        with _ ->
            ()

[<Fact>]
let ``a project with no Platforms anywhere keeps its default`` () =
    let dir =
        Path.Combine(Path.GetTempPath(), "csref-platform-" + Guid.NewGuid().ToString("N"))

    try
        Directory.CreateDirectory(Path.Combine(dir, ".git")) |> ignore
        let project = Path.Combine(dir, "A.csproj")
        File.WriteAllText(project, """<Project Sdk="Microsoft.NET.Sdk"></Project>""")
        Assert.Equal(None, Sweep.projectPlatform project)
    finally
        try
            Directory.Delete(dir, true)
        with _ ->
            ()

[<Collection("Tool")>]
type ReportNotes() =

    [<Fact>]
    member _.``a note in a file a later pass re-analyses is reported once, at its current line``() =
        // RevokeMsgPatcher Device.cs: pass 1 added `using System.Threading;`, pass 2 found the
        // catch notes one line lower, and the report carried both
        let dir =
            Path.Combine(Path.GetTempPath(), "csref-notes-" + Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory dir |> ignore

        File.WriteAllText(
            Path.Combine(dir, "Sample.csproj"),
            """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"""
        )

        let source =
            csharp
                """
                using System.Collections.Generic;
                namespace Sample;
                public static class Device
                {
                    static List<int> _cache;
                    public static void Warm()
                    {
                        if (_cache == null) _cache = new List<int>();
                    }
                    public static int Probe(string s)
                    {
                        try { return int.Parse(s); }
                        catch { return 0; }
                    }
                    public static int Both(System.Threading.Tasks.Task<int> a, System.Threading.Tasks.Task<int> b) =>
                        a.Result + b.Result;
                }

                """

        let file = Path.Combine(dir, "Device.cs")
        File.WriteAllText(file, source)

        use captured = new StringWriter()
        let oldOut = Console.Out
        Console.SetOut captured

        try
            Sweep.resetRun ()

            match
                parseArgs
                    [|
                        Path.Combine(dir, "Sample.csproj")
                        "--codes"
                        "CR0164,CR0064,CR0040"
                        "--notes"
                    |]
            with
            | Ok opts -> Assert.Equal(0, Sweep.executeRun opts)
            | Error e -> failwith e
        finally
            Console.SetOut oldOut

        let edited = File.ReadAllText file
        Assert.Contains("LazyInitializer.EnsureInitialized", edited)

        let notes =
            Sweep.reportedSoFar ()
            |> List.filter (fun f -> f.Code = "CR0064")
            |> List.map (fun f -> f.StartLine)

        let catchLine =
            (edited.Replace("\r\n", "\n").Split('\n')
             |> Array.findIndex (fun l -> l.Contains "catch"))
            + 1

        Assert.Equal<int list>([ catchLine ], notes)
        let output = captured.ToString()
        Assert.Equal(1, output.Split("CR0064").Length - 1)

        // two notes on one line (`a.Result + b.Result`): both reported and both printed
        Assert.Equal(
            2,
            Sweep.reportedSoFar ()
            |> List.filter (fun f -> f.Code = "CR0040")
            |> List.length
        )

        Assert.Equal(2, output.Split("CR0040").Length - 1)
