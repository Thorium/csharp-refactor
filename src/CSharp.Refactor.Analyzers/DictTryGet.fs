/// CR0182 (performance, fix): a `ContainsKey` check and an indexer read of
/// the same key under it look the key up twice; `TryGetValue` looks once and
/// hands the value over:
///
///     if (d.ContainsKey(k)) Use(d[k]);          if (d.TryGetValue(k, out var value)) Use(value);
///     if (!d.ContainsKey(k)) return; … d[k] …  if (!d.TryGetValue(k, out var value)) return; … value …
///     d.ContainsKey(k) ? d[k] : fallback        d.TryGetValue(k, out var value) ? value : fallback
///
/// Where the value is read: the rest of an `&&` chain after the check and
/// the `if`'s statement; for the negated check whose statement leaves (a
/// `return`, `throw`, `continue` or `break`), the statements after the `if`
/// in its block - an `out var` in an `if` condition is in scope there, and
/// definitely assigned; the conditional's true arm. Guards: the dictionary
/// has `TryGetValue(key, out value)`; the dictionary a local, parameter,
/// field or auto-property, the key one of those but a property (its getter
/// would run once where it ran twice) or a constant; `d[k]` matched by what
/// the names mean, not their spelling (a `k` a `foreach` or a lambda
/// declares is another variable); every one a plain read (not assigned,
/// incremented or passed by `ref`), outside any lambda or local function
/// (which runs after the dictionary changed), at least one; the region
/// writes neither the dictionary (an indexer store, `Add`, `Remove`,
/// `Clear`, `TryAdd`, the dictionary handed on), the key nor a name on the
/// way to them (`o = o2` under `o.Map`); and unless the dictionary is a
/// local the member made and never hands on or captures, and the key a
/// constant or an uncaptured local or parameter, nothing between the check
/// and a read runs code that could reach them - a call, a construction, an
/// `await`, a `foreach`, a store into a field, property or indexer (a call
/// around a read runs after it, unless a loop brings the read round); an
/// `else` reading `d[k]` keeps the code (that read throws or is a bug); not
/// in an expression tree or a query clause, where `out var` is not allowed;
/// the name `value` (`value2`…) unused in the member and numbered by the
/// check's place in it, so two fixed in one pass never collide. The F# twin is
/// FR0014. Yields to CA1854.
module CSharp.Refactor.DictTryGet

open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax
open Microsoft.CodeAnalysis.Text

[<Literal>]
let Code = "CR0182"

/// `d.ContainsKey(k)` - the receiver and the key.
let private containsKey (e: ExpressionSyntax) =
    match e with
    | :? InvocationExpressionSyntax as inv when inv.ArgumentList.Arguments.Count = 1 ->
        match inv.Expression with
        | :? MemberAccessExpressionSyntax as ma when ma.Name.Identifier.ValueText = "ContainsKey" ->
            let arg = inv.ArgumentList.Arguments.[0]

            if arg.RefKindKeyword.IsKind SyntaxKind.None && isNull arg.NameColon then
                Some(inv, ma.Expression, arg.Expression)
            else
                None
        | _ -> None
    | _ -> None

/// The symbols an expression names, outermost last: `o.Map` is [o; Map],
/// `this._d` is [_d]. None for anything but names and member accesses.
let private chainOf (model: SemanticModel) (e: ExpressionSyntax) : ISymbol list option =
    let rec go (e: ExpressionSyntax) (acc: ISymbol list) =
        match e with
        | :? IdentifierNameSyntax as id ->
            match model.GetSymbolInfo(id).Symbol with
            | null -> None
            | s -> Some(s :: acc)
        | :? ThisExpressionSyntax -> Some acc
        | :? MemberAccessExpressionSyntax as ma when ma.IsKind SyntaxKind.SimpleMemberAccessExpression ->
            match model.GetSymbolInfo(ma.Name).Symbol with
            | null -> None
            | s -> go ma.Expression (s :: acc)
        | _ -> None

    go e []

let private sameChain (a: ISymbol list) (b: ISymbol list) =
    a.Length = b.Length
    && List.forall2 (fun (x: ISymbol) (y: ISymbol) -> SymbolEqualityComparer.Default.Equals(x, y)) a b

/// A property whose read runs no code of its own: an auto-property in source.
let private autoProperty (p: IPropertySymbol) =
    not p.IsIndexer
    // an abstract, virtual or overriding one may read through a derived getter
    && not p.IsAbstract
    && not p.IsVirtual
    && not p.IsOverride
    && p.ContainingType.TypeKind <> TypeKind.Interface
    && not p.DeclaringSyntaxReferences.IsEmpty
    && p.DeclaringSyntaxReferences
       |> Seq.forall (fun r ->
           match r.GetSyntax() with
           | :? PropertyDeclarationSyntax as pd ->
               isNull pd.ExpressionBody
               && not (isNull pd.AccessorList)
               && pd.AccessorList.Accessors
                  |> Seq.forall (fun a -> isNull a.Body && isNull a.ExpressionBody)
           | _ -> false)

/// Every link a plain storage read: a local, a parameter, a field, an
/// auto-property, or the type a static one hangs on.
let private storageChain (chain: ISymbol list) =
    not chain.IsEmpty
    && chain
       |> List.forall (fun s ->
           match s with
           | :? ILocalSymbol
           | :? IParameterSymbol
           | :? IFieldSymbol
           | :? INamedTypeSymbol -> true
           | :? IPropertySymbol as p -> autoProperty p
           | _ -> false)

/// Is the node inside a lambda or a local function below `within`: code
/// that runs when called, not where it stands?
let private insideDeferred (node: SyntaxNode) (within: SyntaxNode) =
    node.Ancestors()
    |> Seq.takeWhile (fun a -> a <> within)
    |> Seq.exists (fun a -> a :? AnonymousFunctionExpressionSyntax || a :? LocalFunctionStatementSyntax)

/// Is the access a plain read: not a store, an increment or a `ref`?
let private plainRead (ea: ElementAccessExpressionSyntax) =
    match ea.Parent with
    | :? AssignmentExpressionSyntax as a when a.Left = (ea :> ExpressionSyntax) -> false
    | :? PostfixUnaryExpressionSyntax -> false
    | :? PrefixUnaryExpressionSyntax as u when
        u.IsKind SyntaxKind.PreIncrementExpression
        || u.IsKind SyntaxKind.PreDecrementExpression
        ->
        false
    | :? ArgumentSyntax as arg -> arg.RefKindKeyword.IsKind SyntaxKind.None
    | :? RefExpressionSyntax -> false
    | _ -> true

let private mutators =
    set
        [
            "Add"
            "Remove"
            "Clear"
            "TryAdd"
            "TryRemove"
            "AddOrUpdate"
            "GetOrAdd"
            "TryUpdate"
            "EnsureCapacity"
            "TrimExcess"
        ]

/// A local nothing else can reach: created in the member (`new`, a
/// collection expression), never captured by a lambda or a local function,
/// never handed on - only the receiver of its own members and indexer.
let private privateLocal (model: SemanticModel) (scope: SyntaxNode) (s: ISymbol) =
    match s with
    | :? ILocalSymbol as local ->
        let created =
            local.DeclaringSyntaxReferences
            |> Seq.exists (fun r ->
                match r.GetSyntax() with
                | :? VariableDeclaratorSyntax as v when not (isNull v.Initializer) ->
                    v.Initializer.Value :? BaseObjectCreationExpressionSyntax
                    || v.Initializer.Value :? CollectionExpressionSyntax
                | _ -> false)

        let mentions =
            scope.DescendantNodes()
            |> Seq.choose (fun n ->
                match n with
                | :? IdentifierNameSyntax as id when id.Identifier.ValueText = local.Name ->
                    match model.GetSymbolInfo(id).Symbol with
                    | :? ILocalSymbol as l when SymbolEqualityComparer.Default.Equals(l, local) -> Some id
                    | _ -> None
                | _ -> None)
            |> List.ofSeq

        created
        && mentions
           |> List.forall (fun id ->
               not (insideDeferred id scope)
               && (match id.Parent with
                   | :? MemberAccessExpressionSyntax as ma -> ma.Expression = (id :> ExpressionSyntax)
                   | :? ElementAccessExpressionSyntax as ea -> ea.Expression = (id :> ExpressionSyntax)
                   | _ -> false))
    | _ -> false

/// A key only an assignment in sight changes: a constant, or a local or a
/// parameter no lambda or local function captures.
let private privateKey (model: SemanticModel) (scope: SyntaxNode) (key: ExpressionSyntax) (chain: ISymbol list option) =
    model.GetConstantValue(key).HasValue
    || (match chain with
        | Some [ s ] when (s :? ILocalSymbol || s :? IParameterSymbol) ->
            scope.DescendantNodes()
            |> Seq.forall (fun n ->
                match n with
                | :? IdentifierNameSyntax as id when id.Identifier.ValueText = s.Name ->
                    not (insideDeferred id scope)
                    || not (SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(id).Symbol, s))
                | _ -> true)
        | _ -> false)

/// Does the region write the dictionary, the key or any link of either -
/// a store, `++`, `ref`/`out`, a mutating member, the dictionary handed on?
let private writesIn (model: SemanticModel) (links: ISymbol list) (receiver: ISymbol list) (region: SyntaxNode list) =
    let isLink (e: ExpressionSyntax) =
        match chainOf model e with
        | Some c ->
            c
            |> List.exists (fun s -> links |> List.exists (fun l -> SymbolEqualityComparer.Default.Equals(s, l)))
        | None -> false

    let isReceiver (e: ExpressionSyntax) =
        match chainOf model e with
        | Some c -> sameChain c receiver
        | None -> false

    region
    |> List.exists (fun r ->
        r.DescendantNodesAndSelf()
        |> Seq.exists (fun n ->
            match n with
            | :? AssignmentExpressionSyntax as a ->
                match a.Left with
                | :? ElementAccessExpressionSyntax as ea -> isReceiver ea.Expression
                | left -> isLink left
            | :? PostfixUnaryExpressionSyntax as u -> isLink u.Operand
            | :? PrefixUnaryExpressionSyntax as u when
                u.IsKind SyntaxKind.PreIncrementExpression
                || u.IsKind SyntaxKind.PreDecrementExpression
                ->
                isLink u.Operand
            | :? ArgumentSyntax as arg ->
                isReceiver arg.Expression
                || (not (arg.RefKindKeyword.IsKind SyntaxKind.None) && isLink arg.Expression)
            | :? InvocationExpressionSyntax as inv ->
                match inv.Expression with
                | :? MemberAccessExpressionSyntax as ma when isReceiver ma.Expression ->
                    mutators.Contains ma.Name.Identifier.ValueText
                | _ -> false
            | _ -> false))

/// For shared state - a dictionary or a key other code can reach - does
/// anything in the region run code that could change it before a read: a
/// call, a construction, an `await`, a `foreach` (its enumerator), a store
/// into a field, property or indexer, an increment of one? A call around a
/// read runs after it, unless a loop brings the read round again.
let private runsCodeBeforeReads
    (model: SemanticModel)
    (region: SyntaxNode list)
    (reads: ElementAccessExpressionSyntax list)
    =
    let isLoop (n: SyntaxNode) =
        n :? ForStatementSyntax
        || n :? ForEachStatementSyntax
        || n :? WhileStatementSyntax
        || n :? DoStatementSyntax

    let inLoop =
        reads
        |> List.exists (fun r ->
            r.Ancestors()
            |> Seq.takeWhile (fun a -> not (region |> List.exists (fun g -> g = a)))
            |> Seq.exists isLoop)

    let around (n: SyntaxNode) =
        not inLoop && reads |> List.exists (fun r -> n.Span.Contains r.Span)

    let storesOutside (e: ExpressionSyntax) =
        match model.GetSymbolInfo(e).Symbol with
        | :? ILocalSymbol -> false
        | _ -> true

    region
    |> List.exists (fun r ->
        r.DescendantNodesAndSelf(fun n ->
            not (n :? AnonymousFunctionExpressionSyntax || n :? LocalFunctionStatementSyntax))
        |> Seq.exists (fun n ->
            match n with
            | :? InvocationExpressionSyntax
            | :? BaseObjectCreationExpressionSyntax -> not (around n)
            | :? AwaitExpressionSyntax
            | :? ForEachStatementSyntax -> true
            | :? AssignmentExpressionSyntax as a -> storesOutside a.Left
            | :? PostfixUnaryExpressionSyntax as u -> storesOutside u.Operand
            | :? PrefixUnaryExpressionSyntax as u when
                u.IsKind SyntaxKind.PreIncrementExpression
                || u.IsKind SyntaxKind.PreDecrementExpression
                ->
                storesOutside u.Operand
            | _ -> false))

let private insideQuery (node: SyntaxNode) =
    node.Ancestors() |> Seq.exists (fun a -> a :? QueryExpressionSyntax)

/// A statement that leaves: `return`, `throw`, `continue`, `break`, or a block ending in one.
[<TailCall>]
let rec private leaves (s: StatementSyntax) =
    match s with
    | :? ReturnStatementSyntax
    | :? ThrowStatementSyntax
    | :? ContinueStatementSyntax
    | :? BreakStatementSyntax -> true
    | :? BlockSyntax as b when b.Statements.Count > 0 -> leaves b.Statements.[b.Statements.Count - 1]
    | _ -> false

let analyze (tree: SyntaxTree) (model: SemanticModel) (ctx: RuleContext) : Suggestion list =
    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun node ->
        // the check, the region reading the value, and whether it is negated
        let site =
            match node with
            | :? IfStatementSyntax as ifs ->
                // `d.ContainsKey(k)` alone, or first in an `&&` chain
                let rec head (c: ExpressionSyntax) (rest: SyntaxNode list) =
                    match c with
                    | :? BinaryExpressionSyntax as b when b.IsKind SyntaxKind.LogicalAndExpression ->
                        head b.Left ((b.Right :> SyntaxNode) :: rest)
                    | :? ParenthesizedExpressionSyntax as p -> head p.Expression rest
                    | other -> other, rest

                match head ifs.Condition [] with
                | c, rest when (containsKey c).IsSome ->
                    let elseReads = if isNull ifs.Else then [] else [ ifs.Else :> SyntaxNode ]
                    Some(c, (ifs.Statement :> SyntaxNode) :: rest, elseReads)
                | :? PrefixUnaryExpressionSyntax as u, [] when
                    u.IsKind SyntaxKind.LogicalNotExpression && (containsKey u.Operand).IsSome
                    ->
                    match ifs.Parent with
                    | :? BlockSyntax as b when isNull ifs.Else && leaves ifs.Statement ->
                        let i = b.Statements.IndexOf ifs

                        let after =
                            b.Statements
                            |> Seq.skip (i + 1)
                            |> Seq.map (fun s -> s :> SyntaxNode)
                            |> List.ofSeq

                        Some(u.Operand, after, [ ifs.Statement :> SyntaxNode ])
                    | _ when not (isNull ifs.Else) ->
                        Some(u.Operand, [ ifs.Else :> SyntaxNode ], [ ifs.Statement :> SyntaxNode ])
                    | _ -> None
                | _ -> None
            | :? ConditionalExpressionSyntax as ce when (containsKey ce.Condition).IsSome ->
                Some(ce.Condition, [ ce.WhenTrue :> SyntaxNode ], [ ce.WhenFalse :> SyntaxNode ])
            | _ -> None

        match site with
        | Some(check, region, elsewhere) ->
            let inv, receiver, key = (containsKey check).Value

            let dictionaryType = model.GetTypeInfo(receiver).Type

            // the BCL's own dictionary, where `d[k]` and `TryGetValue` are one
            // contract: a derived `new V this[K k]` counting reads, or a positional
            // `this[int i]` beside a long key, would bind the indexer to other code
            let bclDictionary =
                match dictionaryType with
                | :? INamedTypeSymbol as n ->
                    n.OriginalDefinition.DeclaringSyntaxReferences.IsEmpty
                    && (let ns = n.ContainingNamespace.ToDisplayString()

                        ns = "System.Collections.Generic"
                        || ns = "System.Collections.Concurrent"
                        || ns = "System.Collections.Immutable"
                        || ns = "System.Collections.Frozen"
                        || ns = "System.Collections.ObjectModel")
                | _ -> false

            let hasTryGetValue =
                bclDictionary
                && (dictionaryType
                    :: (dictionaryType.AllInterfaces
                        |> Seq.map (fun i -> i :> ITypeSymbol)
                        |> List.ofSeq)
                    |> List.exists (fun t ->
                        t.GetMembers "TryGetValue"
                        |> Seq.exists (fun m ->
                            match m with
                            | :? IMethodSymbol as md ->
                                md.Parameters.Length = 2 && md.Parameters.[1].RefKind = RefKind.Out
                            | _ -> false)))

            let scope = Text.enclosingMember inv
            let receiverChain = chainOf model receiver
            let keyConstant = model.GetConstantValue key
            let keyChain = if keyConstant.HasValue then Some [] else chainOf model key

            // `d[k]` by what the names mean, not how they are spelled: a `k` a
            // foreach or a lambda declares is another variable
            let lookups (nodes: SyntaxNode list) =
                match receiverChain, keyChain with
                | Some rc, Some kc ->
                    nodes
                    |> List.collect (fun r ->
                        r.DescendantNodesAndSelf()
                        |> Seq.choose (fun n ->
                            match n with
                            | :? ElementAccessExpressionSyntax as ea when ea.ArgumentList.Arguments.Count = 1 ->
                                let arg = ea.ArgumentList.Arguments.[0].Expression

                                let sameKey =
                                    if keyConstant.HasValue then
                                        let v = model.GetConstantValue arg
                                        v.HasValue && obj.Equals(v.Value, keyConstant.Value)
                                    else
                                        match chainOf model arg with
                                        | Some c -> sameChain c kc
                                        | None -> false

                                match chainOf model ea.Expression with
                                | Some c when sameKey && sameChain c rc -> Some ea
                                | _ -> None
                            | _ -> None)
                        |> List.ofSeq)
                | _ -> []

            let reads = lookups region

            // a key's getter runs code: a property key is not a stable read
            let keyStable =
                keyConstant.HasValue
                || (match keyChain with
                    | Some c -> storageChain c && not (c |> List.exists (fun s -> s :? IPropertySymbol))
                    | None -> false)

            let receiverStable =
                match receiverChain with
                | Some c -> storageChain c
                | None -> false

            // nothing else can reach either: calls between the check and a read cannot change them
            let unreachable =
                (match receiverChain with
                 | Some [ s ] -> privateLocal model scope s
                 | _ -> false)
                && privateKey model scope key keyChain

            let links =
                (receiverChain |> Option.defaultValue []) @ (keyChain |> Option.defaultValue [])

            if
                not hasTryGetValue
                || not receiverStable
                || not keyStable
                || reads.IsEmpty
                || not (reads |> List.forall plainRead)
                // a read in a lambda or a local function runs later, the dictionary changed by then
                || reads
                   |> List.exists (fun r ->
                       region |> List.exists (fun g -> insideDeferred r g && g.Span.Contains r.Span))
                || not (lookups elsewhere).IsEmpty
                || writesIn model links receiverChain.Value region
                || (not unreachable && runsCodeBeforeReads model region reads)
                || Text.insideExpressionTree model inv
                || insideQuery inv
            then
                None
            else

                // a setter's own `value` is taken: `found` there
                let inAccessor =
                    inv.Ancestors()
                    |> Seq.exists (fun a ->
                        match a with
                        | :? AccessorDeclarationSyntax as acc -> not (acc.IsKind SyntaxKind.GetAccessorDeclaration)
                        | _ -> false)

                let stem = if inAccessor then "found" else "value"

                // every check in the member starts from its own number, so two fixed in
                // one pass never declare the same name (`value`, `value2`, …)
                let preceding =
                    scope.DescendantNodes()
                    |> Seq.filter (fun n ->
                        match n with
                        | :? InvocationExpressionSyntax as i -> i.SpanStart < inv.SpanStart && (containsKey i).IsSome
                        | _ -> false)
                    |> Seq.length

                let name =
                    Seq.initInfinite (fun i -> if i = 0 then stem else $"{stem}{i + 1}")
                    |> Seq.skip preceding
                    |> Seq.find (fun n -> not (Text.mentionsName n scope))

                let edits =
                    Suggestion.replace inv.Span $"{receiver}.TryGetValue({key}, out var {name})"
                    :: (reads |> List.map (fun ea -> Suggestion.replace ea.Span name))

                if Guards.speculativeCheck model edits then
                    Some
                        {
                            Code = Code
                            Message =
                                "ContainsKey and then the indexer look the key up twice: TryGetValue looks once and hands the value over"
                            Span = inv.Span
                            Fixes = [ Suggestion.fix "Use TryGetValue" Code edits ]
                        }
                else
                    None
        | None -> None)
    |> List.ofSeq
