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
