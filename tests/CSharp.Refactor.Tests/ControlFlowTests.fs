module CSharp.Refactor.Tests.ControlFlowTests

open Xunit
open CSharp.Refactor.Tests.Harness

// ---- CR0007 / CR0008 ----

[<Fact>]
let ``identity literals drop, the value-changing ones stay, dynamic stands down`` () =
    let source =
        csharp
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
        csharp
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
        csharp
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

    let fired = fires 5 "CR0001" source
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
        csharp
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
        csharp
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

    let fired = fires 2 "CR0005" source
    let fixedSource = fixAll "CR0005" source

    Assert.Contains(
        csharp
            """
            if (a && b) return 1;
                    else return 2;
            """,
        fixedSource
    )

    Assert.Contains("""if ((a || c) && b) System.Console.WriteLine("x");""", fixedSource)

    Assert.Contains(
        csharp
            """
            if (a)
                    {
                        if (b) return 1;
                    }
                    else return 2;
            """,
        fixedSource
    )

// ---- CR0010 ----

[<Fact>]
let ``a guard that only compares the binder to a constant becomes the constant pattern`` () =
    let source =
        csharp
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
            """var y when "B" == y"""
            "var z when z == Kind"
            "var v when v == 3"
            "var v when v == -1"
        ],
        fired
    )

    let fixedSource = fixAll "CR0010" source
    Assert.Contains("""case "A": return 1;""", fixedSource)
    Assert.Contains("""case "B": return 2;""", fixedSource)
    Assert.Contains("case Kind: return 3;", fixedSource)
    Assert.Contains("""case var w when w == "W": return w.Length;""", fixedSource)
    Assert.Contains("3 => 30, -1 => 10", fixedSource)

// ---- CR0009 ----

[<Fact>]
let ``adjacent same-body sections stack their labels, non-adjacent and binding ones stay`` () =
    let source =
        csharp
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
    assertFired 2 source fired
    let fixedSource = fixAll "CR0009" source

    Assert.Contains(
        csharp
            """
            case 1:
                        case 2:
                            return "one-ish";
            """,
        fixedSource
    )

    Assert.Contains(
        csharp
            """
            case 4:
                            return "one-ish";
            """,
        fixedSource
    )

    Assert.Contains("""1 or 2 => "small",""", fixedSource)

    Assert.Contains(
        csharp
            """
            4 => "other",
                    _ => "other",
            """,
        fixedSource
    )

[<Fact>]
let ``a comment inside a dropped body holds the merge`` () =
    let source =
        csharp
            """
            class C
            {
                string A(int n)
                {
                    switch (n)
                    {
                        case 1:
                            return "x"; // one
                        case 2:
                            return "x";
                        default:
                            return "y";
                    }
                }
            }

            """

    Assert.Empty(suggestCode "CR0009" source)

// ---- CR0012 ----

[<Fact>]
let ``an arm accused by its comment throws instead of returning a stand-in`` () =
    let source =
        csharp
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
        csharp
            """
            Method.Jordan => // TODO: not implemented
                        throw new NotImplementedException(),
            """,
        fixedSource
    )

    Assert.Contains(
        csharp
            """
            Method.Seidel => // unsupported
                        0,
            """,
        fixedSource
    )

[<Fact>]
let ``a merged condition past the wrap column keeps the nesting`` () =
    let source =
        csharp
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
        csharp
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
    Assert.Contains("""bool D(string s) { return s != "a"; }""", fixedSource)
    Assert.Contains("bool E(decimal m) { return m < 1m; }", fixedSource)
    // a lifted ordering is false both ways on null: `!(m > 0)` is true there, `m <= 0` false
    Assert.Contains("bool F(decimal? m) { return !(m > 0); }", fixedSource)
    Assert.Contains("bool G(int? n) { return n != 3; }", fixedSource)

[<Fact>]
let ``a long run of arms folds one pattern per line`` () =
    let source =
        csharp
            """
            enum Kind { ItemOutCreated, ItemOutSent, ItemOutCleared, ItemOutFailed, ItemOutCancelled, Other }
            class C
            {
                string A(Kind k) => k switch
                {
                    Kind.ItemOutCreated => "ItmStatusChanged",
                    Kind.ItemOutSent => "ItmStatusChanged",
                    Kind.ItemOutCleared => "ItmStatusChanged",
                    Kind.ItemOutFailed => "ItmStatusChanged",
                    Kind.ItemOutCancelled => "ItmStatusChanged",
                    _ => "Unmapped",
                };
            }
            """

    let fixedSource = fixAll "CR0009" source

    let expected =
        csharp
            """
                    Kind.ItemOutCreated
                        or Kind.ItemOutSent
                        or Kind.ItemOutCleared
                        or Kind.ItemOutFailed
                        or Kind.ItemOutCancelled => "ItmStatusChanged",
                    _ => "Unmapped",
            """

    Assert.Contains(expected, fixedSource)

[<Fact>]
let ``a comparison joins the merged condition without parentheses`` () =
    let source =
        csharp
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
        csharp
            """
            if (stamps.TryGetValue(key, out var stamp) && DateTime.UtcNow - stamp > Expiry)
                    {
                        // expired
            """,
        fixedSource
    )

[<Fact>]
let ``a comment above the inner if holds the merge`` () =
    let source =
        csharp
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
let ``CR0005 holds a comment beside the outer paren, above an unbraced inner if, or on the outer else`` () =
    let source =
        csharp
            """
            class C
            {
                void A(bool a, bool b)
                {
                    if (a) // only for a
                    {
                        if (b) System.Console.WriteLine("x");
                    }
                }
                void B(bool a, bool b)
                {
                    if (a)
                        // only when b
                        if (b) System.Console.WriteLine("x");
                }
                void D(bool a, bool b)
                {
                    if (a)
                    {
                        if (b) System.Console.WriteLine("x");
                        else System.Console.WriteLine("y");
                    }
                    else // the fallback
                        System.Console.WriteLine("y");
                }
                void E(bool a, bool b)
                {
                    if (a)
                        if (b) System.Console.WriteLine("x"); // kept
                }
            }
            """

    Assert.Equal<string list>([ "if (a)" ], firedText source (suggestCode "CR0005" source))
    Assert.Contains("""if (a && b) System.Console.WriteLine("x"); // kept""", fixAll "CR0005" source)

[<Fact>]
let ``a table of constants with a throwing default is not a stub`` () =
    let source =
        csharp
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

// ---- CR0172 ----

[<Fact>]
let ``a local initialised with a constant and never written becomes const; a written, captured-by-ref or non-constant one does not``
    ()
    =
    let source =
        csharp
            """
            using System;
            enum Status { Active, Inactive }
            class C
            {
                const string Prefix = "v";
                static void Take(ref int r) { }
                static void TakeIn(in int r) { }
                string M(int seed)
                {
                    var schema = "app";
                    int retries = 3;
                    var status = Status.Active;
                    var version = Prefix + "1";
                    double ratio = 1.5, half = 0.5;
                    var interpolated = $"{schema}-{retries}";
                    var counter = 0;
                    counter++;
                    var reassigned = "x";
                    reassigned = "y";
                    int byRef = 1;
                    Take(ref byRef);
                    int byIn = 2;
                    TakeIn(in byIn);
                    var fromParameter = seed;
                    var empty = string.Empty;
                    string nothing = null;
                    var (a, b) = (1, 2);
                    Func<string> f = () => schema;
                    return schema + retries + status + version + ratio + half + interpolated + counter + reassigned + byRef + byIn + fromParameter + empty + nothing + a + b + f();
                }
            }
            """

    let fired = suggestCode "CR0172" source |> firedText source

    Assert.Equal<string list>(
        [
            "var schema = \"app\""
            "int retries = 3"
            "var status = Status.Active"
            "var version = Prefix + \"1\""
            "double ratio = 1.5, half = 0.5"
        ],
        fired
    )

    let fixedSource = fixAll "CR0172" source
    Assert.Contains("""const string schema = "app";""", fixedSource)
    Assert.Contains("const int retries = 3;", fixedSource)
    Assert.Contains("const Status status = Status.Active;", fixedSource)
    Assert.Contains("const double ratio = 1.5, half = 0.5;", fixedSource)
    // the interpolation folds only once its holes are const: the sweep's next pass
    Assert.Contains("""var interpolated = $"{schema}-{retries}";""", fixedSource)
    Assert.Contains("int byIn = 2;", fixedSource)
    Assert.Contains("var counter = 0;", fixedSource)
    Assert.Contains("int byRef = 1;", fixedSource)
    Assert.Contains("var fromParameter = seed;", fixedSource)
    Assert.Contains("var empty = string.Empty;", fixedSource)

// ---- CR0173 ----

[<Fact>]
let ``returns and assignments every branch performs become one conditional; the bool-literal, throw, commented and chained shapes stand down``
    ()
    =
    let source =
        csharp
            """
            using System;
            class C
            {
                int field;
                string A(bool a) { if (a) return "1"; else return "2"; }
                string B(bool a) { if (a) return "1"; return "2"; }
                string D(bool a) { if (a) { return "1"; } else { return "2"; } }
                int E(bool a, int x, int y) { if (a) return x > y ? x : y; else return y; }
                void F(bool a, Func<int> f, Func<int> g) { int v; if (a) v = f(); else v = g(); field = v; }
                bool G(bool a) { if (a) return true; return false; }
                string H(bool a) { if (a) return "1"; else throw new InvalidOperationException(); }
                string I(bool a) { if (a) return "1"; // the first
                    else return "2"; }
                string J(bool a, bool b) { if (a) return "1"; else if (b) return "2"; else return "3"; }
                string K(bool a) { if (a) return "same"; else return "same"; }
                int L(bool a, int x, int y) { if (a) return x; if (x > y) return y; return x + y; }
                string P { get; set; }
                void Q(bool a) { if (a) P = "1"; else P = "2"; }
                int? R(bool a) { if (a) return 1; else return null; }
            }
            """

    let fired = suggestCode "CR0173" source |> firedText source

    Assert.Equal<string list>(
        [
            """if (a) return "1"; else return "2";"""
            """if (a) return "1"; return "2";"""
            """if (a) { return "1"; } else { return "2"; }"""
            "if (a) return x > y ? x : y; else return y;"
            "int v; if (a) v = f(); else v = g();"
            """if (a) return "1"; else if (b) return "2"; else return "3";"""
            "if (x > y) return y; return x + y;"
            "if (a) return 1; else return null;"
        ],
        fired
    )

    let fixedSource = fixAll "CR0173" source
    Assert.Contains("""string A(bool a) { return a ? "1" : "2"; }""", fixedSource)
    Assert.Contains("""string B(bool a) { return a ? "1" : "2"; }""", fixedSource)
    Assert.Contains("""string D(bool a) { return a ? "1" : "2"; }""", fixedSource)
    Assert.Contains("return a ? (x > y ? x : y) : y;", fixedSource)
    Assert.Contains("{ var v = a ? f() : g(); field = v; }", fixedSource)
    Assert.Contains("if (a) return true; return false;", fixedSource)
    Assert.Contains("else throw new InvalidOperationException();", fixedSource)
    Assert.Contains("""if (a) P = "1"; else P = "2";""", fixedSource)
    Assert.Contains("int? R(bool a) { return a ? 1 : null; }", fixedSource)
    Assert.Contains("""string J(bool a, bool b) { return a ? "1" : b ? "2" : "3"; }""", fixedSource)

[<Fact>]
let ``an else-if chain every link of which returns or assigns one target is a conditional ladder, one arm a line when long``
    ()
    =
    let source =
        csharp
            """
            using System;
            using System.Linq.Expressions;
            static class C
            {
                static string Size(int n)
                {
                    if (n < 10) return "small";
                    else if (n < 100) return "medium";
                    return "large";
                }
                static Expression ParseExpression(Type propertyType, string value)
                {
                    if (propertyType == typeof(short) || propertyType == typeof(short?)) return Expression.Constant(short.Parse(value), propertyType);
                    else if (propertyType == typeof(int) || propertyType == typeof(int?)) return Expression.Constant(int.Parse(value), propertyType);
                    else if (propertyType == typeof(string)) return Expression.Constant(value, propertyType);
                    else return Expression.Constant(false);
                }
                static string Describe(int temperatureInCelsius)
                {
                    if (temperatureInCelsius < 0) return "freezing cold outside today";
                    else if (temperatureInCelsius < 15) return "rather chilly outside today";
                    else return "pleasantly warm outside today";
                }
                static int Grade(int score)
                {
                    int g;
                    if (score > 90) g = 1; else if (score > 50) g = 2; else g = 3;
                    return g;
                }
                static string Table(int k)
                {
                    if (k == 1) return "one"; else if (k == 2) return "two"; else if (k == 3) return "three"; else return "many";
                }
                static string Thrown(int n)
                {
                    if (n < 0) return "negative"; else if (n == 0) return "zero"; else throw new ArgumentException();
                }
                static void Open(int n, ref int g)
                {
                    if (n < 0) g = 1; else if (n == 0) g = 2;
                }
                static string Long(int n)
                {
                    if (n < 0) { Console.WriteLine(); return "negative"; } else if (n == 0) return "zero"; else return "positive";
                }
            }
            """

    let fired =
        suggestCode "CR0173" source
        |> firedText source
        |> List.map (fun s ->
            let s = s.Replace("\r\n", "\n")
            s.Substring(0, min 20 s.Length))

    // Size (the else-less tail), ParseExpression, Describe, Grade; Table is CR0002's switch,
    // Thrown throws, Open has no final else, Long has a two-statement link
    Assert.Equal<string list>(
        [
            "if (n < 10) return \""
            "if (propertyType == "
            "if (temperatureInCel"
            "int g;\n        if (s"
        ],
        fired
    )

    let fixedSource = (fixAll "CR0173" source).Replace("\r\n", "\n")
    Assert.Contains("""return n < 10 ? "small" : n < 100 ? "medium" : "large";""", fixedSource)
    Assert.Contains("var g = score > 90 ? 1 : score > 50 ? 2 : 3;", fixedSource)

    // too wide for one line: an arm a line
    Assert.Contains(
        "        return temperatureInCelsius < 0 ? \"freezing cold outside today\"\n"
        + "            : temperatureInCelsius < 15 ? \"rather chilly outside today\"\n"
        + "            : \"pleasantly warm outside today\";",
        fixedSource
    )

    // too wide for an arm a line: the condition and the value on lines of their own
    Assert.Contains(
        "        return propertyType == typeof(short) || propertyType == typeof(short?)\n"
        + "            ? Expression.Constant(short.Parse(value), propertyType)\n"
        + "            : propertyType == typeof(int) || propertyType == typeof(int?)\n"
        + "            ? Expression.Constant(int.Parse(value), propertyType)\n"
        + "            : propertyType == typeof(string)\n"
        + "            ? Expression.Constant(value, propertyType)\n"
        + "            : Expression.Constant(false);",
        fixedSource
    )

    Assert.Contains("""if (k == 1) return "one";""", fixedSource)
    Assert.Contains("else throw new ArgumentException();", fixedSource)
    Assert.Contains("if (n < 0) g = 1; else if (n == 0) g = 2;", fixedSource)
    Assert.Contains("""{ Console.WriteLine(); return "negative"; }""", fixedSource)

[<Fact>]
let ``a bare declaration right above the assigning if joins it: var only where both arms are of the declared type`` () =
    let source =
        csharp
            """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                static IEnumerable<int> F() => new[] { 1 };
                static IEnumerable<int> G() => new[] { 2 };
                static int[] Arr() => new[] { 3 };
                int Seq(bool a)
                {
                    IEnumerable<int> xs;
                    if (a) { xs = F(); } else { xs = G(); };
                    return xs.Sum();
                }
                int Wider(bool a)
                {
                    IEnumerable<int> ys;
                    if (a) ys = Arr(); else ys = Arr().Reverse().ToArray();
                    return ys.Sum();
                }
                long Long(bool a)
                {
                    long n;
                    if (a) n = 1; else n = 2;
                    return n;
                }
                string Nullable(bool a)
                {
                    string s;
                    if (a) s = "x"; else s = null;
                    return s;
                }
                int Commented(bool a)
                {
                    int c; // the count
                    if (a) c = 1; else c = 2;
                    return c;
                }
                int Apart(bool a)
                {
                    int p;
                    System.Console.WriteLine();
                    if (a) p = 1; else p = 2;
                    return p;
                }
                int Pair(bool a)
                {
                    int q, r = 0;
                    if (a) q = 1; else q = 2;
                    return q + r;
                }
                int ReadInCondition(string t)
                {
                    int k;
                    if (int.TryParse(t, out k)) k = k + 1; else k = 0;
                    return k;
                }
            }
            """

    let fixedSource = fixAll "CR0173" source
    // the stray `;` after the else block goes with the if
    Assert.Contains("var xs = a ? F() : G();", fixedSource)
    Assert.DoesNotContain(";;", fixedSource)
    // int[] arms into an IEnumerable<int> local: `var` would change the local's type
    Assert.Contains("IEnumerable<int> ys = a ? Arr() : Arr().Reverse().ToArray();", fixedSource)
    Assert.Contains("long n = a ? 1 : 2;", fixedSource)
    Assert.Contains("""string s = a ? "x" : null;""", fixedSource)
    // a comment on the declaration, a statement between, two declarators, the local
    // read in the condition: the plain assignment form, or nothing
    Assert.Contains("int c; // the count", fixedSource)
    Assert.Contains("c = a ? 1 : 2;", fixedSource)
    Assert.Contains("int p;", fixedSource)
    Assert.Contains("p = a ? 1 : 2;", fixedSource)
    Assert.Contains("int q, r = 0;", fixedSource)
    Assert.Contains("q = a ? 1 : 2;", fixedSource)
    Assert.Contains("int k;", fixedSource)
    Assert.DoesNotContain("var k", fixedSource)

[<Fact>]
let ``a conditional whose arms meet at a wider natural type than each arm converted to stands down`` () =
    let source =
        csharp
            """
            class C
            {
                object Boxed(bool a) { if (a) return 1; else return 2.0; }
                double ViaFloat(bool a, int i, float f) { if (a) return i; return f; }
                void Assigned(bool a, int i, float f) { double d; if (a) d = i; else d = f; System.Console.WriteLine(d); }
                object SameType(bool a) { if (a) return "x"; return "y"; }
                object TargetTyped(bool a) { if (a) return 1; return "x"; }
                long ToTarget(bool a, int i, long l) { if (a) return i; return l; }
            }
            """

    let fired = suggestCode "CR0173" source |> firedText source

    Assert.Equal<string list>(
        [
            """if (a) return "x"; return "y";"""
            """if (a) return 1; return "x";"""
            "if (a) return i; return l;"
        ],
        fired
    )

[<Fact>]
let ``a null arm beside a value of the return type still folds`` () =
    // getters shaped `if (c) return null; return x;` — the
    // null converts to the return type either way; the guard must not stand down
    let source =
        csharp
            """
            using System.Collections.Generic;
            interface IItem { }
            class C
            {
                IItem[] items = new IItem[0];
                Dictionary<int, IItem> map = new();
                Stack<(IItem p, int n)> stack = new();
                IItem A(int i) { if (i < 0) return null; return items[i]; }
                IItem B(int k) { if (!map.ContainsKey(k)) return null; return map[k]; }
                IItem D { get { if (stack.Count == 0) return null; return stack.Peek().p; } }
                IItem E(List<IItem> list) { if (list.Count != 0) return list[index: 0]; return null; }
            }
            """

    Assert.Equal(4, (suggestCode "CR0173" source).Length)

[<Fact>]
let ``review 2026-09-28: CR0173 keeps an assignment whose target's owner or ref a condition can re-point`` () =
    let source =
        csharp
            """
            class Box { public int F; }
            class C
            {
                static Box cur = new Box();
                static bool Swap() { cur = new Box(); return true; }
                void Chain(int n) { if (Swap() && n < 0) cur.F = 1; else if (n < 10) cur.F = 2; else cur.F = 3; }
                void Two(int n) { if (Swap()) cur.F = 1; else cur.F = 2; }
                void Ref(bool c)
                {
                    int b1 = 0, b2 = 0;
                    ref int r = ref b2;
                    if ((r = ref b1) == 0 && c) r = 1; else r = 2;
                }
            }
            """

    Assert.Empty(suggestCode "CR0173" source)
