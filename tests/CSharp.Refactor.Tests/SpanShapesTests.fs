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

[<Fact>]
let ``CR0175 sweeps only where the guard and the cut read the same string`` () =
    let source =
        """
record Order(string Code);
class C
{
    readonly string name = "ORDER-1";
    string field = "ORDER-1";
    string Label { get; } = "ORDER-1";
    int calls;
    string Computed => (calls++ % 2 == 0) ? "ORDER-1234" : "ab";
    bool Reset() { field = "ab"; return true; }
    void Clear() { field = "ab"; }
    bool A(string s) => s.Length >= 6 && s.Substring(0, 6) == "ORDER-";
    bool B(string s) { var t = s.Trim(); if (t.Length >= 6) return t.Substring(0, 6) == "ORDER-"; return false; }
    bool D() => this.name.Length >= 6 && this.name.Substring(0, 6) == "ORDER-";
    bool E(Order o) => o.Code.Length >= 6 && o.Code.Substring(0, 6) == "ORDER-";
    bool F(string s) => s.Length >= 6 ? s.Substring(0, 6) == "ORDER-" : false;
    bool G() => Label.Length >= 6 && Label.Substring(0, 6) == "ORDER-";
    bool H() => field.Length >= 6 && field.Substring(0, 6) == "ORDER-";
    bool F1() => Computed.Length >= 6 && Computed.Substring(0, 6) == "ORDER-";
    bool F2() => this.field.Length >= 6 && Reset() && this.field.Substring(0, 6) == "ORDER-";
    bool F3() { string s = "ORDER-1234"; return s.Length >= 6 && (s = "ab") != null && s.Substring(0, 6) == "ORDER-"; }
    bool F4() { string s = "ORDER-1234"; void Shorten() { s = "ab"; } if (s.Length >= 6) { Shorten(); return s.Substring(0, 6) == "ORDER-"; } return false; }
    bool F5() { if (field.Length >= 6) { Clear(); return field.Substring(0, 6) == "ORDER-"; } return false; }
}
"""

    let fired = suggestCode "CR0175" source
    Assert.Equal(12, fired.Length)

    let swept =
        fired
        |> List.filter (fun s -> s.Fixes |> List.exists (fun f -> not f.EditorOnly))
        |> List.length

    Assert.Equal(7, swept)
    let fixedSource = fixAll "CR0175" source

    Assert.Contains(
        "bool A(string s) => s.Length >= 6 && s.StartsWith(\"ORDER-\", StringComparison.Ordinal);",
        fixedSource
    )

    Assert.Contains("return t.StartsWith(\"ORDER-\", StringComparison.Ordinal);", fixedSource)
    Assert.Contains("this.name.Length >= 6 && this.name.StartsWith(\"ORDER-\", StringComparison.Ordinal);", fixedSource)
    Assert.Contains("o.Code.Length >= 6 && o.Code.StartsWith(\"ORDER-\", StringComparison.Ordinal);", fixedSource)
    Assert.Contains("s.Length >= 6 ? s.StartsWith(\"ORDER-\", StringComparison.Ordinal) : false;", fixedSource)
    Assert.Contains("Label.Length >= 6 && Label.StartsWith(\"ORDER-\", StringComparison.Ordinal);", fixedSource)
    Assert.Contains("field.Length >= 6 && field.StartsWith(\"ORDER-\", StringComparison.Ordinal);", fixedSource)
    Assert.Contains("Computed.Length >= 6 && Computed.Substring(0, 6) == \"ORDER-\";", fixedSource)
    Assert.Contains("Reset() && this.field.Substring(0, 6) == \"ORDER-\";", fixedSource)
    Assert.Contains("(s = \"ab\") != null && s.Substring(0, 6) == \"ORDER-\";", fixedSource)
    Assert.Contains("Shorten(); return s.Substring(0, 6) == \"ORDER-\";", fixedSource)
    Assert.Contains("Clear(); return field.Substring(0, 6) == \"ORDER-\";", fixedSource)

[<Fact>]
let ``CR0175 under an if ends the window at the cut; closures and awaits hold it to the editor, an interface call does not``
    ()
    =
    let source =
        """
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
class C
{
    static string name = "abcdef";
    string Name { get; set; } = "abcd";
    static void Log(string m) { }
    void Hit() { }
    void S02(string raw) { var line = raw; line = line.Trim(); if (line.Length >= 3) { if (line.Substring(0, 3) == "abc") Log(line); } }
    bool S03() { if (Name.Length >= 3) { if (Name.Substring(0, 3) == "abc") { Hit(); return true; } } return false; }
    void S12(StringReader reader) { string? line; while ((line = reader.ReadLine()) != null) { if (line.Length >= 3) { if (line.Substring(0, 3) == "abc") Log(line); } } }
    async Task<bool> S05(Task t) { if (name.Length >= 3) { await t; return name.Substring(0, 3) == "abc"; } return false; }
    bool S06() { string s = "abcdef"; Action<int> reset = _ => s = ""; if (s.Length >= 3) { Array.ForEach(new[] { 1 }, reset); return s.Substring(0, 3) == "abc"; } return false; }
    bool S07() { string s = "abcdef"; var lazy = new Lazy<int>(() => { s = ""; return 1; }); return s.Length >= 3 && lazy.Value == 1 && s.Substring(0, 3) == "abc"; }
    bool S08(ICollection<int> sink) { if (name.Length >= 3) { sink.Add(1); return name.Substring(0, 3) == "abc"; } return false; }
    bool S09() { string s = "abcdef"; Action reset = () => s = ""; if (s.Length >= 3) { Task.Run(reset).Wait(); return s.Substring(0, 3) == "abc"; } return false; }
}
"""

    let fired = suggestCode "CR0175" source
    Assert.Equal(8, fired.Length)

    let swept =
        fired
        |> List.filter (fun s -> s.Fixes |> List.exists (fun f -> not f.EditorOnly))
        |> firedText source

    // S08's `sink.Add(1)` runs an implementation that cannot be seen: the
    // accepted residual, swept
    Assert.Equal<string list>(
        [
            "line.Substring(0, 3) == \"abc\""
            "Name.Substring(0, 3) == \"abc\""
            "line.Substring(0, 3) == \"abc\""
            "name.Substring(0, 3) == \"abc\""
        ],
        swept
    )

[<Fact>]
let ``CR0175 holds to the editor a user conversion, operator or Deconstruct between, and a cut deferred into a lambda``
    ()
    =
    let source =
        """
using System;
using System.Linq;
public sealed class W
{
    public Rec? R;
    public static implicit operator string(W w) { w.R!.Name = ""; return "x"; }
    public static W operator +(W w, int i) { w.R!.Name = ""; return w; }
    public static W operator ++(W w) { w.R!.Name = ""; return w; }
    public void Deconstruct(out int a, out int b) { R!.Name = ""; a = b = 0; }
}
public sealed class Benign
{
    public static implicit operator string(Benign b) => "x";
}
public sealed class Rec
{
    public string Name = "abcd";
    void Reset() { Name = ""; }
    bool Fixed(Benign b) { if (Name.Length >= 3) { string t = b; if (Name.Substring(0, 3) == "abc") return true; } return false; }
    bool V01(W w) { if (Name.Length >= 3) { string t = w; if (Name.Substring(0, 3) == "abc") return true; } return false; }
    bool V02(W w) { if (Name.Length >= 3) { w += 1; if (Name.Substring(0, 3) == "abc") return true; } return false; }
    bool V03(W w) { if (Name.Length >= 3) { w++; if (Name.Substring(0, 3) == "abc") return true; } return false; }
    bool V06(W w) { if (Name.Length >= 3) { var (a, b) = w; if (Name.Substring(0, 3) == "abc") return true; } return false; }
    bool W01() { if (Name.Length >= 3) { Func<bool> f = () => Name.Substring(0, 3) == "abc"; Reset(); return f(); } return false; }
    bool W03() { if (Name.Length >= 3) { bool F() => Name.Substring(0, 3) == "abc"; Reset(); return F(); } return false; }
    int W04() { if (Name.Length >= 3) { var q = new[] { 1 }.Where(i => Name.Substring(0, 3) == "abc"); Reset(); return q.Count(); } return 0; }
    bool Kept(int n) { if (Name.Length >= 3) { n += 1; if (Name.Substring(0, 3) == "abc") return true; } return false; }
}
"""

    let fired = suggestCode "CR0175" source
    Assert.Equal(9, fired.Length)

    // the operators, conversion and Deconstruct of `W` visibly assign `Name`;
    // `Benign`'s conversion does not, and `n += 1` is the built-in operator
    let swept =
        fired
        |> List.filter (fun s -> s.Fixes |> List.exists (fun f -> not f.EditorOnly))
        |> List.length

    Assert.Equal(2, swept)

[<Fact>]
let ``CR0175 in a top-level program sees a local reassigned in a neighbouring statement`` () =
    let program (extra: string) =
        "using System;\nstring s = Console.ReadLine() ?? \"\";\n"
        + extra
        + "if (s.Length >= 3)\n{\n    Console.WriteLine(s.Substring(0, 3) == \"abc\");\n}\n"

    let run (source: string) =
        let compilation, tree =
            compileRaw Microsoft.CodeAnalysis.CSharp.LanguageVersion.Latest source

        suggestRaw compilation tree |> List.filter (fun s -> s.Code = "CR0175")

    let sweeps (found: CSharp.Refactor.Suggestion list) =
        found
        |> List.exists (fun f -> f.Fixes |> List.exists (fun x -> not x.EditorOnly))

    // a lambda in another top-level statement writes the local: never proven
    let reassigned = run (program "Action reset = () => s = \"\";\nreset();\n")
    Assert.Equal(1, reassigned.Length)
    Assert.False(sweeps reassigned)

    let plain = run (program "")
    Assert.Equal(1, plain.Length)
    Assert.True(sweeps plain)

[<Fact>]
let ``CR0175 sees a deconstruction, a ref alias and a primary constructor parameter as writes of the receiver`` () =
    let source =
        """
using System;
public sealed class Node { public string Name { get; set; } = "abcdef"; }
public sealed class Holder(string name)
{
    public void Reset() => name = "x";
    public bool H07() { if (name.Length >= 3) { Reset(); return name.Substring(0, 3) == "abc"; } return false; }
}
public sealed class Lazy(string name)
{
    public Action Zap => () => name = "x";
    public bool H08() { if (name.Length >= 3) { Zap(); return name.Substring(0, 3) == "abc"; } return false; }
}
public sealed class Still(string name)
{
    public void Touch() { }
    public bool H09() { if (name.Length >= 3) { Touch(); return name.Substring(0, 3) == "abc"; } return false; }
}
public sealed class Rec
{
    string name = "abcdef";
    public bool H04() { if (name.Length >= 3) { (name, _) = ("x", 1); return name.Substring(0, 3) == "abc"; } return false; }
}
static class C
{
    static bool H02(string s) { if (s.Length >= 3) { (s, _) = ("x", 1); return s.Substring(0, 3) == "abc"; } return false; }
    static bool H03(string s, string other) { if (s.Length >= 3) { (s, other) = (other, s); return s.Substring(0, 3) == "abc"; } return false; }
    static bool H05(Node n) { if (n.Name.Length >= 3) { (n.Name, _) = ("x", 1); return n.Name.Substring(0, 3) == "abc"; } return false; }
    static bool J01(string s) { ref string r = ref s; if (s.Length >= 3) { r = "x"; return s.Substring(0, 3) == "abc"; } return false; }
    static bool J02(string s) { ref string r = ref s; return s.Length >= 3 && (r = "x").Length > 0 && s.Substring(0, 3) == "abc"; }
    static bool Kept(string s, string other) { if (s.Length >= 3) { (other, _) = ("x", 1); return s.Substring(0, 3) == "abc" && other == "x"; } return false; }
}
"""

    let fired = suggestCode "CR0175" source
    Assert.Equal(10, fired.Length)

    let swept =
        fired
        |> List.filter (fun s -> s.Fixes |> List.exists (fun f -> not f.EditorOnly))
        |> firedText source

    // H09 (a primary parameter the type never writes) and Kept (another local deconstructed)
    Assert.Equal<string list>([ "name.Substring(0, 3) == \"abc\""; "s.Substring(0, 3) == \"abc\"" ], swept)

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
