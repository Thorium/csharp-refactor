/// CR0171 (correctness, fix): `foreach (var x in xs) { … xs.Remove(x); }`
/// — the enumerated collection mutated under its own enumeration. A
/// `List<T>`, `Collection<T>`, `Queue<T>`, `Stack<T>`, `LinkedList<T>`
/// (and, on .NET Framework, a `Dictionary`/`HashSet`) throws
/// `InvalidOperationException` on the next `MoveNext`. Two fixes: the
/// filter shape `if (cond) xs.Remove(x);` alone in the body of a
/// `List<T>` loop becomes `xs.RemoveAll(x => cond);`; any other shape
/// enumerates a snapshot — `foreach (var x in xs.ToList())` — and
/// mutates the original, the user's suggestion. Guards: the source is an
/// identifier or member access (a `.ToList()` is another object; `d.Keys`/
/// `d.Values` enumerate `d` itself — their enumerators check the owning
/// dictionary's version — so the mutations counted are those on `d`);
/// the mutating call is on the same reference (by symbol), outside nested
/// closures, and not followed by `break`/`return`/`throw` (mutate-and-leave
/// never reaches the next `MoveNext`); `Dictionary.Remove`/`Clear` tolerate
/// enumeration since .NET Core 3.0, an overwriting `Dictionary` indexer set
/// and `HashSet.Remove`/`Clear` since .NET 5, and none is reported in a compilation
/// targeting that runtime or later (read off its core library;
/// `netstandard` may run on .NET Framework);
/// `System.Linq` importable for the snapshot, and no `ToList` of the
/// repository's own in scope for the source (it would take the call).
module CSharp.Refactor.EnumerationMutation

open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax
open Microsoft.CodeAnalysis.Text

[<Literal>]
let Code = "CR0171"

let private isClosure (n: SyntaxNode) =
    n :? AnonymousFunctionExpressionSyntax || n :? LocalFunctionStatementSyntax

let private ownNodes (root: SyntaxNode) =
    root.DescendantNodes(fun n -> obj.ReferenceEquals(n, root) || not (isClosure n))

let private collections =
    set
        [
            "System.Collections.Generic.List<T>"
            "System.Collections.Generic.IList<T>"
            "System.Collections.Generic.ICollection<T>"
            "System.Collections.ObjectModel.Collection<T>"
            "System.Collections.ObjectModel.ObservableCollection<T>"
            "System.Collections.Generic.HashSet<T>"
            "System.Collections.Generic.ISet<T>"
            "System.Collections.Generic.SortedSet<T>"
            "System.Collections.Generic.Dictionary<TKey, TValue>"
            "System.Collections.Generic.IDictionary<TKey, TValue>"
            "System.Collections.Generic.SortedDictionary<TKey, TValue>"
            "System.Collections.Generic.Queue<T>"
            "System.Collections.Generic.Stack<T>"
            "System.Collections.Generic.LinkedList<T>"
        ]

let private mutators =
    set
        [
            "Add"
            "AddRange"
            "Insert"
            "InsertRange"
            "Remove"
            "RemoveAt"
            "RemoveAll"
            "RemoveRange"
            "Clear"
            "Enqueue"
            "Dequeue"
            "Push"
            "Pop"
            "AddFirst"
            "AddLast"
            "RemoveFirst"
            "RemoveLast"
            "TryAdd"
            "UnionWith"
            "ExceptWith"
            "IntersectWith"
            "Sort"
            "Reverse"
        ]

/// Is the statement holding the mutation followed by a leave — so the
/// enumerator never moves again?
[<TailCall>]
let rec private leavesAfter (s: StatementSyntax) =
    match s.Parent with
    | :? BlockSyntax as b ->
        let i = b.Statements.IndexOf s

        if i + 1 < b.Statements.Count then
            match b.Statements.[i + 1] with
            | :? BreakStatementSyntax
            | :? ReturnStatementSyntax
            | :? ThrowStatementSyntax -> true
            | _ -> false
        else
            match b.Parent with
            | :? IfStatementSyntax as parentIf -> leavesAfter parentIf
            | :? ElseClauseSyntax as e ->
                match e.Parent with
                | :? IfStatementSyntax as parentIf -> leavesAfter parentIf
                | _ -> false
            | _ -> false
    | :? IfStatementSyntax as parentIf -> leavesAfter parentIf
    | :? ElseClauseSyntax as e ->
        match e.Parent with
        | :? IfStatementSyntax as parentIf -> leavesAfter parentIf
        | _ -> false
    | _ -> false

/// Does the compilation run on .NET Core at least as new as `version`, read
/// off its core library: the reference pack's `System.Runtime` (4.2.2 is
/// .NET Core 3.x, 5.0.0 .NET 5) or the runtime's own `System.Private.CoreLib`
/// (4.0.0 through 3.x, then the runtime's version). `mscorlib` is .NET
/// Framework and `netstandard` any runtime: neither vouches for anything.
let coreRuntimeAtLeast (version: System.Version) (compilation: Compilation) =
    match compilation.GetSpecialType(SpecialType.System_Object).ContainingAssembly with
    | null -> false
    | core ->
        (core.Name = "System.Runtime" || core.Name = "System.Private.CoreLib")
        && core.Identity.Version >= version

/// .NET Core 3.0 stopped `Dictionary.Remove`/`Clear` invalidating
/// enumerators; .NET 5 did the same for an overwriting indexer set and for
/// `HashSet.Remove`/`Clear` (measured: those still throw on 3.1, and every
/// one of them on .NET Framework 4.8).
let private dictionaryRemoveTolerated = System.Version(4, 2, 2)
let private overwriteAndSetRemoveTolerated = System.Version(5, 0, 0)

let analyze (tree: SyntaxTree) (model: SemanticModel) (_ctx: RuleContext) : Suggestion list =
    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun n ->
        match n with
        | :? ForEachStatementSyntax as f ->
            let source = f.Expression

            let plainSource =
                match source with
                | :? IdentifierNameSyntax
                | :? MemberAccessExpressionSyntax -> true
                | _ -> false

            // `d.Keys`/`d.Values` enumerate the dictionary itself: the key and value
            // collections' enumerators check the owning dictionary's version, so an
            // `Add` on `d` under `foreach (var k in d.Keys)` throws just the same
            let root: ExpressionSyntax =
                match source with
                | :? MemberAccessExpressionSyntax as ma when
                    (ma.Name.Identifier.ValueText = "Keys" || ma.Name.Identifier.ValueText = "Values")
                    && (match model.GetTypeInfo(ma.Expression).Type with
                        | null -> false
                        | t -> t.OriginalDefinition.ToDisplayString().Contains "Dictionary<")
                    ->
                    ma.Expression
                | _ -> source

            let sourceType = model.GetTypeInfo(root).Type

            if not plainSource || isNull sourceType then
                None
            else
                let typeName = sourceType.OriginalDefinition.ToDisplayString()

                if not (collections.Contains typeName) then
                    None
                else
                    let isDictionary = typeName = "System.Collections.Generic.Dictionary<TKey, TValue>"
                    let isHashSet = typeName = "System.Collections.Generic.HashSet<T>"

                    let removeTolerated =
                        isDictionary && coreRuntimeAtLeast dictionaryRemoveTolerated model.Compilation
                        || isHashSet && coreRuntimeAtLeast overwriteAndSetRemoveTolerated model.Compilation

                    // an overwrite of an existing key no longer invalidates the
                    // enumerator; an insert still does, and the rule cannot tell
                    // the two apart, so it stays quiet on both
                    let indexerSetTolerated =
                        isDictionary
                        && coreRuntimeAtLeast overwriteAndSetRemoveTolerated model.Compilation

                    // the mutating statements on the same reference
                    let mutations =
                        ownNodes f.Statement
                        |> Seq.choose (fun d ->
                            match d with
                            | :? InvocationExpressionSyntax as inv ->
                                match inv.Expression with
                                | :? MemberAccessExpressionSyntax as ma when
                                    mutators.Contains ma.Name.Identifier.ValueText
                                    && Guards.sameReference model ma.Expression root
                                    && not (
                                        removeTolerated
                                        && (ma.Name.Identifier.ValueText = "Remove"
                                            || ma.Name.Identifier.ValueText = "Clear")
                                    )
                                    ->
                                    Some(inv :> SyntaxNode, ma.Name.Identifier.ValueText)
                                | _ -> None
                            | :? AssignmentExpressionSyntax as a ->
                                match a.Left with
                                | :? ElementAccessExpressionSyntax as ea when
                                    Guards.sameReference model ea.Expression root && not indexerSetTolerated
                                    ->
                                    Some(a :> SyntaxNode, "indexer set")
                                | _ -> None
                            | _ -> None)
                        |> Seq.filter (fun (node, _) ->
                            match node.FirstAncestorOrSelf<StatementSyntax>() with
                            | null -> true
                            | s -> not (leavesAfter s))
                        |> List.ofSeq

                    match mutations with
                    | [] -> None
                    | (firstNode, name) :: _ ->
                        let sourceText = source.ToString()
                        let rootText = root.ToString()

                        // the filter shape: `if (cond) xs.Remove(x);` alone
                        let filter =
                            let body: StatementSyntax =
                                match f.Statement with
                                | :? BlockSyntax as b when b.Statements.Count = 1 -> b.Statements.[0]
                                | other -> other

                            match body with
                            | :? IfStatementSyntax as s when
                                isNull s.Else
                                && typeName = "System.Collections.Generic.List<T>"
                                && mutations.Length = 1
                                ->
                                let inner: StatementSyntax =
                                    match s.Statement with
                                    | :? BlockSyntax as b when b.Statements.Count = 1 -> b.Statements.[0]
                                    | other -> other

                                match inner with
                                | :? ExpressionStatementSyntax as es ->
                                    match es.Expression with
                                    | :? InvocationExpressionSyntax as inv when
                                        obj.ReferenceEquals(inv, firstNode)
                                        && name = "Remove"
                                        && inv.ArgumentList.Arguments.Count = 1
                                        && inv.ArgumentList.Arguments.[0].Expression.ToString() =
                                            f.Identifier.ValueText
                                        && Guards.isPureExpression model s.Condition
                                        && not (Text.mentionsName sourceText s.Condition)
                                        && not (Text.holdsCommentOrDirective f)
                                        ->
                                        Some $"{sourceText}.RemoveAll({f.Identifier.ValueText} => {s.Condition});"
                                    | _ -> None
                                | _ -> None
                            | _ -> None

                        let message =
                            $"'{rootText}' is changed ({name}) while its own foreach enumerates it: the next MoveNext throws InvalidOperationException — enumerate a snapshot ({sourceText}.ToList()) and change the original, or collect the changes and apply them after the loop"

                        match filter with
                        | Some replacement ->
                            Some(
                                {
                                    Code = Code
                                    Message = message
                                    Span = firstNode.Span
                                    Fixes =
                                        [
                                            Suggestion.fix
                                                "Replace the loop with RemoveAll"
                                                Code
                                                [ Suggestion.replace f.Span replacement ]
                                        ]
                                }
                                |> Guards.verified model
                            )
                        | None ->
                            match Usings.importEdit model tree source.SpanStart "System.Linq" "Enumerable" with
                            | None -> Some(Suggestion.note Code message firstNode.Span)
                            // a `ToList` of the repository's own with a more specific
                            // receiver would take the call and snapshot nothing
                            | Some _ when
                                not (
                                    Guards.onlyBclCandidates
                                        model
                                        source.SpanStart
                                        (model.GetTypeInfo(source).Type)
                                        "ToList"
                                )
                                ->
                                Some(Suggestion.note Code message firstNode.Span)
                            | Some imports ->
                                Some(
                                    {
                                        Code = Code
                                        Message = message
                                        Span = firstNode.Span
                                        Fixes =
                                            [
                                                Suggestion.fix
                                                    "Enumerate a snapshot"
                                                    Code
                                                    (imports @ [ Suggestion.insert source.Span.End ".ToList()" ])
                                            ]
                                    }
                                    |> Guards.verified model
                                )
        | _ -> None)
    |> List.ofSeq
