/// The note-only collection rules: shapes where a rewrite is the author's
/// call but the cost is not visible at the site.
///
/// CR0026 (performance): `xs.Count()`, `xs.ElementAt(i)`, `xs.Last()`,
/// `xs.LongCount()` inside a loop on a receiver typed as a bare
/// `IEnumerable<T>` — a query or a generator walked once per iteration.
/// A collection (array, `ICollection<T>`, `IReadOnlyCollection<T>`) is
/// CA1826/CA1829's and stays quiet; the receiver must be the same on every
/// iteration (declared outside the loop); `Count()` in a `for` condition
/// is per iteration too.
///
/// CR0027 (correctness): a deferred `Enumerable`/`Queryable` operator as a
/// statement — `xs.Select(x => Log(x));`, `xs.Where(p);` — builds a query
/// and runs nothing. `_ = xs.Select(…);` is a decision and stays quiet.
///
/// CR0030 (correctness): a parameter typed `IEnumerable<T>` enumerated
/// twice on one path — `if (xs.Any()) foreach (var x in xs)` — runs a
/// query or a generator twice. Sites in the two arms of one `if`, or in
/// different `switch` sections, are one path each; a lambda defers. A
/// collection interface as the parameter type is not this note (yields to
/// CA1851 where it is on).
///
/// CR0034 (correctness, priority): a query (typed `IQueryable<T>`) that
/// mentions the outer loop variable, enumerated inside a loop — one
/// statement per element, the N+1. An outer source that is chunked or
/// paged (`Chunk`, `Skip`/`Take`, a batch `Contains`) is a batch loop and
/// stays quiet; a query in a `Select`/`ForEach` callback is a loop too.
///
/// CR0035 (performance): an iterator that yields from a `foreach` over its
/// own recursive call (`foreach (var c in Walk(child)) yield return c;`, or
/// `children.SelectMany(Walk)`) nests an enumerator per level: O(depth)
/// per element. A tail `return Walk(child)` without `yield` redirects and
/// does not count.
module CSharp.Refactor.LinqNotes

open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax
open Microsoft.CodeAnalysis.Text

[<Literal>]
let RewalkCode = "CR0026"

[<Literal>]
let LazyStatementCode = "CR0027"

[<Literal>]
let DoubleEnumerationCode = "CR0030"

[<Literal>]
let QueryInLoopCode = "CR0034"

[<Literal>]
let RecursiveIteratorCode = "CR0035"

let private rewalkers =
    set
        [
            "Count"
            "LongCount"
            "ElementAt"
            "ElementAtOrDefault"
            "Last"
            "LastOrDefault"
        ]

// ---- CR0026 ----

let private rewalks (tree: SyntaxTree) (model: SemanticModel) : Suggestion list =
    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun node ->
        match node with
        | :? InvocationExpressionSyntax as inv when rewalkers.Contains(Linq.nameOf inv) ->
            match Linq.enumerableCall model inv, Linq.receiverOf inv, Linq.enclosingLoop inv with
            | Some _, Some receiver, Some loop ->
                let t = model.GetTypeInfo(receiver).Type

                if
                    Linq.isGenericEnumerable t
                    && not (Linq.isCollection t)
                    && Linq.invariantOutside model tree loop receiver
                then
                    Some(
                        Suggestion.note
                            RewalkCode
                            $"'{Linq.nameOf inv}()' walks '{receiver}' again on every iteration: materialise it once before the loop"
                            inv.Span
                    )
                else
                    None
            | _ -> None
        | _ -> None)
    |> List.ofSeq

// ---- CR0027 ----

let private lazyStatements (tree: SyntaxTree) (model: SemanticModel) : Suggestion list =
    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun node ->
        match node with
        | :? ExpressionStatementSyntax as s ->
            match s.Expression with
            | :? InvocationExpressionSyntax as inv when Linq.deferredOperators.Contains(Linq.nameOf inv) ->
                let isLinq =
                    (Linq.enumerableCall model inv).IsSome || (Linq.queryableCall model inv).IsSome

                if isLinq then
                    let effect =
                        inv.ArgumentList.DescendantNodes()
                        |> Seq.tryPick (fun n ->
                            match n with
                            | :? InvocationExpressionSyntax as call -> Some(call.Expression.ToString())
                            | :? AssignmentExpressionSyntax as a -> Some(a.ToString())
                            | _ -> None)

                    let named =
                        match effect with
                        | Some e -> $" — the '{e}' inside never runs"
                        | None -> ""

                    Some(
                        Suggestion.note
                            LazyStatementCode
                            $"'{Linq.nameOf inv}' is lazy: as a statement it builds a query and runs nothing{named}; enumerate it or drop it"
                            inv.Span
                    )
                else
                    None
            | _ -> None
        | _ -> None)
    |> List.ofSeq

// ---- CR0030 ----

/// Two sites are exclusive when the nearest branching owner they share
/// puts them in different arms.
let private exclusive (a: SyntaxNode) (b: SyntaxNode) =
    let arms (n: SyntaxNode) =
        n.AncestorsAndSelf()
        |> Seq.choose (fun x ->
            match x.Parent with
            | :? IfStatementSyntax as ifs when obj.ReferenceEquals(ifs.Statement, x) -> Some(ifs.SpanStart, 0)
            | :? ElseClauseSyntax as e -> Some(e.Parent.SpanStart, 1)
            | :? SwitchSectionSyntax as sec -> Some(sec.Parent.SpanStart, sec.SpanStart)
            | _ -> None)
        |> Map.ofSeq

    let aa = arms a
    let bb = arms b

    aa
    |> Map.exists (fun owner arm ->
        match bb.TryFind owner with
        | Some other -> other <> arm
        | None -> false)

let private insideLambda (node: SyntaxNode) =
    node.Ancestors()
    |> Seq.exists (fun a -> a :? AnonymousFunctionExpressionSyntax || a :? LocalFunctionStatementSyntax)

let private doubleEnumerations (tree: SyntaxTree) (model: SemanticModel) : Suggestion list =
    tree.GetRoot().DescendantNodes()
    |> Seq.collect (fun node ->
        match node with
        | :? BaseMethodDeclarationSyntax as m when not (isNull m.Body) || (m :? MethodDeclarationSyntax) ->
            let body: SyntaxNode =
                match m with
                | :? MethodDeclarationSyntax as md when isNull md.Body && not (isNull md.ExpressionBody) ->
                    md.ExpressionBody :> SyntaxNode
                | _ -> m.Body :> SyntaxNode

            if isNull body then
                []
            else
                m.ParameterList.Parameters
                |> Seq.choose (fun p ->
                    match model.GetDeclaredSymbol p with
                    | null -> None
                    | ps when
                        ps.Type.OriginalDefinition.ToDisplayString() = "System.Collections.Generic.IEnumerable<T>"
                        ->
                        let refersTo (e: ExpressionSyntax) =
                            match e with
                            | :? IdentifierNameSyntax as id ->
                                SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(id).Symbol, ps)
                            | _ -> false

                        let sites =
                            body.DescendantNodes()
                            |> Seq.choose (fun n ->
                                match n with
                                | :? ForEachStatementSyntax as f when refersTo f.Expression ->
                                    Some(f.Expression :> SyntaxNode)
                                | :? InvocationExpressionSyntax as inv when
                                    Linq.consumers.Contains(Linq.nameOf inv)
                                    && (Linq.receiverOf inv |> Option.exists refersTo)
                                    && (Linq.enumerableCall model inv).IsSome
                                    ->
                                    Some(inv :> SyntaxNode)
                                | _ -> None)
                            |> Seq.filter (insideLambda >> not)
                            |> List.ofSeq

                        let pairs =
                            [
                                for i in 0 .. sites.Length - 1 do
                                    for j in i + 1 .. sites.Length - 1 do
                                        if not (exclusive sites.[i] sites.[j]) then
                                            yield sites.[i], sites.[j]
                            ]

                        match pairs with
                        | (first, second) :: _ ->
                            Some(
                                Suggestion.note
                                    DoubleEnumerationCode
                                    $"'{ps.Name}' is enumerated here and again at line {tree.GetLineSpan(second.Span).StartLinePosition.Line + 1}: an IEnumerable may be a query or a generator, run twice — materialise it once (ToList/ToArray) before the first use, or read it in one pass"
                                    first.Span
                            )
                        | [] -> None
                    | _ -> None)
                |> List.ofSeq
        | _ -> [])
    |> List.ofSeq

// ---- CR0034 ----

let private batchWords = [ "Chunk("; "Skip("; "Take("; "Batch("; "Page("; "Paged" ]

let private queriesInLoops (tree: SyntaxTree) (model: SemanticModel) : Suggestion list =
    // outer iterations: a foreach over a source, or a lambda handed to a call
    let outerVariables =
        tree.GetRoot().DescendantNodes()
        |> Seq.choose (fun n ->
            match n with
            | :? ForEachStatementSyntax as f ->
                match model.GetDeclaredSymbol f with
                | null -> None
                | v -> Some(f.Statement :> SyntaxNode, v :> ISymbol, f.Expression.ToString())
            | :? SimpleLambdaExpressionSyntax as l when not (isNull l.Body) ->
                match model.GetDeclaredSymbol l.Parameter, l.Parent with
                | null, _ -> None
                | p, (:? ArgumentSyntax as arg) ->
                    let call = arg.Parent.Parent

                    match call with
                    | :? InvocationExpressionSyntax as inv ->
                        // the source of the callback is the receiver of the call
                        let source =
                            Linq.receiverOf inv
                            |> Option.map (fun r -> r.ToString())
                            |> Option.defaultValue ""

                        Some(l.Body, p :> ISymbol, source)
                    | _ -> None
                | _ -> None
            | _ -> None)
        |> List.ofSeq

    outerVariables
    |> List.choose (fun (body, variable, sourceText) ->
        if batchWords |> List.exists (fun w -> sourceText.Contains w) then
            None
        else
            let mentions (n: SyntaxNode) =
                n.DescendantNodesAndSelf()
                |> Seq.exists (fun x ->
                    match x with
                    | :? IdentifierNameSyntax as id ->
                        SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(id).Symbol, variable)
                    | _ -> false)

            let isBatchFilter (n: SyntaxNode) =
                // `chunk.Contains(o.Id)`, `ids.Skip(..)`: one statement per batch
                n.DescendantNodesAndSelf()
                |> Seq.exists (fun x ->
                    match x with
                    | :? InvocationExpressionSyntax as inv ->
                        let name = Linq.nameOf inv

                        (name = "Contains" || name = "Skip" || name = "Take")
                        && (Linq.receiverOf inv |> Option.exists (fun r -> mentions r))
                    | _ -> false)

            // an enumeration of a queryable that mentions the outer variable
            let query =
                body.DescendantNodesAndSelf()
                |> Seq.tryPick (fun n ->
                    let queryable (e: ExpressionSyntax) =
                        Linq.isQueryable (model.GetTypeInfo(e).Type)

                    match n with
                    | :? ForEachStatementSyntax as f when queryable f.Expression && mentions f.Expression ->
                        Some(f.Expression :> SyntaxNode)
                    | :? InvocationExpressionSyntax as inv when
                        Linq.consumers.Contains(Linq.nameOf inv)
                        && (Linq.receiverOf inv |> Option.exists (fun r -> queryable r && mentions r))
                        ->
                        Some(inv :> SyntaxNode)
                    | :? AwaitExpressionSyntax as a ->
                        // `await q.ToListAsync()` and friends
                        match a.Expression with
                        | :? InvocationExpressionSyntax as inv when
                            (Linq.nameOf inv).EndsWith "Async"
                            && (Linq.receiverOf inv |> Option.exists (fun r -> queryable r && mentions r))
                            ->
                            Some(inv :> SyntaxNode)
                        | _ -> None
                    | _ -> None)

            match query with
            | Some q when not (isBatchFilter q) ->
                Some(
                    Suggestion.note
                        QueryInLoopCode
                        $"A query per element of the outer loop ('{variable.Name}'): one statement per row, the N+1 — join or batch the ids"
                        q.Span
                )
            | _ -> None)

// ---- CR0035 ----

let private recursiveIterators (tree: SyntaxTree) (model: SemanticModel) : Suggestion list =
    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun node ->
        match node with
        | :? MethodDeclarationSyntax as m when not (isNull m.Body) ->
            let isIterator =
                m.Body.DescendantNodes()
                |> Seq.exists (fun n -> n :? YieldStatementSyntax && not (insideLambda n))

            if not isIterator then
                None
            else
                let self = model.GetDeclaredSymbol m

                let callsSelf (e: ExpressionSyntax) =
                    match e with
                    | :? InvocationExpressionSyntax as inv ->
                        match model.GetSymbolInfo(inv).Symbol with
                        | :? IMethodSymbol as callee ->
                            SymbolEqualityComparer.Default.Equals(callee.OriginalDefinition, self)
                        | _ -> false
                    | _ -> false

                let namesSelf (e: ExpressionSyntax) =
                    // a method group converts: the symbol may sit among the candidates
                    let info = model.GetSymbolInfo e

                    Seq.append (Option.toList (Option.ofObj info.Symbol)) info.CandidateSymbols
                    |> Seq.exists (fun s ->
                        match s with
                        | :? IMethodSymbol as callee ->
                            SymbolEqualityComparer.Default.Equals(callee.OriginalDefinition, self)
                        | _ -> false)

                m.Body.DescendantNodes()
                |> Seq.tryPick (fun n ->
                    match n with
                    | :? ForEachStatementSyntax as f when callsSelf f.Expression ->
                        // the body yields the element straight through
                        let yieldsThrough =
                            f.Statement.DescendantNodesAndSelf()
                            |> Seq.exists (fun s -> s :? YieldStatementSyntax)

                        if yieldsThrough then
                            Some(f.Expression :> SyntaxNode)
                        else
                            None
                    | :? InvocationExpressionSyntax as inv when
                        Linq.nameOf inv = "SelectMany"
                        && inv.ArgumentList.Arguments.Count > 0
                        && namesSelf inv.ArgumentList.Arguments.[0].Expression
                        ->
                        Some(inv :> SyntaxNode)
                    | _ -> None)
                |> Option.map (fun site ->
                    Suggestion.note
                        RecursiveIteratorCode
                        $"'{m.Identifier.ValueText}' re-yields its own recursive enumeration: every element passes through one enumerator per level (O(depth) each); walk with an explicit stack"
                        site.Span)
        | _ -> None)
    |> List.ofSeq

let analyze (tree: SyntaxTree) (model: SemanticModel) (_ctx: RuleContext) : Suggestion list =
    rewalks tree model
    @ lazyStatements tree model
    @ doubleEnumerations tree model
    @ queriesInLoops tree model
    @ recursiveIterators tree model
