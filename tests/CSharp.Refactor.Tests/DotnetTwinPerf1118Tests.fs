/// C# twins of the analyzer hot spots the 2026-11-18 dotnet/fsharp sweep
/// found in FSharp.Refactor (DotnetSweepPerf1118Tests there). Each test
/// times a rule at N and at 4N: a linear cost stays near 4x, a cost paid
/// per finding over the whole file or the whole solution climbs toward 16x.
///
/// Tagged `Category=Timing`: a clock is only as steady as the machine, so CI
/// runs these in a process of their own (`--filter "Category=Timing"`) after
/// the rest (`--filter "Category!=Timing"`).
module CSharp.Refactor.Tests.DotnetTwinPerf1118Tests

open System
open System.Diagnostics
open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.Text
open Xunit
open CSharp.Refactor
open CSharp.Refactor.Roslyn
open CSharp.Refactor.Tests.Harness

/// Wall time of the rule modules holding `code` over `source`, the
/// compilation already bound (the sweep binds it for its error gate first),
/// and the findings of `code`.
let private timeRule (code: string) (source: string) =
    let compilation, tree = compileClean source
    let model = compilation.GetSemanticModel(tree, false)
    Rules.restrictTo (Some(Set.ofList [ code ]))

    try
        let sw = Stopwatch.StartNew()

        let found =
            Rules.all tree model (Context.forTree None compilation tree false)
            |> List.filter (fun s -> s.Code = code)

        sw.Stop()
        sw.Elapsed.TotalMilliseconds, found.Length
    finally
        Rules.restrictTo None

// ---- CR0045 (twin of FR0142) ----

let private blockingTests (n: int) =
    let classes =
        Seq.init n (fun i ->
            $"public class T{i} {{ static Task<int> Load{i}() => Task.Run(() => {i}); [Fact] public void Test{i}() {{ var x = Load{i}().Result; Assert.Equal(x, x); }} }}\n")
        |> String.concat ""

    "using System.Threading.Tasks;\nusing Xunit;\n" + classes

[<Fact>]
[<Trait("Category", "Sweep1118")>]
[<Trait("Category", "Timing")>]
let ``CR0045 cost grows linearly with the blocking tests it converts`` () =
    // FR0142 twin: each converted test's speculative check re-binds the whole file
    // (25 -> 100 tests measured 293 -> 3473 ms, x11.8; 25 tests in a 1600-line file 6959 ms)
    timeRule "CR0045" (blockingTests 5) |> ignore
    let small, smallHits = timeRule "CR0045" (blockingTests 25)
    let large, largeHits = timeRule "CR0045" (blockingTests 100)
    Assert.Equal(25, smallHits)
    Assert.Equal(100, largeHits)
    let ratio = large / max small 1.0

    Assert.True(
        ratio < 6.0,
        $"CR0045: 25 tests {small:F0} ms, 100 tests {large:F0} ms (x{ratio:F1}); a linear rule stays near x4"
    )

// ---- CR0069 (twin of FR0092) ----

let private add (sol: Solution) (pid: ProjectId) (name: string) (path: string) (text: string) =
    sol.AddDocument(DocumentId.CreateNewId pid, name, SourceText.From text, filePath = path)

/// A library and a test project of `files` test files, each asserting on
/// `literals` string literals: the corpus CR0069's `mentions.InTests`
/// searches for a message a test might read back.
let private solutionWithTests (files: int) (literals: int) =
    let ws = new AdhocWorkspace()

    let info name =
        ProjectInfo.Create(
            ProjectId.CreateNewId(),
            VersionStamp.Default,
            name,
            name,
            LanguageNames.CSharp,
            metadataReferences = metadataReferences
        )

    let lib = info "Lib"
    let tests = info "LibTests"
    let mutable sol = ws.CurrentSolution.AddProject(lib).AddProject(tests)
    sol <- add sol lib.Id "Lib.cs" @"C:\r\Lib\Lib.cs" "class L { }"

    for f in 0 .. files - 1 do
        let body =
            Seq.init literals (fun k ->
                $"        Assert.Equal(\"expected value number {k} of file {f} with some padding text\", Get());\n")
            |> String.concat ""

        let src =
            $"using Xunit;\npublic class C{f}Tests {{\n    static string Get() => \"\";\n    [Fact] public void T() {{\n{body}    }}\n}}\n"

        sol <- add sol tests.Id $"C{f}Tests.cs" $@"C:\r\LibTests\C{f}Tests.cs" src

    sol, sol.GetProject lib.Id

/// Time `questions` misses (the full-scan case: a message no test reads)
/// after the corpus is read.
let private timeMentions (files: int) (questions: int) =
    let sol, lib = solutionWithTests files 200
    let m = References.mentions sol lib
    Assert.False(m.InTests "warm up: never present")
    let sw = Stopwatch.StartNew()

    for i in 0 .. questions - 1 do
        m.InTests $"Operation went wrong at stage {i}" |> ignore

    sw.Stop()
    sw.Elapsed.TotalMilliseconds

[<Fact>]
[<Trait("Category", "Sweep1118")>]
[<Trait("Category", "Timing")>]
let ``CR0069 does not rescan the test corpus for every message`` () =
    // FR0092 twin: InTests runs IndexOf over every literal of every test file per finding,
    // so findings x corpus (400 files x 500 literals: 400 questions 2234 ms, 1600 questions 12381 ms)
    timeMentions 5 5 |> ignore
    let small = timeMentions 100 100
    let large = timeMentions 400 400
    let ratio = large / max small 1.0

    Assert.True(
        ratio < 6.0,
        $"CR0069 InTests: 100 files x 100 messages {small:F0} ms, 400 x 400 {large:F0} ms (x{ratio:F1}); an index read once stays near x4"
    )

// ---- CR0083: the scan of inactive `#if` code for a late write ----

/// `properties` init candidates beside `inactiveLines` lines of code an
/// `#if` leaves out, none of which names one of them.
let private optionsBesideInactiveCode (properties: int) (inactiveLines: int) =
    let declared =
        Seq.init properties (fun i -> $"    public int Port{i} {{ get; set; }}\n")
        |> String.concat ""

    let initialized =
        Seq.init properties (fun i -> $"Port{i} = {i}") |> String.concat ", "

    let inactive =
        Seq.init inactiveLines (fun i ->
            $"    static int Legacy{i}(int[] values, string name) {{ var total = values.Length + name.Length; total += {i}; return total; }}\n")
        |> String.concat ""

    $"class Options\n{{\n{declared}}}\nstatic class Make\n{{\n    public static Options Create() => new Options {{ {initialized} }};\n#if NEVER_DEFINED\n{inactive}#endif\n}}\n"

[<Fact>]
[<Trait("Category", "Sweep1118")>]
[<Trait("Category", "Timing")>]
let ``CR0083 does not run a pattern over the inactive code for every property`` () =
    // a write pattern per candidate over all inactive text: 346 ms per property on 5 MB
    // (200 properties beside 2 MB: about 30 s); a substring check first is 0.3 ms
    timeRule "CR0083" (optionsBesideInactiveCode 5 10) |> ignore
    let ms, hits = timeRule "CR0083" (optionsBesideInactiveCode 200 20000)
    Assert.Equal(200, hits)
    Assert.True(ms < 5000.0, $"CR0083: 200 properties beside 2 MB of inactive code took {ms:F0} ms")
