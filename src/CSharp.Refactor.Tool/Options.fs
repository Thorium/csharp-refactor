/// The command line: the same contract as fsharp-refactor, minus the
/// script and F#-only flags.
module CSharp.Refactor.Tool.Options

open System
open CSharp.Refactor

type Options =
    {
        Target: string
        ShowHelp: bool
        ShowVersion: bool
        Codes: Set<string> option
        /// The codes the user TYPED in --codes, before any --categories
        /// expansion is folded into `Codes`. Only these outrank a rule's
        /// default-off status or a config disable: naming a rule is an
        /// ask, a category is merely a filter.
        ExplicitCodes: Set<string> option
        Categories: Set<RuleCatalog.Category> option
        DryRun: bool
        NoColor: bool
        ApiChanges: bool
        NoIfDefs: bool
        Report: string option
        ParseOnly: bool
        Baseline: string option
        FailOnFindings: bool
        HonorSuppressions: bool
        Notes: bool
        NotesOnly: bool
        Json: bool
        ListRules: bool
        CreateConfig: bool
        Mcp: bool
        MaxPasses: int
        Jobs: int
        Framework: string
    }

let helpText =
    """csharp-refactor — applies C# refactoring quick fixes to your code.

USAGE
  csharp-refactor <what> [options]

WHAT TO FIX — the kind is read off the path, no flag needed:
  Your.csproj           one project
  Thing.cs              one source file; its project is found and analysed,
                        but only that file is edited
  Your.sln, Your.slnx   every C# project the solution lists
  src/                  the solution in that directory, or the projects beneath
  "src/**/*.csproj"     everything the glob matches
  build.csx             one C# script: its own compilation, no MSBuild at all

OPTIONS
  --dry-run             report every fix, change nothing. Rewriting is never
                        implicit: without this it edits, with it it does not
  --codes CR0090,CR0103 only these rules
  --categories <list>   only rules of these kinds: correctness, performance,
                        idiom, cosmetic. Combined with --codes it narrows
                        further. For a repository you do not maintain,
                        "correctness,performance" is the set worth a pull
                        request; nobody welcomes a stranger's punctuation
  --jobs <n>            projects analysed at once (default: cores, at most 4)
  --framework <tfm>     analyse only this target framework. By default a
                        multi-targeted project is worked through framework by
                        framework, narrowest first, because code behind another
                        framework's #if is not in the parse tree at all
  --api-changes         also apply fixes that change internal or public
                        signatures and shapes, rewriting call sites across
                        the solution. Held back and merely counted without
                        this
  --no-color            plain output, even on a colour-capable terminal.
                        NO_COLOR=1 and TERM=dumb do the same, and a piped or
                        redirected stream is never coloured either way
  --no-if-defs          never emit #if/#else/#endif pairs for capability
                        fixes on multi-targeted projects. The fixes stay
                        plain, and any that break a legacy framework are
                        simply put back by the final build check
  --report <file>       write every finding to a file: .sarif (SARIF 2.1.0,
                        what CI turns into inline annotations), .html (a
                        self-contained page) or .csv. Pairs with --dry-run
  --parse-only          no MSBuild, no references: sources come straight from
                        the project directory and only the syntax-only rules
                        run. NOT a substitute for a real run: the typed rules
                        — most of the correctness and performance ones — are
                        excluded outright
  --baseline <sarif>    findings whose fingerprints appear in this earlier
                        report are neither reported nor fixed: the ratchet.
                        Triage once, then only NEW findings surface
  --fail-on-findings    exit 3 when any finding survives the filters — the
                        hard CI gate (0 clean, 1 failure, 2 usage)
  --honor-suppressions  honor every #pragma / SuppressMessage regardless of
                        the config's csharp_refactor.suppressions policy —
                        the CI override for a repo that wants them inert
                        locally
  --notes [on|off|only] on (the bare flag) lists fix-less advisory notes
                        inline; only lists nothing but them. By default a
                        run prints its FIXES and ends with one per-category
                        note count; SARIF (--report) and --format json
                        always carry the notes in full
  --format json         machine-readable stdout: progress prose moves to
                        stderr and the findings leave as one JSON document.
                        The default stays human-readable
  --rules               print the rule catalog (honors --format json)
  --create-config       append a commented block of every rule and run-level
                        key at its default to the .editorconfig in the
                        current directory (or in <what> when that is a
                        directory), writing one when there is none. Never
                        rewrites a line that exists
  --mcp                 serve analyze/list_rules as an MCP server over
                        stdio, keeping the workspace warm between calls
  --max-passes <n>      fix-then-reanalyse iterations (default 5)
  --version, -v         print the version being invoked and stop
  --help, -h, /?        this text

A run refuses a compilation that already has errors, and fails loudly if
applying introduces one. For a multi-targeted project every framework is built
before it reports success.

Rules are configured per repository in .editorconfig:
  dotnet_diagnostic.CR0103.severity = none
  csharp_refactor.CR0006.then_at_least = 30
Full documentation: https://github.com/Thorium/csharp-refactor"""

let private valueFlags =
    [
        "--report"
        "--baseline"
        "--format"
        "--framework"
        "--jobs"
        "--max-passes"
        "--codes"
        "--categories"
    ]

[<TailCall>]
let rec private parseArgsLoop opts args =
    match args with
    | [] -> Ok opts
    | "--project" :: path :: rest -> parseArgsLoop { opts with Target = path } rest
    | "--codes" :: codes :: rest ->
        let parsed =
            codes.Split ','
            |> Array.map (fun c -> c.Trim().ToUpperInvariant())
            |> Array.filter (fun c -> c <> "")
            |> Set.ofArray

        // an unrecognised code is otherwise pure silence: one digit short
        // sweeps the whole project, matches nothing, and reports clean
        match parsed |> Set.filter (RuleCatalog.known.Contains >> not) |> Set.toList with
        | [] ->
            parseArgsLoop
                { opts with
                    Codes = Some parsed
                    ExplicitCodes = Some parsed
                }
                rest
        | unknown ->
            let listed = String.concat ", " unknown
            Error $"not a rule code: {listed}. --rules lists every code."
    | "--categories" :: names :: rest ->
        let parsed = names.Split(',') |> Array.map RuleCatalog.parse

        match parsed |> Array.tryFindIndex Option.isNone with
        | Some bad ->
            let known = RuleCatalog.all |> List.map RuleCatalog.name |> String.concat ", "
            Error $"'{names.Split(',').[bad].Trim()}' is not a category. Known categories: {known}."
        | None ->
            parseArgsLoop
                { opts with
                    Categories = Some(parsed |> Array.choose id |> Set.ofArray)
                }
                rest
    | "--help" :: _
    | "-h" :: _
    | "/?" :: _
    | "-?" :: _ -> Ok { opts with ShowHelp = true }
    | "--version" :: _
    | "-v" :: _ -> Ok { opts with ShowVersion = true }
    | "--dry-run" :: rest -> parseArgsLoop { opts with DryRun = true } rest
    | "--no-color" :: rest -> parseArgsLoop { opts with NoColor = true } rest
    | "--api-changes" :: rest -> parseArgsLoop { opts with ApiChanges = true } rest
    | "--no-if-defs" :: rest -> parseArgsLoop { opts with NoIfDefs = true } rest
    | "--report" :: file :: rest -> parseArgsLoop { opts with Report = Some file } rest
    | "--parse-only" :: rest -> parseArgsLoop { opts with ParseOnly = true } rest
    | "--baseline" :: file :: rest -> parseArgsLoop { opts with Baseline = Some file } rest
    | "--fail-on-findings" :: rest -> parseArgsLoop { opts with FailOnFindings = true } rest
    | "--honor-suppressions" :: rest -> parseArgsLoop { opts with HonorSuppressions = true } rest
    | "--notes" :: ("off" | "on" | "only" as mode) :: rest ->
        match mode with
        | "off" ->
            parseArgsLoop
                { opts with
                    Notes = false
                    NotesOnly = false
                }
                rest
        | "on" -> parseArgsLoop { opts with Notes = true } rest
        | _ ->
            parseArgsLoop
                { opts with
                    NotesOnly = true
                    Notes = true
                    DryRun = true
                }
                rest
    | "--notes" :: rest -> parseArgsLoop { opts with Notes = true } rest
    | "--format" :: "json" :: rest -> parseArgsLoop { opts with Json = true } rest
    | "--format" :: other :: _ -> Error $"--format knows 'json' (the default output is human-readable); got '{other}'"
    | "--rules" :: rest -> parseArgsLoop { opts with ListRules = true } rest
    | "--create-config" :: rest -> parseArgsLoop { opts with CreateConfig = true } rest
    | "--mcp" :: rest -> parseArgsLoop { opts with Mcp = true } rest
    | "--framework" :: tfm :: rest -> parseArgsLoop { opts with Framework = tfm } rest
    | "--jobs" :: n :: rest ->
        match Int32.TryParse n with
        | true, jobs when jobs > 0 -> parseArgsLoop { opts with Jobs = jobs } rest
        | _ -> Error $"--jobs needs a positive number, got '{n}'"
    | "--max-passes" :: n :: rest ->
        match Int32.TryParse n with
        | true, passes when passes > 0 -> parseArgsLoop { opts with MaxPasses = passes } rest
        | _ -> Error $"--max-passes needs a positive number, got '{n}'"
    | path :: rest when not (path.StartsWith '-') && opts.Target = "" -> parseArgsLoop { opts with Target = path } rest
    | extra :: _ when not (extra.StartsWith '-') -> Error $"'{extra}' is a second target; one target per run"
    | [ flag ] when List.contains flag valueFlags -> Error $"'{flag}' needs a value after it"
    | unknown :: _ -> Error $"Unknown argument '{unknown}'"

/// Fold `--categories` into `--codes`, order-independently.
let applyCategories (opts: Options) =
    match opts.Categories with
    | None -> opts
    | Some wanted ->
        let fromCategories = RuleCatalog.codesIn wanted

        let combined =
            opts.Codes
            |> Option.map (fun explicitCodes -> Set.intersect explicitCodes fromCategories)
            |> Option.defaultValue fromCategories

        { opts with Codes = Some combined }

let defaults =
    {
        Target = ""
        ShowHelp = false
        ShowVersion = false
        Codes = None
        ExplicitCodes = None
        Categories = None
        DryRun = false
        NoColor = false
        ApiChanges = false
        NoIfDefs = false
        Report = None
        ParseOnly = false
        Baseline = None
        FailOnFindings = false
        HonorSuppressions = false
        Notes = false
        NotesOnly = false
        Json = false
        ListRules = false
        CreateConfig = false
        Mcp = false
        MaxPasses = 5
        Jobs = min 4 (max 1 Environment.ProcessorCount)
        Framework = ""
    }

let parseArgs (argv: string[]) =
    parseArgsLoop defaults (List.ofArray argv) |> Result.map applyCategories
