module CSharp.Refactor.Tests.DefectTests

open Xunit
open CSharp.Refactor.Tests.Harness

// ---- CR0160 ----

[<Fact>]
let ``a for variable captured by an escaping closure gets a per-iteration copy`` () =
    let source =
        csharp
            """
            using System;
            using System.Collections.Generic;
            using System.Threading.Tasks;
            class C
            {
                void A(List<Action> actions, List<Task> tasks)
                {
                    for (int i = 0; i < 3; i++)
                    {
                        actions.Add(() => Console.WriteLine(i));
                        tasks.Add(Task.Run(() => Console.WriteLine(i * 2)));
                    }
                }
            }
            """

    let fired = fires 2 "CR0160" source
    Assert.True(fired |> List.forall (fun s -> not s.Fixes.IsEmpty))
    let fixedSource = fixAll "CR0160" source

    Assert.Contains(
        csharp
            """
            var i1 = i;
                        actions.Add(() => Console.WriteLine(i1));
            """,
        fixedSource
    )

    Assert.Contains(
        csharp
            """
            var i2 = i;
                        tasks.Add(Task.Run(() => Console.WriteLine(i2 * 2)));
            """,
        fixedSource
    )

[<Fact>]
let ``a while-condition binder captured by a queued task is copied; consumed and foreach closures are quiet`` () =
    let source =
        csharp
            """
            using System;
            using System.Collections.Generic;
            using System.IO;
            using System.Linq;
            using System.Threading.Tasks;
            class C
            {
                void A(TextReader reader, List<Task> tasks, int[] xs)
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        tasks.Add(Task.Run(() => Console.WriteLine(line)));
                    }
                    for (int i = 0; i < 3; i++)
                    {
                        var ys = xs.Where(x => x > i).ToList();
                        Console.WriteLine(ys.Count);
                    }
                    foreach (var s in new[] { "a" })
                    {
                        tasks.Add(Task.Run(() => Console.WriteLine(s)));
                    }
                    for (int j = 0; j < 3; j++)
                    {
                        int k = j;
                        tasks.Add(Task.Run(() => Console.WriteLine(k)));
                    }
                }
            }
            """

    let fired = suggestCode "CR0160" source
    Assert.Equal<string list>([ "() => Console.WriteLine(line)" ], firedText source fired)
    let fixedSource = fixAll "CR0160" source

    Assert.Contains(
        csharp
            """
            var line1 = line;
                        tasks.Add(Task.Run(() => Console.WriteLine(line1)));
            """,
        fixedSource
    )

[<Fact>]
let ``a closure handed to an unknown callee, a closure writing the variable, and a lazy chain stored are handled`` () =
    let source =
        csharp
            """
            using System;
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                void Register(Action a) { }
                void A(List<IEnumerable<int>> queries, int[] xs)
                {
                    for (int i = 0; i < 3; i++)
                    {
                        Register(() => Console.WriteLine(i));
                    }
                    int total = 0;
                    for (int i = 0; i < 3; i++)
                    {
                        Register(() => i += 1);
                    }
                    for (int i = 0; i < 3; i++)
                    {
                        queries.Add(xs.Where(x => x > i));
                    }
                }
            }
            """

    let fired = suggestCode "CR0160" source
    // the unknown callee: a note; the writer: quiet; the stored lazy chain: a fix
    assertFired 2 source fired
    let notes = fired |> List.filter (fun s -> s.Fixes.IsEmpty)
    Assert.Equal<string list>([ "() => Console.WriteLine(i)" ], firedText source notes)
    let fixedSource = fixAll "CR0160" source

    Assert.Contains(
        csharp
            """
            var i1 = i;
                        queries.Add(xs.Where(x => x > i1));
            """,
        fixedSource
    )

[<Fact>]
let ``a straight-line local written after an escaped closure is a note`` () =
    let source =
        csharp
            """
            using System;
            using System.Threading.Tasks;
            class C
            {
                void A()
                {
                    int x = 1;
                    var t = Task.Run(() => Console.WriteLine(x));
                    x = 2;
                    t.Wait();
                }
            }
            """

    let fired = fires 1 "CR0160" source
    Assert.True(fired.Head.Fixes.IsEmpty)

// ---- CR0161 ----

[<Fact>]
let ``a mutating call on a readonly struct field, a property and a list element is noted; a local and a readonly struct are quiet``
    ()
    =
    let source =
        csharp
            """
            using System.Collections.Generic;
            struct Counter
            {
                public int Value;
                public void Bump() => Value++;
                public int Peek() => Value;
            }
            readonly struct Frozen
            {
                public readonly int Value;
                public void Bump() { }
            }
            class C
            {
                readonly Counter _counter;
                Counter Prop { get; set; }
                readonly Frozen _frozen;
                List<Counter> _list = new List<Counter>();
                void A()
                {
                    _counter.Bump();
                    Prop.Bump();
                    _list[0].Bump();
                    _counter.Peek();
                    _frozen.Bump();
                    var local = new Counter();
                    local.Bump();
                    var arr = new Counter[1];
                    arr[0].Bump();
                }
            }
            """

    let fired = suggestCode "CR0161" source

    Assert.Equal<string list>([ "_counter.Bump()"; "Prop.Bump()"; "_list[0].Bump()" ], firedText source fired)

// ---- CR0162 ----

[<Fact>]
let ``a dropped or method-local timer is noted; a field-held or disposed one is quiet`` () =
    let source =
        csharp
            """
            using System;
            using System.Threading;
            class C
            {
                Timer _kept;
                void A()
                {
                    new Timer(_ => Console.WriteLine("tick"), null, 0, 1000);
                    var local = new Timer(_ => Console.WriteLine("tick"), null, 0, 1000);
                    Console.WriteLine(local.GetHashCode());
                    _kept = new Timer(_ => Console.WriteLine("tick"), null, 0, 1000);
                    using var scoped = new Timer(_ => Console.WriteLine("tick"), null, 0, 1000);
                    var passed = new Timer(_ => Console.WriteLine("tick"), null, 0, 1000);
                    GC.KeepAlive(passed);
                    // a started System.Timers.Timer is rooted through the timer queue by its own
                    // Elapsed callback: it keeps firing, so it is not this defect
                    var timers = new System.Timers.Timer(1000);
                    timers.Elapsed += (_, _) => Console.WriteLine("tick");
                    timers.Start();
                }
            }
            """

    let fired = fires 2 "CR0162" source
    Assert.True(fired |> List.forall (fun s -> s.Fixes.IsEmpty))

// ---- CR0163 ----

[<Fact>]
let ``an unguarded semaphore region is wrapped in try-finally`` () =
    let source =
        csharp
            """
            using System.Threading;
            using System.Threading.Tasks;
            class C
            {
                readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
                async Task A(int x)
                {
                    await _gate.WaitAsync();
                    if (x > 0)
                    {
                        return;
                    }
                    Work(x);
                    _gate.Release();
                }
                void Work(int x) { }
            }
            """

    let fired = fires 1 "CR0163" source
    let fixedSource = fixAll "CR0163" source

    let expected =
        csharp
            """
                    await _gate.WaitAsync();
                    try
                    {
                        if (x > 0)
                        {
                            return;
                        }
                        Work(x);
                    }
                    finally
                    {
                        _gate.Release();
                    }
                }
            """

    Assert.Contains(expected, fixedSource)

[<Fact>]
let ``a guarded region, a timeout wait and a between-declared local read after the release are left`` () =
    let source =
        csharp
            """
            using System;
            using System.Threading;
            class C
            {
                readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
                void A()
                {
                    _gate.Wait();
                    try
                    {
                        Work();
                    }
                    finally
                    {
                        _gate.Release();
                    }
                }
                void B()
                {
                    if (_gate.Wait(100))
                    {
                        Work();
                        _gate.Release();
                    }
                }
                void D()
                {
                    _gate.Wait();
                    var result = Compute();
                    _gate.Release();
                    Console.WriteLine(result);
                }
                void Work() { }
                int Compute() => 1;
            }
            """

    let fired = fires 1 "CR0163" source
    Assert.True(fired.Head.Fixes.IsEmpty)

// ---- CR0164 ----

[<Fact>]
let ``a check-then-assign static cache becomes LazyInitializer; an instance field and a locked one are quiet`` () =
    let source =
        csharp
            """
            using System.Collections.Generic;
            class C
            {
                static List<int> _cache;
                static readonly object _lock = new object();
                static List<int> _locked;
                static string _name;
                List<int> _mine;
                List<int> A()
                {
                    if (_cache == null) _cache = new List<int>();
                    return _cache;
                }
                static string Name => _name ??= "x";
                List<int> B()
                {
                    lock (_lock)
                    {
                        if (_locked == null) _locked = new List<int>();
                        return _locked;
                    }
                }
                List<int> D()
                {
                    if (_mine == null) _mine = new List<int>();
                    return _mine;
                }
            }
            """

    let fired = fires 2 "CR0164" source
    let fixedSource = fixAll "CR0164" source
    Assert.Contains("return LazyInitializer.EnsureInitialized(ref _cache, () => new List<int>());", fixedSource)
    Assert.Contains("""static string Name => LazyInitializer.EnsureInitialized(ref _name, () => "x");""", fixedSource)
    Assert.Contains("using System.Threading;", fixedSource)

[<Fact>]
let ``CR0164 fills a settings cache whose loader may answer null by CompareExchange: a missing file is retried as before``
    ()
    =
    let source =
        csharp
            """
            using System.Collections.Generic;
            interface ISettingsStore { Dictionary<string, string>? Load(); }
            static class Settings
            {
                public static ISettingsStore Store = null!;
                static Dictionary<string, string>? _values;
                static Dictionary<string, string>? _warm;
                static List<string>? _names;
                public static Dictionary<string, string>? Values()
                {
                    if (_values == null) _values = Store.Load();
                    return _values;
                }
                public static void Warm() { _warm ??= Store.Load(); }
                public static List<string>? Names => _names ??= LoadNames();
                static List<string>? LoadNames() => null;
            }
            """

    let fired = fires 3 "CR0164" source
    Assert.All(fired, (fun s -> Assert.NotEmpty s.Fixes))
    let fixedSource = fixAll "CR0164" source

    // the author's own `if` keeps its shape: only the assignment inside it changes
    Assert.Contains(
        csharp
            """
            if (_values == null) Interlocked.CompareExchange(ref _values, Store.Load(), null);
                    return _values;
            """,
        fixedSource
    )

    // a `??=` statement becomes a braced `if` (StyleCop SA1503): inline inside a one-line block
    Assert.Contains(
        "public static void Warm() { if (_warm is null) { Interlocked.CompareExchange(ref _warm, Store.Load(), null); } }",
        fixedSource
    )

    Assert.Contains(
        "public static List<string>? Names => _names ?? Interlocked.CompareExchange(ref _names, LoadNames(), null) ?? _names;",
        fixedSource
    )

    Assert.DoesNotContain("EnsureInitialized", fixedSource)
    Assert.Contains("using System.Threading;", fixedSource)

// ---- CR0165 ----

[<Fact>]
let ``a wrapping throw gains the caught exception as inner, naming an unnamed catch`` () =
    let source =
        csharp
            """
            using System;
            class SyncException : Exception
            {
                public SyncException(string message) : base(message) { }
                public SyncException(string message, Exception inner) : base(message, inner) { }
            }
            class C
            {
                void A(int id)
                {
                    try { Work(); }
                    catch (Exception ex) { throw new SyncException($"sync {id} failed"); }
                    try { Work(); }
                    catch (InvalidOperationException) { throw new SyncException("failed"); }
                    try { Work(); }
                    catch (Exception ex) { throw new SyncException("failed: " + ex.Message); }
                    try { Work(); }
                    catch (Exception ex) { throw new InvalidOperationException("no inner overload here?"); }
                }
                void Work() { }
            }
            """

    let fired = fires 3 "CR0165" source
    let fixedSource = fixAll "CR0165" source
    Assert.Contains("""catch (Exception ex) { throw new SyncException($"sync {id} failed", ex); }""", fixedSource)
    Assert.Contains("""catch (InvalidOperationException ex) { throw new SyncException("failed", ex); }""", fixedSource)
    Assert.Contains("""throw new InvalidOperationException("no inner overload here?", ex);""", fixedSource)
    Assert.Contains("""throw new SyncException("failed: " + ex.Message); }""", fixedSource)

[<Fact>]
let ``CR0165 leaves a domain exception without an inner-exception constructor alone`` () =
    let source =
        csharp
            """
            using System;
            class PaymentDeclinedException : Exception
            {
                public PaymentDeclinedException(string reason) : base(reason) { }
            }
            class PaymentGateway
            {
                public void Charge(string cardToken, decimal amount)
                {
                    try { Send(cardToken, amount); }
                    catch (TimeoutException) { throw new PaymentDeclinedException("gateway did not answer"); }
                }
                void Send(string cardToken, decimal amount) { }
            }
            """

    // no (string, Exception) constructor: there is nowhere to put the caught one
    Assert.Empty(suggestCode "CR0165" source)

// ---- CR0166 ----

[<Fact>]
let ``exception-driven parses become TryParse in the assignment, empty-catch and return forms`` () =
    let source =
        csharp
            """
            using System;
            class C
            {
                int _port;
                int A(string s)
                {
                    int v;
                    try
                    {
                        v = int.Parse(s);
                    }
                    catch (Exception)
                    {
                        v = -1;
                    }
                    return v;
                }
                int A2(string s)
                {
                    // FormatException alone leaves an overflow and a null to propagate: a note
                    int v;
                    try { v = int.Parse(s); } catch (FormatException) { v = -1; }
                    return v;
                }
                Guid B2(string s)
                {
                    // a Guid cannot overflow and s is not null here: the fix
                    Guid g;
                    try { g = Guid.Parse(s); } catch (FormatException) { g = Guid.Empty; }
                    return g;
                }
                Guid B3(string? s)
                {
                    Guid g;
                    try { g = Guid.Parse(s!); } catch (ArgumentException) { g = Guid.Empty; }
                    return g;
                }
                Guid B(string s)
                {
                    Guid g = default;
                    try { g = Guid.Parse(s); } catch { }
                    return g;
                }
                double D(string s)
                {
                    try
                    {
                        return double.Parse(s);
                    }
                    catch (Exception)
                    {
                        return 0.0;
                    }
                }
                void E(string s)
                {
                    try { _port = int.Parse(s); } catch (FormatException) { }
                }
                void F(string s)
                {
                    try
                    {
                        var n = int.Parse(s);
                        Console.WriteLine(n);
                    }
                    catch (FormatException ex)
                    {
                        Console.WriteLine(ex.Message);
                    }
                }
            }
            """

    let fired = suggestCode "CR0166" source
    // A, A2, B2, B, D, E, F fire; A, B2, B, D carry the fix; B3's ArgumentException never caught a format error
    assertFired 7 source fired
    Assert.Equal(4, fired |> List.filter (fun s -> not s.Fixes.IsEmpty) |> List.length)
    let fixedSource = fixAll "CR0166" source

    Assert.Contains(
        csharp
            """
                    if (!int.TryParse(s, out v))
                    {
                        v = -1;
                    }
                    return v;
            """,
        fixedSource
    )

    Assert.Contains("Guid.TryParse(s, out g);", fixedSource)
    Assert.Contains("try { v = int.Parse(s); } catch (FormatException) { v = -1; }", fixedSource)
    Assert.Contains("if (!Guid.TryParse(s, out g)) { g = Guid.Empty; }", fixedSource)
    Assert.Contains("try { g = Guid.Parse(s!); } catch (ArgumentException) { g = Guid.Empty; }", fixedSource)
    Assert.Contains("return double.TryParse(s, out var parsed) ? parsed : 0.0;", fixedSource)
    Assert.Contains("try { _port = int.Parse(s); } catch (FormatException) { }", fixedSource)

[<Fact>]
let ``CR0166 keeps a broad catch whose parse argument can throw on its own`` () =
    let source =
        csharp
            """
            using System;
            using System.Collections.Generic;
            using System.Globalization;
            class C
            {
                int A(string[] parts)
                {
                    int v;
                    try { v = int.Parse(parts[1]); } catch { v = 0; }
                    return v;
                }
                int B(Dictionary<string, string> d)
                {
                    try { return int.Parse(d["port"]); } catch (Exception) { return 0; }
                }
                int D(string s)
                {
                    int v;
                    try { v = int.Parse(s, CultureInfo.InvariantCulture); } catch { v = 0; }
                    return v;
                }
            }
            """

    // parts[1] and d["port"] were caught too; D's arguments cannot throw
    Assert.Equal<string list>([ "try" ], firedText source (suggestCode "CR0166" source))

    Assert.Contains("if (!int.TryParse(s, CultureInfo.InvariantCulture, out v)) { v = 0; }", fixAll "CR0166" source)

[<Fact>]
let ``CR0166 takes a user getter or method evaluating the argument: one that throws is the accepted residual`` () =
    let source =
        csharp
            """
            using System;
            using System.IO;
            static class Cfg
            {
                public static string Raw => File.ReadAllText("none.txt");
                public static string Field = "80";
            }
            class C
            {
                static string Norm(string s) => string.Format(s, 1);
                static string Prop { get { return string.Format("{bad", 1); } }
                int A1()
                {
                    int port;
                    try { port = int.Parse(Cfg.Raw); } catch (Exception) { port = 80; }
                    return port;
                }
                int A7()
                {
                    try { return int.Parse(Cfg.Raw); } catch (Exception) { return 80; }
                }
                Guid A5(string s)
                {
                    Guid g;
                    try { g = Guid.Parse(Norm(s)); } catch (FormatException) { g = Guid.Empty; }
                    return g;
                }
                bool A6()
                {
                    bool b;
                    try { b = bool.Parse(Prop); } catch (FormatException) { b = true; }
                    return b;
                }
                int Kept1()
                {
                    int port;
                    try { port = int.Parse(Cfg.Field); } catch (Exception) { port = 80; }
                    return port;
                }
                Guid Kept2(string s)
                {
                    Guid g;
                    try { g = Guid.Parse(s.Trim()); } catch (FormatException) { g = Guid.Empty; }
                    return g;
                }
            }
            """

    let fired = fires 6 "CR0166" source
    Assert.All(fired, fun s -> Assert.NotEmpty s.Fixes)
    let fixedSource = fixAll "CR0166" source
    Assert.Contains("if (!int.TryParse(Cfg.Raw, out port)) { port = 80; }", fixedSource)
    Assert.Contains("return int.TryParse(Cfg.Raw, out var parsed) ? parsed : 80;", fixedSource)
    Assert.Contains("if (!Guid.TryParse(Norm(s), out g)) { g = Guid.Empty; }", fixedSource)
    Assert.Contains("if (!bool.TryParse(Prop, out b)) { b = true; }", fixedSource)
    Assert.Contains("if (!int.TryParse(Cfg.Field, out port)) { port = 80; }", fixedSource)
    Assert.Contains("if (!Guid.TryParse(s.Trim(), out g)) { g = Guid.Empty; }", fixedSource)

[<Fact>]
let ``CR0166 takes an indexed or static auto-property argument, not a user conversion or an overridable getter`` () =
    let source =
        csharp
            """
            using System;
            using System.Collections.Generic;
            public static class Cfg { public static string Port { get; set; } = "80"; }
            public readonly struct W { public static implicit operator string(W w) => ""; }
            class C
            {
                Guid P04(string[] parts) { Guid g; try { g = Guid.Parse(parts[0]); } catch (FormatException) { g = Guid.Empty; } return g; }
                int P05() { int port; try { port = int.Parse(Cfg.Port); } catch (Exception) { port = 80; } return port; }
                Guid P11(List<string> ids) { Guid g; try { g = Guid.Parse(ids[0]); } catch (FormatException) { g = Guid.Empty; } return g; }
                int P07(W w) { int v; try { v = int.Parse(w); } catch { v = -1; } return v; }
                Guid P08(W w) { Guid g; try { g = Guid.Parse(w); } catch (FormatException) { g = Guid.Empty; } return g; }
                Guid P14(Exception ex) { Guid g; try { g = Guid.Parse(ex.Message); } catch (FormatException) { g = Guid.Empty; } return g; }
            }
            """

    let fired = suggestCode "CR0166" source
    let fixedSource = fixAll "CR0166" source
    Assert.Equal(3, fired |> List.filter (fun s -> not s.Fixes.IsEmpty) |> List.length)
    Assert.Contains("if (!Guid.TryParse(parts[0], out g)) { g = Guid.Empty; }", fixedSource)
    Assert.Contains("if (!int.TryParse(Cfg.Port, out port)) { port = 80; }", fixedSource)
    Assert.Contains("if (!Guid.TryParse(ids[0], out g)) { g = Guid.Empty; }", fixedSource)
    Assert.Contains("try { v = int.Parse(w); } catch { v = -1; }", fixedSource)
    Assert.Contains("try { g = Guid.Parse(w); } catch (FormatException) { g = Guid.Empty; }", fixedSource)
    Assert.Contains("try { g = Guid.Parse(ex.Message); } catch (FormatException) { g = Guid.Empty; }", fixedSource)

[<Fact>]
let ``CR0166 takes a virtual BCL getter but not Lazy, ThreadLocal or an Exception member`` () =
    let source =
        csharp
            """
            using System;
            using System.IO;
            using System.Threading;
            class C
            {
                Guid T14(Lazy<string> lazy) { Guid g; try { g = Guid.Parse(lazy.Value); } catch (FormatException) { g = Guid.Empty; } return g; }
                Guid T15(ThreadLocal<string> tl) { Guid g; try { g = Guid.Parse(tl.Value!); } catch (FormatException) { g = Guid.Empty; } return g; }
                Guid P14(Exception ex) { Guid g; try { g = Guid.Parse(ex.Message); } catch (FormatException) { g = Guid.Empty; } return g; }
                Guid Kept(FileSystemInfo info) { Guid g; try { g = Guid.Parse(info.Name); } catch (FormatException) { g = Guid.Empty; } return g; }
            }
            """

    let fired = suggestCode "CR0166" source
    Assert.Equal(1, fired |> List.filter (fun s -> not s.Fixes.IsEmpty) |> List.length)
    Assert.Contains("if (!Guid.TryParse(info.Name, out g)) { g = Guid.Empty; }", fixAll "CR0166" source)

[<Fact>]
let ``CR0166 follows a user method or getter three calls deep for a throwing shape, and keeps a target under a receiver that may be null``
    ()
    =
    let source =
        csharp
            """
            using System;
            public sealed class Box { public int V; }
            public sealed class Cfg { readonly string[] _parts = new string[0]; public string First => _parts[0]; public static string Raw => throw new InvalidOperationException(); }
            public sealed class TextBox { public string Text { get; set; } = "12"; }
            class C
            {
                static string Get(string s) => s.Substring(5);
                static string A(string s) => B(s);
                static string B(string s) => s.Substring(7);
                static string Norm(string s) => int.Parse(s).ToString();
                static string GetText() => "12";
                int X01(string s) { int v; try { v = int.Parse(Get(s)); } catch { v = -1; } return v; }
                int X11() { int v; try { v = int.Parse(A("12")); } catch { v = -1; } return v; }
                int X04(Cfg c) { int v; try { v = int.Parse(c.First); } catch { v = -1; } return v; }
                int X03() { int v; try { v = int.Parse(Cfg.Raw); } catch { v = -1; } return v; }
                Guid X02(string s) { Guid g; try { g = Guid.Parse(Norm(s)); } catch (FormatException) { g = Guid.Empty; } return g; }
                int X06(string s) { int v; try { v = int.Parse(s.Trim()); } catch { v = -1; } return v; }
                int X07(TextBox textBox) { int v; try { v = int.Parse(textBox.Text); } catch { v = -1; } return v; }
                int X08() { int v; try { v = int.Parse(GetText()); } catch { v = -1; } return v; }
                string X10b() { Box? other = null; try { other.V = int.Parse("12"); } catch { return "caught"; } return other.V.ToString(); }
                string X10c(Box other) { try { other.V = int.Parse("12"); } catch { return "caught"; } return other.V.ToString(); }
            }
            """

    let compilation, tree = compile source
    let model = compilation.GetSemanticModel(tree, false)

    let suggestions, failures =
        CSharp.Refactor.Roslyn.Rules.allWithFailures
            tree
            model
            (CSharp.Refactor.Roslyn.Context.forTree None compilation tree false)

    Assert.Empty failures
    let fired = suggestions |> List.filter (fun s -> s.Code = "CR0166")
    // a `Substring` one or two calls down, an indexer or a `throw` behind a
    // getter, and a null receiver the catch absorbed: kept (a nested parse
    // under a FormatException catch is the note); `Trim`, an auto-property,
    // a plain user method and a non-nullable receiver: the fix
    assertFired 5 source fired
    Assert.Equal(4, fired |> List.filter (fun s -> not s.Fixes.IsEmpty) |> List.length)
    let fixedSource = fixAll "CR0166" source
    Assert.Contains("if (!int.TryParse(s.Trim(), out v)) { v = -1; }", fixedSource)
    Assert.Contains("if (!int.TryParse(textBox.Text, out v)) { v = -1; }", fixedSource)
    Assert.Contains("if (!int.TryParse(GetText(), out v)) { v = -1; }", fixedSource)
    Assert.Contains("try { v = int.Parse(Get(s)); } catch { v = -1; }", fixedSource)
    Assert.Contains("""try { v = int.Parse(A("12")); } catch { v = -1; }""", fixedSource)
    Assert.Contains("try { v = int.Parse(c.First); } catch { v = -1; }", fixedSource)
    Assert.Contains("try { v = int.Parse(Cfg.Raw); } catch { v = -1; }", fixedSource)
    Assert.Contains("try { g = Guid.Parse(Norm(s)); } catch (FormatException) { g = Guid.Empty; }", fixedSource)
    Assert.Contains("""Box? other = null; try { other.V = int.Parse("12"); } catch { return "caught"; }""", fixedSource)

    Assert.Contains(
        """if (int.TryParse("12", out var parsed)) other.V = parsed; else { return "caught"; }""",
        fixedSource
    )

[<Fact>]
let ``CR0166 keeps the configured default port when the setting is bad: the parse goes through a fresh variable`` () =
    let source =
        csharp
            """
            using System;
            class ServerConfig
            {
                int retries = 3;
                public int Port(string s)
                {
                    int port = 8080;
                    try { port = int.Parse(s); } catch { }
                    return port;
                }
                public void Retries(string s)
                {
                    try { retries = int.Parse(s); } catch (Exception) { Console.WriteLine("bad retries, keeping " + retries); }
                }
                public int Timeout(string s, int parsed)
                {
                    int timeout = 30;
                    try
                    {
                        timeout = int.Parse(s);
                    }
                    catch (Exception)
                    {
                        Console.WriteLine("bad timeout");
                    }
                    return timeout + parsed;
                }
            }
            """

    let fired = fires 3 "CR0166" source
    Assert.All(fired, (fun s -> Assert.NotEmpty s.Fixes))
    let fixedSource = fixAll "CR0166" source

    Assert.Contains(
        csharp
            """
            int port = 8080;
                    if (int.TryParse(s, out var parsed)) port = parsed;

            """,
        fixedSource
    )

    Assert.Contains(
        """if (int.TryParse(s, out var parsed)) retries = parsed; else { Console.WriteLine("bad retries, keeping " + retries); }""",
        fixedSource
    )

    Assert.Contains(
        csharp
            """
                    if (int.TryParse(s, out var parsed2)) timeout = parsed2;
                    else
                    {
                        Console.WriteLine("bad timeout");
                    }
                    return timeout + parsed;
            """,
        fixedSource
    )

[<Fact>]
let ``CR0166 under an if with an else keeps that else for the outer if`` () =
    // a bare `if (int.TryParse(…)) port = parsed;` as the `if`'s body would
    // take `else port = 9090;` for its own: a bad setting would then fall
    // back to 9090, and no setting at all would keep 8080
    let source =
        csharp
            """
            using System;
            class ServerConfig
            {
                public int Port(bool useSetting, string s)
                {
                    int port = 8080;
                    if (useSetting)
                        try { port = int.Parse(s); } catch (Exception) { }
                    else
                        port = 9090;
                    return port;
                }
                public int Retries(bool useSetting, string s)
                {
                    int retries = 3;
                    if (useSetting)
                        try { retries = int.Parse(s); } catch (Exception) { Console.WriteLine("bad retries"); }
                    else
                        retries = 5;
                    return retries;
                }
            }
            """

    let fixedSource = fixAll "CR0166" source

    Assert.Contains(
        csharp
            """
                    if (useSetting)
                        { if (int.TryParse(s, out var parsed)) port = parsed; }
                    else
                        port = 9090;
            """,
        fixedSource
    )

    Assert.Contains(
        csharp
            """
                        { if (int.TryParse(s, out var parsed)) retries = parsed; else { Console.WriteLine("bad retries"); } }
                    else
                        retries = 5;
            """,
        fixedSource
    )

// ---- CR0167 / CR0168 ----

[<Fact>]
let ``floating equality and integer division into a floating target are noted`` () =
    let source =
        csharp
            """
            using System;
            class C
            {
                bool A(double a, double b, float f, int n) => a * 2 == b || a == b || f != 0 || Math.Round(a) == Math.Round(b) || n == n;
                double B(int sum, int count) => sum / count;
                double D(int sum, int count) => (double)sum / count;
                double E(int a) => a / 1;
                int F(int a, int b) => a / b;
                double G(int a, int b) => Math.Floor((double)(a / b));
                double H(int a, int b) => a / b * 1.0;
            }
            """

    let equality = suggestCode "CR0167" source
    Assert.Equal<string list>([ "a * 2 == b" ], firedText source equality)
    let division = suggestCode "CR0168" source
    Assert.Equal<string list>([ "sum / count"; "a / b" ], firedText source division)

    Assert.True(
        division
        |> List.forall (fun s -> s.Fixes |> List.forall (fun f -> f.EditorOnly))
    )

    Assert.Contains("(double)sum / count", applyFix source division.Head.Fixes.Head)

[<Fact>]
let ``CR0167 leaves a computed charge compared against a named zero constant alone`` () =
    let source =
        csharp
            """
            class ShippingQuote
            {
                const double FreeShipping = 0.0;
                public bool IsFree(double weightKg, double ratePerKg) => weightKg * ratePerKg == FreeShipping;
            }
            """

    // a product is exactly zero when a factor is: the constant is the sentinel
    Assert.Empty(suggestCode "CR0167" source)

[<Fact>]
let ``CR0168 never casts a pallet count in a sweep, the whole-number division may be meant`` () =
    let source =
        csharp
            """
            class PalletPlanner
            {
                public double FullPallets(int cartons, int cartonsPerPallet)
                {
                    double full = cartons / cartonsPerPallet;
                    return full;
                }
            }
            """

    Assert.Single(suggestCode "CR0168" source) |> ignore
    Assert.Equal(normalize source, fixAll "CR0168" source)

[<Fact>]
let ``CR0168 notes a page count rounded up after the integer division already truncated it`` () =
    // 21 items at 10 per page: Ceiling(21 / 10) is Ceiling(2) = 2 pages, not 3.
    // Floor and Truncate agree with the truncation; Ceiling and Round do not
    let source =
        csharp
            """
            using System;
            class Pager
            {
                public int Pages(int items, int pageSize) => (int)MathF.Ceiling(items / pageSize);
                public int Whole(int items, int pageSize) => (int)Math.Floor((double)(items / pageSize));
            }
            """

    Assert.Equal<string list>([ "items / pageSize" ], firedText source (suggestCode "CR0168" source))

// ---- CR0169 ----

[<Fact>]
let ``a local time compared with a UTC time is noted, through a once-written local; explicit kinds are quiet`` () =
    let source =
        csharp
            """
            using System;
            class C
            {
                DateTime _started = DateTime.UtcNow;
                bool A(DateTime stamp)
                {
                    var now = DateTime.Now;
                    var expired = now > _started.AddMinutes(5);
                    var age = DateTime.UtcNow - DateTime.Today;
                    var fine = DateTime.Now.ToUniversalTime() > _started;
                    var unknown = stamp > DateTime.UtcNow;
                    return expired || age.TotalDays > 1 || fine || unknown;
                }
            }
            """

    let fired = suggestCode "CR0169" source

    Assert.Equal<string list>(
        [ "now > _started.AddMinutes(5)"; "DateTime.UtcNow - DateTime.Today" ],
        firedText source fired
    )

// ---- CR0190 ----

[<Fact>]
let ``a date format with the wrong specifier of a pair is rewritten inside its literal`` () =
    let source =
        csharp
            """
            using System;
            class C
            {
                string A(DateTime d) => d.ToString("yyyyMMddhhmmss");
                string B(DateTime d) => d.ToString("yyyymmdd");
                string D(DateTime d) => d.ToString("yyyy-mm-dd");
                string E(DateTimeOffset d) => d.ToString("dd/mm/yyyy", System.Globalization.CultureInfo.InvariantCulture);
                string F(DateTime d) => d.ToString("HH:MM");
                string G(TimeOnly t) => t.ToString("HH:MM:ss");
                string H(DateTime d) => d.ToString("yyyyMMddHHMMss");
                string? I(DateTime? d) => d?.ToString("dd.mm.yyyy hh:mm");
                string J(DateTime d) => d.ToString(@"dd/mm/yyyy");
                string K(DateTime d) => d.ToString("yyyy-mm-dd HH:MM");
                string L(DateTime d) => $"{d:yyyymmdd}_{d:HH:MM}";
                string M(DateOnly d) => d.ToString("d/m/yyyy");
                string N(DateTime d) => d.ToString(format: "yyyy-mm-dd h:mm");
                string O(DateTime d) => d.ToString("dd/mm/yyyy HH:MM:ss");
            }
            """

    let fired = fires 15 "CR0190" source

    Assert.True(
        fired
        |> List.forall (fun s -> s.Fixes.Length = 1 && not s.Fixes.Head.EditorOnly)
    )

    Assert.Contains("12-hour clock", fired.Head.Message)
    Assert.Contains("minutes where the month belongs", fired.[1].Message)
    Assert.Contains("month where the minutes belong", fired.[4].Message)
    let fixedSource = fixAll "CR0190" source
    Assert.Contains("""A(DateTime d) => d.ToString("yyyyMMddHHmmss");""", fixedSource)
    Assert.Contains("""B(DateTime d) => d.ToString("yyyyMMdd");""", fixedSource)
    Assert.Contains("""D(DateTime d) => d.ToString("yyyy-MM-dd");""", fixedSource)
    Assert.Contains("""d.ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture);""", fixedSource)
    Assert.Contains("""F(DateTime d) => d.ToString("HH:mm");""", fixedSource)
    Assert.Contains("""G(TimeOnly t) => t.ToString("HH:mm:ss");""", fixedSource)
    Assert.Contains("""H(DateTime d) => d.ToString("yyyyMMddHHmmss");""", fixedSource)
    Assert.Contains("""d?.ToString("dd.MM.yyyy HH:mm");""", fixedSource)
    Assert.Contains("""d.ToString(@"dd/MM/yyyy");""", fixedSource)
    Assert.Contains("""K(DateTime d) => d.ToString("yyyy-MM-dd HH:mm");""", fixedSource)
    Assert.Contains("""$"{d:yyyyMMdd}_{d:HH:mm}";""", fixedSource)
    Assert.Contains("""M(DateOnly d) => d.ToString("d/M/yyyy");""", fixedSource)
    Assert.Contains("""d.ToString(format: "yyyy-MM-dd H:mm");""", fixedSource)
    Assert.Contains("""O(DateTime d) => d.ToString("dd/MM/yyyy HH:mm:ss");""", fixedSource)
    Assert.Empty(suggestCode "CR0190" fixedSource)

[<Fact>]
let ``CR0190 keeps the quoting of a raw literal`` () =
    let raw = "\"\"\""

    let source =
        (csharp
            """
            using System;
            class C
            {
                string A(DateTime d) => d.ToString(RAWyyyy-mm-dd hh:mmRAW);
            }
            """)
            .Replace("RAW", raw)

    Assert.Contains($"d.ToString({raw}yyyy-MM-dd HH:mm{raw});", fixAll "CR0190" source)

[<Fact>]
let ``CR0190 leaves TimeSpan, a designated 12-hour clock, quoted and escaped letters and unambiguous formats alone``
    ()
    =
    let source =
        csharp
            """
            using System;
            using System.Linq;
            class C
            {
                string A(TimeSpan t) => t.ToString("hh\\:mm");
                string B(DateTime d) => d.ToString("hh:mm tt");
                string D(DateTime d) => d.ToString("HH'h'mm");
                string E(DateTime d) => d.ToString("HH\\hmm");
                string F(DateTime d) => d.ToString("m") + d.ToString("M") + d.ToString("d");
                string G(DateTime d) => d.ToString("yyyyMMddHHmm");
                string H(DateTime d) => d.ToString("MM/dd/yyyy HH:mm");
                string I(DateTime d) => d.ToString("yyyy-mm-dd MM");
                string J(DateTime d) => d.ToString("HH:MM mm");
                string K(DateTime d) => d.ToString("%h");
                string L(DateTime d, string f) => d.ToString(f);
                string M(DateTime d) => d.ToString("mm:ss") + d.ToString("MM-dd") + d.ToString("HH:mm:ss");
                string N(DateTime d) => d.ToString("HHhmm");
                IQueryable<string> O(IQueryable<DateTime> xs) => xs.Select(x => x.ToString("yyyy-mm-dd"));
                FormattableString P(DateTime d) => $"{d:yyyy-mm-dd}";
                string Q(TimeSpan t) => $"{t:hh\\:mm}";
                string R(DateTime d) => d.ToString("HH:MMM");
                string S(DateTime d) => d.ToString("yyyy-MM-dd mm");
                string T(DateTime d) => d.ToString("yyyy'-'mm'-'dd");
                string U(DateTime d) => d.ToString(@"yyyy\-mm\-dd") + d.ToString("yyyy\\mm");
                string V(DateTime d) => d.ToString("yyyy-mm-dd 'x") + d.ToString("yyyy\\-mm\\-dd");
                string W(DateTime d) => d.ToString("yyyy-mm-dd\t");
            }
            """

    Assert.Empty(suggestCode "CR0190" source)

[<Fact>]
let ``CR0190 is quiet in a test file, whose formats are the text it pins`` () =
    let source =
        csharp
            """
            using System;
            using Xunit;
            public class C
            {
                [Fact]
                public void A() => Assert.Equal("2024-00-01", new DateTime(2024, 1, 1).ToString("yyyy-mm-dd"));
            }
            """

    Assert.Empty(suggestCode "CR0190" source)

[<Fact>]
let ``CR0190 at a ParseExact is an editor offer a sweep never applies`` () =
    let source =
        csharp
            """
            using System;
            using System.Globalization;
            class C
            {
                DateTime A(string s) => DateTime.ParseExact(s, "yyyy-mm-dd", CultureInfo.InvariantCulture);
                bool B(string s, out DateTime d) =>
                    DateTime.TryParseExact(s, new[] { "yyyymmdd", "yyyy-MM-dd" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out d);
                bool D(string s, out DateOnly d) => DateOnly.TryParseExact(s, "dd/MM/yyyy", out d);
            }
            """

    let fired = suggestCode "CR0190" source
    Assert.Equal<string list>([ "\"yyyy-mm-dd\""; "\"yyyymmdd\"" ], firedText source fired)
    Assert.True(fired |> List.forall (fun s -> s.Fixes.Length = 1 && s.Fixes.Head.EditorOnly))
    Assert.Equal(normalize source, fixAll "CR0190" source)

    Assert.Contains(
        """DateTime.ParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture)""",
        applyFix source fired.Head.Fixes.Head
    )

// ---- CR0191 ----

[<Fact>]
let ``a date built from the year of one instant and the month of it shifted reads both from the shifted one`` () =
    let source =
        csharp
            """
            using System;
            class C
            {
                DateTime A(DateTime now) => new DateTime(now.Year, now.AddMonths(-1).Month, 25);
                DateTime B() => new DateTime(DateTime.UtcNow.AddMonths(1).Year, DateTime.UtcNow.Month, 1);
                DateOnly D(DateOnly today, int k) => new(today.Year, today.AddDays(k).Month, 1);
                DateTimeOffset E(DateTimeOffset now) => new DateTimeOffset(now.Year, (now.AddMonths(-1)).Month, 1, 0, 0, 0, TimeSpan.Zero);
            }
            """

    let fired = fires 4 "CR0191" source
    // B reads the YEAR from the shifted instant: moving the month to it changes every date,
    // so that one is the editor's
    Assert.Equal<bool list>(
        [ false; true; false; false ],
        fired |> List.map (fun s -> (List.exactlyOne s.Fixes).EditorOnly)
    )

    let fixedSource = fixAll "CR0191" source
    Assert.Contains("new DateTime(now.AddMonths(-1).Year, now.AddMonths(-1).Month, 25);", fixedSource)
    Assert.Contains("new DateTime(DateTime.UtcNow.AddMonths(1).Year, DateTime.UtcNow.Month, 1);", fixedSource)

    Assert.Contains(
        "new DateTime(DateTime.UtcNow.AddMonths(1).Year, DateTime.UtcNow.AddMonths(1).Month, 1);",
        applyFix source fired.[1].Fixes.Head
    )

    Assert.Contains("new(today.AddDays(k).Year, today.AddDays(k).Month, 1);", fixedSource)

    Assert.Contains(
        "new DateTimeOffset(now.AddMonths(-1).Year, (now.AddMonths(-1)).Month, 1, 0, 0, 0, TimeSpan.Zero);",
        fixedSource
    )

    fires 1 "CR0191" fixedSource |> ignore

[<Fact>]
let ``CR0191 is a note where the receiver or the amount runs a call, where AddYears loses its shift and for a day of another instant``
    ()
    =
    let source =
        csharp
            """
            using System;
            class C
            {
                DateTime Clock() => DateTime.UtcNow;
                int Shift() => -1;
                DateTime A() => new DateTime(Clock().Year, Clock().AddMonths(-1).Month, 25);
                DateTime B(DateTime now) => new DateTime(now.Year, now.AddMonths(Shift()).Month, 1);
                DateTime D(DateTime now) => new DateTime(now.Year, now.Month, now.AddDays(1).Day);
                DateTime E(DateTime now) => new DateTime(now.Year, now.AddYears(1).Month, 1);
                DateTime F(DateTime now) => new DateTime(now.AddMonths(1).Year, now.AddMonths(1).Month, now.Day);
            }
            """

    let fired = fires 5 "CR0191" source
    Assert.True(fired |> List.forall (fun s -> s.Fixes.IsEmpty))
    Assert.Contains("the day can belong to another month", fired.[2].Message)
    Assert.Contains("the shift is lost", fired.[3].Message)
    Assert.Equal(normalize source, fixAll "CR0191" source)

[<Fact>]
let ``CR0191 is quiet for one instant, two unrelated instants and a year AddYears shifted beside the plain month`` () =
    let source =
        csharp
            """
            using System;
            class C
            {
                DateTime A(DateTime now) => new DateTime(now.Year, now.Month, 1);
                DateTime B(DateTime now) => new DateTime(now.AddMonths(-1).Year, now.AddMonths(-1).Month, 1);
                DateTime D(DateTime now, DateTime other) => new DateTime(now.Year, other.Month, other.Day);
                DateTime E(DateTime now) => new DateTime(now.AddYears(1).Year, now.Month, 1);
                DateTime F(DateTime now) => new DateTime(now.Year, now.Month, now.Day);
                DateTime G(DateTime now) => new DateTime(now.Year, 12, 31);
                TimeSpan H(DateTime now) => new TimeSpan(now.Year, now.AddMonths(-1).Month, 1);
            }
            """

    Assert.Empty(suggestCode "CR0191" source)

// ---- CR0192 ----

[<Fact>]
let ``two comparisons that settle their chain are noted, the operator swap an editor offer for a chain of two`` () =
    let source =
        csharp
            """
            enum Color { Red, Green, Blue }
            class C
            {
                const int Limit = 10;
                int P { get; set; }
                bool A(int x) => x != 1 || x != 2;
                bool B(string s) => s == "a" && s == "b";
                bool D(Color c) => c != Color.Red || c != Color.Blue;
                bool E(int x, int lo) => x > lo && x < lo;
                bool F(int x, int lo) => lo < x && lo >= x;
                bool G(int x, bool other) => x != 1 || other || x != Limit;
                bool H(int x) => 1 == x && (x == 2);
                bool I(C o, bool other) => other && (o.P != 1 || o.P != 2);
            }
            """

    let fired = fires 8 "CR0192" source

    Assert.Equal<string list>(
        [
            "x != 1 || x != 2"
            "s == \"a\" && s == \"b\""
            "c != Color.Red || c != Color.Blue"
            "x > lo && x < lo"
            "lo < x && lo >= x"
            "x != 1 || other || x != Limit"
            "1 == x && (x == 2)"
            "o.P != 1 || o.P != 2"
        ],
        firedText source fired
    )

    // an offer where the chain is the two comparisons, a bare note otherwise
    Assert.Equal<int list>([ 1; 1; 1; 0; 0; 0; 1; 1 ], fired |> List.map (fun s -> s.Fixes.Length))
    Assert.True(fired |> List.forall (fun s -> s.Fixes |> List.forall (fun f -> f.EditorOnly)))
    Assert.Contains("always true", fired.Head.Message)
    Assert.Contains("always false", fired.[1].Message)
    Assert.Contains("always false", fired.[3].Message)
    Assert.Equal(normalize source, fixAll "CR0192" source)
    Assert.Contains("bool A(int x) => x != 1 && x != 2;", applyFix source fired.Head.Fixes.Head)
    Assert.Contains("""bool B(string s) => s == "a" || s == "b";""", applyFix source fired.[1].Fixes.Head)
    Assert.Contains("other && (o.P != 1 && o.P != 2);", applyFix source fired.[7].Fixes.Head)

[<Fact>]
let ``CR0192 is quiet for one constant twice, an equality range, other subjects, calls and steps, and non-constants``
    ()
    =
    let source =
        csharp
            """
            class C
            {
                const int One = 1;
                int n;
                int Next() => n++;
                bool A(int x) => x != 1 || x != One;
                bool B(int x) => x >= 1 && x <= 1;
                bool D(int x, int y) => x != 1 || y != 2;
                bool E(int x) => x != 1 && x != 2;
                bool F(int x) => x == 1 || x == 2;
                bool G() => Next() != 1 || Next() != 2;
                bool H(int[] xs, int i) => xs[i++] != 1 || xs[i++] != 2;
                bool I(double d) => d != d;
                bool J(int x, int a, int b) => x != a || x != b;
                bool K(int x, int lo, int hi) => x > lo && x < hi;
                bool L(int x, int lo) => x > lo || x < lo;
                bool M(int x, bool other) => x != 1 || other && x != 2;
            }
            """

    Assert.Empty(suggestCode "CR0192" source)

// ---- CR0193 ----

[<Fact>]
let ``a Value read in the branch its own test proved empty is noted`` () =
    let source =
        csharp
            """
            class C
            {
                int? _f;
                int A(int? x) { if (x.HasValue) { return 1; } else { return x.Value; } }
                int B(int? x) { if (!x.HasValue) { return x.Value; } return 0; }
                int D(int? x) { if (x == null) return x.Value; return 0; }
                int E(int? x) => x.HasValue ? 1 : x.Value + 1;
                int F(int? x) => x == null ? x.Value : 2;
                int G() { if (_f is null) { return _f.Value; } return 0; }
                int H(int? x) { if (!(x != null)) { return x.Value; } return 0; }
                int I(int? x) { if (x.HasValue) { return 1; } else if (x is not null) { return 2; } else { return x.Value; } }
            }
            """

    let fired = fires 8 "CR0193" source

    Assert.Equal<string list>(
        [
            "x.Value"
            "x.Value"
            "x.Value"
            "x.Value"
            "x.Value"
            "_f.Value"
            "x.Value"
            "x.Value"
        ],
        firedText source fired
    )

    Assert.True(fired |> List.forall (fun s -> s.Fixes.IsEmpty))
    Assert.Contains("InvalidOperationException", fired.Head.Message)

    // the compiler says so too, where nullable analysis is on
    let compilation, _ = compile source
    Assert.Contains(compilation.GetDiagnostics(), (fun d -> d.Id = "CS8629"))

[<Fact>]
let ``CR0193 is quiet where the value is present, written in the branch, read in a closure or under a joined test`` () =
    let source =
        csharp
            """
            using System;
            class C
            {
                int? _f;
                void Load() { _f = 1; }
                void Fill(out int? v) { v = 1; }
                int A(int? x) { if (x.HasValue) { return x.Value; } return 0; }
                int B(int? x) { if (!x.HasValue) { x = 5; return x.Value; } return 0; }
                int D(int? x) { if (x == null) { Fill(out x); return x.Value; } return 0; }
                int E(int? x, bool other) { if (!x.HasValue || other) { return x.Value; } return 0; }
                int F(int? x, bool other) { if (!x.HasValue && other) { return x.Value; } return 0; }
                Func<int> G(int? x) { if (!x.HasValue) { return () => x.Value; } return () => 0; }
                int H() { if (!_f.HasValue) { Load(); return _f.Value; } return 0; }
                int I(string? s) { if (s == null) { return 0; } return s.Length; }
                int J(int? x) { if (!x.HasValue) { return 0; } return x.Value; }
                int K(int? x) { Action fill = () => x = 1; if (!x.HasValue) { fill(); return x.Value; } return 0; }
                int L(int? x) => x.HasValue ? x.Value : 0;
                string M(int? x) { if (x == null) { return nameof(x.Value); } return ""; }
            }
            """

    Assert.Empty(suggestCode "CR0193" source)

// ---- CR0194 ----

[<Fact>]
let ``a dropped result of an immutable collection or string call is noted, the assignment an editor offer`` () =
    let source =
        csharp
            """
            using System.Collections.Immutable;
            class C
            {
                ImmutableList<int> _items = ImmutableList<int>.Empty;
                void A(ImmutableList<int> xs) { xs.Add(1); }
                void B(ImmutableArray<int> xs) { xs.RemoveAt(0); }
                void D(IImmutableDictionary<string, int> d) { d.SetItem("a", 1); }
                void E(ImmutableList<int> xs) { _ = xs.Add(1); }
                void F() { _items.Add(1).Add(2); }
                void G(ImmutableHashSet<int> s, ImmutableStack<int> st, ImmutableQueue<int> q, ImmutableSortedSet<int> ss)
                {
                    s.Union(new[] { 1 });
                    st.Push(1);
                    q.Enqueue(1);
                    ss.Clear();
                }
                void H(string s) { s.Trim(); s.Replace("a", "b"); }
                void I(ImmutableList<int>? xs) { xs?.Add(1); }
            }
            """

    let fired = fires 12 "CR0194" source

    Assert.Equal<string list>(
        [
            "xs.Add(1)"
            "xs.RemoveAt(0)"
            "d.SetItem(\"a\", 1)"
            "_ = xs.Add(1)"
            "_items.Add(1).Add(2)"
            "s.Union(new[] { 1 })"
            "st.Push(1)"
            "q.Enqueue(1)"
            "ss.Clear()"
            "s.Trim()"
            "s.Replace(\"a\", \"b\")"
            "xs?.Add(1)"
        ],
        firedText source fired
    )

    // no offer for a discard, a field or chain receiver, a conditional call
    Assert.Equal<int list>([ 1; 1; 1; 0; 0; 1; 1; 1; 1; 1; 1; 0 ], fired |> List.map (fun s -> s.Fixes.Length))
    Assert.True(fired |> List.forall (fun s -> s.Fixes |> List.forall (fun f -> f.EditorOnly)))
    Assert.Contains("ImmutableList is immutable", fired.Head.Message)
    Assert.Equal(normalize source, fixAll "CR0194" source)
    Assert.Contains("void A(ImmutableList<int> xs) { xs = xs.Add(1); }", applyFix source fired.Head.Fixes.Head)
    Assert.Contains("{ s = s.Trim(); s.Replace", applyFix source fired.[9].Fixes.Head)

[<Fact>]
let ``CR0194 is quiet for a kept result, a builder, an out result, mutable collections and a discarded string`` () =
    let source =
        csharp
            """
            using System.Collections.Immutable;
            class C
            {
                ImmutableList<int> A(ImmutableList<int> xs) { xs = xs.Add(1); return xs.Add(2); }
                void B(ImmutableList<int>.Builder b) { b.Add(1); b.Remove(1); }
                void D(ImmutableStack<int> st) { st.Pop(out var top); System.Console.WriteLine(top); }
                void E(System.Collections.Generic.List<int> xs) { xs.Add(1); }
                void F(string s) { _ = s.Trim(); }
                ImmutableList<int> G(ImmutableList<int> xs) { var ys = xs.Add(1).Add(2); return ys; }
                void H(ImmutableList<int> xs) { xs.ForEach(x => { }); xs.CopyTo(new int[1]); }
                void I(System.Text.StringBuilder sb) { sb.Append("a"); }
            }
            """

    Assert.Empty(suggestCode "CR0194" source)

[<Fact>]
let ``CR0194 leaves the string shape to CA1806 where that rule runs, and keeps the collections`` () =
    let source =
        csharp
            """
            using System.Collections.Immutable;
            class C
            {
                void A(ImmutableList<int> xs, string s) { xs.Add(1); s.Trim(); }
            }
            """

    let microsoft =
        Some(
            FakeOptions(
                dict
                    [
                        "csharp_refactor.skip_microsoft_duplicates", "true"
                        "dotnet_diagnostic.CA1806.severity", "suggestion"
                    ]
            )
            :> Microsoft.CodeAnalysis.Diagnostics.AnalyzerConfigOptions
        )

    Assert.Equal<string list>([ "xs.Add(1)" ], firedText source (suggestCodeWith microsoft "CR0194" source))
    Assert.Equal<string list>([ "xs.Add(1)"; "s.Trim()" ], firedText source (suggestCode "CR0194" source))

// ---- review 2026-10-03: CR0190 – CR0194, CR0105 ----

[<Fact>]
let ``review 2026-10-03 CR0190 leaves a 12-hour clock to the editor where the designator is rendered apart or the format is no timestamp``
    ()
    =
    let source =
        csharp
            """
            using System;
            class C
            {
                string A(DateTime d) => d.ToString("hh:mm") + " " + d.ToString("tt");
                string B(DateTime d) => d.ToString("yyyy-MM-dd hh:mm") + (d.Hour < 12 ? " am" : " pm");
                string D(DateTime? d) => d?.ToString("hh") ?? "";
                string E(DateTime d) => d.ToString("yyyy-mm-dd hh:mm");
            }
            """

    let fired = fires 4 "CR0190" source
    // the minutes-for-month repair is still a sweep's; the hours are not
    let fixedSource = fixAll "CR0190" source
    Assert.Contains("""d.ToString("hh:mm") + " " + d.ToString("tt");""", fixedSource)
    Assert.Contains("""d.ToString("yyyy-MM-dd hh:mm") + (d.Hour""", fixedSource)
    Assert.Contains("""d?.ToString("hh") ?? "";""", fixedSource)
    Assert.Contains("""E(DateTime d) => d.ToString("yyyy-MM-dd hh:mm");""", fixedSource)
    // the editor still offers the whole repair
    Assert.True(fired |> List.forall (fun s -> (List.last s.Fixes).EditorOnly))
    Assert.Contains("""d.ToString("yyyy-MM-dd HH:mm");""", applyFix source (List.last fired.[3].Fixes))
    Assert.Contains("""d.ToString("HH:mm") + " " """.TrimEnd(), applyFix source (List.last fired.Head.Fixes))

[<Fact>]
let ``review 2026-10-03 CR0190 sweeps the 12-hour clock of a timestamp only where no parser reads that clock`` () =
    let timestamp =
        csharp
            """
            using System;
            class C
            {
                string A(DateTime d) => d.ToString("yyyy-MM-dd hh:mm:ss");
                string B(DateTime d) => d.ToString("hh:mm");
            }
            """

    let fixedSource = fixAll "CR0190" timestamp
    Assert.Contains("""d.ToString("yyyy-MM-dd HH:mm:ss");""", fixedSource)
    Assert.Contains("""B(DateTime d) => d.ToString("hh:mm");""", fixedSource)

    let roundTrip =
        csharp
            """
            using System;
            using System.Globalization;
            class C
            {
                string A(DateTime d) => d.ToString("yyyy-MM-dd hh:mm:ss");
                DateTime B(string s) => DateTime.ParseExact(s, "yyyy-MM-dd hh:mm:ss", CultureInfo.InvariantCulture);
            }
            """

    fires 2 "CR0190" roundTrip |> ignore
    Assert.Equal(normalize roundTrip, fixAll "CR0190" roundTrip)

[<Fact>]
let ``review 2026-10-03 CR0190 is quiet on TimeSpan in every spelling, a designated hole, string.Format, a const and an attribute``
    ()
    =
    let source =
        csharp
            """
            using System;
            using System.Globalization;
            class DisplayAttribute : Attribute { public string Format { get; set; } = ""; }
            class C
            {
                const string Stamp = "yyyy-mm-dd";
                [Display(Format = "yyyy-mm-dd")] public DateTime When { get; set; }
                string A(TimeSpan t) => t.ToString("hh\\:mm") + t.ToString(@"hh\:mm") + $"{t:hh\\:mm}";
                string B(DateTime d) => $"{d:hh:mm tt}";
                string D(DateTime d) => string.Format("{0:yyyy-mm-dd}", d);
                string E(DateTime d) => d.ToString(Stamp);
                DateTime F(string s) => DateTime.ParseExact(s, Stamp, CultureInfo.InvariantCulture);
            }
            """

    Assert.Empty(suggestCode "CR0190" source)

[<Fact>]
let ``review 2026-10-03 CR0191 repeats nothing that runs, reads named arguments by name and sweeps only the year`` () =
    let source =
        csharp
            """
            using System;
            class C
            {
                int n;
                DateTime Next() => DateTime.UtcNow.AddDays(n++);
                DateTime A(DateTime now, TimeSpan offset) => new DateTime((now + offset).Year, (now + offset).AddMonths(-1).Month, 1);
                DateTime B() => new DateTime(Next().Year, Next().AddMonths(-1).Month, 1);
                DateTime D(DateTime a) => new DateTime(month: a.AddMonths(-1).Month, year: a.Year, day: 1);
                DateTime E(DateTime[] xs, int i) => new DateTime(xs[i++].Year, xs[i++].AddMonths(-1).Month, 1);
                DateTime F(DateTime now) => new DateTime(now.AddMonths(1).Year, now.Month, 1);
                DateTime G(DateTime now) => new DateTime(now.Year, (now).AddMonths(-1).Month, 1);
            }
            """

    let fired = suggestCode "CR0191" source
    // A and B: notes; D and E: quiet; F: the month would change every month, an editor offer; G: the fix
    Assert.Equal<int list>([ 0; 0; 1; 1 ], fired |> List.map (fun s -> s.Fixes.Length))
    Assert.True(fired.[2].Fixes.Head.EditorOnly)
    let fixedSource = fixAll "CR0191" source
    Assert.Contains("new DateTime((now).AddMonths(-1).Year, (now).AddMonths(-1).Month, 1);", fixedSource)
    // and nothing else moved
    Assert.Equal(
        (normalize source).Replace("new DateTime(now.Year, (now)", "new DateTime((now).AddMonths(-1).Year, (now)"),
        fixedSource
    )

[<Fact>]
let ``review 2026-10-03 CR0105 reads only a Convert bound to the string overload with a string written`` () =
    let source =
        csharp
            """
            using System;
            using System.Globalization;
            using System.Linq;
            class C
            {
                decimal A(object o) => Convert.ToDecimal(o);
                decimal B(int i) => Convert.ToDecimal(i);
                decimal D(string s) => Convert.ToDecimal(s, CultureInfo.InvariantCulture);
                decimal E() => Convert.ToDecimal(null);
                IQueryable<DateTime> G(IQueryable<string> xs) => xs.Select(x => Convert.ToDateTime(x));
                string H() => nameof(Convert.ToDateTime);
            }
            """

    Assert.Empty(suggestCode "CR0105" source)

    // a dynamic argument binds at run time: nothing to say, and nothing thrown
    let dynamicSource =
        csharp
            """
            using System;
            class C
            {
                decimal F(dynamic v) => Convert.ToDecimal(v);
            }
            """

    let compilation, tree = compile dynamicSource

    let suggestions, failures =
        CSharp.Refactor.Roslyn.Rules.allWithFailures
            tree
            (compilation.GetSemanticModel(tree, false))
            (CSharp.Refactor.Roslyn.Context.forTree None compilation tree false)

    Assert.Empty failures
    Assert.DoesNotContain(suggestions, (fun s -> s.Code = "CR0105"))

[<Fact>]
let ``review 2026-10-03 CR0192 compares constants by value and only through an operator of the language or the BCL``
    ()
    =
    let source =
        csharp
            """
            using System;
            enum E { A = 1, B = 1, C = 2 }
            struct Money
            {
                public static bool operator ==(Money a, int b) => true;
                public static bool operator !=(Money a, int b) => true;
                public override bool Equals(object? o) => false;
                public override int GetHashCode() => 0;
            }
            class C
            {
                const int One = 1;
                bool A(int x) => x != One || x != 1;
                bool B(E e) => e != E.A || e != E.B;
                bool D(int x) => x != 1 || x != 1.0;
                bool F(char c) => c != 'a' || c != 97;
                bool G(Money m) => m != 1 || m != 2;
                bool H(int x, bool a) => a || x == 1 && x == 2;
                bool I(int? x) => x != null || x != 0;
                bool J(string s) => s != "a" || s != "A";
                bool K(double d) => d != double.NaN || d != 1.0;
                bool L(DateTime a, DateTime b) => a > b && a < b;
            }
            """

    let fired = suggestCode "CR0192" source

    Assert.Equal<string list>(
        [
            "x == 1 && x == 2"
            "x != null || x != 0"
            "s != \"a\" || s != \"A\""
            "d != double.NaN || d != 1.0"
            "a > b && a < b"
        ],
        firedText source fired
    )

    // the swap keeps the grouping: `||` binds looser than the `&&` it replaces
    Assert.Contains("a || x == 1 || x == 2;", applyFix source fired.Head.Fixes.Head)

[<Fact>]
let ``review 2026-10-03 CR0193 is quiet where a closure, an alias or a coalescing assignment fills the value, and on a look-alike type``
    ()
    =
    let source =
        csharp
            """
            class Box { public bool HasValue => false; public int Value => 1; }
            class C
            {
                int A(int? x) { void Fill() { x = 1; } if (!x.HasValue) { Fill(); return x.Value; } return 0; }
                int B(int? x) { if (x == null) { x ??= 3; return x.Value; } return 0; }
                int D(int? x) { if (x is not { } v) { return x.GetValueOrDefault(); } return v; }
                int E(Box b) { if (!b.HasValue) { return b.Value; } return 0; }
                int F(int? x) { ref int? r = ref x; if (!x.HasValue) { r = 1; return x.Value; } return 0; }
                int G(int? x) => !x.HasValue ? x.Value : 1;
                int H(int? x) => x.HasValue ? 1 : x.Value;
            }
            """

    let fired = suggestCode "CR0193" source
    // G and H only
    assertFired 2 source fired
    let afterF = (normalize source).IndexOf "int G("
    Assert.True(fired |> List.forall (fun s -> s.Span.Start > afterF))

    let disabled =
        csharp
            """
            #nullable disable
            class C
            {
                int B(int? x) { if (!x.HasValue) { return x.Value; } return 0; }
            }
            """

    fires 1 "CR0193" disabled |> ignore

[<Fact>]
let ``review 2026-10-03 CR0194 reads persistent updates only, and offers the assignment only to a variable of the statement's own function``
    ()
    =
    let source =
        csharp
            """
            using System;
            using System.Collections.Generic;
            using System.Collections.Immutable;
            using System.Linq;
            using System.Text;
            class C
            {
                ImmutableList<int> _f = ImmutableList<int>.Empty;
                ImmutableList<int> P { get; set; } = ImmutableList<int>.Empty;
                int Touch(int x) => x;
                void A(IEnumerable<int> xs) { xs.Select(Touch).ToImmutableList(); }
                void B(int x) { ImmutableInterlocked.Update(ref _f, l => l.Add(x)); }
                void D(string s, char[] buffer) { s.CopyTo(0, buffer, 0, 1); s.GetHashCode(); s.ToString(); }
                void E(StringBuilder sb) { _ = sb.Append("x"); }
                void F(in ImmutableList<int> xs) { xs.Add(1); }
                void G(IEnumerable<ImmutableList<int>> all) { foreach (var xs in all) { xs.Add(1); } }
                void H() { P.Add(1); }
                Action I(ImmutableList<int> xs) => () => { xs.Add(1); };
                Func<ImmutableList<int>, int> J() => xs => { xs.Add(1); return 0; };
            }
            """

    let fired = suggestCode "CR0194" source

    Assert.Equal<string list>(
        [ "xs.Add(1)"; "xs.Add(1)"; "P.Add(1)"; "xs.Add(1)"; "xs.Add(1)" ],
        firedText source fired
    )

    Assert.Equal<int list>([ 0; 0; 0; 0; 1 ], fired |> List.map (fun s -> s.Fixes.Length))

[<Fact>]
let ``review 2026-10-03 the date and defect rules survive broken syntax, error types and top-level statements`` () =
    let run (source: string) =
        let compilation, tree = compile source

        let suggestions, failures =
            CSharp.Refactor.Roslyn.Rules.allWithFailures
                tree
                (compilation.GetSemanticModel(tree, false))
                (CSharp.Refactor.Roslyn.Context.forTree None compilation tree false)

        Assert.True(failures.IsEmpty, sprintf "%A" failures)
        suggestions |> List.map (fun s -> s.Code)

    run (
        csharp
            """
            using System;
            using System.Collections.Immutable;
            class C
            {
                string A(DateTime d) => d.ToString(;
                DateTime B(DateTime now) => new DateTime(now.Year, , 1);
                decimal D(string s) => Convert.ToDecimal();
                bool E(int x) => x != || x != 2;
                int F(int? x) { if (!x.HasValue) { return x.; } return 0; }
                void G(ImmutableList<int> xs) { xs.Add(; }
                string I(Unknown d) => d.ToString("yyyy-mm-dd") + new Unknown(d.Year, d.AddMonths(1).Month, 1);
                bool J(Unknown x) => x != 1 || x != 2;
                string H(DateTime d) => $"{d:}" + d.ToString("yyyy-mm-dd
            }
            """
    )
    |> ignore

    let topLevel =
        run (
            csharp
                """
                using System;
                int? x = null;
                if (!x.HasValue) { Console.WriteLine(x.Value); }
                var s = DateTime.Now.ToString("yyyy-mm-dd");
                Console.WriteLine(s);
                """
        )

    Assert.Contains("CR0193", topLevel)
    Assert.Contains("CR0190", topLevel)

// ---- CR0170 ----

[<Fact>]
let ``a working loop under an unobserved token gets the check; an observed loop and a pure loop are quiet`` () =
    let source =
        csharp
            """
            using System.Threading;
            using System.Threading.Tasks;
            class C
            {
                async Task A(string[] items, CancellationToken ct)
                {
                    foreach (var item in items)
                    {
                        await Process(item);
                    }
                    foreach (var item in items)
                    {
                        await Process(item, ct);
                    }
                    var total = 0;
                    for (int i = 0; i < items.Length; i++)
                    {
                        total += items[i].Length;
                    }
                    while (true)
                    {
                        if (ct.IsCancellationRequested) break;
                        await Process("x");
                    }
                }
                Task Process(string item) => Task.CompletedTask;
                Task Process(string item, CancellationToken ct) => Task.CompletedTask;
            }
            """

    let fired = fires 1 "CR0170" source
    let fixedSource = fixAll "CR0170" source

    Assert.Contains(
        csharp
            """
                    foreach (var item in items)
                    {
                        ct.ThrowIfCancellationRequested();
                        await Process(item);
                    }
            """,
        fixedSource
    )

// ---- CR0171 ----

[<Fact>]
let ``a filter loop becomes RemoveAll, another mutation enumerates a snapshot, mutate-and-break is quiet`` () =
    let source =
        csharp
            """
            using System.Collections.Generic;
            class C
            {
                void A(List<int> xs, Dictionary<string, int> map, List<string> names)
                {
                    foreach (var x in xs)
                    {
                        if (x < 0) xs.Remove(x);
                    }
                    foreach (var name in names)
                    {
                        if (name.Length == 0)
                        {
                            names.Add("fresh");
                        }
                    }
                    foreach (var x in xs)
                    {
                        if (x == 7)
                        {
                            xs.Remove(x);
                            break;
                        }
                    }
                    foreach (var x in xs.ToArray())
                    {
                        xs.Remove(x);
                    }
                }
            }
            """

    let fired = fires 2 "CR0171" source
    let fixedSource = fixAll "CR0171" source
    Assert.Contains("xs.RemoveAll(x => x < 0);", fixedSource)
    Assert.Contains("foreach (var name in names.ToList())", fixedSource)
    Assert.Contains("using System.Linq;", fixedSource)

// ---- CR0164: the `??` spelling ----

[<Fact>]
let ``CR0164 reads the older coalescing spelling of a static cache as the same race; an instance field and a locked one are quiet``
    ()
    =
    let source =
        csharp
            """
            using System.Collections.Generic;
            class C
            {
                static List<int>? _cache;
                static string? _text;
                List<int>? _mine;
                static readonly object _gate = new object();
                static List<int> Cache => _cache ?? (_cache = new List<int>());
                static string? Load() => null;
                static string? Text() { return _text ?? (_text = Load()); }
                static int Count() => (_cache ?? (_cache = new List<int>())).Count;
                List<int> Mine => _mine ?? (_mine = new List<int>());
                static List<int> Locked() { lock (_gate) { return _cache ?? (_cache = new List<int>()); } }
            }
            """

    fires 3 "CR0164" source |> ignore
    let fixedSource = fixAll "CR0164" source

    Assert.Contains(
        "static List<int> Cache => LazyInitializer.EnsureInitialized(ref _cache, () => new List<int>());",
        fixedSource
    )

    Assert.Contains("return _text ?? Interlocked.CompareExchange(ref _text, Load(), null) ?? _text;", fixedSource)
    Assert.Contains("(LazyInitializer.EnsureInitialized(ref _cache, () => new List<int>())).Count;", fixedSource)
    Assert.Contains("List<int> Mine => _mine ?? (_mine = new List<int>());", fixedSource)
    Assert.Contains("lock (_gate) { return _cache ?? (_cache = new List<int>()); }", fixedSource)

// ---- CR0195 ----

[<Fact>]
let ``an assignment standing as a condition becomes the comparison; one that runs something is a note`` () =
    let source =
        csharp
            """
            using System;
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                bool done; bool ready;
                Dictionary<string, int> map = new();
                int A() { if (done = false) return 1; return 0; }
                int B(bool a, bool b) { if (a = b) return 1; return 0; }
                int D(string k) { bool found; int v; if (found = map.TryGetValue(k, out v)) return v; return 0; }
                void E() { while (done = true) { break; } }
                int F(bool a, bool b, bool c) => c && (a = b) ? 1 : 0;
                IEnumerable<int> G(List<int> xs, bool ex) => from x in xs where ex = true select x;
                IEnumerable<C> H(List<C> xs) => xs.Where(x => x.ready = true);
                int I(bool a, bool b) => (a = b) ? 1 : 0;
                void J(bool a, bool b) { do { } while (!(a = b)); }
            }
            """

    let fired = fires 9 "CR0195" source
    // A, E, G, H: the literal shape, `==` for the sweep and the bare test beside it
    Assert.Equal<int list>([ 2; 1; 1; 2; 1; 2; 2; 1; 1 ], fired |> List.map (fun s -> s.Fixes.Length))
    Assert.True(fired.[2].Fixes.Head.EditorOnly)
    Assert.Contains("CS0665", fired.Head.Message)
    let fixedSource = fixAll "CR0195" source
    Assert.Contains("int A() { if (done == false) return 1; return 0; }", fixedSource)
    Assert.Contains("int B(bool a, bool b) { if (a == b) return 1; return 0; }", fixedSource)
    Assert.Contains("if (found = map.TryGetValue(k, out v)) return v;", fixedSource)
    Assert.Contains("void E() { while (done == true) { break; } }", fixedSource)
    Assert.Contains("=> c && (a == b) ? 1 : 0;", fixedSource)
    // per element, and a variable read anew by a loop: the editor's
    Assert.Contains("from x in xs where ex = true select x;", fixedSource)
    Assert.Contains("xs.Where(x => x.ready = true);", fixedSource)
    Assert.Contains("while (!(a = b));", fixedSource)
    Assert.Contains("from x in xs where ex == true select x;", applyFix source fired.[5].Fixes.Head)
    Assert.Contains("xs.Where(x => x.ready == true);", applyFix source fired.[6].Fixes.Head)
    Assert.Contains("xs.Where(x => x.ready);", applyFix source fired.[6].Fixes.[1])
    Assert.Contains("while (!(a == b));", applyFix source fired.[8].Fixes.Head)

    Assert.Equal<bool list>(
        [ false; false; true; false; false; true; true; false; true ],
        fired |> List.map (fun s -> s.Fixes.Head.EditorOnly)
    )

    Assert.Contains("int I(bool a, bool b) => (a == b) ? 1 : 0;", fixedSource)
    // the bare test is the editor's
    Assert.True(fired.Head.Fixes.[1].EditorOnly)
    Assert.Contains("int A() { if (!done) return 1; return 0; }", applyFix source fired.Head.Fixes.[1])
    Assert.Contains("void E() { while (done) { break; } }", applyFix source fired.[3].Fixes.[1])
    Assert.Contains("if (found == map.TryGetValue(k, out v)) return v;", applyFix source fired.[2].Fixes.Head)
    // the note and the three left to the editor
    fires 4 "CR0195" fixedSource |> ignore

[<Fact>]
let ``CR0195 is quiet for an assignment under a comparison, doubled parentheses, statements and lambdas that take nothing``
    ()
    =
    let source =
        csharp
            """
            using System;
            class C
            {
                bool ready;
                int A(System.IO.TextReader r) { int n = 0; string? line; while ((line = r.ReadLine()) != null) n++; return n; }
                int B(bool a, bool b) { if ((a = b)) return 1; return 0; }
                int D(int a, int b) { if ((a = b) > 0) return 1; return 0; }
                void E(bool a, bool b) { a = b; var f = a = b; bool g; g = a = true; Console.WriteLine(f && g); }
                Func<bool> F(bool a) => () => a = true;
                Action<C> G() => x => x.ready = true;
                bool H(bool a, bool b, bool c) => c && ((a = b));
                void I(bool a) { for (a = true; a; a = false) { } }
                bool J(bool a, bool b) => a == b;
                int K(bool a, bool b) => ((a = b)) ? 1 : 0;
            }
            """

    Assert.Empty(suggestCode "CR0195" source)

[<Fact>]
let ``CR0195 leaves to the editor a first write, a lambda that is no predicate and a right side that runs`` () =
    let source =
        csharp
            """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                bool ready;
                int A(bool b) { bool a; if (a = b) return 1; return a ? 2 : 3; }
                IEnumerable<bool> B(List<C> xs) => xs.Select(x => x.ready = true);
                int D(bool a, bool b, bool c) { if (a = b && c) return 1; return 0; }
            }
            """

    let fired = fires 3 "CR0195" source
    Assert.True(fired |> List.forall (fun s -> s.Fixes |> List.forall (fun f -> f.EditorOnly)))
    Assert.Equal(normalize source, fixAll "CR0195" source)
    // `==` binds tighter than the assignment did
    Assert.Contains("if (a == (b && c)) return 1;", applyFix source fired.[2].Fixes.Head)

// ---- CR0196 ----

[<Fact>]
let ``a value that is never null compared with null loses the dead test and what only it guards`` () =
    let source =
        csharp
            """
            using System;
            enum Kind { A, B }
            class C
            {
                DateTime _when; decimal Amount { get; set; }
                int A(DateTime d)
                {
                    if (d == null)
                    {
                        return 0;
                    }
                    return 1;
                }
                int B(Guid g)
                {
                    if (g != null)
                    {
                        Console.WriteLine(g);
                        Console.WriteLine(1);
                    }
                    return 1;
                }
                int D(int n, bool p) => n == null || p ? 1 : 2;
                int E(Kind k) => k != null ? 1 : 2;
                string F() => _when == null ? "none" : "some";
                bool G(bool p) => Amount != null && p;
                void H(decimal m)
                {
                    if (m == null)
                        Console.WriteLine("a");
                    else
                        Console.WriteLine("b");
                }
                void I(int n)
                {
                    if (null != n)
                    {
                        var s = n.ToString();
                        Console.WriteLine(s);
                    }
                }
                void J(Guid id, int[] xs)
                {
                    if (id == null) throw new ArgumentNullException(nameof(id));
                    if (id != null)
                        foreach (var x in xs)
                        {
                            Console.WriteLine(x);
                        }
                }
                bool K(bool a, DateTime d, bool p) => a || d == null || p;
            }
            """

    let fired = fires 11 "CR0196" source

    Assert.True(
        fired
        |> List.forall (fun s -> s.Fixes.Length = 1 && not s.Fixes.Head.EditorOnly)
    )

    Assert.Contains("DateTime?", fired.Head.Message)
    Assert.Contains("always false", fired.Head.Message)
    let fixedSource = fixAll "CR0196" source
    Assert.Contains("int A(DateTime d)\n    {\n        return 1;\n    }", fixedSource)

    Assert.Contains(
        "    {\n        Console.WriteLine(g);\n        Console.WriteLine(1);\n        return 1;\n    }",
        fixedSource
    )

    Assert.Contains("int D(int n, bool p) => p ? 1 : 2;", fixedSource)
    Assert.Contains("int E(Kind k) => 1;", fixedSource)
    Assert.Contains("string F() => \"some\";", fixedSource)
    Assert.Contains("bool G(bool p) => p;", fixedSource)
    Assert.Contains("void H(decimal m)\n    {\n        Console.WriteLine(\"b\");\n    }", fixedSource)
    // a block that declares a name keeps its braces
    Assert.Contains(
        "    {\n        {\n            var s = n.ToString();\n            Console.WriteLine(s);\n        }\n    }",
        fixedSource
    )

    // a guard clause that can never throw goes; a statement kept without braces moves one level out
    Assert.Contains(
        "void J(Guid id, int[] xs)\n    {\n        foreach (var x in xs)\n        {\n            Console.WriteLine(x);\n        }\n    }",
        fixedSource
    )

    Assert.Contains("bool K(bool a, DateTime d, bool p) => a || p;", fixedSource)
    Assert.Empty(suggestCode "CR0196" fixedSource)

[<Fact>]
let ``CR0196 is a note where the test is no dead guard, the editor's where a comment would go, and quiet for what can be null``
    ()
    =
    let source =
        csharp
            """
            using System;
            struct Odd
            {
                public static bool operator ==(Odd a, object? b) => true;
                public static bool operator !=(Odd a, object? b) => false;
                public override bool Equals(object? o) => true;
                public override int GetHashCode() => 0;
            }
            class C
            {
                DateTime Next() => DateTime.Now;
                bool A(DateTime d, bool p) => d == null && p;
                bool B(DateTime d) { return d == null; }
                int D() { if (Next() == null) return 1; return 2; }
                void E(DateTime d) { while (d != null) { break; } }
                int F(DateTime d) { if (d != null) return 1; return 2; }
                double G(int n) { var v = n != null ? 1 : 2.0; return v; }
                int H(DateTime d)
                {
                    // the comment that would go
                    if (d == null) { return 0; }
                    return 1;
                }
                bool I(DateTime? d) => d == null;
                bool J<T>(T t) => t == null;
                bool K(Odd o) => o == null;
                bool L(string? s) => s == null;
                int M(DateTime d, int x) { if (x > 0) return 1; else if (d == null) return 2; return 3; }
                int N(int n) { var v = n != null ? default : 5; return v; }
                int O(DateTime d, bool c) { if (c) if (d != null) return 1; else return 2; return 3; }
                int P(DateTime d, int k)
                {
                    switch (k)
                    {
                        case 1:
                            if (d == null) return 0;
                            break;
                    }
                    return 1;
                }
                int Q(DateTime d, int n)
                {
                    if (d != null)
                    {
                        #if DEBUG
                        n++;
                        #endif
                    }
                    return n;
                }
            }
            """

    let fired = fires 12 "CR0196" source
    // A, B, D, E, F, G: notes; H: the editor's; M, N, O, P: notes; Q: no rewrite across a directive
    Assert.Equal<int list>([ 0; 0; 0; 0; 0; 0; 1; 0; 0; 0; 0; 0 ], fired |> List.map (fun s -> s.Fixes.Length))
    Assert.True(fired.[6].Fixes.Head.EditorOnly)
    Assert.Equal(normalize source, fixAll "CR0196" source)

// ---- CR0197 ----

/// Every offer of a suggestion applied alone compiles.
let private offersCompile (source: string) (s: CSharp.Refactor.Suggestion) =
    for f in s.Fixes do
        let compilation, _ = compile (applyFix source f)
        Assert.True((errorsOf compilation).IsEmpty, sprintf "%s: %A" f.Title (errorsOf compilation))

[<Fact>]
let ``a specific catch around a blocking wait is noted, the wrapper's catch and the removal the editor's`` () =
    let source =
        csharp
            """
            using System;
            using System.IO;
            using System.Threading.Tasks;
            class C
            {
                void A(Task t)
                {
                    try
                    {
                        t.Wait();
                    }
                    catch (IOException e)
                    {
                        Console.WriteLine(e.Message);
                    }
                }
                int B(Task<int> t)
                {
                    try { Console.WriteLine("x"); return t.Result; }
                    catch (InvalidOperationException) { return -1; }
                    catch (Exception) { return -2; }
                }
                void D(Task a, Task b)
                {
                    try { Task.WaitAll(a, b); }
                    catch (IOException) { }
                    finally { Console.WriteLine("done"); }
                }
                void E(Task t, Exception ae)
                {
                    try { t.Wait(); }
                    catch (OperationCanceledException) { Console.WriteLine(ae); }
                    catch (IOException x) when (x.Message != "") { }
                }
            }
            """

    let fired = fires 5 "CR0197" source

    Assert.Equal<string list>(
        [
            "catch (IOException e)"
            "catch (InvalidOperationException)"
            "catch (IOException)"
            "catch (OperationCanceledException)"
            "catch (IOException x)"
        ],
        firedText source fired
    )

    // one level, deepest, removal; no removal beside other statements; none beside a filter
    Assert.Equal<int list>([ 3; 2; 3; 3; 0 ], fired |> List.map (fun s -> s.Fixes.Length))
    Assert.True(fired |> List.forall (fun s -> s.Fixes |> List.forall (fun f -> f.EditorOnly)))
    Assert.Equal(normalize source, fixAll "CR0197" source)
    Assert.Contains("the general catch below", fired.[1].Message)
    Assert.DoesNotContain("the general catch below", fired.Head.Message)
    fired |> List.iter (offersCompile source)

    Assert.Contains(
        "        catch (IOException e)\n        {\n            Console.WriteLine(e.Message);\n        }\n        catch (AggregateException ae) when (ae.InnerException is IOException e)\n        {\n            Console.WriteLine(e.Message);\n        }\n",
        applyFix source fired.Head.Fixes.Head
    )

    Assert.Contains("when (ae.GetBaseException() is IOException e)", applyFix source fired.Head.Fixes.[1])
    // the only clause of a try that holds nothing but the wait: the try goes
    Assert.Contains("void A(Task t)\n    {\n        t.Wait();\n    }", applyFix source fired.Head.Fixes.[2])

    Assert.Contains(
        "catch (InvalidOperationException) { return -1; }\n        catch (AggregateException ae) when (ae.InnerException is InvalidOperationException) { return -1; }\n        catch (Exception) { return -2; }",
        applyFix source fired.[1].Fixes.Head
    )

    Assert.Contains(
        "try { Task.WaitAll(a, b); }\n        finally { Console.WriteLine(\"done\"); }",
        applyFix source fired.[2].Fixes.[2]
    )

    // a name in scope is not taken again
    Assert.Contains(
        "catch (AggregateException ae2) when (ae2.InnerException is OperationCanceledException)",
        applyFix source fired.[3].Fixes.Head
    )

[<Fact>]
let ``CR0197 is quiet where the wrapper is caught, the wait unwraps or throws the type itself, or another handler answers``
    ()
    =
    let source =
        csharp
            """
            using System;
            using System.IO;
            using System.Threading;
            using System.Threading.Tasks;
            class C
            {
                void A(Task t) { try { t.Wait(); } catch (AggregateException) { } }
                void B(Task t) { try { t.Wait(); } catch (Exception) { } }
                void D(Task t) { try { t.GetAwaiter().GetResult(); } catch (IOException) { } }
                async Task E(Task t) { try { t.Wait(); } catch (IOException) { } await t; }
                void F(Task t, CancellationToken ct) { try { t.Wait(ct); } catch (OperationCanceledException) { } }
                void G(Task a, Task b) { try { Task.WaitAny(a, b); } catch (IOException) { } }
                void H(Task t) { try { t.Wait(); } catch (IOException) { } catch (AggregateException) { } }
                void I(Task t) { try { Action act = () => t.Wait(); act(); } catch (IOException) { } }
                void J() { try { File.ReadAllText("x"); } catch (IOException) { } }
                void K(Task t) { try { try { t.Wait(); } catch (Exception) { } } catch (IOException) { } }
                void L(Task t) { try { t.Wait(); } catch (ObjectDisposedException) { } }
                void M(Task t) { try { t.Wait(); } catch { } }
            }
            """

    Assert.Empty(suggestCode "CR0197" source)

[<Fact>]
let ``CR0197 offers no filter where the language has none`` () =
    let source =
        normalize (
            csharp
                """
                using System.IO;
                using System.Threading.Tasks;
                class C
                {
                    void A(Task t, int n)
                    {
                        try { n++; t.Wait(); }
                        catch (IOException) { }
                    }
                }
                """
        )

    let compilation, tree =
        compileRaw Microsoft.CodeAnalysis.CSharp.LanguageVersion.CSharp5 source

    let fired = suggestRaw compilation tree |> List.filter (fun s -> s.Code = "CR0197")

    assertFired 1 source fired
    Assert.Empty fired.Head.Fixes

// ---- review 2026-10-03b: CR0195 – CR0197, CR0031, CR0124, CR0164 ----

/// A sweep in the small: the first primary fix of the rule applied, the
/// result compiled, again until none is left. The source it converges on.
let private sweepUntilQuiet (code: string) (source: string) : string =
    let rec pass (current: string) (budget: int) =
        let next =
            suggestCode code current
            |> List.tryPick (fun s -> s.Fixes |> List.tryFind (fun f -> not f.EditorOnly))

        match next with
        | Some fix when budget > 0 ->
            let applied = applyFix current fix
            let compilation, _ = compile applied
            Assert.True((errorsOf compilation).IsEmpty, sprintf "%s: %A\n%s" fix.Title (errorsOf compilation) applied)
            pass applied (budget - 1)
        | Some fix -> failwithf "no fixed point: %s still applies\n%s" fix.Title current
        | None -> current

    pass (normalize source) 20

[<Fact>]
let ``review 2026-10-03b CR0195 reads every place a condition stands, and nothing that is a value`` () =
    let source =
        csharp
            """
            class P { public bool Flag; }
            class C
            {
                int A(bool a, bool b) { if (!(a = b)) return 1; return 0; }
                int B(P x, P y) { if (x.Flag = y.Flag) return 1; return 0; }
                bool D(bool a, bool b) { return a = b; }
                bool E(bool cond, bool a) { bool r = cond ? (a = true) : false; return r; }
                int F(object o, bool a, bool b) { switch (o) { case int n when a = b: return n; default: return 0; } }
                int G(bool a, bool b, bool c, bool d) { if (c && d && (a = b)) return 1; return 0; }
                void H(bool a, bool b) { a &= b; a |= b; }
                int I(bool? a, bool b) { if ((a = b) == true) return 1; return 0; }
                int J(object o, bool a, bool b) => o switch { int n when (a = b) => n, _ => 0 };
            }
            """

    // a switch expression's guard cannot hold a bare assignment: its parentheses are the
    // one pair more that marks it as meant
    fires 4 "CR0195" source |> ignore
    let swept = sweepUntilQuiet "CR0195" source
    Assert.Contains("if (!(a == b)) return 1;", swept)
    Assert.Contains("if (x.Flag == y.Flag) return 1;", swept)
    Assert.Contains("case int n when a == b: return n;", swept)
    Assert.Contains("if (c && d && (a == b)) return 1;", swept)
    Assert.Contains("int n when (a = b) => n", swept)
    Assert.Contains("return a = b;", swept)
    Assert.Contains("cond ? (a = true) : false", swept)

[<Fact>]
let ``review 2026-10-03b CR0195 and CR0196 are quiet where the compiler's own warning is switched off`` () =
    let source =
        csharp
            """
            using System;
            class C
            {
            #pragma warning disable CS0665, CS0472
            #pragma warning disable 8073
                int A(bool done) { if (done = true) return 1; return 0; }
                bool B(int n, DateTime d) => n == null || d == null;
            #pragma warning restore CS0665, CS0472
            #pragma warning restore 8073
                int D(bool done) { if (done = true) return 1; return 0; }
                bool E(int n, DateTime d) => n == null && d == null;
            }
            """

    Assert.Equal<string list>([ "done = true" ], firedText source (suggestCode "CR0195" source))
    Assert.Equal<string list>([ "n == null"; "d == null" ], firedText source (suggestCode "CR0196" source))

[<Fact>]
let ``review 2026-10-03b CR0196 reads the null comparison only; other spellings, a lifted member and a boxed value are quiet``
    ()
    =
    let source =
        csharp
            """
            using System;
            using System.Diagnostics;
            using System.Linq.Expressions;
            struct Eq
            {
                public static bool operator ==(Eq a, Eq b) => true;
                public static bool operator !=(Eq a, Eq b) => false;
                public override bool Equals(object? o) => true;
                public override int GetHashCode() => 0;
            }
            class Row { public DateTime When; }
            class C
            {
                bool A(DateTime d) => d is { };
                bool B(DateTime d) => d.Equals(null) || ReferenceEquals(d, null) || Equals(d, null);
                bool D() => default(DateTime) == null;
                bool E(Eq e) => e == null;
                bool F(Row? r) => r?.When == null;
                bool G(DateTime d) => (object)d == null;
                Expression<Func<Row, bool>> H() => r => r.When == null || r.When > DateTime.MinValue;
                void I(DateTime d) { Debug.Assert(d != null); }
                bool K((int, int) t) => t.Item1 == null;
                bool M<T>(T? t) => t == null;
                bool N<T>(T? t) where T : struct => t == null;
            }
            """

    let fired = suggestCode "CR0196" source

    Assert.Equal<string list>(
        [
            "default(DateTime) == null"
            "e == null"
            "r.When == null"
            "d != null"
            "t.Item1 == null"
        ],
        firedText source fired
    )

    // none of these is a dead guard a sweep may take out
    Assert.True(fired |> List.forall (fun s -> s.Fixes.IsEmpty))

    // a pointer is no struct
    let compilation, tree = compile "unsafe class U { bool J(int* p) => p == null; }"

    Assert.DoesNotContain(
        CSharp.Refactor.Roslyn.Rules.all
            tree
            (compilation.GetSemanticModel(tree, false))
            (CSharp.Refactor.Roslyn.Context.forTree None compilation tree false),
        (fun s -> s.Code = "CR0196")
    )

[<Fact>]
let ``review 2026-10-03b CR0196 converges, compiling at every step, and keeps what the dead test stood beside`` () =
    let source =
        csharp
            """
            using System;
            class C
            {
                int A(DateTime a, DateTime b) { if (a == null || b == null) return 1; return 2; }
                void B(Guid id) { if (id == null) throw new ArgumentNullException(nameof(id)); }
                int D(DateTime d, string s)
                {
                    if (d == null || !int.TryParse(s, out var n)) return -1;
                    return n;
                }
                int E(DateTime d)
                {
                    int x = 0;
                    if (d != null)
                    {
                        int y = 1;
                        x += y;
                    }
                    int y2 = 2;
                    return x + y2;
                }
                int F(DateTime d)
                {
                    int x;
                    if (d == null) { x = 1; } else { x = 2; }
                    return x;
                }
                int G(DateTime d)
                {
            #if !NOT_DEFINED
                    if (d == null) return 0;
            #endif
                    return 1;
                }
            }
            """

    let swept = sweepUntilQuiet "CR0196" source
    Assert.Contains("int A(DateTime a, DateTime b) { return 2; }", swept)
    Assert.Contains("void B(Guid id) { }", swept)
    Assert.Contains("if (!int.TryParse(s, out var n)) return -1;\n        return n;", swept)

    Assert.Contains(
        "int x = 0;\n        {\n            int y = 1;\n            x += y;\n        }\n        int y2 = 2;",
        swept
    )

    Assert.Contains("int x;\n        x = 2;\n        return x;", swept)
    // a statement inside one branch of a directive goes; the directive stays
    Assert.Contains("#if !NOT_DEFINED\n#endif\n        return 1;", swept)
    Assert.Empty(suggestCode "CR0196" swept)

[<Fact>]
let ``review 2026-10-03b CR0197 reads waits under nested blocks and offers no copied clause whose body rethrows`` () =
    let source =
        csharp
            """
            using System;
            using System.IO;
            using System.Threading.Tasks;
            class C
            {
                void A(Task t) { try { t.Wait(); } catch (IOException) { throw; } }
                void B(Task t)
                {
                    try { lock (this) { using (var s = new MemoryStream()) { t.Wait(TimeSpan.FromSeconds(1)); } } }
                    catch (IOException) { }
                }
                void D(ValueTask<int> v) { try { var r = v.Result; Console.WriteLine(r); } catch (IOException) { } }
                void E(Task t) { try { t.Wait(); } finally { } }
                void F(Task t) { try { t.Wait(); } catch (IOException) { } catch (FormatException) { } }
                void G(Func<Task> f) { Action a = () => f().Wait(); a(); }
            }
            """

    let fired = fires 4 "CR0197" source
    // a copied `throw;` would rethrow the wrapper, not the exception the clause names
    Assert.DoesNotContain(fired.Head.Fixes, (fun f -> f.Title.StartsWith "Also catch"))
    Assert.Equal(2, fired.[1].Fixes.Length)
    fired |> List.iter (offersCompile source)

[<Fact>]
let ``review 2026-10-03b CR0164 takes the coalescing form on fields only, and its rewrite binds for an interface-typed one``
    ()
    =
    let source =
        csharp
            """
            using System;
            namespace N;
            interface IRequest { }
            class Foo : IRequest { }
            class C
            {
                static IRequest? _req;
                static IRequest? Prop { get; set; }
                [ThreadStatic] static Foo? _perThread;
                static volatile Foo? _vol;
                static Lazy<Foo>? _lazy;
                static Foo? Make() => new Foo();
                static IRequest A() => _req ?? (_req = new Foo());
                static IRequest? B() { return _req ?? (_req = Make()); }
                static IRequest D() => Prop ?? (Prop = new Foo());
                static Foo E() => _perThread ?? (_perThread = new Foo());
                static Foo F() => _vol ?? (_vol = new Foo());
                static Lazy<Foo> H() => _lazy ?? (_lazy = new Lazy<Foo>(() => new Foo()));
            }
            """

    fires 3 "CR0164" source |> ignore
    let fixedSource = fixAll "CR0164" source
    Assert.Contains("static IRequest A() => LazyInitializer.EnsureInitialized(ref _req, () => new Foo());", fixedSource)
    Assert.Contains("return _req ?? Interlocked.CompareExchange(ref _req, Make(), null) ?? _req;", fixedSource)
    Assert.Contains("using System;\nusing System.Threading;\nnamespace N;", fixedSource)
    Assert.Contains("Prop ?? (Prop = new Foo());", fixedSource)
    Assert.Contains("_perThread ?? (_perThread = new Foo());", fixedSource)
    Assert.Contains("_vol ?? (_vol = new Foo());", fixedSource)

[<Fact>]
let ``CR0164 a check-then-assign statement on its own line becomes a braced if`` () =
    let source =
        csharp
            """
            using System.Threading;
            class Store { public static Store? Load() => null; }
            static class Cache
            {
                static Store? _store;
                public static void Warm()
                {
                    _store ??= Store.Load();
                }
            }
            """

    // a factory that may answer null takes the exchange, as a braced `if`
    Assert.Contains(
        csharp
            """
            public static void Warm()
                {
                    if (_store is null)
                    {
                        Interlocked.CompareExchange(ref _store, Store.Load(), null);
                    }
                }
            """,
        normalize (fixAll "CR0164" source)
    )
