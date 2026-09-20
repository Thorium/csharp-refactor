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
    File.WriteAllText(path, "root = true\n[*.cs]\ndotnet_diagnostic.CR0103.severity = none\n")

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

// ---- targets ----

[<Fact>]
let ``a source file resolves to the project whose directory holds it`` () =
    let dir = tempDir ()
    let project = Path.Combine(dir, "Lib.csproj")
    File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>")
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
    File.WriteAllText(a, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>")
    File.WriteAllText(b, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>")

    File.WriteAllText(
        Path.Combine(dir, "All.slnx"),
        "<Solution><Project Path=\"A/A.csproj\" /><Project Path=\"B/B.fsproj\" /></Solution>"
    )

    match Targets.resolveTargets dir with
    | Ok [ Targets.Target.Project(p, None) ] -> Assert.True(Workspace.samePath a p)
    | other -> failwithf "unexpected %A" other

    match Targets.resolveTargets (Path.Combine(dir, "**", "*.csproj")) with
    | Ok [ Targets.Target.Project(p, None) ] -> Assert.True(Workspace.samePath a p)
    | other -> failwithf "unexpected %A" other

// ---- end to end ----

let private sampleSource =
    """using System;
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
        "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup></Project>"
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
        Assert.Contains("=> \"no holes here\";", after)
        Assert.Contains("$\"value {x}\";", after)
        Assert.Equal(2, Sweep.runTotalApplied)

        // idempotent
        Assert.Equal(0, run [| project |])
        Assert.Equal(0, Sweep.runTotalApplied)

    [<Fact>]
    member _.``a csx script is its own compilation: #r and #load resolve, fixes apply, the run is verified in memory``
        ()
        =
        let dir = tempDir ()
        File.WriteAllText(Path.Combine(dir, "helpers.csx"), "public static int Twice(int x) => x * 2;\n")

        let script = Path.Combine(dir, "build.csx")

        File.WriteAllText(
            script,
            "#r \"System.Xml.dll\"\n#load \"helpers.csx\"\nusing System.Xml;\n\nvar items = new List<string> { \"a\", \"\", \"b\" };\nforeach (var item in items)\n{\n    if (item.Length == 0) items.Remove(item);\n}\nvar actions = new List<Action>();\nfor (int i = 0; i < 3; i++)\n{\n    actions.Add(() => Console.WriteLine(Twice(i)));\n}\nvar doc = new XmlDocument();\nvar empty = new Guid();\nConsole.WriteLine(doc.OuterXml + empty + actions.Count);\n"
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
        Assert.Contains("var i1 = i;\n    actions.Add(() => Console.WriteLine(Twice(i1)));", after)
        Assert.Contains("var empty = Guid.Empty;", after)

        // the directory form picks the loose scripts up too
        match Targets.resolveTargets dir with
        | Ok targets ->
            let names = targets |> List.map (Targets.projectOf >> Path.GetFileName) |> List.sort
            Assert.Equal<string list>([ "build.csx"; "helpers.csx" ], names)
        | Error e -> failwith e

        Assert.Equal(0, run [| script |])
        Assert.Equal(0, Sweep.runTotalApplied)

    [<Fact>]
    member _.``a taskified method's callers in another file are rewritten in the same pass and the project still builds``
        ()
        =
        let dir = tempDir ()

        File.WriteAllText(
            Path.Combine(dir, "Sample.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"
        )

        File.WriteAllText(
            Path.Combine(dir, "Service.cs"),
            "using System.Threading.Tasks;\nnamespace Sample;\ninternal static class Service\n{\n    static Task<int> Source() => Task.FromResult(1);\n    internal static int Load() { var x = Source().Result; return x; }\n}\n"
        )

        File.WriteAllText(
            Path.Combine(dir, "Caller.cs"),
            "using System.Threading.Tasks;\nnamespace Sample;\nclass Caller\n{\n    async Task<int> Run()\n    {\n        var x = Service.Load();\n        var y = Service.Load();\n        return x + y;\n    }\n}\n"
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
        Assert.Equal(1, Sweep.runTotalApplied)

        Assert.Equal(0, run [| project; "--codes"; "CR0041" |])
        Assert.Equal(0, Sweep.runTotalApplied)

    [<Fact>]
    member _.``under the api pass a friend project's callers are rewritten through the reference oracle and both projects build``
        ()
        =
        let dir = tempDir ()
        Directory.CreateDirectory(Path.Combine(dir, "A")) |> ignore
        Directory.CreateDirectory(Path.Combine(dir, "B")) |> ignore

        File.WriteAllText(
            Path.Combine(dir, "A", "A.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><InternalsVisibleTo Include=\"B\" /></ItemGroup></Project>"
        )

        File.WriteAllText(
            Path.Combine(dir, "A", "Service.cs"),
            "using System.Threading.Tasks;\nnamespace A;\ninternal static class Service\n{\n    static Task<int> Source() => Task.FromResult(1);\n    internal static int Load() { var x = Source().Result; return x; }\n}\n"
        )

        File.WriteAllText(
            Path.Combine(dir, "B", "B.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><ProjectReference Include=\"../A/A.csproj\" /></ItemGroup></Project>"
        )

        File.WriteAllText(
            Path.Combine(dir, "B", "Caller.cs"),
            "using System.Threading.Tasks;\nnamespace B;\nclass Caller\n{\n    async Task<int> Run()\n    {\n        var x = A.Service.Load();\n        return x;\n    }\n}\n"
        )

        File.WriteAllText(
            Path.Combine(dir, "All.slnx"),
            "<Solution><Project Path=\"A/A.csproj\" /><Project Path=\"B/B.csproj\" /></Solution>"
        )

        let run (args: string[]) =
            Sweep.resetRun ()

            match parseArgs args with
            | Ok opts -> Sweep.executeRun opts
            | Error e -> failwith e

        // without the api pass the friend holds the surface: nothing changes
        Assert.Equal(0, run [| dir; "--codes"; "CR0041" |])
        Assert.Equal(0, Sweep.runTotalApplied)

        Assert.Equal(0, run [| dir; "--api-changes"; "--codes"; "CR0041" |])
        let service = File.ReadAllText(Path.Combine(dir, "A", "Service.cs"))
        let caller = File.ReadAllText(Path.Combine(dir, "B", "Caller.cs"))
        Assert.Contains("internal static async Task<int> LoadAsync() { var x = await Source(); return x; }", service)
        Assert.Contains("var x = await A.Service.LoadAsync();", caller)
        Assert.Equal(1, Sweep.runTotalApplied)

        Assert.Equal(0, run [| dir; "--api-changes"; "--codes"; "CR0041" |])
        Assert.Equal(0, Sweep.runTotalApplied)

    [<Fact>]
    member _.``a file that is not UTF-8 goes back in its own encoding, byte for byte outside the edit``() =
        let dir = tempDir ()

        File.WriteAllText(
            Path.Combine(dir, "Sample.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"
        )

        // Windows-1252: `×` is the single byte 0xD7, which is not valid UTF-8 —
        // written as raw bytes, so the tool's own code-page handling is what is tested
        let ascii (s: string) = Text.Encoding.ASCII.GetBytes s

        let original =
            Array.concat
                [
                    ascii "using System;\n// a 3"
                    [| 0xD7uy |]
                    ascii "3 matrix\nclass C { Guid A() => new Guid(); }\n"
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

// ---- legacy projects ----

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
                "<Project ToolsVersion=\"15.0\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\"><PropertyGroup><TargetFrameworkVersion>v4.7.2</TargetFrameworkVersion><OutputType>Library</OutputType></PropertyGroup><ItemGroup><Compile Include=\"A.cs\" /></ItemGroup></Project>"
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
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"
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
        Assert.Equal(0, p.ExitCode)
        let! _ = stderr
        ()
    }
    :> System.Threading.Tasks.Task
