module CSharp.Refactor.Tests.LoopToLinqTests

open Xunit
open CSharp.Refactor.Tests.Harness

// ---- CR0020 ----

[<Fact>]
let ``a copy before a consumer or a foreach goes, before a lazy stage it moves after; the snapshot idiom stays`` () =
    let source =
        """
using System;
using System.Collections.Generic;
using System.Linq;
class C
{
    bool A(HashSet<int> xs) => xs.ToList().Any(x => x > 1);
    void B(IEnumerable<int> xs) { foreach (var x in xs.ToList()) Console.WriteLine(x); }
    IEnumerable<int> D(IEnumerable<int> xs) => xs.ToList().Where(x => x > 1);
    void E(List<int> list) { foreach (var x in list.ToList()) list.Remove(x); }
    void F(IEnumerable<int> xs, List<int> other) { foreach (var x in xs.ToList()) other.Add(x); }
    int G(List<int> list) => list.ToArray().Count();
    void H(IEnumerable<int> xs) { var copy = xs; foreach (var x in xs.ToList()) Console.WriteLine(copy.Count()); }
    int I(IEnumerable<int> xs) => xs.ToList().Count(x => Console.Read() > x);
    void J(IQueryable<int> q) { foreach (var x in q.ToList()) Console.WriteLine(x); }
}
"""

    let fired = suggestCode "CR0020" source
    Assert.Equal(3, fired.Length)
    let fixedSource = fixAll "CR0020" source
    Assert.Contains("bool A(HashSet<int> xs) => xs.Any(x => x > 1);", fixedSource)
    Assert.Contains("void B(IEnumerable<int> xs) { foreach (var x in xs) Console.WriteLine(x); }", fixedSource)
    Assert.Contains("IEnumerable<int> D(IEnumerable<int> xs) => xs.Where(x => x > 1).ToList();", fixedSource)
    // a query's copy ran the query: the loop would run over an open reader
    Assert.Contains("foreach (var x in q.ToList()) Console.WriteLine(x);", fixedSource)

[<Fact>]
let ``CR0020 keeps the copy before a short-circuiting consumer over a generator`` () =
    let source =
        """
using System;
using System.Collections.Generic;
using System.Linq;
class C
{
    IEnumerable<int> Numbers() { for (var i = 0; i < 5; i++) { Console.WriteLine(i); yield return i; } }
    bool A() { var seq = Numbers(); return seq.ToList().Any(p => p > 1); }
    bool B(IEnumerable<int> xs) => xs.ToList().Any(p => p > 1);
    bool D(int[] xs) { var view = xs.Where(x => x > 0); return view.ToList().Any(p => p > 1); }
}
"""

    // A's generator prints all five before; B's may be one; D's is a pure view of an array
    Assert.Equal<string list>([ ".ToList()" ], firedText source (suggestCode "CR0020" source))
    Assert.Contains("return view.Any(p => p > 1);", fixAll "CR0020" source)

// ---- CR0029 ----

[<Fact>]
let ``two Selects fuse and an identity Select goes, a duplicated impure stage or a captured name stays`` () =
    let source =
        """
using System;
using System.Collections.Generic;
using System.Linq;
class P { public readonly P Parent; public readonly string Name; }
class C
{
    IEnumerable<string> A(IEnumerable<P> ps) => ps.Select(p => p.Parent).Select(q => q.Name);
    IEnumerable<int> B(IEnumerable<P> ps) => ps.Select(p => p.Name.Length).Select(n => n * n);
    IEnumerable<int> D(IEnumerable<P> ps) => ps.Select(p => Console.Read()).Select(n => n * n);
    IEnumerable<int> E(IEnumerable<int> xs) => xs.Select(x => x);
    List<int> F(List<int> xs) => xs.Select(x => x).ToList();
    IEnumerable<int> G(IEnumerable<int> xs, int q) => xs.Select(x => x + 1).Select(y => y * q);
    IEnumerable<int> H(IEnumerable<int> xs, int x) => xs.Select(y => y + 1).Select(z => z + x);
}
"""

    let fired = suggestCode "CR0029" source
    Assert.Equal(5, fired.Length)
    let fixedSource = fixAll "CR0029" source
    Assert.Contains("ps.Select(p => p.Parent.Name);", fixedSource)
    Assert.Contains("ps.Select(p => p.Name.Length * p.Name.Length);", fixedSource)
    Assert.Contains("ps.Select(p => Console.Read()).Select(n => n * n);", fixedSource)
    Assert.Contains("IEnumerable<int> E(IEnumerable<int> xs) => xs;", fixedSource)
    Assert.Contains("xs.Select(x => x).ToList()", fixedSource)
    Assert.Contains("xs.Select(x => (x + 1) * q);", fixedSource)
    Assert.Contains("xs.Select(y => (y + 1) + x);", fixedSource)

// ---- CR0021 ----

[<Fact>]
let ``sums, counts and concatenations fold, an unchecked integral sum and a mid-loop read stay`` () =
    let source =
        """
using System;
using System.Collections.Generic;
class Item { public decimal Price; public string Name; public bool Ok; }
class C
{
    decimal A(List<Item> items) { var total = 0m; foreach (var i in items) total += i.Price; return total; }
    int B(Item[] items) { int n = 0; foreach (var i in items) if (i.Ok) n++; Console.WriteLine(n); return n; }
    string D(IEnumerable<Item> items) { var s = ""; foreach (var i in items) s += i.Name; return s + "!"; }
    string E(IEnumerable<string> names) { string s = string.Empty; foreach (var n in names) s += n; return s; }
    int F(IEnumerable<int> xs) { var total = 0; foreach (var x in xs) total += x; return total; }
    int G(int[] xs) { checked { var total = 0; foreach (var x in xs) total += x; return total; } }
    decimal H(IEnumerable<Item> items) { var total = 0m; foreach (var i in items) { total += i.Price; Console.WriteLine(total); } return total; }
    double I(IEnumerable<int> xs) { var total = 0.0; foreach (var x in xs) total += x; return total; }
    // a lazy source: Sum measured 4.7× slower than the loop, so it stays
    int J(IEnumerable<int> xs) { checked { var total = 0; foreach (var x in xs) total += x; return total; } }
}
"""

    let fired = suggestCode "CR0021" source
    Assert.Equal(5, fired.Length)
    let fixedSource = fixAll "CR0021" source
    Assert.Contains("decimal A(List<Item> items) { return items.Sum(i => i.Price); }", fixedSource)

    Assert.Contains(
        "int B(Item[] items) { int n = items.Count(i => i.Ok); Console.WriteLine(n); return n; }",
        fixedSource
    )

    Assert.Contains(
        "string D(IEnumerable<Item> items) { var s = string.Concat(items.Select(i => i.Name)); return s + \"!\"; }",
        fixedSource
    )

    Assert.Contains("string E(IEnumerable<string> names) { return string.Concat(names); }", fixedSource)

    Assert.Contains(
        "int F(IEnumerable<int> xs) { var total = 0; foreach (var x in xs) total += x; return total; }",
        fixedSource
    )

    Assert.Contains("int G(int[] xs) { checked { return xs.Sum(); } }", fixedSource)

    Assert.Contains(
        "int J(IEnumerable<int> xs) { checked { var total = 0; foreach (var x in xs) total += x; return total; } }",
        fixedSource
    )

    Assert.Contains("using System.Linq;", fixedSource)

// ---- CR0022 ----

[<Fact>]
let ``a flag set or cleared by a loop is Any or All, an impure predicate or a mid-loop read stays`` () =
    let source =
        """
using System;
using System.Collections.Generic;
using System.Linq;
class C
{
    bool A(IEnumerable<int> xs) { bool found = false; foreach (var x in xs) if (x > 3) found = true; return found; }
    bool B(IEnumerable<int> xs) { bool ok = true; foreach (var x in xs) { if (x < 0) { ok = false; break; } } return ok; }
    bool D(IEnumerable<int> xs) { bool found = false; foreach (var x in xs) if (Console.Read() == x) found = true; return found; }
    bool E(IEnumerable<int> xs) { bool found = false; foreach (var x in xs) { if (x > 3) found = true; Console.WriteLine(found); } return found; }
    bool F(IEnumerable<string> xs) { var any = false; foreach (var s in xs) if (string.IsNullOrEmpty(s)) any = true; return any; }
}
"""

    let fired = suggestCode "CR0022" source
    Assert.Equal(3, fired.Length)
    let fixedSource = fixAll "CR0022" source
    Assert.Contains("bool A(IEnumerable<int> xs) { bool found = xs.Any(x => x > 3); return found; }", fixedSource)
    Assert.Contains("bool B(IEnumerable<int> xs) { bool ok = xs.All(x => x >= 0); return ok; }", fixedSource)

    Assert.Contains(
        "bool F(IEnumerable<string> xs) { var any = xs.Any(s => string.IsNullOrEmpty(s)); return any; }",
        fixedSource
    )

[<Fact>]
let ``CR0022 without a break needs a total condition over an eager source and the BCL's Any`` () =
    let source =
        """
using System;
using System.Collections.Generic;
using System.Linq;
class Item { public int Id { get; set; } }
class C
{
    bool A(IEnumerable<int> xs) { bool found = false; foreach (var x in xs) if (x > 3) { found = true; break; } return found; }
    bool B(List<Item> items, int id) { bool found = false; foreach (var x in items) if (x.Id == id) found = true; return found; }
    bool D(string[] xs) { var any = false; foreach (var s in xs) if (string.IsNullOrEmpty(s)) any = true; return any; }
    bool E(int[] xs) { bool ok = true; foreach (var x in xs) if (x < 0) ok = false; return ok; }
    bool H1(int?[] xs) { bool found = false; foreach (var x in xs) { if (x.Value > 0) found = true; } return found; }
    bool H2(List<Item?> items, int id) { bool found = false; foreach (var x in items) if (x!.Id == id) found = true; return found; }
    static IEnumerable<int> Gen() { yield return 1; }
    bool H3() { bool found = false; foreach (var x in Gen()) if (x > 0) found = true; return found; }
}
"""

    let fired = suggestCode "CR0022" source
    // H1 (`x.Value`) and H3 (a user iterator walked to its end) are notes; H2's `x!` never fired
    Assert.Equal(6, fired.Length)
    Assert.Equal(4, fired |> List.filter (fun s -> not s.Fixes.IsEmpty) |> List.length)
    let fixedSource = fixAll "CR0022" source
    Assert.Contains("bool found = xs.Any(x => x > 3); return found;", fixedSource)
    Assert.Contains("bool found = items.Any(x => x.Id == id); return found;", fixedSource)
    Assert.Contains("var any = xs.Any(s => string.IsNullOrEmpty(s)); return any;", fixedSource)
    Assert.Contains("bool ok = xs.All(x => x >= 0); return ok;", fixedSource)
    Assert.Contains("foreach (var x in xs) { if (x.Value > 0) found = true; } return found;", fixedSource)
    Assert.Contains("foreach (var x in items) if (x!.Id == id) found = true; return found;", fixedSource)

[<Fact>]
let ``CR0022 without a break takes a string comparison, not a user collection or a queryable over an iterator`` () =
    let source =
        """
using System;
using System.Collections.Generic;
using System.Linq;
public sealed class Bag : IEnumerable<string>
{
    public IEnumerator<string> GetEnumerator() { yield return "a"; }
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
class C
{
    static IEnumerable<int> Gen() { yield return 1; }
    bool F03(List<string> names) { bool found = false; foreach (var n in names) if (n.StartsWith("A", StringComparison.Ordinal)) found = true; return found; }
    bool F06(Bag bag) { bool found = false; foreach (var n in bag) if (n.Length > 0) found = true; return found; }
    bool F07() { bool found = false; foreach (var x in Gen().AsQueryable()) if (x > 0) found = true; return found; }
}
"""

    let fired = suggestCode "CR0022" source
    Assert.Equal(3, fired.Length)
    Assert.Equal(1, fired |> List.filter (fun s -> not s.Fixes.IsEmpty) |> List.length)

    Assert.Contains(
        """bool found = names.Any(n => n.StartsWith("A", StringComparison.Ordinal)); return found;""",
        fixAll "CR0022" source
    )

[<Fact>]
let ``CR0022 without a break takes a StartsWith of a variable: a null element argument is the accepted residual`` () =
    let source =
        """
using System.Collections.Generic;
public sealed class P { public string Name { get; set; } = ""; public string? Sub { get; set; } }
class C
{
    bool T02(List<P> xs) { bool found = false; foreach (var p in xs) { if (p.Name.StartsWith(p.Sub!)) found = true; } return found; }
    bool Kept(List<P> xs) { bool found = false; foreach (var p in xs) { if (p.Name.StartsWith("a")) found = true; } return found; }
}
"""

    let fired = suggestCode "CR0022" source
    Assert.Equal(2, fired.Length)
    Assert.All(fired, fun s -> Assert.NotEmpty s.Fixes)
    let fixedSource = fixAll "CR0022" source
    Assert.Contains("""bool found = xs.Any(p => p.Name.StartsWith(p.Sub!)); return found;""", fixedSource)
    Assert.Contains("""bool found = xs.Any(p => p.Name.StartsWith("a")); return found;""", fixedSource)

[<Fact>]
let ``CR0022 and CR0028 are notes where a repository extension would take the call they spell`` () =
    let source =
        """
using System;
using System.Collections.Generic;
using System.Linq;
static class MyExt
{
    public static bool Any<T>(this T[] xs, Func<T, bool> p) => false;
    public static IEnumerable<T> Where<T>(this T[] xs, Func<T, bool> p) => xs;
}
class C
{
    bool A(int[] xs) { bool found = false; foreach (var x in xs) { if (x > 1) found = true; } return found; }
    List<int> B(int[] xs) { var r = new List<int>(); foreach (var x in xs) if (x > 1) r.Add(x); return r; }
    List<int> D(List<int> xs) { var r = new List<int>(); foreach (var x in xs) if (x > 1) r.Add(x); return r; }
}
"""

    let flags = suggestCode "CR0022" source
    Assert.Equal(1, flags.Length)
    Assert.Empty flags.[0].Fixes
    let fills = suggestCode "CR0028" source
    Assert.Equal(2, fills.Length)
    Assert.Equal(1, fills |> List.filter (fun s -> not s.Fixes.IsEmpty) |> List.length)
    Assert.Contains("var r = xs.Where(x => x > 1).ToList(); return r;", fixAll "CR0028" source)

[<Fact>]
let ``CR0022 without a break follows the source to its visible origin and a Select's lambda to a user method's effects``
    ()
    =
    let source =
        """
using System;
using System.Collections.Generic;
using System.Linq;
public sealed class Holder
{
    readonly IEnumerable<int> _xs;
    public Holder() { _xs = Gen(); }
    static IEnumerable<int> Gen() { yield return 1; yield return 2; }
    public bool Y12() { bool found = false; foreach (var x in _xs) if (x > 0) found = true; return found; }
}
class C
{
    static int calls;
    static readonly List<object> objs = new() { 1, "two" };
    static IEnumerable<int> Gen() { yield return 1; yield return 2; }
    static IEnumerable<int> Items => Gen();
    static IEnumerable<int> Casted => objs.Cast<int>();
    static int Log(int x) { calls++; return x; }
    bool Y01() { bool found = false; foreach (var x in Items) if (x > 0) found = true; return found; }
    bool Y02() { var xs = Gen(); bool found = false; foreach (var x in xs) if (x > 0) found = true; return found; }
    bool Y03() { bool found = false; foreach (var x in Casted) if (x > 0) found = true; return found; }
    bool Y10(List<int> items) { bool found = false; foreach (var x in items.Select(i => Log(i))) if (x > 0) found = true; return found; }
    bool Y08(List<string> items) { bool found = false; foreach (var x in items) if (x.StartsWith("a")) found = true; return found; }
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
    let fired = suggestions |> List.filter (fun s -> s.Code = "CR0022")
    Assert.Equal(6, fired.Length)
    // the field a constructor fills with an iterator, the property and the
    // local holding one, `Cast<T>()` behind a getter, and `Log` counting its
    // calls under `Select`: notes; Y08 over a list takes the fix
    Assert.Equal(1, fired |> List.filter (fun s -> not s.Fixes.IsEmpty) |> List.length)
    let fixedSource = fixAll "CR0022" source
    Assert.Contains("""bool found = items.Any(x => x.StartsWith("a")); return found;""", fixedSource)
    Assert.Contains("foreach (var x in _xs) if (x > 0) found = true; return found;", fixedSource)
    Assert.Contains("foreach (var x in items.Select(i => Log(i))) if (x > 0) found = true; return found;", fixedSource)

[<Fact>]
let ``CR0022 without a break takes a materialised copy of an iterator, and sees an iterator getter and an explicit GetEnumerator``
    ()
    =
    let source =
        """
using System;
using System.Collections.Generic;
using System.Linq;
public sealed class Col : IEnumerable<int>
{
    public int Calls;
    IEnumerator<int> IEnumerable<int>.GetEnumerator() { Calls++; yield return 1; Calls++; yield return 2; }
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => ((IEnumerable<int>)this).GetEnumerator();
}
class C
{
    static int calls;
    static IEnumerable<int> Gen() { calls++; yield return 1; calls++; yield return 2; }
    static IEnumerable<int> Lazy { get { calls++; yield return 1; calls++; yield return 2; } }
    bool Y20() { var arr = Gen().ToArray(); bool found = false; foreach (var x in arr) if (x > 1) found = true; return found; }
    bool Y21() { bool found = false; foreach (var x in Lazy) if (x > 0) found = true; return found; }
    bool Y22() { bool found = false; foreach (var x in new Col()) if (x > 0) found = true; return found; }
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
    let fired = suggestions |> List.filter (fun s -> s.Code = "CR0022")
    Assert.Equal(3, fired.Length)
    // `ToArray()` walked the iterator before the loop; a getter that yields
    // and an explicit `IEnumerable<T>.GetEnumerator()` that yields run code
    // per element
    Assert.Equal(1, fired |> List.filter (fun s -> not s.Fixes.IsEmpty) |> List.length)
    let fixedSource = fixAll "CR0022" source
    Assert.Contains("var arr = Gen().ToArray(); bool found = arr.Any(x => x > 1); return found;", fixedSource)
    Assert.Contains("foreach (var x in Lazy) if (x > 0) found = true; return found;", fixedSource)
    Assert.Contains("foreach (var x in new Col()) if (x > 0) found = true; return found;", fixedSource)

// ---- CR0025 ----

[<Fact>]
let ``growing by one in a loop is noted, except the shape CR0021 rewrites and numeric increments`` () =
    let source =
        """
using System;
using System.Collections.Generic;
using System.Linq;
class C
{
    string A(string[] parts, int n) { var s = ""; for (int i = 0; i < n; i++) { s += parts[i]; s += ","; } return s; }
    int[] B(int[] arr, IEnumerable<int> xs) { foreach (var x in xs) arr = arr.Append(x).ToArray(); return arr; }
    void D(int[] arr, IEnumerable<int> xs) { foreach (var x in xs) Array.Resize(ref arr, arr.Length + 1); }
    int E(IEnumerable<int> xs) { int i = 0; foreach (var x in xs) i = i + 1; return i; }
    string F(IEnumerable<string> xs) { var s = ""; foreach (var x in xs) s += x; return s; }
}
"""

    let texts = firedText source (suggestCode "CR0025" source)

    Assert.Equal<string list>(
        [
            "s += parts[i]"
            "s += \",\""
            "arr = arr.Append(x).ToArray()"
            "Array.Resize(ref arr, arr.Length + 1)"
        ],
        texts
    )

[<Fact>]
let ``CR0025 leaves an audit trail grown with ImmutableList.Add alone: a persistent list shares structure`` () =
    // only ImmutableArray<T>.Add copies the whole array; ImmutableList<T>.Add is O(log n)
    let source =
        """
using System.Collections.Generic;
using System.Collections.Immutable;
class AuditTrail
{
    ImmutableList<string> Record(ImmutableList<string> trail, IEnumerable<string> events)
    {
        foreach (var e in events)
        {
            trail = trail.Add(e);
        }
        return trail;
    }
}
"""

    Assert.Empty(suggestCode "CR0025" source)

// ---- CR0023 ----

[<Fact>]
let ``a literal list probed per element becomes a set, other uses or a public field hold it to a note`` () =
    let source =
        """
using System;
using System.Collections.Generic;
using System.Linq;
class C
{
    static readonly string[] Allowed = { "a", "b", "c" };
    static readonly string[] Listed = new[] { "x", "y" };
    public static readonly int[] Public = { 1, 2 };
    IEnumerable<string> A(IEnumerable<string> xs) => xs.Where(x => Allowed.Contains(x));
    void B(IEnumerable<string> xs) { foreach (var x in xs) if (Listed.Contains(x)) Console.WriteLine(x); }
    string D() => Listed[0];
    bool E(IEnumerable<int> xs) => xs.Any(x => Public.Contains(x));
}
"""

    let fired = suggestCode "CR0023" source
    Assert.Equal(3, fired.Length)
    let fixedSource = fixAll "CR0023" source

    Assert.Contains(
        "static readonly FrozenSet<string> Allowed = new[] { \"a\", \"b\", \"c\" }.ToFrozenSet();",
        fixedSource
    )

    Assert.Contains("using System.Collections.Frozen;", fixedSource)
    Assert.Contains("static readonly string[] Listed = new[] { \"x\", \"y\" };", fixedSource)
    Assert.Contains("public static readonly int[] Public = { 1, 2 };", fixedSource)

// ---- CR0028 ----

[<Fact>]
let ``a fill loop that is then only read becomes the pipeline, a later mutation or a widening add stays`` () =
    let source =
        """
using System;
using System.Collections.Generic;
using System.Linq;
interface IShape { }
class Circle : IShape { public bool Ok; public string Name; }
class C
{
    List<string> A(IEnumerable<Circle> xs)
    {
        var names = new List<string>();
        foreach (var x in xs)
        {
            if (x.Ok) names.Add(x.Name);
        }
        return names;
    }
    List<Circle> B(IEnumerable<Circle> xs) { var r = new List<Circle>(); foreach (var x in xs) r.Add(x); r.Add(null); return r; }
    List<IShape> D(IEnumerable<Circle> xs) { var r = new List<IShape>(); foreach (var x in xs) r.Add(x); return r; }
    int E(IEnumerable<Circle> xs) { List<Circle> r = []; foreach (var x in xs) if (x.Ok) r.Add(x); return r.Count; }
}
"""

    let fired = suggestCode "CR0028" source
    Assert.Equal(2, fired.Length)
    let fixedSource = fixAll "CR0028" source
    Assert.Contains("var names = xs.Where(x => x.Ok).Select(x => x.Name).ToList();\n        return names;", fixedSource)
    Assert.Contains("List<Circle> r = xs.Where(x => x.Ok).ToList(); return r.Count;", fixedSource)
