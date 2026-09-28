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
/// async and sync worlds and gets a note; where the drained method's own
/// type declares a synchronous sibling (`LoadAsync` → `Load`, same
/// arguments, no task returned), the editor offers the swap, and
/// `csharp_refactor.CR0040.sync_swap = true` lets a sweep apply it. Guards: the receiver is typed `Task`/`Task<T>`/`ValueTask`/
/// `ValueTask<T>`; the site is not inside a `lock`, a `catch` filter, a
/// `finally`, an `unsafe` block, a non-`async` lambda or a local function
/// (each is its own boundary; only the innermost function attributes a
/// site); a `catch (AggregateException)` around the site stands the fix
/// down — `.Result` and `Wait()` throw the wrapper, `await` the inner
/// exception, and the handler would go dead — and so does a `catch
/// (Exception)` or bare `catch` whose filter or body reads the wrapper
/// (`InnerException`, `InnerExceptions`, `Flatten`, `is
/// AggregateException`); a task known complete (under
/// its own `IsCompleted` test — an `if`/`?:` branch or the right operand of
/// `&&`/`||`, negation followed — or compared equal to the winner of
/// `await Task.WhenAny(…, t, …)`, born of `Task.FromResult`/`CompletedTask`,
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
/// `WhenAny`/`WaitAny` of one task are left: `WhenAny`'s task never
/// faults and `WaitAny` returns an index, where `t`/`t.Wait()` would
/// throw on a faulted `t`. Yields to CA1842/CA1843.
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

[<Literal>]
let OmittedTokenCode = "CR0189"

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

/// A `try` around the node whose handler names `AggregateException`, or
/// can catch one (`Exception`, or no type) and reads the wrapper — its
/// `InnerException`, `InnerExceptions`, `Flatten`, or the type by name in
/// the filter or body: after `await` it sees the inner exception instead.
let underAggregateCatch (fn: Function) (node: SyntaxNode) =
    let catchesWrapper (c: CatchClauseSyntax) =
        let named =
            not (isNull c.Declaration)
            && c.Declaration.Type.ToString().EndsWith "AggregateException"

        let broad =
            isNull c.Declaration
            || (match c.Declaration.Type.ToString() with
                | "Exception"
                | "System.Exception"
                | "global::System.Exception" -> true
                | _ -> false)

        let readsWrapper () =
            c.DescendantTokens()
            |> Seq.exists (fun t ->
                t.IsKind SyntaxKind.IdentifierToken
                && (match t.ValueText with
                    | "InnerException"
                    | "InnerExceptions"
                    | "Flatten"
                    | "AggregateException" -> true
                    | _ -> false))

        named || (broad && readsWrapper ())

    node.Ancestors()
    |> Seq.takeWhile (fun a -> not (obj.ReferenceEquals(a, fn.Node)))
    |> Seq.exists (fun a ->
        match a with
        | :? TryStatementSyntax as t -> t.Block.Span.Contains node.Span && t.Catches |> Seq.exists catchesWrapper
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

/// The properties that read `true` only on a finished task.
let private completionProbes =
    set [ "IsCompleted"; "IsCompletedSuccessfully"; "IsFaulted"; "IsCanceled" ]

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

    // `winner` of `var winner = await Task.WhenAny(…, t, …);`, never assigned
    // again: `winner == t` proves `t` complete
    let whenAnyArguments (winner: ExpressionSyntax) =
        match winner with
        | :? IdentifierNameSyntax as id ->
            match model.GetSymbolInfo(id).Symbol with
            | :? ILocalSymbol as l ->
                let reassigned =
                    fn.Node.DescendantNodes()
                    |> Seq.exists (fun n ->
                        match n with
                        | :? AssignmentExpressionSyntax as a ->
                            SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(a.Left).Symbol, l)
                        | _ -> false)

                if reassigned then
                    []
                else
                    l.DeclaringSyntaxReferences
                    |> Seq.collect (fun r ->
                        match r.GetSyntax() with
                        | :? VariableDeclaratorSyntax as v when not (isNull v.Initializer) ->
                            match v.Initializer.Value with
                            | :? AwaitExpressionSyntax as a ->
                                // `.ConfigureAwait(false)` sits on the WhenAny call
                                let call =
                                    match a.Expression with
                                    | :? InvocationExpressionSyntax as ca when
                                        (match ca.Expression with
                                         | :? MemberAccessExpressionSyntax as m ->
                                             m.Name.Identifier.ValueText = "ConfigureAwait"
                                         | _ -> false)
                                        ->
                                        (ca.Expression :?> MemberAccessExpressionSyntax).Expression
                                    | e -> e

                                match call with
                                | :? InvocationExpressionSyntax as inv when inv.Expression.ToString() = "Task.WhenAny" ->
                                    inv.ArgumentList.Arguments
                                    |> Seq.collect (fun arg ->
                                        match arg.Expression with
                                        | :? ImplicitArrayCreationExpressionSyntax as arr ->
                                            arr.Initializer.Expressions |> Seq.map string
                                        | :? ArrayCreationExpressionSyntax as arr when not (isNull arr.Initializer) ->
                                            arr.Initializer.Expressions |> Seq.map string
                                        | :? CollectionExpressionSyntax as c ->
                                            c.Elements
                                            |> Seq.choose (fun e ->
                                                match e with
                                                | :? ExpressionElementSyntax as x -> Some(string x.Expression)
                                                | _ -> None)
                                        | e -> Seq.singleton (string e))
                                | _ -> Seq.empty
                            | _ -> Seq.empty
                        | _ -> Seq.empty)
                    |> List.ofSeq
            | _ -> []
        | _ -> []

    // the receivers a condition proves complete when it is true, and when
    // it is false: `t.IsCompleted` (or `IsCompletedSuccessfully`,
    // `IsFaulted`, `IsCanceled`), a WhenAny winner compared to `t`, through
    // `&&`, `||` and `!`
    let rec completionTests (cond: ExpressionSyntax) : string list * string list =
        match cond with
        | :? ParenthesizedExpressionSyntax as p -> completionTests p.Expression
        | :? BinaryExpressionSyntax as b when b.IsKind SyntaxKind.LogicalAndExpression ->
            fst (completionTests b.Left) @ fst (completionTests b.Right), []
        | :? BinaryExpressionSyntax as b when b.IsKind SyntaxKind.LogicalOrExpression ->
            [], snd (completionTests b.Left) @ snd (completionTests b.Right)
        | :? PrefixUnaryExpressionSyntax as u when u.IsKind SyntaxKind.LogicalNotExpression ->
            let t, f = completionTests u.Operand
            f, t
        | :? MemberAccessExpressionSyntax as m when completionProbes.Contains m.Name.Identifier.ValueText ->
            [ m.Expression.ToString() ], []
        | :? BinaryExpressionSyntax as b when
            b.IsKind SyntaxKind.EqualsExpression || b.IsKind SyntaxKind.NotEqualsExpression
            ->
            let compared =
                [
                    if List.contains (b.Right.ToString()) (whenAnyArguments b.Left) then
                        b.Right.ToString()
                    if List.contains (b.Left.ToString()) (whenAnyArguments b.Right) then
                        b.Left.ToString()
                ]

            if b.IsKind SyntaxKind.EqualsExpression then
                compared, []
            else
                [], compared
        | _ -> [], []

    // under a completion test: the then-branch of `if (t.IsCompleted)`, the
    // right operand of `t.IsCompleted && …`, their negated twins
    let underCompletedTest =
        site.Ancestors()
        |> Seq.takeWhile (fun a -> not (obj.ReferenceEquals(a, fn.Node)))
        |> Seq.exists (fun a ->
            let proves (cond: ExpressionSyntax) (branch: SyntaxNode) whenTrue =
                not (isNull branch)
                && branch.Span.Contains site.Span
                && List.contains text ((if whenTrue then fst else snd) (completionTests cond))

            match a with
            | :? IfStatementSyntax as ifs ->
                proves ifs.Condition ifs.Statement true
                || (not (isNull ifs.Else) && proves ifs.Condition ifs.Else.Statement false)
            | :? ConditionalExpressionSyntax as c ->
                proves c.Condition c.WhenTrue true || proves c.Condition c.WhenFalse false
            | :? BinaryExpressionSyntax as b when b.IsKind SyntaxKind.LogicalAndExpression ->
                proves b.Left b.Right true
            | :? BinaryExpressionSyntax as b when b.IsKind SyntaxKind.LogicalOrExpression ->
                proves b.Left b.Right false
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

/// The swap of a boundary drain for the call's synchronous sibling -
/// `x.LoadAsync(p).Result` → `x.Load(p)`, `x.SaveAsync(p).Wait();` →
/// `x.Save(p);` - where the method's own type declares an ordinary `Load`
/// of the same staticness taking those arguments and returning no task:
/// the drain's value type for a `.Result`, anything for a `.Wait()`.
/// Verified against the model, never guessed from the name alone, and
/// checked to compile. It walks the code AWAY from async (FR0049's twin),
/// so it is an editor action unless `sync_swap` opts a sweep in.
let private syncSiblingFix (syncSwap: bool) (model: SemanticModel) (drain: Drain) : Fix option =
    let swap (receiver: ExpressionSyntax) (replaceSpan: TextSpan) (valueType: ITypeSymbol option) =
        match receiver with
        | :? InvocationExpressionSyntax as inv ->
            match model.GetSymbolInfo(inv).Symbol with
            | :? IMethodSymbol as m when m.Name.EndsWith "Async" && m.Name.Length > "Async".Length ->
                let name = m.Name.Substring(0, m.Name.Length - "Async".Length)
                let argCount = inv.ArgumentList.Arguments.Count

                let fits (s: ISymbol) =
                    match s with
                    | :? IMethodSymbol as sibling ->
                        sibling.MethodKind = MethodKind.Ordinary
                        && sibling.IsStatic = m.IsStatic
                        && sibling.Arity = m.Arity
                        && not (isTaskLike sibling.ReturnType)
                        && (sibling.Parameters
                            |> Seq.filter (fun p -> not (p.IsOptional || p.IsParams))
                            |> Seq.length)
                           <= argCount
                        && argCount <= sibling.Parameters.Length
                        && (match valueType with
                            | Some t -> SymbolEqualityComparer.Default.Equals(sibling.ReturnType, t)
                            | None -> true)
                    | _ -> false

                let siblings = m.ContainingType.GetMembers name |> Seq.filter fits |> List.ofSeq

                // the rewritten call must BIND to one of them where it stands: a
                // derived type's better overload (`Derived.Load(object)` for
                // `d.Load(1)`) or a local function named `Load` would take the
                // call instead, compile, and run something else
                let bindsToSibling (callText: string) =
                    let call = SyntaxFactory.ParseExpression callText

                    match
                        model
                            .GetSpeculativeSymbolInfo(inv.SpanStart, call, SpeculativeBindingOption.BindAsExpression)
                            .Symbol
                    with
                    | :? IMethodSymbol as bound ->
                        siblings
                        |> List.exists (fun s ->
                            SymbolEqualityComparer.Default.Equals(bound.OriginalDefinition, s.OriginalDefinition))
                    | _ -> false

                if not siblings.IsEmpty then
                    let callee =
                        match inv.Expression with
                        | :? MemberAccessExpressionSyntax as ma ->
                            let typeArgs =
                                match ma.Name with
                                | :? GenericNameSyntax as g -> g.TypeArgumentList.ToString()
                                | _ -> ""

                            Some $"{ma.Expression}.{name}{typeArgs}"
                        | :? IdentifierNameSyntax -> Some name
                        | :? GenericNameSyntax as g -> Some(name + g.TypeArgumentList.ToString())
                        | _ -> None

                    callee
                    |> Option.map (fun c -> c + inv.ArgumentList.ToString())
                    |> Option.filter bindsToSibling
                    |> Option.map (fun callText -> Suggestion.replace replaceSpan callText)
                    |> Option.filter (fun edit -> Guards.speculativeCheck model [ edit ])
                    |> Option.map (fun edit ->
                        let fix = Suggestion.fix $"Call the synchronous {name}" BlockingCode [ edit ]
                        if syncSwap then fix else Suggestion.editorOnly fix)
                else
                    None
            | _ -> None
        | _ -> None

    match drain with
    | Value(s, r) ->
        // the drained value's type: Task<T>'s T
        match model.GetTypeInfo(r).Type with
        | :? INamedTypeSymbol as t when t.TypeArguments.Length = 1 -> swap r s.Span (Some t.TypeArguments.[0])
        | _ -> None
    | Wait(s, r) -> swap r s.Expression.Span None
    | WaitAll _
    | RunDrain _ -> None

let private blocking (syncSwap: bool) (tree: SyntaxTree) (model: SemanticModel) : Suggestion list =
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
                let message =
                    "A sync-over-async boundary: the thread blocks on the task here; make the caller async (CR0041) or keep it as the one deliberate blocking point"

                // `.Result` and `.Wait()` throw the fault wrapped in an
                // AggregateException, the sibling throws it bare: a `catch
                // (AggregateException)` around the drain would go dead
                let swap =
                    match fn with
                    | Some f when underAggregateCatch f site -> None
                    | _ -> syncSiblingFix syncSwap model drain

                match swap with
                | Some fix ->
                    Some
                        {
                            Code = BlockingCode
                            Message = message + ", or call the method's synchronous sibling"
                            Span = site.Span
                            Fixes = [ fix ]
                        }
                | None -> Some(Suggestion.note BlockingCode message site.Span))

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
            // never WhenAny/WaitAny: WhenAny's task never faults and WaitAny
            // returns an index — the task itself would throw where they did not
            (let e = inv.Expression.ToString()
             e = "Task.WhenAll" || e = "Task.WaitAll")
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

// ---- CR0189 ----

/// A task, a value task, an async stream or anything with `GetAwaiter`: a
/// value whose work may outlive the call that started it.
let private awaitable (t: ITypeSymbol) =
    not (isNull t)
    && (match t.OriginalDefinition.ToDisplayString() with
        | "System.Threading.Tasks.Task"
        | "System.Threading.Tasks.Task<TResult>"
        | "System.Threading.Tasks.ValueTask"
        | "System.Threading.Tasks.ValueTask<TResult>"
        | "System.Collections.Generic.IAsyncEnumerable<T>" -> true
        | _ -> not (t.GetMembers "GetAwaiter").IsEmpty)

/// Is the task a call starts waited for by this function or its caller -
/// awaited (through `ConfigureAwait`), returned, `await foreach`'d, blocked
/// on (`.Result`, `.Wait()`, `.GetAwaiter().GetResult()`), handed to
/// `Task.WhenAll`/`WhenAny`, or held in a local that is? Anything else - a
/// discard, `_ = SendAsync(m);`, a store, an argument elsewhere - may be
/// fire-and-forget: work meant to outlive the caller, which the caller's
/// token would cancel when the caller is done.
let private taskWaitedFor (model: SemanticModel) (body: SyntaxNode) (call: ExpressionSyntax) =
    let rec consumed (depth: int) (e: ExpressionSyntax) =
        // `(e)`, `e.ConfigureAwait(false)`, `e.WithCancellation(…)` stand for e
        let rec outer (e: ExpressionSyntax) =
            match e.Parent with
            | :? ParenthesizedExpressionSyntax as p -> outer p
            | :? MemberAccessExpressionSyntax as ma when
                obj.ReferenceEquals(ma.Expression, e)
                && (ma.Name.Identifier.ValueText = "ConfigureAwait"
                    || ma.Name.Identifier.ValueText = "WithCancellation")
                && (ma.Parent :? InvocationExpressionSyntax)
                ->
                outer (ma.Parent :?> ExpressionSyntax)
            | _ -> e

        let e = outer e

        match e.Parent with
        | :? AwaitExpressionSyntax
        | :? ReturnStatementSyntax
        | :? ArrowExpressionClauseSyntax -> true
        | :? ForEachStatementSyntax as fe -> fe.AwaitKeyword.IsKind SyntaxKind.AwaitKeyword
        | :? MemberAccessExpressionSyntax as ma when obj.ReferenceEquals(ma.Expression, e) ->
            match ma.Name.Identifier.ValueText with
            | "Result"
            | "Wait" -> true
            | "GetAwaiter" ->
                match ma.Parent with
                | :? InvocationExpressionSyntax as ga ->
                    match ga.Parent with
                    | :? MemberAccessExpressionSyntax as gr -> gr.Name.Identifier.ValueText = "GetResult"
                    | _ -> false
                | _ -> false
            | _ -> false
        | :? ArgumentSyntax as arg when depth < 3 ->
            // Task.WhenAll(a, b) / WhenAny, itself waited for
            match arg.Parent, arg.Parent.Parent with
            | :? ArgumentListSyntax, (:? InvocationExpressionSyntax as combinator) ->
                match model.GetSymbolInfo(combinator).Symbol with
                | :? IMethodSymbol as cm when
                    (cm.Name = "WhenAll" || cm.Name = "WhenAny")
                    && cm.ContainingType.ToDisplayString() = "System.Threading.Tasks.Task"
                    ->
                    consumed (depth + 1) combinator
                | _ -> false
            | _ -> false
        | :? EqualsValueClauseSyntax as init when depth < 3 ->
            // `var t = SendAsync(m);` then `await t`: the local's uses decide
            match init.Parent with
            | :? VariableDeclaratorSyntax as v ->
                match model.GetDeclaredSymbol v with
                | :? ILocalSymbol as local ->
                    body.DescendantNodes()
                    |> Seq.exists (fun n ->
                        match n with
                        | :? IdentifierNameSyntax as id when
                            id.Identifier.ValueText = local.Name
                            && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(id).Symbol, local)
                            ->
                            consumed (depth + 1) id
                        | _ -> false)
                | _ -> false
            | _ -> false
        | _ -> false

    consumed 0 call

/// CR0189 (correctness, fix): a call that omits the `CancellationToken` in
/// scope, where the callee takes one - `await repo.GetAsync(id)` inside
/// `Task Load(int id, CancellationToken ct)` - runs to the end whatever the
/// caller cancels: `await repo.GetAsync(id, ct)`. The F# side's FR0118 (the
/// omitted-token half; CR0055 is the explicit `None`, CR0170 the loop). The
/// callee takes it as an optional token parameter left out (passed by name
/// when other optional parameters stand before it), or has an overload that is
/// the same method with a trailing `CancellationToken` added - same type,
/// same name, the same parameters in order, the same return type. Guards: as
/// CR0055 - exactly one token parameter on the enclosing function, not in a
/// `catch` or `finally` (cleanup must run after a cancel), not `Task.Run`/
/// `StartNew`/`ContinueWith` by symbol (a scheduling condition), not after
/// an `IsCancellationRequested` read earlier in the function but a loop
/// condition around the call (that code may run because of the cancel), no
/// named arguments, the token not already among the arguments; a call in a
/// lambda answers to the lambda's parameters; work the call starts is
/// waited for (`taskWaitedFor`), not fire-and-forget; not after a caught
/// OperationCanceledException; not in a `try` whose `finally` touches the
/// receiver (an acquire its `finally` releases); the call with the
/// token binds to the method expected, by a speculative bind.
/// Yields to CA2016.
let private omittedTokens (tree: SyntaxTree) (model: SemanticModel) : Suggestion list =
    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun n ->
        match n with
        | :? InvocationExpressionSyntax as inv when
            inv.ArgumentList.Arguments |> Seq.forall (fun a -> isNull a.NameColon)
            ->
            // a token handed to the scheduler is a condition on running the work at
            // all: cancelled, `Task.Run`'s body or a `ContinueWith` cleanup never
            // runs, and nothing awaits the task to say so. By symbol, however spelled
            let scheduling (m: IMethodSymbol) =
                let owner = m.ContainingType.OriginalDefinition.ToDisplayString()

                (owner = "System.Threading.Tasks.Task"
                 || owner = "System.Threading.Tasks.Task<TResult>"
                 || owner = "System.Threading.Tasks.TaskFactory"
                 || owner = "System.Threading.Tasks.TaskFactory<TResult>")
                && (m.Name = "Run"
                    || m.Name = "StartNew"
                    || m.Name = "ContinueWith"
                    || m.Name = "ContinueWhenAll"
                    || m.Name = "ContinueWhenAny")

            match enclosingFunction inv, model.GetSymbolInfo(inv).Symbol with
            | Some f, (:? IMethodSymbol as m) when
                not (scheduling m || inCatchOrFinally f inv)
                // a callback handed to a token's `Register`/`UnsafeRegister` runs
                // because of the cancel: its own token parameter is already
                // cancelled, and a wait given it throws at once (FR0118's
                // cleanup zones)
                && not (
                    match f.Node.Parent with
                    | :? ArgumentSyntax as arg ->
                        match arg.Parent.Parent with
                        | :? InvocationExpressionSyntax as reg ->
                            match model.GetSymbolInfo(reg).Symbol with
                            | :? IMethodSymbol as rm ->
                                (rm.Name = "Register" || rm.Name = "UnsafeRegister")
                                && rm.ContainingType.ToDisplayString() = "System.Threading.CancellationToken"
                            | _ -> false
                        | _ -> false
                    | _ -> false
                )
                // a `params` call spreads its arguments past the parameters
                && not (m.Parameters |> Seq.exists (fun ps -> ps.IsParams))
                && inv.ArgumentList.Arguments.Count <= m.Parameters.Length
                ->
                let tokenParams =
                    f.Parameters
                    |> List.filter (fun p ->
                        match model.GetDeclaredSymbol p with
                        | null -> false
                        | ps -> isTokenType ps.Type)

                match tokenParams with
                | [ p ] ->
                    let name = p.Identifier.ValueText
                    let args = inv.ArgumentList.Arguments

                    let alreadyPassed =
                        args |> Seq.exists (fun a -> Text.mentionsName name a.Expression)
                        || m.Parameters
                           |> Seq.take args.Count
                           |> Seq.exists (fun ps -> isTokenType ps.Type)

                    // an optional token parameter left out: the first omitted token
                    let omittedOptional =
                        m.Parameters
                        |> Seq.skip args.Count
                        |> Seq.tryFind (fun ps -> isTokenType ps.Type && ps.IsOptional)

                    // the same method with a trailing token: one overload, exactly that
                    let overload () =
                        let sameType (a: ITypeSymbol) (b: ITypeSymbol) =
                            a.OriginalDefinition.ToDisplayString() = b.OriginalDefinition.ToDisplayString()

                        let reduced = if isNull m.ReducedFrom then m else m.ReducedFrom
                        let owner = reduced.ContainingType

                        owner.GetMembers reduced.Name
                        |> Seq.tryPick (fun o ->
                            match o with
                            | :? IMethodSymbol as om when
                                om.Parameters.Length = reduced.Parameters.Length + 1
                                && om.IsStatic = reduced.IsStatic
                                && om.Arity = reduced.Arity
                                ->
                                let last = om.Parameters.[om.Parameters.Length - 1]

                                if
                                    isTokenType last.Type
                                    && sameType om.ReturnType reduced.ReturnType
                                    && Seq.forall2
                                        (fun (x: IParameterSymbol) (y: IParameterSymbol) ->
                                            sameType x.Type y.Type && x.RefKind = y.RefKind)
                                        (Seq.take reduced.Parameters.Length om.Parameters)
                                        reduced.Parameters
                                then
                                    Some om
                                else
                                    None
                            | _ -> None)

                    // every parameter the call left out is optional: appending lands on the token
                    let restOptional =
                        m.Parameters |> Seq.skip args.Count |> Seq.forall (fun ps -> ps.IsOptional)

                    // code the function reaches after reading a cancel state may run
                    // because of the cancel - `if (ct.IsCancellationRequested) { await
                    // LogAsync("cancelled"); }`, a flush after `while
                    // (!ct.IsCancellationRequested) { … }`, after `bool stopping =
                    // ct.IsCancellationRequested;` - and the token passed there throws
                    // at once. Any `IsCancellationRequested` read before the call stands
                    // it down, but the condition of a loop around it: that body runs
                    // while the token is live
                    let tokenObserved =
                        f.Body.DescendantNodes()
                        |> Seq.exists (fun x ->
                            match x with
                            | :? MemberAccessExpressionSyntax as ma when
                                ma.Name.Identifier.ValueText = "IsCancellationRequested"
                                && ma.SpanStart < inv.SpanStart
                                ->
                                let loopAround =
                                    inv.Ancestors()
                                    |> Seq.takeWhile (fun a -> not (obj.ReferenceEquals(a, f.Node)))
                                    |> Seq.exists (fun a ->
                                        let condition: ExpressionSyntax =
                                            match a with
                                            | :? WhileStatementSyntax as s -> s.Condition
                                            | :? ForStatementSyntax as s -> s.Condition
                                            | _ -> null

                                        not (isNull condition) && condition.Span.Contains ma.Span)

                                not loopAround
                            | _ -> false)

                    // the argument, and the method the call must then bind to
                    // work started and not waited for is the author's to detach
                    let fireAndForget = awaitable m.ReturnType && not (taskWaitedFor model f.Body inv)

                    // `try { await gate.WaitAsync(); … } finally { gate.Release(); }`: a
                    // cancelled acquire still reaches the release, which then gives back
                    // what was never taken - SemaphoreFullException, or a lock open to
                    // two. A call in a `try` whose `finally` touches the same receiver
                    let releasedInFinally =
                        match inv.Expression with
                        | :? MemberAccessExpressionSyntax as ma ->
                            let receiver = ma.Expression.ToString()

                            inv.Ancestors()
                            |> Seq.takeWhile (fun a -> not (obj.ReferenceEquals(a, f.Node)))
                            |> Seq.exists (fun a ->
                                match a with
                                | :? TryStatementSyntax as ts when
                                    not (isNull ts.Finally) && ts.Block.Span.Contains inv.Span
                                    ->
                                    ts.Finally.Block.DescendantNodes()
                                    |> Seq.exists (fun x ->
                                        match x with
                                        | :? MemberAccessExpressionSyntax as fm ->
                                            fm.Expression.ToString() = receiver
                                        | _ -> false)
                                | _ -> false)
                        | _ -> false

                    // after a caught cancel - `try { … } catch (OperationCanceledException)
                    // { … }` then `await FlushAsync();` - the code runs because of it
                    let afterCaughtCancel =
                        f.Body.DescendantNodes()
                        |> Seq.exists (fun x ->
                            match x with
                            | :? CatchClauseSyntax as c when
                                c.SpanStart < inv.SpanStart
                                && not (c.Span.Contains inv.Span)
                                && not (isNull c.Declaration)
                                ->
                                match model.GetTypeInfo(c.Declaration.Type).Type with
                                | null -> false
                                | t ->
                                    let rec cancels (t: ITypeSymbol) =
                                        not (isNull t)
                                        && (t.ToDisplayString() = "System.OperationCanceledException"
                                            || cancels t.BaseType)

                                    cancels t
                            | _ -> false)

                    let insertion =
                        if
                            alreadyPassed
                            || tokenObserved
                            || fireAndForget
                            || releasedInFinally
                            || afterCaughtCancel
                        then
                            None
                        else
                            match omittedOptional with
                            | Some ps when restOptional ->
                                // positional when the token is the next parameter, else by name
                                let index = m.Parameters.IndexOf ps

                                if index = args.Count then
                                    Some(name, m)
                                else
                                    Some($"{ps.Name}: {name}", m)
                            | Some _ -> None
                            | None when args.Count = m.Parameters.Length ->
                                overload () |> Option.map (fun om -> name, om)
                            | None -> None

                    let separator = if args.Count = 0 then "" else ", "

                    // the call with the token binds to that method: a derived type's
                    // `GetAsync(int, object)` hides the base overload once the call has
                    // two arguments, an inaccessible overload loses to an accessible
                    // one, an instance method outranks the extension that bound before
                    let bindsTo (argument: string) (target: IMethodSymbol) =
                        let callText = inv.ToString()
                        let at = inv.ArgumentList.CloseParenToken.SpanStart - inv.SpanStart

                        let rewritten =
                            SyntaxFactory.ParseExpression(callText.Insert(at, separator + argument))

                        let definition (x: IMethodSymbol) =
                            (if isNull x.ReducedFrom then x else x.ReducedFrom).OriginalDefinition

                        match
                            model
                                .GetSpeculativeSymbolInfo(
                                    inv.SpanStart,
                                    rewritten,
                                    SpeculativeBindingOption.BindAsExpression
                                )
                                .Symbol
                        with
                        | :? IMethodSymbol as bound ->
                            SymbolEqualityComparer.Default.Equals(definition bound, definition target)
                        | _ -> false

                    match insertion with
                    | Some(argument, target) when bindsTo argument target ->
                        let edit =
                            Suggestion.insert inv.ArgumentList.CloseParenToken.SpanStart (separator + argument)

                        if Guards.speculativeCheck model [ edit ] then
                            Some
                                {
                                    Code = OmittedTokenCode
                                    Message =
                                        $"'{name}' is in scope and '{m.Name}' takes a token: pass it, or the call runs to the end whatever is cancelled"
                                    Span = inv.Span
                                    Fixes = [ Suggestion.fix ("Pass " + name) OmittedTokenCode [ edit ] ]
                                }
                        else
                            None
                    | _ -> None
                | _ -> None
            | _ -> None
        | _ -> None)
    |> List.ofSeq

let analyze (tree: SyntaxTree) (model: SemanticModel) (ctx: RuleContext) : Suggestion list =
    // the sync-sibling swap walks code AWAY from async: an editor action the
    // author picks, or `csharp_refactor.CR0040.sync_swap = true` for a sweep
    blocking (RuleContext.knobBool ctx BlockingCode "sync_swap" false) tree model
    @ forgottenTasks tree model
    @ elideAsync tree model
    @ asyncVoidLambdas tree model
    @ singleTasks tree model
    @ tokens tree model
    @ omittedTokens tree model
