/// The reference oracle a host with a `Solution` gives the rules: every
/// reference to a symbol outside the file being analysed, found by
/// `SymbolFinder` across every project of the solution, C# and VB alike.
/// A site in a C# document the host may write is `Editable`; a VB site is
/// read only, so a rule that would have to rewrite it stands down. The
/// compiler has no solution and never builds one of these.
namespace CSharp.Refactor.Roslyn

open System
open System.Collections.Generic
open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.FindSymbols
open CSharp.Refactor

module References =
    /// The oracle for one tree of one solution; answers are cached per
    /// symbol for the life of the pass.
    let oracle (solution: Solution) (tree: SyntaxTree) : ISymbol -> ReferenceSite list =
        let cache = Dictionary<ISymbol, ReferenceSite list>(SymbolEqualityComparer.Default)
        let models = Dictionary<DocumentId, SemanticModel>()

        let modelOf (document: Document) =
            match models.TryGetValue document.Id with
            | true, m -> m
            | _ ->
                let m = document.GetSemanticModelAsync().Result
                models.[document.Id] <- m
                m

        fun symbol ->
            match cache.TryGetValue symbol with
            | true, sites -> sites
            | _ ->
                let sites =
                    try
                        SymbolFinder.FindReferencesAsync(symbol, solution).Result
                        |> Seq.collect (fun r -> r.Locations)
                        |> Seq.choose (fun location ->
                            let document = location.Document

                            if
                                location.IsImplicit
                                || isNull document
                                || isNull document.FilePath
                                || String.Equals(document.FilePath, tree.FilePath, StringComparison.OrdinalIgnoreCase)
                            then
                                None
                            else
                                let root = document.GetSyntaxRootAsync().Result

                                let node =
                                    root.FindNode(location.Location.SourceSpan, getInnermostNodeForTie = true)

                                Some
                                    {
                                        Tree = root.SyntaxTree
                                        Model = modelOf document
                                        Node = node
                                        Editable = document.Project.Language = LanguageNames.CSharp
                                    })
                        |> List.ofSeq
                    with _ ->
                        // an oracle that cannot answer holds the fix: one unreadable site
                        [
                            {
                                Tree = tree
                                Model = null
                                Node = null
                                Editable = false
                            }
                        ]

                cache.[symbol] <- sites
                sites
