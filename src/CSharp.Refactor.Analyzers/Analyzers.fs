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
            "NestedIfMerge", NestedIfMerge.analyze
            "SwitchShapes", SwitchShapes.analyze
            "NullableMatch", NullableMatch.analyze
            "TypeTestChain", TypeTestChain.analyze
            "IfChainSwitch", IfChainSwitch.analyze
            "Loops", Loops.analyze
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
            typedNamed
            |> List.collect (fun (name, rule) ->
                try
                    rule tree model ctx
                with ex ->
                    failures.Add(name, ex)
                    [])
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
