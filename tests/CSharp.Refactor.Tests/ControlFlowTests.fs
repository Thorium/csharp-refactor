module CSharp.Refactor.Tests.ControlFlowTests

open Xunit
open CSharp.Refactor.Tests.Harness

// ---- CR0007 / CR0008 ----

[<Fact>]
let ``identity literals drop, the value-changing ones stay, dynamic stands down`` () =
    let source =
        """
class C
{
    bool M(bool x, bool y, dynamic d)
    {
        var a = x && true;
        var b = true && x;
        var c = x || false;
        var e = false || y;
        var f = x && false;
        var g = true || x;
        bool h = d && true;
        return a && b && c && e && f && g && h;
    }
}
"""

    let fired = suggestCode "CR0007" source |> firedText source
    Assert.Equal<string list>([ "x && true"; "true && x"; "x || false"; "false || y" ], fired)
    let fixedSource = fixAll "CR0007" source
    Assert.Contains("var a = x;", fixedSource)
    Assert.Contains("var e = y;", fixedSource)
    Assert.Contains("var f = x && false;", fixedSource)
    Assert.Contains("bool h = d && true;", fixedSource)

[<Fact>]
let ``a duplicated pure operand collapses; a call or a differing operand does not`` () =
    let source =
        """
class C
{
    bool Try() => true;
    bool M(bool x, int[] xs, string s)
    {
        var a = x || x;
        var b = xs.Length > 0 && xs.Length > 0;
        var c = Try() || Try();
        var e = s.Length > 1 && s.Length > 2;
        var f = !x && !x;
        return a && b && c && e && f;
    }
}
"""

    let fired = suggestCode "CR0008" source |> firedText source
    Assert.Equal<string list>([ "x || x"; "xs.Length > 0 && xs.Length > 0"; "!x && !x" ], fired)
    let fixedSource = fixAll "CR0008" source
    Assert.Contains("var a = x;", fixedSource)
    Assert.Contains("var b = xs.Length > 0;", fixedSource)
    Assert.Contains("var c = Try() || Try();", fixedSource)

// ---- CR0001 ----

[<Fact>]
let ``if-return-true-return-false becomes return condition, negated where needed`` () =
    let source =
        """
class C
{
    bool A(int x) { if (x > 0) return true; return false; }
    bool B(int x) { if (x > 0) { return false; } else { return true; } }
    bool D(int x) { if (x > 0 || x < -5) return false; return true; }
    bool E(bool p) { if (!p) return false; return true; }
    void F(int x, out bool r) { if (x > 0) r = true; else r = false; }
    bool G(int x) { if (x > 0) return true; return true; }
    bool H(int x) { if (x > 0) { return true; } else if (x < 0) { return false; } return false; }
}
"""

    let fired = suggestCode "CR0001" source
    Assert.Equal(5, fired.Length)
    let fixedSource = fixAll "CR0001" source
    Assert.Contains("bool A(int x) { return x > 0; }", fixedSource)
    Assert.Contains("bool B(int x) { return x <= 0; }", fixedSource)
    Assert.Contains("bool D(int x) { return !(x > 0 || x < -5); }", fixedSource)
    Assert.Contains("bool E(bool p) { return p; }", fixedSource)
    Assert.Contains("void F(int x, out bool r) { r = x > 0; }", fixedSource)
    Assert.Contains("if (x > 0) return true; return true;", fixedSource)

[<Fact>]
let ``a property target, a comment, or a non-bool condition keeps the ifs`` () =
    let source =
        """
class C
{
    bool P { get; set; }
    void A(int x) { if (x > 0) P = true; else P = false; }
    bool B(int x)
    {
        if (x > 0)
            return true; // the fast path
        return false;
    }
}
"""

    Assert.Empty(suggestCode "CR0001" source)

// ---- CR0005 ----

[<Fact>]
let ``nested ifs merge with an identical else or with none, never with a lone outer else`` () =
    let source =
        """
class C
{
    int A(bool a, bool b)
    {
        if (a)
        {
            if (b) return 1;
            else return 2;
        }
        else return 2;
        return 0;
    }
    void B(bool a, bool b, bool c)
    {
        if (a || c)
        {
            if (b) System.Console.WriteLine("x");
        }
    }
    int D(bool a, bool b)
    {
        if (a)
        {
            if (b) return 1;
        }
        else return 2;
        return 0;
    }
}
"""

    let fired = suggestCode "CR0005" source
    Assert.Equal(2, fired.Length)
    let fixedSource = fixAll "CR0005" source
    Assert.Contains("if (a && b) return 1;\n        else return 2;", fixedSource)
    Assert.Contains("if ((a || c) && b) System.Console.WriteLine(\"x\");", fixedSource)
    Assert.Contains("if (a)\n        {\n            if (b) return 1;\n        }\n        else return 2;", fixedSource)

// ---- CR0010 ----

[<Fact>]
let ``a guard that only compares the binder to a constant becomes the constant pattern`` () =
    let source =
        """
class C
{
    const string Kind = "k";
    int A(string s)
    {
        switch (s)
        {
            case var x when x == "A": return 1;
            case var y when "B" == y: return 2;
            case var z when z == Kind: return 3;
            case var w when w == "W": return w.Length;
            default: return 0;
        }
    }
    int B(int n) => n switch { var v when v == 3 => 30, var v when v == -1 => 10, _ => 0 };
}
"""

    let fired = suggestCode "CR0010" source |> firedText source

    Assert.Equal<string list>(
        [
            "var x when x == \"A\""
            "var y when \"B\" == y"
            "var z when z == Kind"
            "var v when v == 3"
            "var v when v == -1"
        ],
        fired
    )

    let fixedSource = fixAll "CR0010" source
    Assert.Contains("case \"A\": return 1;", fixedSource)
    Assert.Contains("case \"B\": return 2;", fixedSource)
    Assert.Contains("case Kind: return 3;", fixedSource)
    Assert.Contains("case var w when w == \"W\": return w.Length;", fixedSource)
    Assert.Contains("3 => 30, -1 => 10", fixedSource)

// ---- CR0009 ----

[<Fact>]
let ``adjacent same-body sections stack their labels, non-adjacent and binding ones stay`` () =
    let source =
        """
class C
{
    string A(int n)
    {
        switch (n)
        {
            case 1:
                return "one-ish";
            case 2:
                return "one-ish";
            case 3:
                return "three";
            case 4:
                return "one-ish";
            case int k when k > 10:
                return "big";
            default:
                return "other";
        }
    }
    string B(int n) => n switch
    {
        1 => "small",
        2 => "small",
        3 => "three",
        4 => "other",
        _ => "other",
    };
}
"""

    let fired = suggestCode "CR0009" source
    // the discard arm is the expression's default: `4 or _` reads as a mistake
    Assert.Equal(2, fired.Length)
    let fixedSource = fixAll "CR0009" source
    Assert.Contains("case 1:\n            case 2:\n                return \"one-ish\";", fixedSource)
    Assert.Contains("case 4:\n                return \"one-ish\";", fixedSource)
    Assert.Contains("1 or 2 => \"small\",", fixedSource)
    Assert.Contains("4 => \"other\",\n        _ => \"other\",", fixedSource)

[<Fact>]
let ``a comment inside a dropped body holds the merge`` () =
    let source =
        "class C\n{\n    string A(int n)\n    {\n        switch (n)\n        {\n            case 1:\n                return \"x\"; // one\n            case 2:\n                return \"x\";\n            default:\n                return \"y\";\n        }\n    }\n}\n"

    Assert.Empty(suggestCode "CR0009" source)

// ---- CR0012 ----

[<Fact>]
let ``an arm accused by its comment throws instead of returning a stand-in`` () =
    let source =
        """
using System;
enum Method { Gauss, Seidel, Jordan }
class C
{
    static double[] Solve(Method m, double[] cf)
    {
        switch (m)
        {
            case Method.Gauss: return Gauss(cf);
            case Method.Seidel:
                // not supported yet
                return null;
            default: throw new ArgumentOutOfRangeException(nameof(m));
        }
    }
    static double[] Gauss(double[] cf) => cf;
    static int Area(Method m) => m switch
    {
        Method.Gauss => Gauss(new double[1]).Length,
        Method.Seidel => Gauss(new double[2]).Length,
        Method.Jordan => // TODO: not implemented
            0,
    };
    static int Table(Method m) => m switch
    {
        Method.Gauss => 1,
        Method.Seidel => // unsupported
            0,
        _ => 2,
    };
    static string? Maybe(Method m)
    {
        switch (m)
        {
            case Method.Gauss: return Gauss(new double[0]).Length.ToString();
            case Method.Seidel:
                // not supported yet
                return null;
            default: return "x";
        }
    }
}
"""

    let fired = suggestCode "CR0012" source |> firedText source
    Assert.Equal<string list>([ "return null;"; "0" ], fired)
    let fixedSource = fixAll "CR0012" source
    Assert.Contains("throw new NotImplementedException();", fixedSource)

    Assert.Contains(
        "Method.Jordan => // TODO: not implemented\n            throw new NotImplementedException(),",
        fixedSource
    )

    Assert.Contains("Method.Seidel => // unsupported\n            0,", fixedSource)

[<Fact>]
let ``a merged condition past the wrap column keeps the nesting`` () =
    let source =
        """
class C
{
    void A(System.Collections.Generic.Dictionary<string, string> secrets)
    {
        if (!secrets.TryGetValue("oauth_client_id", out var clientId) || string.IsNullOrWhiteSpace(clientId))
        {
            if (!secrets.TryGetValue("client_id", out clientId) || string.IsNullOrWhiteSpace(clientId))
            {
                throw new System.InvalidOperationException("client_id not found");
            }
        }
    }
}
"""

    Assert.Empty(suggestCode "CR0005" source)

[<Fact>]
let ``a negated comparison flips its operator, except on floating or nullable operands`` () =
    let source =
        """
class C
{
    bool A(int x) { if (x < 0) return false; return true; }
    bool B(double x) { if (x < 0) return false; return true; }
    bool D(string s) { if (s == "a") return false; return true; }
    bool E(decimal m) { if (m >= 1m) return false; return true; }
    bool F(decimal? m) { if (m > 0) return false; return true; }
    bool G(int? n) { if (n == 3) return false; return true; }
}
"""

    let fixedSource = fixAll "CR0001" source
    Assert.Contains("bool A(int x) { return x >= 0; }", fixedSource)
    Assert.Contains("bool B(double x) { return !(x < 0); }", fixedSource)
    Assert.Contains("bool D(string s) { return s != \"a\"; }", fixedSource)
    Assert.Contains("bool E(decimal m) { return m < 1m; }", fixedSource)
    // a lifted ordering is false both ways on null: `!(m > 0)` is true there, `m <= 0` false
    Assert.Contains("bool F(decimal? m) { return !(m > 0); }", fixedSource)
    Assert.Contains("bool G(int? n) { return n != 3; }", fixedSource)

[<Fact>]
let ``a long run of arms folds one pattern per line`` () =
    let source =
        """
enum Kind { PaymentOutCreated, PaymentOutSent, PaymentOutCleared, PaymentOutFailed, PaymentOutCancelled, Other }
class C
{
    string A(Kind k) => k switch
    {
        Kind.PaymentOutCreated => "PayoutStatusChanged",
        Kind.PaymentOutSent => "PayoutStatusChanged",
        Kind.PaymentOutCleared => "PayoutStatusChanged",
        Kind.PaymentOutFailed => "PayoutStatusChanged",
        Kind.PaymentOutCancelled => "PayoutStatusChanged",
        _ => "Unmapped",
    };
}
"""

    let fixedSource = fixAll "CR0009" source

    let expected =
        "        Kind.PaymentOutCreated\n            or Kind.PaymentOutSent\n            or Kind.PaymentOutCleared\n            or Kind.PaymentOutFailed\n            or Kind.PaymentOutCancelled => \"PayoutStatusChanged\",\n        _ => \"Unmapped\","

    Assert.Contains(expected, fixedSource)

[<Fact>]
let ``a comparison joins the merged condition without parentheses`` () =
    let source =
        """
using System;
using System.Collections.Generic;
class C
{
    static readonly TimeSpan Expiry = TimeSpan.FromMinutes(5);
    int? A(Dictionary<string, DateTime> stamps, string key)
    {
        if (stamps.TryGetValue(key, out var stamp))
        {
            if (DateTime.UtcNow - stamp > Expiry)
            {
                // expired
                stamps.Remove(key);
                return null;
            }
        }
        return 1;
    }
}
"""

    let fixedSource = fixAll "CR0005" source

    Assert.Contains(
        "if (stamps.TryGetValue(key, out var stamp) && DateTime.UtcNow - stamp > Expiry)\n        {\n            // expired",
        fixedSource
    )

[<Fact>]
let ``a comment above the inner if holds the merge`` () =
    let source =
        """
class C
{
    void A(bool a, bool b)
    {
        if (a)
        {
            // only when b
            if (b) System.Console.WriteLine("x");
        }
    }
}
"""

    Assert.Empty(suggestCode "CR0005" source)

[<Fact>]
let ``a table of constants with a throwing default is not a stub`` () =
    let source =
        """
using System;
enum Kind { A, B, C }
class C
{
    static int N(Kind k)
    {
        switch (k)
        {
            case Kind.A: return 1;
            case Kind.B:
                // not supported yet
                return 0;
            default: throw new ArgumentOutOfRangeException(nameof(k));
        }
    }
}
"""

    Assert.Empty(suggestCode "CR0012" source)
