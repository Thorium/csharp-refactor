/// --mcp: a minimal MCP server over stdio — newline-delimited JSON-RPC
/// 2.0, no extra dependencies, one process (and its MSBuild) warm across
/// every call. Progress prose is diverted to stderr so the protocol
/// stream stays clean. Mirrors fsharp-refactor's.
module CSharp.Refactor.Tool.Mcp

open System
open System.Text.Json
open CSharp.Refactor
open CSharp.Refactor.Tool.Options

/// What a server holds every call to, from its own command line:
/// `csharp-refactor [directory] --mcp [--dry-run] [--define ...] [--jobs n]`.
type Limits =
    {
        /// The directory the server works in: the one it was given, else
        /// the one it was started in. A call's target lies inside it, and
        /// so does everything a call writes. A client is an agent acting on
        /// what it read, and a server that analyses any path it is handed
        /// builds, and with `apply` rewrites, any tree on the machine.
        Root: string
        /// `--dry-run`: no call applies anything.
        ReadOnly: bool
        /// `--define`: symbols every call keeps (a call's `defines` adds to them).
        Defines: string list
        Jobs: int
    }

/// The server's limits from its command line, or why the line is refused:
/// a root that is no directory, or a flag that belongs to a run and would
/// be ignored here, where it reads as honoured.
let limitsOf (opts: Options) : Result<Limits, string> =
    let ignored =
        [
            "--codes", opts.ExplicitCodes.IsSome
            "--categories", opts.Categories.IsSome
            "--exclude-codes", not opts.ExcludedCodes.IsEmpty
            "--api-changes", opts.ApiChanges
            "--report", opts.Report.IsSome
            "--parse-only", opts.ParseOnly
            "--baseline", opts.Baseline.IsSome
            "--fail-on-findings", opts.FailOnFindings
            "--honor-suppressions", opts.HonorSuppressions
            "--notes", opts.Notes || opts.NotesOnly
            "--format", opts.Json
            "--framework", opts.Framework <> ""
            "--max-passes", opts.MaxPasses <> defaults.MaxPasses
        ]
        |> List.filter snd
        |> List.map fst

    if not ignored.IsEmpty then
        let listed = String.concat ", " ignored

        Error
            $"--mcp serves many runs, and {listed} would have no effect on them. The server takes a directory, --dry-run (it then applies nothing), --define and --jobs; an analyze call names its own codes, excludeCodes, categories and parseOnly."
    elif opts.Target <> "" && not (IO.Directory.Exists opts.Target) then
        Error
            $"--mcp takes the DIRECTORY the server may work in; '{opts.Target}' is not one. Without one it is the current directory."
    else
        Ok
            {
                Root =
                    IO.Path.GetFullPath(
                        if opts.Target = "" then
                            IO.Directory.GetCurrentDirectory()
                        else
                            opts.Target
                    )
                // `--notes only` implies a dry run, and is refused above
                ReadOnly = opts.DryRun
                Defines = opts.Defines
                Jobs = opts.Jobs
            }

let private mcpToolResult (text: string) =
    dict [ "content", box [ dict [ "type", box "text"; "text", box text ] ] ]

let rulesAsRows () =
    RuleCatalog.rules
    |> List.map (fun r -> r.Code, RuleCatalog.name r.Category, r.Default)

let private rulesJson () =
    [
        for code, category, enabledByDefault in rulesAsRows () ->
            dict
                [
                    "code", box code
                    "category", box category
                    "enabledByDefault", box enabledByDefault
                ]
    ]

let private toolsJson =
    let prop (t: string) (description: string) =
        dict [ "type", box t; "description", box description ]

    JsonSerializer.Serialize(
        dict
            [
                "tools",
                box
                    [
                        dict
                            [
                                "name", box "analyze"
                                "description",
                                box
                                    "Analyze a C# project, solution or directory with csharp-refactor, inside the directory this server was started for. Dry-run by default: reports findings without editing. Set apply=true to write the fixes (build-verified); a server started with --dry-run refuses it. Returns findings as JSON with stable fingerprints and source snippets."
                                "inputSchema",
                                box (
                                    dict
                                        [
                                            "type", box "object"
                                            "properties",
                                            box (
                                                dict
                                                    [
                                                        "target",
                                                        box (
                                                            prop
                                                                "string"
                                                                "csproj, sln, directory or glob to analyze, inside the server's root; a relative path is relative to it"
                                                        )
                                                        "codes",
                                                        box (prop "string" "comma-separated rule codes to restrict to")
                                                        "excludeCodes",
                                                        box (
                                                            prop
                                                                "string"
                                                                "comma-separated rule codes to leave out; wins over codes and categories"
                                                        )
                                                        "categories",
                                                        box (
                                                            prop
                                                                "string"
                                                                "comma-separated: correctness,performance,idiom,cosmetic"
                                                        )
                                                        "defines",
                                                        box (
                                                            prop
                                                                "string"
                                                                "preprocessor symbols to define, ;-separated, like --define"
                                                        )
                                                        "parseOnly",
                                                        box (prop "boolean" "no references, syntactic rules only")
                                                        "apply",
                                                        box (prop "boolean" "write the fixes (default: dry-run)")
                                                    ]
                                            )
                                            "required", box [ "target" ]
                                        ]
                                )
                            ]
                        dict
                            [
                                "name", box "list_rules"
                                "description", box "The rule catalog: code, category, enabled-by-default."
                                "inputSchema", box (dict [ "type", box "object"; "properties", box (dict []) ])
                            ]
                    ]
            ]
    )

let private handleAnalyze (limits: Limits) (args: JsonElement) =
    let getString name =
        match args.TryGetProperty(name: string) with
        | true, v when v.ValueKind = JsonValueKind.String -> Some(v.GetString())
        | _ -> None

    let getBool name =
        match args.TryGetProperty(name: string) with
        | true, v -> v.ValueKind = JsonValueKind.True
        | _ -> false

    // the target as a full path inside the root; a relative one is relative
    // to the root, not to wherever the process stands
    let inRoot (target: string) : Result<string, string> =
        try
            let full = IO.Path.GetFullPath(target, limits.Root)

            if Sweep.isUnder limits.Root full then
                Ok full
            else
                Error
                    $"'{target}' is outside this server's root {limits.Root}. Start the server for the directory it may work in: csharp-refactor <directory> --mcp"
        with
        | :? ArgumentException
        | :? NotSupportedException
        | :? IO.PathTooLongException -> Error $"'{target}' is not a usable path"

    match getString "target" |> Option.map inRoot with
    | None -> Error "analyze needs a 'target'"
    | Some(Error refusal) -> Error refusal
    | Some(Ok _) when limits.ReadOnly && getBool "apply" ->
        Error "this server was started with --dry-run and applies nothing; call analyze without apply"
    | Some(Ok target) ->
        // codes, excludeCodes and categories go through the command line's
        // own parser: an unknown code or category is an error there, never
        // an empty, clean run
        let argv =
            [|
                target
                match getString "codes" with
                | Some codes -> yield! [ "--codes"; codes ]
                | None -> ()
                match getString "excludeCodes" with
                | Some codes -> yield! [ "--exclude-codes"; codes ]
                | None -> ()
                match getString "categories" with
                | Some categories -> yield! [ "--categories"; categories ]
                | None -> ()
                match getString "defines" with
                | Some defines -> yield! [ "--define"; defines ]
                | None -> ()
            |]

        match parseArgs argv with
        | Error message -> Error message
        | Ok baseOpts ->
            let opts =
                { baseOpts with
                    DryRun = not (getBool "apply")
                    ParseOnly = getBool "parseOnly"
                    Defines = List.distinct (limits.Defines @ baseOpts.Defines)
                    Jobs = limits.Jobs
                }

            Sweep.resetRun ()
            let exitCode = Sweep.executeRun opts
            let findings = Sweep.reportedSoFar ()

            let body =
                dict
                    [
                        "exitCode", box exitCode
                        // why the exit code is not 0, where the run says
                        "exitReasons", box (Sweep.runExitReasons ())
                        "applied", box (not opts.DryRun)
                        "findingCount", box findings.Length
                        "findings", box (Reports.findingsPayload findings)
                        "baselineSuppressed", box (Sweep.baselineSuppressed ())
                        "commentSuppressed", box (Sweep.commentSuppressed ())
                        "suppressionsOverridden", box (Sweep.suppressionOverridden ())
                    ]

            Ok(JsonSerializer.Serialize body)

[<return: Struct>]
let inline private (|IsNullOrWhiteSpace|_|) (input: string) =
    if String.IsNullOrWhiteSpace input then
        ValueSome input
    else
        ValueNone

let private serve (limits: Limits) =
    let protocolOut = Console.Out
    Console.SetOut Console.Error

    let respond (idJson: string) (resultJson: string) =
        protocolOut.WriteLine $"{{\"jsonrpc\":\"2.0\",\"id\":{idJson},\"result\":{resultJson}}}"
        protocolOut.Flush()

    let respondError (idJson: string) (code: int) (message: string) =
        let msg = JsonSerializer.Serialize message

        protocolOut.WriteLine(
            $"{{\"jsonrpc\":\"2.0\",\"id\":{idJson},\"error\":{{\"code\":{code},\"message\":{msg}}}}}"
        )

        protocolOut.Flush()

    let serialize (o: obj) = JsonSerializer.Serialize o
    let version = Reports.toolVersion.Value
    let mutable running = true

    while running do
        match Console.In.ReadLine() with
        | null -> running <- false
        | IsNullOrWhiteSpace _ -> ()
        | line ->
            let idJson, method_, params_ =
                try
                    use doc = JsonDocument.Parse line
                    let root = doc.RootElement

                    let pid =
                        match root.TryGetProperty "id" with
                        | true, v -> v.GetRawText()
                        | _ -> "null"

                    // `"method": null` (or a number) is no method: unparseable
                    let m =
                        match root.TryGetProperty "method" with
                        | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
                        | _ -> ""

                    let p =
                        match root.TryGetProperty "params" with
                        | true, v -> Some(v.Clone())
                        | _ -> None

                    pid, m, p
                with _ -> // an unparsable request answers as a request without an id; fsharpanalyzer: ignore-line FR0055
                    "null", "", None

            match method_ with
            | "initialize" ->
                respond
                    idJson
                    $"{{\"protocolVersion\":\"2024-11-05\",\"capabilities\":{{\"tools\":{{}}}},\"serverInfo\":{{\"name\":\"csharp-refactor\",\"version\":\"{version}\"}}}}"
            | "notifications/initialized"
            | "notifications/cancelled" -> ()
            | "ping" -> respond idJson "{}"
            | "tools/list" -> respond idJson toolsJson
            | "tools/call" ->
                // a malformed call is an invalid-params error, never a crash of the server
                let call =
                    match params_ with
                    | Some p when p.ValueKind = JsonValueKind.Object ->
                        let n =
                            match p.TryGetProperty "name" with
                            | true, v when v.ValueKind = JsonValueKind.String -> Some(v.GetString())
                            | _ -> None

                        let a =
                            match p.TryGetProperty "arguments" with
                            | true, v when v.ValueKind = JsonValueKind.Object -> Some v
                            | true, _ -> None
                            | _ -> Some(JsonDocument.Parse("{}").RootElement)

                        match n, a with
                        | Some n, Some a -> Ok(n, a)
                        | None, _ -> Error "tools/call needs a string 'name'"
                        | _, None -> Error "tools/call 'arguments' must be an object"
                    | Some _ -> Error "tools/call 'params' must be an object"
                    | None -> Error "tools/call needs 'params'"

                match call with
                | Error message -> respondError idJson -32602 message
                | Ok("list_rules", _) -> respond idJson (serialize (mcpToolResult (serialize (rulesJson ()))))
                | Ok("analyze", args) ->
                    try
                        handleAnalyze limits args
                        |> Result.map (mcpToolResult >> serialize >> respond idJson)
                        |> Result.defaultWith (fun msg -> respondError idJson -32602 msg)
                    with ex ->
                        respondError idJson -32603 $"analyze failed: {ex.Message}"
                | Ok(other, _) -> respondError idJson -32601 $"unknown tool '{other}'"
            | "" -> respondError idJson -32700 "unparseable request"
            | notification when not (notification.StartsWith "notifications/") && idJson <> "null" ->
                respondError idJson -32601 $"unknown method '{notification}'"
            | _ -> ()

    0

/// The server from its limits to the end of its input: every call is held
/// to them, and the process is left as it was found, since a host that runs
/// the server in-process (the tests) goes on after it.
let run (limits: Limits) : int =
    let out = Console.Out
    Sweep.setWriteRoot (Some limits.Root)

    try
        serve limits
    finally
        Sweep.setWriteRoot None
        Console.SetOut out
