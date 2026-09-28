module CSharp.Refactor.Tests.RegexTests

open Xunit
open CSharp.Refactor.Tests.Harness

// ---- CR0107 / CR0108 ----

[<Fact>]
let ``an invalid pattern is noted; plain-text patterns become string operations`` () =
    let source =
        csharp
            """
            using System.Text.RegularExpressions;
            class C
            {
                bool A(string s) => Regex.IsMatch(s, "(unclosed");
                bool B(string s) => Regex.IsMatch(s, "^abc");
                bool D(string s) => Regex.IsMatch(s, "abc");
                bool E(string s) => Regex.IsMatch(s, "abc$");
                bool F(string s) => Regex.IsMatch(s, @"\d+");
                string G(string s) => Regex.Replace(s, "abcd", "x");
                string H(string s) => Regex.Replace(s, "abcd", "$1");
                bool I(string s) => Regex.IsMatch(s, "a b", RegexOptions.IgnorePatternWhitespace);
                bool J(string s) => Regex.IsMatch(s, "[");
                int K(string s) => Regex.Matches(s, "ON CONFLICT").Count;
                string[] L(string s) => Regex.Split(s, ", ");
                int M(string s) => Regex.Matches(s, @"\d+").Count;
                bool N(string s) => Regex.Matches(s, "b").Count > 0;
                bool O(string s) => Regex.Matches(s, "b").Count == 0;
                bool P(string s) => 1 <= Regex.Matches(s, "^b").Count;
                bool Q(string s) => Regex.Match(s, "b").Success;
                bool R(string s) => !Regex.Match(s, "b").Success;
            }
            """

    Assert.Equal<string list>([ "\"(unclosed\""; "\"[\"" ], firedText source (suggestCode "CR0107" source))
    let fired = suggestCode "CR0108" source
    // B, D, G, K, L, N..R: H carries a substitution, I an option, M a real pattern
    assertFired 10 source fired
    let fixedSource = fixAll "CR0108" source
    Assert.Contains("""s.StartsWith("abc", StringComparison.Ordinal)""", fixedSource)
    Assert.Contains("""s.Contains("abc")""", fixedSource)
    Assert.Contains("""s.Replace("abcd", "x")""", fixedSource)
    Assert.Contains("""Regex.IsMatch(s, "abc$")""", fixedSource)
    Assert.Contains("""Regex.Replace(s, "abcd", "$1")""", fixedSource)
    Assert.Contains("""s.AsSpan().Count("ON CONFLICT")""", fixedSource)
    Assert.Contains("""bool N(string s) => s.Contains("b");""", fixedSource)
    Assert.Contains("""bool O(string s) => !s.Contains("b");""", fixedSource)
    Assert.Contains("""bool P(string s) => s.StartsWith("b", StringComparison.Ordinal);""", fixedSource)
    Assert.Contains("""bool Q(string s) => s.Contains("b");""", fixedSource)
    Assert.Contains("""bool R(string s) => !s.Contains("b");""", fixedSource)
    Assert.Contains("""s.Split(", ")""", fixedSource)
    Assert.Contains("""Regex.Matches(s, @"\d+").Count""", fixedSource)
    Assert.Contains("using System;", fixedSource)

[<Fact>]
let ``a compound subject is parenthesised as the string operation's receiver`` () =
    // `s ?? "".Replace(…)` would compile and replace nothing; a C# 12 alias
    // of a tuple type has no Name, which the test-file probe must survive
    let source =
        csharp
            """
            using System.Text.RegularExpressions;
            using Point = (int X, int Y);
            class C
            {
                string A(string? s) => Regex.Replace(s ?? "", "-", "_");
                string B(string a, string b) => Regex.Replace(a + b, "-", "_");
                bool D(string a, string b) => Regex.IsMatch(a + b, "x");
                string[] E(string? s) => Regex.Split(s ?? "", ",");
            }
            """

    let fixedSource = fixAll "CR0108" source
    Assert.Contains("""(s ?? "").Replace("-", "_")""", fixedSource)
    Assert.Contains("""(a + b).Replace("-", "_")""", fixedSource)
    Assert.Contains("""(a + b).Contains("x")""", fixedSource)
    Assert.Contains("""(s ?? "").Split(",")""", fixedSource)

[<Fact>]
let ``CR0108 keeps Regex.Replace when the replacement's $$ escape means one dollar sign`` () =
    // Regex.Replace("99 USD", "USD", "$$") is "99 $"; string.Replace would write "99 $$"
    let source =
        csharp
            """
            using System.Text.RegularExpressions;
            class PriceLabel
            {
                string ToDollarSign(string label) => Regex.Replace(label, "USD", "$$");
            }
            """

    Assert.Empty(suggestCode "CR0108" source)

// ---- CR0109 ----

[<Fact>]
let ``a regex built per call is hoisted to a generated regex, the type made partial; names come from the binder, the pattern or the member``
    ()
    =
    let source =
        csharp
            """
            using System.Text.RegularExpressions;
            class C
            {
                /// <summary>Digits.</summary>
                bool A(string s) => Regex.IsMatch(s, @"\d+") && Regex.IsMatch(s, @"\w+");
                string B(string s) => Regex.Replace(s, @"\s+", " ");
                bool D(string s) => new Regex("^x+", RegexOptions.IgnoreCase).IsMatch(s);
                bool E(string s) { var codes = Regex.Matches(s, @"Error Code (\d+)"); return codes.Count > 0; }
                string F(string s) => Regex.Replace(s, "Error Code", "");
                static readonly Regex Once = new Regex("on.e");
                bool G(string s) => Regex.IsMatch(s, "on.e");
                bool H(string s) { var regex = Regex.IsMatch(s, @"\d+"); var again = Regex.IsMatch(s, @"\d+"); return regex || again; }
            }
            """

    let fired = suggestCode "CR0109" source
    // A twice, B, D, E, G, H twice; F is CR0108's (plain text: no hoist, a string operation)
    assertFired 8 source fired
    Assert.True(fired |> List.forall (fun s -> not s.Fixes.IsEmpty))
    let fixedSource = fixAllAllowing [ "CS8795" ] None "CR0109" source
    Assert.Contains("partial class C", fixedSource)

    // above the doc comment; the second regex of A takes the numbered name
    Assert.Contains(
        csharp
            """
            [GeneratedRegex(@"\d+")]
                private static partial Regex ARegex();

                [GeneratedRegex(@"\w+")]
                private static partial Regex ARegex2();

                /// <summary>Digits.</summary>
                bool A(string s) => ARegex().IsMatch(s) && ARegex2().IsMatch(s);
            """,
        normalize fixedSource
    )

    Assert.Contains("""string B(string s) => BRegex().Replace(s, " ");""", fixedSource)

    Assert.Contains(
        csharp
            """
            [GeneratedRegex("^x+", RegexOptions.IgnoreCase)]
                private static partial Regex DRegex();
            """,
        normalize fixedSource
    )

    Assert.Contains("bool D(string s) => DRegex().IsMatch(s);", fixedSource)
    // bound to a local: named after it
    Assert.Contains("var codes = CodesRegex().Matches(s);", fixedSource)
    Assert.Contains("""Regex.Replace(s, "Error Code", "")""", fixedSource)
    // a field the type already holds for the pattern is used, and one pattern gets one member
    Assert.Contains("bool G(string s) => Once.IsMatch(s);", fixedSource)
    Assert.Contains("var regex = ARegex().IsMatch(s); var again = ARegex().IsMatch(s);", fixedSource)
    Assert.DoesNotContain("HRegex", fixedSource)

[<Fact>]
let ``CR0109 names a hoist after its member before the pattern's words, which lose their escapes`` () =
    // `\bError\b` must not give `BerrorRegex`: the escape's letter must not
    // stick to the word, nor the word be lower-cased. A predicate's `Is` goes; a name
    // taken moves to the next candidate - the whole member name, then the
    // pattern's words
    let source =
        csharp
            """
            using System.Text.RegularExpressions;
            class C
            {
                static int PostcodeRegex;
                static int ScanRegex;
                bool Check(string s) => Regex.IsMatch(s, @"\bERROR\b\s*\d+");
                bool IsPostcode(string s) => Regex.IsMatch(s, @"^[A-Z]{2}\d$");
                bool Scan(string s) => Regex.IsMatch(s, @"\bWarningCode\b\d+");
            }
            """

    let fixedSource = fixAllAllowing [ "CS8795" ] None "CR0109" source
    Assert.Contains("bool Check(string s) => CheckRegex().IsMatch(s);", fixedSource)
    Assert.Contains("bool IsPostcode(string s) => IsPostcodeRegex().IsMatch(s);", fixedSource)
    Assert.Contains("bool Scan(string s) => WarningCodeRegex().IsMatch(s);", fixedSource)
    Assert.DoesNotContain("Berror", fixedSource)

[<Fact>]
let ``CR0109 never takes a name a base type or the type itself holds`` () =
    // a field named like an inherited one hides it, and `Legacy`'s reference
    // would silently test the new pattern; a member named like its type is
    // CS0542
    let source =
        csharp
            """
            using System.Text.RegularExpressions;
            class BaseValidator { protected static readonly Regex PostcodeRegex = new Regex("^[A-Z]{2}$"); }
            class Validator : BaseValidator
            {
                bool IsPostcode(string s) => Regex.IsMatch(s, "^[0-9]{5}$");
                bool Legacy(string s) => PostcodeRegex.IsMatch(s);
            }
            class EmailRegex
            {
                bool IsEmail(string s) => Regex.IsMatch(s, "^[^@]+@[^@]+$");
            }
            """

    let fixedSource = fixAllAllowing [ "CS8795" ] None "CR0109" source
    Assert.Contains("bool IsPostcode(string s) => IsPostcodeRegex().IsMatch(s);", fixedSource)
    Assert.Contains("bool Legacy(string s) => PostcodeRegex.IsMatch(s);", fixedSource)
    Assert.DoesNotContain("Regex PostcodeRegex()", fixedSource)
    Assert.Contains("bool IsEmail(string s) => IsEmailRegex().IsMatch(s);", fixedSource)

[<Fact>]
let ``CR0109 places a hoisted field above the static initializer that reaches its member`` () =
    let source =
        csharp
            """
            using System.Text.RegularExpressions;
            class Table<T>
            {
                static readonly string[] Parts = Split("a,b");

                static string[] Split(string s) => Regex.Split(s, ",[ ]*");
            }
            """

    let fixedSource = fixAll "CR0109" source
    let field = fixedSource.IndexOf "private static readonly Regex"
    Assert.True(field > 0, fixedSource)
    Assert.True(field < fixedSource.IndexOf "static readonly string[] Parts", fixedSource)

// ---- CR0110 ----

[<Fact>]
let ``an HttpClient per call and a factory per iteration are noted`` () =
    let source =
        csharp
            """
            using System.Net.Http;
            using System.Security.Cryptography;
            class C
            {
                static readonly HttpClient Shared = new HttpClient();
                void A() { var c = new HttpClient(); }
                void B(byte[][] xs) { foreach (var x in xs) { using var md5 = MD5.Create(); } }
                void D(byte[] x) { using var md5 = MD5.Create(); }
            }
            """

    Assert.Equal<string list>([ "new HttpClient()"; "MD5.Create()" ], firedText source (suggestCode "CR0110" source))

[<Fact>]
let ``CR0110 leaves the payment gateway's HttpClient alone when a Lazy builds it once`` () =
    // the lambda is a factory Lazy<T> runs a single time, not a per-call construction
    let source =
        csharp
            """
            using System;
            using System.Net.Http;
            using System.Threading.Tasks;
            class PaymentGateway
            {
                static readonly Lazy<HttpClient> Client =
                    new Lazy<HttpClient>(() => new HttpClient { BaseAddress = new Uri("https://payments.example.com/") });

                public Task<HttpResponseMessage> Charge(HttpContent payment) => Client.Value.PostAsync("charges", payment);
            }
            """

    Assert.Empty(suggestCode "CR0110" source)

// ---- CR0111 ----

[<Fact>]
let ``a hand-joined path is noted with evidence, a route, a key or an escape is not`` () =
    let source =
        csharp
            """
            using System;
            class C
            {
                string A(string dir, string file) => dir + "\\" + file;
                string B(string root, string name) => root + "/" + name + ".txt";
                string D(string id) => "/img/" + id;
                string E(string result, char c) => result + "\\" + c;
                string F(string owner) => "https://github.com/" + owner + "/x";
                bool G(string dir, string n, string key) => dir + "/" + n == key;
                string H(string p) => "./" + p;
                string I(string a, string b) => a + "/" + b;
            }
            """

    Assert.Equal<string list>(
        [ """dir + "\\" + file"""; "root + \"/\" + name + \".txt\"" ],
        firedText source (suggestCode "CR0111" source)
    )

[<Fact>]
let ``a pattern the engine does not support under its options is not provable and keeps the rest of the file`` () =
    let source =
        csharp
            """
            using System.Text.RegularExpressions;
            class C
            {
                bool A(string s) => new Regex(@"(a)\1", RegexOptions.NonBacktracking).IsMatch(s);
                bool B(string s) => Regex.IsMatch(s, @"\d+");
            }
            """

    Assert.Empty(suggestCode "CR0107" source)
    Assert.Equal<string list>([ "Regex.IsMatch(s, @\"\d+\")" ], firedText source (suggestCode "CR0109" source))
