/// ConcurrentDictionary shapes.
///
/// CR0049 (correctness, fix): a check-then-store on a
/// `ConcurrentDictionary` races — two threads miss, both compute, the
/// later store wins; the miss arm is `GetOrAdd`.
///
///     if (!cd.TryGetValue(k, out var v))       if (!cd.TryGetValue(k, out var v))
///     {                                   →    {
///         v = Compute(k);                          v = cd.GetOrAdd(k, _ => Compute(k));
///         cd[k] = v;                           }
///     }
///
/// The hit path keeps `TryGetValue` (measured: 2.1 ns and no delegate on
/// the hit against 7.3 ns and 64 B for `GetOrAdd` with a lambda). Guards:
/// the receiver is typed `ConcurrentDictionary<K,V>`; the miss arm is
/// exactly `v = <expr>;` then the store — `cd[k] = v;`, `cd.TryAdd(k, v);`
/// or `cd.AddOrUpdate(k, v, (_, _) => v);` — with the same key text; the
/// key is a pure atom (or a tuple of atoms); the value type is not
/// `Lazy<T>` (CR0050's subject) nor a delegate (the lambda would be
/// ambiguous with the value overload) nor `Task` (the note alone); the
/// factory captures no `ref struct`, `ref`/`out` parameter; the message
/// carries the `Lazy<T>` hint when the factory calls something (two
/// racing factories still both run; only the stored value is one).
///
/// CR0050 (correctness, note): `cd.GetOrAdd(k, factory)` whose value type
/// is `Task`, `ValueTask` or `Lazy` caches a faulted value for good: the
/// first failure is every later caller's answer. A factory that merely
/// throws caches nothing and is not reported.
module CSharp.Refactor.ConcurrentCache

open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax
open Microsoft.CodeAnalysis.Text

[<Literal>]
let GetOrAddCode = "CR0049"

[<Literal>]
let CachedFailureCode = "CR0050"

let private concurrentDictionary (model: SemanticModel) (e: ExpressionSyntax) : INamedTypeSymbol option =
    match model.GetTypeInfo(e).Type with
    | :? INamedTypeSymbol as n when
        n.OriginalDefinition.ToDisplayString() = "System.Collections.Concurrent.ConcurrentDictionary<TKey, TValue>"
        ->
        Some n
    | _ -> None

let private valueKind (t: ITypeSymbol) =
    match t with
    | null -> "unknown"
    | t ->
        let n = t.OriginalDefinition.ToDisplayString()

        if n.StartsWith "System.Lazy<" then
            "Lazy"
        elif
            n.StartsWith "System.Threading.Tasks.Task"
            || n.StartsWith "System.Threading.Tasks.ValueTask"
        then
            "Task"
        elif t.TypeKind = TypeKind.Delegate then
            "delegate"
        else
            "value"

let rec private isAtom (e: ExpressionSyntax) =
    match e with
    | :? IdentifierNameSyntax
    | :? LiteralExpressionSyntax -> true
    | :? MemberAccessExpressionSyntax as m -> isAtom m.Expression
    | :? TupleExpressionSyntax as t -> t.Arguments |> Seq.forall (fun a -> isAtom a.Expression)
    | :? ThisExpressionSyntax -> true
    | _ -> false

// ---- CR0049 ----

let private checkThenStore (tree: SyntaxTree) (model: SemanticModel) : Suggestion list =
    let text = tree.GetText()

    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun n ->
        match n with
        | :? IfStatementSyntax as ifs when isNull ifs.Else ->
            // `!cd.TryGetValue(k, out var v)`
            let test =
                match ifs.Condition with
                | :? PrefixUnaryExpressionSyntax as u when u.IsKind SyntaxKind.LogicalNotExpression ->
                    match u.Operand with
                    | :? InvocationExpressionSyntax as inv when
                        Linq.nameOf inv = "TryGetValue" && inv.ArgumentList.Arguments.Count = 2
                        ->
                        match inv.Expression with
                        | :? MemberAccessExpressionSyntax as m ->
                            match
                                concurrentDictionary model m.Expression, inv.ArgumentList.Arguments.[1].Expression
                            with
                            | Some cd, (:? DeclarationExpressionSyntax as d) ->
                                match d.Designation with
                                | :? SingleVariableDesignationSyntax as v ->
                                    Some(
                                        m.Expression,
                                        cd,
                                        inv.ArgumentList.Arguments.[0].Expression,
                                        v.Identifier.ValueText
                                    )
                                | _ -> None
                            | _ -> None
                        | _ -> None
                    | _ -> None
                | _ -> None

            match test with
            | None -> None
            | Some(receiver, cd, key, binder) ->
                let statements =
                    match ifs.Statement with
                    | :? BlockSyntax as b -> List.ofSeq b.Statements
                    | s -> [ s ]

                // `v = expr;` then the store
                match statements with
                | [ (:? ExpressionStatementSyntax as compute); (:? ExpressionStatementSyntax as store) ] ->
                    let factory =
                        match compute.Expression with
                        | :? AssignmentExpressionSyntax as a when
                            a.IsKind SyntaxKind.SimpleAssignmentExpression && a.Left.ToString() = binder
                            ->
                            Some a.Right
                        | _ -> None

                    let storesIt =
                        let cdText = receiver.ToString()
                        let keyText = key.ToString()

                        match store.Expression with
                        | :? AssignmentExpressionSyntax as a when a.IsKind SyntaxKind.SimpleAssignmentExpression ->
                            match a.Left with
                            | :? ElementAccessExpressionSyntax as e ->
                                e.Expression.ToString() = cdText
                                && e.ArgumentList.Arguments.Count = 1
                                && e.ArgumentList.Arguments.[0].Expression.ToString() = keyText
                                && a.Right.ToString() = binder
                            | _ -> false
                        | :? InvocationExpressionSyntax as inv ->
                            let name = Linq.nameOf inv
                            let args = inv.ArgumentList.Arguments

                            (Linq.receiverOf inv |> Option.exists (fun r -> r.ToString() = cdText))
                            && ((name = "TryAdd"
                                 && args.Count = 2
                                 && args.[0].Expression.ToString() = keyText
                                 && args.[1].Expression.ToString() = binder)
                                || (name = "AddOrUpdate"
                                    && args.Count = 3
                                    && args.[0].Expression.ToString() = keyText
                                    && args.[1].Expression.ToString() = binder
                                    && args.[2].Expression.ToString().EndsWith("=> " + binder)))
                        | _ -> false

                    match factory with
                    | Some factory when storesIt && isAtom key ->
                        let valueType = cd.TypeArguments.[1]
                        let kind = valueKind valueType

                        let capturesUnsafe =
                            factory.DescendantNodesAndSelf()
                            |> Seq.exists (fun x ->
                                match x with
                                | :? IdentifierNameSyntax as id ->
                                    match model.GetSymbolInfo(id).Symbol with
                                    | :? IParameterSymbol as p -> p.RefKind <> RefKind.None
                                    | :? ILocalSymbol as l -> l.Type.IsRefLikeType || l.IsRef
                                    | _ -> false
                                | _ -> false)

                        if
                            kind = "Lazy"
                            || kind = "delegate"
                            || capturesUnsafe
                            || Text.mentionsName binder factory
                        then
                            None
                        elif kind = "Task" then
                            Some(
                                Suggestion.note
                                    GetOrAddCode
                                    "A check-then-store on a ConcurrentDictionary races: two threads miss and both store. GetOrAdd would close it, but with a Task value the first failure would be cached for good (CR0050) — consider a Lazy<Task<T>> with retry"
                                    ifs.Condition.Span
                            )
                        else
                            let callsSomething =
                                factory.DescendantNodesAndSelf()
                                |> Seq.exists (fun x ->
                                    x :? InvocationExpressionSyntax || x :? ObjectCreationExpressionSyntax)

                            let replacement =
                                binder
                                + " = "
                                + receiver.ToString()
                                + ".GetOrAdd("
                                + key.ToString()
                                + ", _ => "
                                + factory.ToString()
                                + ");"

                            let span = TextSpan.FromBounds(compute.SpanStart, store.Span.End)
                            let edit = Suggestion.replace span replacement

                            if Guards.speculativeCheck model [ edit ] then
                                Some
                                    {
                                        Code = GetOrAddCode
                                        Message =
                                            "A check-then-store on a ConcurrentDictionary races — two threads miss and both store; GetOrAdd stores one"
                                            + (if callsSomething then
                                                   " (both factories still run; a Lazy<T> value would run one)"
                                               else
                                                   "")
                                        Span = ifs.Condition.Span
                                        Fixes = [ Suggestion.fix "Use GetOrAdd on the miss" GetOrAddCode [ edit ] ]
                                    }
                            else
                                None
                    | _ -> None
                | _ -> None
        | _ -> None)
    |> List.ofSeq

// ---- CR0050 ----

let private cachedFailures (tree: SyntaxTree) (model: SemanticModel) : Suggestion list =
    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun n ->
        match n with
        | :? InvocationExpressionSyntax as inv when
            Linq.nameOf inv = "GetOrAdd" && inv.ArgumentList.Arguments.Count >= 2
            ->
            match inv.Expression with
            | :? MemberAccessExpressionSyntax as m ->
                match concurrentDictionary model m.Expression with
                | Some cd ->
                    let kind = valueKind cd.TypeArguments.[1]

                    if kind = "Task" || kind = "Lazy" then
                        // a factory that only throws caches nothing
                        let onlyThrows =
                            match inv.ArgumentList.Arguments.[1].Expression with
                            | :? LambdaExpressionSyntax as l ->
                                match l.Body with
                                | :? ThrowExpressionSyntax -> true
                                | :? BlockSyntax as b ->
                                    b.Statements.Count = 1 && (b.Statements.[0] :? ThrowStatementSyntax)
                                | _ -> false
                            | _ -> false

                        if onlyThrows then
                            None
                        else
                            Some(
                                Suggestion.note
                                    CachedFailureCode
                                    $"GetOrAdd with a {kind} value caches a faulted value for good: the first failure becomes every later caller's answer — evict on failure, or cache a factory that retries"
                                    inv.Span
                            )
                    else
                        None
                | None -> None
            | _ -> None
        | _ -> None)
    |> List.ofSeq

let analyze (tree: SyntaxTree) (model: SemanticModel) (_ctx: RuleContext) : Suggestion list =
    checkThenStore tree model @ cachedFailures tree model
