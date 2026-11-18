/// The reference oracle a host with a `Solution` gives the rules: every
/// reference to a symbol outside the file being analysed, found by
/// `SymbolFinder` across every project of the solution, C# and VB alike.
/// A site in a C# document the host may write is `Editable`; a VB site is
/// read only, so a rule that would have to rewrite it stands down. A file
/// other projects compile too (a linked `<Compile Include>`) declares its
/// symbols once per compilation: the references of each are the symbol's,
/// and the tree's other compilations are lent to the rules
/// (`RuleContext.linkedCompilations`). The compiler has no solution and never
/// builds one of these.
namespace CSharp.Refactor.Roslyn

open System
open System.Collections.Generic
open System.Collections.Concurrent
open System.Text.RegularExpressions
open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.FindSymbols
open Microsoft.CodeAnalysis.Text
open CSharp.Refactor

module References =
    /// CR0081's question about the projects referencing `project`: does one use the type in a way a struct can't take? Their
    /// indexes are built when a candidate first asks, and once, so a project with no candidate pays nothing.
    let consumerStructHostile (solution: Solution) (project: Project) : ITypeSymbol -> bool =
        let names =
            lazy
                (solution.GetProjectDependencyGraph().GetProjectsThatTransitivelyDependOnThisProject project.Id
                 |> Seq.map solution.GetProject
                 |> Seq.filter (fun p -> p.Language = LanguageNames.CSharp)
                 |> Seq.map (fun p -> p.GetCompilationAsync().GetAwaiter().GetResult())
                 |> Seq.filter (isNull >> not)
                 |> Seq.map (Index.ofCompilation >> Index.structHostileNames)
                 |> Set.unionMany)

        fun t -> names.Value.Contains(t.WithNullableAnnotation(NullableAnnotation.NotAnnotated).ToDisplayString())

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

        let sitesFound (symbol: ISymbol) : (string * TextSpan * ReferenceSite) list =
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
                        location.Location.SourceSpan,
                        {
                            Tree = root.SyntaxTree
                            Model = modelOf document
                            Node = node
                            Editable = document.Project.Language = LanguageNames.CSharp
                        }
                    ))
            |> List.ofSeq

        // the other C# projects compiling one of these files, by path: a linked
        // `<Compile Include>`. A multi-targeted project's flavors share its
        // assembly name, and their sites are its own
        let linkedProjects (paths: string seq) (ownAssembly: string) : Project list =
            paths
            |> Seq.filter (String.IsNullOrEmpty >> not)
            |> Seq.collect solution.GetDocumentIdsWithFilePath
            |> Seq.map (fun d -> d.ProjectId)
            |> Seq.distinct
            |> Seq.map solution.GetProject
            |> Seq.filter (fun p -> p.Language = LanguageNames.CSharp && p.AssemblyName <> ownAssembly)
            |> List.ofSeq

        // the same declaration in each other compilation of its file: its own
        // symbol there, which a search for this one does not reach
        let linkedTwins (symbol: ISymbol) : ISymbol list =
            match DocumentationCommentId.CreateDeclarationId symbol with
            | null -> []
            | pid when not (isNull symbol.ContainingAssembly) ->
                let paths =
                    symbol.Locations
                    |> Seq.filter (fun l -> l.IsInSource)
                    |> Seq.map (fun l -> l.SourceTree.FilePath)

                linkedProjects paths symbol.ContainingAssembly.Name
                |> List.choose (fun p ->
                    match p.GetCompilationAsync().Result with
                    | null -> None
                    | c -> Option.ofObj (DocumentationCommentId.GetFirstSymbolForDeclarationId(pid, c)))
            | _ -> []

        // every site of the symbol, the asking file's included, and of its
        // twins in the other compilations of its file
        let search (symbol: ISymbol) : (string * ReferenceSite) list =
            let own = sitesFound symbol

            let twins =
                match linkedTwins symbol with
                | [] -> []
                | twins ->
                    let seen =
                        HashSet<string * TextSpan>(
                            own |> Seq.map (fun (path, span, _) -> path.ToUpperInvariant(), span)
                        )

                    twins
                    |> List.collect sitesFound
                    |> List.filter (fun (path, span, _) -> seen.Add((path.ToUpperInvariant(), span)))

            own @ twins |> List.map (fun (path, _, site) -> path, site)

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

        fun (tree: SyntaxTree) ->
            // the rules ask, for a fix to a declaration of a linked file, what
            // the other compilations of the file are
            match solution.GetDocumentId tree with
            | null -> ()
            | own ->
                match linkedProjects [ tree.FilePath ] (solution.GetProject own.ProjectId).AssemblyName with
                | [] -> ()
                | projects ->
                    RuleContext.registerLinked
                        tree
                        (lazy (projects |> List.choose (fun p -> Option.ofObj (p.GetCompilationAsync().Result))))

            fun (symbol: ISymbol) ->
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

    /// The other C# projects of the solution compiling the tree's file (a
    /// linked `<Compile Include>`). The flavors of the tree's own project
    /// share its assembly name and are not among them.
    let linkedTo (solution: Solution) (tree: SyntaxTree) : Project list =
        match solution.GetDocumentId tree with
        | null -> []
        | _ when String.IsNullOrEmpty tree.FilePath -> []
        | own ->
            let assembly = (solution.GetProject own.ProjectId).AssemblyName

            solution.GetDocumentIdsWithFilePath tree.FilePath
            |> Seq.map (fun d -> d.ProjectId)
            |> Seq.distinct
            |> Seq.map solution.GetProject
            |> Seq.filter (fun p -> p.Language = LanguageNames.CSharp && p.AssemblyName <> assembly)
            |> List.ofSeq

    /// One file's string literals: their spellings, and the text each string
    /// holds (an interpolation's pieces each), read when the file is a test.
    /// Whether it is one is not this tree's to remember: its project's
    /// global usings can make it one without the file changing.
    type private FileLiterals =
        {
            Spellings: HashSet<string>
            Texts: Lazy<string[]>
        }

    // per tree: a file no fix touched is the same tree in the next snapshot
    // of the solution, and is not read again
    let private fileLiterals =
        System.Runtime.CompilerServices.ConditionalWeakTable<SyntaxTree, FileLiterals>()

    let private stringTokens (tree: SyntaxTree) =
        tree.GetRoot().DescendantTokens()
        |> Seq.filter (fun t ->
            t.IsKind SyntaxKind.StringLiteralToken
            || t.IsKind SyntaxKind.SingleLineRawStringLiteralToken
            || t.IsKind SyntaxKind.MultiLineRawStringLiteralToken
            || t.IsKind SyntaxKind.InterpolatedStringTextToken)

    let private literalsOf (tree: SyntaxTree) =
        fileLiterals.GetValue(
            tree,
            fun tree ->
                {
                    Spellings =
                        HashSet<string>(
                            stringTokens tree
                            |> Seq.filter (fun t -> t.IsKind SyntaxKind.StringLiteralToken)
                            |> Seq.map (fun t -> t.Text),
                            StringComparer.Ordinal
                        )
                    // the strings only, read again from the tree when asked:
                    // a token holds its whole syntax tree
                    Texts = lazy (stringTokens tree |> Seq.map (fun t -> t.ValueText) |> Array.ofSeq)
                }
        )

    /// A run of letters and digits.
    let private words = Regex(@"[\p{L}\p{N}]+", RegexOptions.Compiled)

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
                    // a multi-targeted project's flavors hold the same files:
                    // the first one read answers, the rest are not parsed
                    let seen = HashSet<string>(StringComparer.OrdinalIgnoreCase)

                    solution.Projects
                    |> Seq.filter (fun p -> p.Language = LanguageNames.CSharp)
                    |> Seq.collect (fun p ->
                        let trees =
                            p.Documents
                            |> Seq.filter (fun d -> not (isNull d.FilePath) && seen.Add d.FilePath)
                            |> Seq.choose (fun d ->
                                match d.GetSyntaxTreeAsync().Result with
                                | null -> None
                                | tree -> Some(d.FilePath, tree))
                            |> Array.ofSeq

                        // the project's global usings (its GlobalUsings.g.cs
                        // included) can make every file of it a test file
                        let globalNames = Text.globalUsingNames (trees |> Seq.map snd)

                        trees
                        |> Seq.map (fun (path, tree) ->
                            own.Contains path, Text.isTestFileWith globalNames tree, literalsOf tree))
                    |> Array.ofSeq
                    |> Some
                 with
                 | :? AggregateException
                 | :? OperationCanceledException
                 | :? InvalidOperationException -> None)

        // the test files' strings, and by every word they hold the strings
        // holding it: built once, where a scan per question costs questions x corpus
        let tests =
            lazy
                (files.Value
                 |> Option.map (fun files ->
                     let texts =
                         files
                         |> Array.filter (fun (_, isTest, _) -> isTest)
                         |> Array.collect (fun (_, _, f) -> f.Texts.Value)

                     let byWord = Dictionary<string, ResizeArray<int>>(StringComparer.Ordinal)

                     texts
                     |> Array.iteri (fun i t ->
                         let distinct =
                             words.Matches t |> Seq.cast<Match> |> Seq.map (fun m -> m.Value) |> Seq.distinct

                         for w in distinct do
                             match byWord.TryGetValue w with
                             | true, holders -> holders.Add i
                             | false, _ -> byWord.[w] <- ResizeArray [ i ])

                     texts, byWord))

        {
            Spelled =
                fun spelling ->
                    match files.Value with
                    | Some files ->
                        files
                        |> Array.exists (fun (isOwn, _, f) -> not isOwn && f.Spellings.Contains spelling)
                    | None -> true
            InTests =
                fun text ->
                    match tests.Value with
                    | Some(texts, byWord) ->
                        let holds (i: int) =
                            texts.[i].IndexOf(text, StringComparison.Ordinal) >= 0

                        // a word of the text with a non-word character on both sides
                        // is a whole word of any string holding the text: only the
                        // strings holding the rarest such word can
                        let inner =
                            words.Matches text
                            |> Seq.cast<Match>
                            |> Seq.filter (fun m -> m.Index > 0 && m.Index + m.Length < text.Length)
                            |> Seq.map (fun m ->
                                match byWord.TryGetValue m.Value with
                                | true, holders -> holders
                                | false, _ -> ResizeArray())
                            |> List.ofSeq

                        match inner with
                        | [] -> Seq.init texts.Length id |> Seq.exists holds
                        | holders -> holders |> List.minBy (fun h -> h.Count) |> Seq.exists holds
                    | None -> true
        }
