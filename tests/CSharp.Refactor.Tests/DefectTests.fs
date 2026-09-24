module CSharp.Refactor.Tests.DefectTests

open Xunit
open CSharp.Refactor.Tests.Harness

// ---- CR0160 ----

[<Fact>]
let ``a for variable captured by an escaping closure gets a per-iteration copy`` () =
    let source =
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

    let fired = suggestCode "CR0160" source
    Assert.Equal(2, fired.Length)
    Assert.True(fired |> List.forall (fun s -> not s.Fixes.IsEmpty))
    let fixedSource = fixAll "CR0160" source
    Assert.Contains("var i1 = i;\n            actions.Add(() => Console.WriteLine(i1));", fixedSource)
    Assert.Contains("var i2 = i;\n            tasks.Add(Task.Run(() => Console.WriteLine(i2 * 2)));", fixedSource)

[<Fact>]
let ``a while-condition binder captured by a queued task is copied; consumed and foreach closures are quiet`` () =
    let source =
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
    Assert.Contains("var line1 = line;\n            tasks.Add(Task.Run(() => Console.WriteLine(line1)));", fixedSource)

[<Fact>]
let ``a closure handed to an unknown callee, a closure writing the variable, and a lazy chain stored are handled`` () =
    let source =
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
    Assert.Equal(2, fired.Length)
    let notes = fired |> List.filter (fun s -> s.Fixes.IsEmpty)
    Assert.Equal<string list>([ "() => Console.WriteLine(i)" ], firedText source notes)
    let fixedSource = fixAll "CR0160" source
    Assert.Contains("var i1 = i;\n            queries.Add(xs.Where(x => x > i1));", fixedSource)

[<Fact>]
let ``a straight-line local written after an escaped closure is a note`` () =
    let source =
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

    let fired = suggestCode "CR0160" source
    Assert.Equal(1, fired.Length)
    Assert.True(fired.Head.Fixes.IsEmpty)

// ---- CR0161 ----

[<Fact>]
let ``a mutating call on a readonly struct field, a property and a list element is noted; a local and a readonly struct are quiet``
    ()
    =
    let source =
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

    let fired = suggestCode "CR0162" source
    Assert.Equal(2, fired.Length)
    Assert.True(fired |> List.forall (fun s -> s.Fixes.IsEmpty))

// ---- CR0163 ----

[<Fact>]
let ``an unguarded semaphore region is wrapped in try-finally`` () =
    let source =
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

    let fired = suggestCode "CR0163" source
    Assert.Equal(1, fired.Length)
    let fixedSource = fixAll "CR0163" source

    let expected =
        "        await _gate.WaitAsync();\n        try\n        {\n            if (x > 0)\n            {\n                return;\n            }\n            Work(x);\n        }\n        finally\n        {\n            _gate.Release();\n        }\n    }"

    Assert.Contains(expected, fixedSource)

[<Fact>]
let ``a guarded region, a timeout wait and a between-declared local read after the release are left`` () =
    let source =
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

    let fired = suggestCode "CR0163" source
    Assert.Equal(1, fired.Length)
    Assert.True(fired.Head.Fixes.IsEmpty)

// ---- CR0164 ----

[<Fact>]
let ``a check-then-assign static cache becomes LazyInitializer; an instance field and a locked one are quiet`` () =
    let source =
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

    let fired = suggestCode "CR0164" source
    Assert.Equal(2, fired.Length)
    let fixedSource = fixAll "CR0164" source
    Assert.Contains("return LazyInitializer.EnsureInitialized(ref _cache, () => new List<int>());", fixedSource)
    Assert.Contains("static string Name => LazyInitializer.EnsureInitialized(ref _name, () => \"x\");", fixedSource)
    Assert.Contains("using System.Threading;", fixedSource)

// ---- CR0165 ----

[<Fact>]
let ``a wrapping throw gains the caught exception as inner, naming an unnamed catch`` () =
    let source =
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

    let fired = suggestCode "CR0165" source
    Assert.Equal(3, fired.Length)
    let fixedSource = fixAll "CR0165" source
    Assert.Contains("catch (Exception ex) { throw new SyncException($\"sync {id} failed\", ex); }", fixedSource)
    Assert.Contains("catch (InvalidOperationException ex) { throw new SyncException(\"failed\", ex); }", fixedSource)
    Assert.Contains("throw new InvalidOperationException(\"no inner overload here?\", ex);", fixedSource)
    Assert.Contains("throw new SyncException(\"failed: \" + ex.Message); }", fixedSource)

[<Fact>]
let ``CR0165 leaves a domain exception without an inner-exception constructor alone`` () =
    let source =
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
    Assert.Equal(7, fired.Length)
    Assert.Equal(4, fired |> List.filter (fun s -> not s.Fixes.IsEmpty) |> List.length)
    let fixedSource = fixAll "CR0166" source

    Assert.Contains(
        "        if (!int.TryParse(s, out v))\n        {\n            v = -1;\n        }\n        return v;",
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

// ---- CR0167 / CR0168 ----

[<Fact>]
let ``floating equality and integer division into a floating target are noted`` () =
    let source =
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

// ---- CR0170 ----

[<Fact>]
let ``a working loop under an unobserved token gets the check; an observed loop and a pure loop are quiet`` () =
    let source =
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

    let fired = suggestCode "CR0170" source
    Assert.Equal(1, fired.Length)
    let fixedSource = fixAll "CR0170" source

    Assert.Contains(
        "        foreach (var item in items)\n        {\n            ct.ThrowIfCancellationRequested();\n            await Process(item);\n        }",
        fixedSource
    )

// ---- CR0171 ----

[<Fact>]
let ``a filter loop becomes RemoveAll, another mutation enumerates a snapshot, mutate-and-break is quiet`` () =
    let source =
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

    let fired = suggestCode "CR0171" source
    Assert.Equal(2, fired.Length)
    let fixedSource = fixAll "CR0171" source
    Assert.Contains("xs.RemoveAll(x => x < 0);", fixedSource)
    Assert.Contains("foreach (var name in names.ToList())", fixedSource)
    Assert.Contains("using System.Linq;", fixedSource)
