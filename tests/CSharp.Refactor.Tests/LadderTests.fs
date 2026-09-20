module CSharp.Refactor.Tests.LadderTests

open Xunit
open CSharp.Refactor.Tests.Harness

// ---- CR0148 ----

[<Fact>]
let ``a constant ASCII string's bytes become a u8 literal, bare for a span; non-ASCII stays`` () =
    let source =
        """
using System;
using System.Text;
class C
{
    byte[] A() => Encoding.UTF8.GetBytes("hello");
    ReadOnlySpan<byte> B() => Encoding.ASCII.GetBytes("hi");
    byte[] D() => Encoding.UTF8.GetBytes("héllo");
    byte[] E(string s) => Encoding.UTF8.GetBytes(s);
}
"""

    Assert.Equal(2, (suggestCode "CR0148" source).Length)
    let fixedSource = fixAll "CR0148" source
    Assert.Contains("byte[] A() => \"hello\"u8.ToArray();", fixedSource)
    Assert.Contains("ReadOnlySpan<byte> B() => \"hi\"u8;", fixedSource)

// ---- CR0149 ----

[<Fact>]
let ``a property every construction sets becomes required; one a constructor or a method assigns stays`` () =
    let source =
        """
class Options
{
    public string Name { get; init; }
    public int Port { get; init; }
    public string Host { get; set; }
    public Options() { Host = "x"; }
}
class Use
{
    static Options A() => new Options { Name = "a", Port = 1 };
    static Options B() => new Options { Name = "b" };
    static void D(Options o) { o.Host = "y"; }
}
"""

    Assert.Equal<string list>([ "Name" ], firedText source (suggestCode "CR0149" source))
    Assert.Contains("public required string Name { get; init; }", fixAll "CR0149" source)

// ---- CR0150 / CR0152 ----

[<Fact>]
let ``a static set filled once and only read is frozen; a gate object used only by lock is a Lock`` () =
    let source =
        """
using System;
using System.Collections.Generic;
class C
{
    private static readonly HashSet<string> Allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "a", "b" };
    private static readonly Dictionary<string, int> Codes = new Dictionary<string, int> { ["a"] = 1 };
    private static readonly HashSet<int> Seen = new HashSet<int> { 1 };
    private readonly object _gate = new();
    private readonly object _shared = new object();
    bool A(string s) => Allowed.Contains(s);
    int B(string s) => Codes.TryGetValue(s, out var v) ? v : 0;
    void D(int x) { Seen.Add(x); }
    void E() { lock (_gate) { } }
    void F() { lock (_shared) { } Console.WriteLine(_shared); }
}
"""

    Assert.Equal(2, (suggestCode "CR0150" source).Length)
    let frozen = fixAll "CR0150" source
    Assert.Contains("using System.Collections.Frozen;", frozen)

    Assert.Contains(
        "private static readonly FrozenSet<string> Allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { \"a\", \"b\" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);",
        frozen
    )

    Assert.Contains(
        "FrozenDictionary<string, int> Codes = new Dictionary<string, int> { [\"a\"] = 1 }.ToFrozenDictionary();",
        frozen
    )

    Assert.Contains("HashSet<int> Seen", frozen)
    Assert.Equal(1, (suggestCode "CR0152" source).Length)
    Assert.Contains("private readonly Lock _gate = new();", fixAll "CR0152" source)

// ---- CR0153 / CR0154 ----

[<Fact>]
let ``a backing field used only by its property becomes the field keyword; a null-guarded assignment becomes null-conditional``
    ()
    =
    let source =
        """
class C
{
    private int _count = 3;
    public int Count { get => _count; set => _count = value < 0 ? 0 : value; }
    private string _name;
    public string Name { get => _name; set => _name = value; }
    public C() { _name = "x"; }
    void A(C other, int v) { if (other != null) other.Count = v; }
    void B(C other, int v) { if (other is not null) { other.Count += v; } }
    void D(C other, int v) { if (other != null) { other.Count = v; other.Name = "y"; } }
}
"""

    Assert.Equal<string list>([ "Count" ], firedText source (suggestCode "CR0153" source))
    let fixedSource = fixAll "CR0153" source
    Assert.Contains("public int Count { get => field; set => field = value < 0 ? 0 : value; } = 3;", fixedSource)
    Assert.DoesNotContain("_count", fixedSource)
    Assert.Equal(2, (suggestCode "CR0154" source).Length)
    let assigned = fixAll "CR0154" source
    Assert.Contains("void A(C other, int v) { other?.Count = v; }", assigned)
    Assert.Contains("void B(C other, int v) { other?.Count += v; }", assigned)

// ---- CR0157 ----

[<Fact>]
let ``an exhaustive switch whose discard throws becomes UnreachableException; a message keeps a note, a missing case nothing``
    ()
    =
    let source =
        """
using System;
enum Kind { A, B }
abstract record Shape;
sealed record Circle(double R) : Shape;
sealed record Square(double S) : Shape;
class C
{
    int A(Kind k) => k switch { Kind.A => 1, Kind.B => 2, _ => throw new InvalidOperationException() };
    int B(Kind k) => k switch { Kind.A => 1, _ => throw new InvalidOperationException() };
    int D(Kind k) => k switch { Kind.A => 1, Kind.B => 2, _ => throw new InvalidOperationException("kind") };
    double E(Shape s) => s switch { Circle c => c.R, Square q => q.S, _ => throw new ArgumentOutOfRangeException() };
}
"""

    let fired = suggestCode "CR0157" source
    Assert.Equal(3, fired.Length)
    Assert.Equal(1, fired |> List.filter (fun s -> s.Fixes.IsEmpty) |> List.length)
    let fixedSource = fixAll "CR0157" source
    Assert.Contains("Kind.B => 2, _ => throw new UnreachableException() }", fixedSource)
    Assert.Contains("Square q => q.S, _ => throw new UnreachableException() }", fixedSource)
    Assert.Contains("using System.Diagnostics;", fixedSource)

// ---- CR0147 / CR0151 ----

[<Fact>]
let ``a chain of length tests becomes a switch over list patterns, and CR0002 stands down on it`` () =
    let source =
        """
using System;
class C
{
    string A(int[] xs)
    {
        if (xs.Length == 0)
        {
            return "none";
        }
        else if (xs.Length == 1)
        {
            var a = xs[0];
            return "one " + a;
        }
        else
        {
            return "many";
        }
    }
    string B(int[] xs)
    {
        if (xs.Length == 0) return "none";
        else if (xs.Length == 2) { var a = xs[2]; return "x"; }
        else return "many";
    }
}
"""

    let fired = suggestCode "CR0147" source
    Assert.Equal(1, fired.Length)
    Assert.Empty(suggestCode "CR0002" source)
    let fixedSource = normalize (fixAll "CR0147" source)

    Assert.Contains(
        "switch (xs)\n        {\n            case []:\n                return \"none\";\n            case [var a]:\n                return \"one \" + a;\n            default:\n                return \"many\";\n        }",
        fixedSource
    )

[<Fact>]
let ``a params array the body only reads becomes a params span; one handed on or captured stays`` () =
    let source =
        """
using System;
using System.Linq;
class C
{
    private int Sum(params int[] xs) { var t = 0; foreach (var x in xs) t += x; return t + xs.Length; }
    private int First(params int[] xs) => xs.Length > 0 ? xs[0] : -1;
    private int Keep(params int[] xs) { Store(xs); return 0; }
    private int Query(params int[] xs) => xs.Sum();
    private int Lazy(params int[] xs) { Func<int> f = () => xs.Length; return f(); }
    void Store(int[] a) { }
    int Use() => Sum(1, 2) + First() + Keep(3) + Query(4) + Lazy(5);
}
"""

    Assert.Equal(2, (suggestCode "CR0151" source).Length)
    let fixedSource = fixAll "CR0151" source
    Assert.Contains("private int Sum(params ReadOnlySpan<int> xs)", fixedSource)
    Assert.Contains("private int First(params ReadOnlySpan<int> xs)", fixedSource)
    Assert.Contains("private int Keep(params int[] xs)", fixedSource)

[<Fact>]
let ``the C# 15 union rules stay silent below C# 15`` () =
    let source =
        """
abstract record Shape;
sealed record Circle(double R) : Shape;
sealed record Square(double S) : Shape;
"""

    Assert.Empty(suggestCode "CR0156" source)
    Assert.Empty(suggestCode "CR0158" source)
