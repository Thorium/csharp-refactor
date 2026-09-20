/// CR0060 (correctness, fix): a disposable constructed into a local and
/// never disposed is a `using`.
///
///     var s = new FileStream(path, FileMode.Open);   →  using var s = new FileStream(path, FileMode.Open);
///
/// Three tiers, decided by where the binder's mentions send the value:
///
/// FIX when the value provably stays inside the scope — every mention is
/// an invoked member or property read whose result is a plain value
/// (void, a primitive, a string, an array or tuple of those), a
/// comparison operand, or a local bound to such a value; nothing pending
/// (a task or a lazy sequence obtained through it) outlives the scope.
///
/// NOTHING when an escape is an ownership transfer: returned (also inside
/// a tuple or a wrapper), handed to another disposable's constructor
/// (`new StreamReader(stream)` adopts it), stored where a holder beyond
/// the scope keeps it (a field, a property, a collection `Add`/`Enqueue`/
/// `Push`/`Insert`, an indexer set, a static), or disposed/closed by hand
/// anywhere in scope (`x.Dispose()`, `x.Close()`, `((IDisposable)x).Dispose()`).
///
/// NOTE ONLY, naming the destination, when a mention could move the value
/// somewhere whose ownership is unknown: handed to a method by name (a
/// same-file method is read one hop — a parameter it disposes is a
/// transfer, one it keeps is the leak), captured by a lambda or local
/// function, a method group handed on, a task obtained through it and
/// dropped or handed on (in flight past the scope), a
/// `CancellationTokenSource` whose token reaches anything but an awaited
/// call in the same statement.
///
/// Never a candidate: a self-active object (a timer, a watcher, a
/// listener, anything started or subscribed to — `using` would stop it on
/// the way out); `HttpClient` (a shared lifetime); `HttpRequestMessage` and the string,
/// form and byte contents (own nothing unmanaged, and a test's handler mock
/// reads them back after the send); `MemoryStream`,
/// `StringReader`, `StringWriter` (own nothing); a wrapper over a foreign
/// resource (`new StreamReader(parameter)`, or over a local that itself
/// escapes); a value already under `using`; an `IAsyncDisposable`-only
/// type (v2: `await using`). Under `Main` or top-level statements only a
/// flush-sensitive type (a writer, a stream, a transaction) is lost work
/// — .NET runs no finalizers at exit — and only those are reported there.
/// `using var` needs C# 8; below it the rule notes. Yields to CA2000.
module CSharp.Refactor.UseBinding

open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax
open Microsoft.CodeAnalysis.Text

[<Literal>]
let Code = "CR0060"

let private isDisposable (t: ITypeSymbol) =
    match t with
    | null -> false
    | t ->
        let name (i: INamedTypeSymbol) = i.ToDisplayString()

        (match t with
         | :? INamedTypeSymbol as n -> name n = "System.IDisposable"
         | _ -> false)
        || t.AllInterfaces |> Seq.exists (fun i -> name i = "System.IDisposable")

let private noOwnership =
    set
        [
            "System.Net.Http.HttpClient"
            // a request message owns nothing unmanaged (its Dispose reaches its
            // content, a string or form), and the handler mocks tests use read it
            // back after the send: a `using` here breaks the test and gains nothing
            "System.Net.Http.HttpRequestMessage"
            "System.Net.Http.StringContent"
            "System.Net.Http.FormUrlEncodedContent"
            "System.Net.Http.ByteArrayContent"
            "System.IO.MemoryStream"
            "System.IO.StringReader"
            "System.IO.StringWriter"
            "System.Threading.Tasks.Task"
            "System.Threading.SemaphoreSlim"
            "System.Threading.ManualResetEventSlim"
        ]

let private selfActiveNames =
    [
        "Timer"
        "Watcher"
        "Listener"
        "Server"
        "Host"
        "Subscription"
        "Observer"
        "Registration"
    ]

let private factories =
    set
        [
            "MD5.Create"
            "SHA1.Create"
            "SHA256.Create"
            "SHA384.Create"
            "SHA512.Create"
            "Aes.Create"
            "RandomNumberGenerator.Create"
            "File.OpenRead"
            "File.OpenWrite"
            "File.Create"
            "File.Open"
            "File.OpenText"
            "File.CreateText"
            "File.AppendText"
            "File.OpenHandle"
            "CancellationTokenSource.CreateLinkedTokenSource"
        ]

let private flushSensitive (t: ITypeSymbol) =
    let n = t.Name

    n.EndsWith "Writer"
    || n.EndsWith "Stream"
    || n.Contains "Transaction"
    || n.EndsWith "Connection"

let private plainValue (t: ITypeSymbol) =
    match t with
    | null -> true
    | t when t.SpecialType = SpecialType.System_Void -> true
    | t when t.SpecialType <> SpecialType.None -> true
    | t when
        t.TypeKind = TypeKind.Enum
        || t.TypeKind = TypeKind.Struct && not (isDisposable t)
        ->
        true
    | :? IArrayTypeSymbol as a -> a.ElementType.SpecialType <> SpecialType.None
    | t when t.IsTupleType -> true
    | _ -> false

let private isTaskLike (t: ITypeSymbol) = AsyncShapes.isTaskLike t

/// A value reached through the binder that is evaluated and done: a plain
/// value, or an object that is neither disposable, nor pending work, nor a
/// lazy sequence (one that runs when enumerated, after the scope).
let private settled (t: ITypeSymbol) =
    plainValue t
    || (not (isDisposable t)
        && not (isTaskLike t)
        && not (Linq.isGenericEnumerable t && not (Linq.isCollection t)))

/// A call whose result is awaited or drained on this statement: done with
/// its arguments once it completes.
[<TailCall>]
let rec private awaitedHere (inv: InvocationExpressionSyntax) =
    match inv.Parent with
    | :? AwaitExpressionSyntax -> true
    | :? MemberAccessExpressionSyntax as m ->
        match m.Name.Identifier.ValueText, m.Parent with
        | ("Result" | "Wait" | "GetAwaiter"), _ -> true
        | "ConfigureAwait", (:? InvocationExpressionSyntax as outer) -> awaitedHere outer
        | _ -> false
    | _ -> false

/// Where a mention sends the value.
type private Fate =
    | Stays
    | Transfer
    | Escape of string

/// The mentions of a local symbol in the statements after its declaration.
let private mentions (model: SemanticModel) (symbol: ISymbol) (scope: SyntaxNode) =
    scope.DescendantNodes()
    |> Seq.choose (fun n ->
        match n with
        | :? IdentifierNameSyntax as id when
            SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(id).Symbol, symbol)
            ->
            Some id
        | _ -> None)
    |> List.ofSeq

let private disposesArgument (model: SemanticModel) (callee: IMethodSymbol) (index: int) =
    // a same-file method read one hop: does it dispose its parameter?
    callee.DeclaringSyntaxReferences
    |> Seq.exists (fun r ->
        match r.GetSyntax() with
        | :? MethodDeclarationSyntax as m when index < m.ParameterList.Parameters.Count ->
            let p = m.ParameterList.Parameters.[index].Identifier.ValueText
            let body = m.ToString()

            body.Contains("using (" + p)
            || body.Contains("using var " + p)
            || body.Contains(p + ".Dispose()")
            || body.Contains(p + ".Close()")
            || System.Text.RegularExpressions.Regex.IsMatch(body, $@"new\s+\w+\({p}[,)]")
        | _ -> false)

/// Whether a call reads its argument and answers: an assertion, or a BCL
/// call returning a plain value.
let private readsOnly (callee: IMethodSymbol) =
    let ns = callee.ContainingNamespace.ToDisplayString()
    let holder = callee.ContainingType.Name

    holder = "Assert"
    || holder = "Assume"
    || ns.StartsWith "Xunit"
    || ns.StartsWith "NUnit.Framework"
    || ns.StartsWith "FluentAssertions"
    || ns.StartsWith "Shouldly"
    || ns.StartsWith "Microsoft.VisualStudio.TestTools"
    || (ns.StartsWith "System" && plainValue callee.ReturnType)

/// A BCL call whose result carries the argument (`Task.FromResult(x)`,
/// `Tuple.Create(x, …)`): the result is the value in a wrapper.
let private wraps (model: SemanticModel) (callee: IMethodSymbol) (argType: ITypeSymbol) =
    match argType with
    | null -> false
    | argType ->
        callee.ContainingNamespace.ToDisplayString().StartsWith "System"
        && (match callee.ReturnType with
            | :? INamedTypeSymbol as r when r.IsGenericType ->
                r.TypeArguments
                |> Seq.exists (fun t -> model.Compilation.HasImplicitConversion(argType, t))
            | _ -> false)

/// Where an expression's value goes, read off its parent through the
/// wrappers that keep the same value (`??`, parentheses, `!`, casts, `as`).
/// `what` names the value for a note; `dropped` is the fate of a bare
/// statement of it, `returned` that of a return of it.
let rec private resultFate
    (model: SemanticModel)
    (tree: SyntaxTree)
    (scope: SyntaxNode)
    (what: string)
    (dropped: Fate)
    (returned: Fate)
    (e: ExpressionSyntax)
    : Fate =
    let again (outer: ExpressionSyntax) =
        resultFate model tree scope what dropped returned outer

    match e.Parent with
    | :? AwaitExpressionSyntax -> Stays
    | :? ParenthesizedExpressionSyntax as p -> again p
    | :? PostfixUnaryExpressionSyntax as u when u.IsKind SyntaxKind.SuppressNullableWarningExpression -> again u
    | :? CastExpressionSyntax as c -> again c
    | :? BinaryExpressionSyntax as b when
        (b.IsKind SyntaxKind.CoalesceExpression && b.Left.Span = e.Span)
        || b.IsKind SyntaxKind.AsExpression
        ->
        again b
    | :? BinaryExpressionSyntax -> Stays // compared
    | :? IsPatternExpressionSyntax -> Stays
    | :? ConditionalAccessExpressionSyntax as c -> again c
    | :? MemberAccessExpressionSyntax as m when m.Expression.Span = e.Span ->
        // a chain: the next link's result decides
        match m.Parent with
        | :? InvocationExpressionSyntax as inv ->
            let name = m.Name.Identifier.ValueText

            if name = "Dispose" || name = "Close" || name = "DisposeAsync" then
                Transfer
            elif settled (model.GetTypeInfo(inv).Type) then
                Stays
            else
                resultFate
                    model
                    tree
                    scope
                    $"a value reached through it ('{name}')"
                    Stays
                    (Escape $"a value reached through it ('{name}') is returned")
                    inv
        | _ ->
            if settled (model.GetTypeInfo(m).Type) then
                Stays
            else
                let name = m.Name.Identifier.ValueText

                resultFate
                    model
                    tree
                    scope
                    $"'{name}' read through it"
                    Stays
                    (Escape $"'{name}' read through it is returned")
                    m
    | :? MemberBindingExpressionSyntax -> Stays
    | :? AssignmentExpressionSyntax as a when a.Left.Span = e.Span -> Stays // written into
    | :? EqualsValueClauseSyntax as ev ->
        match ev.Parent with
        | :? VariableDeclaratorSyntax as v ->
            followAlias model tree scope (model.GetDeclaredSymbol v) (Escape $"{what} is aliased")
        | _ -> Escape $"{what} is stored"
    | :? ExpressionStatementSyntax -> dropped
    | :? ArgumentSyntax as arg when (arg.Parent :? TupleExpressionSyntax) -> returned
    | :? ArgumentSyntax as arg ->
        match arg.Parent.Parent with
        | :? InvocationExpressionSyntax as inv ->
            match model.GetSymbolInfo(inv).Symbol with
            | :? IMethodSymbol as callee when readsOnly callee -> Stays
            | :? IMethodSymbol as callee when wraps model callee (model.GetTypeInfo(arg.Expression).Type) ->
                resultFate model tree scope $"{what}, wrapped by '{Linq.nameOf inv}'," dropped returned inv
            | _ -> Escape $"{what} is handed to '{Linq.nameOf inv}'"
        | _ -> Escape $"{what} is handed on"
    | :? ReturnStatementSyntax
    | :? ArrowExpressionClauseSyntax
    | :? YieldStatementSyntax -> returned
    | :? ExpressionElementSyntax
    | :? ArrayCreationExpressionSyntax
    | :? ImplicitArrayCreationExpressionSyntax
    | :? InitializerExpressionSyntax
    | :? AnonymousObjectMemberDeclaratorSyntax -> returned // built into something that leaves
    | :? AssignmentExpressionSyntax as a when a.Right.Span = e.Span ->
        match model.GetSymbolInfo(a.Left).Symbol with
        | :? IFieldSymbol
        | :? IPropertySymbol -> returned
        | :? ILocalSymbol as l when
            (Linq.declarationSpan tree l
             |> Option.exists (fun s -> not (scope.Span.Contains s)))
            ->
            returned
        | _ -> Escape $"{what} is assigned to another local"
    | :? ConditionalExpressionSyntax as c -> again c
    | :? SwitchExpressionArmSyntax as arm -> again (arm.Parent :?> ExpressionSyntax)
    | _ -> Escape $"{what} is used in a way the rule cannot follow"

/// Where a mention of the binder sends the value.
and private fateOf
    (model: SemanticModel)
    (tree: SyntaxTree)
    (scope: SyntaxNode)
    (symbol: ISymbol)
    (id: IdentifierNameSyntax)
    : Fate =
    // captured by a lambda or local function: unknown lifetime
    let captured =
        id.Ancestors()
        |> Seq.takeWhile (fun a -> not (obj.ReferenceEquals(a, scope)))
        |> Seq.exists (fun a -> a :? AnonymousFunctionExpressionSyntax || a :? LocalFunctionStatementSyntax)

    if captured then
        Escape "captured by a lambda"
    else
        match id.Parent with
        | :? MemberAccessExpressionSyntax as m when m.Expression.Span = id.Span ->
            match m.Parent with
            | :? InvocationExpressionSyntax as inv ->
                let name = m.Name.Identifier.ValueText

                if name = "Dispose" || name = "Close" || name = "DisposeAsync" then
                    Transfer
                else
                    let result = model.GetTypeInfo(inv).Type

                    if settled result then
                        Stays
                    elif isTaskLike result then
                        // pending work: awaited or drained here is done; dropped or handed on is in flight
                        resultFate
                            model
                            tree
                            scope
                            "a task obtained through it is still pending:"
                            (Escape "a task obtained through it is dropped, still pending")
                            (Escape "a task obtained through it is returned, still pending")
                            inv
                    else
                        resultFate
                            model
                            tree
                            scope
                            $"a value reached through it ('{name}')"
                            Stays
                            (Escape $"a value reached through it ('{name}') is returned")
                            inv
            | _ ->
                // a property read, or a method group handed on
                match model.GetSymbolInfo(m).Symbol with
                | :? IMethodSymbol -> Escape $"the method group '{m.Name.Identifier.ValueText}' is handed on"
                | _ ->
                    let t = model.GetTypeInfo(m).Type
                    let name = m.Name.Identifier.ValueText

                    if name = "Token" && not (isNull t) && t.Name = "CancellationToken" then
                        // a CancellationTokenSource's token: kept only by an awaited BCL call (a user function is opaque)
                        let awaitedBcl =
                            match m.Parent with
                            | :? ArgumentSyntax as arg ->
                                match arg.Parent.Parent with
                                | :? InvocationExpressionSyntax as inv when (inv.Parent :? AwaitExpressionSyntax) ->
                                    match model.GetSymbolInfo(inv).Symbol with
                                    | :? IMethodSymbol as callee ->
                                        callee.ContainingNamespace.ToDisplayString().StartsWith "System"
                                    | _ -> false
                                | _ -> false
                            | _ -> false

                        if awaitedBcl then
                            Stays
                        else
                            Escape "its token is handed on"
                    elif settled t then
                        Stays
                    else
                        resultFate
                            model
                            tree
                            scope
                            $"'{name}' read through it"
                            Stays
                            (Escape $"'{name}' read through it is returned")
                            m
        | :? ArgumentSyntax as arg when (arg.Parent :? TupleExpressionSyntax) -> Transfer // built into a tuple that leaves
        | :? ArgumentSyntax as arg ->
            match arg.Parent.Parent with
            | :? BaseObjectCreationExpressionSyntax as c when isDisposable (model.GetTypeInfo(c).Type) ->
                // the wrapper adopts it unless told to leave it open
                let leftOpen =
                    c.ArgumentList.Arguments
                    |> Seq.exists (fun a ->
                        not (isNull a.NameColon)
                        && a.NameColon.Name.Identifier.ValueText = "leaveOpen"
                        && a.Expression.IsKind SyntaxKind.TrueLiteralExpression)

                if leftOpen then Stays else Transfer
            | :? BaseObjectCreationExpressionSyntax as c ->
                let t = model.GetTypeInfo(c).Type
                let holder = if isNull t then "an object" else $"'{t.Name}'"
                Escape $"handed to the constructor of {holder}, which cannot dispose it"
            | :? InvocationExpressionSyntax as inv ->
                let name = Linq.nameOf inv

                if List.contains name [ "Add"; "TryAdd"; "Enqueue"; "Push"; "Insert"; "AddOrUpdate" ] then
                    Transfer
                else
                    match model.GetSymbolInfo(inv).Symbol with
                    | :? IMethodSymbol as callee ->
                        let index = inv.ArgumentList.Arguments.IndexOf arg

                        if
                            callee.DeclaringSyntaxReferences.Length > 0
                            && disposesArgument model callee index
                        then
                            Transfer
                        elif readsOnly callee then
                            Stays
                        elif
                            callee.ContainingNamespace.ToDisplayString().StartsWith "System"
                            && awaitedHere inv
                        then
                            Stays // a BCL call, done with it once awaited here
                        elif wraps model callee (model.GetTypeInfo(arg.Expression).Type) then
                            resultFate model tree scope $"wrapped by '{name}', it" Stays Transfer inv
                        else
                            Escape $"handed to '{name}', which may keep it"
                    | _ -> Escape $"handed to '{name}'"
            | _ -> Escape "handed on"
        | :? ReturnStatementSyntax
        | :? ArrowExpressionClauseSyntax
        | :? YieldStatementSyntax -> Transfer
        | :? ExpressionElementSyntax
        | :? ArrayCreationExpressionSyntax
        | :? ImplicitArrayCreationExpressionSyntax
        | :? CollectionExpressionSyntax
        | :? InitializerExpressionSyntax
        | :? AnonymousObjectMemberDeclaratorSyntax -> Transfer // built into something that leaves
        | :? AssignmentExpressionSyntax as a when a.Right.Span = id.Span ->
            match model.GetSymbolInfo(a.Left).Symbol with
            | :? IFieldSymbol
            | :? IPropertySymbol -> Transfer
            | :? ILocalSymbol as l when
                (Linq.declarationSpan tree l
                 |> Option.exists (fun s -> not (scope.Span.Contains s)))
                ->
                Transfer
            | _ -> Escape "assigned to another local"
        | :? EqualsValueClauseSyntax as e ->
            match e.Parent with
            | :? VariableDeclaratorSyntax as v ->
                followAlias model tree scope (model.GetDeclaredSymbol v) (Escape "aliased")
            | _ -> Escape "aliased"
        | :? CastExpressionSyntax as c ->
            // `((IDisposable)x).Dispose()`
            let text = c.Parent.Parent.Parent.ToString()

            if text.Contains ".Dispose()" || text.Contains ".Close()" then
                Transfer
            else
                Escape "cast and handed on"
        | :? BinaryExpressionSyntax -> Stays
        | :? IsPatternExpressionSyntax -> Stays
        | :? ParenthesizedExpressionSyntax as p ->
            match p.Parent with
            | :? MemberAccessExpressionSyntax as m when m.Name.Identifier.ValueText = "Dispose" -> Transfer
            | _ -> Escape "handed on"
        | :? ConditionalAccessExpressionSyntax as c ->
            // `x?.Dispose()` is manual management
            let rest = c.WhenNotNull.ToString()

            if
                rest.StartsWith ".Dispose("
                || rest.StartsWith ".Close("
                || rest.StartsWith ".DisposeAsync("
            then
                Transfer
            else
                Stays
        | :? AwaitExpressionSyntax -> Stays
        | :? UsingStatementSyntax -> Transfer // `using (x) { … }`
        | _ -> Escape "used in a way the rule cannot follow"

/// A local bound to a value reached through the binder: judged by its own mentions.
and private followAlias
    (model: SemanticModel)
    (tree: SyntaxTree)
    (scope: SyntaxNode)
    (alias: ISymbol)
    (fallback: Fate)
    =
    match alias with
    | null -> fallback
    | alias ->
        let aliasType =
            match alias with
            | :? ILocalSymbol as l -> l.Type
            | _ -> null

        if settled aliasType then
            Stays
        else
            let fates = mentions model alias scope |> List.map (fateOf model tree scope alias)

            if
                fates
                |> List.exists (fun f ->
                    match f with
                    | Transfer -> true
                    | Stays
                    | Escape _ -> false)
            then
                Transfer
            elif
                fates
                |> List.forall (fun f ->
                    match f with
                    | Stays -> true
                    | Transfer
                    | Escape _ -> false)
            then
                Stays
            else
                let why =
                    fates
                    |> List.pick (fun f ->
                        match f with
                        | Escape why -> Some why
                        | Stays
                        | Transfer -> None)

                if isTaskLike aliasType then
                    Escape $"a task obtained through it ('{alias.Name}') is still pending, {why}"
                else
                    Escape $"through '{alias.Name}' it is {why}"

[<return: Struct>]
let inline private (|IsDisposable|_|) input =
    if isDisposable input then ValueSome input else ValueNone

let private construction (model: SemanticModel) (init: ExpressionSyntax) : ITypeSymbol option =
    match init with
    | :? BaseObjectCreationExpressionSyntax as c ->
        match model.GetTypeInfo(c).Type with
        | null -> None
        | IsDisposable t -> Some t
        | _ -> None
    | :? InvocationExpressionSyntax as inv when factories.Contains(inv.Expression.ToString()) ->
        match model.GetTypeInfo(inv).Type with
        | null -> None
        | t -> Some t
    | _ -> None

/// A wrapper over a resource the scope did not create: a parameter, a
/// field, a property read or a captured local as the first argument.
let private wrapsForeign (model: SemanticModel) (init: ExpressionSyntax) =
    match init with
    | :? BaseObjectCreationExpressionSyntax as c when not (isNull c.ArgumentList) && c.ArgumentList.Arguments.Count > 0 ->
        let first = c.ArgumentList.Arguments.[0].Expression

        match model.GetSymbolInfo(first).Symbol with
        | :? IParameterSymbol
        | :? IFieldSymbol
        | :? IPropertySymbol -> isDisposable (model.GetTypeInfo(first).Type)
        | _ -> false
    | _ -> false

let private selfActive
    (model: SemanticModel)
    (t: ITypeSymbol)
    (init: ExpressionSyntax)
    (scope: SyntaxNode)
    (name: string)
    =
    selfActiveNames |> List.exists (fun s -> t.Name.Contains s)
    || (match init with
        | :? BaseObjectCreationExpressionSyntax as c when not (isNull c.ArgumentList) ->
            c.ArgumentList.Arguments
            |> Seq.exists (fun a -> a.Expression :? AnonymousFunctionExpressionSyntax)
        | _ -> false)
    // started or subscribed to in the scope
    || (scope.ToString().Contains(name + ".Start(")
        || scope.ToString().Contains(name + ".Enable")
        || System.Text.RegularExpressions.Regex.IsMatch(scope.ToString(), name + @"\.\w+\s*\+="))

let analyze (tree: SyntaxTree) (model: SemanticModel) (ctx: RuleContext) : Suggestion list =
    let text = tree.GetText()
    let usingVar = ctx.LanguageVersion >= LanguageVersion.CSharp8

    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun n ->
        match n with
        | :? LocalDeclarationStatementSyntax as d when
            d.UsingKeyword.IsKind SyntaxKind.None
            && d.Declaration.Variables.Count = 1
            && not (isNull d.Declaration.Variables.[0].Initializer)
            && (d.Parent :? BlockSyntax)
            ->
            let v = d.Declaration.Variables.[0]
            let init = v.Initializer.Value

            match construction model init with
            | None -> None
            | Some t when noOwnership.Contains(t.OriginalDefinition.ToDisplayString()) -> None
            | Some t when wrapsForeign model init -> None
            | Some t ->
                let block = d.Parent :?> BlockSyntax
                let name = v.Identifier.ValueText

                // the scope: the statements after the declaration
                let rest =
                    block.Statements |> Seq.filter (fun s -> s.SpanStart > d.Span.End) |> List.ofSeq

                let scopeText = rest |> List.map (fun s -> s.ToString()) |> String.concat "\n"

                if selfActive model t init block name then
                    None
                else
                    let symbol = model.GetDeclaredSymbol v

                    let fates =
                        rest
                        |> List.collect (fun s -> mentions model symbol s |> List.map (fateOf model tree block symbol))

                    let underMain =
                        match AsyncShapes.enclosingFunction d with
                        | Some {
                                   AsyncShapes.Function.Node = (:? MethodDeclarationSyntax as m)
                               } -> m.Identifier.ValueText = "Main"
                        | None -> d.Ancestors() |> Seq.exists (fun a -> a :? GlobalStatementSyntax)
                        | _ -> false

                    if underMain && not (flushSensitive t) then
                        None
                    elif
                        fates
                        |> List.exists (fun f ->
                            match f with
                            | Transfer -> true
                            | Stays
                            | Escape _ -> false)
                    then
                        None
                    else
                        let escapes =
                            fates
                            |> List.choose (fun f ->
                                match f with
                                | Escape why -> Some why
                                | Stays
                                | Transfer -> None)

                        match escapes with
                        | why :: _ ->
                            Some(
                                Suggestion.note
                                    Code
                                    $"'{name}' ({t.Name}) is never disposed here, and {why}: dispose it where its lifetime ends"
                                    v.Span
                            )
                        | [] when usingVar ->
                            let edit = Suggestion.insert d.SpanStart "using "

                            if Guards.speculativeCheck model [ edit ] then
                                Some
                                    {
                                        Code = Code
                                        Message =
                                            $"'{name}' ({t.Name}) is never disposed and stays in this scope: a using declaration"
                                        Span = v.Span
                                        Fixes = [ Suggestion.fix "Make it a using declaration" Code [ edit ] ]
                                    }
                            else
                                None
                        | [] ->
                            Some(
                                Suggestion.note
                                    Code
                                    $"'{name}' ({t.Name}) is never disposed and stays in this scope: wrap it in a using statement"
                                    v.Span
                            )
        | _ -> None)
    |> List.ofSeq
