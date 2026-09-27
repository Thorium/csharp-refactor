/// The Roslyn DiagnosticAnalyzer entry point. The logic lives in the
/// per-rule modules; this file builds the descriptors, the per-file rule
/// context and the diagnostics, and runs one pass per file version through
/// a single semantic-model action.
namespace CSharp.Refactor.Roslyn

open System.Collections.Immutable
open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.Diagnostics
open CSharp.Refactor

module Descriptors =
    /// Every rule's message ends with its kind, so wherever a reader meets
    /// a suggestion it says whether it is a defect or punctuation without
    /// looking the code up; a suffix because editors truncate from the
    /// right.
    let describe (rule: RuleCatalog.Rule) : DiagnosticDescriptor =
        let severity =
            if rule.Priority then
                DiagnosticSeverity.Warning
            else
                DiagnosticSeverity.Info

        let tags =
            if rule.Category = RuleCatalog.Category.Cosmetic then
                [| WellKnownDiagnosticTags.Unnecessary |]
            else
                [||]

        DiagnosticDescriptor(
            rule.Code,
            rule.Title,
            "{0} [" + RuleCatalog.name rule.Category + "]",
            "CSharp.Refactor",
            severity,
            rule.Default,
            rule.Title,
            RuleCatalog.helpUri rule.Code,
            tags
        )

    let all: ImmutableArray<DiagnosticDescriptor> =
        RuleCatalog.rules |> List.map describe |> ImmutableArray.CreateRange

    let byCode = all |> Seq.map (fun d -> d.Id, d) |> Map.ofSeq

/// The rule context a host builds per file.
module Context =
    let private internalsVisibleTo (compilation: Compilation) =
        compilation.Assembly.GetAttributes()
        |> Seq.exists (fun a ->
            not (isNull a.AttributeClass)
            && a.AttributeClass.Name = "InternalsVisibleToAttribute")

    let private isLeaf (compilation: Compilation) =
        match compilation.Options.OutputKind with
        | OutputKind.ConsoleApplication
        | OutputKind.WindowsApplication
        | OutputKind.WindowsRuntimeApplication -> true
        | _ -> false

    /// The context for one tree of a compilation: its effective config
    /// (when the host has analyzer options), the compilation's shape, and
    /// the run's `--api-changes` decision.
    let forTree
        (options: AnalyzerConfigOptions option)
        (compilation: Compilation)
        (tree: SyntaxTree)
        (apiChanges: bool)
        =
        let apiChanges =
            apiChanges
            || (match options with
                | Some o -> Configuration.apiChanges o
                | None -> false)

        {
            RuleContext.Options = options
            ApiChanges = apiChanges
            IsLeaf = isLeaf compilation
            HasFriends = internalsVisibleTo compilation
            LanguageVersion =
                match tree.Options with
                | :? CSharpParseOptions as o -> LanguageVersionFacts.MapSpecifiedToEffectiveVersion o.LanguageVersion
                | _ -> LanguageVersionFacts.MapSpecifiedToEffectiveVersion LanguageVersion.Latest
            // the compiler sees one compilation; a host with a solution fills this in
            References = None
        }

/// The pure rules, run by both the analyzer and the apply tool. Every rule
/// runs once: `all` for a host with a semantic model (the compiler, an
/// editor, the tool), `parseOnly` for one without (`--parse-only`, a fix
/// provider on an unloaded document) — the rules that can read syntax
/// alone run there with their conservative syntactic gates.
module Rules =
    /// For a host WITHOUT a semantic model.
    let parseOnly (tree: SyntaxTree) (ctx: RuleContext) : Suggestion list =
        InterpolationHoles.analyze tree ctx
        @ RedundantSyntax.analyze tree ctx
        @ AttributeMerge.analyze tree ctx
        @ CommentDoc.analyze tree ctx

    /// Everything a file yields, for a host that has a semantic model.
    /// Every rule runs exactly once here; a rule with a parse-only form
    /// runs its typed form instead of both.
    /// The typed rules by module name, so a host can say which one threw.
    let typedNamed: (string * (SyntaxTree -> SemanticModel -> RuleContext -> Suggestion list)) list =
        [
            "InterpolationHoles", InterpolationHoles.analyzeTyped
            "EmptyGuid", EmptyGuid.analyze
            "RedundantSyntax", RedundantSyntax.analyzeTyped
            "BooleanSimplify", BooleanSimplify.analyze
            "BoolReturn", BoolReturn.analyze
            "ConstLocal", ConstLocal.analyze
            "ReturnHoist", ReturnHoist.analyze
            "SpanShapes", SpanShapes.analyze
            "NestedIfMerge", NestedIfMerge.analyze
            "SwitchShapes", SwitchShapes.analyze
            "NullableMatch", NullableMatch.analyze
            "TypeTestChain", TypeTestChain.analyze
            "IfChainSwitch", IfChainSwitch.analyze
            "Loops", Loops.analyze
            "LoopInvariant", LoopInvariant.analyze
            "EnumCoverage", EnumCoverage.analyze
            "PyramidFlip", PyramidFlip.analyze
            "FlagLoop", FlagLoop.analyze
            "HintEngine", HintEngine.analyze
            "AttributeMerge", (fun tree _ ctx -> AttributeMerge.analyze tree ctx)
            "CommentDoc", (fun tree _ ctx -> CommentDoc.analyze tree ctx)
            "QualifiedNames", QualifiedNames.analyze
            "LinqNotes", LinqNotes.analyze
            "CollectionFixes", CollectionFixes.analyze
            "ConversionMove", ConversionMove.analyze
            "QueryCopy", QueryCopy.analyze
            "SelectFusion", SelectFusion.analyze
            "Accumulation", Accumulation.analyze
            "FillLoop", FillLoop.analyze
            "ContainsSet", ContainsSet.analyze
            "AsyncShapes", AsyncShapes.analyze
            "Locks", Locks.analyze
            "ConcurrentCache", ConcurrentCache.analyze
            "Lifetimes", Lifetimes.analyze
            "AsyncSignatures", AsyncSignatures.analyze
            "AwaitableTwin", AwaitableTwin.analyze
            "ExceptionRules", ExceptionRules.analyze
            "DisposableDesign", DisposableDesign.analyze
            "UseBinding", UseBinding.analyze
            "StringShapes", StringShapes.analyze
            "CultureTime", CultureTime.analyze
            "RegexRules", RegexRules.analyze
            "PathSeparator", PathSeparator.analyze
            "SourceHygiene", SourceHygiene.analyze
            "LoggingRules", LoggingRules.analyze
            "TypeNotes", TypeNotes.analyze
            "Immutability", Immutability.analyze
            "StructShapes", StructShapes.analyze
            "ClockMigration", ClockMigration.analyze
            "SecurityRules", SecurityRules.analyze
            "Ladder", Ladder.analyze
            "LadderShapes", LadderShapes.analyze
            "ClosureCaptures", ClosureCaptures.analyze
            "GuardedRegions", GuardedRegions.analyze
            "ParseAndNumbers", ParseAndNumbers.analyze
            "EnumerationMutation", EnumerationMutation.analyze
        ]

    let private typed = typedNamed |> List.map snd

    /// The codes each typed rule module declares - its `Code` literals
    /// (`let Code = "CR0023"`, `let FlagCode = ...`) - read off the module's
    /// compiled class. Every rule module spells the codes it reports that
    /// way and borrows no other module's (a test holds the source to it), so
    /// a module whose codes are all outside a run's `--codes` has nothing to
    /// say in it. A module whose class is not found reads as empty, and an
    /// empty set always runs.
    let moduleCodes: Lazy<Map<string, Set<string>>> =
        lazy
            (let assembly = typeof<RuleContext>.Assembly

             let flags =
                 System.Reflection.BindingFlags.Static
                 ||| System.Reflection.BindingFlags.Public
                 ||| System.Reflection.BindingFlags.NonPublic

             let isCode (value: obj) =
                 match value with
                 | :? string as s ->
                     s.Length = 6
                     && s.StartsWith "CR"
                     && s.Substring 2 |> Seq.forall System.Char.IsDigit
                 | _ -> false

             typedNamed
             |> List.map (fun (name, _) ->
                 let codes =
                     match assembly.GetType("CSharp.Refactor." + name) with
                     | null -> Set.empty
                     | t ->
                         let fromFields =
                             t.GetFields flags
                             |> Seq.filter (fun f -> f.FieldType = typeof<string>)
                             |> Seq.choose (fun f ->
                                 try
                                     Some(
                                         if f.IsLiteral then
                                             f.GetRawConstantValue()
                                         else
                                             f.GetValue null
                                     )
                                 with
                                 | :? System.InvalidOperationException
                                 | :? System.Reflection.TargetInvocationException -> None)

                         let fromProperties =
                             t.GetProperties flags
                             |> Seq.filter (fun p ->
                                 p.PropertyType = typeof<string> && p.GetIndexParameters().Length = 0)
                             |> Seq.choose (fun p ->
                                 try
                                     Some(p.GetValue null)
                                 with :? System.Reflection.TargetInvocationException ->
                                     None)

                         Seq.append fromFields fromProperties
                         |> Seq.filter isCode
                         |> Seq.map (fun v -> v :?> string)
                         |> Set.ofSeq

                 name, codes)
             |> Map.ofList)

    /// The codes the running tool is restricted to (`--codes`, narrowed by
    /// `--categories`), or None. Set by the tool for the length of a run and
    /// cleared after it; an editor never sets it. A module none of whose
    /// codes is wanted is skipped outright: filtering only its diagnostics
    /// afterwards ran every rule, twice per file, for a run of one code.
    /// An AsyncLocal: it switches rules OFF, and a process-wide switch would
    /// take them from whatever runs beside the run - a test class in parallel
    /// with one driving the tool. It flows into the tasks the run starts
    /// (Roslyn's analyzer driver among them) and nowhere else.
    let private restriction = System.Threading.AsyncLocal<Set<string> option>()

    let restrictTo (codes: Set<string> option) = restriction.Value <- codes

    let private runs (name: string) =
        match restriction.Value with
        | None -> true
        | Some wanted ->
            match moduleCodes.Value.TryFind name with
            | Some codes when not codes.IsEmpty -> codes |> Set.exists wanted.Contains
            | _ -> true

    /// Time spent in each rule module, summed over every file and thread
    /// since the last reset, in Stopwatch ticks: a module takes microseconds
    /// on a small file, and milliseconds would round most of them to zero.
    /// The tool prints the slowest after a run; an editor never reads it,
    /// and pays one Stopwatch per module per file for the record.
    let timings = System.Collections.Concurrent.ConcurrentDictionary<string, int64>()

    let resetTimings () =
        timings.Clear()
        Index.resetBuildTicks ()

    /// The record in milliseconds, slowest first; the cross-file index's
    /// build under its own name, kept out of the rules' figures.
    let timingsMs () : (string * int64) list =
        let toMs (ticks: int64) =
            ticks * 1000L / System.Diagnostics.Stopwatch.Frequency

        ("Index", toMs (Index.buildTicksSoFar ()))
        :: (timings |> Seq.map (fun kv -> kv.Key, toMs kv.Value) |> List.ofSeq)
        |> List.filter (fun (_, ms) -> ms > 0L)
        |> List.sortByDescending snd

    /// The yields-to gate, applied centrally: a suggestion of a rule whose
    /// Microsoft twin is enabled in the file's config is dropped here, so
    /// no rule needs to ask.
    let private notShadowed (ctx: RuleContext) (s: Suggestion) =
        match RuleCatalog.tryFind s.Code with
        | Some rule when not rule.YieldsTo.IsEmpty -> not (RuleContext.shadowedRuleOn ctx rule.YieldsTo)
        | _ -> true

    /// Where two rules read the same shape, the better spelling wins: a
    /// suggestion of the loser overlapping one of the winner is dropped.
    let private overlapWinners = [ "CR0147", "CR0002"; "CR0160", "CR0017" ]

    /// Every rule's suggestions, and the rules that threw on this file (a
    /// rule that throws loses its own suggestions, never the file's): a
    /// compilation full of unresolved references is a tree of error types
    /// no rule was written against.
    let allWithFailures
        (tree: SyntaxTree)
        (model: SemanticModel)
        (ctx: RuleContext)
        : Suggestion list * (string * exn) list =
        let failures = ResizeArray<string * exn>()

        let suggestions =
            // a generated file is the generator's to write, not ours to tidy
            if Text.isGeneratedFile tree then
                []
            else
                typedNamed
                |> List.filter (fun (name, _) -> runs name)
                |> List.collect (fun (name, rule) ->
                    let sw = System.Diagnostics.Stopwatch.StartNew()
                    let insideIndexBefore = Index.threadInsideTicks ()

                    try
                        try
                            rule tree model ctx
                        with ex ->
                            failures.Add(name, ex)
                            []
                    finally
                        // the index's build, or the wait for it, is not this rule's time
                        let own = sw.ElapsedTicks - (Index.threadInsideTicks () - insideIndexBefore)
                        timings.AddOrUpdate(name, own, (fun _ total -> total + own)) |> ignore)
                |> List.filter (notShadowed ctx)

        let kept =
            suggestions
            |> List.filter (fun s ->
                overlapWinners
                |> List.forall (fun (winner, loser) ->
                    s.Code <> loser
                    || not (
                        suggestions
                        |> List.exists (fun w -> w.Code = winner && w.Span.IntersectsWith s.Span)
                    )))

        kept, List.ofSeq failures

    let all (tree: SyntaxTree) (model: SemanticModel) (ctx: RuleContext) : Suggestion list =
        fst (allWithFailures tree model ctx)

[<DiagnosticAnalyzer(LanguageNames.CSharp)>]
type CSharpRefactorAnalyzer() =
    inherit DiagnosticAnalyzer()

    let report (reportDiagnostic: Diagnostic -> unit) (tree: SyntaxTree) (suggestions: Suggestion list) =
        for s in suggestions do
            match Descriptors.byCode |> Map.tryFind s.Code with
            | Some descriptor ->
                let location = Location.Create(tree, s.Span)
                reportDiagnostic (Diagnostic.Create(descriptor, location, s.Message))
            | None -> ()

    override _.SupportedDiagnostics = Descriptors.all

    override _.Initialize(context: AnalysisContext) =
        context.ConfigureGeneratedCodeAnalysis GeneratedCodeAnalysisFlags.None
        context.EnableConcurrentExecution()

        // one action, one walk: the compiler always has a model, so every
        // rule runs its typed form here and nothing runs twice
        context.RegisterSemanticModelAction(fun ctx ->
            let tree = ctx.SemanticModel.SyntaxTree
            let options = ctx.Options.AnalyzerConfigOptionsProvider.GetOptions tree

            // a path the repository told us to ignore (csharp_refactor.ignore_paths,
            // or the built-in generated-file globs) is neither analysed nor fixed
            if not (Configuration.isIgnoredPath (Some options) tree.FilePath) then
                let ruleContext =
                    Context.forTree (Some options) ctx.SemanticModel.Compilation tree false

                report ctx.ReportDiagnostic tree (Rules.all tree ctx.SemanticModel ruleContext))
