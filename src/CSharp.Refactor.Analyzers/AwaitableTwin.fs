/// CR0042 (correctness, fix): a synchronous call inside an `async` body
/// whose `…Async` twin exists is the awaited twin.
///
///     var line = reader.ReadLine();         →  var line = await reader.ReadLineAsync();
///     stream.Flush();                       →  await stream.FlushAsync();
///     var text = File.ReadAllText(path);    →  var text = await File.ReadAllTextAsync(path);
///     Thread.Sleep(100);                    →  await Task.Delay(100);
///
/// Guards: the twin is proven from the typed tree — same name plus
/// `Async`, on the same type (or an extension in scope), the same
/// parameter types in the same order, a trailing optional
/// `CancellationToken` tolerated (CA2016 hands it the token on the next
/// pass), and a return type that WRAPS the original's, compared
/// structurally (`void` → `Task`/`ValueTask`, `T` → `Task<T>`/
/// `ValueTask<T>`); the site is a statement, the initializer of a local,
/// or the right side of an assignment statement, in an `async` body and
/// not inside a `lock`, `catch`, `finally`, non-async lambda or local
/// function; never `Dispose` → `DisposeAsync` (a `ValueTask` twin with
/// nothing to await behind it), `CancellationTokenSource.Cancel` →
/// `CancelAsync`, nor a `System.Xml` twin (it throws unless the reader or
/// writer settings opted in with `Async = true`); nor a LINQ twin over
/// `IQueryable<T>` (EF's `ToListAsync`, `FirstAsync`: only a provider that
/// implements the async query interface runs it — an in-memory
/// `AsQueryable()` source, a test fake, throws at the call); `Thread.Sleep(0)` and
/// `Sleep(1)` are thread yields and stay; never a `MemoryStream`/`StringWriter`/
/// `StringReader` receiver or `Console.Out`/`Console.Error` (nothing behind
/// the twin), EF Core's `Add`/`AddRange` (async only for database value
/// generators, EF's documentation says), nor inside a member named for its
/// synchronous work (`WriteTokenSyncReadingAsync`). The inserted `await`
/// carries `.ConfigureAwait(false)` where the method's own awaits all use it,
/// or, in a method with no other await, where CA2007 is configured
/// (`AsyncShapes.configureAwaitSuffix`). Yields to CA1849.
module CSharp.Refactor.AwaitableTwin

open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax
open Microsoft.CodeAnalysis.Text

[<Literal>]
let Code = "CR0042"

let private wraps (original: ITypeSymbol) (twin: ITypeSymbol) =
    match twin with
    | :? INamedTypeSymbol as t ->
        let name = t.OriginalDefinition.ToDisplayString()

        if original.SpecialType = SpecialType.System_Void then
            name = "System.Threading.Tasks.Task"
            || name = "System.Threading.Tasks.ValueTask"
        else
            (name = "System.Threading.Tasks.Task<TResult>"
             || name = "System.Threading.Tasks.ValueTask<TResult>")
            && t.TypeArguments.Length = 1
            && SymbolEqualityComparer.Default.Equals(t.TypeArguments.[0], original)
    | _ -> false

let private isToken (t: ITypeSymbol) =
    t.ToDisplayString() = "System.Threading.CancellationToken"

/// The twin of a method, if the typed tree proves one.
let private twinOf
    (model: SemanticModel)
    (position: int)
    (receiverType: ITypeSymbol)
    (m: IMethodSymbol)
    : IMethodSymbol option =
    // `Dispose` → `DisposeAsync` has nothing to await behind it;
    // `CancellationTokenSource.Cancel` → `CancelAsync` only changes who runs the callbacks
    if
        m.Name = "Dispose"
        || m.Name.EndsWith "Async"
        || (m.Name = "Cancel"
            && m.ContainingType.ToDisplayString() = "System.Threading.CancellationTokenSource")
        // an XmlReader/XmlWriter async twin throws unless the settings opted in (`Async = true`)
        || m.ContainingType.ToDisplayString().StartsWith "System.Xml"
    then
        None
    else
        let candidates =
            if isNull receiverType then
                m.ContainingType.GetMembers(m.Name + "Async") |> Seq.cast<ISymbol>
            else
                model.LookupSymbols(position, receiverType, m.Name + "Async", true)

        candidates
        |> Seq.choose (fun s ->
            match s with
            | :? IMethodSymbol as t -> Some t
            | _ -> None)
        |> Seq.tryFind (fun t ->
            let ps = t.Parameters |> List.ofSeq
            let os = m.Parameters |> List.ofSeq

            let sameParams =
                ps.Length = os.Length
                && List.forall2
                    (fun (p: IParameterSymbol) (o: IParameterSymbol) ->
                        SymbolEqualityComparer.Default.Equals(p.Type, o.Type))
                    ps
                    os

            let trailingToken =
                ps.Length = os.Length + 1
                && isToken (List.last ps).Type
                && (List.last ps).IsOptional
                && List.forall2
                    (fun (p: IParameterSymbol) (o: IParameterSymbol) ->
                        SymbolEqualityComparer.Default.Equals(p.Type, o.Type))
                    (List.take os.Length ps)
                    os

            // a LINQ twin over `IQueryable<T>` (EF's `ToListAsync`, `FirstAsync`…) runs
            // only on a provider that implements the async query interface: an
            // in-memory `AsQueryable()` source, a test fake, throws at the call
            let queryableTwin =
                let reduced = if isNull t.ReducedFrom then t else t.ReducedFrom

                reduced.IsExtensionMethod
                && reduced.Parameters.Length > 0
                && (let receiver = reduced.Parameters.[0].Type.OriginalDefinition.ToDisplayString()
                    receiver = "System.Linq.IQueryable<T>" || receiver = "System.Linq.IQueryable")

            t.IsStatic = m.IsStatic
            && (sameParams || trailingToken)
            && wraps m.ReturnType t.ReturnType
            && not queryableTwin)

/// A member named for its synchronous work (`WriteTokenSyncReadingAsync`):
/// the sync call inside is the point.
let private syncOnPurpose (node: SyntaxNode) =
    let name =
        match node with
        | :? MethodDeclarationSyntax as m -> m.Identifier.ValueText
        | :? LocalFunctionStatementSyntax as l -> l.Identifier.ValueText
        | _ -> ""

    // `Sync` as an inner word before the I/O it does (`WriteToken|Sync|Reading`): not
    // the verb in `SyncContactsAsync`/`DoSyncAsync`, nor `Resync`, `Synchronize`, nor
    // the noun in `RunDataSyncJobAsync`/`HandleSyncRequestAsync`
    let stem =
        if name.EndsWith "Async" then
            name.Substring(0, name.Length - 5)
        else
            name

    System.Text.RegularExpressions.Regex.IsMatch(
        stem,
        "[a-z]Sync(Reading|Writing|Flushing|Copying|Loading|Saving|Sending|Receiving|Waiting|Blocking)(?![a-z])"
    )

let private memoryTypes =
    set
        [
            "System.IO.MemoryStream"
            "System.IO.StringWriter"
            "System.IO.StringReader"
            "System.IO.UnmanagedMemoryStream"
        ]

/// Twins with nothing behind them, or advised against: an in-memory stream,
/// reader or writer and the console's writers complete synchronously anyway;
/// EF Core's `Add`/`AddRange` are async only for value generators that reach
/// the database (SQL Server HiLo), and EF's documentation says to use the
/// synchronous ones otherwise.
let private inMemory (model: SemanticModel) (inv: InvocationExpressionSyntax) (m: IMethodSymbol) =
    let receiver =
        match inv.Expression with
        | :? MemberAccessExpressionSyntax as ma -> Some ma.Expression
        | _ -> None

    let rec within (names: Set<string>) (t: ITypeSymbol) =
        not (isNull t)
        && (names.Contains(t.OriginalDefinition.ToDisplayString())
            || within names t.BaseType)

    let receiverType =
        match receiver with
        | Some r -> model.GetTypeInfo(r).Type
        | None -> m.ContainingType :> ITypeSymbol

    let consoleWriter =
        match receiver with
        | Some r ->
            let text = r.ToString().Replace("System.", "")
            text = "Console.Out" || text = "Console.Error"
        | None -> false

    let rec efType (t: ITypeSymbol) =
        not (isNull t)
        && (t.ContainingNamespace.ToDisplayString().StartsWith "Microsoft.EntityFrameworkCore"
            || efType t.BaseType)

    consoleWriter
    || within memoryTypes receiverType
    || ((m.Name = "Add" || m.Name = "AddRange") && efType m.ContainingType)

/// A statement, a local's initializer, or an assignment's right side.
let private bindablePosition (inv: InvocationExpressionSyntax) =
    match inv.Parent with
    | :? ExpressionStatementSyntax -> true
    | :? EqualsValueClauseSyntax as e -> e.Parent :? VariableDeclaratorSyntax
    | :? AssignmentExpressionSyntax as a -> a.Right.Span = inv.Span && (a.Parent :? ExpressionStatementSyntax)
    | :? ReturnStatementSyntax -> true
    | _ -> false

let analyze (tree: SyntaxTree) (model: SemanticModel) (_ctx: RuleContext) : Suggestion list =
    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun n ->
        match n with
        | :? InvocationExpressionSyntax as inv when bindablePosition inv ->
            match AsyncShapes.enclosingFunction inv with
            | Some f when f.IsAsync ->
                let noBind =
                    inv.Ancestors()
                    |> Seq.takeWhile (fun a -> not (obj.ReferenceEquals(a, f.Node)))
                    |> Seq.exists (fun a ->
                        a :? LockStatementSyntax
                        || a :? CatchClauseSyntax
                        || a :? FinallyClauseSyntax
                        || a :? UnsafeStatementSyntax)

                if noBind then
                    None
                else
                    // `Thread.Sleep(n);` is `await Task.Delay(n);`
                    let sleep =
                        inv.Expression.ToString() = "Thread.Sleep"
                        && inv.ArgumentList.Arguments.Count = 1
                        && (inv.Parent :? ExpressionStatementSyntax)
                        // `Sleep(0)`/`Sleep(1)` yield the thread; `Task.Delay(0)` yields nothing
                        && not (
                            match inv.ArgumentList.Arguments.[0].Expression with
                            | :? LiteralExpressionSyntax as l -> l.Token.ValueText = "0" || l.Token.ValueText = "1"
                            | _ -> false
                        )
                        && (match model.GetSymbolInfo(inv).Symbol with
                            | :? IMethodSymbol as m -> m.ContainingType.ToDisplayString() = "System.Threading.Thread"
                            | _ -> false)

                    if sleep then
                        let edit =
                            Suggestion.replace
                                inv.Span
                                ("await Task.Delay"
                                 + inv.ArgumentList.ToString()
                                 + (AsyncShapes.configureAwaitSuffix tree model inv))

                        if Guards.speculativeCheck model [ edit ] then
                            Some
                                {
                                    Code = Code
                                    Message = "Thread.Sleep inside an async method holds the thread: await Task.Delay"
                                    Span = inv.Span
                                    Fixes = [ Suggestion.fix "Await Task.Delay" Code [ edit ] ]
                                }
                        else
                            None
                    else
                        match model.GetSymbolInfo(inv).Symbol with
                        | :? IMethodSymbol as m when not (m.IsAsync || AsyncShapes.isTaskLike m.ReturnType) ->
                            let receiverType =
                                match inv.Expression with
                                | :? MemberAccessExpressionSyntax as ma when not m.IsStatic ->
                                    model.GetTypeInfo(ma.Expression).Type
                                | _ -> null

                            match twinOf model inv.SpanStart receiverType m with
                            | Some _ when syncOnPurpose f.Node || inMemory model inv m -> None
                            | Some twin ->
                                let nameToken =
                                    match inv.Expression with
                                    | :? MemberAccessExpressionSyntax as ma -> Some ma.Name.Identifier
                                    | :? IdentifierNameSyntax as pid -> Some pid.Identifier
                                    | _ -> None

                                match nameToken with
                                | None -> None
                                | Some token ->
                                    let suffix = AsyncShapes.configureAwaitSuffix tree model inv

                                    let edits =
                                        [
                                            Suggestion.insert inv.SpanStart "await "
                                            Suggestion.replace token.Span twin.Name
                                            if suffix <> "" then
                                                Suggestion.insert inv.Span.End suffix
                                        ]

                                    if Guards.speculativeCheck model edits then
                                        Some
                                            {
                                                Code = Code
                                                Message =
                                                    $"'{m.Name}' has an async twin '{twin.Name}': await it instead of holding the thread"
                                                Span = inv.Span
                                                Fixes = [ Suggestion.fix ("Await " + twin.Name) Code edits ]
                                            }
                                    else
                                        None
                            | None -> None
                        | _ -> None
            | _ -> None
        | _ -> None)
    |> List.ofSeq
