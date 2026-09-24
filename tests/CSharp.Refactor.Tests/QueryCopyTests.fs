module CSharp.Refactor.Tests.QueryCopyTests

open Xunit
open CSharp.Refactor.Tests.Harness

// ---- CR0178 ----

let private prelude =
    """
using System;
using System.Collections.Generic;
using System.Linq;
class Order
{
    public int Id { get; set; }
    public int State { get; set; }
    public bool Open { get; set; }
    public decimal Total { get; set; }
    public string Name { get; set; } = "";
    public int? Parent { get; set; }
    public int Computed => Id * 2;
}
"""

[<Fact>]
let ``a copy of a query before trivial Where and Select moves after them`` () =
    let source =
        prelude
        + """
class C
{
    IQueryable<Order> Orders => new List<Order>().AsQueryable();
    List<int> A() => Orders.ToList().Where(o => o.Id > 0 || o.State == 0).Select(o => o.Id).ToList();
    IEnumerable<int> B() => Orders.ToList().Where(o => o.Open && !(o.State != 2)).Select(o => o.Id);
    object D() => Orders.ToArray().Select(o => new { o.Id, S = o.State });
    IEnumerable<Order> E(int min) => Orders.ToList().Where(o => o.Id > min);
    IEnumerable<Order> F() => Orders.ToList().Where(o => o.Name != null && o.Parent == null);
    IEnumerable<int> G() => Orders.ToList().Where(o => o.Id > 0).Select(o => o.Id * 2);
    IEnumerable<Order> H() => Orders.AsEnumerable().Where(o => o.State == 1);
}
"""

    let fixedSource = fixAll "CR0178" source
    Assert.Contains("Orders.Where(o => o.Id > 0 || o.State == 0).Select(o => o.Id).ToList();", fixedSource)
    Assert.Contains("Orders.Where(o => o.Open && !(o.State != 2)).Select(o => o.Id).ToList();", fixedSource)
    Assert.Contains("Orders.Select(o => new { o.Id, S = o.State }).ToArray();", fixedSource)
    Assert.Contains("Orders.Where(o => o.Id > min).ToList();", fixedSource)
    Assert.Contains("Orders.Where(o => o.Name != null && o.Parent == null).ToList();", fixedSource)
    // the arithmetic Select stays in memory; the Where before it moves
    Assert.Contains("Orders.Where(o => o.Id > 0).ToList().Select(o => o.Id * 2);", fixedSource)
    Assert.Contains("Orders.Where(o => o.State == 1).AsEnumerable();", fixedSource)

[<Fact>]
let ``a stage a provider might not translate, or translates differently, stays in memory`` () =
    let source =
        prelude
        + """
class C
{
    IQueryable<Order> Orders => new List<Order>().AsQueryable();
    IEnumerable<Order> A() => Orders.ToList().Where(o => o.Computed > 0);
    IEnumerable<Order> B() => Orders.ToList().Where(o => o.Name.StartsWith("a"));
    IEnumerable<Order> D() => Orders.ToList().Where(o => o.Id % 2 == 0);
    IEnumerable<Order> E() { var min = 0; var r = Orders.ToList().Where(o => o.Id > min); min = 5; return r; }
    IEnumerable<int> F(List<Order> xs) => xs.ToList().Where(o => o.Id > 0).Select(o => o.Id);
    IEnumerable<Order> G() => Orders.ToList().Where((o, i) => i > 0);
    IEnumerable<(int, int)> H() => Orders.ToList().Select(o => (o.Id, o.State));
}
"""

    Assert.Empty(suggestCode "CR0178" source)

[<Fact>]
let ``where List<T> would bind differently from IEnumerable<T>, the move is the editor's offer`` () =
    let source =
        prelude
        + """
static class Print
{
    public static string Show(IEnumerable<int> xs) => "seq";
    public static string Show(List<int> xs) => "list";
    public static T Same<T>(T x) => x;
}
class C
{
    IQueryable<Order> Orders => new List<Order>().AsQueryable();
    string A() => Print.Show(Orders.ToList().Where(o => o.Id > 0).Select(o => o.Id));
    object B() => Print.Same(Orders.ToList().Where(o => o.Id > 0));
    void D() { var r = Orders.ToList().Where(o => o.Id > 0); r = Enumerable.Empty<Order>(); }
    IEnumerable<Order> E() => Orders.ToList().Where(o => o.Id > 0).Reverse();
    Func<IEnumerable<Order>> F() => () => Orders.ToList().Where(o => o.Id > 0);
    int G() { var r = Orders.ToList().Where(o => o.Id > 0); foreach (var o in r) { } return r.Count(); }
}
"""

    let fired = suggestCode "CR0178" source
    let fixedSource = fixAll "CR0178" source
    // D's reassignment and E's Reverse() would not compile over a List:
    // the speculative check drops those two outright
    Assert.Equal(4, fired.Length)
    Assert.Contains("Print.Show(Orders.ToList().Where(o => o.Id > 0).Select(o => o.Id));", fixedSource)
    Assert.Contains("Print.Same(Orders.ToList().Where(o => o.Id > 0));", fixedSource)
    Assert.Contains("var r = Orders.ToList().Where(o => o.Id > 0); r = ", fixedSource)
    Assert.Contains("Orders.ToList().Where(o => o.Id > 0).Reverse();", fixedSource)
    Assert.Contains("() => Orders.ToList().Where(o => o.Id > 0);", fixedSource)
    // a var local read only by a foreach and an Enumerable call stays stable
    Assert.Contains("var r = Orders.Where(o => o.Id > 0).ToList(); foreach", fixedSource)

[<Fact>]
let ``a navigation property is no column: null without Include in memory, a join in the query`` () =
    let source =
        prelude.Replace(
            "public int Computed => Id * 2;",
            "public int Computed => Id * 2;\n    public Order? Previous { get; set; }"
        )
        + """
class C
{
    IQueryable<Order> Orders => new List<Order>().AsQueryable();
    IEnumerable<Order> A() => Orders.ToList().Where(o => o.Previous == null);
    IEnumerable<Order?> B() => Orders.ToList().Select(o => o.Previous);
}
"""

    Assert.Empty(suggestCode "CR0178" source)

[<Fact>]
let ``a string, decimal or nullable comparison is the editor's offer, never the sweep's`` () =
    let source =
        prelude
        + """
class C
{
    IQueryable<Order> Orders => new List<Order>().AsQueryable();
    IEnumerable<Order> A() => Orders.ToList().Where(o => o.Name == "a");
    IEnumerable<Order> B() => Orders.ToList().Where(o => o.Total > 0.005m);
    IEnumerable<Order> D() => Orders.ToList().Where(o => o.Parent != 3);
}
"""

    let fired = suggestCode "CR0178" source
    Assert.Equal(3, fired.Length)

    for s in fired do
        Assert.All(s.Fixes, (fun f -> Assert.True f.EditorOnly))
        Assert.Contains("collation", s.Message)

    Assert.Equal(normalize source, normalize (fixAll "CR0178" source))

[<Fact>]
let ``the child orders of one parent are filtered in the query: a nullable id compared with a value drops the NULL row in both``
    ()
    =
    let source =
        prelude
        + """
class C
{
    IQueryable<Order> Orders => new List<Order>().AsQueryable();
    IEnumerable<Order> A(int parentId) => Orders.ToList().Where(o => o.Parent == parentId);
    IEnumerable<Order> B() => Orders.ToList().Where(o => o.Parent >= 3 && o.Open);
    IEnumerable<Order> D() => Orders.ToList().Where(o => !(o.Parent > 3));
    IEnumerable<Order> E(int? parentId) => Orders.ToList().Where(o => o.Parent == parentId);
    IEnumerable<Order> F() => Orders.ToList().Where(o => o.Parent != 3);
    IEnumerable<Order> G() => Orders.ToList().Where(o => o.Parent == o.Id);
}
"""

    let fixedSource = fixAll "CR0178" source
    Assert.Contains("Orders.Where(o => o.Parent == parentId).ToList();", fixedSource)
    Assert.Contains("Orders.Where(o => o.Parent >= 3 && o.Open).ToList();", fixedSource)
    // under `!` the NULL row is C#'s true and SQL's unknown; a nullable value may be null;
    // `!=` keeps the NULL row in C#; a column on both sides may be NULL on both
    Assert.Contains("Orders.ToList().Where(o => !(o.Parent > 3));", fixedSource)
    Assert.Contains("Orders.ToList().Where(o => o.Parent == parentId);", fixedSource)
    Assert.Contains("Orders.ToList().Where(o => o.Parent != 3);", fixedSource)
    Assert.Contains("Orders.ToList().Where(o => o.Parent == o.Id);", fixedSource)
