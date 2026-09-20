/// Lifetimes: a disposable that a returned task still needs, and a
/// handler that pins an object to a process-wide publisher.
///
/// CR0051 (correctness, fix): `Task<T> M() { using var x = …; return
/// DoAsync(x); }` disposes `x` when `M` returns — before the task that
/// uses it completes. The method becomes `async` and the return `return
/// await DoAsync(x);`, so the `using` spans the work. Guards: the method
/// is not `async` and returns `Task`/`Task<T>`/`ValueTask`; a `using`
/// declaration or statement in the body binds a name the returned
/// expression mentions (as an argument or inside its lambda); the
/// returned expression is typed as a task; every other return of the
/// method binds too (`await`, `return;` for `Task.CompletedTask`, the
/// value for `Task.FromResult(value)`), returns inside lambdas untouched;
/// the message states the timing change (an exception while evaluating the arguments now faults the
/// task instead of throwing synchronously).
///
/// CR0052 (correctness, note): `AppDomain.CurrentDomain.ProcessExit += (s,
/// e) => this.Flush();` — a handler capturing `this` on a process-wide or
/// static publisher, never removed — keeps the object alive as long as the
/// publisher, which is the process. Guards: the publisher is
/// `AppDomain.CurrentDomain.*`, `Console.CancelKeyPress`,
/// `SystemEvents.*`, a `static event`, or `Subscribe` on a static
/// `IObservable`; the handler captures `this` — a lambda mentioning
/// `this`, an instance member, or a method group of an instance method
/// (explicitly, or wrapped in a delegate constructor); no matching `-=`
/// on the same publisher anywhere in the type; a publisher the object
/// owns (its own event, an event of a field) is a cycle inside one
/// lifetime and stays quiet; a lambda parameter shadowing the captured
/// name suppresses the note.
module CSharp.Refactor.Lifetimes

open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax
open Microsoft.CodeAnalysis.Text

[<Literal>]
let UsingTaskCode = "CR0051"

[<Literal>]
let PinnedHandlerCode = "CR0052"

// ---- CR0051 ----

let private usingOutlivedByTask (tree: SyntaxTree) (model: SemanticModel) : Suggestion list =
    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun n ->
        match n with
        | :? MethodDeclarationSyntax as m when
            not (isNull m.Body)
            && not (m.Modifiers |> Seq.exists (fun t -> t.IsKind SyntaxKind.AsyncKeyword))
            && AsyncShapes.isTaskLike (model.GetTypeInfo(m.ReturnType).Type)
            ->
            // the names the body's usings bind
            let usingNames =
                m.Body.DescendantNodes()
                |> Seq.collect (fun x ->
                    match x with
                    | :? LocalDeclarationStatementSyntax as d when not (d.UsingKeyword.IsKind SyntaxKind.None) ->
                        d.Declaration.Variables |> Seq.map (fun v -> v.Identifier.ValueText)
                    | :? UsingStatementSyntax as u when not (isNull u.Declaration) ->
                        u.Declaration.Variables |> Seq.map (fun v -> v.Identifier.ValueText)
                    | _ -> Seq.empty)
                |> List.ofSeq

            if usingNames.IsEmpty then
                None
            else
                // a return whose task mentions a using-bound name
                let returns =
                    m.Body.DescendantNodes()
                    |> Seq.choose (fun x ->
                        match x with
                        | :? ReturnStatementSyntax as r when
                            not (isNull r.Expression)
                            && AsyncShapes.isTaskLike (model.GetTypeInfo(r.Expression).Type)
                            && not (r.Expression :? AwaitExpressionSyntax)
                            && (match AsyncShapes.enclosingFunction r with
                                | Some f -> obj.ReferenceEquals(f.Node, m)
                                | None -> false)
                            && usingNames |> List.exists (fun name -> Text.mentionsName name r.Expression)
                            // a `return Task.FromResult(x)` is complete already
                            && not (r.Expression.ToString().Contains "FromResult")
                            && not (r.Expression.ToString().Contains "CompletedTask")
                            ->
                            Some r
                        | _ -> None)
                    |> List.ofSeq

                if returns.IsEmpty then
                    None
                else
                    // every task-typed return of the method itself (not of a lambda in it)
                    // must bind once the method is async: `await` on a task, `return;` for
                    // `Task.CompletedTask`, the value for `Task.FromResult(value)`
                    let returnsVoidTask =
                        match model.GetTypeInfo(m.ReturnType).Type with
                        | :? INamedTypeSymbol as t -> not t.IsGenericType
                        | _ -> false

                    let otherReturns =
                        m.Body.DescendantNodes()
                        |> Seq.choose (fun x ->
                            match x with
                            | :? ReturnStatementSyntax as r when
                                not (isNull r.Expression)
                                && not (returns |> List.exists (fun k -> obj.ReferenceEquals(k, r)))
                                && not (r.Expression :? AwaitExpressionSyntax)
                                && (match AsyncShapes.enclosingFunction r with
                                    | Some f -> obj.ReferenceEquals(f.Node, m)
                                    | None -> false)
                                ->
                                Some r
                            | _ -> None)
                        |> List.ofSeq

                    let lastOfBody (r: ReturnStatementSyntax) =
                        m.Body.Statements.Count > 0
                        && obj.ReferenceEquals(m.Body.Statements.[m.Body.Statements.Count - 1], r)

                    // a `Task` method cannot `return await t;`: it awaits, then returns
                    let awaitThenReturn (r: ReturnStatementSyntax) =
                        let awaited = "await " + r.Expression.ToString() + ";"

                        if lastOfBody r then
                            Suggestion.replace r.Span awaited
                        elif r.Parent :? BlockSyntax then
                            Suggestion.replace r.Span (awaited + " return;")
                        else
                            Suggestion.replace r.Span ("{ " + awaited + " return; }")

                    let rebind (r: ReturnStatementSyntax) =
                        let text = r.Expression.ToString()

                        if returnsVoidTask && text = "Task.CompletedTask" then
                            Suggestion.replace r.Span "return;"
                        elif returnsVoidTask then
                            awaitThenReturn r
                        else
                            match r.Expression with
                            | :? InvocationExpressionSyntax as inv when
                                inv.Expression.ToString() = "Task.FromResult"
                                && inv.ArgumentList.Arguments.Count = 1
                                ->
                                Suggestion.replace
                                    r.Expression.Span
                                    (inv.ArgumentList.Arguments.[0].Expression.ToString())
                            | _ -> Suggestion.insert r.Expression.SpanStart "await "

                    let edits =
                        Suggestion.insert m.ReturnType.SpanStart "async "
                        :: (returns
                            |> List.map (fun r ->
                                if returnsVoidTask then
                                    awaitThenReturn r
                                else
                                    Suggestion.insert r.Expression.SpanStart "await "))
                        @ (otherReturns |> List.map rebind)

                    if Guards.speculativeCheck model edits then
                        Some
                            {
                                Code = UsingTaskCode
                                Message =
                                    "The using disposes before the returned task completes: await the task inside the using (the method becomes async; an exception while evaluating the call's arguments now faults the task instead of throwing synchronously)"
                                Span = returns.Head.Span
                                Fixes = [ Suggestion.fix "Await inside the using" UsingTaskCode edits ]
                            }
                    else
                        None
        | _ -> None)
    |> List.ofSeq

// ---- CR0052 ----

let private processWide (model: SemanticModel) (publisher: ExpressionSyntax) =
    let t = publisher.ToString()

    t.StartsWith "AppDomain.CurrentDomain."
    || t.StartsWith "Console.CancelKeyPress"
    || t.Contains "SystemEvents."
    || (match model.GetSymbolInfo(publisher).Symbol with
        | :? IEventSymbol as e -> e.IsStatic
        | _ -> false)

let private capturesThis (model: SemanticModel) (handler: ExpressionSyntax) =
    let instanceMember (s: ISymbol) =
        match s with
        | null -> false
        | :? IMethodSymbol as m -> not m.IsStatic
        | :? IFieldSymbol as f -> not f.IsStatic
        | :? IPropertySymbol as p -> not p.IsStatic
        | _ -> false

    match handler with
    | :? AnonymousFunctionExpressionSyntax as l ->
        let shadowing =
            match l with
            | :? SimpleLambdaExpressionSyntax as s -> [ s.Parameter.Identifier.ValueText ]
            | :? ParenthesizedLambdaExpressionSyntax as p ->
                p.ParameterList.Parameters
                |> Seq.map (fun q -> q.Identifier.ValueText)
                |> List.ofSeq
            | _ -> []

        l.Body.DescendantNodesAndSelf()
        |> Seq.exists (fun x ->
            match x with
            | :? ThisExpressionSyntax -> true
            | :? IdentifierNameSyntax as id when not (List.contains id.Identifier.ValueText shadowing) ->
                // a bare instance member is an implicit `this`
                not (
                    id.Parent :? MemberAccessExpressionSyntax
                    && (id.Parent :?> MemberAccessExpressionSyntax).Name.Span = id.Span
                )
                && instanceMember (model.GetSymbolInfo(id).Symbol)
            | _ -> false)
    | :? ObjectCreationExpressionSyntax as c when not (isNull c.ArgumentList) && c.ArgumentList.Arguments.Count = 1 ->
        // `new EventHandler(this.OnX)`
        instanceMember (model.GetSymbolInfo(c.ArgumentList.Arguments.[0].Expression).Symbol)
    | e ->
        let info = model.GetSymbolInfo e

        instanceMember info.Symbol
        || (info.CandidateSymbols |> Seq.exists instanceMember)

let private pinnedHandlers (tree: SyntaxTree) (model: SemanticModel) : Suggestion list =
    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun n ->
        match n with
        | :? AssignmentExpressionSyntax as a when a.IsKind SyntaxKind.AddAssignmentExpression ->
            let publisher = a.Left

            if not (processWide model publisher && capturesThis model a.Right) then
                None
            else
                // a matching `-=` anywhere in the type
                let typeDecl =
                    a.Ancestors()
                    |> Seq.tryPick (fun x ->
                        match x with
                        | :? TypeDeclarationSyntax as t -> Some t
                        | _ -> None)

                let removed =
                    match typeDecl with
                    | Some t ->
                        t.DescendantNodes()
                        |> Seq.exists (fun x ->
                            match x with
                            | :? AssignmentExpressionSyntax as r ->
                                r.IsKind SyntaxKind.SubtractAssignmentExpression
                                && r.Left.ToString() = publisher.ToString()
                                // a lambda can never be removed; a method group by the same name can
                                && not (a.Right :? AnonymousFunctionExpressionSyntax)
                                && r.Right.ToString() = a.Right.ToString()
                            | _ -> false)
                    | None -> false

                if removed then
                    None
                else
                    Some(
                        Suggestion.note
                            PinnedHandlerCode
                            $"A handler capturing 'this' on '{publisher}', never removed: the object lives as long as the publisher, which is the process — unsubscribe (-=) when the object is done, or subscribe through a weak reference"
                            a.Span
                    )
        | :? InvocationExpressionSyntax as inv when
            Linq.nameOf inv = "Subscribe" && inv.ArgumentList.Arguments.Count >= 1
            ->
            match Linq.receiverOf inv with
            | Some publisher when
                (match model.GetSymbolInfo(publisher).Symbol with
                 | :? IFieldSymbol as f -> f.IsStatic
                 | :? IPropertySymbol as p -> p.IsStatic
                 | _ -> false)
                && capturesThis model inv.ArgumentList.Arguments.[0].Expression
                && (inv.Parent :? ExpressionStatementSyntax)  // the subscription is dropped, never disposed
                ->
                Some(
                    Suggestion.note
                        PinnedHandlerCode
                        $"A subscription capturing 'this' on the static '{publisher}', its IDisposable dropped: the object lives as long as the publisher"
                        inv.Span
                )
            | _ -> None
        | _ -> None)
    |> List.ofSeq

let analyze (tree: SyntaxTree) (model: SemanticModel) (_ctx: RuleContext) : Suggestion list =
    usingOutlivedByTask tree model @ pinnedHandlers tree model
