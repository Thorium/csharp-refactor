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

    let fired = fires 2 "CR0024" source
    let fixedSource = fixAll "CR0024" source
    Assert.Contains("void A(int[] xs) { acc.AddRange(xs); }", fixedSource)

    Assert.Contains(
        csharp
            """
                void B(int[] xs)
                {
                    acc.AddRange(xs);
                }
            """,
        fixedSource
    )

// ---- CR0031 ----

[<Fact>]
let ``a parameterless Random used for calls becomes Random.Shared, a seeded or stored one stays`` () =
    let source =
        csharp
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

    let fired = fires 2 "CR0031" source
    let fixedSource = fixAll "CR0031" source
    Assert.Contains("int A() => Random.Shared.Next(10);", fixedSource)
    Assert.Contains("int B() { var r = Random.Shared; return r.Next(1, 6) + r.Next(); }", fixedSource)

[<Fact>]
let ``CR0031 takes a seed read off the clock, a thread or a fresh GUID for no seed; any other operand keeps it`` () =
    let source =
        csharp
            """
            using System;
            using System.Threading;
            class C
            {
                int _seed;
                int A() => new Random(DateTime.Now.Millisecond).Next(10);
                int B() { var r = new Random((int)DateTime.Now.Ticks); return r.Next(); }
                int D() => new Random(DateTime.Now.Second + DateTime.Now.Millisecond + Thread.CurrentThread.ManagedThreadId).Next();
                int E() => new Random(Environment.TickCount).Next();
                int F() => new Random(Guid.NewGuid().GetHashCode()).Next();
                int G() => new Random(unchecked((int)DateTime.UtcNow.Ticks * 31)).Next();
                int H(int seed) => new Random(seed).Next();
                int I() => new Random(_seed + Environment.TickCount).Next();
                int J() => new Random(42).Next();
                int K() => new Random(40 + 2).Next();
                int L(DateTime at) => new Random(at.Millisecond).Next();
                Random M() { var r = new Random(Environment.TickCount); return r; }
                int N() => new Random(DateTime.Today.DayOfYear).Next();
                int O() => new Random(DateTime.Now.Hour).Next();
            }
            """

    let fired = fires 6 "CR0031" source
    Assert.Contains("seeded from the clock", fired.Head.Message)
    let fixedSource = fixAll "CR0031" source
    Assert.Contains("int A() => Random.Shared.Next(10);", fixedSource)
    Assert.Contains("int B() { var r = Random.Shared; return r.Next(); }", fixedSource)
    Assert.Contains("int D() => Random.Shared.Next();", fixedSource)
    Assert.Contains("int E() => Random.Shared.Next();", fixedSource)
    Assert.Contains("int F() => Random.Shared.Next();", fixedSource)
    Assert.Contains("int G() => Random.Shared.Next();", fixedSource)
    Assert.Contains("new Random(seed).Next();", fixedSource)
    Assert.Contains("new Random(_seed + Environment.TickCount).Next();", fixedSource)
    Assert.Contains("new Random(42).Next();", fixedSource)
    Assert.Contains("new Random(40 + 2).Next();", fixedSource)
    Assert.Contains("new Random(at.Millisecond).Next();", fixedSource)
    Assert.Contains("var r = new Random(Environment.TickCount); return r;", fixedSource)
    // a seed that holds for the day or the hour is a decision
    Assert.Contains("new Random(DateTime.Today.DayOfYear).Next();", fixedSource)
    Assert.Contains("new Random(DateTime.Now.Hour).Next();", fixedSource)

[<Fact>]
let ``review 2026-10-03b CR0031 reads a clock seed through operators; a seed in a variable, a field's Random and a locked or rebound one stay``
    ()
    =
    let source =
        csharp
            """
            using System;
            using System.Diagnostics;
            using System.Threading;
            class C
            {
                static Random _shared = new Random(Environment.TickCount);
                Random _mine = new Random((int)DateTime.Now.Ticks);
                int A() => new Random(Environment.TickCount ^ Thread.CurrentThread.ManagedThreadId).Next();
                int B() => new Random((int)DateTime.Now.Ticks & 0xFFFF).Next();
                int D() => new Random(unchecked((int)Stopwatch.GetTimestamp())).Next();
                int E() { var seed = Environment.TickCount; return new Random(seed).Next(); }
                int F() { var r = new Random(Environment.TickCount); lock (r) { return r.Next(); } }
                int G() { var r = new Random(Environment.TickCount); r = new Random(2); return r.Next(); }
            }
            """

    fires 3 "CR0031" source |> ignore
    let fixedSource = fixAll "CR0031" source
    Assert.Contains("int A() => Random.Shared.Next();", fixedSource)
    Assert.Contains("int B() => Random.Shared.Next();", fixedSource)
    Assert.Contains("int D() => Random.Shared.Next();", fixedSource)
    Assert.Contains("static Random _shared = new Random(Environment.TickCount);", fixedSource)
    Assert.Contains("return new Random(seed).Next();", fixedSource)
    Assert.Contains("var r = new Random(Environment.TickCount); lock (r)", fixedSource)
    Assert.Contains("var r = new Random(Environment.TickCount); r = new Random(2);", fixedSource)

[<Fact>]
let ``CR0031 without Random.Shared in the framework only notes the clock seed`` () =
    // the netstandard2.0 reference assembly, where the NuGet cache holds it:
    // a framework whose Random has no Shared
    let reference =
        System.IO.Path.Combine(
            System.Environment.GetFolderPath System.Environment.SpecialFolder.UserProfile,
            ".nuget",
            "packages",
            "netstandard.library",
            "2.0.3",
            "build",
            "netstandard2.0",
            "ref",
            "netstandard.dll"
        )

    if System.IO.File.Exists reference then
        let source =
            normalize (
                csharp
                    """
                    using System;
                    class C
                    {
                        int A() => new Random(Environment.TickCount).Next(10);
                        int B() => new Random().Next(10);
                    }
                    """
            )

        let tree =
            Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(source, parseOptions, path = "Sample.cs")

        let compilation =
            Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(
                "Old",
                [ tree ],
                [
                    Microsoft.CodeAnalysis.MetadataReference.CreateFromFile reference
                    :> Microsoft.CodeAnalysis.MetadataReference
                ],
                Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(
                    Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary
                )
            )

        Assert.Empty(errorsOf compilation)

        let fired = suggestRaw compilation tree |> List.filter (fun s -> s.Code = "CR0031")

        Assert.Equal<string list>([ "new Random(Environment.TickCount)" ], firedText source fired)
        Assert.Empty fired.Head.Fixes

// ---- CR0032 ----

[<Fact>]
let ``a Keys loop with a lookup per key enumerates the pairs, a writing loop stays`` () =
    let source =
        csharp
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

    let fired = fires 2 "CR0032" source
    let fixedSource = fixAll "CR0032" source

    Assert.Contains(
        normalize (
            csharp
                """
                        foreach (var (k, value) in d)
                        {
                            Console.WriteLine(k + value);
                            Console.WriteLine(value * 2);
                        }
                """
        ),
        fixedSource
    )

    Assert.Contains("foreach (var (key, v) in d) Console.WriteLine(v + value);", fixedSource)

[<Fact>]
let ``CR0032 still enumerates the pairs when nothing before a lookup can write the dictionary`` () =
    let source =
        csharp
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

    let fired = fires 11 "CR0032" source
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
        csharp
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

    let fired = fires 10 "CR0032" source
    Assert.All(fired, fun s -> Assert.Empty s.Fixes)

[<Fact>]
let ``CR0032 lets any call run before the lookup of a dictionary no other code can reach`` () =
    let source =
        csharp
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

    let fired = fires 4 "CR0032" source
    Assert.All(fired, fun s -> Assert.NotEmpty s.Fixes)
    let fixedSource = fixAll "CR0032" source

    Assert.Contains(
        "foreach (var (k, value) in _counts) { if (ShouldSkip(k)) continue; sb.Append(k).Append(value); }",
        fixedSource
    )

    Assert.Contains("""foreach (var (k, value) in d) { logger.Info("key " + k); total += value; }""", fixedSource)
    Assert.Contains("foreach (var (k, value) in d) { var n = Norm(k); sb.Append(n + value); }", fixedSource)
    Assert.Contains("foreach (var (k, value) in d) { if (ShouldSkip(k)) continue; total += value; }", fixedSource)

[<Fact>]
let ``CR0032 is a note for a visible iterator, an alias store or a closure write; an interface call or an unknown Dispose is fixed``
    ()
    =
    let source =
        csharp
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

    let fired = fires 5 "CR0032" source
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
        csharp
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

    let fired = fires 7 "CR0032" source
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
        csharp
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

    let fired = fires 3 "CR0032" source
    // three deep is seen; five deep is beyond the look and the accepted residual
    Assert.Empty fired.[0].Fixes
    Assert.NotEmpty fired.[1].Fixes
    Assert.Empty fired.[2].Fixes

[<Fact>]
let ``CR0032 counts a dictionary as confined only through its own members, BCL keys and a BCL comparer`` () =
    let source =
        csharp
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

    let fired = fires 7 "CR0032" source
    Assert.Equal(1, fired |> List.filter (fun s -> not s.Fixes.IsEmpty) |> List.length)
    Assert.Contains("foreach (var (k, value) in d) { Sink.Poke(k); t += value; }", fixAll "CR0032" source)

[<Fact>]
let ``CR0032 in a top-level program sees a neighbouring statement that lets the dictionary escape`` () =
    let program (extra: string) (bump: string) =
        csharp
            """
            using System;
            using System.Collections.Generic;
            var d = new Dictionary<string, int> { ["a"] = 1 };

            """
        + extra
        + csharp
            """
            int total = 0;
            foreach (var k in d.Keys) { Bump(k); total += d[k]; }
            Console.WriteLine(total);

            """
        + bump

    let run (count: int) (source: string) =
        let compilation, tree =
            compileRaw Microsoft.CodeAnalysis.CSharp.LanguageVersion.Latest source

        let fired =
            suggestRawClean compilation tree |> List.filter (fun s -> s.Code = "CR0032")

        assertFired count source fired
        fired

    // `var m = d;` in another top-level statement is an alias the local
    // function visibly writes through: the local is not confined, and the
    // call before the lookup touches a dictionary
    let escaping = run 1 (program "var m = d;\n" "void Bump(string k) => m[k] = 2;\n")
    Assert.Empty escaping.Head.Fixes

    let confined = run 1 (program "" "static void Bump(string k) { }\n")
    Assert.NotEmpty confined.Head.Fixes

[<Fact>]
let ``CR0032 sees a method group of a writer as an escape and an unbound call as a hazard, not a tuple foreach or GetValueOrDefault``
    ()
    =
    let source =
        csharp
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

    let fired = fires 5 "CR0032" source

    let fixedNames =
        fired
        |> List.filter (fun s -> not s.Fixes.IsEmpty)
        |> List.map (fun s -> s.Span.Start)
        |> List.length

    Assert.Equal(3, fixedNames)
    let fixedSource = fixAll "CR0032" source
    Assert.Contains("foreach (var (k, value) in d) { foreach (var (a, b) in ps) t += a + b; t += value; }", fixedSource)
    Assert.Contains("foreach (var (k, value) in d) { t += d.GetValueOrDefault(other); t += value; }", fixedSource)
    Assert.Contains("""foreach (var (k, value) in d) { t += value; d.Remove("z"); }""", fixedSource)

[<Fact>]
let ``CR0032 sweeps past an unbound call: a baseline error is no detection`` () =
    let source =
        csharp
            """
            using System.Collections.Generic;
            static class C
            {
                static int K3(Dictionary<string, int> d) { int t = 0; foreach (var k in d.Keys) { Foo(k); t += d[k]; } return t; }
                static int K4(Dictionary<string, int> d) { int t = 0; foreach (var k in d.Keys) { t += d[k]; Foo(k); } return t; }
            }

            """

    let compilation, tree =
        compileRaw Microsoft.CodeAnalysis.CSharp.LanguageVersion.Latest source

    // the unbound `Foo` is the point: raw, without the compile-clean check
    let fired = suggestRaw compilation tree |> List.filter (fun s -> s.Code = "CR0032")

    assertFired 2 source fired
    Assert.All(fired, fun s -> Assert.NotEmpty s.Fixes)

[<Fact>]
let ``CR0032 follows a partial method's implementation and a delegate a constructor or another member stores; a property read runs its getter alone``
    ()
    =
    let source =
        csharp
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
    assertFired 7 source fired
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
        csharp
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
    assertFired 3 source fired
    // `(delegate*<string, void>)(&Bump)` binds the method group through the
    // conversion: the pointer visibly writes `S`; `&Noop` through the same
    // cast writes nothing
    let methodOf (s: CSharp.Refactor.Suggestion) =
        let line =
            tree.GetText().Lines.[tree.GetLineSpan(s.Span).StartLinePosition.Line].ToString()

        System.Text.RegularExpressions.Regex.Match(line, @"int (V\d+)\(").Groups.[1].Value

    Assert.Equal<string list>([ "V40"; "V41" ], fired |> List.filter (fun s -> s.Fixes.IsEmpty) |> List.map methodOf)

[<Fact>]
let ``CR0032 times a compound store's getter before its right side, and formats through a base type's ToString`` () =
    // `acc.Sum += d[k]` runs Sum's getter BEFORE reading d[k]: a getter
    // writing the dictionary happens ahead of the lookup, which the loop
    // over pairs would already have read. An interpolation formats `x`
    // through the ToString Derived inherits from Base, which writes it too.
    // The controls: a setter alone runs after the read, and a type whose
    // ToString (own or inherited) writes nothing
    let source =
        csharp
            """
            using System;
            using System.Collections.Generic;
            class Holder
            {
                readonly Dictionary<string, int> m; int sum;
                public Holder(Dictionary<string, int> m) { this.m = m; }
                public int Sum { get { m["z"] = 1; return sum; } set { sum = value; } }
                public int Quiet { get { return sum; } set { m["z"] = 1; sum = value; } }
            }
            class Base
            {
                protected Dictionary<string, int> m;
                public override string ToString() { m["z"] = 1; return "b"; }
            }
            class Derived : Base { public Derived(Dictionary<string, int> d) { m = d; } }
            class Plain { public override string ToString() => "p"; }
            class PlainDerived : Plain { }
            class C
            {
                int W1(Dictionary<string, int> d) { var acc = new Holder(d); foreach (var k in d.Keys) { acc.Sum += d[k]; } return acc.Sum; }
                string W2(Dictionary<string, int> d) { var x = new Derived(d); var r = ""; foreach (var k in d.Keys) { r += $"{x}{d[k]}"; } return r; }
                int W3(Dictionary<string, int> d) { var acc = new Holder(d); foreach (var k in d.Keys) { acc.Quiet = d[k]; } return 0; }
                string W4(Dictionary<string, int> d) { var y = new PlainDerived(); var r = ""; foreach (var k in d.Keys) { r += $"{y}{d[k]}"; } return r; }
            }
            """

    let fired = fires 4 "CR0032" source
    // W1 and W2 keep the loop as notes; W3 and W4 are fixed
    Assert.Equal(2, fired |> List.filter (fun s -> s.Fixes.IsEmpty) |> List.length)
    let fixedSource = fixAll "CR0032" source
    Assert.Contains("foreach (var k in d.Keys) { acc.Sum += d[k]; }", fixedSource)
    Assert.Contains("""foreach (var k in d.Keys) { r += $"{x}{d[k]}"; }""", fixedSource)
    Assert.Contains("foreach (var (k, value) in d) { acc.Quiet = value; }", fixedSource)
    Assert.Contains("""foreach (var (k, value) in d) { r += $"{y}{value}"; }""", fixedSource)

// ---- CR0033 ----

[<Fact>]
let ``an appended concatenation appends its pieces, splitting only string additions`` () =
    let source =
        csharp
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

    let fired = fires 2 "CR0033" source
    let fixedSource = fixAll "CR0033" source
    Assert.Contains("sb.Append(a).Append(n).Append(b);", fixedSource)
    Assert.Contains("sb.Append(n + 1).Append(a);", fixedSource)
    Assert.Contains("sb.Append(n + 1);", fixedSource)

[<Fact>]
let ``CR0033 keeps a concatenation that reads the builder`` () =
    let source =
        csharp
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
        csharp
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
        csharp
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

    fires 2 "CR0034" source |> ignore

[<Fact>]
let ``CR0034 leaves one query per pre-split id batch alone`` () =
    let source =
        csharp
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

// ---- CR0182 ----

[<Fact>]
let ``a ContainsKey check and an indexer read of the same key become one TryGetValue; a write, a reachable call or an expression tree keep them``
    ()
    =
    let source =
        csharp
            """
            using System;
            using System.Collections.Generic;
            using System.Linq.Expressions;
            class C
            {
                Dictionary<string, int> _d = new();
                static void Use(int v) { }
                void Reset() => _d.Clear();
                static string Key() => "k";
                void A(Dictionary<string, int> d, string k) { if (d.ContainsKey(k)) Use(d[k]); }
                int B(Dictionary<string, int> d, string k) { if (d.ContainsKey(k) && d[k] > 0) return d[k]; return 0; }
                int D(Dictionary<string, int> d, string k)
                {
                    if (!d.ContainsKey(k)) return 0;
                    return d[k] * 2;
                }
                int E(IReadOnlyDictionary<string, int> d, string k) => d.ContainsKey(k) ? d[k] : -1;
                void F(Dictionary<string, int> d, string k) { if (d.ContainsKey(k)) d[k] = d[k] + 1; }
                void G(Dictionary<string, int> d, string k) { if (d.ContainsKey(k)) { d.Remove(k); Use(d[k]); } }
                void H(string k) { if (_d.ContainsKey(k)) { Reset(); Use(_d[k]); } }
                Expression<Func<Dictionary<string, int>, int>> I = d => d.ContainsKey("a") ? d["a"] : 0;
                int J { get => 0; set { if (_d.ContainsKey("v")) Use(_d["v"] + value); } }
                void K(Dictionary<string, int> d) { if (d.ContainsKey(Key())) Use(d[Key()]); }
                void L(Dictionary<string, int> d, string k) { if (d.ContainsKey(k)) Use(1); else Use(d[k]); }
                void M(Dictionary<string, int> d, string k, int value) { if (d.ContainsKey(k)) Use(d[k] + value); }
            }
            """

    // A, B, D, E, J, M; F stores, G removes, H calls a method of the type over a field,
    // I is an expression tree, K keys by a call, L reads in the else
    Assert.Equal(6, (suggestCode "CR0182" source).Length)
    let fixedSource = fixAll "CR0182" source
    Assert.Contains("if (d.TryGetValue(k, out var value)) Use(value);", fixedSource)
    Assert.Contains("if (d.TryGetValue(k, out var value) && value > 0) return value; return 0;", fixedSource)
    Assert.Contains("if (!d.TryGetValue(k, out var value)) return 0;", fixedSource)
    Assert.Contains("return value * 2;", fixedSource)
    Assert.Contains("=> d.TryGetValue(k, out var value) ? value : -1;", fixedSource)
    Assert.Contains("if (d.ContainsKey(k)) d[k] = d[k] + 1;", fixedSource)
    Assert.Contains("d.Remove(k); Use(d[k]);", fixedSource)
    Assert.Contains("Reset(); Use(_d[k]);", fixedSource)
    Assert.Contains("""d => d.ContainsKey("a") ? d["a"] : 0;""", fixedSource)
    Assert.Contains("""if (_d.TryGetValue("v", out var found)) Use(found + value);""", fixedSource)
    Assert.Contains("if (d.ContainsKey(Key())) Use(d[Key()]);", fixedSource)
    Assert.Contains("else Use(d[k]);", fixedSource)
    Assert.Contains("if (d.TryGetValue(k, out var value2)) Use(value2 + value);", fixedSource)

// ---- CR0184 ----

[<Fact>]
let ``a private static readonly array the code only reads becomes an ImmutableArray; a store, a params argument, Contains or a struct element keep it``
    ()
    =
    let source =
        csharp
            """
            using System;
            using System.Collections.Generic;
            using System.Linq;
            struct Point { public int X; }
            class C
            {
                private static readonly string[] Allowed = { "a", "b" };
                private static readonly int[] Weights = new[] { 1, 2 };
                private static readonly int[] Written = { 1 };
                private static readonly string[] Formatted = { "x" };
                private static readonly string[] Probed = { "p" };
                private static readonly Point[] Points = { new Point() };
                private static readonly string[] Joined = { "j" };
                public static readonly string[] Shared = { "s" };
                static void Take(IReadOnlyList<string> xs) { }
                void Use()
                {
                    Console.WriteLine(Allowed.Length + Allowed[0]);
                    foreach (var a in Allowed) Console.WriteLine(a);
                    Take(Allowed);
                    Console.WriteLine(Allowed.Where(a => a != "").Count());
                    Console.WriteLine(Weights.Sum());
                    Written[0] = 2;
                    Console.WriteLine(string.Format("{0}", Formatted));
                    Console.WriteLine(Probed.Contains("p"));
                    Points[0].X = 1;
                    Console.WriteLine(string.Join(",", Joined));
                    Console.WriteLine(Shared[0]);
                }
            }
            """

    Assert.Equal<string list>([ "Allowed"; "Weights" ], firedText source (suggestCode "CR0184" source))
    let fixedSource = fixAll "CR0184" source
    Assert.Contains("using System.Collections.Immutable;", fixedSource)
    Assert.Contains("""private static readonly ImmutableArray<string> Allowed = ["a", "b"];""", fixedSource)
    Assert.Contains("private static readonly ImmutableArray<int> Weights = [1, 2];", fixedSource)
    Assert.Contains("private static readonly int[] Written = { 1 };", fixedSource)
    Assert.Contains("""private static readonly string[] Formatted = { "x" };""", fixedSource)
    Assert.Contains("""private static readonly string[] Probed = { "p" };""", fixedSource)
    Assert.Contains("private static readonly Point[] Points", fixedSource)
    Assert.Contains("""private static readonly string[] Joined = { "j" };""", fixedSource)

[<Fact>]
let ``CR0182 keeps the lookup where the value could change between the check and the read`` () =
    // each shape a rewrite gets wrong: it reads the value at the check, the code at the read
    let source =
        csharp
            """
            using System;
            using System.Collections.Generic;
            using System.Linq;
            using System.Text;
            class Holder { public Dictionary<string, int> Map = new(); }
            class C
            {
                string key = "a";
                string _k = "a";
                Dictionary<string, int> _d = new();
                Dictionary<string, int> Map { get; } = new();
                void Advance() => _k = "b";
                void Bump() => Map["a"] = 42;
                string Shadowed(Dictionary<string, int> d)
                {
                    var sb = new StringBuilder();
                    if (d.ContainsKey(key)) foreach (var key in new[] { "b", "c" }) sb.Append(d[key]);
                    return sb.ToString();
                }
                string Lambda(Dictionary<string, int> d, string k, string[] xs)
                {
                    if (d.ContainsKey(k)) return string.Join(",", xs.Select(k => d[k]));
                    return "";
                }
                int FieldKey(Dictionary<string, int> d) { if (d.ContainsKey(_k)) { Advance(); return d[_k]; } return 0; }
                int PropertyDictionary(string k) { if (Map.ContainsKey(k)) { Bump(); return Map[k]; } return 0; }
                int LocalFunction()
                {
                    var d = new Dictionary<string, int> { ["a"] = 1 };
                    void Set() => d["a"] = 42;
                    if (d.ContainsKey("a")) { Set(); return d["a"]; }
                    return 0;
                }
                int Deferred()
                {
                    var d = new Dictionary<string, int> { ["a"] = 1 };
                    Func<int> get = () => -1;
                    if (d.ContainsKey("a")) { get = () => d["a"]; }
                    d["a"] = 99;
                    return get();
                }
                int Owner(Holder o, Holder o2) { if (o.Map.ContainsKey("a")) { o = o2; return o.Map["a"]; } return 0; }
                int Alias()
                {
                    var d = new Dictionary<string, int> { ["a"] = 1 };
                    var alias = d;
                    if (!d.ContainsKey("a")) return 0;
                    alias["a"] = 7;
                    return d["a"];
                }
                int Loop(Dictionary<string, int> d, string k, int[] xs)
                {
                    var t = 0;
                    if (d.ContainsKey(k)) foreach (var x in xs) t += Twice(d[k]);
                    return t;
                }
                static int Twice(int x) => x * 2;
                int Private()
                {
                    var d = new Dictionary<string, int> { ["a"] = 1 };
                    if (d.ContainsKey("a")) { Console.WriteLine(); return d["a"]; }
                    return 0;
                }
            }
            """

    // only Private: a dictionary the member made and never hands on, a constant key - the call cannot reach them
    Assert.Equal<string list>([ "d.ContainsKey(\"a\")" ], firedText source (suggestCode "CR0182" source))

[<Fact>]
let ``review 2026-09-28: CR0184 keeps an array read by Aggregate or ElementAt, sliced, handed to an overloaded or type-testing callee``
    ()
    =
    let source =
        csharp
            """
            using System;
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                private static readonly int[] Weights = { };
                private static readonly int[] Picked = { 1, 2 };
                private static readonly int[] Sliced = { 1, 2, 3 };
                private static readonly int[] Spanned = { 1, 2 };
                private static readonly int[] Tested = { 1, 2 };
                static string Sum(ReadOnlySpan<int> xs) => "span";
                static string Sum(IEnumerable<int> xs) => "enumerable";
                static string Kind(IEnumerable<int> xs) => xs is int[] ? "array" : "sequence";
                void Use()
                {
                    Console.WriteLine(Weights.Aggregate((a, b) => a + b));
                    Console.WriteLine(Picked.ElementAt(5));
                    Console.WriteLine(Sliced[1..2].Length);
                    Console.WriteLine(Sum(Spanned));
                    Console.WriteLine(Kind(Tested));
                }
            }
            """

    Assert.Empty(suggestCode "CR0184" source)

[<Fact>]
let ``parity 2026-09-28 CR0182 keeps a dictionary whose indexer or TryGetValue is not the BCL's`` () =
    let source =
        csharp
            """
            using System.Collections.Generic;
            class Counting<K, V> : Dictionary<K, V>
            {
                public int Reads;
                public new V this[K k] { get { Reads++; return base[k]; } }
            }
            class Positional<V> : Dictionary<long, V>
            {
                public V this[int i] => default;
            }
            abstract class Holder
            {
                public abstract Dictionary<string, int> Map { get; }
                int A(string k) { if (this.Map.ContainsKey(k)) return this.Map[k]; return 0; }
            }
            class C
            {
                int A(Counting<string, int> d, string k) { if (d.ContainsKey(k)) return d[k]; return 0; }
                int B(Positional<int> d, int i) { if (d.ContainsKey(i)) return d[i]; return 0; }
                int D(Dictionary<string, int> d, string k) { if (d.ContainsKey(k)) return d[k]; return 0; }
            }
            """

    // D alone: Counting's indexer counts, Positional's d[i] is positional, Map is computed by a derived getter
    Assert.Equal<string list>([ "d.ContainsKey(k)" ], firedText source (suggestCode "CR0182" source))
