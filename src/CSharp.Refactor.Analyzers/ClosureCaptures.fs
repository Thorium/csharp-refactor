/// Captures and copies: the defects F# has no room for.
///
/// CR0160 (correctness, fix): a closure created inside a loop reads a
/// local the loop writes — the `for` variable, the `while ((line =
/// Read()) != null)` binder, a counter bumped in the body — and outlives
/// the iteration: stored in a collection, a field or an outer local,
/// returned, handed to `Task.Run`/`StartNew`/`QueueUserWorkItem`/a
/// `Thread`/a `Timer`/an event, or kept alive by a lazy LINQ chain that
/// itself escapes. Every closure then reads the cell's final value. The
/// fix is the per-iteration copy the author meant: `var i1 = i;` before
/// the statement, the closure reading `i1`. Guards: the closure only reads
/// the local; the local is declared outside the loop body (a body local
/// is fresh per iteration); the closure sits in a statement of the loop
/// body (one in the condition or incrementor is a note); a closure handed
/// to an unknown callee is a note (the callee may run it at once); an
/// awaited call consumed it; a `foreach` variable is fresh since C# 5.
/// Outside a loop, a local written after an escaped closure's creation is
/// a note only — late binding is sometimes the point.
///
/// CR0161 (correctness, note): a mutating method called on a struct the
/// compiler copied first — a `readonly` field, a property getter, a
/// `List<T>` indexer, a `foreach` variable, an `in` parameter. The call
/// runs on the copy and the original never changes. The method is proven
/// to mutate from its source (a write to a field or auto-property of
/// `this`, a call to another mutator, `this` by `ref`), or is one of the
/// framework's known mutators (`MoveNext`, `Reset`, `Enter`, `TryEnter`,
/// `Exit`, `Dispose`) on a struct that is not `readonly`.
///
/// CR0162 (correctness, note): `new System.Threading.Timer(…)` dropped, or
/// bound to a local that never leaves the method: nothing references the
/// timer once the method returns, the collector takes it, the callbacks
/// stop. `System.Timers.Timer` is not this: once started it is rooted
/// through the timer queue by its own Elapsed callback (measured: it kept
/// firing through five forced collections), so it never fires here.
module CSharp.Refactor.ClosureCaptures

open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax
open Microsoft.CodeAnalysis.Text

[<Literal>]
let LoopCaptureCode = "CR0160"

[<Literal>]
let StructCopyCode = "CR0161"

[<Literal>]
let DroppedTimerCode = "CR0162"

let private isClosure (n: SyntaxNode) =
    n :? AnonymousFunctionExpressionSyntax || n :? LocalFunctionStatementSyntax

/// The nodes of a function body, not descending into nested closures.
let private ownNodes (root: SyntaxNode) =
    root.DescendantNodes(fun n -> obj.ReferenceEquals(n, root) || not (isClosure n))

let private symbolOf (model: SemanticModel) (e: SyntaxNode) =
    let info = model.GetSymbolInfo e

    if isNull info.Symbol then
        match info.CandidateSymbols |> Seq.tryHead with
        | Some s -> s
        | None -> null
    else
        info.Symbol

let private sameSymbol (a: ISymbol) (b: ISymbol) =
    SymbolEqualityComparer.Default.Equals(a, b)

// ---- CR0160 ----

/// Where a closure ends up once created.
type private Fate =
    /// consumed before the next iteration
    | Stays
    /// kept beyond the iteration through a known channel
    | Escapes
    /// handed to a callee the rule cannot read
    | Unknown

let private deferredCallees =
    set
        [
            "System.Threading.Tasks.Task.Run"
            "System.Threading.Tasks.TaskFactory.StartNew"
            "System.Threading.ThreadPool.QueueUserWorkItem"
            "System.Threading.ThreadPool.UnsafeQueueUserWorkItem"
            "System.Threading.Tasks.Task.ContinueWith"
            "System.Threading.CancellationToken.Register"
        ]

let private deferredConstructors =
    set
        [
            "System.Threading.Thread"
            "System.Threading.Timer"
            "System.Timers.Timer"
            "System.Threading.Tasks.Task"
            "System.Lazy"
        ]

let private insertNames =
    set
        [
            "Add"
            "TryAdd"
            "Enqueue"
            "Push"
            "Insert"
            "AddOrUpdate"
            "AddRange"
            "AddLast"
            "AddFirst"
        ]

let private lazyReturn (m: IMethodSymbol) =
    let n = m.ReturnType.OriginalDefinition.ToDisplayString()

    n = "System.Collections.Generic.IEnumerable<T>"
    || n = "System.Linq.IQueryable<T>"
    || n = "System.Linq.IOrderedEnumerable<TElement>"
    || n = "System.Linq.IOrderedQueryable<T>"
    || n = "System.Linq.IGrouping<TKey, TElement>"
    || n = "System.Linq.ILookup<TKey, TElement>"

let private isCollectionOwner (t: ITypeSymbol) =
    if isNull t then
        false
    else
        let ns = t.ContainingNamespace

        not (isNull ns) && ns.ToDisplayString().StartsWith "System.Collections"

/// Is the declaring node of a local outside a loop's body (so the local
/// lives across iterations)?
let private declaredOutside (loopBody: SyntaxNode) (local: ISymbol) =
    match local.DeclaringSyntaxReferences |> Seq.tryHead with
    | Some r -> not (loopBody.Span.Contains r.Span)
    | None -> false

/// Is the local written anywhere in the node, outside nested closures?
let private writtenIn (model: SemanticModel) (local: ISymbol) (node: SyntaxNode) =
    ownNodes node
    |> Seq.exists (fun n ->
        let target =
            match n with
            | :? AssignmentExpressionSyntax as a -> Some a.Left
            | :? PostfixUnaryExpressionSyntax as u -> Some u.Operand
            | :? PrefixUnaryExpressionSyntax as u when
                u.IsKind SyntaxKind.PreIncrementExpression
                || u.IsKind SyntaxKind.PreDecrementExpression
                ->
                Some u.Operand
            | :? ArgumentSyntax as a when not (a.RefKindKeyword.IsKind SyntaxKind.None) -> Some a.Expression
            | _ -> None

        match target with
        | Some(:? IdentifierNameSyntax as id) -> sameSymbol (symbolOf model id) local
        | _ -> false)
    || (match local.DeclaringSyntaxReferences |> Seq.tryHead with
        // a `for` declaration with an initialiser is a write of the loop's own
        | Some r ->
            match r.GetSyntax() with
            | :? VariableDeclaratorSyntax as d when
                not (isNull d.Initializer)
                && node.Span.Contains d.Span
                && (d.Parent.Parent :? ForStatementSyntax)
                ->
                true
            | _ -> false
        | None -> false)

/// The body of a closure, for reads.
let private closureBody (c: SyntaxNode) : SyntaxNode =
    match c with
    | :? AnonymousFunctionExpressionSyntax as l -> l.Body
    | :? LocalFunctionStatementSyntax as f ->
        (if isNull f.Body then
             f.ExpressionBody :> SyntaxNode
         else
             f.Body)
    | _ -> c

/// The identifiers inside a closure that read a given local, and whether
/// the closure writes it.
let private readsOf (model: SemanticModel) (closure: SyntaxNode) (local: ISymbol) =
    let body = closureBody closure

    let ids =
        body.DescendantNodesAndSelf()
        |> Seq.choose (fun n ->
            match n with
            | :? IdentifierNameSyntax as id when
                id.Identifier.ValueText = local.Name && sameSymbol (symbolOf model id) local
                ->
                Some id
            | _ -> None)
        |> List.ofSeq

    let writes =
        body.DescendantNodesAndSelf()
        |> Seq.exists (fun n ->
            let target =
                match n with
                | :? AssignmentExpressionSyntax as a -> Some a.Left
                | :? PostfixUnaryExpressionSyntax as u -> Some u.Operand
                | :? PrefixUnaryExpressionSyntax as u when
                    u.IsKind SyntaxKind.PreIncrementExpression
                    || u.IsKind SyntaxKind.PreDecrementExpression
                    ->
                    Some u.Operand
                | :? ArgumentSyntax as a when not (a.RefKindKeyword.IsKind SyntaxKind.None) -> Some a.Expression
                | _ -> None

            match target with
            | Some(:? IdentifierNameSyntax as id) -> sameSymbol (symbolOf model id) local
            | _ -> false)

    ids, writes

/// The captured locals of a closure: locals or parameters of the
/// enclosing method declared outside the closure and read inside it.
let private capturedLocals (model: SemanticModel) (closure: SyntaxNode) : ISymbol list =
    closureBody closure
    |> fun body -> body.DescendantNodesAndSelf()
    |> Seq.choose (fun n ->
        match n with
        | :? IdentifierNameSyntax as id ->
            match symbolOf model id with
            | :? ILocalSymbol as l when
                (l.DeclaringSyntaxReferences
                 |> Seq.forall (fun r -> not (closure.Span.Contains r.Span)))
                ->
                Some(l :> ISymbol)
            | _ -> None
        | _ -> None)
    |> Seq.distinctBy (fun s -> s.Name)
    |> List.ofSeq

/// Where an expression's value goes: climbed through the wrappers that
/// carry it unchanged, resolved at the first channel that keeps or
/// consumes it. `hop` is the one-level follow through a body local.
let rec private fateOf (model: SemanticModel) (loopBody: SyntaxNode option) (hop: bool) (e: SyntaxNode) : Fate =
    match e.Parent with
    | null -> Unknown
    | :? ParenthesizedExpressionSyntax
    | :? CastExpressionSyntax
    | :? ConditionalExpressionSyntax
    | :? InitializerExpressionSyntax
    | :? CollectionExpressionSyntax
    | :? ExpressionElementSyntax
    | :? ArrayCreationExpressionSyntax
    | :? ImplicitArrayCreationExpressionSyntax
    | :? AnonymousObjectMemberDeclaratorSyntax
    | :? AnonymousObjectCreationExpressionSyntax
    | :? TupleExpressionSyntax as p -> fateOf model loopBody hop p
    | :? ConditionalAccessExpressionSyntax as p -> fateOf model loopBody hop p
    | :? ArgumentSyntax as arg ->
        match arg.Parent with
        | :? TupleExpressionSyntax as t -> fateOf model loopBody hop t
        | :? ArgumentListSyntax as al ->
            match al.Parent with
            | :? InvocationExpressionSyntax as inv ->
                match symbolOf model inv with
                | :? IMethodSymbol as m ->
                    let full = m.ContainingType.OriginalDefinition.ToDisplayString() + "." + m.Name

                    if deferredCallees.Contains full then
                        Escapes
                    elif insertNames.Contains m.Name && isCollectionOwner m.ContainingType then
                        // the collection itself may be the iteration's own
                        match inv.Expression with
                        | :? MemberAccessExpressionSyntax as ma ->
                            match symbolOf model ma.Expression, loopBody with
                            | (:? ILocalSymbol as l), Some body when not (declaredOutside body l) -> Stays
                            | _ -> Escapes
                        | _ -> Escapes
                    elif lazyReturn m then
                        fateOf model loopBody hop inv
                    elif inv.Parent :? AwaitExpressionSyntax then
                        Stays
                    elif m.ReturnType.OriginalDefinition.ToDisplayString().StartsWith "System.Threading.Tasks." then
                        // a task-returning callee holds the closure until it runs
                        Escapes
                    elif
                        m.ContainingNamespace.ToDisplayString() = "System.Linq"
                        || m.ContainingNamespace.ToDisplayString() = "System"
                        || (m.ContainingNamespace.ToDisplayString().StartsWith "System.Collections")
                    then
                        Stays
                    else
                        Unknown
                | _ -> Unknown
            | :? ObjectCreationExpressionSyntax as c ->
                match model.GetTypeInfo(c).Type with
                | null -> Unknown
                | t when deferredConstructors.Contains(t.OriginalDefinition.ToDisplayString()) -> Escapes
                | t when t.TypeKind = TypeKind.Delegate -> fateOf model loopBody hop c
                | _ -> Unknown
            | :? ImplicitObjectCreationExpressionSyntax as c ->
                match model.GetTypeInfo(c).Type with
                | null -> Unknown
                | t when deferredConstructors.Contains(t.OriginalDefinition.ToDisplayString()) -> Escapes
                | t when t.TypeKind = TypeKind.Delegate -> fateOf model loopBody hop c
                | _ -> Unknown
            | _ -> Unknown
        | _ -> Unknown
    | :? AssignmentExpressionSyntax as a when obj.ReferenceEquals(a.Right, e) ->
        if a.IsKind SyntaxKind.AddAssignmentExpression then
            Escapes
        else
            match symbolOf model a.Left with
            | :? ILocalSymbol as l ->
                match loopBody with
                | Some body when not (declaredOutside body l) -> if hop then followLocal model loopBody l else Stays
                | Some _ -> Escapes
                | None -> Stays
            | :? IFieldSymbol
            | :? IPropertySymbol -> Escapes
            | _ -> Unknown
    | :? EqualsValueClauseSyntax as ev ->
        match ev.Parent with
        | :? VariableDeclaratorSyntax as d ->
            match d.Parent.Parent with
            | :? LocalDeclarationStatementSyntax ->
                match model.GetDeclaredSymbol d with
                | :? ILocalSymbol as l ->
                    match loopBody with
                    | Some body when not (declaredOutside body l) -> if hop then followLocal model loopBody l else Stays
                    | Some _ -> Escapes
                    | None -> Stays
                | _ -> Unknown
            | :? FieldDeclarationSyntax -> Escapes
            | _ -> Unknown
        | :? PropertyDeclarationSyntax -> Escapes
        | _ -> Unknown
    | :? ReturnStatementSyntax
    | :? YieldStatementSyntax -> Escapes
    | :? ArrowExpressionClauseSyntax -> Escapes
    | :? ExpressionStatementSyntax -> Stays
    | :? InvocationExpressionSyntax as inv when obj.ReferenceEquals(inv.Expression, e) -> Stays
    | :? MemberAccessExpressionSyntax as ma when obj.ReferenceEquals(ma.Expression, e) ->
        // `xs.Where(…).ToList()`: the chain's callee decides
        match ma.Parent with
        | :? InvocationExpressionSyntax as inv ->
            match symbolOf model inv with
            | :? IMethodSymbol as m when lazyReturn m -> fateOf model loopBody hop inv
            | :? IMethodSymbol -> Stays
            | _ -> Unknown
        | _ -> Unknown
    | :? AwaitExpressionSyntax -> Stays
    | :? LambdaExpressionSyntax
    | :? AnonymousMethodExpressionSyntax -> Unknown
    | _ -> Unknown

/// One hop: a body local holding the closure — where do its reads go?
and private followLocal (model: SemanticModel) (loopBody: SyntaxNode option) (l: ILocalSymbol) : Fate =
    match loopBody with
    | None -> Stays
    | Some body ->
        let fates =
            ownNodes body
            |> Seq.choose (fun n ->
                match n with
                | :? IdentifierNameSyntax as id when
                    id.Identifier.ValueText = l.Name && sameSymbol (symbolOf model id) l
                    ->
                    Some(fateOf model loopBody false id)
                | _ -> None)
            |> List.ofSeq

        if fates |> List.contains Escapes then Escapes
        elif fates |> List.contains Unknown then Unknown
        else Stays

/// The loops around a closure, innermost first, each with its body: a
/// local the OUTER loop changes is as captured as the inner one's.
let private loopsOf (closure: SyntaxNode) : (StatementSyntax * SyntaxNode) list =
    closure.Ancestors()
    |> Seq.takeWhile (fun a -> not (a :? MemberDeclarationSyntax || isClosure a))
    |> Seq.choose (fun a ->
        match a with
        | :? ForStatementSyntax as f -> Some(f :> StatementSyntax, f.Statement :> SyntaxNode)
        | :? WhileStatementSyntax as w -> Some(w :> StatementSyntax, w.Statement :> SyntaxNode)
        | :? DoStatementSyntax as d -> Some(d :> StatementSyntax, d.Statement :> SyntaxNode)
        | :? ForEachStatementSyntax as f -> Some(f :> StatementSyntax, f.Statement :> SyntaxNode)
        | _ -> None)
    |> List.ofSeq

/// The innermost block-level statement of the loop body holding the
/// closure: where the copy goes.
let private bodyStatement (loopBody: SyntaxNode) (closure: SyntaxNode) : StatementSyntax option =
    match loopBody with
    | :? BlockSyntax as b ->
        closure.AncestorsAndSelf()
        |> Seq.takeWhile (fun a -> not (obj.ReferenceEquals(a, b)))
        |> Seq.tryPick (fun a ->
            match a with
            | :? StatementSyntax as s when (s.Parent :? BlockSyntax) -> Some s
            | _ -> None)
    | _ -> None

/// A name free in the scope and not handed out already in this pass.
let private freshName (used: System.Collections.Generic.HashSet<int * string>) (scope: SyntaxNode) (name: string) =
    let fresh =
        Seq.initInfinite (fun i -> name + string (i + 1))
        |> Seq.find (fun candidate ->
            not (used.Contains((scope.SpanStart, candidate)))
            && not (Text.mentionsName candidate scope))

    used.Add((scope.SpanStart, fresh)) |> ignore
    fresh

let private loopCaptures (tree: SyntaxTree) (model: SemanticModel) : Suggestion list =
    let text = tree.GetText()
    let root = tree.GetRoot()

    let closures = root.DescendantNodes() |> Seq.filter isClosure |> List.ofSeq

    // per closure: the captured locals the loop writes, with the fate
    let found =
        closures
        |> List.collect (fun closure ->
            if Guards.insideExpressionTree model closure then
                []
            else
                match loopsOf closure with
                | [] ->
                    // straight-line: written after the closure, escaped
                    let fn =
                        closure.Ancestors()
                        |> Seq.tryFind (fun a -> a :? MemberDeclarationSyntax || isClosure a)

                    match fn with
                    | None -> []
                    | Some fn ->
                        capturedLocals model closure
                        |> List.choose (fun local ->
                            let reads, writes = readsOf model closure local

                            if reads.IsEmpty || writes then
                                None
                            else
                                let writtenAfter =
                                    ownNodes (closureBody fn |> fun b -> if isNull b then fn else b)
                                    |> Seq.exists (fun n ->
                                        n.SpanStart > closure.Span.End
                                        && (match n with
                                            | :? AssignmentExpressionSyntax as a ->
                                                (match a.Left with
                                                 | :? IdentifierNameSyntax as id ->
                                                     sameSymbol (symbolOf model id) local
                                                 | _ -> false)
                                            | :? PostfixUnaryExpressionSyntax as u ->
                                                (match u.Operand with
                                                 | :? IdentifierNameSyntax as id ->
                                                     sameSymbol (symbolOf model id) local
                                                 | _ -> false)
                                            | _ -> false))

                                if writtenAfter && fateOf model None true closure = Escapes then
                                    Some(
                                        Choice2Of2(
                                            Suggestion.note
                                                LoopCaptureCode
                                                $"The closure reads '{local.Name}', which is assigned again after the closure is created: it will see the later value when it runs — copy the value into a new local before the closure if the value at creation was meant"
                                                closure.Span
                                        )
                                    )
                                else
                                    None)
                | loops ->
                    let _, innerBody = loops.Head

                    capturedLocals model closure
                    |> List.choose (fun local ->
                        // the loop that changes the local, and outside whose body it lives
                        let writing =
                            loops
                            |> List.tryFind (fun (l, b) -> declaredOutside b local && writtenIn model local l)

                        match writing with
                        | None -> None
                        | Some(loop, body) ->
                            let reads, writes = readsOf model closure local

                            if reads.IsEmpty || writes then
                                None
                            else
                                match fateOf model (Some body) true closure with
                                | Stays -> None
                                | Unknown ->
                                    Some(
                                        Choice2Of2(
                                            Suggestion.note
                                                LoopCaptureCode
                                                $"The closure reads '{local.Name}', which the loop changes: if the callee keeps the closure past this iteration, every copy sees the final value — copy it into a new local before the closure (`var {local.Name}1 = {local.Name};`)"
                                                closure.Span
                                        )
                                    )
                                | Escapes ->
                                    // another closure of the loop writing the local wants the
                                    // shared cell: a copy would cut this reader off from it
                                    let sharedCell =
                                        loop.DescendantNodes()
                                        |> Seq.filter isClosure
                                        |> Seq.exists (fun c ->
                                            not (obj.ReferenceEquals(c, closure)) && snd (readsOf model c local))

                                    // the copy goes right before the closure, in the innermost loop
                                    match bodyStatement innerBody closure with
                                    | _ when sharedCell -> None
                                    | None ->
                                        Some(
                                            Choice2Of2(
                                                Suggestion.note
                                                    LoopCaptureCode
                                                    $"The closure reads '{local.Name}', which the loop changes, and outlives the iteration: every copy sees the final value — copy it into a new local before the closure"
                                                    closure.Span
                                            )
                                        )
                                    | Some statement when writtenIn model local statement ->
                                        Some(
                                            Choice2Of2(
                                                Suggestion.note
                                                    LoopCaptureCode
                                                    $"The closure reads '{local.Name}', which this statement changes, and outlives the iteration: every copy sees the final value — copy the value into a new local right before the closure"
                                                    closure.Span
                                            )
                                        )
                                    | Some statement -> Some(Choice1Of2(statement, local, closure, reads))))

    let notes =
        found
        |> List.choose (function
            | Choice2Of2 n -> Some n
            | Choice1Of2 _ -> None)

    // one copy per (statement, local), every closure of the statement renamed
    let used = System.Collections.Generic.HashSet<int * string>()

    let fixes =
        found
        |> List.choose (function
            | Choice1Of2 f -> Some f
            | Choice2Of2 _ -> None)
        |> List.groupBy (fun (statement, local, _, _) -> statement.SpanStart, local.Name)
        |> List.map (fun (_, group) ->
            let statement, local, _, _ = group.Head
            let scope = Text.enclosingMember statement
            let copy = freshName used scope local.Name
            let indent = Text.leadingWhitespace text statement.SpanStart
            let nl = Text.newlineAt text statement.SpanStart

            let edits =
                Suggestion.insert statement.SpanStart $"var {copy} = {local.Name};{nl}{indent}"
                :: (group
                    |> List.collect (fun (_, _, _, reads) ->
                        reads |> List.map (fun id -> Suggestion.replace id.Span copy)))

            let closures = group |> List.map (fun (_, _, c, _) -> (c: SyntaxNode))
            let first = closures |> List.minBy (fun c -> c.SpanStart)

            {
                Code = LoopCaptureCode
                Message =
                    $"The closure reads '{local.Name}', which the loop changes, and outlives the iteration: every copy sees the final value — copy it into '{copy}' before the closure"
                Span = first.Span
                Fixes =
                    [
                        Suggestion.fix $"Copy '{local.Name}' into '{copy}' before the closure" LoopCaptureCode edits
                    ]
            }
            |> Guards.checked model)

    notes @ fixes

// ---- CR0161 ----

let private knownMutators =
    set [ "MoveNext"; "Reset"; "Enter"; "TryEnter"; "Exit"; "Dispose" ]

/// Does a source method of a struct write its own state?
let rec private mutatesSelf (model: SemanticModel) (visited: Set<string>) (m: IMethodSymbol) : bool =
    match m.DeclaringSyntaxReferences |> Seq.tryHead with
    | None -> knownMutators.Contains m.Name
    | Some r ->
        match r.GetSyntax() with
        | :? MethodDeclarationSyntax as decl ->
            let body: SyntaxNode =
                if isNull decl.Body then
                    decl.ExpressionBody :> SyntaxNode
                else
                    decl.Body :> SyntaxNode

            if isNull body then
                false
            else
                let declModel =
                    if obj.ReferenceEquals(body.SyntaxTree, model.SyntaxTree) then
                        model
                    else
                        model.Compilation.GetSemanticModel body.SyntaxTree

                let ownMember (e: ExpressionSyntax) =
                    let sym =
                        match e with
                        | :? MemberAccessExpressionSyntax as ma when (ma.Expression :? ThisExpressionSyntax) ->
                            symbolOf declModel ma
                        | :? IdentifierNameSyntax as id -> symbolOf declModel id
                        | _ -> null

                    match sym with
                    | :? IFieldSymbol as f -> not f.IsStatic && sameSymbol f.ContainingType m.ContainingType
                    | :? IPropertySymbol as p -> not p.IsStatic && sameSymbol p.ContainingType m.ContainingType
                    | _ -> false

                ownNodes body
                |> Seq.exists (fun n ->
                    match n with
                    | :? AssignmentExpressionSyntax as a -> ownMember a.Left
                    | :? PostfixUnaryExpressionSyntax as u -> ownMember u.Operand
                    | :? PrefixUnaryExpressionSyntax as u when
                        u.IsKind SyntaxKind.PreIncrementExpression
                        || u.IsKind SyntaxKind.PreDecrementExpression
                        ->
                        ownMember u.Operand
                    | :? ArgumentSyntax as a when not (a.RefKindKeyword.IsKind SyntaxKind.None) ->
                        (a.Expression :? ThisExpressionSyntax) || ownMember a.Expression
                    | :? InvocationExpressionSyntax as inv ->
                        let callee =
                            match inv.Expression with
                            | :? IdentifierNameSyntax -> Some(symbolOf declModel inv)
                            | :? MemberAccessExpressionSyntax as ma when (ma.Expression :? ThisExpressionSyntax) ->
                                Some(symbolOf declModel inv)
                            | _ -> None

                        match callee with
                        | Some(:? IMethodSymbol as callee) when
                            not callee.IsStatic
                            && sameSymbol callee.ContainingType m.ContainingType
                            && not (visited.Contains callee.Name)
                            ->
                            mutatesSelf model (visited.Add callee.Name) callee
                        | _ -> false
                    | _ -> false)
        | _ -> false

/// Is the receiver a copy the compiler makes — and why?
[<TailCall>]
let rec private copiedReceiver (model: SemanticModel) (e: ExpressionSyntax) : string option =
    match e with
    | :? ParenthesizedExpressionSyntax as p -> copiedReceiver model p.Expression
    | :? ElementAccessExpressionSyntax as a ->
        match symbolOf model a with
        | :? IPropertySymbol as p when p.IsIndexer && not p.ReturnsByRef && not p.ReturnsByRefReadonly ->
            Some "an indexer returns a copy"
        | _ -> None
    | :? IdentifierNameSyntax
    | :? MemberAccessExpressionSyntax ->
        match symbolOf model e with
        | :? IFieldSymbol as f when f.IsReadOnly ->
            // writable inside the declaring type's constructors
            let inCtor =
                e.Ancestors()
                |> Seq.exists (fun a ->
                    match a with
                    | :? ConstructorDeclarationSyntax as c ->
                        (match model.GetDeclaredSymbol c with
                         | null -> false
                         | s -> sameSymbol s.ContainingType f.ContainingType && s.IsStatic = f.IsStatic)
                    | _ -> false)

            if inCtor then
                None
            else
                Some "a readonly field is copied before the call"
        | :? IFieldSymbol as f when not f.IsStatic ->
            // a mutable struct field of a copied struct is still a copy
            match e with
            | :? MemberAccessExpressionSyntax as ma when f.ContainingType.IsValueType ->
                copiedReceiver model ma.Expression
            | _ -> None
        | :? IPropertySymbol as p when not (p.ReturnsByRef || p.ReturnsByRefReadonly) ->
            Some "a property getter returns a copy"
        | :? ILocalSymbol as l when l.IsForEach -> Some "a foreach variable is a copy"
        | :? ILocalSymbol as l when l.IsUsing -> Some "a using variable is read-only"
        | :? IParameterSymbol as p when p.RefKind = RefKind.In -> Some "an 'in' parameter is read-only"
        | _ -> None
    | _ -> None

let private structCopies (tree: SyntaxTree) (model: SemanticModel) : Suggestion list =
    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun n ->
        match n with
        | :? InvocationExpressionSyntax as inv ->
            match inv.Expression with
            | :? MemberAccessExpressionSyntax as ma ->
                match symbolOf model inv with
                | :? IMethodSymbol as m when
                    not m.IsStatic
                    && not m.IsExtensionMethod
                    && not (isNull m.ContainingType)
                    && m.ContainingType.TypeKind = TypeKind.Struct
                    && not m.ContainingType.IsReadOnly
                    && not m.IsReadOnly
                    && m.MethodKind = MethodKind.Ordinary
                    ->
                    match copiedReceiver model ma.Expression with
                    | None -> None
                    | Some why ->
                        let disposeOnly = m.Name = "Dispose"

                        let disposeAllowed =
                            match symbolOf model ma.Expression with
                            | :? ILocalSymbol as l -> l.IsForEach
                            | :? IParameterSymbol as p -> p.RefKind = RefKind.In
                            | _ -> false

                        if disposeOnly && not disposeAllowed then
                            None
                        elif mutatesSelf model (Set.singleton m.Name) m then
                            Some(
                                Suggestion.note
                                    StructCopyCode
                                    $"'{m.Name}' mutates the struct '{m.ContainingType.Name}', but {why}: the call changes the copy and the original stays as it was — call it on a variable (a local, a non-readonly field, a ref), or make the struct immutable"
                                    inv.Span
                            )
                        else
                            None
                | _ -> None
            | _ -> None
        | _ -> None)
    |> List.ofSeq

// ---- CR0162 ----

let private timerTypes = set [ "System.Threading.Timer" ]

let private droppedTimers (tree: SyntaxTree) (model: SemanticModel) : Suggestion list =
    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun n ->
        let creation =
            match n with
            | :? ObjectCreationExpressionSyntax as c -> Some(c :> ExpressionSyntax)
            | :? ImplicitObjectCreationExpressionSyntax as c -> Some(c :> ExpressionSyntax)
            | _ -> None

        match creation with
        | None -> None
        | Some c ->
            match model.GetTypeInfo(c).Type with
            | null -> None
            | t when not (timerTypes.Contains(t.OriginalDefinition.ToDisplayString())) -> None
            | t ->
                match c.Parent with
                | :? ExpressionStatementSyntax ->
                    Some(
                        Suggestion.note
                            DroppedTimerCode
                            "The timer is created and dropped: nothing references it, the collector takes it and its callbacks stop — keep it in a field for as long as it should fire, and dispose it when done"
                            c.Span
                    )
                | :? EqualsValueClauseSyntax as ev ->
                    match ev.Parent with
                    | :? VariableDeclaratorSyntax as d when (d.Parent.Parent :? LocalDeclarationStatementSyntax) ->
                        match model.GetDeclaredSymbol d with
                        | :? ILocalSymbol as local when not local.IsUsing ->
                            let fn =
                                d.Ancestors()
                                |> Seq.tryFind (fun a -> a :? MemberDeclarationSyntax || isClosure a)

                            match fn with
                            | None -> None
                            | Some fn ->
                                let uses =
                                    fn.DescendantNodes()
                                    |> Seq.choose (fun u ->
                                        match u with
                                        | :? IdentifierNameSyntax as id when
                                            id.Identifier.ValueText = local.Name
                                            && sameSymbol (symbolOf model id) local
                                            ->
                                            Some id
                                        | _ -> None)
                                    |> List.ofSeq

                                let leaves (id: IdentifierNameSyntax) =
                                    // inside a nested closure: captured, and alive with it
                                    (id.Ancestors()
                                     |> Seq.takeWhile (fun a -> not (obj.ReferenceEquals(a, fn)))
                                     |> Seq.exists isClosure)
                                    || (match id.Parent with
                                        | :? ArgumentSyntax -> true
                                        | :? AssignmentExpressionSyntax as a -> obj.ReferenceEquals(a.Right, id)
                                        | :? EqualsValueClauseSyntax -> true
                                        | :? ReturnStatementSyntax
                                        | :? YieldStatementSyntax
                                        | :? ArrowExpressionClauseSyntax
                                        | :? UsingStatementSyntax
                                        | :? InitializerExpressionSyntax
                                        | :? CollectionExpressionSyntax
                                        | :? ExpressionElementSyntax -> true
                                        | :? MemberAccessExpressionSyntax as ma ->
                                            ma.Name.Identifier.ValueText = "Dispose"
                                        | _ -> false)

                                if not (uses |> List.exists leaves) then
                                    Some(
                                        Suggestion.note
                                            DroppedTimerCode
                                            $"'{local.Name}' never leaves this method: once it returns nothing references the timer, the collector takes it and its callbacks stop — keep it in a field for as long as it should fire, and dispose it when done"
                                            c.Span
                                    )
                                else
                                    None
                        | _ -> None
                    | _ -> None
                | _ -> None)
    |> List.ofSeq

let analyze (tree: SyntaxTree) (model: SemanticModel) (_ctx: RuleContext) : Suggestion list =
    loopCaptures tree model @ structCopies tree model @ droppedTimers tree model
