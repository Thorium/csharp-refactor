/// The sweep: load the compilations through MSBuildWorkspace, run the
/// analyzers, apply the non-overlapping fixes bottom-up, recompile in
/// memory, repeat until a pass applies nothing, and let a real build be
/// the final arbiter. The fixes come from the same pure rules the editor
/// runs; the diagnostics come through CompilationWithAnalyzers, so
/// .editorconfig severities, pragmas and generated-code skipping are
/// Roslyn's own answers.
module CSharp.Refactor.Tool.Sweep

open System
open System.Collections.Generic
open System.Collections.Immutable
open System.IO
open System.Threading
open Microsoft.Build.Locator
open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.Diagnostics
open Microsoft.CodeAnalysis.MSBuild
open Microsoft.CodeAnalysis.Text
open CSharp.Refactor
open CSharp.Refactor.Roslyn
open CSharp.Refactor.Tool.Options
open CSharp.Refactor.Tool.Reports
open CSharp.Refactor.Tool.Targets

// ---- run-scoped state, reset per executeRun (a resident --mcp host runs many) ----

let mutable private baselineFingerprints: Set<string> = Set.empty
let mutable baselineSuppressed = 0
let mutable commentSuppressed = 0
let mutable suppressionOverridden = 0
let mutable private honorAllSuppressions = false
let mutable private showNotes = false
let mutable private notesOnly = false
let private heldNoteCounts = Dictionary<string, int>()
let private heldByScope = Dictionary<string, int>()
let private reportedFindings = ResizeArray<ReportedFinding>()
let private reportedKeys = HashSet<string>()
let private printedNotes = HashSet<string>()
let mutable runTotalApplied = 0
let mutable private runBuildFailures = 0
let mutable private runCrossFileHeld = 0
let private exitReasons = ResizeArray<string>()

let reportedSoFar () =
    lock reportedFindings (fun () -> List.ofSeq reportedFindings)

let resetRun () =
    lock reportedFindings (fun () ->
        reportedFindings.Clear()
        reportedKeys.Clear())

    printedNotes.Clear()
    heldNoteCounts.Clear()
    heldByScope.Clear()
    exitReasons.Clear()
    baselineSuppressed <- 0
    commentSuppressed <- 0
    suppressionOverridden <- 0
    runTotalApplied <- 0
    runBuildFailures <- 0
    runCrossFileHeld <- 0

let private recordForReport (finding: ReportedFinding) =
    lock reportedFindings (fun () ->
        let file =
            try
                Path.GetFullPath(finding.File).ToLowerInvariant()
            with _ ->
                finding.File.ToLowerInvariant()

        let key =
            $"{finding.Code}|{file}|{finding.StartLine}|{finding.StartColumn}|{finding.Message}"

        if reportedKeys.Add key then
            reportedFindings.Add finding)

// ---- files ----

let private encodingOf (path: string) : Text.Encoding =
    let bom =
        try
            use fs = File.OpenRead path
            let buffer = Array.zeroCreate 3
            let n = fs.Read(buffer, 0, 3)
            Array.truncate n buffer
        with
        | :? IOException
        | :? UnauthorizedAccessException -> [||]

    match bom with
    | [| 0xEFuy; 0xBBuy; 0xBFuy |] -> Text.UTF8Encoding true
    | _ when bom.Length >= 2 && bom.[0] = 0xFFuy && bom.[1] = 0xFEuy -> Text.Encoding.Unicode
    | _ when bom.Length >= 2 && bom.[0] = 0xFEuy && bom.[1] = 0xFFuy -> Text.Encoding.BigEndianUnicode
    | _ -> Text.UTF8Encoding false

/// Write a source text back in the encoding it was read with — Roslyn
/// decodes a file that is not valid UTF-8 as the system code page, and a
/// legacy file's `×` or `ä` must go back as the bytes it came in — else in
/// the encoding its bytes announce.
let private writeSource (path: string) (text: SourceText) =
    let encoding =
        match text.Encoding with
        | null -> encodingOf path
        | e -> e

    File.WriteAllText(path, text.ToString(), encoding)

/// The originals of every file this run wrote — the bytes, so a put-back
/// restores the file exactly, whatever its encoding. First write wins:
/// the original is the original.
let private runOriginals =
    Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)

let private rememberOriginal (path: string) =
    if not (runOriginals.ContainsKey path) then
        runOriginals.[path] <- File.ReadAllBytes path

let private putBack (files: string seq) (why: string) =
    let mutable n = 0

    for f in files do
        match runOriginals.TryGetValue f with
        | true, original ->
            File.WriteAllBytes(f, original)
            n <- n + 1
        | _ -> ()

    if n > 0 then
        Out.skip $"  ({n} file(s) put back: {why})"

// ---- MSBuild ----

let private msbuildRegistered =
    lazy
        (if not MSBuildLocator.IsRegistered then
             // the SDK `dotnet` would pick in this directory (global.json honoured)
             try
                 MSBuildLocator.RegisterDefaults() |> ignore
             with :? InvalidOperationException as ex ->
                 // a global.json asking for an SDK that is not installed: the newest
                 // installed one loads the projects, and the run says so — the final
                 // `dotnet build` still answers to the global.json
                 let newest =
                     // queried from a directory with no global.json of its own
                     MSBuildLocator.QueryVisualStudioInstances(
                         VisualStudioInstanceQueryOptions(
                             DiscoveryTypes = DiscoveryType.DotNetSdk,
                             WorkingDirectory = Path.GetTempPath()
                         )
                     )
                     |> Seq.sortByDescending (fun i -> i.Version)
                     |> Seq.tryHead

                 match newest with
                 | Some instance ->
                     Out.bad
                         $"  (the SDK this directory's global.json asks for is not installed — {ex.Message.Split('\n').[0].Trim()}; loading with SDK {instance.Version} instead. Install the SDK or adjust global.json, or the verification build cannot run)"

                     MSBuildLocator.RegisterInstance instance
                 | None -> reraise ())

let private tfmRank (tfm: string) =
    let t = (tfm.ToLowerInvariant().Split '-').[0]
    let digits = String(t |> Seq.filter Char.IsDigit |> Seq.toArray)

    let version =
        match Int32.TryParse digits with
        | true, v -> v
        | false, _ -> 0

    if t.StartsWith "netstandard" then 0, version
    elif t.StartsWith "netcoreapp" then 2, version
    elif t.StartsWith "net" && t.Contains '.' then 3, version
    else 1, version

/// The framework a workspace project's name carries — MSBuildWorkspace
/// names a multi-targeted project's flavors `Name(net8.0)`.
let private frameworkOf (project: Project) =
    let name =
        if isNull project || isNull project.Name then
            ""
        else
            project.Name

    let i = name.LastIndexOf '('

    if i > 0 && name.EndsWith ")" then
        name.Substring(i + 1, name.Length - i - 2)
    else
        ""

// ---- one compilation ----

let private analyzers: ImmutableArray<DiagnosticAnalyzer> =
    ImmutableArray.Create<DiagnosticAnalyzer>(CSharpRefactorAnalyzer())

let private errorsOf (compilation: Compilation) =
    compilation.GetDiagnostics()
    |> Seq.filter (fun d -> d.Severity = DiagnosticSeverity.Error)
    |> List.ofSeq

/// Parse-phase errors only: what --parse-only can judge without references.
let private parseErrorsOf (compilation: Compilation) =
    compilation.SyntaxTrees
    |> Seq.collect (fun t -> t.GetDiagnostics())
    |> Seq.filter (fun d -> d.Severity = DiagnosticSeverity.Error)
    |> List.ofSeq

let private kindColumn (code: string) =
    let name = RuleCatalog.name (RuleCatalog.categoryOf code)
    $"[{name}]".PadRight 13

/// A finding of one pass: the diagnostic, its file's text, and the
/// suggestion the pure rules produced at the same span (its fixes).
type private Finding =
    {
        Diagnostic: Diagnostic
        Document: Document
        Text: SourceText
        Suggestion: Suggestion option
        Suppressed: bool
    }

let private primaryFix (s: Suggestion) =
    s.Fixes |> List.tryFind (fun f -> not f.EditorOnly)

/// Does a fix's edit span cover a comment its replacement does not carry,
/// or a directive? Then the edit would silently swallow it, and the fix
/// is held. Message-level: a compound fix may MOVE a comment from one
/// edit's span into another edit's text, so every replacement of the fix
/// vouches for every comment.
let private losesTrivia (tree: SyntaxTree) (treeOf: string -> SyntaxTree option) (fix: Fix) =
    let carried = fix.Edits |> List.map (fun e -> e.Replacement) |> String.concat "\n"

    fix.Edits
    |> List.exists (fun e ->
        // an edit in another file is judged against that file's tree; one the
        // solution has no tree for is judged unsafe
        let root =
            match e.File with
            | None -> Some(tree.GetRoot())
            | Some path -> treeOf path |> Option.map (fun t -> t.GetRoot())

        e.Span.Length > 0
        && (match root with
            | None -> true
            | Some root ->
                root.DescendantTrivia(e.Span, descendIntoTrivia = true)
                |> Seq.exists (fun t ->
                    t.IsDirective
                    || ((t.IsKind SyntaxKind.SingleLineCommentTrivia
                         || t.IsKind SyntaxKind.MultiLineCommentTrivia
                         || t.IsKind SyntaxKind.SingleLineDocumentationCommentTrivia
                         || t.IsKind SyntaxKind.MultiLineDocumentationCommentTrivia)
                        // the comment survives if its words do, whatever marks them: a
                        // `// note` carried into a `/// <summary>note</summary>` is kept
                        && not (
                            carried.Contains(
                                t.ToString().Trim().TrimStart([| '/'; '*'; ' ' |]).TrimEnd [| '*'; '/'; ' ' |]
                            )
                        )))))

let private toReported (f: Finding) : ReportedFinding =
    let span = f.Diagnostic.Location.SourceSpan
    let lines = f.Text.Lines
    let startPos = lines.GetLinePosition span.Start
    let endPos = lines.GetLinePosition span.End
    let file = f.Document.FilePath

    let hash, snippet, snippetLines, regionText =
        fingerprintAndSnippet f.Text file f.Diagnostic.Id span

    let fixes =
        match f.Suggestion |> Option.bind primaryFix with
        | Some fix ->
            fix.Edits
            |> List.map (fun e ->
                let s = lines.GetLinePosition e.Span.Start
                let en = lines.GetLinePosition e.Span.End
                s.Line + 1, s.Character, en.Line + 1, en.Character, f.Text.ToString e.Span, e.Replacement)
        | None -> []

    {
        File = file
        Code = f.Diagnostic.Id
        Message = f.Diagnostic.GetMessage()
        Severity = f.Diagnostic.Severity
        StartLine = startPos.Line + 1
        StartColumn = startPos.Character
        EndLine = endPos.Line + 1
        EndColumn = endPos.Character
        Fixable = not fixes.IsEmpty
        Fixes = fixes
        Fingerprint = hash
        Snippet = snippet
        SnippetLines = snippetLines
        RegionText = regionText
    }

/// Run the analyzers over one project's compilation and pair every CR
/// diagnostic with the pure rules' suggestion at its span.
let private analyzeProject (opts: Options) (project: Project) (compilation: Compilation) (ct: CancellationToken) =
    // --codes names an ask: a default-off rule or a config `none` wakes
    // for the codes typed, through the compilation's own diagnostic
    // options, which outrank .editorconfig
    // the re-optioned twin runs the analyzers; the models and the reference
    // oracle stay on the solution's own compilation, which the solution knows
    let withCodes =
        match opts.ExplicitCodes with
        | Some codes when not codes.IsEmpty ->
            let specific =
                codes
                |> Seq.map (fun c -> KeyValuePair(c, ReportDiagnostic.Info))
                |> ImmutableDictionary.CreateRange

            compilation.WithOptions(compilation.Options.WithSpecificDiagnosticOptions specific)
        | _ -> compilation

    let analyzerOptions =
        CompilationWithAnalyzersOptions(
            project.AnalyzerOptions,
            (fun ex analyzer _ ->
                eprintfn $"  (analyzer {analyzer.GetType().Name} failed: {ex.GetType().Name}: {ex.Message})"),
            concurrentAnalysis = true,
            logAnalyzerExecutionTime = false,
            reportSuppressedDiagnostics = true
        )

    let withAnalyzers = CompilationWithAnalyzers(withCodes, analyzers, analyzerOptions)

    let diagnostics = withAnalyzers.GetAnalyzerDiagnosticsAsync(ct).Result

    let wanted (d: Diagnostic) =
        RuleCatalog.known.Contains d.Id
        && (match opts.Codes with
            | Some codes -> codes.Contains d.Id
            | None -> true)
        && d.Location.IsInSource

    let byTree =
        diagnostics
        |> Seq.filter wanted
        |> Seq.groupBy (fun d -> d.Location.SourceTree)
        |> List.ofSeq

    // the api pass may fix what only the reference oracle can see (a caller
    // in another project), which the analyzer never reported: every tree is
    // then run, and such a fix becomes a finding of its own where the rule
    // is on for the tree
    let trees =
        if opts.ApiChanges then
            compilation.SyntaxTrees
            |> Seq.map (fun t ->
                t,
                byTree
                |> List.tryFind (fun (bt, _) -> obj.ReferenceEquals(bt, t))
                |> Option.map snd
                |> Option.defaultValue Seq.empty)
            |> List.ofSeq
        else
            byTree

    let ruleOnFor (tree: SyntaxTree) (code: string) =
        let configured =
            let provider = compilation.Options.SyntaxTreeOptionsProvider

            if isNull provider then
                None
            else
                let mutable report = ReportDiagnostic.Default

                if provider.TryGetDiagnosticValue(tree, code, ct, &report) then
                    Some report
                elif provider.TryGetGlobalDiagnosticValue(code, ct, &report) then
                    Some report
                else
                    None

        let explicitly =
            match opts.ExplicitCodes with
            | Some codes -> codes.Contains code
            | None -> false

        (match opts.Codes with
         | Some codes -> codes.Contains code
         | None -> true)
        && (match configured with
            | Some ReportDiagnostic.Suppress -> explicitly
            | Some _ -> true
            | None -> explicitly || RuleCatalog.isDefaultOn code)

    [
        for tree, ds in trees do
            let document = project.GetDocument tree

            if not (isNull document) then
                let text = tree.GetText ct
                let model = compilation.GetSemanticModel(tree, false)

                let options =
                    Some(project.AnalyzerOptions.AnalyzerConfigOptionsProvider.GetOptions tree)

                // an executable another project of the solution references (its tests)
                // is not a leaf: those callers see its public shape
                let referencedByAnother =
                    project.Solution.Projects
                    |> Seq.exists (fun p ->
                        p.Id <> project.Id
                        && p.ProjectReferences |> Seq.exists (fun r -> r.ProjectId = project.Id))

                // a consumer MSBuildWorkspace cannot load (an F# project) sees the
                // public surface too, and no verification of this run can build it:
                // the surface is held, flag or no flag
                let foreignConsumer =
                    match Workspace.workspaceOf opts.Target project.FilePath with
                    | Some ws ->
                        Workspace.referencersOf ws project.FilePath
                        |> List.exists (Workspace.isCSharpProject >> not)
                    | None -> false

                let ruleContext =
                    let c = Context.forTree options compilation tree opts.ApiChanges

                    { c with
                        IsLeaf = c.IsLeaf && not referencedByAnother && not foreignConsumer
                        ApiChanges = c.ApiChanges && not foreignConsumer
                        // callers in other files and projects, for the cross-file edit sets
                        References = Some(References.oracle project.Solution tree)
                    }

                let suggestions =
                    if opts.ParseOnly then
                        Rules.parseOnly tree ruleContext
                    else
                        let kept, failures = Rules.allWithFailures tree model ruleContext

                        for (name, ex) in failures do
                            let file = Path.GetFileName tree.FilePath
                            let kind = ex.GetType().Name

                            Out.bad
                                $"  (rule {name} threw on {file}: {kind}: {ex.Message} — its suggestions for this file are lost; the log line is the bug report)"

                        kept

                for d in ds do
                    let span = d.Location.SourceSpan

                    let suggestion =
                        suggestions |> List.tryFind (fun s -> s.Code = d.Id && s.Span = span)

                    yield
                        {
                            Diagnostic = d
                            Document = document
                            Text = text
                            Suggestion = suggestion
                            Suppressed = d.IsSuppressed
                        }

                if opts.ApiChanges && not (Configuration.isIgnoredPath options tree.FilePath) then
                    for s in suggestions do
                        let covered =
                            ds |> Seq.exists (fun d -> d.Id = s.Code && d.Location.SourceSpan = s.Span)

                        if
                            not covered
                            && not s.Fixes.IsEmpty
                            && s.Fixes
                               |> List.exists (fun f -> f.Edits |> List.exists (fun e -> e.File.IsSome))
                            && ruleOnFor tree s.Code
                        then
                            match Descriptors.byCode |> Map.tryFind s.Code with
                            | Some descriptor ->
                                yield
                                    {
                                        Diagnostic =
                                            Diagnostic.Create(descriptor, Location.Create(tree, s.Span), s.Message)
                                        Document = document
                                        Text = text
                                        Suggestion = Some s
                                        Suppressed = false
                                    }
                            | None -> ()
    ]

/// Which of a file's fixes to apply this pass: bottom-up, non-overlapping
/// (an overlap waits for the next pass), none that swallows a comment.
let private chooseEdits (tree: SyntaxTree) (treeOf: string -> SyntaxTree option) (fixes: Fix list) =
    let mutable taken: (string option * TextSpan * Fix) list = []

    for fix in
        fixes
        |> List.sortBy (fun f -> f.Edits |> List.map (fun e -> e.Span.Start) |> List.min) do
        let spans = fix.Edits |> List.map (fun e -> e.File, e.Span)

        // a shared character, or two edits at the same position of the same
        // file, is a conflict; edits that merely touch are fine, and so are
        // two pure insertions at one position (several `using`s land in order)
        let conflict (fa: string option, a: TextSpan) (fb: string option, b: TextSpan) =
            fa = fb
            && (a.OverlapsWith b || (a.Start = b.Start && not (a.Length = 0 && b.Length = 0)))

        let overlaps =
            taken |> List.exists (fun (f, s, _) -> spans |> List.exists (conflict (f, s)))

        if not (overlaps || losesTrivia tree treeOf fix) then
            for f, s in spans do
                taken <- (f, s, fix) :: taken

    taken |> List.map (fun (_, _, f) -> f) |> List.distinct

// ---- the passes over one project ----

type private PassOutcome =
    {
        Applied: int
        Solution: Solution
        ChangedFiles: string list
    }

let private printFinding (prefix: string) (f: Finding) =
    let pos = f.Text.Lines.GetLinePosition f.Diagnostic.Location.SourceSpan.Start
    let name = Path.GetFileName f.Document.FilePath
    let message = plainMessage (toReported f)
    $"{prefix}{f.Diagnostic.Id} {kindColumn f.Diagnostic.Id} {name}({pos.Line + 1},{pos.Character}): {message}"

let private firstSentence (text: string) =
    let cutAt =
        [ text.IndexOf ". "; text.IndexOf ".\n"; text.IndexOf '\n' ]
        |> List.filter (fun i -> i >= 0)

    match cutAt with
    | [] -> text
    | cuts -> text.Substring(0, List.min cuts + 1)

/// One pass: analyse, report, apply what can be applied, check in memory.
let private runPass
    (opts: Options)
    (solution: Solution)
    (projectId: ProjectId)
    (onlyFile: string option)
    (baselineErrors: int)
    (pass: int)
    (ct: CancellationToken)
    : PassOutcome =
    let project = solution.GetProject projectId
    let compilation = project.GetCompilationAsync(ct).Result
    let findings = analyzeProject opts project compilation ct

    let suppressionPolicy =
        if honorAllSuppressions then
            "all"
        else
            // the policy is per file in principle; read off the first tree
            match Seq.tryHead compilation.SyntaxTrees with
            | Some t -> Configuration.suppressions (project.AnalyzerOptions.AnalyzerConfigOptionsProvider.GetOptions t)
            | None -> "all"

    // what is reported, and of that what may be fixed
    let visible =
        findings
        |> List.choose (fun f ->
            let code = f.Diagnostic.Id
            let reported = toReported f

            if baselineFingerprints.Contains reported.Fingerprint then
                baselineSuppressed <- baselineSuppressed + 1
                None
            elif f.Suppressed then
                let correctness =
                    RuleCatalog.categoryOf code = RuleCatalog.Category.Correctness
                    || RuleCatalog.isPriority code

                match suppressionPolicy with
                | "none" ->
                    suppressionOverridden <- suppressionOverridden + 1
                    Some(f, reported, false)
                | "no-correctness" when correctness ->
                    suppressionOverridden <- suppressionOverridden + 1
                    Some(f, reported, false)
                | _ ->
                    commentSuppressed <- commentSuppressed + 1
                    None
            else
                let inScope =
                    match onlyFile with
                    | Some only -> Workspace.samePath only f.Document.FilePath
                    | None -> true

                Some(f, reported, inScope))

    for _, reported, _ in visible do
        recordForReport reported

    // notes: printed once per run, or counted per category
    for f, reported, _ in visible do
        if not reported.Fixable then
            let key =
                $"{reported.Code}|{reported.File}|{reported.StartLine}|{reported.StartColumn}|{reported.Message}"

            if printedNotes.Add key then
                if
                    showNotes
                    || RuleCatalog.isPriority reported.Code
                    || f.Diagnostic.Severity = DiagnosticSeverity.Warning
                then
                    Out.note (
                        printFinding "  " f
                        |> fun s -> s.Replace($"): {reported.Message}", $") note: {firstSentence reported.Message}")
                    )
                else
                    let kind = RuleCatalog.name (RuleCatalog.categoryOf reported.Code)

                    heldNoteCounts.[kind] <-
                        (match heldNoteCounts.TryGetValue kind with
                         | true, n -> n
                         | false, _ -> 0)
                        + 1

    // fixes, per file, in file order
    let fixable =
        visible
        |> List.filter (fun (_, reported, mayFix) -> reported.Fixable && mayFix && not notesOnly)
        |> List.sortBy (fun (f, _, _) -> f.Document.FilePath, f.Diagnostic.Location.SourceSpan.Start)

    let byFile = fixable |> List.groupBy (fun (f, _, _) -> f.Document.Id)

    let mutable applied = 0
    let mutable current = solution
    let changed = ResizeArray<string>()
    // the files one document's fixes touched, its own and those reached
    // through cross-file edits: an edit set stands or falls as one
    let units = ResizeArray<string list>()

    for documentId, items in byFile do
        let f0, _, _ = items.Head
        let tree = f0.Document.GetSyntaxTreeAsync(ct).Result

        let fixes =
            items |> List.choose (fun (f, _, _) -> f.Suggestion |> Option.bind primaryFix)

        // a file this pass already rewrote (through another file's cross-file
        // fix) has moved: its own fixes, and any fix reaching into a file
        // touched this pass, wait for the next one, whose spans are fresh
        let touched (path: string) =
            changed |> Seq.exists (fun c -> Workspace.samePath c path)

        // an edit set is applied whole or not at all: a target the solution holds no
        // document for (a `#load`ed script, a file of another workspace) holds the fix
        let fresh (fix: Fix) =
            not (touched f0.Document.FilePath)
            && fix.Edits
               |> List.forall (fun e ->
                   match e.File with
                   | None -> true
                   | Some path ->
                       not (touched path)
                       && not (Workspace.samePath path f0.Document.FilePath)
                       && not (Seq.isEmpty (current.GetDocumentIdsWithFilePath path)))

        let treeOf (path: string) =
            current.GetDocumentIdsWithFilePath path
            |> Seq.tryHead
            |> Option.map (fun id -> current.GetDocument(id).GetSyntaxTreeAsync(ct).Result)

        let chosen = chooseEdits tree treeOf (fixes |> List.filter fresh)

        for f, _, _ in items do
            match f.Suggestion |> Option.bind primaryFix with
            | Some fix when List.contains fix chosen -> Out.good (printFinding "  " f)
            | _ ->
                let line = printFinding "  " f
                printfn $"{line} (held to the next pass)"

        if not (opts.DryRun || chosen.IsEmpty) then
            // this document's edits, and those addressed to other files of the
            // solution (the callers a reshaped method rewrites): each FILE once —
            // a file two projects compile (a signed twin, a link) is one file on
            // disk, so every document of that path takes the same text
            let byTarget =
                chosen
                |> List.collect (fun fix -> fix.Edits)
                |> List.groupBy (fun e ->
                    match e.File with
                    | None -> f0.Document.FilePath
                    | Some path -> path)

            for path, edits in byTarget do
                let ids = current.GetDocumentIdsWithFilePath path |> List.ofSeq

                if not ids.IsEmpty then
                    let text = current.GetDocument(ids.Head).GetTextAsync(ct).Result

                    let changes =
                        edits
                        // two fixes asking for the same insertion (a `partial` on the type) need it once
                        |> List.distinctBy (fun e -> e.Span, e.Replacement)
                        // insertions sharing a position land in text order
                        |> List.sortBy (fun e -> e.Span.Start, e.Replacement)
                        |> List.map (fun e -> TextChange(e.Span, e.Replacement))

                    let patched = text.WithChanges changes

                    for id in ids do
                        current <- current.WithDocumentText(id, patched)

                    if not (changed.Contains path) then
                        changed.Add path

            units.Add(byTarget |> List.map fst)

            applied <- applied + chosen.Length

    if opts.DryRun || applied = 0 then
        {
            Applied = 0
            Solution = solution
            ChangedFiles = []
        }
    else
        // the in-memory arbiter: the error count must not rise. Where it
        // does, the files are tried one at a time and the offenders put back
        let errorsIn (s: Solution) (id: ProjectId) =
            let c = s.GetProject(id).GetCompilationAsync(ct).Result
            (if opts.ParseOnly then parseErrorsOf c else errorsOf c)

        let errorsAfter (s: Solution) = errorsIn s projectId

        // a cross-file edit may land in another project: that project's own
        // count is its baseline
        let otherProjects =
            changed
            |> Seq.collect (fun file -> solution.GetDocumentIdsWithFilePath file)
            |> Seq.map (fun d -> d.ProjectId)
            |> Seq.filter (fun p -> p <> projectId)
            |> Seq.distinct
            |> List.ofSeq

        let othersHold (s: Solution) =
            otherProjects
            |> List.forall (fun p -> (errorsIn s p).Length <= (errorsIn solution p).Length)

        let after = errorsAfter current

        let survivors, finalSolution =
            if after.Length <= baselineErrors && othersHold current then
                List.ofSeq changed, current
            else
                Out.bad $"  the pass introduced {after.Length - baselineErrors} error(s); bisecting per file:"

                for d in after |> List.truncate 5 do
                    Out.dim $"    {d}"

                let mutable kept = solution
                let mutable ok = []

                // per unit: a document's fixes with every file they reached
                for unit in units do
                    let candidate =
                        unit
                        |> List.fold
                            (fun (s: Solution) file ->
                                current.GetDocumentIdsWithFilePath file
                                |> Seq.fold
                                    (fun (s: Solution) id ->
                                        s.WithDocumentText(id, current.GetDocument(id).GetTextAsync(ct).Result))
                                    s)
                            kept

                    if (errorsAfter candidate).Length <= baselineErrors && othersHold candidate then
                        kept <- candidate
                        ok <- List.rev unit @ ok
                    else
                        let names = unit |> List.map Path.GetFileName |> String.concat ", "

                        Out.skip
                            $"  ({names}: its fixes broke the compilation and were not applied — a defect of this tool)"

                        exitReasons.Add $"fixes in {names} put back"

                List.rev ok, kept

        for file in survivors do
            let doc =
                finalSolution.GetDocumentIdsWithFilePath file
                |> Seq.tryHead
                |> Option.map finalSolution.GetDocument

            match doc with
            | Some d ->
                rememberOriginal file
                writeSource file (d.GetTextAsync(ct).Result)
            | None -> ()

        let count =
            if survivors.Length = changed.Count then
                applied
            else
                survivors.Length

        Out.good $"  {count} fix(es) applied in pass {pass}"

        {
            Applied = count
            Solution = finalSolution
            ChangedFiles = survivors
        }

/// Load a project (and what it references) into the workspace, once.
/// A project that was never restored has no `project.assets.json`, and
/// the workspace then loads it with no references at all: every file
/// reports hundreds of errors and the run refuses it. Restore first, once
/// per project, the way a build would. A restore that fails is reported
/// and the load goes on, so the error gate can say what is wrong.
/// The paket roots this run has restored: a repository managed by Paket
/// (`paket.dependencies` above the project) needs `dotnet tool restore`
/// and `dotnet paket restore` before `dotnet restore` can resolve anything.
let private paketRestored = HashSet<string>(StringComparer.OrdinalIgnoreCase)

let private ensurePaketRestored (projectDir: string) =
    let rec root (dir: string) =
        if String.IsNullOrEmpty dir then
            None
        elif File.Exists(Path.Combine(dir, "paket.dependencies")) then
            Some dir
        else
            root (Path.GetDirectoryName dir)

    match root projectDir with
    | Some dir when paketRestored.Add dir ->
        Out.dimPart $"  paket restore in {Path.GetFileName dir}... "

        let run (arguments: string) =
            let code, _, err =
                Processes.runProcessIn (Some dir) Processes.processTimeout "dotnet" arguments

            if code <> 0 then
                eprintfn $"{err.Trim()}"

            code = 0

        let tools =
            not (File.Exists(Path.Combine(dir, ".config", "dotnet-tools.json")))
            || run "tool restore"

        if tools && run "paket restore" then
            Out.dim "done"
        else
            Out.dim "failed"
    | _ -> ()

let private ensureRestored (path: string) =
    let assets = Path.Combine(Path.GetDirectoryName path, "obj", "project.assets.json")

    if not (File.Exists assets) then
        ensurePaketRestored (Path.GetDirectoryName path)
        Out.dimPart $"  restoring {Path.GetFileName path}... "

        let code, _, err =
            Processes.runProcessIn
                (Some(Path.GetDirectoryName path))
                Processes.processTimeout
                "dotnet"
                $"restore \"{path}\" -nologo -v q"

        if code = 0 then
            Out.dim "done"
        else
            Out.dim "failed"
            eprintfn $"{err.Trim()}"

/// The legacy (non-SDK) projects of a run, read by LegacyProjects into their
/// own workspace, and whether each builds before any fix (the arbiter's
/// baseline; None where no MSBuild.exe answers).
let private legacyWorkspace = lazy (new AdhocWorkspace())

let private legacyBaseline =
    Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)

/// Load a project: an SDK-style one through MSBuildWorkspace (every
/// framework flavor), a legacy one through the project-file reader.
let private loadProject (workspace: MSBuildWorkspace) (path: string) : Workspace * Project list =
    if Scripts.isScript path then
        let ws = legacyWorkspace.Value
        let project = Scripts.load ws path
        ws :> Workspace, [ project ]
    elif LegacyProjects.isLegacy path then
        let ws = legacyWorkspace.Value
        let project = LegacyProjects.load ws path
        ws :> Workspace, (if isNull project then [] else [ project ])
    else
        let already =
            workspace.CurrentSolution.Projects
            |> Seq.exists (fun p -> not (isNull p.FilePath) && Workspace.samePath p.FilePath path)

        if not already then
            ensureRestored path
            workspace.OpenProjectAsync(path).Wait()

            // a file that is not UTF-8: Roslyn's loader falls back to the system
            // page on Windows and replaces the bytes with U+FFFD elsewhere; the
            // document takes the text read the one way, so the sweep writes the
            // file back as it came in on every platform
            let mutable solution = workspace.CurrentSolution

            for project in workspace.CurrentSolution.Projects do
                if not (isNull project.FilePath) && Workspace.samePath project.FilePath path then
                    for document in project.Documents do
                        if
                            not (isNull document.FilePath)
                            && File.Exists document.FilePath
                            && Workspace.replacedInvalidBytes document.FilePath (document.GetTextAsync().Result)
                        then
                            solution <- solution.WithDocumentText(document.Id, Workspace.readSource document.FilePath)

            if not (obj.ReferenceEquals(solution, workspace.CurrentSolution)) then
                workspace.TryApplyChanges solution |> ignore

        workspace :> Workspace,
        workspace.CurrentSolution.Projects
        |> Seq.filter (fun p -> not (isNull p.FilePath) && Workspace.samePath p.FilePath path)
        |> Seq.sortBy (frameworkOf >> tfmRank)
        |> List.ofSeq

let private buildVerify (project: string) =
    let code, out, err =
        Processes.runProcessIn
            (Some(Path.GetDirectoryName project))
            Processes.processTimeout
            "dotnet"
            $"build \"{project}\" -nologo -v q -clp:NoSummary"

    if code = 0 then
        Ok()
    else
        let lines =
            ($"{out}\n{err}").Split '\n'
            |> Array.map (fun l -> l.Trim())
            |> Array.filter (fun l -> l.Contains "error" || l.StartsWith Processes.TimeCapMark)
            |> Array.distinct
            |> Array.truncate 12

        Error(String.concat "\n" lines)

/// The whole run for one Options value.
let executeRun (opts: Options) : int =
    // a source file that is not UTF-8 decodes as the system code page (Roslyn's
    // fallback asks the provider for it), not as UTF-8 with every such byte
    // replaced by U+FFFD and written back so
    Text.Encoding.RegisterProvider Text.CodePagesEncodingProvider.Instance
    honorAllSuppressions <- opts.HonorSuppressions
    showNotes <- opts.Notes
    notesOnly <- opts.NotesOnly
    runOriginals.Clear()

    baselineFingerprints <-
        match opts.Baseline with
        | Some path ->
            match loadBaseline path with
            | Ok prints -> prints
            | Error message ->
                eprintfn $"{message}"
                Set.empty
        | None -> Set.empty

    match resolveTargets opts.Target with
    | Error message ->
        eprintfn $"{message}"
        2
    | Ok targets ->
        msbuildRegistered.Force()

        let properties = Dictionary<string, string>()

        if opts.Framework <> "" then
            properties.["TargetFramework"] <- opts.Framework

        use workspace = MSBuildWorkspace.Create properties
        workspace.SkipUnrecognizedProjects <- true

        workspace.WorkspaceFailed.Add(fun e ->
            if e.Diagnostic.Kind = WorkspaceDiagnosticKind.Failure then
                Out.bad $"  (workspace: {e.Diagnostic.Message})"
            else
                Out.dim $"  (workspace: {e.Diagnostic.Message})")

        let ct = CancellationToken.None

        let writeReportNow () =
            match opts.Report with
            | Some path -> writeReport path opts.Target (reportedSoFar ())
            | None -> ()

        let changedProjects = HashSet<string>(StringComparer.OrdinalIgnoreCase)
        let mutable exitCode = 0

        // every target loads first, so the project graph (who references whom)
        // is complete before any project is analysed: a referenced executable
        // is not a leaf, and a dependent is verified after
        for target in targets do
            try
                loadProject workspace (Path.GetFullPath(projectOf target)) |> ignore
            with _ ->
                ()

        for target in targets do
            let projectPath = Path.GetFullPath(projectOf target)

            let onlyFile =
                match target with
                | Target.Project(_, only) -> only

            printfn $"== {Path.GetFileName projectPath} =="

            let projectWorkspace, flavors =
                try
                    loadProject workspace projectPath
                with ex ->
                    Out.bad $"  could not load {Path.GetFileName projectPath}: {ex.GetBaseException().Message}"
                    exitCode <- 1
                    (workspace :> Workspace), []

            let script = Scripts.isScript projectPath
            // a script has no build either: the in-memory check holds its error count
            let legacy = LegacyProjects.isLegacy projectPath || script

            if legacy && not script && not flavors.IsEmpty then
                for r in
                    LegacyProjects.loadReports ()
                    |> List.filter (fun r -> Workspace.samePath r.Project projectPath) do
                    let framework = r.FrameworkDirectory |> Option.defaultValue "?"

                    Out.dim
                        $"  (legacy project: {r.Documents} files, framework {framework}, {r.MissingReferences.Length} reference(s) unresolved)"

                    if not r.MissingReferences.IsEmpty then
                        let shown = String.Join(", ", r.MissingReferences |> List.truncate 8)
                        Out.dim $"   unresolved: {shown}"

                // the arbiter's baseline: does the project build before any fix?
                if not (opts.DryRun || legacyBaseline.ContainsKey projectPath) then
                    match LegacyProjects.build projectPath with
                    | Some(Ok()) -> legacyBaseline.[projectPath] <- true
                    | Some(Error detail) ->
                        Out.dim "  (the project does not build before any fix: this run is verified in memory only)"

                        for line in
                            detail.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
                            |> Array.truncate 3 do
                            Out.dim $"    {line.Trim()}"

                        legacyBaseline.[projectPath] <- false
                    | None ->
                        Out.dim "  (no MSBuild.exe found: this run is verified in memory only)"
                        legacyBaseline.[projectPath] <- false

            for project in flavors do
                let framework = frameworkOf project

                if framework <> "" then
                    Out.dim $"  ({framework})"

                let compilation = project.GetCompilationAsync(ct).Result

                // the #r directives resolve while the compilation is built
                if script then
                    match Scripts.unresolvedOf projectPath with
                    | [] -> Out.dim "  (script: verified in memory — nothing builds a script)"
                    | unresolved ->
                        let shown = String.Join(", ", unresolved |> List.truncate 8)

                        Out.dim
                            $"  (script: verified in memory — nothing builds a script; {unresolved.Length} #r unresolved: {shown})"

                let baselineErrors =
                    if opts.ParseOnly then
                        parseErrorsOf compilation
                    else
                        errorsOf compilation

                // a legacy project read without its build tree carries unresolved-reference
                // errors by construction: the run proceeds on the relative in-memory check,
                // the typed guards standing down where a type is unknown
                if not (baselineErrors.IsEmpty || opts.ParseOnly || legacy) then
                    Out.bad $"The project has {baselineErrors.Length} error(s) before any fix; fix those first:"

                    for d in baselineErrors |> List.truncate 5 do
                        Out.dim $"    {d}"

                    exitCode <- max exitCode 1
                else
                    if not baselineErrors.IsEmpty && script then
                        Out.dim
                            $"  ({baselineErrors.Length} error(s) before any fix — a script host's globals (Args) and unrestored packages are unknown here; the in-memory check holds the count)"
                    elif not baselineErrors.IsEmpty && legacy then
                        Out.dim
                            $"  ({baselineErrors.Length} error(s) before any fix, expected without the build tree; the in-memory check holds the count)"

                    printfn $"{RuleCatalog.rules.Length} rules, {Seq.length project.Documents} files"
                    let mutable pass = 1
                    let mutable go = true
                    let mutable solution = projectWorkspace.CurrentSolution
                    let mutable projectApplied = 0

                    while go do
                        printfn $"pass {pass}:"

                        let outcome =
                            runPass opts solution project.Id onlyFile baselineErrors.Length pass ct

                        projectApplied <- projectApplied + outcome.Applied
                        solution <- outcome.Solution

                        // a cross-file fix may have landed in another project: it is
                        // verified with the rest
                        for file in outcome.ChangedFiles do
                            for id in solution.GetDocumentIdsWithFilePath file do
                                let owner = solution.GetProject id.ProjectId

                                if
                                    not (isNull owner.FilePath)
                                    && not (Workspace.samePath owner.FilePath projectPath)
                                then
                                    changedProjects.Add(Path.GetFullPath owner.FilePath) |> ignore

                        // the workspace must see the new text for the next pass
                        if outcome.Applied > 0 then
                            projectWorkspace.TryApplyChanges solution |> ignore
                            solution <- projectWorkspace.CurrentSolution

                        go <- outcome.Applied > 0 && pass < opts.MaxPasses && not opts.DryRun
                        pass <- pass + 1

                    runTotalApplied <- runTotalApplied + projectApplied

                    if projectApplied > 0 then
                        changedProjects.Add projectPath |> ignore

            writeReportNow ()

        // the final arbiter: a real build of every project the run edited
        if changedProjects.Count > 0 && not opts.DryRun then
            printfn "verifying every changed project builds..."

            // a legacy project builds through MSBuild.exe, where its baseline built
            let verify (project: string) : Result<unit, string> option =
                if Scripts.isScript project then
                    printfn $"  {Path.GetFileName project}: verified in memory (a script has no build)"
                    None
                elif LegacyProjects.isLegacy project then
                    match legacyBaseline.TryGetValue project with
                    | true, true -> LegacyProjects.build project
                    | _ ->
                        printfn $"  {Path.GetFileName project}: verified in memory only (no baseline build)"
                        None
                else
                    Some(buildVerify project)

            for project in changedProjects do
                match verify project with
                | None -> ()
                | Some(Ok()) -> printfn $"  {Path.GetFileName project}: still builds"
                | Some(Error detail) ->
                    let files =
                        runOriginals.Keys
                        |> Seq.filter (fun f ->
                            f.StartsWith(Path.GetDirectoryName project, StringComparison.OrdinalIgnoreCase))
                        |> List.ofSeq

                    // out they come, and the project is built again: one that does not
                    // build without them either (a package never restored, a task host
                    // the machine lacks) is no verdict, and the in-memory check stands
                    let patched = files |> List.map (fun f -> f, File.ReadAllBytes f)
                    putBack files "checking the project builds without them"

                    match verify project with
                    | Some(Error _) ->
                        for f, bytes in patched do
                            File.WriteAllBytes(f, bytes)

                        Out.dim
                            $"  {Path.GetFileName project} does not build with or without the fixes — no verdict on them; they stand, verified in memory only:"

                        eprintfn $"{detail}"
                    | _ ->
                        runBuildFailures <- runBuildFailures + 1
                        Out.bad $"  {Path.GetFileName project} does not build with the fixes; putting them back:"
                        eprintfn $"{detail}"
                        exitReasons.Add $"{Path.GetFileName project}: verification build failed"

            // a project that references a changed one sees its public shape: it
            // must build too, and a failure puts back what it references
            let allProjects () =
                Seq.append
                    workspace.CurrentSolution.Projects
                    (if legacyWorkspace.IsValueCreated then
                         legacyWorkspace.Value.CurrentSolution.Projects
                     else
                         Seq.empty)

            let byPath =
                allProjects ()
                |> Seq.filter (fun p -> not (isNull p.FilePath))
                |> Seq.groupBy (fun p -> Path.GetFullPath p.FilePath)
                |> Seq.map (fun (path, ps) -> path, Seq.head ps)
                |> dict

            let rec upstream (path: string) (seen: HashSet<string>) =
                match byPath.TryGetValue path with
                | true, p ->
                    for r in p.ProjectReferences do
                        let q = p.Solution.GetProject r.ProjectId

                        if not (isNull q || isNull q.FilePath) then
                            let qp = Path.GetFullPath q.FilePath

                            if seen.Add qp then
                                upstream qp seen
                | _ -> ()

            let dependents =
                byPath.Keys
                |> Seq.filter (fun path -> not (changedProjects.Contains path))
                |> Seq.filter (fun path ->
                    let seen = HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    upstream path seen
                    seen |> Seq.exists changedProjects.Contains)
                |> List.ofSeq

            let buildDependent (project: string) =
                if LegacyProjects.isLegacy project then
                    LegacyProjects.build project
                else
                    Some(buildVerify project)

            for project in dependents do
                match buildDependent project with
                | None -> ()
                | Some(Ok()) -> printfn $"  {Path.GetFileName project}: still builds (a dependent)"
                | Some(Error detail) ->
                    let seen = HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    upstream project seen

                    let files =
                        runOriginals.Keys
                        |> Seq.filter (fun f ->
                            seen
                            |> Seq.exists (fun up ->
                                changedProjects.Contains up
                                && f.StartsWith(Path.GetDirectoryName up, StringComparison.OrdinalIgnoreCase)))
                        |> List.ofSeq

                    // the fixes come out and the dependent is built again: a dependent
                    // that does not build without them either is no verdict on them (a
                    // missing package, a task host the machine lacks), and they stand
                    let patched = files |> List.map (fun f -> f, File.ReadAllBytes f)

                    putBack files "checking the dependent builds without them"

                    match buildDependent project with
                    | Some(Error _) ->
                        for f, bytes in patched do
                            File.WriteAllBytes(f, bytes)

                        Out.dim
                            $"  {Path.GetFileName project} (a dependent) does not build with or without the fixes — no verdict on them; they stand, verified in memory only:"

                        eprintfn $"{detail}"
                    | _ ->
                        runBuildFailures <- runBuildFailures + 1

                        Out.bad
                            $"  {Path.GetFileName project} (a dependent) does not build with the fixes; putting back what it references:"

                        eprintfn $"{detail}"
                        exitReasons.Add $"{Path.GetFileName project}: a dependent's verification build failed"

        // the summary
        let heldNotes = heldNoteCounts |> List.ofSeq

        if not heldNotes.IsEmpty then
            let total = heldNotes |> List.sumBy (fun kv -> kv.Value)

            let breakdown =
                heldNotes
                |> List.sortByDescending (fun kv -> kv.Value)
                |> List.map (fun kv -> $"{kv.Value} {kv.Key}")
                |> String.concat ", "

            Out.note $"  {total} advisory note(s) held: {breakdown} — list with --notes, export with --report"

        if baselineSuppressed > 0 then
            printfn $"  ({baselineSuppressed} finding(s) matched the baseline and were suppressed)"

        if commentSuppressed > 0 then
            printfn $"  ({commentSuppressed} finding(s) silenced by pragmas or SuppressMessage)"

        if suppressionOverridden > 0 then
            printfn
                $"  ({suppressionOverridden} suppression(s) not honored by the csharp_refactor.suppressions policy — reported above, never auto-fixed)"

        if opts.DryRun then
            let n = (reportedSoFar ()) |> List.filter (fun f -> f.Fixable) |> List.length
            printfn $"dry run: {n} fix(es) would be applied"
        else
            printfn $"{runTotalApplied} fix(es) applied in total"

        writeReportNow ()

        if runBuildFailures > 0 || exitCode <> 0 || exitReasons.Count > 0 then
            for r in exitReasons do
                eprintfn $"  {r}"

            max exitCode 1
        elif opts.FailOnFindings && not (reportedSoFar ()).IsEmpty then
            3
        else
            0
