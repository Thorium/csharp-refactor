module CSharp.Refactor.Tests.NullableMatchTests

open Xunit
open CSharp.Refactor.Tests.Harness

[<Literal>]
let private code = "CR0004"

[<Fact>]
let ``the conditional, statement and chain shapes become patterns`` () =
    let source =
        """
using System;
class C
{
    int A(int? x) => x.HasValue ? x.Value + 1 : 0;
    int B(int? x) => !x.HasValue ? 0 : x.Value * 2;
    int D(int? x) => x == null ? -1 : x.Value + x.Value;
    void E(int? x)
    {
        if (x.HasValue)
        {
            Console.WriteLine(x.Value);
        }
        else
        {
            Console.WriteLine("none");
        }
    }
    bool F(int? x) => x.HasValue && x.Value > 3 && x.Value < 9;
    bool G(int? x) => !x.HasValue || x.Value > 3;
    void H(int? x) { if (x != null) Console.WriteLine(x.Value); }
}
"""

    let fired = suggestCode code source
    Assert.Equal(7, fired.Length)
    let fixedSource = fixAll code source
    Assert.Contains("int A(int? x) => x is { } v ? v + 1 : 0;", fixedSource)
    Assert.Contains("int B(int? x) => x is { } v ? v * 2 : 0;", fixedSource)
    Assert.Contains("int D(int? x) => x is { } v ? v + v : -1;", fixedSource)

    Assert.Contains(
        "if (x is { } v)\n        {\n            Console.WriteLine(v);\n        }\n        else\n        {\n            Console.WriteLine(\"none\");\n        }",
        fixedSource
    )

    Assert.Contains("bool F(int? x) => x is { } v && v > 3 && v < 9;", fixedSource)
    Assert.Contains("bool G(int? x) => x is not { } v || v > 3;", fixedSource)
    Assert.Contains("void H(int? x) { if (x is { } v) Console.WriteLine(v); }", fixedSource)

[<Fact>]
let ``a pass-through payload, a Value read in the other branch, a taken binder, an assignment or a custom HasValue stand down``
    ()
    =
    let source =
        """
using System;
struct Maybe { public bool HasValue => true; public int Value => 1; }
class C
{
    int A(int? x) => x.HasValue ? x.Value : 0;
    int B(int? x) => x.HasValue ? x.Value + 1 : x.Value;
    int D(int? x) { int v = 1; return x.HasValue ? x.Value + v : 0; }
    int E(int? x) { if (x.HasValue) { x = null; return x.Value; } return 0; }
    int F(Maybe m) => m.HasValue ? m.Value + 1 : 0;
    int G(int? x) => x.HasValue ? 1 : 0;
}
"""

    let fired = suggestCode code source |> firedText source
    // D still fires: the binder simply avoids the taken name
    Assert.Equal<string list>([ "x.HasValue" ], fired)
    Assert.Contains("return x is { } xValue ? xValue + v : 0;", fixAll code source)

[<Fact>]
let ``two sites in one member take distinct binders, a member receiver naming its own`` () =
    let source =
        """
class Bound { public int? Min; public int? Max; }
class C
{
    bool Outside(Bound bound, int amount)
    {
        var below = bound.Min.HasValue && amount < bound.Min.Value;
        var above = bound.Max.HasValue && amount > bound.Max.Value;
        var again = bound.Min.HasValue && amount == bound.Min.Value;
        return below || above || again;
    }
}
"""

    let fixedSource = fixAll code source
    Assert.Contains("var below = bound.Min is { } min && amount < min;", fixedSource)
    Assert.Contains("var above = bound.Max is { } max && amount > max;", fixedSource)
    Assert.Contains("var again = bound.Min is { } minValue && amount == minValue;", fixedSource)

[<Fact>]
let ``an alias line names the binder and goes, and a conditional keeps its layout`` () =
    let source =
        """
using System;
class Request { public decimal? Threshold; }
class C
{
    void A(Request request)
    {
        if (request.Threshold.HasValue)
        {
            var threshold = request.Threshold.Value;
            Console.WriteLine(threshold + request.Threshold.Value);
        }
    }
    string B(int? id)
    {
        var text = id.HasValue
            ? Lookup(id.Value)
            : null;
        return text;
    }
    string Lookup(int id) => id.ToString();
}
"""

    let fixedSource = fixAll code source

    Assert.Contains(
        normalize
            """        if (request.Threshold is { } threshold)
        {
            Console.WriteLine(threshold + threshold);
        }""",
        fixedSource
    )

    Assert.Contains(
        normalize
            """        var text = id is { } v
            ? Lookup(v)
            : null;""",
        fixedSource
    )

[<Fact>]
let ``an all-caps member lowercases whole and skips the keyword`` () =
    let source =
        """
class Attr { public bool? BOOL; }
class C
{
    bool A(Attr a) => a.BOOL.HasValue && a.BOOL.Value;
}
"""

    Assert.Contains("a.BOOL is { } boolValue && boolValue", fixAll code source)

[<Fact>]
let ``a lambda parameter named like the receiver is another thing`` () =
    let source =
        """
using System.Linq;
using System.Collections.Generic;
class C
{
    int A(int? x, List<int?> items) => x.HasValue ? items.Sum(x => x.Value) + x.Value : 0;
}
"""

    Assert.Contains("x is { } v ? items.Sum(x => x.Value) + v : 0", fixAll code source)
