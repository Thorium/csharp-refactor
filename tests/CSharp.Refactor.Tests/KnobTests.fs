/// The rules' tunables: `--create-config` writes each at "this build's
/// default", taken from `RuleCatalog.knobs`, while each rule reads its own
/// from the `RuleContext.knobInt`/`knobBool` call that asks for it. Two
/// places, so they could drift - and a config stating a wrong default is
/// worse than one stating none, since a user who keeps the line has pinned
/// a value they never chose. The source is read, because the fallback a
/// rule passes cannot be observed from outside it. Then the three knobs
/// that only DESIGN.md had: CR0040 `sync_swap`, CR0125
/// `drop_legacy_protocols` and CR0109 `per_call`.
module CSharp.Refactor.Tests.KnobTests

open System.IO
open System.Text.RegularExpressions
open Xunit
open CSharp.Refactor
open CSharp.Refactor.Tests.Harness

let private sources =
    Directory.GetFiles(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "src", "CSharp.Refactor.Analyzers"), "*.fs")
    |> Array.map File.ReadAllText

/// Every knob a rule reads: (code, knob, default as written - None where
/// the fallback is not a literal, a measured floor or a file's width).
let private readByRules =
    [
        for text in sources do
            // a code constant of this file: `let Code = "CR0006"`
            let codeOf (name: string) =
                let m = Regex.Match(text, $@"let\s+(?:private\s+)?{name}\s*=\s*""(CR\d{{4}})""")
                if m.Success then m.Groups.[1].Value else name

            for m in Regex.Matches(text, @"RuleContext\.knob(?:Int|Bool)\s+ctx\s+(\w+)\s+""(\w+)""\s+([\w.]+)") do
                let written = m.Groups.[3].Value

                let fallback =
                    if Regex.IsMatch(written, @"^(\d+|true|false)$") then
                        Some written
                    else
                        None

                yield codeOf m.Groups.[1].Value, m.Groups.[2].Value, fallback

            for m in Regex.Matches(text, @"RuleContext\.wrapColumn\s+ctx\s+(\w+)") do
                yield codeOf m.Groups.[1].Value, "wrap_column", None
    ]
    |> List.distinct

let private catalogued =
    [
        for code, knobs in RuleCatalog.knobs do
            for k in knobs do
                code, k.Name, k.Default
    ]

[<Fact>]
let ``the catalogue lists exactly the knobs the rules read, at the defaults they fall back to`` () =
    Assert.NotEmpty readByRules
    let sort = List.sortBy (fun (c, k, _) -> c, k)
    Assert.Equal<(string * string * string option) list>(sort readByRules, sort catalogued)

[<Fact>]
let ``every catalogued knob belongs to a rule`` () =
    for code, _ in RuleCatalog.knobs do
        Assert.True(RuleCatalog.known.Contains code, $"{code} is catalogued as tunable but is not a rule")

[<Fact>]
let ``--create-config writes each knob under its rule, a derived default commented out`` () =
    let text = CSharp.Refactor.Tool.ConfigFile.defaultConfigText ()
    Assert.Contains("csharp_refactor.CR0006.then_at_least = 20  #", text)
    Assert.Contains("csharp_refactor.CR0040.sync_swap = false  #", text)
    Assert.Contains("# csharp_refactor.CR0023.min_elements =  #", text)
    // below the rule's own severity line
    let severity = text.IndexOf "dotnet_diagnostic.CR0006.severity"
    Assert.True(severity >= 0 && severity < text.IndexOf "csharp_refactor.CR0006.then_at_least")

let private with' (key: string) (value: string) =
    Some(FakeOptions(dict [ key, value ]) :> Microsoft.CodeAnalysis.Diagnostics.AnalyzerConfigOptions)

// ---- CR0040 sync_swap ----

[<Literal>]
let private syncSource =
    """
using System.Threading.Tasks;
class Store
{
    public Task<int> LoadAsync(int id) => Task.FromResult(id);
    public int Load(int id) => id;
    public Task SaveAsync(int id) => Task.CompletedTask;
    public void Save(int id) { }
    public Task<int> CountAsync() => Task.FromResult(0);
}
class C
{
    int A(Store s) => s.LoadAsync(1).Result;
    void B(Store s) { s.SaveAsync(2).Wait(); }
    int D(Store s) => s.CountAsync().Result;
}
"""

[<Fact>]
let ``CR0040 offers a boundary drain's synchronous sibling in the editor, and a sweep takes it under sync_swap`` () =
    let fired = suggestCode "CR0040" syncSource
    Assert.Equal(3, fired.Length)
    // A and B have a sibling: an editor-only offer; D has none: a note
    Assert.Equal(
        2,
        fired
        |> List.filter (fun s -> s.Fixes |> List.exists (fun f -> f.EditorOnly))
        |> List.length
    )

    Assert.Equal(syncSource.Replace("\r\n", "\n"), (fixAll "CR0040" syncSource).Replace("\r\n", "\n"))

    let swapped =
        fixAllWith (with' "csharp_refactor.CR0040.sync_swap" "true") "CR0040" syncSource

    Assert.Contains("int A(Store s) => s.Load(1);", swapped)
    Assert.Contains("void B(Store s) { s.Save(2); }", swapped)
    Assert.Contains("int D(Store s) => s.CountAsync().Result;", swapped)

// ---- CR0125 drop_legacy_protocols ----

[<Literal>]
let private protocolSource =
    """
using System.Net;
class C
{
#pragma warning disable SYSLIB0014
    void A() { ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11; }
    void B() { ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls11 | SecurityProtocolType.Tls12; }
    void D() { ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls11; }
#pragma warning restore SYSLIB0014
}
"""

[<Fact>]
let ``CR0125 comments a retired protocol out of the flags in the editor, and a sweep does under drop_legacy_protocols``
    ()
    =
    let fired = suggestCode "CR0125" protocolSource
    Assert.Equal(3, fired.Length)

    Assert.Equal(
        2,
        fired
        |> List.filter (fun s -> s.Fixes |> List.exists (fun f -> f.EditorOnly))
        |> List.length
    )

    let dropped =
        fixAllWith (with' "csharp_refactor.CR0125.drop_legacy_protocols" "true") "CR0125" protocolSource

    Assert.Contains(
        "ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 /* | SecurityProtocolType.Tls11 */;",
        dropped
    )

    Assert.Contains(
        "ServicePointManager.SecurityProtocol = /* SecurityProtocolType.Tls11 | */ SecurityProtocolType.Tls12;",
        dropped
    )

    // alone, the protocol is the whole setting: a note
    Assert.Contains("ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls11; }", dropped)

// ---- CR0109 per_call ----

[<Fact>]
let ``CR0109 per_call false keeps only the hoists out of loops and per-element lambdas`` () =
    let source =
        csharp
            """
            using System.Collections.Generic;
            using System.Linq;
            using System.Text.RegularExpressions;
            class C
            {
                bool Once(string s) => Regex.IsMatch(s, @"^\d+$");
                int Looped(List<string> xs) { var n = 0; foreach (var x in xs) if (Regex.IsMatch(x, @"^\w+$")) n++; return n; }
                int Mapped(List<string> xs) => xs.Count(x => Regex.IsMatch(x, @"^\s+$"));
            }
            """

    Assert.Equal(3, (suggestCode "CR0109" source).Length)

    let loopsOnly =
        suggestCodeWith (with' "csharp_refactor.CR0109.per_call" "false") "CR0109" source

    Assert.Equal(2, loopsOnly.Length)
    Assert.DoesNotContain(loopsOnly, fun s -> source.Substring(s.Span.Start, s.Span.Length).Contains @"^\d+$")

// ---- the review's reproductions: no swap or retirement where it would mean something else ----

[<Fact>]
let ``CR0040 swaps only to a sibling the call binds to, and never under a catch of AggregateException`` () =
    // a derived type's better overload and a local function of the same name
    // would take the rewritten call; `.Result` wraps the fault the catch
    // expects, the sibling throws it bare
    let source =
        csharp
            """
            using System;
            using System.Threading.Tasks;
            class Base
            {
                public Task<string> LoadAsync(int id) => Task.FromResult("async");
                public string Load(int id) => "base";
            }
            class Derived : Base { public string Load(object id) => "derived"; }
            class Svc
            {
                public Task<string> FetchAsync(int id) => Task.FromResult("async");
                public string Fetch(int id) => "member";
                public Task<string> ReadAsync(int id) => Task.FromResult("async");
                public string Read(int id) => "read";
                string A(Derived d) => d.LoadAsync(1).Result;
                string B() { string Fetch(int y) => "local"; return FetchAsync(1).Result + Fetch(0); }
                string D(int x) { try { return ReadAsync(x).Result; } catch (AggregateException) { return "handled"; } }
                string E(Base b) => b.LoadAsync(2).Result;
            }
            """

    let swapped =
        fixAllWith (with' "csharp_refactor.CR0040.sync_swap" "true") "CR0040" source

    Assert.Contains("string A(Derived d) => d.LoadAsync(1).Result;", swapped)
    Assert.Contains("return FetchAsync(1).Result + Fetch(0);", swapped)
    Assert.Contains("try { return ReadAsync(x).Result; }", swapped)
    // the plain case still swaps
    Assert.Contains("string E(Base b) => b.Load(2);", swapped)

[<Fact>]
let ``CR0125 retires a protocol only from the flags being switched on, never from a mask or a test`` () =
    let source =
        csharp
            """
            using System.Net;
            class C
            {
            #pragma warning disable SYSLIB0014
                void Off() { ServicePointManager.SecurityProtocol &= ~(SecurityProtocolType.Ssl3 | SecurityProtocolType.Tls11); }
                bool Test() => (ServicePointManager.SecurityProtocol & (SecurityProtocolType.Tls | SecurityProtocolType.Tls11)) != 0;
                void On() { ServicePointManager.SecurityProtocol = (SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11); }
            #pragma warning restore SYSLIB0014
            }
            """

    let dropped =
        fixAllWith (with' "csharp_refactor.CR0125.drop_legacy_protocols" "true") "CR0125" source

    Assert.Contains("&= ~(SecurityProtocolType.Ssl3 | SecurityProtocolType.Tls11);", dropped)
    Assert.Contains("& (SecurityProtocolType.Tls | SecurityProtocolType.Tls11)) != 0;", dropped)
    Assert.Contains("= (SecurityProtocolType.Tls12 /* | SecurityProtocolType.Tls11 */);", dropped)
