/// The async shapes read off one body.
///
/// CR0040 (correctness, fix): a blocking drain inside an `async` body is
/// an `await`.
///
///     var x = t.Result;                     →  var x = await t;
///     t.Wait();                             →  await t;
///     t.GetAwaiter().GetResult()            →  await t
///     Task.WaitAll(a, b);                   →  await Task.WhenAll(a, b);
///     Task.Run(() => t.Result)              →  t
///
/// Outside an `async` body the same drain is the boundary between the
/// async and sync worlds and gets a note (the sync-twin swap is the
/// author's). Guards: the receiver is typed `Task`/`Task<T>`/`ValueTask`/
/// `ValueTask<T>`; the site is not inside a `lock`, a `catch` filter, a
/// `finally`, an `unsafe` block, a non-`async` lambda or a local function
/// (each is its own boundary; only the innermost function attributes a
/// site); a `catch (AggregateException)` around the site stands the fix
/// down — `.Result` and `Wait()` throw the wrapper, `await` the inner
/// exception, and the handler would go dead; a task known complete (under
/// its own `IsCompleted` test, born of `Task.FromResult`/`CompletedTask`,
/// after its own `Wait(timeout)` or `proc.WaitForExit()`) is a read, not a
/// block; a body choreographed around a thread (a `Thread`, a signal,
/// `Interlocked`) gets the note only — a bind moves the continuation off
/// the thread the wait kept it on; `Task.WaitAll(tasks, timeout)` stays;
/// the spine of `Main` and top-level statements is the console's blocking
/// point and gets no note; the replacement takes parentheses where it is
/// a receiver. `GetAwaiter().GetResult()` is never emitted by any rule.
///
/// CR0044 (correctness, note): a `Task`-returning call as a statement in a
/// non-`async` method — `Task.Run(…);`, `SaveAsync();` — is fire-and-forget
/// whose failure nobody observes (on .NET Core an unobserved fault is
/// silently lost; it killed the process only on .NET Framework 4.0). A
/// `try/catch` around the start catches nothing of the work. Quiet where
/// the task is discarded on purpose (`_ = …`), continued with
/// `OnlyOnFaulted`, or the started lambda's whole body is a `try/catch`;
/// inside an `async` method the compiler's CS4014 says it.
///
/// CR0046 (performance, fix): `async Task<T> M(x) { return await Inner(x); }`
/// with nothing else in the body is `Task<T> M(x) { return Inner(x); }` —
/// one state machine and its allocation fewer. Guards: the body is exactly
/// `return await e;`, `await e;` or `=> await e`; `e` is an invocation of a
/// method the typed tree marks `async` (an async callee never throws
/// synchronously, so the one observable difference — a synchronous throw
/// where the caller held a faulted task — cannot occur) with pure-atom
/// arguments; no `ConfigureAwait`; no `using`, `try`, `lock`, `foreach` or
/// `await using`; the return types match exactly.
///
/// CR0053 (correctness, note): an `async` lambda converted to a
/// `void`-returning delegate — `list.ForEach(async x => …)`,
/// `Parallel.ForEach(xs, async x => …)` — is `async void` in disguise:
/// nothing awaits it, an exception crashes the process. `Task.Run(async ()
/// => …)`, any `Func<Task>` target and an event subscription (`+=`, the
/// one place async void belongs) are fine. Yields
/// to VSTHRD101.
///
/// CR0054 (performance, note): `Task.WhenAll(new[] { t })`, `Task.WhenAll([t])`,
/// `Task.WaitAll(new[] { t })` combine one task; only a literal one-element
/// collection matches. The direct form changes the result type
/// (`Task<T[]>` → `Task<T>`), so the editor offers it and the sweep does
/// not (for `WaitAll` the element alone would drop the wait: `t.Wait()`).
/// Yields to CA1842/CA1843.
///
/// CR0055 (correctness, fix): `Foo(x, CancellationToken.None)` or
/// `Foo(x, default)` while a token parameter is in scope hands the callee
/// nothing to cancel on: `Foo(x, ct)`. Guards: exactly one
/// `CancellationToken` parameter on the enclosing function; the call
/// resolves to a method whose parameter at that position is a
/// `CancellationToken`; never inside a `catch`/`finally` (a cancelled
/// operation must still roll back); never on `Task.Run`/
/// `Task.Factory.StartNew`, where the token is a scheduling condition and
/// `None` means "always start the work"; never where the token is already
/// among the arguments; never a named argument; never where the `None` is
/// bound to a name. Yields to CA2016.
module CSharp.Refactor.AsyncShapes

open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax
open Microsoft.CodeAnalysis.Text

[<Literal>]
let BlockingCode = "CR0040"

[<Literal>]
let ForgottenTaskCode = "CR0044"

[<Literal>]
let ElideAsyncCode = "CR0046"

[<Literal>]
let AsyncVoidLambdaCode = "CR0053"

[<Literal>]
let SingleTaskCode = "CR0054"

[<Literal>]
let TokenCode = "CR0055"

// ---- what a body is ----

let private taskNames =
    set
        [
            "System.Threading.Tasks.Task"
            "System.Threading.Tasks.Task<TResult>"
            "System.Threading.Tasks.ValueTask"
            "System.Threading.Tasks.ValueTask<TResult>"
        ]

let isTaskLike (t: ITypeSymbol) =
    match t with
    | null -> false
    | t -> taskNames.Contains(t.OriginalDefinition.ToDisplayString())

let private isGenericTask (t: ITypeSymbol) =
    match t with
    | null -> false
    | t ->
        let n = t.OriginalDefinition.ToDisplayString()

        n = "System.Threading.Tasks.Task<TResult>"
        || n = "System.Threading.Tasks.ValueTask<TResult>"

/// The innermost function around a node: a method, accessor, lambda or
/// local function, with whether it is `async`, and its body.
type Function =
    {
        Node: SyntaxNode
        IsAsync: bool
        Body: SyntaxNode
        Parameters: ParameterSyntax list
    }

let enclosingFunction (node: SyntaxNode) : Function option =
    node.Ancestors()
    |> Seq.tryPick (fun a ->
        let hasAsync (mods: SyntaxTokenList) =
            mods |> Seq.exists (fun t -> t.IsKind SyntaxKind.AsyncKeyword)

        match a with
        | :? AnonymousFunctionExpressionSyntax as l ->
            let ps =
                match l with
                | :? SimpleLambdaExpressionSyntax as s -> [ s.Parameter ]
                | :? ParenthesizedLambdaExpressionSyntax as p -> List.ofSeq p.ParameterList.Parameters
                | :? AnonymousMethodExpressionSyntax as m when not (isNull m.ParameterList) ->
                    List.ofSeq m.ParameterList.Parameters
                | _ -> []

            Some
                {
                    Node = a
                    IsAsync = hasAsync l.Modifiers
                    Body = l.Body
                    Parameters = ps
                }
        | :? LocalFunctionStatementSyntax as f ->
            let body: SyntaxNode =
                if isNull f.Body then
                    f.ExpressionBody :> SyntaxNode
                else
                    f.Body :> SyntaxNode

            Some
                {
                    Node = a
                    IsAsync = hasAsync f.Modifiers
                    Body = body
                    Parameters = List.ofSeq f.ParameterList.Parameters
                }
        | :? MethodDeclarationSyntax as m ->
            let body: SyntaxNode =
                if isNull m.Body then
                    m.ExpressionBody :> SyntaxNode
                else
                    m.Body :> SyntaxNode

            Some
                {
                    Node = a
                    IsAsync = hasAsync m.Modifiers
                    Body = body
                    Parameters = List.ofSeq m.ParameterList.Parameters
                }
        | :? AccessorDeclarationSyntax as acc ->
            let body: SyntaxNode =
                if isNull acc.Body then
                    acc.ExpressionBody :> SyntaxNode
                else
                    acc.Body :> SyntaxNode

            Some
                {
                    Node = a
                    IsAsync = false
                    Body = body
                    Parameters = []
                }
        | :? ConstructorDeclarationSyntax as c ->
            Some
                {
                    Node = a
                    IsAsync = false
                    Body =
                        (if isNull c.Body then
                             c.ExpressionBody :> SyntaxNode
                         else
                             c.Body :> SyntaxNode)
                    Parameters = List.ofSeq c.ParameterList.Parameters
                }
        | _ -> None)

/// Inside a `lock`, a `catch` filter, a `finally`, an `unsafe` block —
/// where `await` is illegal or wrong — below the given function.
let private inNoBindZone (fn: Function) (node: SyntaxNode) =
    node.Ancestors()
    |> Seq.takeWhile (fun a -> not (obj.ReferenceEquals(a, fn.Node)))
    |> Seq.exists (fun a ->
        match a with
        | :? LockStatementSyntax
        | :? FinallyClauseSyntax
        | :? CatchFilterClauseSyntax
        | :? UnsafeStatementSyntax -> true
        | _ -> false)

let private inCatchOrFinally (fn: Function) (node: SyntaxNode) =
    node.Ancestors()
    |> Seq.takeWhile (fun a -> not (obj.ReferenceEquals(a, fn.Node)))
    |> Seq.exists (fun a -> a :? CatchClauseSyntax || a :? FinallyClauseSyntax)

/// A `try` around the node whose handler names `AggregateException`.
let private underAggregateCatch (fn: Function) (node: SyntaxNode) =
    node.Ancestors()
    |> Seq.takeWhile (fun a -> not (obj.ReferenceEquals(a, fn.Node)))
    |> Seq.exists (fun a ->
        match a with
        | :? TryStatementSyntax as t ->
            t.Block.Span.Contains node.Span
            && t.Catches
               |> Seq.exists (fun c ->
                   not (isNull c.Declaration)
                   && c.Declaration.Type.ToString().EndsWith "AggregateException")
        | _ -> false)

/// A body that choreographs threads by hand: a bind would move the
/// continuation off the thread the wait kept it on.
let private threadChoreographed (body: SyntaxNode) =
    body.DescendantNodes()
    |> Seq.exists (fun n ->
        let text = n.ToString()

        match n with
        | :? ObjectCreationExpressionSyntax as c ->
            let t = c.Type.ToString()

            t = "Thread"
            || t.EndsWith ".Thread"
            || t.Contains "ManualResetEvent"
            || t.Contains "AutoResetEvent"
            || t.Contains "Barrier"
            || t.Contains "CountdownEvent"
        | :? MemberAccessExpressionSyntax as m ->
            text.StartsWith "Interlocked."
            || (m.Name.Identifier.ValueText = "Set" && text.Contains "Event")
            || m.Name.Identifier.ValueText = "WaitOne"
        | _ -> false)

/// Is the task known complete where it is drained?
let private knownComplete (model: SemanticModel) (fn: Function) (receiver: ExpressionSyntax) (site: SyntaxNode) =
    let text = receiver.ToString()

    let bornComplete (e: ExpressionSyntax) =
        let s = e.ToString()

        s.Contains "Task.FromResult"
        || s.Contains "Task.CompletedTask"
        || s.Contains "ValueTask.FromResult"
        || s.Contains "Task.FromException"
        || s.Contains "Task.FromCanceled"

    // born complete: the receiver's own declaration, or a static readonly field
    let declaredComplete =
        match receiver with
        | :? IdentifierNameSyntax as id ->
            match model.GetSymbolInfo(id).Symbol with
            | :? ILocalSymbol as l ->
                l.DeclaringSyntaxReferences
                |> Seq.exists (fun r ->
                    match r.GetSyntax() with
                    | :? VariableDeclaratorSyntax as v when not (isNull v.Initializer) ->
                        bornComplete v.Initializer.Value
                    | _ -> false)
            | :? IFieldSymbol as f when f.IsReadOnly ->
                f.DeclaringSyntaxReferences
                |> Seq.exists (fun r ->
                    match r.GetSyntax() with
                    | :? VariableDeclaratorSyntax as v when not (isNull v.Initializer) ->
                        bornComplete v.Initializer.Value
                    | _ -> false)
            | _ -> false
        | e -> bornComplete e

    // under `if (t.IsCompleted)` / `IsCompletedSuccessfully` in the then-branch
    let underCompletedTest =
        site.Ancestors()
        |> Seq.takeWhile (fun a -> not (obj.ReferenceEquals(a, fn.Node)))
        |> Seq.exists (fun a ->
            match a with
            | :? IfStatementSyntax as ifs when ifs.Statement.Span.Contains site.Span ->
                let c = ifs.Condition.ToString()
                c.Contains(text + ".IsCompleted")
            | :? ConditionalExpressionSyntax as c when c.WhenTrue.Span.Contains site.Span ->
                c.Condition.ToString().Contains(text + ".IsCompleted")
            | _ -> false)

    // after its own `Wait(timeout)` or a `WaitForExit()` in the same block
    let waitedAbove =
        match
            site.Ancestors()
            |> Seq.tryPick (fun a ->
                match a with
                | :? BlockSyntax as b -> Some b
                | _ -> None)
        with
        | Some block ->
            block.Statements
            |> Seq.takeWhile (fun s -> s.SpanStart < site.SpanStart)
            |> Seq.exists (fun s ->
                let t = s.ToString()
                // `t.Wait(timeout);` bounds the block; `t.Wait();` is the drain itself;
                // an `await t` or `await Task.WhenAll(…, t, …)` above completed it
                (t.StartsWith(text + ".Wait(") && t.Trim() <> text + ".Wait();")
                || t.Contains ".WaitForExit("
                || (s.DescendantNodesAndSelf()
                    |> Seq.exists (fun n ->
                        match n with
                        | :? AwaitExpressionSyntax as a ->
                            let awaited = a.Expression.ToString()

                            awaited = text
                            || ((awaited.StartsWith "Task.WhenAll(")
                                && (match a.Expression with
                                    | :? InvocationExpressionSyntax as inv ->
                                        inv.ArgumentList.Arguments
                                        |> Seq.exists (fun arg -> arg.Expression.ToString() = text)
                                    | _ -> false))
                        | _ -> false)))
        | None -> false

    declaredComplete || underCompletedTest || waitedAbove

/// The `Main` spine or top-level statements: the console's blocking point.
let private isConsoleSpine (fn: Function option) (node: SyntaxNode) =
    match fn with
    | Some {
               Node = :? MethodDeclarationSyntax as m
           } -> m.Identifier.ValueText = "Main"
    | None -> node.Ancestors() |> Seq.exists (fun a -> a :? GlobalStatementSyntax)
    | _ -> false

/// Does an `await` here need parentheses: as a receiver of a member,
/// element or conditional access?
let private needsParens (site: SyntaxNode) =
    match site.Parent with
    | :? MemberAccessExpressionSyntax as m -> m.Expression.Span = site.Span
    | :? ElementAccessExpressionSyntax as e -> e.Expression.Span = site.Span
    | :? ConditionalAccessExpressionSyntax as c -> c.Expression.Span = site.Span
    | :? InvocationExpressionSyntax as i -> i.Expression.Span = site.Span
    | :? PostfixUnaryExpressionSyntax -> true
    | _ -> false

// ---- CR0040 ----

type Drain =
    /// `t.Result`, `t.GetAwaiter().GetResult()`: an expression
    | Value of site: ExpressionSyntax * receiver: ExpressionSyntax
    /// `t.Wait();` as a statement
    | Wait of site: ExpressionStatementSyntax * receiver: ExpressionSyntax
    /// `Task.WaitAll(a, b);` as a statement
    | WaitAll of site: ExpressionStatementSyntax * inv: InvocationExpressionSyntax
    /// `Task.Run(() => t.Result)`
    | RunDrain of site: InvocationExpressionSyntax * receiver: ExpressionSyntax

let drains (model: SemanticModel) (root: SyntaxNode) : Drain list =
    let typed (e: ExpressionSyntax) = isTaskLike (model.GetTypeInfo(e).Type)

    root.DescendantNodes()
    |> Seq.choose (fun n ->
        match n with
        | :? MemberAccessExpressionSyntax as m when
            m.Name.Identifier.ValueText = "Result"
            && typed m.Expression
            && not (
                m.Parent :? InvocationExpressionSyntax
                && (m.Parent :?> InvocationExpressionSyntax).Expression.Span = m.Span
            )
            // the body of `Task.Run(() => t.Result)` is the RunDrain shape, not a site of its own
            && not (
                match m.Parent with
                | :? ParenthesizedLambdaExpressionSyntax as l ->
                    match l.Parent with
                    | :? ArgumentSyntax as arg ->
                        match arg.Parent.Parent with
                        | :? InvocationExpressionSyntax as run -> run.Expression.ToString() = "Task.Run"
                        | _ -> false
                    | _ -> false
                | _ -> false
            )
            ->
            Some(Value(m, m.Expression))
        | :? InvocationExpressionSyntax as inv when inv.ToString().EndsWith ".GetAwaiter().GetResult()" ->
            match inv.Expression with
            | :? MemberAccessExpressionSyntax as m ->
                match m.Expression with
                | :? InvocationExpressionSyntax as getAwaiter ->
                    match getAwaiter.Expression with
                    | :? MemberAccessExpressionSyntax as ga when typed ga.Expression -> Some(Value(inv, ga.Expression))
                    | _ -> None
                | _ -> None
            | _ -> None
        | :? ExpressionStatementSyntax as s ->
            match s.Expression with
            | :? InvocationExpressionSyntax as inv ->
                match inv.Expression with
                | :? MemberAccessExpressionSyntax as m when
                    m.Name.Identifier.ValueText = "Wait"
                    && inv.ArgumentList.Arguments.Count = 0
                    && typed m.Expression
                    ->
                    Some(Wait(s, m.Expression))
                | :? MemberAccessExpressionSyntax as m when
                    m.Name.Identifier.ValueText = "WaitAll"
                    && m.Expression.ToString() = "Task"
                    && inv.ArgumentList.Arguments.Count > 0
                    && inv.ArgumentList.Arguments |> Seq.forall (fun a -> typed a.Expression)
                    ->
                    Some(WaitAll(s, inv))
                | _ -> None
            | _ -> None
        | :? InvocationExpressionSyntax as inv when
            inv.Expression.ToString() = "Task.Run" && inv.ArgumentList.Arguments.Count = 1
            ->
            match inv.ArgumentList.Arguments.[0].Expression with
            | :? ParenthesizedLambdaExpressionSyntax as l when
                l.ParameterList.Parameters.Count = 0
                && not (isNull l.ExpressionBody)
                && not (l.Modifiers |> Seq.exists (fun t -> t.IsKind SyntaxKind.AsyncKeyword))
                ->
                match l.ExpressionBody with
                | :? MemberAccessExpressionSyntax as m when
                    m.Name.Identifier.ValueText = "Result" && typed m.Expression
                    ->
                    Some(RunDrain(inv, m.Expression))
                | _ -> None
            | _ -> None
        | _ -> None)
    |> List.ofSeq

/// The site a drain occupies, and the receiver it drains.
let drainSite (drain: Drain) : SyntaxNode * ExpressionSyntax option =
    match drain with
    | Value(s, r) -> s :> SyntaxNode, Some r
    | Wait(s, r) -> s :> SyntaxNode, Some r
    | WaitAll(s, _) -> s :> SyntaxNode, None
    | RunDrain(s, r) -> s :> SyntaxNode, Some r

/// The edit that awaits a drain.
let awaitEdit (drain: Drain) : TextEdit =
    match drain with
    | Value(s, r) ->
        let awaited = "await " + r.ToString()
        Suggestion.replace s.Span (if needsParens s then $"({awaited})" else awaited)
    | Wait(s, r) -> Suggestion.replace s.Expression.Span ("await " + r.ToString())
    | WaitAll(s, inv) -> Suggestion.replace s.Expression.Span ("await Task.WhenAll" + inv.ArgumentList.ToString())
    | RunDrain(s, r) -> Suggestion.replace s.Span (r.ToString())

/// Can a drain in this function be awaited at all: not in a no-bind zone,
/// not under an AggregateException handler, not known complete, not in a
/// thread-choreographed body?
let bindable (model: SemanticModel) (fn: Function) (drain: Drain) =
    let site, receiver = drainSite drain

    not (inNoBindZone fn site)
    && not (underAggregateCatch fn site)
    && not (receiver |> Option.exists (fun r -> knownComplete model fn r site))
    && not (threadChoreographed fn.Body)

let private blocking (tree: SyntaxTree) (model: SemanticModel) : Suggestion list =
    let text = tree.GetText()

    drains model (tree.GetRoot())
    |> List.choose (fun drain ->
        let site: SyntaxNode =
            match drain with
            | Value(s, _) -> s :> SyntaxNode
            | Wait(s, _) -> s :> SyntaxNode
            | WaitAll(s, _) -> s :> SyntaxNode
            | RunDrain(s, _) -> s :> SyntaxNode

        let receiver =
            match drain with
            | Value(_, r)
            | Wait(_, r)
            | RunDrain(_, r) -> Some r
            | WaitAll _ -> None

        let fn = enclosingFunction site

        let complete =
            match fn with
            | Some f -> receiver |> Option.exists (fun r -> knownComplete model f r site)
            | None -> false

        match fn with
        | _ when complete -> None
        | Some f when f.IsAsync ->
            if inNoBindZone f site then
                None
            elif underAggregateCatch f site then
                Some(
                    Suggestion.note
                        BlockingCode
                        "A blocking drain inside an async method; the catch of AggregateException around it would go dead under await (await throws the inner exception), so the fix is held"
                        site.Span
                )
            elif threadChoreographed f.Body then
                Some(
                    Suggestion.note
                        BlockingCode
                        "A blocking drain inside an async method; the body choreographs a thread by hand, and a bind would move the continuation off it, so the fix is held"
                        site.Span
                )
            else
                let edit = awaitEdit drain

                if Guards.speculativeCheck model [ edit ] then
                    Some
                        {
                            Code = BlockingCode
                            Message = "A blocking drain inside an async method holds a thread the await would free"
                            Span = site.Span
                            Fixes = [ Suggestion.fix "Await it" BlockingCode [ edit ] ]
                        }
                else
                    None
        | _ ->
            // outside an async body: the boundary, unless the console's own
            if isConsoleSpine fn site then
                None
            else
                Some(
                    Suggestion.note
                        BlockingCode
                        "A sync-over-async boundary: the thread blocks on the task here; make the caller async (CR0041) or keep it as the one deliberate blocking point"
                        site.Span
                ))

// ---- CR0044 ----

let private forgottenTasks (tree: SyntaxTree) (model: SemanticModel) : Suggestion list =
    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun n ->
        match n with
        | :? ExpressionStatementSyntax as s when
            (s.Expression :? InvocationExpressionSyntax
             || s.Expression :? ConditionalAccessExpressionSyntax)
            ->
            let t = model.GetTypeInfo(s.Expression).Type

            if not (isTaskLike t) then
                None
            else
                match enclosingFunction s with
                | Some f when f.IsAsync -> None // CS4014's
                | _ ->
                    let handled =
                        match s.Expression with
                        | :? InvocationExpressionSyntax as inv ->
                            let name = Linq.nameOf inv

                            // `.ContinueWith(…, TaskContinuationOptions.OnlyOnFaulted)`
                            (name = "ContinueWith" && inv.ArgumentList.ToString().Contains "OnlyOnFaulted")
                            // `Task.Run(async () => { try { … } catch { … } })`: the whole body handled
                            || (inv.ArgumentList.Arguments.Count >= 1
                                && (match inv.ArgumentList.Arguments.[0].Expression with
                                    | :? AnonymousFunctionExpressionSyntax as l ->
                                        match l.Body with
                                        | :? BlockSyntax as b when b.Statements.Count = 1 ->
                                            match b.Statements.[0] with
                                            | :? TryStatementSyntax as ts -> ts.Catches.Count > 0
                                            | _ -> false
                                        | _ -> false
                                    | _ -> false))
                        | _ -> false

                    if handled then
                        None
                    else
                        let aroundStart =
                            s.Ancestors()
                            |> Seq.exists (fun a ->
                                match a with
                                | :? TryStatementSyntax as ts -> ts.Block.Span.Contains s.Span && ts.Catches.Count > 0
                                | _ -> false)

                        let message =
                            "A task started and dropped: nobody observes its failure, which is silently lost (an unobserved fault killed the process on .NET Framework 4.0 only)"
                            + (if aroundStart then
                                   "; the try/catch around the start catches nothing of the work — move it inside the started body"
                               else
                                   "; await it, store it, or discard it on purpose with '_ ='")

                        Some(Suggestion.note ForgottenTaskCode message s.Expression.Span)
        | _ -> None)
    |> List.ofSeq

// ---- CR0046 ----

let private elideAsync (tree: SyntaxTree) (model: SemanticModel) : Suggestion list =
    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun n ->
        match n with
        | :? MethodDeclarationSyntax as m when m.Modifiers |> Seq.exists (fun t -> t.IsKind SyntaxKind.AsyncKeyword) ->
            let asyncToken = m.Modifiers |> Seq.find (fun t -> t.IsKind SyntaxKind.AsyncKeyword)

            // the one awaited expression, and the await node
            let awaited: (AwaitExpressionSyntax * bool) option =
                if not (isNull m.ExpressionBody) then
                    match m.ExpressionBody.Expression with
                    | :? AwaitExpressionSyntax as a -> Some(a, true)
                    | _ -> None
                elif not (isNull m.Body) && m.Body.Statements.Count = 1 then
                    match m.Body.Statements.[0] with
                    | :? ReturnStatementSyntax as r ->
                        match r.Expression with
                        | :? AwaitExpressionSyntax as a -> Some(a, true)
                        | _ -> None
                    | :? ExpressionStatementSyntax as s ->
                        match s.Expression with
                        | :? AwaitExpressionSyntax as a -> Some(a, false)
                        | _ -> None
                    | _ -> None
                else
                    None

            match awaited with
            | Some(a, returns) ->
                match a.Expression with
                | :? InvocationExpressionSyntax as inv when
                    not (inv.ToString().Contains "ConfigureAwait")
                    && inv.ArgumentList.Arguments
                       |> Seq.forall (fun arg -> Guards.isPureExpression model arg.Expression)
                    ->
                    match model.GetSymbolInfo(inv).Symbol, model.GetDeclaredSymbol m with
                    | (:? IMethodSymbol as callee), self when
                        not (isNull self)
                        && callee.IsAsync
                        && SymbolEqualityComparer.Default.Equals(callee.ReturnType, self.ReturnType)
                        && not (Text.holdsCommentOrDirective m)
                        ->
                        let edits =
                            [
                                // `async ` with its trailing space
                                Suggestion.replace
                                    (TextSpan.FromBounds(asyncToken.SpanStart, asyncToken.FullSpan.End))
                                    ""
                                if returns then
                                    Suggestion.replace (TextSpan.FromBounds(a.SpanStart, a.Expression.SpanStart)) ""
                                else
                                    Suggestion.replace
                                        (TextSpan.FromBounds(a.SpanStart, a.Expression.SpanStart))
                                        "return "
                            ]

                        if Guards.speculativeCheck model edits then
                            Some
                                {
                                    Code = ElideAsyncCode
                                    Message =
                                        "A method that only awaits and returns an async call builds a state machine for nothing: return the task"
                                    Span = asyncToken.Span
                                    Fixes = [ Suggestion.fix "Return the task" ElideAsyncCode edits ]
                                }
                        else
                            None
                    | _ -> None
                | _ -> None
            | None -> None
        | _ -> None)
    |> List.ofSeq

// ---- CR0053 ----

let private asyncVoidLambdas (tree: SyntaxTree) (model: SemanticModel) : Suggestion list =
    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun n ->
        match n with
        | :? AnonymousFunctionExpressionSyntax as l when
            l.Modifiers |> Seq.exists (fun t -> t.IsKind SyntaxKind.AsyncKeyword)
            // an event handler is the one place async void belongs
            && not (
                match l.Parent with
                | :? AssignmentExpressionSyntax as a ->
                    a.IsKind SyntaxKind.AddAssignmentExpression
                    || a.IsKind SyntaxKind.SubtractAssignmentExpression
                | _ -> false
            )
            ->
            match model.GetTypeInfo(l).ConvertedType with
            | :? INamedTypeSymbol as d when d.TypeKind = TypeKind.Delegate && not (isNull d.DelegateInvokeMethod) ->
                if d.DelegateInvokeMethod.ReturnsVoid then
                    Some(
                        Suggestion.note
                            AsyncVoidLambdaCode
                            $"An async lambda handed to a '{d.ToDisplayString()}' is async void in disguise: nothing awaits it and an exception crashes the process — take a Func<Task> overload or a Task.WhenAll of the started tasks"
                            l.Span
                    )
                else
                    None
            | _ -> None
        | _ -> None)
    |> List.ofSeq

// ---- CR0054 ----

let private singleTasks (tree: SyntaxTree) (model: SemanticModel) : Suggestion list =
    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun n ->
        match n with
        | :? InvocationExpressionSyntax as inv when
            (let e = inv.Expression.ToString()

             e = "Task.WhenAll"
             || e = "Task.WhenAny"
             || e = "Task.WaitAll"
             || e = "Task.WaitAny")
            && inv.ArgumentList.Arguments.Count = 1
            ->
            let arg = inv.ArgumentList.Arguments.[0].Expression

            let single =
                match arg with
                | :? ImplicitArrayCreationExpressionSyntax as a -> a.Initializer.Expressions.Count = 1
                | :? ArrayCreationExpressionSyntax as a ->
                    not (isNull a.Initializer) && a.Initializer.Expressions.Count = 1
                | :? CollectionExpressionSyntax as c -> c.Elements.Count = 1
                | _ -> false

            if single then
                let name = inv.Expression.ToString()

                let element =
                    match arg with
                    | :? ImplicitArrayCreationExpressionSyntax as a -> a.Initializer.Expressions.[0].ToString()
                    | :? ArrayCreationExpressionSyntax as a -> a.Initializer.Expressions.[0].ToString()
                    | :? CollectionExpressionSyntax as c -> c.Elements.[0].ToString()
                    | _ -> ""

                let direct =
                    if name.StartsWith "Task.Wait" then
                        element + ".Wait()"
                    else
                        element

                Some
                    {
                        Code = SingleTaskCode
                        Message =
                            $"'{name}' of one task combines nothing: '{direct}' is the task itself (the result type changes, so the editor offers it)"
                        Span = inv.Span
                        Fixes =
                            [
                                Suggestion.fix
                                    "Use the task itself"
                                    SingleTaskCode
                                    [ Suggestion.replace inv.Span direct ]
                                |> Suggestion.editorOnly
                            ]
                    }
            else
                None
        | _ -> None)
    |> List.ofSeq

// ---- CR0055 ----

let private isTokenType (t: ITypeSymbol) =
    not (isNull t) && t.ToDisplayString() = "System.Threading.CancellationToken"

let private tokens (tree: SyntaxTree) (model: SemanticModel) : Suggestion list =
    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun n ->
        match n with
        | :? ArgumentSyntax as arg when isNull arg.NameColon ->
            let isNone =
                match arg.Expression with
                | :? MemberAccessExpressionSyntax as m -> m.ToString() = "CancellationToken.None"
                | :? LiteralExpressionSyntax as l ->
                    l.IsKind SyntaxKind.DefaultLiteralExpression
                    && isTokenType (model.GetTypeInfo(l).ConvertedType)
                | :? DefaultExpressionSyntax as d -> isTokenType (model.GetTypeInfo(d).Type)
                | _ -> false

            if not isNone then
                None
            else
                match arg.Parent.Parent with
                | :? InvocationExpressionSyntax as inv ->
                    let callee = inv.Expression.ToString()

                    let scheduling =
                        callee = "Task.Run"
                        || callee.EndsWith "Task.Factory.StartNew"
                        || callee = "Task.Factory.StartNew"

                    // a decision spelled out beside the call, or a best-effort cleanup (the
                    // call alone in a `try` whose `catch` swallows): the author's `None`
                    let statement =
                        arg.Ancestors()
                        |> Seq.tryPick (fun a ->
                            match a with
                            | :? StatementSyntax as s -> Some s
                            | _ -> None)

                    let deliberate =
                        match statement with
                        | Some s ->
                            let comments =
                                s.GetLeadingTrivia()
                                |> Seq.filter (fun t ->
                                    t.IsKind SyntaxKind.SingleLineCommentTrivia
                                    || t.IsKind SyntaxKind.MultiLineCommentTrivia)
                                |> Seq.map (fun t -> t.ToString())
                                |> String.concat " "

                            let names =
                                comments.Contains "None" || comments.ToLowerInvariant().Contains "cancel"

                            let bestEffort =
                                match s.Parent with
                                | :? BlockSyntax as b when b.Statements.Count = 1 ->
                                    match b.Parent with
                                    | :? TryStatementSyntax as t -> t.Catches.Count > 0
                                    | _ -> false
                                | _ -> false

                            names || bestEffort
                        | None -> false

                    match enclosingFunction arg with
                    | Some f when not (scheduling || deliberate || inCatchOrFinally f arg) ->
                        let tokenParams =
                            f.Parameters
                            |> List.filter (fun p ->
                                match model.GetDeclaredSymbol p with
                                | null -> false
                                | ps -> isTokenType ps.Type)

                        match tokenParams with
                        | [ p ] ->
                            let name = p.Identifier.ValueText

                            let alreadyPassed =
                                inv.ArgumentList.Arguments
                                |> Seq.exists (fun a -> a.Expression.ToString() = name)

                            let namedArguments =
                                inv.ArgumentList.Arguments |> Seq.exists (fun a -> not (isNull a.NameColon))

                            // the parameter at that position is a token
                            let positionIsToken =
                                match model.GetSymbolInfo(inv).Symbol with
                                | :? IMethodSymbol as m ->
                                    let i = inv.ArgumentList.Arguments.IndexOf arg
                                    i < m.Parameters.Length && isTokenType m.Parameters.[i].Type
                                | _ -> false

                            if alreadyPassed || namedArguments || not positionIsToken then
                                None
                            else
                                let edit = Suggestion.replace arg.Expression.Span name

                                if Guards.speculativeCheck model [ edit ] then
                                    Some
                                        {
                                            Code = TokenCode
                                            Message =
                                                $"'{name}' is in scope: pass it, or the callee has nothing to cancel on"
                                            Span = arg.Span
                                            Fixes = [ Suggestion.fix ("Pass " + name) TokenCode [ edit ] ]
                                        }
                                else
                                    None
                        | _ -> None
                    | _ -> None
                | _ -> None
        | _ -> None)
    |> List.ofSeq

let analyze (tree: SyntaxTree) (model: SemanticModel) (_ctx: RuleContext) : Suggestion list =
    blocking tree model
    @ forgottenTasks tree model
    @ elideAsync tree model
    @ asyncVoidLambdas tree model
    @ singleTasks tree model
    @ tokens tree model
