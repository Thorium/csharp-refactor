/// csharp-refactor: the apply tool. Ported from fsharp-refactor's Program.fs;
/// the sweep itself lives in Sweep.fs on Roslyn's MSBuildWorkspace.
module CSharp.Refactor.Tool.Program

open System
open System.IO
open System.Text.Json
open CSharp.Refactor.Tool.Options

let private printRules (json: bool) =
    if json then
        let payload =
            [
                for code, category, enabledByDefault in Mcp.rulesAsRows () ->
                    dict
                        [
                            "code", box code
                            "category", box category
                            "enabledByDefault", box enabledByDefault
                        ]
            ]

        printfn $"{JsonSerializer.Serialize(payload, JsonSerializerOptions(WriteIndented = true))}"
    else
        for code, category, enabledByDefault in Mcp.rulesAsRows () do
            let marker = if enabledByDefault then "" else "  (off by default)"
            printfn $"%s{code}  %-12s{category}%s{marker}"

[<EntryPoint>]
let main argv =
    let parsed = parseArgs argv

    match parsed with
    | Ok opts when opts.NoColor -> Out.goPlain ()
    | _ -> ()

    match parsed with
    | Error message ->
        eprintfn $"{message}"
        2
    | Ok opts when opts.ShowHelp ->
        printfn $"{helpText}"
        0
    | Ok opts when opts.ShowVersion ->
        printfn $"csharp-refactor {Reports.toolVersion.Value}"
        0
    | Ok opts when opts.ListRules ->
        printRules opts.Json
        0
    | Ok opts when opts.CreateConfig ->
        let directory =
            if opts.Target = "" then
                Ok(Directory.GetCurrentDirectory())
            elif Directory.Exists opts.Target then
                Ok opts.Target
            else
                Error $"--create-config writes into a DIRECTORY; '{opts.Target}' is not one."

        match directory |> Result.bind ConfigFile.writeInto with
        | Error message ->
            eprintfn $"{message}"
            2
        | Ok path ->
            printfn
                $"Wrote the csharp-refactor block into {path} — every rule at this build's default, so it changes nothing until you edit it."

            0
    | Ok opts when opts.Mcp -> Mcp.run ()
    | Ok opts when opts.Target = "" ->
        printfn $"{helpText}"
        2
    | Ok opts ->
        let realOut = Console.Out

        if opts.Json then
            Console.SetOut Console.Error

        Sweep.resetRun ()
        let code = Sweep.executeRun opts

        if opts.Json then
            Console.SetOut realOut
            let findings = Sweep.reportedSoFar ()

            printfn
                $"{Reports.findingsAsJson findings Sweep.baselineSuppressed Sweep.commentSuppressed Sweep.suppressionOverridden}"

        code
