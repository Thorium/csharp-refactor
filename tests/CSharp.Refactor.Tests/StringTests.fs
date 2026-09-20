module CSharp.Refactor.Tests.StringTests

open Xunit
open CSharp.Refactor.Tests.Harness

// ---- CR0100 ----

[<Fact>]
let ``a concatenation of literals and values becomes an interpolated string; two terms, verbatim and braces stay`` () =
    let source =
        """
using System;
class C
{
    string A(string name) => "Hello " + name + "!";
    string B(int n, string unit) => "Total: " + n + " " + unit;
    string D(string path) => path + ".bak";
    string E(string a, string b) => a + b + "x" + "y";
    string F(string name) => @"C:\" + name + "\\";
    string G(string name) => "{" + name + "}";
    string H(int a, int b) => a + b + " items";
    string I(bool ok) => "state: " + (ok ? "on" : "off") + ".";
    string J(string name) => $"Hi {name}" + " and " + name;
    int K(string a, string b) => (a + ", " + b).Length;
}
"""

    let fired = suggestCode "CR0100" source
    // A, B, E, I, J, K (H has two operands once the numeric `+` is one)
    Assert.Equal(6, fired.Length)
    let fixedSource = fixAll "CR0100" source
    Assert.Contains("$\"Hello {name}!\"", fixedSource)
    Assert.Contains("$\"Total: {n} {unit}\"", fixedSource)
    Assert.Contains("$\"{a}{b}xy\"", fixedSource)
    Assert.Contains("a + b + \" items\"", fixedSource)
    Assert.Contains("$\"state: {(ok ? \"on\" : \"off\")}.\"", fixedSource)
    Assert.Contains("$\"Hi {name} and {name}\"", fixedSource)
    // the parentheses the `+` needed go with it
    Assert.Contains("=> $\"{a}, {b}\".Length;", fixedSource)
    Assert.Contains("path + \".bak\"", fixedSource)
    Assert.Contains("@\"C:\\\" + name", fixedSource)
    Assert.Contains("\"{\" + name + \"}\"", fixedSource)

// ---- CR0101 ----

[<Fact>]
let ``string.Format with a literal template becomes an interpolated string; providers, arrays and reuse stay`` () =
    let source =
        """
using System;
using System.Globalization;
class C
{
    string A(string a, decimal b) => string.Format("{0} of {1:N2}", a, b);
    string B(int n) => string.Format("{0,5}|{{x}}", n);
    string D(string a, string b) => string.Format(CultureInfo.InvariantCulture, "{0} {1}", a, b);
    string E(object[] args) => string.Format("{0} {1}", args);
    string F() => string.Format("{0} {0}", DateTime.Now.Ticks);
    string G(int x) => string.Format("{0} {0}", x);
    string H(int x) => string.Format("{1}", x, x);
    string I(int x) => string.Format("{1}.{0}\n", x, "label");
    string J(int x) => string.Format(@"c:\{0}\{1}", "dir", x);
}
"""

    let fired = suggestCode "CR0101" source
    // A, B, G, I, J
    Assert.Equal(5, fired.Length)
    let fixedSource = fixAll "CR0101" source
    Assert.Contains("$\"{a} of {b:N2}\"", fixedSource)
    Assert.Contains("$\"{n,5}|{{x}}\"", fixedSource)
    Assert.Contains("$\"{x} {x}\"", fixedSource)
    // a literal argument is text, not a hole
    Assert.Contains("$\"label.{x}\\n\"", fixedSource)
    Assert.Contains("$@\"c:\\dir\\{x}\"", fixedSource)
    Assert.Contains("string.Format(\"{0} {0}\", DateTime.Now.Ticks)", fixedSource)

// ---- CR0102 ----

[<Fact>]
let ``a ToString inside a hole or under Join goes where the value cannot be null`` () =
    let source =
        """
#nullable enable
using System;
using System.Linq;
class C
{
    string A(int x) => $"{x.ToString()} items";
    string B(int? x) => $"{x.ToString()} items";
    string D(int x) => $"{x.ToString():N0} items";
    string E(string s) => $"{s.ToString()}";
    string F(string? s) => $"{s.ToString()}";
    string G(int[] xs) => string.Join(", ", xs.Select(x => x.ToString()));
    string H(string?[] xs) => string.Join(", ", xs.Select(x => x.ToString()));
}
"""

    let fired = suggestCode "CR0102" source
    // A, E, G
    Assert.Equal(3, fired.Length)
    let fixedSource = fixAll "CR0102" source
    Assert.Contains("$\"{x} items\";", fixedSource)
    Assert.Contains("$\"{s}\";", fixedSource)
    Assert.Contains("string.Join(\", \", xs);", fixedSource)
    Assert.Contains("$\"{x.ToString():N0} items\"", fixedSource)

// ---- CR0104 ----

[<Fact>]
let ``spelled-out emptiness tests become IsNullOrEmpty or IsNullOrWhiteSpace; a leading Trim is editor-only`` () =
    let source =
        """
using System;
class C
{
    bool A(string x) => x == null || x == "";
    bool B(string x) => x is null || x.Length == 0;
    bool D(string x) => x == null || x.Trim() == "";
    bool E(string x) => x != null && x != string.Empty;
    bool F(string x) => x.Trim() == "" || x == null;
    bool G(string x) => string.IsNullOrEmpty(x.Trim());
    bool H(string x, string y) => x == null || y == "";
    bool I(string x) => x == null || x.Trim().Length == 0;
}
"""

    let fired = suggestCode "CR0104" source
    Assert.Equal(7, fired.Length)
    let fixedSource = fixAll "CR0104" source
    Assert.Contains("bool A(string x) => string.IsNullOrEmpty(x);", fixedSource)
    Assert.Contains("bool B(string x) => string.IsNullOrEmpty(x);", fixedSource)
    Assert.Contains("bool D(string x) => string.IsNullOrWhiteSpace(x);", fixedSource)
    Assert.Contains("bool E(string x) => !string.IsNullOrEmpty(x);", fixedSource)
    Assert.Contains("bool F(string x) => x.Trim() == \"\" || x == null;", fixedSource)
    Assert.Contains("bool G(string x) => string.IsNullOrEmpty(x.Trim());", fixedSource)
    Assert.Contains("bool I(string x) => string.IsNullOrWhiteSpace(x);", fixedSource)

// ---- CR0105 / CR0106 ----

[<Fact>]
let ``a culture-free Parse is noted with editor offers, the CLI applies the invariant one under the knob`` () =
    let source =
        """
using System;
using System.Globalization;
class C
{
    double A(string s) => double.Parse(s);
    int B(string s) => int.Parse(s);
    DateTime D(string s) => DateTime.Parse(s, CultureInfo.InvariantCulture);
    bool E(string s, out decimal d) => decimal.TryParse(s, out d);
}
"""

    let fired = suggestCode "CR0105" source
    Assert.Equal<string list>([ "double.Parse(s)"; "decimal.TryParse(s, out d)" ], firedText source fired)
    Assert.True(fired |> List.forall (fun s -> s.Fixes |> List.forall (fun f -> f.EditorOnly)))
    Assert.Contains("double.Parse(s)", fixAll "CR0105" source)

    let fixedSource =
        fixAllWith
            (Some(
                FakeOptions(dict [ "csharp_refactor.CR0105.invariant", "true" ])
                :> Microsoft.CodeAnalysis.Diagnostics.AnalyzerConfigOptions
            ))
            "CR0105"
            source

    Assert.Contains("double.Parse(s, CultureInfo.InvariantCulture)", fixedSource)

[<Fact>]
let ``DateTime.Now as an instant becomes UtcNow under the knob; calendar reads, Today and same-day tests are notes or quiet``
    ()
    =
    let source =
        """
using System;
using System.IO;
class C
{
    DateTime A() => DateTime.Now;
    long B() => DateTime.Now.Ticks;
    int D() => DateTime.Now.Day;
    DateTime E() => DateTime.Today;
    bool F(DateTime dt) => dt.Date != DateTime.Today;
    void G(string p) => File.SetLastWriteTime(p, DateTime.Now);
    DateTimeOffset H() => DateTimeOffset.Now;
    DateTime I() => DateTime.UtcNow.Date;
    object J(string member) => member switch { "Now" => DateTime.Now, _ => null };
}
"""

    let fired = suggestCode "CR0106" source
    // A, B (fixes); D, E, I (notes)
    Assert.Equal(5, fired.Length)
    Assert.Contains("DateTime.Now;", fixAll "CR0106" source)

    let fixedSource =
        fixAllWith
            (Some(
                FakeOptions(dict [ "csharp_refactor.CR0106.utc_now", "true" ])
                :> Microsoft.CodeAnalysis.Diagnostics.AnalyzerConfigOptions
            ))
            "CR0106"
            source

    Assert.Contains("DateTime A() => DateTime.UtcNow;", fixedSource)
    Assert.Contains("long B() => DateTime.UtcNow.Ticks;", fixedSource)
    Assert.Contains("int D() => DateTime.Now.Day;", fixedSource)
    Assert.Contains("File.SetLastWriteTime(p, DateTime.Now)", fixedSource)
    Assert.Contains("\"Now\" => DateTime.Now", fixedSource)
