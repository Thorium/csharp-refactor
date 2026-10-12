/// The reference oracle a host with a `Solution` gives the rules: every
/// reference to a symbol outside the file being analysed, found by
/// `SymbolFinder` across every project of the solution, C# and VB alike.
/// A site in a C# document the host may write is `Editable`; a VB site is
/// read only, so a rule that would have to rewrite it stands down. The
/// compiler has no solution and never builds one of these.
namespace CSharp.Refactor.Roslyn

open System
open System.Collections.Generic
open System.Collections.Concurrent
open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.FindSymbols
open CSharp.Refactor

module References =
    /// The oracle for every tree of one solution: a symbol's references are
    /// searched once, whichever file asks - the analyzer run and the tool's
    /// own pass alike, on as many threads as `--jobs` - and each tree is
    /// answered the sites outside its own file.
    let solutionOracle (solution: Solution) : SyntaxTree -> ISymbol -> ReferenceSite list =
        // one computation per key however many threads ask at once; a
        // computation that faults is forgotten, so the next asker tries
        // again instead of being handed the same failure for good
        let once (cache: ConcurrentDictionary<'k, Lazy<'v>>) (key: 'k) (make: 'k -> 'v) : 'v =
            let entry = cache.GetOrAdd(key, (fun k -> lazy (make k)))

            try
                entry.Value
            with _ ->
                // this entry only: another thread may have put a fresh one in already
                (cache :> ICollection<KeyValuePair<'k, Lazy<'v>>>).Remove(KeyValuePair(key, entry))
                |> ignore

                reraise ()

        let models = ConcurrentDictionary<DocumentId, Lazy<SemanticModel>>()

        // the rules are synchronous by contract (Roslyn calls an analyzer's
        // actions synchronously) and the workspace answers in tasks: the
        // oracle is where the two meet, and waits
        let modelOf (document: Document) =
            once models document.Id (fun _ -> document.GetSemanticModelAsync().Result)

        let sites =
            ConcurrentDictionary<ISymbol, Lazy<(string * ReferenceSite) list>>(SymbolEqualityComparer.Default)

        // every site of the symbol, the asking file's included
        let search (symbol: ISymbol) : (string * ReferenceSite) list =
            SymbolFinder.FindReferencesAsync(symbol, solution).Result
            |> Seq.collect (fun r -> r.Locations)
            |> Seq.choose (fun location ->
                let document = location.Document

                if location.IsImplicit || isNull document || isNull document.FilePath then
                    None
                else
                    let root = document.GetSyntaxRootAsync().Result

                    let node =
                        root.FindNode(location.Location.SourceSpan, getInnermostNodeForTie = true)

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

        // None when the search failed: a task that faulted or was cancelled
        // (the wait wraps either in an AggregateException), a location the
        // tree no longer holds, a document without a model
        let sitesOf (symbol: ISymbol) : (string * ReferenceSite) list option =
            try
                Some(once sites symbol search)
            with
            | :? AggregateException
            | :? OperationCanceledException
            | :? ArgumentException
            | :? InvalidOperationException
            | :? NotSupportedException -> None

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

    /// One file's string literals: their spellings, and for a test file the
    /// text each string holds (an interpolation's pieces each).
    type private FileLiterals =
        {
            Spellings: HashSet<string>
            TestTexts: string[]
        }

    // per tree: a file no fix touched is the same tree in the next snapshot
    // of the solution, and is not read again
    let private fileLiterals =
        System.Runtime.CompilerServices.ConditionalWeakTable<SyntaxTree, FileLiterals>()

    let private literalsOf (tree: SyntaxTree) =
        fileLiterals.GetValue(
            tree,
            fun tree ->
                let strings =
                    tree.GetRoot().DescendantTokens()
                    |> Seq.filter (fun t ->
                        t.IsKind SyntaxKind.StringLiteralToken
                        || t.IsKind SyntaxKind.SingleLineRawStringLiteralToken
                        || t.IsKind SyntaxKind.MultiLineRawStringLiteralToken
                        || t.IsKind SyntaxKind.InterpolatedStringTextToken)
                    |> Array.ofSeq

                {
                    Spellings =
                        HashSet<string>(
                            strings
                            |> Seq.filter (fun t -> t.IsKind SyntaxKind.StringLiteralToken)
                            |> Seq.map (fun t -> t.Text),
                            StringComparer.Ordinal
                        )
                    TestTexts =
                        if Text.isTestFile tree then
                            strings |> Array.map (fun t -> t.ValueText)
                        else
                            [||]
                }
        )

    /// The solution's literals beyond `project`'s own files: whether another
    /// project, a test above all, reads a text back. Read on the first
    /// question only; a solution that cannot be read answers yes to every
    /// question, which makes the asking rule stand down.
    let mentions (solution: Solution) (project: Project) : TextMentions =
        let own =
            HashSet<string>(
                project.Documents |> Seq.choose (fun d -> Option.ofObj d.FilePath),
                StringComparer.OrdinalIgnoreCase
            )

        let files =
            lazy
                (try
                    solution.Projects
                    |> Seq.filter (fun p -> p.Language = LanguageNames.CSharp)
                    |> Seq.collect (fun p -> p.Documents)
                    |> Seq.filter (fun d -> not (isNull d.FilePath))
                    // a multi-targeted project's flavors hold the same files
                    |> Seq.distinctBy (fun d -> d.FilePath.ToLowerInvariant())
                    |> Seq.choose (fun d ->
                        match d.GetSyntaxTreeAsync().Result with
                        | null -> None
                        | tree -> Some(own.Contains d.FilePath, literalsOf tree))
                    |> Array.ofSeq
                    |> Some
                 with
                 | :? AggregateException
                 | :? OperationCanceledException
                 | :? InvalidOperationException -> None)

        {
            Spelled =
                fun spelling ->
                    match files.Value with
                    | Some files ->
                        files
                        |> Array.exists (fun (isOwn, f) -> not isOwn && f.Spellings.Contains spelling)
                    | None -> true
            InTests =
                fun text ->
                    match files.Value with
                    | Some files ->
                        files
                        |> Array.exists (fun (_, f) ->
                            f.TestTexts
                            |> Array.exists (fun t -> t.IndexOf(text, StringComparison.Ordinal) >= 0))
                    | None -> true
        }
