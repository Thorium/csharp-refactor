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
    bool A(IEnumerable<int> xs) => xs.ToList().Any(x => x > 1);
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
    Assert.Contains("bool A(IEnumerable<int> xs) => xs.Any(x => x > 1);", fixedSource)
    Assert.Contains("void B(IEnumerable<int> xs) { foreach (var x in xs) Console.WriteLine(x); }", fixedSource)
    Assert.Contains("IEnumerable<int> D(IEnumerable<int> xs) => xs.Where(x => x > 1).ToList();", fixedSource)
    // a query's copy ran the query: the loop would run over an open reader
    Assert.Contains("foreach (var x in q.ToList()) Console.WriteLine(x);", fixedSource)

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
