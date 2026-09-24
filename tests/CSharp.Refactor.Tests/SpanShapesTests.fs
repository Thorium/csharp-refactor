module CSharp.Refactor.Tests.SpanShapesTests

open Xunit
open CSharp.Refactor.Tests.Harness

// ---- CR0174 ----

[<Fact>]
let ``a Substring or a range handed to a span-reading consumer becomes AsSpan; a comparison consumer and a bound copy stay``
    ()
    =
    let source =
        """
using System.Globalization;
using System.IO;
using System.Text;
class C
{
    int A(string s) => int.Parse(s.Substring(6, 5));
    bool B(string s, out long v) => long.TryParse(s.Substring(6), NumberStyles.Integer, CultureInfo.InvariantCulture, out v);
    void D(string s, StringBuilder sb) { sb.Append(s.Substring(6)); sb.Append(s[6..]); sb.Append(s[6..11]); sb.Append(s[^3..]); }
    void E(string s, TextWriter w, StringWriter sw) { w.Write(s.Substring(1, 2)); sw.WriteLine(s.Substring(2)); }
    bool F(string s) => s.Substring(0, 3).StartsWith("ab");
    bool G(string s) => s.Substring(0, 3).Equals("abc");
    string H(string s) { var part = s.Substring(1); return part + part; }
    decimal I(string s) => decimal.Parse(s[2..]);
    string J(string s) => string.Concat(s.Substring(0, 2), "x");
    System.Numerics.BigInteger K(string s) => System.Numerics.BigInteger.Parse(s.Substring(1));
}
"""

    let fired = suggestCode "CR0174" source |> firedText source

    Assert.Equal<string list>(
        [
            "s.Substring(6, 5)"
            "s.Substring(6)"
            "s.Substring(6)"
            "s[6..]"
            "s[6..11]"
            "s[^3..]"
            "s.Substring(1, 2)"
            "s.Substring(2)"
            "s[2..]"
            "s.Substring(0, 2)"
            "s.Substring(1)"
        ],
        fired
    )

    let fixedSource = fixAll "CR0174" source
    Assert.Contains("int.Parse(s.AsSpan(6, 5))", fixedSource)

    Assert.Contains(
        "long.TryParse(s.AsSpan(6), NumberStyles.Integer, CultureInfo.InvariantCulture, out v)",
        fixedSource
    )

    Assert.Contains(
        "sb.Append(s.AsSpan(6)); sb.Append(s.AsSpan(6)); sb.Append(s.AsSpan()[6..11]); sb.Append(s.AsSpan()[^3..]);",
        fixedSource
    )

    Assert.Contains("w.Write(s.AsSpan(1, 2)); sw.WriteLine(s.AsSpan(2));", fixedSource)
    Assert.Contains("s.Substring(0, 3).StartsWith(\"ab\")", fixedSource)
    Assert.Contains("s.Substring(0, 3).Equals(\"abc\")", fixedSource)
    Assert.Contains("var part = s.Substring(1);", fixedSource)
    Assert.Contains("decimal.Parse(s.AsSpan(2))", fixedSource)
    Assert.Contains("string.Concat(s.AsSpan(0, 2), \"x\")", fixedSource)
    Assert.Contains("using System;", fixedSource)

[<Fact>]
let ``CR0174 keeps a Substring handed to a domain type's Parse, whose string overload normalises the SKU`` () =
    // a user type's string and span overloads are its author's; here only the string one trims and upper-cases
    let source =
        """
using System;
namespace Catalog
{
    sealed class Sku
    {
        public string Code { get; }
        Sku(string code) { Code = code; }
        public static Sku Parse(string s) => new Sku(s.Trim().ToUpperInvariant());
        public static Sku Parse(ReadOnlySpan<char> s) => new Sku(s.ToString());
    }

    class OrderLineReader
    {
        Sku ReadSku(string line) => Sku.Parse(line.Substring(0, 8));
    }
}
"""

    Assert.Empty(suggestCode "CR0174" source)

// ---- CR0175 ----

[<Fact>]
let ``a guarded prefix or suffix compared with a literal becomes StartsWith or EndsWith; unguarded, mis-sized and mid cuts stay``
    ()
    =
    let source =
        """
class C
{
    bool A(string s) => s.Length >= 6 && s.Substring(0, 6) == "ORDER-";
    bool B(string s) => s.Length > 5 && s[..6] == "ORDER-";
    bool D(string s) => s.Length < 3 || s.Substring(s.Length - 3) != "MED";
    bool E(string s) { if (s.Length >= 3) { return s[^3..] == "MED"; } return false; }
    bool F(string s) => s.Substring(0, 6) == "ORDER-";
    bool G(string s) => s.Length >= 6 && s.Substring(0, 3) == "ORDER-";
    bool H(string s) => s.Length >= 6 && s.Substring(1, 5) == "RDER-";
    bool I(string s) => s.Length >= 6 && "ORDER-" == s.Substring(0, 6);
    bool J(string s) { if (s.Length >= 6) { s = s.Trim(); return s.Substring(0, 6) == "ORDER-"; } return false; }
    bool K(string s, bool other) => other && s.Length >= 6 && s.Substring(0, 6) == "ORDER-";
    bool L(string s) => s.Length >= 6 ? s[0..6] == "ORDER-" : false;
    bool M(string s) => 6 <= s.Length && s.Substring(0, 6) == "ORDER-";
    class P { public string S = ""; }
    bool N(P p, P q) { if (p.S.Length >= 6) { p = q; return p.S.Substring(0, 6) == "ORDER-"; } return false; }
    bool O(P p) { if (p.S.Length >= 6) { return p.S.Substring(0, 6) == "ORDER-"; } return false; }
    static void Swap(ref P a) { }
    bool Q(P p) { if (p.S.Length >= 6) { Swap(ref p); return p.S.Substring(0, 6) == "ORDER-"; } return false; }
}
"""

    let fired = suggestCode "CR0175" source |> firedText source

    Assert.Equal<string list>(
        [
            "s.Substring(0, 6) == \"ORDER-\""
            "s[..6] == \"ORDER-\""
            "s.Substring(s.Length - 3) != \"MED\""
            "s[^3..] == \"MED\""
            "s.Substring(0, 6) == \"ORDER-\""
            "\"ORDER-\" == s.Substring(0, 6)"
            "s.Substring(0, 6) == \"ORDER-\""
            "s.Substring(0, 6) == \"ORDER-\""
            "s[0..6] == \"ORDER-\""
            "s.Substring(0, 6) == \"ORDER-\""
            "p.S.Substring(0, 6) == \"ORDER-\""
            "p.S.Substring(0, 6) == \"ORDER-\""
            "p.S.Substring(0, 6) == \"ORDER-\""
        ],
        fired
    )

    // the unguarded sites (F, J) are offered in the editor only: a sweep leaves them
    let editorOnly =
        suggestCode "CR0175" source
        |> List.filter (fun s -> s.Fixes |> List.forall (fun f -> f.EditorOnly))
        |> firedText source

    // N and Q write `p` under a guard on `p.S`: the guard is void, the editor still offers
    Assert.Equal<string list>(
        [
            "s.Substring(0, 6) == \"ORDER-\""
            "s.Substring(0, 6) == \"ORDER-\""
            "p.S.Substring(0, 6) == \"ORDER-\""
            "p.S.Substring(0, 6) == \"ORDER-\""
        ],
        editorOnly
    )

    let fixedSource = fixAll "CR0175" source

    Assert.Contains(
        "bool A(string s) => s.Length >= 6 && s.StartsWith(\"ORDER-\", StringComparison.Ordinal);",
        fixedSource
    )

    Assert.Contains(
        "bool B(string s) => s.Length > 5 && s.StartsWith(\"ORDER-\", StringComparison.Ordinal);",
        fixedSource
    )

    Assert.Contains("bool D(string s) => s.Length < 3 || !s.EndsWith(\"MED\", StringComparison.Ordinal);", fixedSource)
    Assert.Contains("return s.EndsWith(\"MED\", StringComparison.Ordinal); } return false;", fixedSource)
    Assert.Contains("bool F(string s) => s.Substring(0, 6) == \"ORDER-\";", fixedSource)
    Assert.Contains("s.Substring(0, 3) == \"ORDER-\"", fixedSource)
    Assert.Contains("s.Substring(1, 5) == \"RDER-\"", fixedSource)
    Assert.Contains("s = s.Trim(); return s.Substring(0, 6) == \"ORDER-\";", fixedSource)
    Assert.Contains("using System;", fixedSource)

// ---- CR0176 ----

[<Fact>]
let ``a ToCharArray a foreach reads once becomes the string; a sliced, bound or LINQ-consumed copy stays`` () =
    let source =
        """
using System.Linq;
class C
{
    int A(string s) { int n = 0; foreach (var c in s.ToCharArray()) if (c == 'a') n++; return n; }
    int B(string s) { int n = 0; foreach (char c in s.ToCharArray(1, 2)) n += c; return n; }
    int D(string s) { var chars = s.ToCharArray(); chars[0] = 'x'; return chars.Length; }
    bool E(string s) => s.ToCharArray().Any(char.IsDigit);
    int F(char[] cs) { int n = 0; foreach (var c in cs) n += c; return n; }
}
"""

    Assert.Equal<string list>([ "s.ToCharArray()" ], suggestCode "CR0176" source |> firedText source)
    let fixedSource = fixAll "CR0176" source
    Assert.Contains("foreach (var c in s) if (c == 'a') n++;", fixedSource)
    Assert.Contains("foreach (char c in s.ToCharArray(1, 2))", fixedSource)
    Assert.Contains("s.ToCharArray().Any(char.IsDigit)", fixedSource)
