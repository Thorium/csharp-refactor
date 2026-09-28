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
    /// The oracle for every tree of one solution: a symbol's references are
    /// searched once, whichever file asks - the analyzer run and the tool's
    /// own pass alike, on as many threads as `--jobs` - and each tree is
    /// answered the sites outside its own file.
    let solutionOracle (solution: Solution) : SyntaxTree -> ISymbol -> ReferenceSite list =
        let models =
            System.Collections.Concurrent.ConcurrentDictionary<DocumentId, Lazy<SemanticModel>>()

        let modelOf (document: Document) =
            models.GetOrAdd(document.Id, (fun _ -> lazy (document.GetSemanticModelAsync().Result))).Value

        // every site of the symbol, the asking file's included; None when the
        // search failed
        let sitesOf =
            let cache =
                System.Collections.Concurrent.ConcurrentDictionary<ISymbol, Lazy<(string * ReferenceSite) list option>>(
                    SymbolEqualityComparer.Default
                )

            fun (symbol: ISymbol) ->
                cache
                    .GetOrAdd(
                        symbol,
                        fun symbol ->
                            lazy
                                (try
                                    SymbolFinder.FindReferencesAsync(symbol, solution).Result
                                    |> Seq.collect (fun r -> r.Locations)
                                    |> Seq.choose (fun location ->
                                        let document = location.Document

                                        if location.IsImplicit || isNull document || isNull document.FilePath then
                                            None
                                        else
                                            let root = document.GetSyntaxRootAsync().Result

                                            let node =
                                                root.FindNode(
                                                    location.Location.SourceSpan,
                                                    getInnermostNodeForTie = true
                                                )

                                            Some(
                                                document.FilePath,
                                                {
                                                    Tree = root.SyntaxTree
                                                    Model = modelOf document
                                                    Node = node
                                                    Editable = document.Project.Language = LanguageNames.CSharp
                                                }
                                            ))
                                    |> List.ofSeq
                                    |> Some
                                 with _ ->
                                     None)
                    )
                    .Value

        fun (tree: SyntaxTree) (symbol: ISymbol) ->
            match sitesOf symbol with
            | Some sites ->
                sites
                |> List.choose (fun (path, site) ->
                    if String.Equals(path, tree.FilePath, StringComparison.OrdinalIgnoreCase) then
                        None
                    else
                        Some site)
            | None ->
                // an oracle that cannot answer holds the fix: one unreadable site
                [
                    {
                        Tree = tree
                        Model = null
                        Node = null
                        Editable = false
                    }
                ]

    /// The oracle for one tree of one solution.
    let oracle (solution: Solution) (tree: SyntaxTree) : ISymbol -> ReferenceSite list = solutionOracle solution tree
