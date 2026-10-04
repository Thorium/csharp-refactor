/// The apply tool's pieces: the command line, the config-file writer, the
/// target resolution, and one end-to-end sweep over a synthetic project
/// through MSBuildWorkspace (serialized: MSBuild registration and the
/// console are process-wide).
module CSharp.Refactor.Tests.ToolTests

open System
open System.IO
open Xunit
open CSharp.Refactor.Tool
open CSharp.Refactor.Tool.Options
open Microsoft.CodeAnalysis.CSharp

// ---- options ----

[<Fact>]
let ``a bare path is the target and flags combine in any order`` () =
    match
        parseArgs
            [|
                "--dry-run"
                "X.csproj"
                "--codes"
                "cr0090,CR0103"
                "--categories"
                "cosmetic"
            |]
    with
    | Ok o ->
        Assert.Equal("X.csproj", o.Target)
        Assert.True o.DryRun
        Assert.Equal<Set<string>>(set [ "CR0103" ], o.Codes.Value)
        Assert.Equal<Set<string>>(set [ "CR0090"; "CR0103" ], o.ExplicitCodes.Value)
    | Error e -> failwith e

[<Fact>]
let ``an unknown code, a second target and a value-less flag are named`` () =
    let err args =
        match parseArgs args with
        | Error e -> e
        | Ok _ -> failwith "expected an error"

    Assert.Contains("not a rule code: CR9999", err [| "X.csproj"; "--codes"; "CR9999" |])
    Assert.Contains("second target", err [| "A.csproj"; "B.csproj" |])
    Assert.Contains("needs a value", err [| "X.csproj"; "--report" |])
    Assert.Contains("not a category", err [| "X.csproj"; "--categories"; "style" |])

[<Fact>]
let ``notes only implies a dry run`` () =
    match parseArgs [| "X.csproj"; "--notes"; "only" |] with
    | Ok o ->
        Assert.True o.NotesOnly
        Assert.True o.DryRun
    | Error e -> failwith e

[<Fact>]
let ``--define takes every spelling, lists and repeats, and keeps each symbol once`` () =
    let definesOf args =
        match parseArgs args with
        | Ok o -> o.Defines
        | Error e -> failwith e

    Assert.Equal<string list>([ "LOCAL_BUILD" ], definesOf [| "X.csproj"; "--define"; "LOCAL_BUILD" |])
    Assert.Equal<string list>([ "LOCAL_BUILD" ], definesOf [| "X.csproj"; "--define:LOCAL_BUILD" |])
    Assert.Equal<string list>([ "LOCAL_BUILD" ], definesOf [| "-d:LOCAL_BUILD"; "X.csproj" |])
    Assert.Equal<string list>([ "A"; "B"; "_C1" ], definesOf [| "X.csproj"; "--define"; "A;B, _C1" |])

    Assert.Equal<string list>(
        [ "A"; "B"; "C" ],
        definesOf [| "X.csproj"; "--define"; "A"; "-d:B;A"; "--define:C"; "--dry-run" |]
    )

    Assert.Empty(definesOf [| "X.csproj" |])

[<Fact>]
let ``--define refuses a symbol #if cannot test, and a missing value`` () =
    let err args =
        match parseArgs args with
        | Error e -> e
        | Ok _ -> failwith "expected an error"

    Assert.Contains("'1ABC' is not a preprocessor symbol", err [| "X.csproj"; "--define"; "1ABC" |])
    Assert.Contains("'LOCAL-BUILD' is not a preprocessor symbol", err [| "X.csproj"; "--define:A;LOCAL-BUILD" |])
    Assert.Contains("'A B' is not a preprocessor symbol", err [| "X.csproj"; "-d:A B" |])
    Assert.Contains("'--define' needs a value after it", err [| "X.csproj"; "--define" |])
    Assert.Contains("'--define' needs a value after it", err [| "X.csproj"; "--define"; "--dry-run" |])
    Assert.Contains("'--define' needs a value after it", err [| "X.csproj"; "--define:" |])
    Assert.Contains("'--define' needs a value after it", err [| "X.csproj"; "-d:;" |])

// ---- config file ----

let private tempDir () =
    let dir =
        Path.Combine(Path.GetTempPath(), "csref-tests", Guid.NewGuid().ToString "N")

    Directory.CreateDirectory dir |> ignore
    dir

[<Fact>]
let ``create-config writes a block with every rule and keeps existing keys`` () =
    let dir = tempDir ()
    let path = Path.Combine(dir, ".editorconfig")

    File.WriteAllText(
        path,
        csharp
            """
            root = true
            [*.cs]
            dotnet_diagnostic.CR0103.severity = none

            """
    )

    match ConfigFile.writeInto dir with
    | Ok written ->
        Assert.Equal(path, written)
        let text = File.ReadAllText path
        Assert.Contains(ConfigFile.Marker, text)
        Assert.Contains("dotnet_diagnostic.CR0090.severity = suggestion", text)
        // the line the file already had is kept, and not repeated
        Assert.Equal(1, (text.Split "dotnet_diagnostic.CR0103.severity").Length - 1)
        Assert.Contains("dotnet_diagnostic.CR0103.severity = none", text)
        Assert.Contains("csharp_refactor.suppressions = all", text)
    | Error e -> failwith e

    match ConfigFile.writeInto dir with
    | Error e -> Assert.Contains("already carries", e)
    | Ok _ -> failwith "a second write must refuse"

[<Fact>]
let ``csharp_refactor.defines is read from the nearest .editorconfig, as symbols and the entries that are none`` () =
    let text =
        csharp
            """
            root = true
            [*.cs]
            dotnet_diagnostic.CR0103.severity = none
            csharp_refactor.defines = LOCAL_BUILD; OTHER,bad-one  # why
            csharp_refactor.defines.nested = NOT_READ
            csharp_refactor.CR0090.defines = NOT_READ_EITHER
            """

    match ConfigFile.definesInText text with
    | Some(symbols, invalid), isRoot ->
        Assert.Equal<string list>([ "LOCAL_BUILD"; "OTHER" ], symbols)
        Assert.Equal<string list>([ "bad-one" ], invalid)
        Assert.True isRoot
    | other -> failwithf "unexpected %A" other

    Assert.Equal((None, false), ConfigFile.definesInText "[*.cs]\ncsharp_refactor.ignore_paths = gen\n")

    // nearest file wins; the walk stops at root = true
    let dir = tempDir ()
    let sub = Path.Combine(dir, "src", "App")
    Directory.CreateDirectory sub |> ignore
    File.WriteAllText(Path.Combine(dir, ".editorconfig"), "root = true\n[*]\ncsharp_refactor.defines = TOP\n")
    File.WriteAllText(Path.Combine(dir, "src", ".editorconfig"), "[*.cs]\ndotnet_diagnostic.CR0103.severity = none\n")

    match ConfigFile.definesFrom sub with
    | Some(path, symbols, []) ->
        Assert.True(Workspace.samePath (Path.Combine(dir, ".editorconfig")) path)
        Assert.Equal<string list>([ "TOP" ], symbols)
    | other -> failwithf "unexpected %A" other

    File.WriteAllText(Path.Combine(sub, ".editorconfig"), "root = true\n[*.cs]\ncsharp_refactor.defines = NEAR\n")

    match ConfigFile.definesFrom sub with
    | Some(_, symbols, _) -> Assert.Equal<string list>([ "NEAR" ], symbols)
    | other -> failwithf "unexpected %A" other

    File.WriteAllText(Path.Combine(sub, ".editorconfig"), "root = true\n")
    Assert.Equal(None, ConfigFile.definesFrom sub)

[<Fact>]
let ``defines is a reserved run-level key, never a rule, and --create-config writes it`` () =
    Assert.Contains("defines", ConfigFile.runLevelKeys)
    Assert.False(CSharp.Refactor.RuleCatalog.known.Contains "DEFINES")

    for key in ConfigFile.runLevelKeys do
        Assert.False(CSharp.Refactor.RuleCatalog.known.Contains(key.ToUpperInvariant()))

    match parseArgs [| "X.csproj"; "--codes"; "defines" |] with
    | Error e -> Assert.Contains("not a rule code: DEFINES", e)
    | Ok _ -> failwith "defines is no rule code"

    let block = ConfigFile.defaultConfigText ()
    Assert.Contains("\ncsharp_refactor.defines =", block.Replace("\r\n", "\n"))

[<Fact>]
let ``a script's #r under an #if the run does not define is named`` () =
    let source =
        csharp
            """
            #if LOCAL_BUILD
            #r "../bin/Lib.dll"
            #else
            #r "nuget: Lib, 1.0.0"
            #endif
            #if !NOT_THIS
            #r "other.dll"
            #endif
            #if DEBUG // a comment
            using System;
            #endif
            #if true
            #r "always.dll"
            #endif
            Console.WriteLine();
            """

    Assert.Equal<string list>([ "LOCAL_BUILD" ], Scripts.symbolsGuardingReferences source [])
    Assert.Empty(Scripts.symbolsGuardingReferences source [ "LOCAL_BUILD" ])

    Assert.Equal<string list>(
        [ "B" ],
        Scripts.symbolsGuardingReferences "#if A\n#elif B\n#r \"x.dll\"\n#endif\n" [ "A" ]
    )

// ---- targets ----

[<Fact>]
let ``a source file resolves to the project whose directory holds it`` () =
    let dir = tempDir ()
    let project = Path.Combine(dir, "Lib.csproj")
    File.WriteAllText(project, """<Project Sdk="Microsoft.NET.Sdk"></Project>""")
    Directory.CreateDirectory(Path.Combine(dir, "Sub")) |> ignore
    let source = Path.Combine(dir, "Sub", "A.cs")
    File.WriteAllText(source, "class A { }")

    match Targets.resolveTargets source with
    | Ok [ Targets.Target.Project(p, Some only) ] ->
        Assert.True(Workspace.samePath project p)
        Assert.True(Workspace.samePath source only)
    | other -> failwithf "unexpected %A" other

[<Fact>]
let ``a directory takes its solution's C# projects, a glob everything it matches`` () =
    let dir = tempDir ()
    let a = Path.Combine(dir, "A", "A.csproj")
    let b = Path.Combine(dir, "B", "B.fsproj")
    Directory.CreateDirectory(Path.GetDirectoryName a) |> ignore
    Directory.CreateDirectory(Path.GetDirectoryName b) |> ignore
    File.WriteAllText(a, """<Project Sdk="Microsoft.NET.Sdk"></Project>""")
    File.WriteAllText(b, """<Project Sdk="Microsoft.NET.Sdk"></Project>""")

    File.WriteAllText(
        Path.Combine(dir, "All.slnx"),
        """<Solution><Project Path="A/A.csproj" /><Project Path="B/B.fsproj" /></Solution>"""
    )

    match Targets.resolveTargets dir with
    | Ok [ Targets.Target.Project(p, None) ] -> Assert.True(Workspace.samePath a p)
    | other -> failwithf "unexpected %A" other

    match Targets.resolveTargets (Path.Combine(dir, "**", "*.csproj")) with
    | Ok [ Targets.Target.Project(p, None) ] -> Assert.True(Workspace.samePath a p)
    | other -> failwithf "unexpected %A" other

/// A linked worktree nested inside its own repository is another checkout
/// of the same code: walking it builds and fixes every project twice. A
/// worktree beside its repository (a workspace of checkouts) and a
/// submodule are code of their own and are walked.
[<Fact>]
let ``a directory walk skips a worktree nested in its own repository only`` () =
    let dir = tempDir ()
    let repo = Path.Combine(dir, "Repo").Replace('\\', '/')

    let write (relative: string) (content: string) =
        let full = Path.Combine(dir, relative)
        Directory.CreateDirectory(Path.GetDirectoryName full) |> ignore
        File.WriteAllText(full, content)

    let project = """<Project Sdk="Microsoft.NET.Sdk"></Project>"""
    write "Repo/A/A.csproj" project
    // absolute, as `git worktree add` writes it by default
    write "Repo/.claude/worktrees/agent-1/.git" $"gitdir: {repo}/.git/worktrees/agent-1\n"
    write "Repo/.claude/worktrees/agent-1/A/A.csproj" project
    // relative, as worktree.useRelativePaths writes it
    write "Repo/.claude/worktrees/agent-2/.git" "gitdir: ../../../.git/worktrees/agent-2\n"
    write "Repo/.claude/worktrees/agent-2/A/A.csproj" project
    write "Repo/vendor/lib/.git" "gitdir: ../../.git/modules/lib\n"
    write "Repo/vendor/lib/L.csproj" project
    write "Repo-branch/.git" $"gitdir: {repo}/.git/worktrees/Repo-branch\n"
    write "Repo-branch/B.csproj" project

    let found =
        FileWalk.files "*.csproj" dir
        |> Seq.map (fun p -> Path.GetRelativePath(dir, p).Replace('\\', '/'))
        |> Set.ofSeq

    Assert.Equal<Set<string>>(set [ "Repo/A/A.csproj"; "Repo/vendor/lib/L.csproj"; "Repo-branch/B.csproj" ], found)

// ---- end to end ----

let private sampleSource =
    csharp
        """
        using System;
        namespace Sample;
        public static class Demo
        {
            public static Guid Empty() => new Guid();
            public static Guid FromBytes(byte[] b) => new Guid(b);
            public static string NoHoles() => $"no holes here";
            public static string WithHole(int x) => $"value {x}";
        }
        """

let private writeProject (dir: string) =
    File.WriteAllText(
        Path.Combine(dir, "Sample.csproj"),
        """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup></Project>"""
    )

    File.WriteAllText(Path.Combine(dir, "Program.cs"), sampleSource)
    Path.Combine(dir, "Sample.csproj")

[<Collection("Tool")>]
type EndToEnd() =

    [<Fact>]
    member _.``a dry run reports without editing, an apply edits and verifies``() =
        let dir = tempDir ()
        let project = writeProject dir
        let report = Path.Combine(dir, "findings.sarif")

        let run (args: string[]) =
            Sweep.resetRun ()

            match parseArgs args with
            | Ok opts -> Sweep.executeRun opts
            | Error e -> failwith e

        Assert.Equal(0, run [| project; "--dry-run"; "--report"; report |])
        let findings = Sweep.reportedSoFar ()
        Assert.Equal<string list>([ "CR0090"; "CR0103" ], findings |> List.map (fun f -> f.Code) |> List.sort)
        Assert.Equal(sampleSource, File.ReadAllText(Path.Combine(dir, "Program.cs")))
        Assert.True(File.Exists report)
        Assert.Contains("csrefContextHash/v1", File.ReadAllText report)

        // the baseline ratchet: a second dry run against the report is clean
        Assert.Equal(0, run [| project; "--dry-run"; "--baseline"; report; "--fail-on-findings" |])
        Assert.Empty(Sweep.reportedSoFar ())

        Assert.Equal(0, run [| project |])
        let after = File.ReadAllText(Path.Combine(dir, "Program.cs"))
        Assert.Contains("=> Guid.Empty;", after)
        Assert.Contains("=> new Guid(b);", after)
        Assert.Contains("""=> "no holes here";""", after)
        Assert.Contains("""$"value {x}";""", after)
        Assert.Equal(2, Sweep.runTotalApplied ())

        // idempotent
        Assert.Equal(0, run [| project |])
        Assert.Equal(0, Sweep.runTotalApplied ())

    [<Fact>]
    member _.``--codes runs only the rules asked for, and a later pass re-analyses only the files the last one touched``
        ()
        =
        let dir = tempDir ()
        let project = writeProject dir

        File.WriteAllText(
            Path.Combine(dir, "Quiet.cs"),
            csharp
                """
                namespace Sample;
                public static class Quiet
                {
                    public static int One() => 1;
                }

                """
        )

        use captured = new StringWriter()
        let oldOut = Console.Out
        Console.SetOut captured

        let code =
            try
                Sweep.resetRun ()

                match parseArgs [| project; "--codes"; "CR0090" |] with
                | Ok opts -> Sweep.executeRun opts
                | Error e -> failwith e
            finally
                Console.SetOut oldOut

        let output = captured.ToString()
        Assert.Equal(0, code)
        Assert.Contains($"1 of {CSharp.Refactor.RuleCatalog.rules.Length} rules", output)
        // CR0103's hole-free interpolation is not asked for: untouched
        let after = File.ReadAllText(Path.Combine(dir, "Program.cs"))
        Assert.Contains("=> Guid.Empty;", after)
        Assert.Contains("$\"no holes here\"", after)
        Assert.Equal(1, Sweep.runTotalApplied ())
        // pass 2 looks at Program.cs alone, never Quiet.cs
        Assert.Matches(@"re-analysing 1 of \d+ file\(s\)", output)

    [<Fact>]
    member _.``a csx script is its own compilation: #r and #load resolve, fixes apply, the run is verified in memory``
        ()
        =
        let dir = tempDir ()

        File.WriteAllText(
            Path.Combine(dir, "helpers.csx"),
            csharp
                """
                public static int Twice(int x) => x * 2;

                """
        )

        let script = Path.Combine(dir, "build.csx")

        File.WriteAllText(
            script,
            csharp
                """
                #r "System.Xml.dll"
                #load "helpers.csx"
                using System.Xml;

                var items = new List<string> { "a", "", "b" };
                foreach (var item in items)
                {
                    if (item.Length == 0) items.Remove(item);
                }
                var actions = new List<Action>();
                for (int i = 0; i < 3; i++)
                {
                    actions.Add(() => Console.WriteLine(Twice(i)));
                }
                var doc = new XmlDocument();
                var empty = new Guid();
                Console.WriteLine(doc.OuterXml + empty + actions.Count);

                """
        )

        let run (args: string[]) =
            Sweep.resetRun ()

            match parseArgs args with
            | Ok opts -> Sweep.executeRun opts
            | Error e -> failwith e

        Assert.Equal(0, run [| script; "--dry-run" |])

        let codes = Sweep.reportedSoFar () |> List.map (fun f -> f.Code) |> List.sort
        Assert.Contains("CR0090", codes)
        Assert.Contains("CR0160", codes)
        Assert.Contains("CR0171", codes)
        // the twin note stands down under the fix
        Assert.DoesNotContain("CR0017", codes)

        Assert.Equal(0, run [| script |])
        let after = File.ReadAllText script
        Assert.Contains("#load \"helpers.csx\"", after)
        Assert.Contains("items.RemoveAll(item => item.Length == 0);", after)

        Assert.Contains(
            csharp
                """
                var i1 = i;
                    actions.Add(() => Console.WriteLine(Twice(i1)));
                """,
            after
        )

        Assert.Contains("var empty = Guid.Empty;", after)

        // the directory form picks the loose scripts up too
        match Targets.resolveTargets dir with
        | Ok targets ->
            let names = targets |> List.map (Targets.projectOf >> Path.GetFileName) |> List.sort
            Assert.Equal<string list>([ "build.csx"; "helpers.csx" ], names)
        | Error e -> failwith e

        Assert.Equal(0, run [| script |])
        Assert.Equal(0, Sweep.runTotalApplied ())

    [<Fact>]
    member _.``a taskified method's callers in another file are rewritten in the same pass and the project still builds``
        ()
        =
        let dir = tempDir ()

        File.WriteAllText(
            Path.Combine(dir, "Sample.csproj"),
            """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"""
        )

        File.WriteAllText(
            Path.Combine(dir, "Service.cs"),
            csharp
                """
                using System.Threading.Tasks;
                namespace Sample;
                internal static class Service
                {
                    static Task<int> Source() => Task.FromResult(1);
                    internal static int Load() { var x = Source().Result; return x; }
                }

                """
        )

        File.WriteAllText(
            Path.Combine(dir, "Caller.cs"),
            csharp
                """
                using System.Threading.Tasks;
                namespace Sample;
                class Caller
                {
                    async Task<int> Run()
                    {
                        var x = Service.Load();
                        var y = Service.Load();
                        return x + y;
                    }
                }

                """
        )

        let project = Path.Combine(dir, "Sample.csproj")

        let run (args: string[]) =
            Sweep.resetRun ()

            match parseArgs args with
            | Ok opts -> Sweep.executeRun opts
            | Error e -> failwith e

        Assert.Equal(0, run [| project; "--codes"; "CR0041" |])
        let service = File.ReadAllText(Path.Combine(dir, "Service.cs"))
        let caller = File.ReadAllText(Path.Combine(dir, "Caller.cs"))
        Assert.Contains("internal static async Task<int> LoadAsync() { var x = await Source(); return x; }", service)
        Assert.Contains("var x = await Service.LoadAsync();", caller)
        Assert.Contains("var y = await Service.LoadAsync();", caller)
        Assert.Equal(1, Sweep.runTotalApplied ())

        Assert.Equal(0, run [| project; "--codes"; "CR0041" |])
        Assert.Equal(0, Sweep.runTotalApplied ())

    [<Fact>]
    member _.``--api-changes opens a public shape whose fix stays in its own file, as the config key does``() =
        let write (dir: string) =
            File.WriteAllText(
                Path.Combine(dir, "Lib.csproj"),
                """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Library</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"""
            )

            File.WriteAllText(
                Path.Combine(dir, "Knobs.cs"),
                csharp
                    """
                    public static class Knobs
                    {
                        public static int Limit = 10;
                        private static int Mine = 2;
                        public static int Sum() => Limit + Mine;
                    }

                    """
            )

            Path.Combine(dir, "Lib.csproj")

        let run (args: string list) =
            Sweep.resetRun ()

            match parseArgs (Array.ofList args) with
            | Ok opts -> Sweep.executeRun opts |> ignore
            | Error e -> failwith e

        // a library without the flag: only the private field
        let plain = tempDir ()
        run [ write plain; "--codes"; "CR0180" ]
        let plainText = File.ReadAllText(Path.Combine(plain, "Knobs.cs"))
        Assert.Contains("public static int Limit = 10;", plainText)
        Assert.Contains("private static readonly int Mine = 2;", plainText)

        // with it: the public field too, though its fix edits no other file
        let opened = tempDir ()
        run [ write opened; "--codes"; "CR0180"; "--api-changes" ]
        Assert.Contains("public static readonly int Limit = 10;", File.ReadAllText(Path.Combine(opened, "Knobs.cs")))

    [<Fact>]
    member _.``--api-changes keeps a public member another project writes, hashes or compares, and fixes the one it only builds``
        ()
        =
        let dir = tempDir ()
        Directory.CreateDirectory(Path.Combine(dir, "Core")) |> ignore
        Directory.CreateDirectory(Path.Combine(dir, "App")) |> ignore

        File.WriteAllText(
            Path.Combine(dir, "Core", "Core.csproj"),
            """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"""
        )

        // each type is built by an object initializer in its own project, so the
        // compilation's own index sees only construction
        File.WriteAllText(
            Path.Combine(dir, "Core", "Models.cs"),
            csharp
                """
                namespace Core;
                public class AccountFilter
                {
                    public string Currency { get; set; }
                    public string Status { get; set; }
                }
                public class Key
                {
                    public int Id { get; init; }
                }
                public class Plain
                {
                    public int Id { get; init; }
                }
                public static class Limits
                {
                    public static int Retries = 3;
                    public static int Timeout = 30;
                }
                public static class Make
                {
                    public static AccountFilter Filter() => new AccountFilter { Currency = "EUR", Status = "active" };
                    public static Key K() => new Key { Id = 1 };
                    public static Plain P() => new Plain { Id = 2 };
                    public static int Sum() => Limits.Retries + Limits.Timeout;
                }

                """
        )

        File.WriteAllText(
            Path.Combine(dir, "App", "App.csproj"),
            """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><ProjectReference Include="../Core/Core.csproj" /></ItemGroup></Project>"""
        )

        // App writes Currency after construction, hashes Key, writes Retries
        File.WriteAllText(
            Path.Combine(dir, "App", "Use.cs"),
            csharp
                """
                using System.Collections.Generic;
                namespace App;
                public static class Use
                {
                    public static int Run(string currency)
                    {
                        var filter = new Core.AccountFilter { Status = "active" };
                        filter.Currency = currency;
                        var seen = new HashSet<Core.Key> { Core.Make.K() };
                        Core.Limits.Retries = 5;
                        return seen.Count + filter.Status.Length + Core.Make.P().Id;
                    }
                }

                """
        )

        File.WriteAllText(
            Path.Combine(dir, "All.slnx"),
            """<Solution><Project Path="Core/Core.csproj" /><Project Path="App/App.csproj" /></Solution>"""
        )

        Sweep.resetRun ()

        match parseArgs [| dir; "--api-changes"; "--codes"; "CR0083,CR0080,CR0180" |] with
        | Ok opts -> Sweep.executeRun opts |> ignore
        | Error e -> failwith e

        let models = File.ReadAllText(Path.Combine(dir, "Core", "Models.cs"))
        // written in App after construction: CS8852 as init
        Assert.Contains("public string Currency { get; set; }", models)
        // App only builds it: init is safe
        Assert.Contains("public string Status { get; init; }", models)
        // App hashes Key: a record would change the set; Plain App only reads
        Assert.Contains("public class Key", models)
        Assert.Contains("public record Plain", models)
        // App writes Retries; nothing writes Timeout
        Assert.Contains("public static int Retries = 3;", models)
        Assert.Contains("public static readonly int Timeout = 30;", models)

    [<Fact>]
    member _.``--api-changes keeps a setter another project sets through a nested initializer``() =
        let dir = tempDir ()
        Directory.CreateDirectory(Path.Combine(dir, "Core")) |> ignore
        Directory.CreateDirectory(Path.Combine(dir, "App")) |> ignore

        File.WriteAllText(
            Path.Combine(dir, "Core", "Core.csproj"),
            """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"""
        )

        File.WriteAllText(
            Path.Combine(dir, "Core", "Models.cs"),
            csharp
                """
                namespace Core;
                public class Inner
                {
                    public int P { get; set; }
                }
                public class Outer
                {
                    public Inner Inner { get; } = new Inner { P = 1 };
                }

                """
        )

        File.WriteAllText(
            Path.Combine(dir, "App", "App.csproj"),
            """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><ProjectReference Include="../Core/Core.csproj" /></ItemGroup></Project>"""
        )

        // `Inner = { P = 5 }` sets P on the Inner that Outer already holds: CS8852 as init
        File.WriteAllText(
            Path.Combine(dir, "App", "Use.cs"),
            csharp
                """
                namespace App;
                public static class Use
                {
                    public static int Run() => new Core.Outer { Inner = { P = 5 } }.Inner.P;
                }

                """
        )

        File.WriteAllText(
            Path.Combine(dir, "All.slnx"),
            """<Solution><Project Path="Core/Core.csproj" /><Project Path="App/App.csproj" /></Solution>"""
        )

        Sweep.resetRun ()

        match parseArgs [| dir; "--api-changes"; "--codes"; "CR0083" |] with
        | Ok opts -> Sweep.executeRun opts |> ignore
        | Error e -> failwith e

        Assert.Contains("public int P { get; set; }", File.ReadAllText(Path.Combine(dir, "Core", "Models.cs")))

    [<Fact>]
    member _.``--api-changes holds the public surface a project outside the run compiles against through a HintPath``
        ()
        =
        let write (withOutsider: bool) =
            let dir = tempDir ()
            Directory.CreateDirectory(Path.Combine(dir, ".git")) |> ignore
            Directory.CreateDirectory(Path.Combine(dir, "Lib")) |> ignore

            File.WriteAllText(
                Path.Combine(dir, "Lib", "Lib.csproj"),
                """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"""
            )

            File.WriteAllText(
                Path.Combine(dir, "Lib", "Knobs.cs"),
                csharp
                    """
                    public static class Knobs
                    {
                        public static int Limit = 10;
                        private static int Mine = 2;
                        public static int Sum() => Limit + Mine;
                    }

                    """
            )

            // a legacy consumer: no ProjectReference, the built dll from a shared bin
            if withOutsider then
                Directory.CreateDirectory(Path.Combine(dir, "Other")) |> ignore

                File.WriteAllText(
                    Path.Combine(dir, "Other", "Other.csproj"),
                    """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><Reference Include="Lib"><HintPath>..\bin\Lib.dll</HintPath></Reference></ItemGroup></Project>"""
                )

            dir, Path.Combine(dir, "Lib", "Lib.csproj")

        let run (project: string) =
            Sweep.resetRun ()

            match parseArgs [| project; "--api-changes"; "--codes"; "CR0180" |] with
            | Ok opts -> Sweep.executeRun opts |> ignore
            | Error e -> failwith e

        // nothing outside the run compiles against Lib: the flag opens its public field
        let dir, project = write false
        run project

        Assert.Contains(
            "public static readonly int Limit = 10;",
            File.ReadAllText(Path.Combine(dir, "Lib", "Knobs.cs"))
        )

        // Other reads Lib.dll from a shared bin, outside the run: the public field keeps its shape
        let dir, project = write true
        run project
        let knobs = File.ReadAllText(Path.Combine(dir, "Lib", "Knobs.cs"))
        Assert.Contains("public static int Limit = 10;", knobs)
        Assert.Contains("private static readonly int Mine = 2;", knobs)

    [<Fact>]
    member _.``a fix that raises an analyzer warning the project treats as an error is held, the others stand``() =
        let dir = tempDir ()

        File.WriteAllText(
            Path.Combine(dir, "Sample.csproj"),
            """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup></Project>"""
        )

        // a custom hint is its author's to aim: this one writes the Any()
        // CA1860 forbids on an array
        File.WriteAllText(
            Path.Combine(dir, "hints.txt"),
            csharp
                """
                x.Length > 0 ===> x.Any()

                """
        )

        File.WriteAllText(
            Path.Combine(dir, ".editorconfig"),
            csharp
                """
                root = true
                [*.cs]
                dotnet_diagnostic.CA1860.severity = warning
                csharp_refactor.hints = hints.txt

                """
        )

        File.WriteAllText(
            Path.Combine(dir, "Arrays.cs"),
            csharp
                """
                using System.Linq;
                namespace Sample;
                public static class Arrays
                {
                    public static bool HasAny(int[] xs) => xs.Length > 0;
                }

                """
        )

        File.WriteAllText(
            Path.Combine(dir, "Sequences.cs"),
            csharp
                """
                using System.Collections.Generic;
                using System.Linq;
                namespace Sample;
                public static class Sequences
                {
                    public static bool HasAny(IEnumerable<int> xs) => xs.Count() > 0;
                }

                """
        )

        let project = Path.Combine(dir, "Sample.csproj")
        Sweep.resetRun ()

        match parseArgs [| project; "--codes"; "CR0011" |] with
        | Ok opts -> Sweep.executeRun opts |> ignore
        | Error e -> failwith e

        Assert.Contains("xs.Length > 0;", File.ReadAllText(Path.Combine(dir, "Arrays.cs")))
        Assert.Contains("xs.Any();", File.ReadAllText(Path.Combine(dir, "Sequences.cs")))
        Assert.Equal(1, Sweep.runTotalApplied ())

    [<Fact>]
    member _.``under the api pass a friend project's callers are rewritten through the reference oracle and both projects build``
        ()
        =
        let dir = tempDir ()
        Directory.CreateDirectory(Path.Combine(dir, "A")) |> ignore
        Directory.CreateDirectory(Path.Combine(dir, "B")) |> ignore

        File.WriteAllText(
            Path.Combine(dir, "A", "A.csproj"),
            """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><InternalsVisibleTo Include="B" /></ItemGroup></Project>"""
        )

        File.WriteAllText(
            Path.Combine(dir, "A", "Service.cs"),
            csharp
                """
                using System.Threading.Tasks;
                namespace A;
                internal static class Service
                {
                    static Task<int> Source() => Task.FromResult(1);
                    internal static int Load() { var x = Source().Result; return x; }
                }

                """
        )

        File.WriteAllText(
            Path.Combine(dir, "B", "B.csproj"),
            """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><ProjectReference Include="../A/A.csproj" /></ItemGroup></Project>"""
        )

        File.WriteAllText(
            Path.Combine(dir, "B", "Caller.cs"),
            csharp
                """
                using System.Threading.Tasks;
                namespace B;
                class Caller
                {
                    async Task<int> Run()
                    {
                        var x = A.Service.Load();
                        return x;
                    }
                }

                """
        )

        File.WriteAllText(
            Path.Combine(dir, "All.slnx"),
            """<Solution><Project Path="A/A.csproj" /><Project Path="B/B.csproj" /></Solution>"""
        )

        let run (args: string[]) =
            Sweep.resetRun ()

            match parseArgs args with
            | Ok opts -> Sweep.executeRun opts
            | Error e -> failwith e

        // without the api pass the friend holds the surface: nothing changes
        Assert.Equal(0, run [| dir; "--codes"; "CR0041" |])
        Assert.Equal(0, Sweep.runTotalApplied ())

        Assert.Equal(0, run [| dir; "--api-changes"; "--codes"; "CR0041" |])
        let service = File.ReadAllText(Path.Combine(dir, "A", "Service.cs"))
        let caller = File.ReadAllText(Path.Combine(dir, "B", "Caller.cs"))
        Assert.Contains("internal static async Task<int> LoadAsync() { var x = await Source(); return x; }", service)
        Assert.Contains("var x = await A.Service.LoadAsync();", caller)
        Assert.Equal(1, Sweep.runTotalApplied ())

        Assert.Equal(0, run [| dir; "--api-changes"; "--codes"; "CR0041" |])
        Assert.Equal(0, Sweep.runTotalApplied ())

    [<Fact>]
    member _.``a file that is not UTF-8 goes back in its own encoding, byte for byte outside the edit``() =
        let dir = tempDir ()

        File.WriteAllText(
            Path.Combine(dir, "Sample.csproj"),
            """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"""
        )

        // Windows-1252: `×` is the single byte 0xD7, which is not valid UTF-8 —
        // written as raw bytes, so the tool's own code-page handling is what is tested
        let ascii (s: string) = Text.Encoding.ASCII.GetBytes s

        let original =
            Array.concat
                [
                    ascii (
                        csharp
                            """
                            using System;
                            // a 3
                            """
                    )
                    [| 0xD7uy |]
                    ascii (
                        csharp
                            """
                            3 matrix
                            class C { Guid A() => new Guid(); }

                            """
                    )
                ]

        let path = Path.Combine(dir, "Program.cs")
        File.WriteAllBytes(path, original)

        let run (args: string[]) =
            Sweep.resetRun ()

            match parseArgs args with
            | Ok opts -> Sweep.executeRun opts
            | Error e -> failwith e

        Assert.Equal(0, run [| Path.Combine(dir, "Sample.csproj"); "--codes"; "CR0090" |])
        let bytes = File.ReadAllBytes path
        Assert.Contains(0xD7uy, bytes)
        Assert.DoesNotContain(0xC3uy, bytes)

        Assert.Contains(
            "Guid A() => Guid.Empty;",
            Text.Encoding.ASCII.GetString(bytes |> Array.filter (fun b -> b < 128uy))
        )
        // everything before the edit is byte for byte the original
        Assert.Equal<byte[]>(original.[0..39], bytes.[0..39])

/// `--define` end to end: code under `#if LOCAL_BUILD` is analysed and
/// fixed only when the run defines it, and the symbols are ADDED to the
/// project's own DefineConstants: the DEBUG/TRACE/project-constant guard
/// below does not compile without them, and the project's own target fails
/// the verification build unless every one of them is there. A global
/// property (-p:DefineConstants=...) would replace them and fail both.
[<Collection("Tool")>]
type DefinesEndToEnd() =

    [<Fact>]
    member _.``--define LOCAL_BUILD analyses and fixes #if LOCAL_BUILD code, and DEBUG and TRACE stay defined``() =
        let dir = tempDir ()
        let project = Path.Combine(dir, "Defines.csproj")

        File.WriteAllText(
            project,
            csharp
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <DefineConstants>$(DefineConstants);PROJ_OWN</DefineConstants>
                  </PropertyGroup>
                  <Target Name="RequireDefines" BeforeTargets="CoreCompile" Condition="'$(DesignTimeBuild)' != 'true'">
                    <Error Condition="'$(DefineConstants.Contains(`LOCAL_BUILD`))' != 'true' Or '$(DefineConstants.Contains(`DEBUG`))' != 'true' Or '$(DefineConstants.Contains(`TRACE`))' != 'true' Or '$(DefineConstants.Contains(`PROJ_OWN`))' != 'true'" Text="DefineConstants is '$(DefineConstants)'" />
                  </Target>
                </Project>
                """
        )

        let source =
            csharp
                """
                using System;
                namespace Defines;
                public static class Demo
                {
                #if LOCAL_BUILD
                    public static Guid Local() => new Guid();
                #endif
                #if DEBUG && TRACE && PROJ_OWN
                    public static int Keep() => 1;
                #else
                    public static int Keep() => this_does_not_compile;
                #endif
                }
                """

        let path = Path.Combine(dir, "Program.cs")
        File.WriteAllText(path, source)

        let run (args: string[]) =
            Sweep.resetRun ()

            match parseArgs args with
            | Ok opts -> Sweep.executeRun opts
            | Error e -> failwith e

        let before = Environment.GetEnvironmentVariable "DefineConstants"

        // without the symbol the code is not in the parse tree: nothing to fix
        Assert.Equal(0, run [| project; "--codes"; "CR0090" |])
        Assert.Equal(0, Sweep.runTotalApplied ())
        Assert.Equal(source, File.ReadAllText path)

        // with it: analysed, fixed, and the verification build (whose target
        // demands every symbol) passes
        Assert.Equal(0, run [| project; "--codes"; "CR0090"; "--define"; "LOCAL_BUILD" |])
        Assert.Equal(1, Sweep.runTotalApplied ())
        Assert.Contains("public static Guid Local() => Guid.Empty;", File.ReadAllText path)

        // the run leaves the process environment as it found it
        Assert.Equal(before, Environment.GetEnvironmentVariable "DefineConstants")
        Assert.Empty(RunDefines.current ())

    [<Fact>]
    member _.``the run's symbols are appended to DefineConstants and parse options, never repeated``() =
        try
            RunDefines.set [ "LOCAL_BUILD"; "EXTRA"; "LOCAL_BUILD" ]
            Assert.Equal<string list>([ "LOCAL_BUILD"; "EXTRA" ], RunDefines.current ())
            Assert.Equal(Some "LOCAL_BUILD;EXTRA", RunDefines.environmentValue "")
            Assert.Equal(Some "OWN;EXTRA;LOCAL_BUILD", RunDefines.environmentValue "OWN;EXTRA;")

            let parse =
                CSharpParseOptions(preprocessorSymbols = [ "DEBUG"; "EXTRA" ])
                |> RunDefines.addTo
                :?> CSharpParseOptions

            Assert.Equal<string list>([ "DEBUG"; "EXTRA"; "LOCAL_BUILD" ], List.ofSeq parse.PreprocessorSymbolNames)
            // nothing missing: the same options back
            Assert.Same(parse, RunDefines.addTo parse)
        finally
            RunDefines.set []

        Assert.Equal(None, RunDefines.environmentValue "OWN")

    [<Fact>]
    member _.``a script or legacy project cached by a resident host takes each run's symbols, not the first run's``() =
        let dir = tempDir ()
        let script = Path.Combine(dir, "build.csx")
        File.WriteAllText(script, "System.Console.WriteLine(1);\n")
        let project = Path.Combine(dir, "Legacy.csproj")

        File.WriteAllText(
            project,
            """<Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003"><PropertyGroup><TargetFrameworkVersion>v4.7.2</TargetFrameworkVersion><OutputType>Library</OutputType><DefineConstants>OWN</DefineConstants></PropertyGroup><ItemGroup><Compile Include="A.cs" /></ItemGroup></Project>"""
        )

        File.WriteAllText(Path.Combine(dir, "A.cs"), "class A { }\n")
        use workspace = new Microsoft.CodeAnalysis.AdhocWorkspace()

        let symbolsOf (p: Microsoft.CodeAnalysis.Project) =
            List.ofSeq (p.ParseOptions :?> CSharpParseOptions).PreprocessorSymbolNames

        try
            RunDefines.set [ "LOCAL_BUILD" ]
            Assert.Contains("LOCAL_BUILD", symbolsOf (Scripts.load workspace script))
            Assert.Equal<string list>([ "OWN"; "LOCAL_BUILD" ], symbolsOf (LegacyProjects.load workspace project))

            // a later run of the same host without the symbol: the cached projects lose it
            RunDefines.set []
            Assert.DoesNotContain("LOCAL_BUILD", symbolsOf (Scripts.load workspace script))
            Assert.Equal<string list>([ "OWN" ], symbolsOf (LegacyProjects.load workspace project))

            // and one with another symbol gets that one, not the first run's
            RunDefines.set [ "OTHER" ]
            Assert.Equal<string list>([ "OWN"; "OTHER" ], symbolsOf (LegacyProjects.load workspace project))
        finally
            RunDefines.set []

    [<Fact>]
    member _.``a fix that makes the next pass's work converges pass by pass; the dry run before it reports the finding and writes nothing``
        ()
        =
        let dir = tempDir ()

        File.WriteAllText(
            Path.Combine(dir, "Sample.csproj"),
            """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"""
        )

        let source =
            csharp
                """
                namespace Sample;
                public static class Flags
                {
                    public static int Check(bool done) { if (done = true) return 1; return 0; }
                }

                """

        let file = Path.Combine(dir, "Flags.cs")
        File.WriteAllText(file, source)
        let project = Path.Combine(dir, "Sample.csproj")

        let run (args: string[]) =
            use captured = new StringWriter()
            let oldOut = Console.Out
            Console.SetOut captured

            let code =
                try
                    Sweep.resetRun ()

                    match parseArgs args with
                    | Ok opts -> Sweep.executeRun opts
                    | Error e -> failwith e
                finally
                    Console.SetOut oldOut

            code, captured.ToString()

        // the dry run: the finding, one pass, the file as it was
        let dryCode, dryOutput = run [| project; "--codes"; "CR0195,CR0011"; "--dry-run" |]
        Assert.Equal(0, dryCode)

        Assert.Equal<(string * int) list>(
            [ "CR0195", 4 ],
            Sweep.reportedSoFar () |> List.map (fun f -> f.Code, f.StartLine)
        )

        Assert.Equal(source, File.ReadAllText file)
        Assert.Contains("dry run: 1 fix(es) would be applied", dryOutput)
        Assert.DoesNotContain("pass 2:", dryOutput)
        Assert.Equal(0, Sweep.runTotalApplied ())

        // the sweep: `done = true` → `done == true` in pass 1, which is what pass 2's
        // `x == true ===> x` reads; pass 3 finds nothing and ends the run
        let code, output = run [| project; "--codes"; "CR0195,CR0011" |]
        Assert.Equal(0, code)
        Assert.Contains("if (done) return 1;", File.ReadAllText file)
        Assert.Equal(2, Sweep.runTotalApplied ())
        Assert.Contains("1 fix(es) applied in pass 1", output)
        Assert.Contains("1 fix(es) applied in pass 2", output)
        Assert.Contains("pass 3:", output)
        Assert.DoesNotContain("pass 4:", output)
        Assert.Contains("Sample.csproj: still builds", output)
        Assert.Contains("2 fix(es) applied in total", output)

        Assert.Equal<string list>(
            [ "CR0011"; "CR0195" ],
            Sweep.reportedSoFar () |> List.map (fun f -> f.Code) |> List.sort
        )

    [<Fact>]
    member _.``fixes that break the compilation together are bisected per file: the offender is put back, the rest stand, and the report lists all three``
        ()
        =
        let dir = tempDir ()

        File.WriteAllText(
            Path.Combine(dir, "Sample.csproj"),
            """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"""
        )

        // two halves of one type, each hoisting a regex out of a method named
        // `Match`: either field alone compiles, the two together are one name twice
        let one =
            csharp
                """
                using System.Text.RegularExpressions;
                namespace Sample;
                public static partial class Lines
                {
                    public static bool Match(string s) { return Regex.IsMatch(s, "a+b[0-9]"); }
                }

                """

        let two =
            csharp
                """
                using System.Text.RegularExpressions;
                namespace Sample;
                public static partial class Lines
                {
                    public static bool Match(string s, int n) { return n > 0 && Regex.IsMatch(s, "c+d[0-9]"); }
                }

                """

        File.WriteAllText(Path.Combine(dir, "One.cs"), one)
        File.WriteAllText(Path.Combine(dir, "Two.cs"), two)

        File.WriteAllText(
            Path.Combine(dir, "Three.cs"),
            csharp
                """
                using System;
                namespace Sample;
                public static class Ids
                {
                    public static Guid None() => new Guid();
                }

                """
        )

        let project = Path.Combine(dir, "Sample.csproj")
        let report = Path.Combine(dir, "findings.sarif")
        use captured = new StringWriter()
        let oldOut = Console.Out
        Console.SetOut captured

        let code =
            try
                Sweep.resetRun ()

                match parseArgs [| project; "--codes"; "CR0109,CR0090"; "--report"; report |] with
                | Ok opts -> Sweep.executeRun opts
                | Error e -> failwith e
            finally
                Console.SetOut oldOut

        let output = captured.ToString()
        // a fix put back is the run's failure to report
        Assert.Equal(1, code)
        Assert.Contains("2 fix(es) applied in pass 1", output)
        Assert.Contains("Sample.csproj: still builds", output)
        Assert.Equal(2, Sweep.runTotalApplied ())

        Assert.Contains(
            "private static readonly Regex MatchRegex = new Regex(\"a+b[0-9]\");",
            File.ReadAllText(Path.Combine(dir, "One.cs"))
        )

        Assert.Equal(two, File.ReadAllText(Path.Combine(dir, "Two.cs")))
        Assert.Contains("=> Guid.Empty;", File.ReadAllText(Path.Combine(dir, "Three.cs")))

        // the report, by what does not move between machines: rule, file, place, text
        use sarif = System.Text.Json.JsonDocument.Parse(File.ReadAllText report)

        let results =
            sarif.RootElement.GetProperty("runs").[0].GetProperty("results").EnumerateArray()
            |> Seq.map (fun r ->
                let location = r.GetProperty("locations").[0].GetProperty("physicalLocation")
                let region = location.GetProperty("region")
                let context = location.GetProperty("contextRegion")

                String.Join(
                    "|",
                    [
                        r.GetProperty("ruleId").GetString()
                        location.GetProperty("artifactLocation").GetProperty("uri").GetString()
                        string (region.GetProperty("startLine").GetInt32())
                        string (region.GetProperty("startColumn").GetInt32())
                        region.GetProperty("snippet").GetProperty("text").GetString()
                        string (context.GetProperty("startLine").GetInt32())
                        string (context.GetProperty("endLine").GetInt32())
                        string (r.GetProperty("properties").GetProperty("autoFixable").GetBoolean())
                    ]
                ))
            |> Seq.sort
            |> List.ofSeq

        Assert.Equal<string list>(
            [
                "CR0090|Three.cs|5|34|new Guid()|4|5|True"
                "CR0109|One.cs|5|49|Regex.IsMatch(s, \"a+b[0-9]\")|4|5|True"
                "CR0109|Two.cs|5|65|Regex.IsMatch(s, \"c+d[0-9]\")|4|5|True"
            ],
            results
        )

// ---- legacy projects ----

[<Fact>]
let ``a project naming its SDK in an Sdk element is not legacy`` () =
    let dir = tempDir ()

    let write (name: string) (text: string) =
        let path = Path.Combine(dir, name)
        File.WriteAllText(path, text)
        path

    let element =
        write
            "Element.csproj"
            (csharp
                """
                <Project>
                  <Sdk Name="Microsoft.NET.Sdk" />
                  <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
                </Project>
                """)

    let legacy =
        write
            "Legacy.csproj"
            """<Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003"><ItemGroup><Compile Include="A.cs" /></ItemGroup></Project>"""

    Assert.False(LegacyProjects.isLegacy element)
    Assert.True(LegacyProjects.isLegacy legacy)

[<Fact>]
let ``a legacy project parses at the language version its build will use`` () =
    let dir = tempDir ()

    let project (name: string) (langVersion: string option) =
        let extra =
            match langVersion with
            | Some v -> $"<LangVersion>{v}</LangVersion>"
            | None -> ""

        let path = Path.Combine(dir, name + ".csproj")

        File.WriteAllText(
            path,
            $"<Project ToolsVersion=\"15.0\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\"><PropertyGroup><TargetFrameworkVersion>v4.7.2</TargetFrameworkVersion><OutputType>Library</OutputType>{extra}</PropertyGroup><ItemGroup><Compile Include=\"A.cs\" /></ItemGroup></Project>"
        )

        path

    File.WriteAllText(Path.Combine(dir, "A.cs"), "class A { }\n")
    use workspace = new Microsoft.CodeAnalysis.AdhocWorkspace()

    let versionOf (path: string) =
        let p = LegacyProjects.load workspace path
        (p.ParseOptions :?> CSharpParseOptions).LanguageVersion

    // MSBuild's default for a .NET Framework target is C# 7.3; `default` says the same
    Assert.Equal(LanguageVersion.CSharp7_3, versionOf (project "Plain" None))

    Assert.Equal(LanguageVersion.CSharp7_3, versionOf (project "Default" (Some "default")))

    Assert.Equal(LanguageVersion.CSharp9, versionOf (project "Nine" (Some "9.0")))

    Assert.True(versionOf (project "Latest" (Some "latest")) >= LanguageVersion.CSharp12)

[<Fact>]
let ``a legacy project's file that is not UTF-8 is read in the system code page, not with replacement characters`` () =
    task {
        let dir = tempDir ()

        do!
            File.WriteAllTextAsync(
                Path.Combine(dir, "Legacy.csproj"),
                """<Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003"><PropertyGroup><TargetFrameworkVersion>v4.7.2</TargetFrameworkVersion><OutputType>Library</OutputType></PropertyGroup><ItemGroup><Compile Include="A.cs" /></ItemGroup></Project>"""
            )

        // Windows-1252: `×` is the single byte 0xD7, not valid UTF-8
        let ascii (s: string) = Text.Encoding.ASCII.GetBytes s

        do!
            File.WriteAllBytesAsync(
                Path.Combine(dir, "A.cs"),
                Array.concat [ ascii "// 3"; [| 0xD7uy |]; ascii "3\nclass A { }\n" ]
            )

        use workspace = new Microsoft.CodeAnalysis.AdhocWorkspace()
        let p = LegacyProjects.load workspace (Path.Combine(dir, "Legacy.csproj"))
        let! text = (Seq.head p.Documents).GetTextAsync()
        Assert.Contains("3×3", text.ToString())
        Assert.DoesNotContain("�", text.ToString())
        // the encoding it came in is the one it goes back in
        Assert.NotNull text.Encoding
        Assert.NotEqual(65001, text.Encoding.CodePage)
    }
    :> System.Threading.Tasks.Task

// ---- the MCP server ----

/// `csharp-refactor --mcp` over stdio, as an agent host drives it: the
/// handshake, the tool list, `list_rules`, and an `analyze` of a project
/// that reports its findings and, by default, edits nothing.
[<Fact>]
let ``the MCP server answers the handshake, lists its tools and rules, and analyzes a project without editing it`` () =
    task {
        let dir = tempDir ()

        do!
            File.WriteAllTextAsync(
                Path.Combine(dir, "Sample.csproj"),
                """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"""
            )

        let source = "using System;\nclass C { Guid A() => new Guid(); }\n"
        do! File.WriteAllTextAsync(Path.Combine(dir, "C.cs"), source)

        let toolDll =
            Path.Combine(Path.GetDirectoryName(typeof<Options>.Assembly.Location), "CSharp.Refactor.Tool.dll")

        let psi =
            Diagnostics.ProcessStartInfo(
                "dotnet",
                $"\"{toolDll}\" --mcp",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            )

        use p = Diagnostics.Process.Start psi
        let stderr = p.StandardError.ReadToEndAsync()

        let target = Path.Combine(dir, "Sample.csproj").Replace("\\", "\\\\")

        for request in
            [
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"test","version":"0"}}}"""
                """{"jsonrpc":"2.0","method":"notifications/initialized"}"""
                """{"jsonrpc":"2.0","id":2,"method":"tools/list"}"""
                """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"list_rules","arguments":{}}}"""
                $"""{{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{{"name":"analyze","arguments":{{"target":"{target}","codes":"CR0090"}}}}}}"""
                // unknown codes and categories are errors, as on the command line
                $"""{{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{{"name":"analyze","arguments":{{"target":"{target}","codes":"CR9999"}}}}}}"""
                $"""{{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{{"name":"analyze","arguments":{{"target":"{target}","categories":"bogus"}}}}}}"""
                // malformed requests are JSON-RPC errors, and the server keeps serving
                """{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":5}}"""
                """{"jsonrpc":"2.0","id":8,"method":"tools/call","params":[1]}"""
                """{"jsonrpc":"2.0","id":9,"method":null}"""
                """{"jsonrpc":"2.0","id":10,"method":"ping"}"""
            ] do
            p.StandardInput.WriteLine request

        p.StandardInput.Close()
        let! output = p.StandardOutput.ReadToEndAsync()

        if not (p.WaitForExit 300_000) then
            p.Kill()
            failwith "the MCP server did not exit when its input closed"

        let responses =
            output.Split '\n'
            |> Array.filter (fun l -> l.Trim() <> "")
            |> Array.map (fun l -> Text.Json.JsonDocument.Parse(l).RootElement)

        let byId (id: int) =
            responses
            |> Array.find (fun r ->
                r.TryGetProperty "id"
                |> fun (ok, v) -> ok && v.ValueKind = Text.Json.JsonValueKind.Number && v.GetInt32() = id)

        // the handshake names the server
        Assert.Contains(
            "csharp-refactor",
            (byId 1).GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString()
        )

        let tools =
            (byId 2).GetProperty("result").GetProperty("tools").EnumerateArray()
            |> Seq.map (fun t -> t.GetProperty("name").GetString())
            |> List.ofSeq

        Assert.Equal<string list>([ "analyze"; "list_rules" ], List.sort tools)

        // a tool result carries its JSON as text content
        let content (r: Text.Json.JsonElement) =
            r.GetProperty("result").GetProperty("content").[0].GetProperty("text").GetString()

        let rules = Text.Json.JsonDocument.Parse(content (byId 3)).RootElement
        Assert.Equal(CSharp.Refactor.RuleCatalog.rules.Length, rules.GetArrayLength())

        let analysis = Text.Json.JsonDocument.Parse(content (byId 4)).RootElement
        Assert.Equal(1, analysis.GetProperty("findingCount").GetInt32())
        Assert.False(analysis.GetProperty("applied").GetBoolean())
        Assert.Contains("CR0090", content (byId 4))
        // a dry run by default: the file is as it was
        Assert.Equal(source, File.ReadAllText(Path.Combine(dir, "C.cs")))

        for id in [ 5; 6; 7; 8; 9 ] do
            Assert.True(fst ((byId id).TryGetProperty "error"), $"request {id} should be an error")

        Assert.Contains("not a rule code: CR9999", (byId 5).GetProperty("error").GetProperty("message").GetString())
        Assert.Contains("not a category", (byId 6).GetProperty("error").GetProperty("message").GetString())
        Assert.True(fst ((byId 10).TryGetProperty "result"))
        Assert.Equal(0, p.ExitCode)
        let! _ = stderr
        ()
    }
    :> System.Threading.Tasks.Task

[<Collection("Tool")>]
type Frameworks() =

    [<Fact>]
    member _.``a fix only the wider framework compiles is not offered for code the narrower one builds too``() =
        let dir = tempDir ()
        let project = Path.Combine(dir, "Sample.csproj")

        File.WriteAllText(
            project,
            """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFrameworks>net48;net8.0</TargetFrameworks><LangVersion>latest</LangVersion></PropertyGroup></Project>"""
        )

        File.WriteAllText(
            Path.Combine(dir, "Model.cs"),
            csharp
                """
                using System;
                namespace Sample;
                internal class Entity
                {
                    public int Id { get; set; }
                }
                public static class Use
                {
                    internal static Entity Make() => new Entity { Id = 1 };
                    public static Guid Shared() => new Guid();
                }
                """
        )

        File.WriteAllText(
            Path.Combine(dir, "Modern.cs"),
            csharp
                """
                using System;
                namespace Sample;
                public static class Modern
                {
                #if NET8_0_OR_GREATER
                    public static Guid Wider() => new Guid();
                #endif
                }
                """
        )

        use captured = new StringWriter()
        let oldOut = Console.Out
        Console.SetOut captured

        let code =
            try
                Sweep.resetRun ()

                match parseArgs [| project; "--codes"; "CR0083,CR0090" |] with
                | Ok opts -> Sweep.executeRun opts
                | Error e -> failwith e
            finally
                Console.SetOut oldOut

        let output = captured.ToString()
        Assert.Equal(0, code)
        // `init` needs a type net48 lacks: never applied, so nothing to put back
        Assert.DoesNotContain("bisecting", output)
        Assert.DoesNotContain("were not applied", output)
        let model = File.ReadAllText(Path.Combine(dir, "Model.cs"))
        Assert.Contains("{ get; set; }", model)
        // the shared code is the narrowest sweep's, the #if region the wider one's
        Assert.Contains("Shared() => Guid.Empty;", model)
        Assert.Contains("Wider() => Guid.Empty;", File.ReadAllText(Path.Combine(dir, "Modern.cs")))
        Assert.Equal(2, Sweep.runTotalApplied ())

    [<Fact>]
    member _.``frameworks compiling the same sources are swept once, on the narrowest``() =
        let dir = tempDir ()
        let project = Path.Combine(dir, "Sample.csproj")

        File.WriteAllText(
            project,
            """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFrameworks>net8.0;net48</TargetFrameworks><LangVersion>latest</LangVersion></PropertyGroup></Project>"""
        )

        File.WriteAllText(Path.Combine(dir, "Program.cs"), sampleSource)

        use captured = new StringWriter()
        let oldOut = Console.Out
        Console.SetOut captured

        let code =
            try
                Sweep.resetRun ()

                match parseArgs [| project; "--codes"; "CR0090" |] with
                | Ok opts -> Sweep.executeRun opts
                | Error e -> failwith e
            finally
                Console.SetOut oldOut

        let output = captured.ToString()
        Assert.Equal(0, code)
        Assert.Contains("(net8.0: the same sources as net48, swept there)", output)
        Assert.Contains("=> Guid.Empty;", File.ReadAllText(Path.Combine(dir, "Program.cs")))
        Assert.Equal(1, Sweep.runTotalApplied ())
