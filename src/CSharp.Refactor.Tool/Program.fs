/// csharp-refactor: the apply tool, the twin of fsharp-refactor's Program.fs;
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

/// Code the run loads - a project's analyzers and source generators, its
/// MSBuild tasks, the database drivers they reach for - leaves objects
/// behind whose finalizers run on the GC's thread whenever it gets to them.
/// One that throws there ends the process: no try/with reaches the finalizer
/// thread. fsharp-refactor lost an hour-long run that way to the managed
/// Oracle driver, whose OracleConnection finalizer reads its config through
/// System.Security.Permissions, which this runtime does not ship.
///
/// Such an exception belongs to the code the run LOADED, not to the run:
/// it is reported, once per kind and origin, and the run goes on. One with
/// our code anywhere on its stack, or thrown on the main thread, still takes
/// the process down - a bug of ours fails loudly.
///
/// The assembly whose code threw `ex` when the run may survive it: thrown
/// off the main thread, with none of our code on its stack (or its inner
/// exceptions' - a bug of ours that throws inside a BCL call is still ours).
/// Named by the first frame outside the runtime: a finalizer's
/// TypeInitializationException is raised by the runtime's class-constructor
/// machinery, but it is the driver that failed. None when the run must not
/// survive it.
let foreignBackgroundOrigin (mainThread: int) (currentThread: int) (ex: exn) : string option =
    let assemblies =
        try
            Seq.unfold (fun (e: exn) -> if isNull e then None else Some(e, e.InnerException)) ex
            |> Seq.collect (fun e ->
                match Diagnostics.StackTrace(e, false).GetFrames() with
                | null -> Seq.empty
                | frames ->
                    frames
                    |> Seq.choose (fun frame ->
                        match frame.GetMethod() with
                        | null -> None
                        | m -> Some(m.Module.Assembly.GetName().Name)))
            |> Seq.toList
        with _ -> // reflection over a half-loaded assembly; fsharpanalyzer: ignore-line FR0055
            []

    let runtime (name: string) =
        name = "System.Private.CoreLib" || name.StartsWith "System."

    // out of memory is the process's state, not the thrower's: going on is
    // not safe whoever threw it
    if
        currentThread = mainThread
        || (ex :? OutOfMemoryException)
        || (ex.GetBaseException() :? OutOfMemoryException)
        || assemblies |> List.exists (fun name -> name.StartsWith "CSharp.Refactor")
    then
        None
    else
        match assemblies |> List.tryFind (runtime >> not) with
        | Some name -> Some name
        | None -> assemblies |> List.tryHead |> Option.orElse (Some "unknown code")

/// How many background failures of loaded code this run went on past.
let private survivedForeignExceptions = ref 0

/// Installed only when this tool IS the process: the test suite calls into
/// the tool from its own host, whose background failures are not ours to
/// swallow.
let private surviveForeignBackgroundExceptions () =
    if Reflection.Assembly.GetEntryAssembly() = Reflection.Assembly.GetExecutingAssembly() then
        let mainThread = Threading.Thread.CurrentThread.ManagedThreadId
        let reported = Collections.Concurrent.ConcurrentDictionary<string, unit>()

        let handler (ex: exn) =
            match foreignBackgroundOrigin mainThread Threading.Thread.CurrentThread.ManagedThreadId ex with
            | None -> false
            | Some from ->
                Threading.Interlocked.Increment(&survivedForeignExceptions.contents) |> ignore

                if reported.TryAdd($"{ex.GetType().FullName}|{from}", ()) then
                    eprintfn
                        $"  (a background thread of {from} threw {ex.GetType().Name}: {ex.GetBaseException().Message} - ignored; the run goes on, but whatever loaded {from}, typically an analyzer, source generator or MSBuild task, may have analysed incompletely)"

                true

        try
            Runtime.ExceptionServices.ExceptionHandling.SetUnhandledExceptionHandler(Func<exn, bool> handler)
        with :? InvalidOperationException -> // a host set one first; it keeps its say
            ()

let private runMain argv =
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
                $"{Reports.findingsAsJson
                       findings
                       (Sweep.baselineSuppressed ())
                       (Sweep.commentSuppressed ())
                       (Sweep.suppressionOverridden ())}"

        code

[<EntryPoint>]
let main argv =
    surviveForeignBackgroundExceptions ()
    let code = runMain argv

    // a run that went on past loaded code failing on a background thread
    // analysed what it could, not everything: exit 1 says so
    match Threading.Volatile.Read(&survivedForeignExceptions.contents) with
    | 0 -> code
    | survived ->
        eprintfn
            $"exit 1: {survived} background failure(s) of loaded code were survived (see above) - the analysis may be incomplete"

        if code = 0 then 1 else code
