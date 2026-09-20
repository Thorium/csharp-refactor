/// --mcp: a minimal MCP server over stdio — newline-delimited JSON-RPC
/// 2.0, no extra dependencies, one process (and its MSBuild) warm across
/// every call. Progress prose is diverted to stderr so the protocol
/// stream stays clean. Ported from fsharp-refactor.
module CSharp.Refactor.Tool.Mcp

open System
open System.Text.Json
open CSharp.Refactor
open CSharp.Refactor.Tool.Options

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
                                    "Analyze a C# project, solution or directory with csharp-refactor. Dry-run by default: reports findings without editing. Set apply=true to write the fixes (build-verified). Returns findings as JSON with stable fingerprints and source snippets."
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
                                                        box (prop "string" "csproj, sln, directory or glob to analyze")
                                                        "codes",
                                                        box (prop "string" "comma-separated rule codes to restrict to")
                                                        "categories",
                                                        box (
                                                            prop
                                                                "string"
                                                                "comma-separated: correctness,performance,idiom,cosmetic"
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

let private handleAnalyze (args: JsonElement) =
    let getString name =
        match args.TryGetProperty(name: string) with
        | true, v when v.ValueKind = JsonValueKind.String -> Some(v.GetString())
        | _ -> None

    let getBool name =
        match args.TryGetProperty(name: string) with
        | true, v -> v.ValueKind = JsonValueKind.True
        | _ -> false

    match getString "target" with
    | None -> Error "analyze needs a 'target'"
    | Some target ->
        match parseArgs [| target |] with
        | Error message -> Error message
        | Ok baseOpts ->
            let codes =
                getString "codes"
                |> Option.map (fun s -> s.Split ',' |> Array.map (fun c -> c.Trim().ToUpperInvariant()) |> Set.ofArray)

            let categories =
                getString "categories"
                |> Option.map (fun s -> s.Split ',' |> Array.choose RuleCatalog.parse |> Set.ofArray)

            let opts =
                { baseOpts with
                    DryRun = not (getBool "apply")
                    ParseOnly = getBool "parseOnly"
                    Codes = codes
                    ExplicitCodes = codes
                    Categories = categories
                }
                |> applyCategories

            Sweep.resetRun ()
            let exitCode = Sweep.executeRun opts
            let findings = Sweep.reportedSoFar ()

            let body =
                dict
                    [
                        "exitCode", box exitCode
                        "applied", box (not opts.DryRun)
                        "findingCount", box findings.Length
                        "findings", box (Reports.findingsPayload findings)
                        "baselineSuppressed", box Sweep.baselineSuppressed
                        "commentSuppressed", box Sweep.commentSuppressed
                        "suppressionsOverridden", box Sweep.suppressionOverridden
                    ]

            Ok(JsonSerializer.Serialize body)

[<return: Struct>]
let inline private (|IsNullOrWhiteSpace|_|) (input: string) =
    if String.IsNullOrWhiteSpace input then
        ValueSome input
    else
        ValueNone

let run () =
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

                    let id =
                        match root.TryGetProperty "id" with
                        | true, v -> v.GetRawText()
                        | _ -> "null"

                    let m =
                        match root.TryGetProperty "method" with
                        | true, v -> v.GetString()
                        | _ -> ""

                    let p =
                        match root.TryGetProperty "params" with
                        | true, v -> Some(v.Clone())
                        | _ -> None

                    id, m, p
                with _ ->
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
                let name, args =
                    match params_ with
                    | Some p ->
                        let n =
                            match p.TryGetProperty "name" with
                            | true, v -> v.GetString()
                            | _ -> ""

                        let a =
                            match p.TryGetProperty "arguments" with
                            | true, v -> v
                            | _ -> JsonDocument.Parse("{}").RootElement

                        n, a
                    | None -> "", JsonDocument.Parse("{}").RootElement

                match name with
                | "list_rules" -> respond idJson (serialize (mcpToolResult (serialize (rulesJson ()))))
                | "analyze" ->
                    try
                        handleAnalyze args
                        |> Result.map (mcpToolResult >> serialize >> respond idJson)
                        |> Result.defaultWith (fun msg -> respondError idJson -32602 msg)
                    with ex ->
                        respondError idJson -32603 $"analyze failed: {ex.Message}"
                | other -> respondError idJson -32601 $"unknown tool '{other}'"
            | "" -> respondError idJson -32700 "unparseable request"
            | notification when not (notification.StartsWith "notifications/") && idJson <> "null" ->
                respondError idJson -32601 $"unknown method '{notification}'"
            | _ -> ()

    0
