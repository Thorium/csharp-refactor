/// The tool when a run cannot go as asked: a flag, baseline or report path
/// that cannot work is refused before anything is written, a glob searches
/// the directories it names, and a run that fails before its verification
/// builds puts back what it wrote.
module CSharp.Refactor.Tests.ReleaseAuditToolTests

open System
open System.IO
open Xunit
open CSharp.Refactor.Tool
open CSharp.Refactor.Tool.Options

let private tempDir () =
    let dir =
        Path.Combine(Path.GetTempPath(), "csref-tests", Guid.NewGuid().ToString "N")

    Directory.CreateDirectory dir |> ignore
    dir

/// A run through the command line's parser; returns its exit code and what
/// it wrote to stderr.
let private run (args: string[]) : int * string =
    use captured = new StringWriter()
    let oldErr = Console.Error
    Console.SetError captured

    try
        Sweep.resetRun ()

        let code =
            match parseArgs args with
            | Ok opts -> Sweep.executeRun opts
            | Error e -> failwith e

        code, captured.ToString()
    finally
        Console.SetError oldErr

let private refused (args: string list) =
    match parseArgs (Array.ofList args) with
    | Error message -> message
    | Ok _ -> failwithf "%A parsed" args

// ---- the command line ----

[<Fact>]
let ``a value-taking flag followed by a flag is a usage error, not a value`` () =
    Assert.Equal("'--report' needs a value after it", refused [ "src"; "--report"; "--dry-run" ])
    Assert.Equal("'--baseline' needs a value after it", refused [ "src"; "--baseline"; "--dry-run" ])
    Assert.Equal("'--framework' needs a value after it", refused [ "src"; "--framework"; "--dry-run" ])

[<Fact>]
let ``filters that leave no rule to run are refused`` () =
    Assert.Contains("names no rule code", refused [ "src"; "--codes"; "" ])
    Assert.Contains("names no rule code", refused [ "src"; "--codes"; " , " ])
    // CR0090 is a correctness rule
    Assert.Contains("leave no rule to run", refused [ "src"; "--codes"; "CR0090"; "--categories"; "cosmetic" ])

    match parseArgs [| "src"; "--codes"; "CR0090"; "--categories"; "correctness" |] with
    | Ok opts -> Assert.Equal<Set<string>>(set [ "CR0090" ], opts.Codes.Value)
    | Error e -> failwith e

// ---- globs ----

[<Fact>]
let ``a glob searches the directories it names, not every directory under the first`` () =
    let root = tempDir ()

    for relative in
        [
            "src/App/App.csproj"
            "src/AppTests/AppTests.csproj"
            "src/Lib/Lib.csproj"
            "src/Lib/Nested/Deep.csproj"
            "Top.csproj"
        ] do
        let path = Path.Combine(root, relative)
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        File.WriteAllText(path, "<Project />")

    let found (pattern: string) =
        FileWalk.glob (Path.Combine(root, pattern))
        |> List.map (fun f -> Path.GetRelativePath(root, f).Replace('\\', '/'))
        |> List.sort

    Assert.Equal<string list>([ "src/App/App.csproj"; "src/AppTests/AppTests.csproj" ], found "src/App*/*.csproj")
    Assert.Equal<string list>([ "Top.csproj" ], found "*.csproj")
    Assert.Equal<string list>([ "src/Lib/Lib.csproj" ], found "src/Li?/*.csproj")
    Assert.Equal<string list>([ "src/Lib/Nested/Deep.csproj" ], found "src/*/Nested/*.csproj")

    Assert.Equal<string list>(
        [
            "src/App/App.csproj"
            "src/AppTests/AppTests.csproj"
            "src/Lib/Lib.csproj"
            "src/Lib/Nested/Deep.csproj"
        ],
        found "src/**/*.csproj"
    )

    Assert.Equal(5, (found "**/*.csproj").Length)
    // a `?` with no `*` is a glob too
    Assert.Equal<string list>([ "Top.csproj" ], found "To?.csproj")

// ---- --create-config ----

[<Fact>]
let ``create-config writes root = true only where no directory above has an .editorconfig`` () =
    let repo = tempDir ()
    let nested = Path.Combine(repo, "src", "App")
    Directory.CreateDirectory nested |> ignore

    // the temp directory's own ancestors may carry one: only the difference counts
    let alone =
        match ConfigFile.writeInto nested with
        | Ok path -> File.ReadAllText path
        | Error e -> failwith e

    File.Delete(Path.Combine(nested, ".editorconfig"))
    File.WriteAllText(Path.Combine(repo, ".editorconfig"), "root = true\n\n[*.cs]\nindent_size = 4\n")

    let inheriting =
        match ConfigFile.writeInto nested with
        | Ok path -> File.ReadAllText path
        | Error e -> failwith e

    Assert.DoesNotContain("root = true", inheriting)
    Assert.Contains(ConfigFile.Marker, inheriting)
    Assert.Contains(ConfigFile.Marker, alone)

// ---- end to end ----

let private guidSource (name: string) =
    $"using System;\nnamespace Sample;\npublic static class {name}\n{{\n    public static Guid Empty() => new Guid();\n}}\n"

/// A project of two files, each with one CR0090 fix.
let private writeTwoFileProject (dir: string) =
    File.WriteAllText(
        Path.Combine(dir, "Sample.csproj"),
        """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup></Project>"""
    )

    File.WriteAllText(Path.Combine(dir, "First.cs"), guidSource "First")
    File.WriteAllText(Path.Combine(dir, "Second.cs"), guidSource "Second")
    Path.Combine(dir, "Sample.csproj")

[<Collection("Tool")>]
type EndedEarly() =

    [<Fact>]
    member _.``a baseline that cannot be read is a usage error and nothing is fixed``() =
        let dir = tempDir ()
        let project = writeTwoFileProject dir

        let code, said = run [| project; "--baseline"; Path.Combine(dir, "typo.sarif") |]

        Assert.Equal(2, code)
        Assert.Contains("could not read baseline", said)
        Assert.Equal(guidSource "First", File.ReadAllText(Path.Combine(dir, "First.cs")))

    [<Fact>]
    member _.``a report into a directory that does not exist is refused before any work``() =
        let dir = tempDir ()
        let project = writeTwoFileProject dir

        let code, said =
            run [| project; "--report"; Path.Combine(dir, "artifacts", "findings.sarif") |]

        Assert.Equal(2, code)
        Assert.Contains("does not exist", said)
        Assert.Equal(guidSource "First", File.ReadAllText(Path.Combine(dir, "First.cs")))

    [<Fact>]
    member _.``a source that cannot be written fails the run and puts back the ones that were``() =
        let dir = tempDir ()
        let project = writeTwoFileProject dir
        let first = Path.Combine(dir, "First.cs")
        let second = Path.Combine(dir, "Second.cs")

        // whichever the run writes first, the other refuses its write
        File.SetAttributes(first, FileAttributes.ReadOnly)

        try
            let code, said = run [| project; "--codes"; "CR0090" |]

            Assert.Equal(1, code)
            Assert.Contains("put back unverified", said)
            Assert.Equal(guidSource "First", File.ReadAllText first)
            Assert.Equal(guidSource "Second", File.ReadAllText second)
        finally
            File.SetAttributes(first, FileAttributes.Normal)

    [<Fact>]
    member _.``a second run in one process reads a script as it is on disk, not as the first run loaded it``() =
        // an MCP server's calls are runs of one process
        let dir = tempDir ()
        let script = Path.Combine(dir, "build.csx")
        File.WriteAllText(script, "using System;\nvar id = new Guid();\nConsole.WriteLine(id);\n")

        let code, _ = run [| script; "--codes"; "CR0090"; "--dry-run" |]
        Assert.Equal(0, code)

        // an edit between the two calls
        File.WriteAllText(
            script,
            "using System;\nvar id = new Guid();\nConsole.WriteLine(id);\nConsole.WriteLine(\"edited\");\n"
        )

        let code, _ = run [| script; "--codes"; "CR0090" |]
        Assert.Equal(0, code)

        let after = File.ReadAllText script
        Assert.Contains("Guid.Empty", after)
        Assert.Contains("\"edited\"", after)

    [<Fact>]
    member _.``a project at a drive root still owns the files beside it``() =
        let root = Path.GetPathRoot(Path.GetTempPath())
        let folder = Path.Combine(root, "src", "App")

        Assert.Equal(root, Sweep.folderPrefix root)
        Assert.StartsWith(Sweep.folderPrefix root, Path.Combine(root, "Program.cs"))
        Assert.Equal(folder + string Path.DirectorySeparatorChar, Sweep.folderPrefix folder)

    [<Fact>]
    member _.``a write refused after an interrupt ends the run with 130, not with its exception``() =
        try
            let said = ref -1

            Sweep.putBackOnInterruptSaying (fun restored -> said.Value <- restored)
            |> ignore
            // said before the run can go on to its next write
            Assert.Equal(0, said.Value)

            Assert.Equal(
                130,
                Sweep.endingOnInterrupt (fun () -> raise (OperationCanceledException "nothing more is written"))
            )

            // out of the run's task
            Assert.Equal(
                130,
                Sweep.endingOnInterrupt (fun () ->
                    raise (AggregateException(OperationCanceledException "nothing more is written")))
            )

            Sweep.clearInterrupt ()
            Assert.Equal(3, Sweep.endingOnInterrupt (fun () -> 3))

            // a cancellation no interrupt caused is the run's own failure
            Assert.Throws<OperationCanceledException>(fun () ->
                Sweep.endingOnInterrupt (fun () -> raise (OperationCanceledException "a timeout"))
                |> ignore)
            |> ignore
        finally
            Sweep.clearInterrupt ()

    [<Fact>]
    member _.``an interrupt after the verification builds leaves the verified fixes``() =
        let dir = tempDir ()
        let project = writeTwoFileProject dir

        try
            let code, _ = run [| project; "--codes"; "CR0090" |]

            Assert.Equal(0, code)
            Assert.Equal(0, Sweep.putBackOnInterrupt ())
            Assert.Contains("=> Guid.Empty;", File.ReadAllText(Path.Combine(dir, "First.cs")))
        finally
            Sweep.clearInterrupt ()
