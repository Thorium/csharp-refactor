/// The Roslyn CodeFixProvider: for a diagnostic at a span, re-run the pure
/// rules over the document and offer every fix the matching suggestion
/// carries as a CodeAction whose only operation is a text change. No
/// SyntaxGenerator, no formatter: the edited spans are what changes.
namespace CSharp.Refactor.Roslyn

open System.Collections.Immutable
open System.Composition
open System.Threading
open System.Threading.Tasks
open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CodeActions
open Microsoft.CodeAnalysis.CodeFixes
open Microsoft.CodeAnalysis.Text
open CSharp.Refactor

[<ExportCodeFixProvider(LanguageNames.CSharp, Name = "CSharpRefactorCodeFixProvider"); Shared>]
type CSharpRefactorCodeFixProvider() =
    inherit CodeFixProvider()

    static let fixable: ImmutableArray<string> =
        RuleCatalog.rules |> List.map (fun r -> r.Code) |> ImmutableArray.CreateRange

    /// Apply a fix: this document's edits, and any edit addressed to
    /// another file of the solution (a caller of a reshaped method).
    let apply (document: Document) (fix: Fix) (ct: CancellationToken) : Task<Solution> =
        task {
            let mutable solution = document.Project.Solution

            let byDocument =
                fix.Edits
                |> List.groupBy (fun e ->
                    match e.File with
                    | None -> Some document.Id
                    | Some path -> solution.GetDocumentIdsWithFilePath path |> Seq.tryHead)

            // whole or not at all: a target the solution holds no document for
            // leaves the set unapplied rather than half applied
            if byDocument |> List.exists (fun (id, _) -> id.IsNone) then
                return solution
            else
                for documentId, edits in byDocument do
                    match documentId with
                    | Some id ->
                        let doc = solution.GetDocument id
                        let! text = doc.GetTextAsync ct
                        let changes = edits |> List.map (fun e -> TextChange(e.Span, e.Replacement))
                        solution <- solution.WithDocumentText(id, text.WithChanges changes)
                    | None -> ()

                return solution
        }

    override _.FixableDiagnosticIds = fixable

    /// Fix-all is Roslyn's batch fixer over the equivalence key: every
    /// suggestion of one rule shares its key, so "fix all in document"
    /// applies them together.
    override _.GetFixAllProvider() = WellKnownFixAllProviders.BatchFixer

    override this.RegisterCodeFixesAsync(context: CodeFixContext) : Task =
        task {
            let document = context.Document
            let! tree = document.GetSyntaxTreeAsync context.CancellationToken
            let! model = document.GetSemanticModelAsync context.CancellationToken

            if not (isNull tree) then
                let options =
                    try
                        Some(document.Project.AnalyzerOptions.AnalyzerConfigOptionsProvider.GetOptions tree)
                    with _ ->
                        None

                let suggestions =
                    if isNull model then
                        Rules.parseOnly
                            tree
                            { RuleContext.editor with
                                Options = options
                            }
                    else
                        let ctx =
                            { Context.forTree options model.Compilation tree false with
                                References = Some(References.oracle document.Project.Solution tree)
                            }

                        Rules.all tree model ctx

                for diagnostic in context.Diagnostics do
                    let matching =
                        suggestions
                        |> List.filter (fun s -> s.Code = diagnostic.Id && s.Span = diagnostic.Location.SourceSpan)

                    for s in matching do
                        for fix in s.Fixes do
                            let action =
                                CodeAction.Create(fix.Title, (fun ct -> apply document fix ct), fix.Key)

                            context.RegisterCodeFix(action, diagnostic)
        }
        :> Task
