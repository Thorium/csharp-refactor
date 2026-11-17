/// Regions that must be left the way they were entered: an acquired
/// semaphore, a lazily filled static, a caught exception, a loop under a
/// cancellation token.
///
/// CR0163 (correctness, fix): `sem.Wait();` (or `await sem.WaitAsync();`,
/// `Semaphore.WaitOne`, `Mutex.WaitOne`, `ReaderWriterLockSlim.Enter*`)
/// followed by statements and the matching `Release()` in the same block,
/// with no `try` between them: an exception or an early `return` between
/// the two leaves the semaphore held for good. The statements between
/// become a `try` body, the release its `finally`. Guards: the acquire
/// and the release are own-line statements on the same receiver (by
/// symbol) with no timeout and no result used; exactly one release of
/// that receiver in the block, none in a nested `finally`; no re-acquire
/// between them; nothing between them declares a local that the code
/// after the release reads (the `try` would scope it out — a note);
/// nothing between them is a multi-line literal (re-indenting would change
/// it — a note).
///
/// CR0164 (correctness, fix): `if (_x == null) _x = new X();` on a static
/// reference-typed field, outside any `lock` — two threads build two, a
/// reader may see a half-published one. `LazyInitializer.EnsureInitialized
/// (ref _x, () => new X())` publishes exactly one. Guards: the field is
/// static, not `volatile`/`readonly`/`[ThreadStatic]`, and written only by
/// lazy initialisations (another assignment, a reset, `Array.Resize(ref _x,
/// n)` make the null test a fallback, not a cache); the assignment is
/// the whole `if` body, or the shape is `_x ??= expr` or the older `_x ??
/// (_x = expr)` as a value anywhere; `EnsureInitialized`
/// only for an expression provably non-null (it throws on a null factory
/// result): a `new`, an array or collection expression, a string, a `??`
/// with such a right side, or a not-null flow state under `#nullable`. Any
/// other expression takes the null-exact `Interlocked.CompareExchange(ref
/// _x, expr, null)` — a null result leaves `_x` null and the next call
/// loads again, as before: the `if` body's assignment becomes the exchange;
/// a `_x ??= expr;` statement (in a block only: a new `if` could capture an
/// `else`) becomes `if (_x is null) …exchange…;`; a `??=` value becomes
/// `_x ?? …exchange… ?? _x`;
/// the expression does not mention the field; no enclosing `lock` and not
/// a static constructor (both already serialise); `LazyInitializer`
/// resolves. Instance fields are not reported.
///
/// CR0165 (correctness, fix): `catch (Exception ex) { throw new
/// MyException("…"); }` — the wrapper has a `(…, Exception)` constructor
/// and the caught exception is not passed: its type, message and stack
/// are gone. The fix appends the caught exception, naming an unnamed
/// catch `ex`. Guards: the throw is a statement under the catch (through
/// blocks and `if`s only); the arguments do not mention the caught
/// exception; a constructor with the same parameters plus a trailing
/// `System.Exception` is accessible. A try that adds to `HttpHeaders` keeps
/// it a note: the header exception quotes the value (a key, a token) that
/// the wrapper's message left out. Yields to CA2200.
///
/// CR0170 (correctness, fix): a method, lambda or local function takes a
/// `CancellationToken` and holds a loop that awaits, sleeps or blocks on a task, without
/// ever reading the token — the caller's cancellation is ignored for the
/// loop's whole run. The fix puts `ct.ThrowIfCancellationRequested();` at
/// the top of the loop body. Guards: the parameter is the function's own;
/// the loop neither mentions the token nor a local built from it (a
/// `ResilienceContext` or linked source that carries it into the calls),
/// passes no explicit `CancellationToken.None`/`default` (the author chose
/// not to cancel there), and sits inside no other loop of the same function
/// (the outer loop is the granularity), nor in a `catch` or `finally`
/// (cleanup — a rollback after a cancelled copy — must not throw); the body is a
/// block with at least one statement (an expression-bodied loop is a
/// note); the body has an `await`, a `Thread.Sleep` or a block on a task.
module CSharp.Refactor.GuardedRegions

open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax
open Microsoft.CodeAnalysis.Text

[<Literal>]
let ReleaseCode = "CR0163"

[<Literal>]
let LazyStaticCode = "CR0164"

[<Literal>]
let InnerExceptionCode = "CR0165"

[<Literal>]
let TokenLoopCode = "CR0170"

let private isClosure (n: SyntaxNode) =
    n :? AnonymousFunctionExpressionSyntax || n :? LocalFunctionStatementSyntax

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

/// The indentation unit a block uses: the difference between its first
/// statement and its brace, else four spaces.
let private indentUnit (text: SourceText) (block: BlockSyntax) =
    match block.Statements |> Seq.tryHead with
    | Some s ->
        let inner = Text.leadingWhitespace text s.SpanStart
        let outer = Text.leadingWhitespace text block.OpenBraceToken.SpanStart

        if inner.Length > outer.Length && inner.StartsWith outer then
            inner.Substring outer.Length
        else
            "    "
    | None -> "    "

// ---- CR0163 ----

/// acquire method → (owner type, release method)
let private pairs =
    dict
        [
            ("System.Threading.SemaphoreSlim", "Wait"), "Release"
            ("System.Threading.SemaphoreSlim", "WaitAsync"), "Release"
            ("System.Threading.Semaphore", "WaitOne"), "Release"
            ("System.Threading.Mutex", "WaitOne"), "ReleaseMutex"
            ("System.Threading.ReaderWriterLockSlim", "EnterReadLock"), "ExitReadLock"
            ("System.Threading.ReaderWriterLockSlim", "EnterWriteLock"), "ExitWriteLock"
            ("System.Threading.ReaderWriterLockSlim", "EnterUpgradeableReadLock"), "ExitUpgradeableReadLock"
        ]

/// The invocation a statement is — through an `await`.
let private statementCall (s: StatementSyntax) : InvocationExpressionSyntax option =
    match s with
    | :? ExpressionStatementSyntax as es ->
        match es.Expression with
        | :? InvocationExpressionSyntax as inv -> Some inv
        | :? AwaitExpressionSyntax as a ->
            match a.Expression with
            | :? InvocationExpressionSyntax as inv -> Some inv
            | _ -> None
        | _ -> None
    | _ -> None

let private isTokenArg (model: SemanticModel) (a: ArgumentSyntax) =
    match model.GetTypeInfo(a.Expression).Type with
    | null -> false
    | t -> t.ToDisplayString() = "System.Threading.CancellationToken"

let private releases (tree: SyntaxTree) (model: SemanticModel) : Suggestion list =
    let text = tree.GetText()

    let holdsDirective (n: SyntaxNode) =
        n.DescendantTrivia() |> Seq.exists (fun t -> t.IsDirective)

    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun n ->
        match n with
        | :? BlockSyntax as block ->
            let statements = block.Statements |> List.ofSeq

            statements
            |> List.indexed
            |> List.tryPick (fun (i, s) ->
                match statementCall s with
                | Some inv ->
                    match inv.Expression, symbolOf model inv with
                    | (:? MemberAccessExpressionSyntax as ma), (:? IMethodSymbol as m) ->
                        let key = m.ContainingType.OriginalDefinition.ToDisplayString(), m.Name

                        let args = inv.ArgumentList.Arguments |> List.ofSeq

                        if
                            pairs.ContainsKey key
                            && (args.IsEmpty || (args.Length = 1 && isTokenArg model args.Head))
                        then
                            let releaseName = pairs.[key]
                            let receiver = ma.Expression
                            let receiverSymbol = symbolOf model receiver

                            let isReleaseCall (x: SyntaxNode) =
                                match x with
                                | :? InvocationExpressionSyntax as r ->
                                    match r.Expression with
                                    | :? MemberAccessExpressionSyntax as rma ->
                                        rma.Name.Identifier.ValueText = releaseName
                                        && r.ArgumentList.Arguments.Count = 0
                                        && Guards.sameReference model rma.Expression receiver
                                    | _ -> false
                                | _ -> false

                            let isAcquireCall (x: SyntaxNode) =
                                match x with
                                | :? InvocationExpressionSyntax as r ->
                                    match r.Expression with
                                    | :? MemberAccessExpressionSyntax as rma ->
                                        rma.Name.Identifier.ValueText = m.Name
                                        && Guards.sameReference model rma.Expression receiver
                                    | _ -> false
                                | _ -> false

                            let allReleases = block.DescendantNodes() |> Seq.filter isReleaseCall |> List.ofSeq

                            let after = statements |> List.skip (i + 1)

                            let releaseIndex =
                                after
                                |> List.tryFindIndex (fun r ->
                                    match statementCall r with
                                    | Some rc -> isReleaseCall rc
                                    | None -> false)

                            match releaseIndex with
                            | Some k when
                                k > 0
                                && allReleases.Length = 1
                                && not (isNull receiverSymbol)
                                && not (
                                    after
                                    |> List.take k
                                    |> List.exists (fun b -> b.DescendantNodes() |> Seq.exists isAcquireCall)
                                )
                                ->
                                let between = after |> List.take k
                                let release = after.[k]
                                let rest = after |> List.skip (k + 1)

                                let declared = between |> List.collect Text.declaredLocals

                                let scopedOut =
                                    declared
                                    |> List.exists (fun name -> rest |> List.exists (Text.mentionsName name))

                                let ownLine (st: StatementSyntax) =
                                    let line = text.Lines.GetLineFromPosition st.SpanStart
                                    line.ToString().Trim() = st.ToString().Trim()

                                let receiverText = receiver.ToString()

                                let message =
                                    $"'{receiverText}' is acquired here and released {k} statement(s) later with nothing guarding the way: an exception or an early return between them keeps it held for good — release it in a finally"

                                if
                                    scopedOut
                                    || between |> List.exists Text.spansLines
                                    || not (ownLine s)
                                    || not (ownLine release)
                                    || between |> List.exists holdsDirective
                                    || holdsDirective release
                                then
                                    Some(Suggestion.note ReleaseCode message s.Span)
                                else
                                    let indent = Text.leadingWhitespace text s.SpanStart
                                    let unit = indentUnit text block
                                    let nl = Text.newlineAt text s.SpanStart
                                    let acquireLine = text.Lines.GetLineFromPosition s.SpanStart
                                    let releaseLine = text.Lines.GetLineFromPosition release.SpanStart

                                    let region =
                                        TextSpan.FromBounds(acquireLine.EndIncludingLineBreak, releaseLine.Start)

                                    let reindented =
                                        text.ToString(region).Split [| '\n' |]
                                        |> Array.map (fun line ->
                                            if System.String.IsNullOrWhiteSpace line then
                                                line
                                            else
                                                unit + line)
                                        |> String.concat "\n"

                                    // the guarded run is one braced block of the author's own
                                    // (`Enter(); { … } Exit();`): its braces are the try's
                                    let ownBlock =
                                        match
                                            block.Statements
                                            |> Seq.filter (fun x ->
                                                x.SpanStart > s.Span.End && x.Span.End <= release.SpanStart)
                                            |> List.ofSeq
                                        with
                                        | [ :? BlockSyntax as b ] ->
                                            let line = text.Lines.GetLineFromPosition b.SpanStart
                                            let closing = text.Lines.GetLineFromPosition b.Span.End

                                            line.Start = region.Start
                                            && closing.EndIncludingLineBreak = region.End
                                            && text.ToString(TextSpan.FromBounds(line.Start, b.SpanStart)).Trim() = ""
                                        | _ -> false

                                    let releaseSpan =
                                        TextSpan.FromBounds(releaseLine.Start, releaseLine.EndIncludingLineBreak)

                                    let finallyText =
                                        $"{indent}finally{nl}{indent}{{{nl}{indent}{unit}{release.ToString().Trim()}{nl}{indent}}}{nl}"

                                    let edits =
                                        if ownBlock then
                                            [
                                                Suggestion.insert region.Start $"{indent}try{nl}"
                                                Suggestion.replace releaseSpan finallyText
                                            ]
                                        else
                                            [
                                                Suggestion.replace
                                                    region
                                                    ($"{indent}try{nl}{indent}{{{nl}" + reindented)
                                                Suggestion.replace releaseSpan ($"{indent}}}{nl}" + finallyText)
                                            ]

                                    Some(
                                        {
                                            Code = ReleaseCode
                                            Message = message
                                            Span = s.Span
                                            Fixes = [ Suggestion.fix "Release in a finally" ReleaseCode edits ]
                                        }
                                        |> Guards.verified model
                                    )
                            | _ -> None
                        else
                            None
                    | _ -> None
                | None -> None)
        | _ -> None)
    |> List.ofSeq

// ---- CR0164 ----

/// Every write of the field is itself a lazy initialisation — the body of
/// `if (f == null)`/`if (f is null)`, `f ??= x`, `f ?? (f = x)` — so the field
/// is a cache. Another assignment, a reset, `Array.Resize(ref f, n)` or a
/// deserialised value makes the null test a fallback (shadowsocks'
/// `_Masterkey`, `_userSettings`). Scanned in the declaring type's trees; a
/// write the compilation index counts beyond those (another type's) stands
/// the rule down too.
let private onlyLazyWrites (model: SemanticModel) (f: IFieldSymbol) =
    let compilation = model.Compilation

    let trees =
        f.ContainingType.DeclaringSyntaxReferences
        |> Seq.map (fun r -> r.SyntaxTree)
        |> Seq.filter compilation.ContainsSyntaxTree
        |> Seq.distinct
        |> List.ofSeq

    let isField (m: SemanticModel) (e: ExpressionSyntax) =
        (match e with
         | :? IdentifierNameSyntax as pid -> pid.Identifier.ValueText = f.Name
         | :? MemberAccessExpressionSyntax as ma -> ma.Name.Identifier.ValueText = f.Name
         | _ -> false)
        && SymbolEqualityComparer.Default.Equals(m.GetSymbolInfo(e).Symbol, f)

    let rec bare (e: ExpressionSyntax) =
        match e with
        | :? ParenthesizedExpressionSyntax as p -> bare p.Expression
        | _ -> e

    let isNullTest (m: SemanticModel) (cond: ExpressionSyntax) =
        match bare cond with
        | :? BinaryExpressionSyntax as b when b.IsKind SyntaxKind.EqualsExpression ->
            (b.Right.IsKind SyntaxKind.NullLiteralExpression && isField m b.Left)
            || (b.Left.IsKind SyntaxKind.NullLiteralExpression && isField m b.Right)
        | :? IsPatternExpressionSyntax as p ->
            (match p.Pattern with
             | :? ConstantPatternSyntax as c -> c.Expression.IsKind SyntaxKind.NullLiteralExpression
             | _ -> false)
            && isField m p.Expression
        | _ -> false

    // a simple assignment that is a lazy site: an `if (f == null)` body (alone,
    // or braced alone), or the right side of `f ?? (f = x)`
    let lazyAssignment (m: SemanticModel) (a: AssignmentExpressionSyntax) =
        let viaIf =
            match a.Parent with
            | :? ExpressionStatementSyntax as s ->
                let owner: SyntaxNode =
                    match s.Parent with
                    | :? BlockSyntax as b when b.Statements.Count = 1 -> b.Parent
                    | p -> p

                match owner with
                | :? IfStatementSyntax as ifs -> isNull ifs.Else && isNullTest m ifs.Condition
                | _ -> false
            | _ -> false

        let viaCoalesce =
            let rec up (n: SyntaxNode) =
                match n.Parent with
                | :? ParenthesizedExpressionSyntax as p -> up p
                | :? BinaryExpressionSyntax as b when b.IsKind SyntaxKind.CoalesceExpression ->
                    obj.ReferenceEquals(bare b.Right, a) && isField m b.Left
                | _ -> false

            up a

        viaIf || viaCoalesce

    // every write in the declaring type's trees: true for a lazy site, false otherwise
    let writes =
        trees
        |> List.collect (fun tree ->
            let m = compilation.GetSemanticModel(tree, true)

            tree.GetRoot().DescendantNodes()
            |> Seq.choose (fun n ->
                match n with
                | :? AssignmentExpressionSyntax as a when isField m a.Left ->
                    Some(
                        a.IsKind SyntaxKind.CoalesceAssignmentExpression
                        || (a.IsKind SyntaxKind.SimpleAssignmentExpression && lazyAssignment m a)
                    )
                | :? AssignmentExpressionSyntax as a when
                    (a.Left :? TupleExpressionSyntax)
                    && a.Left.DescendantNodes()
                       |> Seq.exists (fun d -> d :? ExpressionSyntax && isField m (d :?> ExpressionSyntax))
                    ->
                    Some false
                | :? ArgumentSyntax as arg when
                    not (arg.RefKindKeyword.IsKind SyntaxKind.None) && isField m arg.Expression
                    ->
                    Some false
                | :? PrefixUnaryExpressionSyntax as u when
                    (u.IsKind SyntaxKind.PreIncrementExpression
                     || u.IsKind SyntaxKind.PreDecrementExpression)
                    && isField m u.Operand
                    ->
                    Some false
                | :? PostfixUnaryExpressionSyntax as u when
                    (u.IsKind SyntaxKind.PostIncrementExpression
                     || u.IsKind SyntaxKind.PostDecrementExpression)
                    && isField m u.Operand
                    ->
                    Some false
                | _ -> None)
            |> List.ofSeq)

    List.forall id writes
    && (f.DeclaredAccessibility = Accessibility.Private
        || (Index.writesOf (Index.ofCompilation compilation) f).Length <= writes.Length)

let private staticCacheField (model: SemanticModel) (e: ExpressionSyntax) : IFieldSymbol option =
    match e with
    | :? IdentifierNameSyntax
    | :? MemberAccessExpressionSyntax ->
        match symbolOf model e with
        | :? IFieldSymbol as f when
            f.IsStatic
            && not f.IsReadOnly
            && not f.IsVolatile
            && not f.IsConst
            && f.Type.IsReferenceType
            && not (
                f.GetAttributes()
                |> Seq.exists (fun a ->
                    not (isNull a.AttributeClass) && a.AttributeClass.Name = "ThreadStaticAttribute")
            )
            && onlyLazyWrites model f
            ->
            Some f
        | _ -> None
    | _ -> None

[<TailCall>]
let rec private provablyNonNull (model: SemanticModel) (e: ExpressionSyntax) =
    match e with
    | :? ParenthesizedExpressionSyntax as p -> provablyNonNull model p.Expression
    | :? ObjectCreationExpressionSyntax
    | :? ImplicitObjectCreationExpressionSyntax
    | :? ArrayCreationExpressionSyntax
    | :? ImplicitArrayCreationExpressionSyntax
    | :? CollectionExpressionSyntax
    | :? InterpolatedStringExpressionSyntax
    | :? AnonymousObjectCreationExpressionSyntax -> true
    | :? LiteralExpressionSyntax as l -> l.IsKind SyntaxKind.StringLiteralExpression
    | :? BinaryExpressionSyntax as b when b.IsKind SyntaxKind.CoalesceExpression -> provablyNonNull model b.Right
    | _ ->
        let context = model.GetNullableContext e.SpanStart

        context.HasFlag NullableContext.AnnotationsEnabled
        && model.GetTypeInfo(e).Nullability.FlowState = NullableFlowState.NotNull

let private lazyStatics (tree: SyntaxTree) (model: SemanticModel) : Suggestion list =
    let text = tree.GetText()

    let available =
        not (isNull (model.Compilation.GetTypeByMetadataName "System.Threading.LazyInitializer"))

    let serialised (n: SyntaxNode) =
        n.Ancestors()
        |> Seq.exists (fun a ->
            match a with
            | :? LockStatementSyntax -> true
            | :? ConstructorDeclarationSyntax as c ->
                c.Modifiers |> Seq.exists (fun t -> t.IsKind SyntaxKind.StaticKeyword)
            | _ -> false)

    let call (fieldText: string) (expr: ExpressionSyntax) =
        $"LazyInitializer.EnsureInitialized(ref {fieldText}, () => {expr})"

    // the null-exact spelling for a factory that may answer null (where
    // `EnsureInitialized` would throw): a null result leaves the field null
    // and the next call loads again, as the check-then-assign did
    let exchange (fieldText: string) (expr: ExpressionSyntax) =
        $"Interlocked.CompareExchange(ref {fieldText}, {expr}, null)"

    // the `using System.Threading;` the bare name needs, or nothing to add
    let imports (position: int) (typeName: string) =
        Usings.importEdit model tree position "System.Threading" typeName

    let suggestion (fieldText: string) (span: TextSpan) (viaExchange: bool) (position: int) (edits: TextEdit list) =
        let typeName, how =
            if viaExchange then
                "Interlocked", "Interlocked.CompareExchange"
            else
                "LazyInitializer", "LazyInitializer.EnsureInitialized"

        {
            Code = LazyStaticCode
            Message =
                $"'{fieldText}' is filled by check-then-assign on a static field: two threads can build two values and a reader can see a half-published one — {how} publishes exactly one"
            Span = span
            Fixes =
                [
                    Suggestion.fix
                        $"Initialise with {how}"
                        LazyStaticCode
                        ((defaultArg (imports position typeName) []) @ edits)
                ]
        }
        |> Guards.verified model

    let nullTest (cond: ExpressionSyntax) : ExpressionSyntax option =
        match cond with
        | :? BinaryExpressionSyntax as b when b.IsKind SyntaxKind.EqualsExpression ->
            if b.Right.IsKind SyntaxKind.NullLiteralExpression then
                Some b.Left
            elif b.Left.IsKind SyntaxKind.NullLiteralExpression then
                Some b.Right
            else
                None
        | :? IsPatternExpressionSyntax as p ->
            match p.Pattern with
            | :? ConstantPatternSyntax as c when c.Expression.IsKind SyntaxKind.NullLiteralExpression ->
                Some p.Expression
            | _ -> None
        | _ -> None

    if not available then
        []
    else
        tree.GetRoot().DescendantNodes()
        |> Seq.choose (fun n ->
            match n with
            | :? IfStatementSyntax as s when isNull s.Else && not (serialised s) ->
                match nullTest s.Condition with
                | None -> None
                | Some target ->
                    match staticCacheField model target with
                    | None -> None
                    | Some field ->
                        let body: StatementSyntax =
                            match s.Statement with
                            | :? BlockSyntax as b when b.Statements.Count = 1 -> b.Statements.[0]
                            | other -> other

                        match body with
                        | :? ExpressionStatementSyntax as es ->
                            match es.Expression with
                            | :? AssignmentExpressionSyntax as a when
                                a.IsKind SyntaxKind.SimpleAssignmentExpression
                                && Guards.sameReference model a.Left target
                                && not (Text.mentionsName field.Name a.Right)
                                && not (provablyNonNull model a.Right)
                                ->
                                // the assignment alone becomes the exchange; the test
                                // and a following `return field;` stay as they are
                                let fieldText = target.ToString()

                                Some(
                                    suggestion
                                        fieldText
                                        s.Span
                                        true
                                        s.SpanStart
                                        [ Suggestion.replace a.Span (exchange fieldText a.Right) ]
                                )
                            | :? AssignmentExpressionSyntax as a when
                                a.IsKind SyntaxKind.SimpleAssignmentExpression
                                && Guards.sameReference model a.Left target
                                && not (Text.mentionsName field.Name a.Right)
                                ->
                                let fieldText = target.ToString()
                                let callText = call fieldText a.Right

                                // a `return field;` right after folds in
                                let next =
                                    match s.Parent with
                                    | :? BlockSyntax as b ->
                                        let i = b.Statements.IndexOf s

                                        if i >= 0 && i + 1 < b.Statements.Count then
                                            match b.Statements.[i + 1] with
                                            | :? ReturnStatementSyntax as r when
                                                not (isNull r.Expression)
                                                && Guards.sameReference model r.Expression target
                                                ->
                                                Some r
                                            | _ -> None
                                        else
                                            None
                                    | _ -> None

                                let edits =
                                    match next with
                                    | Some r ->
                                        [
                                            Suggestion.replace
                                                (TextSpan.FromBounds(s.SpanStart, r.Span.End))
                                                $"return {callText};"
                                        ]
                                    | None -> [ Suggestion.replace s.Span $"{callText};" ]

                                Some(suggestion fieldText s.Span false s.SpanStart edits)
                            | _ -> None
                        | _ -> None
            | :? AssignmentExpressionSyntax as a when
                a.IsKind SyntaxKind.CoalesceAssignmentExpression && not (serialised a)
                ->
                match staticCacheField model a.Left with
                | Some field when
                    not (Text.mentionsName field.Name a.Right)
                    && (a.Parent :? ExpressionStatementSyntax
                        || a.Parent :? ReturnStatementSyntax
                        || a.Parent :? ArrowExpressionClauseSyntax)
                    ->
                    let fieldText = a.Left.ToString()

                    if provablyNonNull model a.Right then
                        Some(
                            suggestion
                                fieldText
                                a.Span
                                false
                                a.SpanStart
                                [ Suggestion.replace a.Span (call fieldText a.Right) ]
                        )
                    else
                        match a.Parent with
                        // a statement: the test first, so the factory runs only on a miss;
                        // only in a block, where the new `if` cannot capture an `else`
                        | :? ExpressionStatementSyntax as es when (es.Parent :? BlockSyntax) ->
                            // braced (StyleCop SA1503): on lines of its own when the
                            // statement had its line, else inline in a one-line block
                            let indent = Text.leadingWhitespace text es.SpanStart
                            let unit = Text.indentStep text es
                            let nl = Text.newlineAt text es.SpanStart
                            let body = $"{exchange fieldText a.Right};"

                            let alone =
                                text.Lines.GetLineFromPosition(es.SpanStart).ToString().Trim() = es.ToString().Trim()

                            let replacement =
                                if alone then
                                    $"if ({fieldText} is null){nl}{indent}{{{nl}{indent}{unit}{body}{nl}{indent}}}"
                                else
                                    $"if ({fieldText} is null) {{ {body} }}"

                            Some(
                                suggestion fieldText a.Span true a.SpanStart [ Suggestion.replace es.Span replacement ]
                            )
                        | :? ExpressionStatementSyntax -> None
                        // a value: the field when set, else the exchange's answer — null
                        // when this call won (then the field is what it stored)
                        | _ ->
                            Some(
                                suggestion
                                    fieldText
                                    a.Span
                                    true
                                    a.SpanStart
                                    [
                                        Suggestion.replace
                                            a.Span
                                            $"{fieldText} ?? {exchange fieldText a.Right} ?? {fieldText}"
                                    ]
                            )
                | _ -> None
            // `_x ?? (_x = expr)`: the same check-then-assign, as a value
            | :? BinaryExpressionSyntax as b when
                b.IsKind SyntaxKind.CoalesceExpression
                && not (Text.crossesDirective b)
                && not (serialised b)
                ->
                let rec bare (e: ExpressionSyntax) =
                    match e with
                    | :? ParenthesizedExpressionSyntax as p -> bare p.Expression
                    | _ -> e

                match staticCacheField model b.Left, bare b.Right with
                | Some field, (:? AssignmentExpressionSyntax as a) when
                    a.IsKind SyntaxKind.SimpleAssignmentExpression
                    && Guards.sameReference model a.Left b.Left
                    && not (Text.mentionsName field.Name a.Right)
                    // the factory becomes a lambda, or runs on a path it did not: no `await` in it
                    && not (
                        a.Right.DescendantNodesAndSelf()
                        |> Seq.exists (fun d -> d :? AwaitExpressionSyntax)
                    )
                    ->
                    let fieldText = b.Left.ToString()

                    let replacement, viaExchange =
                        if provablyNonNull model a.Right then
                            call fieldText a.Right, false
                        else
                            $"{fieldText} ?? {exchange fieldText a.Right} ?? {fieldText}", true

                    Some(suggestion fieldText b.Span viaExchange b.SpanStart [ Suggestion.replace b.Span replacement ])
                | _ -> None
            | _ -> None)
        |> List.ofSeq

// ---- CR0165 ----

let private innerExceptions (tree: SyntaxTree) (model: SemanticModel) : Suggestion list =
    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun n ->
        match n with
        | :? ThrowStatementSyntax as t when not (isNull t.Expression) ->
            match t.Expression with
            | :? ObjectCreationExpressionSyntax as c when
                not (isNull c.ArgumentList) && c.ArgumentList.Arguments.Count > 0
                ->
                // the catch, through blocks and ifs only
                let handler =
                    t.Ancestors()
                    |> Seq.takeWhile (fun a ->
                        a :? BlockSyntax
                        || a :? IfStatementSyntax
                        || a :? ElseClauseSyntax
                        || a :? CatchClauseSyntax)
                    |> Seq.tryPick (fun a ->
                        match a with
                        | :? CatchClauseSyntax as cc -> Some cc
                        | _ -> None)

                match handler, symbolOf model c with
                | Some cc, (:? IMethodSymbol as ctor) ->
                    let binder =
                        if isNull cc.Declaration || cc.Declaration.Identifier.IsKind SyntaxKind.None then
                            None
                        else
                            Some cc.Declaration.Identifier.ValueText

                    let args = c.ArgumentList.Arguments |> List.ofSeq

                    let mentioned =
                        match binder with
                        | Some b -> Text.mentionsName b c
                        | None -> false

                    let named = args |> List.exists (fun a -> not (isNull a.NameColon))

                    let sibling =
                        ctor.ContainingType.InstanceConstructors
                        |> Seq.tryFind (fun o ->
                            o.Parameters.Length = ctor.Parameters.Length + 1
                            && (Seq.zip o.Parameters ctor.Parameters
                                |> Seq.forall (fun (a, b) -> sameSymbol a.Type b.Type))
                            && (let last = o.Parameters.[o.Parameters.Length - 1].Type
                                last.Name = "Exception" && last.ContainingNamespace.ToDisplayString() = "System")
                            && model.IsAccessible(t.SpanStart, o))

                    if mentioned || named || ctor.Parameters.Length <> args.Length then
                        None
                    else
                        match sibling with
                        | None -> None
                        | Some _ ->
                            // the catch's own name, else one free in the catch and
                            // the member (no name free: no fix)
                            let named =
                                match binder with
                                | Some b -> Some(b, [])
                                | None ->
                                    Text.catchBinderName cc
                                    |> Option.map (fun fresh ->
                                        if isNull cc.Declaration then
                                            let exceptionType =
                                                Guards.typeText model cc.SpanStart "System" "Exception"

                                            fresh,
                                            [
                                                Suggestion.insert
                                                    cc.CatchKeyword.Span.End
                                                    $" ({exceptionType} {fresh})"
                                            ]
                                        else
                                            fresh, [ Suggestion.insert cc.Declaration.Type.Span.End $" {fresh}" ])

                            // the try adds to HTTP headers: their FormatException quotes the
                            // value ("The format of value '…' is invalid"), often a key or a
                            // token, which a message naming only the header left out
                            let headerValues =
                                match cc.Parent with
                                | :? TryStatementSyntax as ts ->
                                    ts.Block.DescendantNodes()
                                    |> Seq.exists (fun x ->
                                        match x with
                                        // the calls that take a header's value; a read
                                        // (`GetValues`, `Contains`) or a `Remove` quotes none
                                        | :? InvocationExpressionSyntax as inv ->
                                            match symbolOf model inv with
                                            | :? IMethodSymbol as m when
                                                m.Name = "Add"
                                                || m.Name = "TryAddWithoutValidation"
                                                || m.Name = "Parse"
                                                || m.Name = "ParseAdd"
                                                ->
                                                let rec headers (t: ITypeSymbol) =
                                                    not (isNull t)
                                                    && ((not (isNull t.ContainingNamespace)
                                                         && t.ContainingNamespace.ToDisplayString() =
                                                             "System.Net.Http.Headers")
                                                        || headers t.BaseType)

                                                headers m.ContainingType
                                            | _ -> false
                                        // `new AuthenticationHeaderValue(key)`
                                        | :? ObjectCreationExpressionSyntax as created ->
                                            match model.GetTypeInfo(created).Type with
                                            | null -> false
                                            | t ->
                                                t.Name.EndsWith "HeaderValue"
                                                && not (isNull t.ContainingNamespace)
                                                && t.ContainingNamespace.ToDisplayString() = "System.Net.Http.Headers"
                                        | _ -> false)
                                | _ -> false

                            let fixes =
                                match named with
                                | Some _ when headerValues -> []
                                | Some(name, nameEdits) ->
                                    let edits =
                                        nameEdits
                                        @ [
                                            Suggestion.insert
                                                (List.last args).Span.End
                                                (Text.appendedArgument (tree.GetText()) c.ArgumentList name)
                                        ]

                                    [
                                        Suggestion.fix
                                            "Pass the caught exception as the inner exception"
                                            InnerExceptionCode
                                            edits
                                    ]
                                | None -> []

                            Some(
                                {
                                    Code = InnerExceptionCode
                                    Message =
                                        if headerValues then
                                            $"The new {c.Type} drops the caught exception: the try adds HTTP headers, and their exception quotes the value, which may be a secret — pass it as the inner exception only if that value may be logged"
                                        else
                                            $"The new {c.Type} drops the caught exception: its type, message and stack are gone — pass it as the inner exception"
                                    Span = t.Span
                                    Fixes = fixes
                                }
                                |> Guards.verified model
                            )
                | _ -> None
            | _ -> None
        | _ -> None)
    |> List.ofSeq

// ---- CR0170 ----

let private tokenLoops (tree: SyntaxTree) (model: SemanticModel) : Suggestion list =
    let text = tree.GetText()

    let loopParts (s: SyntaxNode) : (StatementSyntax * StatementSyntax) option =
        match s with
        | :? ForStatementSyntax as f -> Some(f :> StatementSyntax, f.Statement)
        | :? ForEachStatementSyntax as f -> Some(f :> StatementSyntax, f.Statement)
        | :? WhileStatementSyntax as w -> Some(w :> StatementSyntax, w.Statement)
        | :? DoStatementSyntax as d -> Some(d :> StatementSyntax, d.Statement)
        | _ -> None

    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun n ->
        match loopParts n with
        | None -> None
        | Some(loop, body) ->
            match AsyncShapes.enclosingFunction loop with
            | None -> None
            | Some fn when AsyncShapes.isSharedFactory fn -> None
            | Some fn ->
                let token =
                    fn.Parameters
                    |> List.tryFind (fun p ->
                        match model.GetDeclaredSymbol p with
                        | null -> false
                        | s -> s.Type.ToDisplayString() = "System.Threading.CancellationToken")

                match token with
                | None -> None
                | Some p ->
                    let name = p.Identifier.ValueText

                    // the outer loop is the granularity; a loop in a finally or a catch is
                    // cleanup (a rollback after a cancelled copy) and must not throw
                    let nestedInLoop =
                        loop.Ancestors()
                        |> Seq.takeWhile (fun a -> not (obj.ReferenceEquals(a, fn.Node)))
                        |> Seq.exists (fun a ->
                            (loopParts a).IsSome || a :? FinallyClauseSyntax || a :? CatchClauseSyntax)

                    // locals that carry the token: built from it (`var e =
                    // source.GetAsyncEnumerator(ct);`, `var context = Pool.Get(ct);`) — but an
                    // AWAITED result (`var items = await repo.GetAllAsync(ct);`) is data the
                    // operation produced, a carrier only when typed to hold a token (a token,
                    // a source, a type with a token member: Polly's ResilienceContext)
                    let carriesToken (t: ITypeSymbol) =
                        let isToken (x: ITypeSymbol) =
                            not (isNull x)
                            && (x.ToDisplayString() = "System.Threading.CancellationToken"
                                || x.ToDisplayString() = "System.Threading.CancellationTokenSource")

                        not (isNull t)
                        && (isToken t
                            || t.GetMembers()
                               |> Seq.exists (fun m ->
                                   match m with
                                   | :? IPropertySymbol as p -> isToken p.Type
                                   | :? IFieldSymbol as f -> isToken f.Type
                                   | _ -> false))

                    let carriers =
                        let declarators =
                            fn.Body.DescendantNodes()
                            |> Seq.choose (fun d ->
                                match d with
                                | :? VariableDeclaratorSyntax as v when
                                    not (isNull v.Initializer)
                                    && (not (v.Initializer.Value :? AwaitExpressionSyntax)
                                        || (match model.GetDeclaredSymbol v with
                                            | :? ILocalSymbol as l -> carriesToken l.Type
                                            | _ -> false))
                                    ->
                                    Some v
                                | _ -> None)
                            |> List.ofSeq

                        let rec grow (names: Set<string>) =
                            let more =
                                declarators
                                |> List.filter (fun v ->
                                    not (names.Contains v.Identifier.ValueText)
                                    && names |> Set.exists (fun n -> Text.mentionsName n v.Initializer.Value))
                                |> List.map (fun v -> v.Identifier.ValueText)

                            if more.IsEmpty then
                                names
                            else
                                grow (Set.union names (set more))

                        grow (set [ name ])

                    // the author passes no token on purpose inside the loop
                    let explicitlyUncancelled =
                        body.DescendantNodes()
                        |> Seq.exists (fun d ->
                            match d with
                            | :? MemberAccessExpressionSyntax as ma ->
                                ma.Name.Identifier.ValueText = "None"
                                && ma.Expression.ToString().EndsWith "CancellationToken"
                            | :? DefaultExpressionSyntax as de -> de.Type.ToString().EndsWith "CancellationToken"
                            | _ -> false)

                    // a loop that waits: an await, a sleep, or a block on a task —
                    // an in-memory loop over a user call is not long by itself
                    let isTask (e: ExpressionSyntax) =
                        match model.GetTypeInfo(e).Type with
                        | null -> false
                        | t -> t.OriginalDefinition.ToDisplayString().StartsWith "System.Threading.Tasks."

                    let works =
                        body.DescendantNodesAndSelf()
                        |> Seq.exists (fun d ->
                            match d with
                            | :? AwaitExpressionSyntax -> true
                            | :? InvocationExpressionSyntax as inv ->
                                match inv.Expression with
                                | :? MemberAccessExpressionSyntax as ma ->
                                    let n = ma.Name.Identifier.ValueText

                                    (n = "Sleep" && ma.Expression.ToString().EndsWith "Thread")
                                    || ((n = "Wait" || n = "GetResult" || n = "WaitAll" || n = "WaitAny")
                                        && (isTask ma.Expression
                                            || ma.Expression.ToString().EndsWith "Task"
                                            || ma.Expression.ToString().EndsWith "GetAwaiter()"))
                                | _ -> false
                            | :? MemberAccessExpressionSyntax as ma ->
                                ma.Name.Identifier.ValueText = "Result" && isTask ma.Expression
                            | _ -> false)

                    if
                        nestedInLoop
                        || carriers |> Set.exists (fun n -> Text.mentionsName n loop)
                        || explicitlyUncancelled
                        || not works
                    then
                        None
                    else
                        let message =
                            $"This loop never looks at '{name}': the caller's cancellation is ignored for its whole run — call {name}.ThrowIfCancellationRequested() at the top of each iteration, or pass the token to the calls inside"

                        match body with
                        | :? BlockSyntax as b when b.Statements.Count > 0 ->
                            let first = b.Statements.[0]
                            let indent = Text.leadingWhitespace text first.SpanStart
                            let nl = Text.newlineAt text first.SpanStart

                            let edit =
                                Suggestion.insert first.SpanStart $"{name}.ThrowIfCancellationRequested();{nl}{indent}"

                            Some(
                                {
                                    Code = TokenLoopCode
                                    Message = message
                                    Span = loop.GetFirstToken().Span
                                    Fixes =
                                        [ Suggestion.fix "Observe the token each iteration" TokenLoopCode [ edit ] ]
                                }
                                |> Guards.verified model
                            )
                        | _ -> Some(Suggestion.note TokenLoopCode message (loop.GetFirstToken().Span)))
    |> List.ofSeq

let analyze (tree: SyntaxTree) (model: SemanticModel) (_ctx: RuleContext) : Suggestion list =
    releases tree model
    @ lazyStatics tree model
    @ innerExceptions tree model
    @ tokenLoops tree model
