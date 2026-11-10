/// The MCP server's limits: it works inside the directory it was started
/// for, writes nowhere else, and a server started with --dry-run applies
/// nothing.
module CSharp.Refactor.Tests.McpServerTests

open System
open System.IO
open System.Text.Json
open Xunit
open CSharp.Refactor.Tool
open CSharp.Refactor.Tool.Options

let private tempDir () =
    let dir =
        Path.Combine(Path.GetTempPath(), "csref-tests", Guid.NewGuid().ToString "N")

    Directory.CreateDirectory dir |> ignore
    dir

/// A script with one CR0090 fix (`new Guid()` to `Guid.Empty`).
let private fixable =
    "using System;\nvar id = new Guid();\nConsole.WriteLine(id);\n"

let private analyzeRequest (id: int) (target: string) (apply: bool) =
    JsonSerializer.Serialize(
        dict
            [
                "jsonrpc", box "2.0"
                "id", box id
                "method", box "tools/call"
                "params",
                box (
                    dict
                        [
                            "name", box "analyze"
                            "arguments", box (dict [ "target", box target; "apply", box apply; "codes", box "CR0090" ])
                        ]
                )
            ]
    )

let private limits (args: string list) : Mcp.Limits =
    match parseArgs (Array.ofList args) |> Result.bind Mcp.limitsOf with
    | Ok limits -> limits
    | Error message -> failwith message

let private refusal (args: string list) : string =
    match parseArgs (Array.ofList args) |> Result.bind Mcp.limitsOf with
    | Error message -> message
    | Ok limits -> failwithf "%A accepted" limits

/// The server run over `requests` as its stdin; returns the protocol lines
/// it answered with.
let private serve (limits: Mcp.Limits) (requests: string list) : string list =
    let oldIn, oldOut, oldErr = Console.In, Console.Out, Console.Error
    use input = new StringReader(String.concat "\n" requests)
    use output = new StringWriter()
    use errors = new StringWriter()
    Console.SetIn input
    Console.SetOut output
    Console.SetError errors

    try
        Assert.Equal(0, Mcp.run limits)

        output.ToString().Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
        |> List.ofArray
    finally
        Console.SetIn oldIn
        Console.SetOut oldOut
        Console.SetError oldErr

/// An answer's error message, or the body of its tool result.
let private read (answer: string) : Result<JsonElement, string> =
    use doc = JsonDocument.Parse answer

    match doc.RootElement.TryGetProperty "error" with
    | true, error -> Error(error.GetProperty("message").GetString())
    | _ ->
        let text =
            doc.RootElement.GetProperty("result").GetProperty("content").[0].GetProperty("text").GetString()

        use body = JsonDocument.Parse text
        Ok(body.RootElement.Clone())

[<Fact>]
let ``a flag the server would ignore, or a root that is no directory, is a usage error`` () =
    let root = tempDir ()
    let file = Path.Combine(root, "build.csx")
    File.WriteAllText(file, fixable)

    Assert.Contains("--report", refusal [ root; "--mcp"; "--report"; Path.Combine(root, "x.sarif") ])
    Assert.Contains("--api-changes", refusal [ root; "--mcp"; "--api-changes" ])
    Assert.Contains("DIRECTORY", refusal [ file; "--mcp" ])

    let served = limits [ root; "--mcp"; "--dry-run"; "--define"; "LOCAL" ]
    Assert.True(Sweep.isUnder served.Root file)
    Assert.True served.ReadOnly
    Assert.Equal<string list>([ "LOCAL" ], served.Defines)
    // without a directory, the one the server is started in
    Assert.Equal(Path.GetFullPath(Directory.GetCurrentDirectory()), (limits [ "--mcp" ]).Root)

[<Fact>]
let ``inside the root is the directory and what lies beneath it, not a sibling its name prefixes`` () =
    let root = Path.Combine(Path.GetTempPath(), "csref-root")

    Assert.True(Sweep.isUnder root root)
    Assert.True(Sweep.isUnder root (Path.Combine(root, "src", "A.cs")))
    Assert.True(Sweep.isUnder (root + string Path.DirectorySeparatorChar) (Path.Combine(root, "A.cs")))
    Assert.False(Sweep.isUnder root (root + "-other"))
    Assert.False(Sweep.isUnder root (Path.Combine(root, "..", "elsewhere", "A.cs")))
    Assert.False(Sweep.isUnder root (Path.GetTempPath()))

[<Collection("Tool")>]
type Served() =

    [<Fact>]
    member _.``a target outside the server's root is refused and left alone``() =
        let root = tempDir ()
        let elsewhere = tempDir ()
        let outside = Path.Combine(elsewhere, "build.csx")
        File.WriteAllText(outside, fixable)

        let answers =
            serve
                (limits [ root; "--mcp" ])
                [
                    analyzeRequest 1 outside true
                    analyzeRequest 2 (Path.Combine(root, "..", Path.GetFileName elsewhere, "build.csx")) true
                ]

        Assert.Equal(2, answers.Length)

        for answer in answers do
            match read answer with
            | Error message -> Assert.Contains("outside this server's root", message)
            | Ok body -> failwithf "answered %s" (body.GetRawText())

        Assert.Equal(fixable, File.ReadAllText outside)

    [<Fact>]
    member _.``a target inside the root is analysed, a relative one from the root``() =
        let root = tempDir ()
        let script = Path.Combine(root, "build.csx")
        File.WriteAllText(script, fixable)

        let answers =
            serve (limits [ root; "--mcp" ]) [ analyzeRequest 1 script false; analyzeRequest 2 "build.csx" false ]

        Assert.Equal(2, answers.Length)

        for answer in answers do
            match read answer with
            | Ok body ->
                Assert.Equal(1, body.GetProperty("findingCount").GetInt32())
                Assert.False(body.GetProperty("applied").GetBoolean())
            | Error message -> failwith message

        Assert.Equal(fixable, File.ReadAllText script)

    [<Fact>]
    member _.``a server started with --dry-run applies nothing``() =
        let root = tempDir ()
        let script = Path.Combine(root, "build.csx")
        File.WriteAllText(script, fixable)

        let answers =
            serve
                (limits [ root; "--mcp"; "--dry-run" ])
                [ analyzeRequest 1 script true; analyzeRequest 2 script false ]

        match answers |> List.map read with
        | [ Error refused; Ok report ] ->
            Assert.Contains("--dry-run", refused)
            Assert.Equal(1, report.GetProperty("findingCount").GetInt32())
        | other -> failwithf "answered %A" other

        Assert.Equal(fixable, File.ReadAllText script)

    [<Fact>]
    member _.``an apply writes inside the root only: a run reaching a linked file outside it is put back whole``() =
        let parent = tempDir ()
        let root = Path.Combine(parent, "root")
        let shared = Path.Combine(parent, "shared")
        Directory.CreateDirectory root |> ignore
        Directory.CreateDirectory shared |> ignore

        let source (name: string) =
            $"using System;\nnamespace Sample;\npublic static class {name}\n{{\n    public static Guid Empty() => new Guid();\n}}\n"

        let outside = Path.Combine(shared, "Lib.cs")
        let own = Path.Combine(root, "Own.cs")
        File.WriteAllText(outside, source "Lib")
        File.WriteAllText(own, source "Own")

        File.WriteAllText(
            Path.Combine(root, "Sample.csproj"),
            """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><Compile Include="..\shared\Lib.cs" Link="Lib.cs" /></ItemGroup></Project>"""
        )

        let answers =
            serve (limits [ root; "--mcp" ]) [ analyzeRequest 1 (Path.Combine(root, "Sample.csproj")) true ]

        match answers |> List.map read with
        | [ Ok body ] ->
            Assert.Equal(1, body.GetProperty("exitCode").GetInt32())

            Assert.Contains(
                "outside",
                body.GetProperty("exitReasons").EnumerateArray()
                |> Seq.map _.GetString()
                |> String.concat "; "
            )
        | other -> failwithf "answered %A" other

        Assert.Equal(source "Lib", File.ReadAllText outside)
        Assert.Equal(source "Own", File.ReadAllText own)
