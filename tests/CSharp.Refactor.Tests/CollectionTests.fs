module CSharp.Refactor.Tests.CollectionTests

open Xunit
open CSharp.Refactor.Tests.Harness

// ---- CR0024 ----

[<Fact>]
let ``a foreach that only adds becomes AddRange, projections and per-element receivers stay`` () =
    let source =
        """
using System.Collections.Generic;
using System.Linq;
class C
{
    readonly List<int> acc = new List<int>();
    void A(int[] xs) { foreach (var x in xs) acc.Add(x); }
    void B(int[] xs)
    {
        foreach (var x in xs)
        {
            acc.Add(x);
        }
    }
    void D(int[] xs) { foreach (var x in xs) acc.Add(x * 2); }
    void E(List<List<int>> columns, int[] xs) { foreach (var x in xs) columns[x].Add(x); }
    void F() { foreach (var x in acc) acc.Add(x); }
    void G(int[] xs, HashSet<int> set) { foreach (var x in xs) set.Add(x); }
    // .NET 10's `AddRange(params ReadOnlySpan<T>)` would take the Array as ONE element
    void H(System.Array values, List<object> objects) { foreach (var v in values) objects.Add(v); }
    // AddRange over a lazy sequence measured 2.9× slower than the loop
    void I(IEnumerable<int> xs) { foreach (var x in xs) acc.Add(x); }
}
"""

    let fired = suggestCode "CR0024" source
    Assert.Equal(2, fired.Length)
    let fixedSource = fixAll "CR0024" source
    Assert.Contains("void A(int[] xs) { acc.AddRange(xs); }", fixedSource)
    Assert.Contains("    void B(int[] xs)\n    {\n        acc.AddRange(xs);\n    }", fixedSource)

// ---- CR0031 ----

[<Fact>]
let ``a parameterless Random used for calls becomes Random.Shared, a seeded or stored one stays`` () =
    let source =
        """
using System;
class C
{
    Random kept;
    int A() => new Random().Next(10);
    int B() { var r = new Random(); return r.Next(1, 6) + r.Next(); }
    int D() => new Random(42).Next(10);
    void E() { kept = new Random(); }
    Random F() { var r = new Random(); return r; }
    void G(Action<Random> use) { var r = new Random(); use(r); }
}
"""

    let fired = suggestCode "CR0031" source
    Assert.Equal(2, fired.Length)
    let fixedSource = fixAll "CR0031" source
    Assert.Contains("int A() => Random.Shared.Next(10);", fixedSource)
    Assert.Contains("int B() { var r = Random.Shared; return r.Next(1, 6) + r.Next(); }", fixedSource)

// ---- CR0032 ----

[<Fact>]
let ``a Keys loop with a lookup per key enumerates the pairs, a writing loop stays`` () =
    let source =
        """
using System;
using System.Collections.Generic;
class C
{
    void A(Dictionary<string, int> d)
    {
        foreach (var k in d.Keys)
        {
            Console.WriteLine(k + d[k]);
            Console.WriteLine(d[k] * 2);
        }
    }
    void B(Dictionary<string, int> d) { foreach (var k in d.Keys) d[k] = 0; }
    void D(Dictionary<string, int> d) { foreach (var k in d.Keys) Console.WriteLine(k); }
    void E(IReadOnlyDictionary<int, string> d) { int value = 1; foreach (var key in d.Keys) Console.WriteLine(d[key] + value); }
}
"""

    let fired = suggestCode "CR0032" source
    Assert.Equal(2, fired.Length)
    let fixedSource = fixAll "CR0032" source

    Assert.Contains(
        normalize
            """        foreach (var (k, value) in d)
        {
            Console.WriteLine(k + value);
            Console.WriteLine(value * 2);
        }""",
        fixedSource
    )

    Assert.Contains("foreach (var (key, v) in d) Console.WriteLine(v + value);", fixedSource)

[<Fact>]
let ``CR0032 still enumerates the pairs when nothing before a lookup can write the dictionary`` () =
    let source =
        """
using System;
using System.Collections.Generic;
using System.Text;
class C
{
    readonly Dictionary<string, int> map = new Dictionary<string, int>();
    Dictionary<string, int> Table { get; } = new Dictionary<string, int>();
    static void Use(string key, int n) { }
    void A(Dictionary<string, int> d) { foreach (var k in d.Keys) Console.WriteLine($"{k}={d[k]}"); }
    void B(Dictionary<string, int> d, StringBuilder sb) { foreach (var k in d.Keys) sb.Append(k).Append(d[k]); }
    void D(Dictionary<string, int> d, Dictionary<string, int> result) { foreach (var k in d.Keys) result.Add(k, d[k]); }
    int E(Dictionary<string, int> d) { var total = 0; foreach (var k in d.Keys) { var x = k.Length; total += d[k]; } return total; }
    void F(Dictionary<string, int> d, List<string> list) { foreach (var k in d.Keys) { if (d[k] > 0) list.Add(k); } }
    void G(Dictionary<string, int> d) { foreach (var k in d.Keys) { Console.WriteLine(k); Console.WriteLine(d[k]); } }
    void H(Dictionary<string, int> d) { foreach (var k in d.Keys) { Console.WriteLine(d[k]); Use(k, 0); } }
    void I(Dictionary<string, int> d) { foreach (var k in d.Keys) Use(k, d[k]); }
    void J() { foreach (var k in this.map.Keys) Console.WriteLine(this.map[k]); }
    void K() { foreach (var k in Table.Keys) Console.WriteLine(Table[k]); }
    void L(Dictionary<string, int> d, Dictionary<string, int> acc) { foreach (var k in d.Keys) acc[k] = d[k]; }
}
"""

    let fired = suggestCode "CR0032" source
    Assert.Equal(11, fired.Length)
    Assert.All(fired, fun s -> Assert.NotEmpty s.Fixes)
    let fixedSource = fixAll "CR0032" source
    Assert.Contains("""foreach (var (k, value) in d) Console.WriteLine($"{k}={value}");""", fixedSource)
    Assert.Contains("foreach (var (k, value) in d) sb.Append(k).Append(value);", fixedSource)
    Assert.Contains("foreach (var (k, value) in d) result.Add(k, value);", fixedSource)
    Assert.Contains("foreach (var (k, value) in d) { var x = k.Length; total += value; }", fixedSource)
    Assert.Contains("foreach (var (k, value) in d) { if (value > 0) list.Add(k); }", fixedSource)
    Assert.Contains("foreach (var (k, value) in d) { Console.WriteLine(k); Console.WriteLine(value); }", fixedSource)
    Assert.Contains("foreach (var (k, value) in d) { Console.WriteLine(value); Use(k, 0); }", fixedSource)
    Assert.Contains("foreach (var (k, value) in d) Use(k, value);", fixedSource)
    Assert.Contains("foreach (var (k, value) in this.map) Console.WriteLine(value);", fixedSource)
    Assert.Contains("foreach (var (k, value) in Table) Console.WriteLine(value);", fixedSource)
    Assert.Contains("foreach (var (k, value) in d) acc[k] = value;", fixedSource)

[<Fact>]
let ``CR0032 is a note when something before the lookup may write the dictionary`` () =
    let source =
        """
using System;
using System.Collections.Generic;
class C
{
    Dictionary<string, int> d = new Dictionary<string, int>();
    int calls;
    Dictionary<string, int> Computed => new Dictionary<string, int> { ["a"] = ++calls };
    static void Bump(Dictionary<string, int> m, string key) { m[key] = 100; }
    void Touch(string key) { d[key] = 99; }
    string C0(Dictionary<string, int> d) { var r = ""; foreach (var k in d.Keys) { d["a"] = 100; r += d[k]; } return r; }
    string C1(Dictionary<string, int> d) { var r = ""; foreach (var k in d.Keys) { Bump(d, k); r += d[k]; } return r; }
    string C2(Dictionary<string, int> d) { var r = ""; foreach (var k in d.Keys) { d.Remove(k); r += d[k]; } return r; }
    string C3(Dictionary<string, int> d) { var r = ""; foreach (var k in d.Keys) { var m = d; m[k] = 50; r += d[k]; } return r; }
    string C4() { var r = ""; foreach (var k in this.Computed.Keys) { r += this.Computed[k]; } return r; }
    string C5(Dictionary<string, int> d) { Action<string> bump = key => d[key] = 77; var r = ""; foreach (var k in d.Keys) { bump(k); r += d[k]; } return r; }
    string C6() { var r = ""; foreach (var k in d.Keys) { this.d = new Dictionary<string, int>(); r += d[k]; } return r; }
    string C7() { var r = ""; foreach (var k in d.Keys) { Touch(k); r += d[k]; } return r; }
    string C8(Dictionary<string, int> d) { var r = ""; foreach (var k in d.Keys) { for (int i = 0; i < 2; i++) { r += d[k]; Touch(k); } } return r; }
    string C9(Dictionary<string, int> d, List<Func<int>> later) { foreach (var k in d.Keys) later.Add(() => d[k]); return ""; }
}
"""

    let fired = suggestCode "CR0032" source
    Assert.Equal(10, fired.Length)
    Assert.All(fired, fun s -> Assert.Empty s.Fixes)

[<Fact>]
let ``CR0032 lets any call run before the lookup of a dictionary no other code can reach`` () =
    let source =
        """
using System;
using System.Collections.Generic;
using System.Text;
public interface ILog { void Info(string m); }
class Report
{
    private readonly Dictionary<string, int> _counts = new() { ["a"] = 1 };
    static bool ShouldSkip(string k) => k == "skip";
    static string Norm(string k) => k.Trim();
    public string D18() { var sb = new StringBuilder(); foreach (var k in _counts.Keys) { if (ShouldSkip(k)) continue; sb.Append(k).Append(_counts[k]); } return sb.ToString(); }
    int D06(ILog logger) { var d = new Dictionary<string, int> { ["a"] = 1 }; int total = 0; foreach (var k in d.Keys) { logger.Info("key " + k); total += d[k]; } return total; }
    string D14() { var d = new Dictionary<string, int>(); var sb = new StringBuilder(); foreach (var k in d.Keys) { var n = Norm(k); sb.Append(n + d[k]); } return sb.ToString(); }
    int D19() { var d = new Dictionary<string, int>(); int total = 0; foreach (var k in d.Keys) { if (ShouldSkip(k)) continue; total += d[k]; } return total; }
}
"""

    let fired = suggestCode "CR0032" source
    Assert.Equal(4, fired.Length)
    Assert.All(fired, fun s -> Assert.NotEmpty s.Fixes)
    let fixedSource = fixAll "CR0032" source

    Assert.Contains(
        "foreach (var (k, value) in _counts) { if (ShouldSkip(k)) continue; sb.Append(k).Append(value); }",
        fixedSource
    )

    Assert.Contains("foreach (var (k, value) in d) { logger.Info(\"key \" + k); total += value; }", fixedSource)
    Assert.Contains("foreach (var (k, value) in d) { var n = Norm(k); sb.Append(n + value); }", fixedSource)
    Assert.Contains("foreach (var (k, value) in d) { if (ShouldSkip(k)) continue; total += value; }", fixedSource)

[<Fact>]
let ``CR0032 is a note for a visible iterator, an alias store or a closure write; an interface call or an unknown Dispose is fixed``
    ()
    =
    let source =
        """
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;
public sealed class Res : IDisposable { public static Dictionary<string, int> Map = new(); public void Dispose() { Map["a"] = 99; } }
class C
{
    static Dictionary<string, int> shared = new();
    static IEnumerable<int> Touch() { shared["a"] = 99; yield return 0; }
    int H01() { IEnumerable<int> xs = Touch(); int t = 0; foreach (var k in shared.Keys) { foreach (var x in xs) { } t += shared[k]; } return t; }
    int H03(ICollection<int> sink) { var d = Res.Map; int t = 0; foreach (var k in d.Keys) { sink.Add(1); t += d[k]; } return t; }
    int H05(IDisposable r) { var d = Res.Map; int t = 0; foreach (var k in d.Keys) { using (r) { } t += d[k]; } return t; }
    int H16() { var inner = new Dictionary<string, int>(); var d = new ReadOnlyDictionary<string, int>(inner); int t = 0; foreach (var k in d.Keys) { inner["a"] = 99; t += d[k]; } return t; }
    int B01() { var d = new Dictionary<string, int>(); var seen = new ObservableCollection<string>(); seen.CollectionChanged += (s, e) => d["a"] = 99; int t = 0; foreach (var k in d.Keys) { seen.Add(k); t += d[k]; } return t; }
}
"""

    let fired = suggestCode "CR0032" source
    Assert.Equal(5, fired.Length)
    // H03 and H05 call through interfaces whose implementations cannot be
    // seen: the accepted residual, fixed
    Assert.Equal(2, fired |> List.filter (fun s -> not s.Fixes.IsEmpty) |> List.length)
    let fixedSource = fixAll "CR0032" source
    Assert.Contains("foreach (var (k, value) in d) { sink.Add(1); t += value; }", fixedSource)
    Assert.Contains("foreach (var (k, value) in d) { using (r) { } t += value; }", fixedSource)
    Assert.Contains("foreach (var k in shared.Keys) { foreach (var x in xs) { } t += shared[k]; }", fixedSource)

[<Fact>]
let ``CR0032 sweeps past calls whose bodies cannot be seen or touch no dictionary`` () =
    let source =
        """
using System;
using System.Collections.Generic;
public interface ILogger { void Info(string m); void LogInformation(string m, params object[] args); }
public static class Helper { public static string Format(string k) => k.ToUpperInvariant(); }
public sealed class Item { public override string ToString() => "item"; }
class C
{
    readonly ILogger _log;
    public event EventHandler<string>? Changed;
    public C(ILogger log) { _log = log; }
    static void Note(string k) { }
    int A(Dictionary<string, int> d) { int total = 0; foreach (var k in d.Keys) { Note(k); total += d[k]; } return total; }
    int B(Dictionary<string, int> d) { int total = 0; foreach (var k in d.Keys) { _log.Info(k); total += d[k]; } return total; }
    int D(Dictionary<string, int> d) { int total = 0; foreach (var k in d.Keys) { Console.WriteLine(k); total += d[k]; } return total; }
    int E(Dictionary<string, int> d, ILogger logger) { int total = 0; foreach (var k in d.Keys) { logger.LogInformation("key {0}", k); total += d[k]; } return total; }
    int F(Dictionary<string, int> d) { int total = 0; foreach (var k in d.Keys) { var f = Helper.Format(k); total += d[k] + f.Length; } return total; }
    int G(Dictionary<string, int> d, Item item) { int total = 0; foreach (var k in d.Keys) { var s = item.ToString(); total += d[k] + s.Length; } return total; }
    int H(Dictionary<string, int> d) { int total = 0; foreach (var k in d.Keys) { Changed?.Invoke(this, k); total += d[k]; } return total; }
}
"""

    let fired = suggestCode "CR0032" source
    Assert.Equal(7, fired.Length)
    Assert.All(fired, fun s -> Assert.NotEmpty s.Fixes)
    let fixedSource = fixAll "CR0032" source
    Assert.Contains("foreach (var (k, value) in d) { Note(k); total += value; }", fixedSource)
    Assert.Contains("foreach (var (k, value) in d) { _log.Info(k); total += value; }", fixedSource)
    Assert.Contains("foreach (var (k, value) in d) { Changed?.Invoke(this, k); total += value; }", fixedSource)

[<Fact>]
let ``CR0032 looks three calls deep into visible bodies for a dictionary touched, and a handler an event visibly has``
    ()
    =
    let source =
        """
using System;
using System.Collections.Generic;
class C
{
    Dictionary<string, int> map = new();
    public event EventHandler<string>? Changed;
    void B3(string k) { map[k] = 0; }
    void B2(string k) => B3(k);
    void B1(string k) => B2(k);
    void A5(string k) { map[k] = 0; }
    void A4(string k) => A5(k);
    void A3(string k) => A4(k);
    void A2(string k) => A3(k);
    void A1(string k) => A2(k);
    void Wire() { Changed += (s, k) => map[k] = 0; }
    int Three(Dictionary<string, int> d) { int total = 0; foreach (var k in d.Keys) { B1(k); total += d[k]; } return total; }
    int Five(Dictionary<string, int> d) { int total = 0; foreach (var k in d.Keys) { A1(k); total += d[k]; } return total; }
    int Handler(Dictionary<string, int> d) { int total = 0; foreach (var k in d.Keys) { Changed?.Invoke(this, k); total += d[k]; } return total; }
}
"""

    let fired = suggestCode "CR0032" source
    Assert.Equal(3, fired.Length)
    // three deep is seen; five deep is beyond the look and the accepted residual
    Assert.Empty fired.[0].Fixes
    Assert.NotEmpty fired.[1].Fixes
    Assert.Empty fired.[2].Fixes

[<Fact>]
let ``CR0032 counts a dictionary as confined only through its own members, BCL keys and a BCL comparer`` () =
    let source =
        """
using System;
using System.Collections.Generic;
using System.Linq;
public static class Sink
{
    public static IEnumerable<KeyValuePair<string, int>> S = new List<KeyValuePair<string, int>>();
    public static Dictionary<string, int> D = new();
    public static void Poke(string k) { ((Dictionary<string, int>)S)[k] = 99; D[k] = 99; }
}
public sealed class Key { public int Id; }
public sealed class Cmp : IEqualityComparer<string>
{
    public bool Upper;
    public bool Equals(string? a, string? b) => a == b;
    public int GetHashCode(string s) => s.GetHashCode();
}
class Report
{
    private readonly Dictionary<string, int> _d = new();
    static void Bump(Key k) => k.Id += 100;
    int X01() { var d = new Dictionary<string, int>(); Sink.S = d.AsEnumerable(); int t = 0; foreach (var k in d.Keys) { Sink.Poke(k); t += d[k]; } return t; }
    int X02() { var d = new Dictionary<string, int>(); Sink.S = d.Cast<KeyValuePair<string, int>>(); int t = 0; foreach (var k in d.Keys) { Sink.Poke(k); t += d[k]; } return t; }
    int X03() { var d = new Dictionary<string, int>(); var lk = d.GetAlternateLookup<ReadOnlySpan<char>>(); Sink.D = lk.Dictionary; int t = 0; foreach (var k in d.Keys) { Sink.Poke(k); t += d[k]; } return t; }
    int X05() { var d = new Dictionary<Key, int>(); int t = 0; foreach (var k in d.Keys) { Bump(k); t += d[k]; } return t; }
    int X06() { var cmp = new Cmp(); var d = new Dictionary<string, int>(cmp); int t = 0; foreach (var k in d.Keys) { cmp.Upper = true; t += d[k]; } return t; }
    int X16() { Sink.S = _d.AsEnumerable(); int t = 0; foreach (var k in _d.Keys) { Sink.Poke(k); t += _d[k]; } return t; }
    int Kept() { var d = new Dictionary<string, int>(StringComparer.Ordinal); var n = d.Where(p => p.Value > 0).Count(); int t = n; foreach (var k in d.Keys) { Sink.Poke(k); t += d[k]; } return t; }
}
"""

    let fired = suggestCode "CR0032" source
    Assert.Equal(7, fired.Length)
    Assert.Equal(1, fired |> List.filter (fun s -> not s.Fixes.IsEmpty) |> List.length)
    Assert.Contains("foreach (var (k, value) in d) { Sink.Poke(k); t += value; }", fixAll "CR0032" source)

[<Fact>]
let ``CR0032 in a top-level program sees a neighbouring statement that lets the dictionary escape`` () =
    let program (extra: string) (bump: string) =
        "using System;\nusing System.Collections.Generic;\nvar d = new Dictionary<string, int> { [\"a\"] = 1 };\n"
        + extra
        + "int total = 0;\nforeach (var k in d.Keys) { Bump(k); total += d[k]; }\nConsole.WriteLine(total);\n"
        + bump

    let run (source: string) =
        let compilation, tree =
            compileRaw Microsoft.CodeAnalysis.CSharp.LanguageVersion.Latest source

        suggestRaw compilation tree |> List.filter (fun s -> s.Code = "CR0032")

    // `var m = d;` in another top-level statement is an alias the local
    // function visibly writes through: the local is not confined, and the
    // call before the lookup touches a dictionary
    let escaping = run (program "var m = d;\n" "void Bump(string k) => m[k] = 2;\n")

    Assert.Equal(1, escaping.Length)
    Assert.Empty escaping.Head.Fixes

    let confined = run (program "" "static void Bump(string k) { }\n")
    Assert.Equal(1, confined.Length)
    Assert.NotEmpty confined.Head.Fixes

[<Fact>]
let ``CR0032 sees a method group of a writer as an escape and an unbound call as a hazard, not a tuple foreach or GetValueOrDefault``
    ()
    =
    let source =
        """
using System;
using System.Collections.Generic;
static class C
{
    static void Zap(Func<string, bool> f, string k) => f(k);
    static int H11() { var d = new Dictionary<string, int>(); Func<string, bool> rm = d.Remove; int t = 0; foreach (var k in d.Keys) { Zap(rm, k); t += d[k]; } return t; }
    static int H16() { var d = new Dictionary<string, int>(); Func<string, bool> rm = d.Remove; var runner = new List<string>(); int t = 0; foreach (var k in d.Keys) { runner.ForEach(x => rm(x)); t += d[k]; } return t; }
    static int Tuples(Dictionary<string, int> d, List<(int, int)> ps) { int t = 0; foreach (var k in d.Keys) { foreach (var (a, b) in ps) t += a + b; t += d[k]; } return t; }
    static int Default(Dictionary<string, int> d, string other) { int t = 0; foreach (var k in d.Keys) { t += d.GetValueOrDefault(other); t += d[k]; } return t; }
    static int Invoked() { var d = new Dictionary<string, int>(); int t = 0; foreach (var k in d.Keys) { t += d[k]; d.Remove("z"); } return t; }
}
"""

    let fired = suggestCode "CR0032" source
    Assert.Equal(5, fired.Length)

    let fixedNames =
        fired
        |> List.filter (fun s -> not s.Fixes.IsEmpty)
        |> List.map (fun s -> s.Span.Start)
        |> List.length

    Assert.Equal(3, fixedNames)
    let fixedSource = fixAll "CR0032" source
    Assert.Contains("foreach (var (k, value) in d) { foreach (var (a, b) in ps) t += a + b; t += value; }", fixedSource)
    Assert.Contains("foreach (var (k, value) in d) { t += d.GetValueOrDefault(other); t += value; }", fixedSource)
    Assert.Contains("foreach (var (k, value) in d) { t += value; d.Remove(\"z\"); }", fixedSource)

[<Fact>]
let ``CR0032 sweeps past an unbound call: a baseline error is no detection`` () =
    let source =
        "using System.Collections.Generic;\nstatic class C\n{\n    static int K3(Dictionary<string, int> d) { int t = 0; foreach (var k in d.Keys) { Foo(k); t += d[k]; } return t; }\n    static int K4(Dictionary<string, int> d) { int t = 0; foreach (var k in d.Keys) { t += d[k]; Foo(k); } return t; }\n}\n"

    let compilation, tree =
        compileRaw Microsoft.CodeAnalysis.CSharp.LanguageVersion.Latest source

    let fired = suggestRaw compilation tree |> List.filter (fun s -> s.Code = "CR0032")

    Assert.Equal(2, fired.Length)
    Assert.All(fired, fun s -> Assert.NotEmpty s.Fixes)

[<Fact>]
let ``CR0032 follows a partial method's implementation and a delegate a constructor or another member stores; a property read runs its getter alone``
    ()
    =
    let source =
        """
using System;
using System.Collections.Generic;
public sealed partial class P
{
    readonly Dictionary<string, int> _d = new() { ["a"] = 1, ["b"] = 2 };
    readonly Action<string> _bump;
    Action<string>? _late;
    Action<string> _init = k => { };
    Func<string, int> Bump { get; } = k => 1;
    public P() { _bump = Bump2; Bump = k => _d[k] = 100; _init = k => _d[k] = 100; }
    public void Init() { _late = k => _d[k] = 100; }
    void Bump2(string k) { _d[k] = 100; }
    partial void Touch(string k);
    int V05() { int total = 0; foreach (var k in _d.Keys) { Touch(k); total += _d[k]; } return total; }
    int V06() { int total = 0; foreach (var k in _d.Keys) { _bump(k); total += _d[k]; } return total; }
    int V07() { int total = 0; foreach (var k in _d.Keys) { _late?.Invoke(k); total += _d[k]; } return total; }
    int V18() { int total = 0; foreach (var k in _d.Keys) { _init(k); total += _d[k]; } return total; }
    int V23() { int total = 0; foreach (var k in _d.Keys) { Bump(k); total += _d[k]; } return total; }
}
public sealed partial class P { partial void Touch(string k) { _d[k] = 100; } }
public sealed class Q
{
    readonly Dictionary<string, int> _d = new() { ["a"] = 1 };
    Dictionary<string, int> _o = new();
    Dictionary<string, int> Data { get => _o; set => _o = value; }
    static void Note(string k) { }
    int V30() { int total = 0; foreach (var k in Data.Keys) { Note(k); total += Data[k]; } return total; }
    int V30b() { int total = 0; foreach (var k in _d.Keys) { var n = Data.Count; total += _d[k] + n; } return total; }
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
    let fired = suggestions |> List.filter (fun s -> s.Code = "CR0032")
    Assert.Equal(7, fired.Length)
    // P: every call before the lookup visibly writes `_d` — through the
    // partial implementation, the stored lambdas or the method group; Q: a
    // property receiver with a setter, and another property read, run only
    // their getters
    Assert.Equal(5, fired |> List.filter (fun s -> s.Fixes.IsEmpty) |> List.length)
    let fixedSource = fixAll "CR0032" source
    Assert.Contains("foreach (var (k, value) in Data) { Note(k); total += value; }", fixedSource)
    Assert.Contains("foreach (var (k, value) in _d) { var n = Data.Count; total += value + n; }", fixedSource)

[<Fact>]
let ``CR0032 follows a function pointer through a cast in parentheses to the method it takes`` () =
    let source =
        """
using System;
using System.Collections.Generic;
public static unsafe class U
{
    static readonly Dictionary<string, int> S = new() { ["a"] = 1, ["b"] = 2 };
    static void Bump(string k) => S[k] = 99;
    static void Noop(string k) { }
    public static int V40() { delegate*<string, void> fp = (delegate*<string, void>)(&Bump); int total = 0; foreach (var k in S.Keys) { fp(k); total += S[k]; } return total; }
    public static int V41() { delegate*<string, void> fp = &Noop; fp = (delegate*<string, void>)(&Bump); int total = 0; foreach (var k in S.Keys) { fp(k); total += S[k]; } return total; }
    public static int V42() { delegate*<string, void> fp = (delegate*<string, void>)(&Noop); int total = 0; foreach (var k in S.Keys) { fp(k); total += S[k]; } return total; }
}
"""

    // a function pointer wants an unsafe compilation
    let tree =
        Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(normalize source, parseOptions, path = "Sample.cs")

    let compilation =
        Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(
            "Test",
            [ tree ],
            metadataReferences,
            Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(
                Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions = Microsoft.CodeAnalysis.NullableContextOptions.Enable,
                allowUnsafe = true
            )
        )

    Assert.Empty(errorsOf compilation)
    let model = compilation.GetSemanticModel(tree, false)

    let suggestions, failures =
        CSharp.Refactor.Roslyn.Rules.allWithFailures
            tree
            model
            (CSharp.Refactor.Roslyn.Context.forTree None compilation tree false)

    Assert.Empty failures
    let fired = suggestions |> List.filter (fun s -> s.Code = "CR0032")
    Assert.Equal(3, fired.Length)
    // `(delegate*<string, void>)(&Bump)` binds the method group through the
    // conversion: the pointer visibly writes `S`; `&Noop` through the same
    // cast writes nothing
    let methodOf (s: CSharp.Refactor.Suggestion) =
        let line =
            tree.GetText().Lines.[tree.GetLineSpan(s.Span).StartLinePosition.Line].ToString()

        System.Text.RegularExpressions.Regex.Match(line, @"int (V\d+)\(").Groups.[1].Value

    Assert.Equal<string list>([ "V40"; "V41" ], fired |> List.filter (fun s -> s.Fixes.IsEmpty) |> List.map methodOf)

// ---- CR0033 ----

[<Fact>]
let ``an appended concatenation appends its pieces, splitting only string additions`` () =
    let source =
        """
using System.Text;
class C
{
    void A(StringBuilder sb, string a, int n, string b)
    {
        sb.Append(a + n + b);
        sb.Append(n + 1 + a);
        sb.Append($"{a}{n}");
        sb.Append(a);
        sb.Append(n + 1);
    }
}
"""

    let fired = suggestCode "CR0033" source
    Assert.Equal(2, fired.Length)
    let fixedSource = fixAll "CR0033" source
    Assert.Contains("sb.Append(a).Append(n).Append(b);", fixedSource)
    Assert.Contains("sb.Append(n + 1).Append(a);", fixedSource)
    Assert.Contains("sb.Append(n + 1);", fixedSource)

[<Fact>]
let ``CR0033 keeps a concatenation that reads the builder`` () =
    let source =
        """
using System.Text;
class C
{
    void A(StringBuilder sb) { sb.Append("Len:" + sb.Length); sb.Append("x" + sb); }
}
"""

    Assert.Empty(suggestCode "CR0033" source)

// ---- CR0026 / CR0027 / CR0030 / CR0035 notes ----

[<Fact>]
let ``the collection notes fire on the lazy shapes and stay quiet on collections and exclusive arms`` () =
    let source =
        """
using System;
using System.Collections.Generic;
using System.Linq;
class Node { public List<Node> Children = new(); }
class C
{
    void Rewalk(IEnumerable<int> xs, List<int> list)
    {
        for (int i = 0; i < xs.Count(); i++) Console.WriteLine(xs.ElementAt(i));
        for (int i = 0; i < list.Count(); i++) Console.WriteLine(list.ElementAt(i));
        foreach (var x in xs) { var fresh = xs.Where(v => v > x); Console.WriteLine(fresh.Count()); }
        foreach (var g in xs.GroupBy(v => v % 2).OrderBy(g => g.Count())) Console.WriteLine(g.Key);
    }
    void Lazy(List<int> xs)
    {
        xs.Select(x => { Console.WriteLine(x); return x; });
        xs.Where(x => x > 1);
        _ = xs.Select(x => x);
        xs.ToList();
    }
    int Twice(IEnumerable<int> xs) { if (xs.Any()) { foreach (var x in xs) Console.WriteLine(x); } return 0; }
    int Once(IEnumerable<int> xs, bool flag) { if (flag) return xs.Count(); else return xs.Sum(); }
    int Collection(ICollection<int> xs) { if (xs.Any()) { foreach (var x in xs) Console.WriteLine(x); } return 0; }
    int Deferred(IEnumerable<int> xs) { Func<int> f = () => xs.Count(); return xs.Count() + f(); }
    IEnumerable<Node> Walk(Node n) { yield return n; foreach (var c in n.Children) foreach (var d in Walk(c)) yield return d; }
    IEnumerable<Node> Flat(Node n) { yield return n; foreach (var c in n.Children.SelectMany(Flat)) yield return c; }
    IEnumerable<Node> Redirect(Node n) { if (n.Children.Count == 0) return new[] { n }; return Redirect(n.Children[0]); }
}
"""

    let texts (code: string) =
        firedText source (suggestCode code source)

    Assert.Equal<string list>([ "xs.Count()"; "xs.ElementAt(i)" ], texts "CR0026")

    Assert.Equal<string list>(
        [
            "xs.Select(x => { Console.WriteLine(x); return x; })"
            "xs.Where(x => x > 1)"
        ],
        texts "CR0027"
    )

    Assert.Equal<string list>([ "xs.Count()"; "xs.Any()" ], texts "CR0030")
    Assert.Equal<string list>([ "Walk(c)"; "n.Children.SelectMany(Flat)" ], texts "CR0035")

// ---- CR0034 ----

[<Fact>]
let ``a query per element of an outer loop is the N+1, a chunked outer loop is a batch`` () =
    let source =
        """
using System;
using System.Collections.Generic;
using System.Linq;
class Order { public int CustomerId; }
class Customer { public int Id; }
class Db { public IQueryable<Order> Orders; }
class C
{
    void A(Db db, List<Customer> customers)
    {
        foreach (var c in customers)
        {
            foreach (var o in db.Orders.Where(o => o.CustomerId == c.Id)) Console.WriteLine(o);
        }
    }
    void B(Db db, List<Customer> customers)
    {
        var all = customers.Select(c => db.Orders.Where(o => o.CustomerId == c.Id).ToList()).ToList();
    }
    void D(Db db, List<Customer> customers)
    {
        foreach (var chunk in customers.Chunk(200))
        {
            var ids = chunk.Select(c => c.Id).ToList();
            var orders = db.Orders.Where(o => ids.Contains(o.CustomerId)).ToList();
        }
    }
    void E(Db db, List<Customer> customers)
    {
        var orders = db.Orders.ToList();
        foreach (var c in customers) Console.WriteLine(orders.Count(o => o.CustomerId == c.Id));
    }
}
"""

    let fired = suggestCode "CR0034" source
    Assert.Equal(2, fired.Length)

[<Fact>]
let ``CR0034 leaves one query per pre-split id batch alone`` () =
    let source =
        """
using System;
using System.Collections.Generic;
using System.Linq;
class Order { public int CustomerId; public decimal Total; }
class Db { public IQueryable<Order> Orders; }
class InvoiceRun
{
    public decimal Bill(Db db, List<int[]> customerIdGroups)
    {
        decimal billed = 0m;
        foreach (var group in customerIdGroups)
        {
            var orders = db.Orders.Where(o => group.Contains(o.CustomerId)).ToList();
            billed += orders.Sum(o => o.Total);
        }
        return billed;
    }
}
"""

    // `group.Contains(...)` sends the whole batch in one statement: a batch loop, not an N+1
    Assert.Empty(suggestCode "CR0034" source)
