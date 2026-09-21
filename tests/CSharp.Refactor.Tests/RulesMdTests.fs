/// Rules.md and the catalog stay in step: every code has a row and a
/// section, categories and defaults match, the help link of every rule
/// lands on its heading. Ported from fsharp-refactor.
module CSharp.Refactor.Tests.RulesMdTests

open System.IO
open System.Text.RegularExpressions
open Xunit
open CSharp.Refactor

let private repoFile name =
    Path.Combine(__SOURCE_DIRECTORY__, "..", "..", name) |> File.ReadAllText

let private documentedCodes () =
    Regex.Matches(repoFile "Rules.md", @"^### (CR\d{4}) ", RegexOptions.Multiline)
    |> Seq.map (fun m -> m.Groups.[1].Value)
    |> Set.ofSeq

[<Fact>]
let ``every documented rule is in the catalog and the catalog invents none`` () =
    let documented = documentedCodes ()
    Assert.NotEmpty documented
    Assert.Equal<Set<string>>(RuleCatalog.known, documented)

[<Fact>]
let ``categories partition the rules`` () =
    let counted =
        RuleCatalog.all
        |> List.sumBy (fun c -> (RuleCatalog.codesIn (Set.singleton c)).Count)

    Assert.Equal(RuleCatalog.known.Count, counted)

[<Fact>]
let ``category names round-trip`` () =
    for category in RuleCatalog.all do
        Assert.Equal(Some category, RuleCatalog.parse (RuleCatalog.name category))

/// The table's rows: code, category, enabled, api, priority.
let private rulesTableRows () =
    Regex.Matches(repoFile "Rules.md", @"^\| (CR\d{4}) \| (\w+) \| (v?) ?\| (v?) ?\| (v?) ?\|", RegexOptions.Multiline)
    |> Seq.map (fun m ->
        m.Groups.[1].Value,
        m.Groups.[2].Value,
        m.Groups.[3].Value = "v",
        m.Groups.[4].Value = "v",
        m.Groups.[5].Value = "v")
    |> List.ofSeq

[<Fact>]
let ``Rules.md has exactly one row per catalogued rule`` () =
    let rows = rulesTableRows () |> List.map (fun (code, _, _, _, _) -> code)
    let missing = RuleCatalog.known - Set.ofList rows
    let unknown = Set.ofList rows - RuleCatalog.known

    let duplicated =
        rows |> List.countBy id |> List.filter (fun (_, n) -> n > 1) |> List.map fst

    Assert.True(missing.IsEmpty, $"Rules.md lacks a row for: %A{missing}")
    Assert.True(unknown.IsEmpty, $"Rules.md lists codes the catalog does not know: %A{unknown}")
    Assert.True(duplicated.IsEmpty, $"Rules.md lists twice: %A{duplicated}")

[<Fact>]
let ``Rules.md categories, defaults and priorities match the catalog`` () =
    for code, category, enabled, _, priority in rulesTableRows () do
        let expected = RuleCatalog.name (RuleCatalog.categoryOf code)

        Assert.True(
            System.String.Equals(category, expected, System.StringComparison.OrdinalIgnoreCase),
            $"{code}: Rules.md says {category}, the catalog says {expected}"
        )

        Assert.True((enabled = RuleCatalog.isDefaultOn code), $"{code}: Rules.md says enabled={enabled}")
        Assert.True((priority = RuleCatalog.isPriority code), $"{code}: Rules.md says priority={priority}")

/// GitHub's heading slug: lowercase, every character that is neither a
/// letter, a digit, a space nor a hyphen dropped, every space a hyphen.
let private githubSlug (heading: string) =
    Regex.Replace(heading.ToLowerInvariant(), @"[^\p{L}\p{N} -]", "").Replace(' ', '-')

[<Fact>]
let ``the help link of every rule lands on its Rules.md section`` () =
    let headings =
        Regex.Matches(repoFile "Rules.md", @"^### ((CR\d{4}) .*?)\r?$", RegexOptions.Multiline)
        |> Seq.map (fun m -> m.Groups.[2].Value, githubSlug m.Groups.[1].Value)
        |> List.ofSeq

    Assert.NotEmpty headings

    let wrong =
        headings
        |> List.filter (fun (code, slug) -> RuleCatalog.anchor code <> slug)
        |> List.map (fun (code, slug) -> $"{code}: the link says #{RuleCatalog.anchor code}, the heading is #{slug}")

    Assert.True(wrong.IsEmpty, String.concat "\n" wrong)

    Assert.Equal(
        "https://github.com/Thorium/csharp-refactor/blob/main/Rules.md#cr0103--cosmetic",
        RuleCatalog.helpUri "CR0103"
    )

/// Every performance claim is measured: a rule of the category has a
/// benchmark class in benchmarks/PerfClaims (`CRnnnn_…`) or a stated
/// reason in LaterClaims.cs why no runtime pair exists (`//   CRnnnn —`).
/// A new performance rule missing from both is visible here, not in a
/// review two milestones on.
[<Fact>]
let ``every performance rule has a PerfClaims pair or a stated reason`` () =
    let benchmarks =
        Directory.GetFiles(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "benchmarks", "PerfClaims"), "*.cs")
        |> Seq.map File.ReadAllText
        |> String.concat "\n"

    let measured =
        Regex.Matches(benchmarks, @"public (?:partial )?class (CR\d{4})_")
        |> Seq.map (fun m -> m.Groups.[1].Value)
        |> set

    let excused =
        Regex.Matches(benchmarks, @"^//   (CR\d{4}) — ", RegexOptions.Multiline)
        |> Seq.map (fun m -> m.Groups.[1].Value)
        |> set

    let unmeasured =
        RuleCatalog.rules
        |> List.filter (fun r -> r.Category = RuleCatalog.Category.Performance)
        |> List.map (fun r -> r.Code)
        |> List.filter (fun c -> not (measured.Contains c || excused.Contains c))

    Assert.True(unmeasured.IsEmpty, $"performance rules without a PerfClaims pair or a stated reason: %A{unmeasured}")
    Assert.True(Set.isEmpty (Set.intersect measured excused), "a rule is both measured and excused")
