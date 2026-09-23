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

[<Fact>]
let ``CR0149 stands down for a derived type constructed bare in another file and for a new() type argument`` () =
    let options = "class Options { public string Name { get; init; } }"
    let setting = "class Use { Options A() => new Options { Name = \"a\" }; }"
    Assert.Equal(1, (suggestInProject false false "CR0149" [ "Options.cs", options; "Use.cs", setting ]).Length)

    let derived =
        "class Derived : Options { }\nclass Use { Options A() => new Options { Name = \"a\" }; Derived B() => new Derived(); }"

    Assert.Empty(suggestInProject false false "CR0149" [ "Options.cs", options; "Use.cs", derived ])

    let generic =
        "class Use { Options A() => new Options { Name = \"a\" }; static T Make<T>() where T : new() => new T(); Options B() => Make<Options>(); }"

    Assert.Empty(suggestInProject false false "CR0149" [ "Options.cs", options; "Use.cs", generic ])
    // a public type of a library under --api-changes: without the oracle, callers beyond are unseen
    let exported = "public class Options { public string Name { get; init; } }"
    Assert.Empty(suggestInProject false true "CR0149" [ "Options.cs", exported; "Use.cs", setting ])
    Assert.Equal(1, (suggestInProject true true "CR0149" [ "Options.cs", exported; "Use.cs", setting ]).Length)

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

[<Fact>]
let ``CR0150 reads the uses of a shared field in the other files: a write or a foreach there keeps the Dictionary`` () =
    let registry (access: string) =
        $"""
using System.Collections.Generic;
{access} static class Registry
{{
    {access} static readonly Dictionary<string, int> Map = new Dictionary<string, int> {{ ["a"] = 1 }};
    static int Get(string k) => Map.TryGetValue(k, out var v) ? v : 0;
}}
"""

    let writer =
        "class Writer { void M() { Registry.Map[\"b\"] = 2; Registry.Map.Add(\"c\", 3); } }"

    let walker =
        "class Walker { int M() { var n = 0; foreach (var kv in Registry.Map) n += kv.Value; return n; } }"

    let reader = "class Reader { bool M(string k) => Registry.Map.ContainsKey(k); }"

    let files other =
        [ "Registry.cs", registry "internal"; "Other.cs", other ]

    Assert.Empty(suggestInProject false false "CR0150" (files writer))
    Assert.Empty(suggestInProject false false "CR0150" (files walker))
    Assert.Equal(1, (suggestInProject false false "CR0150" (files reader)).Length)
    // a public field of a library under --api-changes: only the oracle sees every caller
    let exported = [ "Registry.cs", registry "public"; "Other.cs", reader ]
    Assert.Empty(suggestInProject false true "CR0150" exported)
    Assert.Equal(1, (suggestInProject true true "CR0150" exported).Length)
    Assert.Empty(suggestInProject true true "CR0150" [ "Registry.cs", registry "public"; "Other.cs", writer ])

[<Fact>]
let ``CR0152 keeps the object gate a Monitor call uses in another part of the partial type`` () =
    let part =
        "partial class C\n{\n    private readonly object _gate = new();\n    void E() { lock (_gate) { } }\n}\n"

    let locking = "partial class C { void F() { lock (_gate) { } } }"

    let monitoring =
        "using System.Threading;\npartial class C { void F() { Monitor.Enter(_gate); Monitor.Exit(_gate); } }"

    Assert.Equal(1, (suggestInProject false false "CR0152" [ "A.cs", part; "B.cs", locking ]).Length)
    Assert.Empty(suggestInProject false false "CR0152" [ "A.cs", part; "B.cs", monitoring ])

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

[<Fact>]
let ``CR0154 keeps a null test through a user-defined inequality`` () =
    let source =
        """
class Node
{
    public int Count { get; set; }
    public static bool operator ==(Node? a, Node? b) => ReferenceEquals(a, b) || (a is not null && b is not null && a.Count == -1);
    public static bool operator !=(Node? a, Node? b) => !(a == b);
    public override bool Equals(object? o) => ReferenceEquals(this, o);
    public override int GetHashCode() => 0;
    void A(Node? other, int v) { if (other != null) other.Count = v; }
    void B(Node? other, int v) { if (other is not null) other.Count = v; }
}
"""

    Assert.Equal<string list>([ "other is not null" ], firedText source (suggestCode "CR0154" source))

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

/// CR0151 over a two-file compilation: `H` declares the params methods, the
/// other file uses them; `oracle` runs the rules as the tool and the fix
/// provider do, with the solution's reference finder.
let private paramsSpanAcrossFiles (oracle: bool) =
    let declaring =
        """
using System;
static class H
{
    internal static int Sum(params int[] xs) { var t = 0; foreach (var x in xs) t += x; return t; }
    internal static bool In(int v, params int[] xs) { foreach (var x in xs) if (x == v) return true; return false; }
    internal static int Ok(params int[] xs) => xs.Length;
    public static int Open(params int[] xs) => xs.Length;
}
"""

    let using' =
        """
using System;
using System.Linq.Expressions;
static class U
{
    static Func<int[], int> f = H.Sum;
    static Expression<Func<int, bool>> e = x => H.In(x, 1, 2);
    static int g = H.Ok(1, 2) + H.Open(3);
}
"""

    use workspace = new Microsoft.CodeAnalysis.AdhocWorkspace()

    let project =
        workspace
            .AddProject("Test", Microsoft.CodeAnalysis.LanguageNames.CSharp)
            .WithCompilationOptions(
                Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(
                    Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary
                )
            )
            .WithParseOptions(parseOptions)
            .AddMetadataReferences
            metadataReferences

    let d = project.AddDocument("H.cs", normalize declaring, filePath = "C:/fake/H.cs")
    let u = d.Project.AddDocument("U.cs", normalize using', filePath = "C:/fake/U.cs")
    let d = u.Project.GetDocument d.Id
    let compilation = d.Project.GetCompilationAsync().Result
    Assert.Empty(errorsAfterFix compilation)
    let tree = d.GetSyntaxTreeAsync().Result
    let model = compilation.GetSemanticModel(tree, false)

    let ctx =
        { CSharp.Refactor.Roslyn.Context.forTree None compilation tree false with
            References =
                if oracle then
                    Some(CSharp.Refactor.Roslyn.References.oracle d.Project.Solution tree)
                else
                    None
        }

    let text = tree.GetText().ToString()

    CSharp.Refactor.Roslyn.Rules.all tree model ctx
    |> List.filter (fun s -> s.Code = "CR0151")
    |> List.map (fun s -> text.Substring(s.Span.Start, s.Span.Length))

[<Fact>]
let ``a params array referenced from another file as a method group or inside an expression tree keeps its type`` () =
    // the compiler's view: no reference oracle, the compilation's own trees are read
    Assert.Equal<string list>([ "params int[] xs"; "params int[] xs" ], paramsSpanAcrossFiles false)
    // the tool's and the fix provider's view: the solution's reference finder
    Assert.Equal<string list>([ "params int[] xs"; "params int[] xs" ], paramsSpanAcrossFiles true)

// ---- CR0155 ----

[<Fact>]
let ``CR0155 holds a class with an attributed method or a ref receiver`` () =
    let source =
        """
using System;
static class Plain { public static int Twice(this int x) => x * 2; }
static class ByRef { public static void Bump(this ref int x) { x++; } }
static class Marked { [Obsolete] public static int Half(this int x) => x / 2; }
"""

    Assert.Equal<string list>([ "Plain" ], firedText source (suggestCode "CR0155" source))
