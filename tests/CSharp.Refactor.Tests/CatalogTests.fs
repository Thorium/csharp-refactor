/// The catalog and the Roslyn descriptors stay in step: every rule has a
/// descriptor, every descriptor a rule, and every message ends in its
/// category. (Microsoft.CodeAnalysis.Analyzers cannot check an F# analyzer,
/// so these tests stand in for RS1xxx.)
module CSharp.Refactor.Tests.CatalogTests

open Xunit
open CSharp.Refactor
open CSharp.Refactor.Roslyn

[<Fact>]
let ``every rule has a descriptor and every descriptor a rule`` () =
    let codes = RuleCatalog.rules |> List.map (fun r -> r.Code) |> set
    let described = Descriptors.all |> Seq.map (fun d -> d.Id) |> set
    Assert.Equal<Set<string>>(codes, described)

[<Fact>]
let ``codes are unique and well formed`` () =
    let codes = RuleCatalog.rules |> List.map (fun r -> r.Code)
    Assert.Equal(codes.Length, (set codes).Count)

    for c in codes do
        Assert.Matches(@"^CR\d{4}$", c)

[<Fact>]
let ``a priority rule is a warning and the rest are info`` () =
    for d in Descriptors.all do
        let expected =
            if RuleCatalog.isPriority d.Id then
                Microsoft.CodeAnalysis.DiagnosticSeverity.Warning
            else
                Microsoft.CodeAnalysis.DiagnosticSeverity.Info

        Assert.Equal(expected, d.DefaultSeverity)

[<Fact>]
let ``messages end with the category`` () =
    for d in Descriptors.all do
        let category = RuleCatalog.name (RuleCatalog.categoryOf d.Id)
        Assert.EndsWith($"[{category}]", d.MessageFormat.ToString())

[<Fact>]
let ``the analyzer supports exactly the catalog`` () =
    let analyzer = CSharpRefactorAnalyzer()
    let supported = analyzer.SupportedDiagnostics |> Seq.map (fun d -> d.Id) |> set
    Assert.Equal<Set<string>>(RuleCatalog.known, supported)

[<Fact>]
let ``the fix provider fixes exactly the catalog`` () =
    let provider = CSharpRefactorCodeFixProvider()
    Assert.Equal<Set<string>>(RuleCatalog.known, set provider.FixableDiagnosticIds)
