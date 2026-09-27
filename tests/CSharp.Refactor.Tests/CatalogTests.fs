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

// ---- --codes skips rule modules by the codes they declare ----

/// Each rule module's `Code` literals, read from its source.
let private declaredInSource () =
    let dir =
        System.IO.Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "src", "CSharp.Refactor.Analyzers")

    System.IO.Directory.GetFiles(dir, "*.fs")
    |> Array.choose (fun file ->
        let text = System.IO.File.ReadAllText file

        let header =
            System.Text.RegularExpressions.Regex.Match(
                text,
                @"^module CSharp\.Refactor\.(\w+)",
                System.Text.RegularExpressions.RegexOptions.Multiline
            )

        if header.Success then
            let literal =
                System.Text.RegularExpressions.Regex(
                    @"let\s+(?:private\s+|internal\s+)?\w*Code\w*\s*=\s*""(CR\d{4})"""
                )

            let codes =
                literal.Matches text |> Seq.map (fun m -> m.Groups.[1].Value) |> Set.ofSeq

            // a code quoted anywhere else in the module - outside those
            // literals and comments - is one the map would not see
            let rest =
                System.Text.RegularExpressions.Regex.Replace(literal.Replace(text, ""), @"//[^\n]*", "")

            let stray =
                System.Text.RegularExpressions.Regex.Matches(rest, @"""(CR\d{4})""")
                |> Seq.map (fun m -> m.Groups.[1].Value)
                |> Set.ofSeq

            Some(header.Groups.[1].Value, (codes, stray))
        else
            None)
    |> Map.ofArray

[<Fact>]
let ``every typed rule module declares the codes it reports, as literals the tool's --codes filter reads`` () =
    let sources = declaredInSource ()

    for name, _ in Rules.typedNamed do
        let reflected = Rules.moduleCodes.Value.[name]
        Assert.False(reflected.IsEmpty, $"{name}: no Code literal found by reflection")

        match sources.TryFind name with
        | Some(codes, stray) ->
            Assert.True(Set.isSubset codes reflected, $"{name}: source {codes} vs reflected {reflected}")
            Assert.True(stray.IsEmpty, $"{name} quotes {stray} outside a Code literal: --codes could skip it")
        | None -> failwith $"no source file declares module {name}"

    // every catalogued rule belongs to some typed module or a parse-only one
    let covered =
        Rules.typedNamed
        |> List.map (fun (n, _) -> Rules.moduleCodes.Value.[n])
        |> Set.unionMany

    let missing = Set.difference RuleCatalog.known covered
    Assert.True(missing.IsEmpty, $"catalogued codes no typed module declares: {missing}")

[<Fact>]
let ``a run restricted to other codes skips the module, and the restriction stays in its own flow`` () =
    let source =
        csharp
            """
            using System.Collections.Generic;
            class C
            {
                int A(Dictionary<string, int> d) { var t = 0; foreach (var k in d.Keys) { t += d[k]; } return t; }
            }
            """

    Assert.NotEmpty(Harness.suggestCode "CR0032" source)
    Rules.restrictTo (Some(set [ "CR0023" ]))

    try
        Assert.Empty(Harness.suggestCode "CR0032" source)
        // work this flow starts inherits it: Roslyn's analyzer driver
        Assert.Empty(System.Threading.Tasks.Task.Run(fun () -> Harness.suggestCode "CR0032" source).Result)

        // work in another flow - a test class beside a tool run - keeps
        // every rule
        let other = ref []

        let t =
            System.Threading.Thread(fun () -> other.Value <- Harness.suggestCode "CR0032" source)

        do
            use _ = System.Threading.ExecutionContext.SuppressFlow()
            t.Start()

        t.Join()
        Assert.NotEmpty other.Value
    finally
        Rules.restrictTo None

    Assert.NotEmpty(Harness.suggestCode "CR0032" source)
