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
/// ambiguous with the value overload); a `Task` value is fixed too — the
/// original store kept a faulting task for good already, `GetOrAdd`
/// changes only the race (CR0050 notes the failure caching on it); the
/// factory captures no `ref struct`, `ref`/`out` parameter; the message
/// carries the `Lazy<T>` hint when the factory calls something (two
/// racing factories still both run; only the stored value is one).
///
/// CR0050 (correctness, note): `cd.GetOrAdd(k, factory)` whose value type
/// is `Task`, `ValueTask` or `Lazy` caches a faulted value for good: the
/// first failure is every later caller's answer. A factory that merely
/// throws caches nothing and is not reported. Quiet where the file removes
/// entries from that dictionary, by symbol: `TryRemove` or `Remove` on it,
/// directly or through a cast (`((ICollection&lt;KeyValuePair&lt;K, V&gt;&gt;)cache).Remove(pair)`);
/// whether the removal runs on a fault is not read. Also quiet where the scan
/// cannot tell: the dictionary handed on (an argument, an alias, a returned
/// value), a field or property that is not private or whose type has a part in
/// another file, a dictionary-typed removal with no name (`Pick().TryRemove(k, out _)`),
/// or a `GetOrAdd` with no name and any dictionary-typed removal in the file.
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
                                | :? IdentifierNameSyntax as pid ->
                                    match model.GetSymbolInfo(pid).Symbol with
                                    | :? IParameterSymbol as p -> p.RefKind <> RefKind.None
                                    | :? ILocalSymbol as l -> l.Type.IsRefLikeType || l.IsRef
                                    | _ -> false
                                | _ -> false)

                        if
                            kind = "Lazy"
                            || kind = "delegate"
                            || capturesUnsafe
                            || Text.mentionsName binder factory
                            // the factory's `_` would read the key the new lambda takes
                            || Text.mentionsName "_" factory
                        then
                            None
                        else
                            // a Task value takes the fix as well: `cd[k] = v` kept a
                            // faulting task for good already; only the race changes
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

/// `e` without the parens, casts and `!` around it.
[<TailCall>]
let rec private uncast (e: ExpressionSyntax) : ExpressionSyntax =
    match e with
    | :? ParenthesizedExpressionSyntax as p -> uncast p.Expression
    | :? CastExpressionSyntax as c -> uncast c.Expression
    | :? PostfixUnaryExpressionSyntax as p when p.IsKind SyntaxKind.SuppressNullableWarningExpression ->
        uncast p.Operand
    | :? BinaryExpressionSyntax as b when b.IsKind SyntaxKind.AsExpression -> uncast b.Left
    | _ -> e

/// The field, property, local or parameter a receiver names, past any cast;
/// `None` for anything else, a call or an index.
let private namedDictionary (model: SemanticModel) (receiver: ExpressionSyntax) : ISymbol option =
    let named = uncast receiver

    match named with
    | :? IdentifierNameSyntax
    | :? MemberAccessExpressionSyntax ->
        let symbol = model.GetSymbolInfo(named).Symbol

        match symbol with
        | :? IFieldSymbol
        | :? IPropertySymbol
        | :? ILocalSymbol
        | :? IParameterSymbol -> Some symbol.OriginalDefinition
        | _ -> None
    | _ -> None

let private isDictionaryType (t: ITypeSymbol) =
    match t with
    | null -> false
    | t ->
        match t.OriginalDefinition.ToDisplayString() with
        | "System.Collections.Concurrent.ConcurrentDictionary<TKey, TValue>"
        | "System.Collections.Generic.IDictionary<TKey, TValue>"
        | "System.Collections.IDictionary" -> true
        | "System.Collections.Generic.ICollection<T>" -> t.ToDisplayString().Contains "KeyValuePair<"
        | _ -> false

/// Is this mention of the dictionary something other than reading or storing
/// its entries, so that what happens to it is out of the scan's sight?
let private handedOn (id: IdentifierNameSyntax) =
    let mention: ExpressionSyntax =
        match id.Parent with
        | :? MemberAccessExpressionSyntax as q when obj.ReferenceEquals(q.Name, id) -> q
        | _ -> id

    match mention.Parent with
    | :? MemberAccessExpressionSyntax as m -> not (obj.ReferenceEquals(m.Expression, mention))
    | :? ElementAccessExpressionSyntax as a -> not (obj.ReferenceEquals(a.Expression, mention))
    | :? ForEachStatementSyntax as f -> not (obj.ReferenceEquals(f.Expression, mention))
    | :? AssignmentExpressionSyntax as a -> not (obj.ReferenceEquals(a.Left, mention))
    | _ -> true

let private cachedFailures (tree: SyntaxTree) (model: SemanticModel) : Suggestion list =
    // built for the first capturing GetOrAdd, which most files never have
    let removalReceivers =
        lazy
            (tree.GetRoot().DescendantNodes()
             |> Seq.choose (fun n ->
                 match n with
                 | :? InvocationExpressionSyntax as inv ->
                     match inv.Expression with
                     | :? MemberAccessExpressionSyntax as ma when
                         ma.Name.Identifier.ValueText = "TryRemove"
                         || ma.Name.Identifier.ValueText = "Remove"
                         ->
                         Some ma.Expression
                     | _ -> None
                 | _ -> None)
             |> List.ofSeq)

    let identifiers =
        lazy
            (tree.GetRoot().DescendantNodes()
             |> Seq.choose (fun n ->
                 match n with
                 | :? IdentifierNameSyntax as pid -> Some pid
                 | _ -> None)
             |> List.ofSeq)

    let removedOrUnknown (receiver: ExpressionSyntax) =
        let isDictionaryTyped (r: ExpressionSyntax) =
            isDictionaryType (model.GetTypeInfo(r).Type)

        match namedDictionary model receiver with
        | None -> removalReceivers.Value |> List.exists isDictionaryTyped
        | Some dictionary ->
            let isIt (s: ISymbol) =
                not (isNull s)
                && SymbolEqualityComparer.Default.Equals(s.OriginalDefinition, dictionary)

            let outOfSight =
                match dictionary with
                | :? IFieldSymbol
                | :? IPropertySymbol ->
                    dictionary.DeclaredAccessibility <> Accessibility.Private
                    || dictionary.ContainingType.DeclaringSyntaxReferences
                       |> Seq.exists (fun d -> not (obj.ReferenceEquals(d.SyntaxTree, tree)))
                | _ -> false

            outOfSight
            || removalReceivers.Value
               |> List.exists (fun r ->
                   match namedDictionary model r with
                   | Some named -> isIt named
                   | None -> isDictionaryTyped r)
            || identifiers.Value
               |> List.exists (fun id ->
                   id.Identifier.ValueText = dictionary.Name
                   && handedOn id
                   && isIt (model.GetSymbolInfo(id).Symbol))

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

                        if onlyThrows || removedOrUnknown m.Expression then
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
