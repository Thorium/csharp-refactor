/// Exception shapes.
///
/// CR0064 (correctness, note): a catch-all that swallows — `catch { }`,
/// `catch (Exception) { return null; }`, `catch (Exception e) when
/// (flag)` never reading `e` — hides every failure, the ones it did not
/// mean too. Note; the editor offers are v2. Not a swallow: a handler
/// that reads the exception (logs it, inspects it) or rethrows; a
/// comment on the handler (the author's own acknowledgement); the `bool`
/// probe idiom (`try { …; return true; } catch { return false; }`, the
/// body answering with the opposite literal); a `Try…` method returning
/// `false`; a value that carries the failure (a `Result`/`OneOf` error, an
/// `Exception` object); a catch-all after an arm that rethrows
/// cancellation, or followed by an unconditional failure; the one-call
/// teardown idiom (`try { x.Dispose(); } catch { }`) and a try around a
/// non-throwing probe (`File.Exists`), each named for what it is; test
/// files. `catch (SystemException)` is not a catch-all. Yields to CA1031.
///
/// CR0065 (idiom, fix): `catch (E ex) { if (!Cond(ex)) throw; … }` is
/// `catch (E ex) when (Cond(ex)) { … }` — the filter runs before any
/// inner `finally` and never unwinds for an exception it will not handle.
/// Guards: the guard is the first statement of the handler, its
/// then-branch exactly `throw;` (not `throw ex;` — CA2200's), no `else`;
/// the condition reads only the exception's members, literals and pure
/// atoms (a filter runs BEFORE inner `finally` blocks, so an effectful
/// condition would reorder effects); no existing filter; the catch is the
/// last of its try (a rethrow skips the sibling catches, a declining
/// filter lets them see the exception); no comment on the guard line.
///
/// CR0066 (correctness, note, priority): a `throw` inside `finally`
/// replaces whatever exception was in flight. Throws the block itself
/// catches stay quiet. Yields to CA2219.
///
/// CR0067 (correctness, note): a `throw` inside `Equals`, `GetHashCode`,
/// `ToString`, `Dispose`, a static constructor, a finalizer, an implicit
/// conversion or `==`/`!=` — members the runtime and the BCL call without
/// expecting one. Throws the member's own `try` catches stay quiet. Yields
/// to CA1065.
///
/// CR0068 (correctness, note): `throw new NullReferenceException()`,
/// `IndexOutOfRangeException`, `OutOfMemoryException`,
/// `StackOverflowException`, `AccessViolationException`,
/// `ExecutionEngineException`, `ArrayTypeMismatchException` — the
/// runtime's own exceptions, which a catch cannot tell from the real
/// thing. A `switch` whose arms throw three or more distinct types is a
/// fault-injection table and stays quiet; plain `Exception` is CA2201's.
///
/// CR0069 (idiom, fix): `throw new InvalidOperationException("Error");`
/// with a constant message becomes
/// `$"Error, calling {nameof(M)} with x: {x}"` — the values that led
/// here, spelled as the literal was (escapes intact). Guards: a constant
/// string message with no `{`/`}`, not verbatim or raw, not the one
/// argument of `ArgumentNullException`/`ArgumentOutOfRangeException` (a
/// parameter name, not a message); the enclosing method has one to four parameters whose types
/// print usefully (primitives, `string`, enums, `DateTime`,
/// `Guid`, `decimal`, nullable of those; a class, an array or a delegate
/// prints its type name and does not count, and a record prints every
/// member — a dump of whatever it carries — and does not count either); the message mentions no
/// parameter as a word, is not the method's own name, is not an
/// invariant ("unreachable", "not possible", "NYI", "internal error",
/// "invalid case"); no secret-smelling name on the method, its type, its
/// namespace or a parameter (auth, session, crypt, token, password,
/// secret, credential); the literal appears nowhere else in the
/// compilation (a test asserting on it); not a test file. Exception text
/// is observable behaviour: private methods always, the rest under the
/// api gate. The message says the rewrite puts argument values into the
/// text (a PII decision on personal data).
///
/// CR0070 (correctness, note): `catch (ReflectionTypeLoadException e) {
/// Log(e.Message); }` — the informative member goes unread:
/// `LoaderExceptions`/`Types`; `AggregateException` → `InnerExceptions`/
/// `Flatten()`/`InnerException`; `WebException` → `Response`/`Status`;
/// `SqlException` → `Errors`/`Number`; `FileNotFoundException` →
/// `FusionLog`/`FileName`. Reading the member anywhere in the handler or
/// its filter counts as informed; the tested type is resolved by symbol,
/// so a user type of the same short name never matches.
module CSharp.Refactor.ExceptionRules

open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax
open Microsoft.CodeAnalysis.Text

[<Literal>]
let SwallowCode = "CR0064"

[<Literal>]
let FilterCode = "CR0065"

[<Literal>]
let ThrowInFinallyCode = "CR0066"

[<Literal>]
let ThrowInSpecialCode = "CR0067"

[<Literal>]
let ReservedExceptionCode = "CR0068"

[<Literal>]
let MessageContextCode = "CR0069"

[<Literal>]
let ExceptionDetailCode = "CR0070"

let private throwsIn (node: SyntaxNode) =
    node.DescendantNodes()
    |> Seq.exists (fun n -> n :? ThrowStatementSyntax || n :? ThrowExpressionSyntax)

/// A throw not caught inside the node itself.
let private uncaughtThrows (node: SyntaxNode) =
    node.DescendantNodes()
    |> Seq.filter (fun n -> n :? ThrowStatementSyntax || n :? ThrowExpressionSyntax)
    |> Seq.filter (fun t ->
        not (
            t.Ancestors()
            |> Seq.takeWhile (fun a -> not (obj.ReferenceEquals(a, node)))
            |> Seq.exists (fun a ->
                match a with
                | :? TryStatementSyntax as ts -> ts.Block.Span.Contains t.Span && ts.Catches.Count > 0
                | _ -> false)
        ))
    |> List.ofSeq

// ---- CR0064 ----

let private isCatchAll (model: SemanticModel) (c: CatchClauseSyntax) =
    if isNull c.Declaration then
        true
    else
        match model.GetTypeInfo(c.Declaration.Type).Type with
        | null -> false
        | t ->
            t.SpecialType = SpecialType.System_Object
            || t.ToDisplayString() = "System.Exception"

let private readsBinder (c: CatchClauseSyntax) =
    if isNull c.Declaration || c.Declaration.Identifier.IsKind SyntaxKind.None then
        false
    else
        let name = c.Declaration.Identifier.ValueText

        name <> ""
        && (Text.mentionsName name c.Block
            || (not (isNull c.Filter) && Text.mentionsName name c.Filter))

let private hasComment (node: SyntaxNode) =
    node.DescendantTrivia(descendIntoTrivia = true)
    |> Seq.exists (fun t ->
        t.IsKind SyntaxKind.SingleLineCommentTrivia
        || t.IsKind SyntaxKind.MultiLineCommentTrivia)

let private lastStatement (b: BlockSyntax) =
    if b.Statements.Count = 0 then
        None
    else
        Some b.Statements.[b.Statements.Count - 1]

let private returnsLiteral (kind: SyntaxKind) (s: StatementSyntax) =
    match s with
    | :? ReturnStatementSyntax as r -> not (isNull r.Expression) && r.Expression.IsKind kind
    | _ -> false

let private teardownNames =
    set
        [
            "Dispose"
            "Close"
            "Shutdown"
            "Cancel"
            "Complete"
            "Delete"
            "Reset"
            "Kill"
            "Abort"
        ]

let private probeNames =
    set
        [
            "File.Exists"
            "Directory.Exists"
            "File.GetLastWriteTime"
            "Path.GetFullPath"
        ]

let private swallows (tree: SyntaxTree) (model: SemanticModel) : Suggestion list =
    if Text.isTestFile tree then
        []
    else
        tree.GetRoot().DescendantNodes()
        |> Seq.choose (fun n ->
            match n with
            | :? CatchClauseSyntax as c when isCatchAll model c && not (readsBinder c) && not (throwsIn c.Block) ->
                let tryStmt = c.Parent :?> TryStatementSyntax
                let enclosingMethod = Text.enclosingMember c

                let boolProbe =
                    (lastStatement tryStmt.Block
                     |> Option.exists (returnsLiteral SyntaxKind.TrueLiteralExpression))
                    && (lastStatement c.Block
                        |> Option.exists (returnsLiteral SyntaxKind.FalseLiteralExpression))

                let tryMethod =
                    match enclosingMethod with
                    | :? MethodDeclarationSyntax as m ->
                        m.Identifier.ValueText.StartsWith "Try"
                        && (lastStatement c.Block
                            |> Option.exists (returnsLiteral SyntaxKind.FalseLiteralExpression))
                    | _ -> false

                let carriesFailure =
                    match lastStatement c.Block with
                    | Some(:? ReturnStatementSyntax as r) when not (isNull r.Expression) ->
                        let t = model.GetTypeInfo(r.Expression).Type
                        let text = r.Expression.ToString()

                        (not (isNull t)
                         && (t.Name.EndsWith "Exception" || t.Name = "Result" || t.Name.StartsWith "OneOf"))
                        || text.Contains "Error"
                        || text.Contains "Fail"
                    | _ -> false

                let cancellationRethrownBefore =
                    tryStmt.Catches
                    |> Seq.takeWhile (fun other -> not (obj.ReferenceEquals(other, c)))
                    |> Seq.exists (fun other ->
                        not (isNull other.Declaration)
                        && other.Declaration.Type.ToString().Contains "OperationCanceled"
                        && throwsIn other.Block)

                let followedByFailure =
                    match tryStmt.Parent with
                    | :? BlockSyntax as b ->
                        let i = b.Statements.IndexOf tryStmt

                        i + 1 < b.Statements.Count
                        && (match b.Statements.[i + 1] with
                            | :? ThrowStatementSyntax -> true
                            | :? ExpressionStatementSyntax as s ->
                                s.ToString().Contains "Environment.Exit" || s.ToString().Contains "FailFast"
                            | _ -> false)
                    | _ -> false

                let teardown =
                    tryStmt.Block.Statements.Count >= 1
                    && tryStmt.Block.Statements.Count <= 2
                    && (match tryStmt.Block.Statements.[0] with
                        | :? ExpressionStatementSyntax as s ->
                            match s.Expression with
                            | :? InvocationExpressionSyntax as inv -> teardownNames.Contains(Linq.nameOf inv)
                            | _ -> false
                        | _ -> false)

                let probe =
                    tryStmt.Block.DescendantNodes()
                    |> Seq.exists (fun x ->
                        match x with
                        | :? InvocationExpressionSyntax as inv -> probeNames.Contains(inv.Expression.ToString())
                        | _ -> false)

                if
                    boolProbe
                    || tryMethod
                    || carriesFailure
                    || cancellationRethrownBefore
                    || followedByFailure
                    || hasComment c
                then
                    None
                elif teardown then
                    Some(
                        Suggestion.note
                            SwallowCode
                            "A catch-all around a teardown call: a failure to release is hidden; if that is the intent, a comment saying so keeps this quiet"
                            c.CatchKeyword.Span
                    )
                elif probe then
                    Some(
                        Suggestion.note
                            SwallowCode
                            "A catch-all around a probe that answers instead of throwing (File.Exists and friends): the try guards nothing and hides everything else"
                            c.CatchKeyword.Span
                    )
                else
                    Some(
                        Suggestion.note
                            SwallowCode
                            "A catch-all that never reads the exception swallows every failure, the ones it did not mean too: catch the type expected, log it, or rethrow"
                            c.CatchKeyword.Span
                    )
            | _ -> None)
        |> List.ofSeq

// ---- CR0065 ----

let private filters (tree: SyntaxTree) (model: SemanticModel) : Suggestion list =
    let text = tree.GetText()

    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun n ->
        match n with
        | :? CatchClauseSyntax as c when
            isNull c.Filter
            && not (isNull c.Declaration)
            && not (c.Declaration.Identifier.IsKind SyntaxKind.None)
            && c.Block.Statements.Count >= 1
            ->
            match c.Block.Statements.[0] with
            | :? IfStatementSyntax as ifs when isNull ifs.Else ->
                let rethrows =
                    match ifs.Statement with
                    | :? ThrowStatementSyntax as t -> isNull t.Expression
                    | :? BlockSyntax as b when b.Statements.Count = 1 ->
                        match b.Statements.[0] with
                        | :? ThrowStatementSyntax as t -> isNull t.Expression
                        | _ -> false
                    | _ -> false

                let binder = c.Declaration.Identifier.ValueText

                // the condition reads the exception's members, literals, locals and
                // parameters, and calls nothing but the BCL: a filter runs before inner
                // finally blocks, so an effectful condition would reorder effects
                let readsOnlyException =
                    ifs.Condition.DescendantNodesAndSelf()
                    |> Seq.forall (fun x ->
                        match x with
                        | :? AssignmentExpressionSyntax
                        | :? AwaitExpressionSyntax
                        | :? PostfixUnaryExpressionSyntax -> false
                        | :? InvocationExpressionSyntax as inv ->
                            match model.GetSymbolInfo(inv).Symbol with
                            | :? IMethodSymbol as ms -> ms.ContainingNamespace.ToDisplayString().StartsWith "System"
                            | _ -> false
                        | :? IdentifierNameSyntax as id when
                            not (
                                id.Parent :? MemberAccessExpressionSyntax
                                && (id.Parent :?> MemberAccessExpressionSyntax).Name.Span = id.Span
                            )
                            ->
                            id.Identifier.ValueText = binder
                            || (match model.GetSymbolInfo(id).Symbol with
                                | :? ILocalSymbol
                                | :? IParameterSymbol -> true
                                | :? IFieldSymbol as f -> f.IsConst || f.IsReadOnly
                                | :? INamedTypeSymbol -> true
                                | _ -> false)
                        | _ -> true)

                // a rethrow skips the sibling catches; a filter that declines lets them see the
                // exception — so only the last catch of its try converts
                let lastCatch =
                    match c.Parent with
                    | :? TryStatementSyntax as t -> obj.ReferenceEquals(t.Catches.[t.Catches.Count - 1], c)
                    | _ -> false

                if
                    rethrows
                    && lastCatch
                    && Text.mentionsName binder ifs.Condition
                    && readsOnlyException
                    && not (Text.holdsCommentOrDirective ifs)
                then
                    // `if (!cond) throw;` → `when (cond)`; `if (cond) throw;` → `when (!cond)`
                    let filter = BoolReturn.conditionText model ifs.Condition true

                    let edits =
                        [
                            Suggestion.insert c.Declaration.Span.End ($" when ({filter})")
                            Suggestion.replace (Text.statementLineSpan text ifs) ""
                        ]

                    if Guards.speculativeCheck model edits then
                        Some
                            {
                                Code = FilterCode
                                Message =
                                    "A guard that rethrows is an exception filter: it runs before any inner finally and never unwinds for an exception it will not handle"
                                Span = ifs.Span
                                Fixes = [ Suggestion.fix "Make it a filter" FilterCode edits ]
                            }
                    else
                        None
                else
                    None
            | _ -> None
        | _ -> None)
    |> List.ofSeq

// ---- CR0066 / CR0067 / CR0068 ----

let private throwNotes (tree: SyntaxTree) (model: SemanticModel) : Suggestion list =
    let reserved =
        set
            [
                "System.NullReferenceException"
                "System.IndexOutOfRangeException"
                "System.OutOfMemoryException"
                "System.StackOverflowException"
                "System.AccessViolationException"
                "System.ExecutionEngineException"
                "System.ArrayTypeMismatchException"
                "System.Runtime.InteropServices.COMException"
                "System.Runtime.InteropServices.SEHException"
            ]

    let root = tree.GetRoot()

    let inFinally =
        root.DescendantNodes()
        |> Seq.choose (fun n ->
            match n with
            | :? FinallyClauseSyntax as f ->
                match uncaughtThrows f.Block with
                | t :: _ ->
                    Some(
                        Suggestion.note
                            ThrowInFinallyCode
                            "A throw inside finally replaces whatever exception was in flight"
                            t.Span
                    )
                | [] -> None
            | _ -> None)

    let special =
        root.DescendantNodes()
        |> Seq.choose (fun n ->
            let named =
                match n with
                | :? MethodDeclarationSyntax as m ->
                    let name = m.Identifier.ValueText

                    if List.contains name [ "Equals"; "GetHashCode"; "ToString"; "Dispose" ] then
                        Some(name, n)
                    else
                        None
                | :? ConstructorDeclarationSyntax as c when
                    c.Modifiers |> Seq.exists (fun t -> t.IsKind SyntaxKind.StaticKeyword)
                    ->
                    Some("the static constructor", n)
                | :? DestructorDeclarationSyntax -> Some("the finalizer", n)
                | :? ConversionOperatorDeclarationSyntax as c when
                    c.ImplicitOrExplicitKeyword.IsKind SyntaxKind.ImplicitKeyword
                    ->
                    Some("an implicit conversion", n)
                | :? OperatorDeclarationSyntax as o when
                    o.OperatorToken.IsKind SyntaxKind.EqualsEqualsToken
                    || o.OperatorToken.IsKind SyntaxKind.ExclamationEqualsToken
                    ->
                    Some("operator " + o.OperatorToken.Text, n)
                | _ -> None

            match named with
            | Some(name, member') ->
                match uncaughtThrows member' with
                | t :: _ ->
                    Some(
                        Suggestion.note
                            ThrowInSpecialCode
                            $"A throw inside {name}: the runtime and the BCL call it without expecting one — return a value (false, a hash, a placeholder text) instead, and validate elsewhere"
                            t.Span
                    )
                | [] -> None
            | None -> None)

    let reservedThrows =
        root.DescendantNodes()
        |> Seq.choose (fun n ->
            match n with
            | :? ObjectCreationExpressionSyntax as c when
                (c.Parent :? ThrowStatementSyntax || c.Parent :? ThrowExpressionSyntax)
                ->
                match model.GetTypeInfo(c).Type with
                | null -> None
                | t when reserved.Contains(t.ToDisplayString()) ->
                    // a fault-injection table: a switch whose arms throw three or more distinct types
                    let table =
                        c.Ancestors()
                        |> Seq.exists (fun a ->
                            match a with
                            | :? SwitchStatementSyntax
                            | :? SwitchExpressionSyntax ->
                                let types =
                                    a.DescendantNodes()
                                    |> Seq.choose (fun x ->
                                        match x with
                                        | :? ObjectCreationExpressionSyntax as oc when
                                            (oc.Parent :? ThrowStatementSyntax)
                                            || (oc.Parent :? ThrowExpressionSyntax)
                                            ->
                                            Some(oc.Type.ToString())
                                        | _ -> None)
                                    |> Seq.distinct
                                    |> Seq.length

                                types >= 3
                            | _ -> false)

                    if table then
                        None
                    else
                        Some(
                            Suggestion.note
                                ReservedExceptionCode
                                $"'{t.Name}' is the runtime's own: a catch cannot tell this from the real thing — throw InvalidOperationException or ArgumentException"
                                c.Span
                        )
                | _ -> None
            | _ -> None)

    List.concat [ List.ofSeq inFinally; List.ofSeq special; List.ofSeq reservedThrows ]

// ---- CR0069 ----

let private invariants =
    [
        "unreachable"
        "not possible"
        "nyi"
        "internal error"
        "invalid case"
        "should not happen"
        "impossible"
        "not implemented"
        "not supported"
    ]

let private secretWords =
    [
        "auth"
        "session"
        "crypt"
        "token"
        "password"
        "secret"
        "credential"
        "key"
        "connection" // a connection string carries the password
        "pwd"
    ]

let private prints (t: ITypeSymbol) =
    match t with
    | null -> false
    | t ->
        let t =
            if t.OriginalDefinition.SpecialType = SpecialType.System_Nullable_T then
                (t :?> INamedTypeSymbol).TypeArguments.[0]
            else
                t

        t.SpecialType <> SpecialType.None && t.SpecialType <> SpecialType.System_Object
        || t.TypeKind = TypeKind.Enum
        // a record prints, but prints every member: a request record in an exception
        // message is a dump of whatever it carries (a payout request, its bank details)
        || List.contains
            (t.ToDisplayString())
            [
                "System.DateTime"
                "System.DateTimeOffset"
                "System.TimeSpan"
                "System.Guid"
                "System.DateOnly"
                "System.TimeOnly"
            ]

let private messageContexts (tree: SyntaxTree) (model: SemanticModel) (ctx: RuleContext) : Suggestion list =
    if Text.isTestFile tree then
        []
    else
        tree.GetRoot().DescendantNodes()
        |> Seq.choose (fun n ->
            match n with
            | :? ObjectCreationExpressionSyntax as c when
                (c.Parent :? ThrowStatementSyntax || c.Parent :? ThrowExpressionSyntax)
                && not (isNull c.ArgumentList)
                && c.ArgumentList.Arguments.Count >= 1
                ->
                match c.ArgumentList.Arguments.[0].Expression with
                | :? LiteralExpressionSyntax as lit when
                    lit.IsKind SyntaxKind.StringLiteralExpression
                    && not (lit.Token.Text.StartsWith "@")
                    && not (lit.Token.Text.StartsWith "\"\"\"")
                    ->
                    let message = lit.Token.ValueText
                    let lower = message.ToLowerInvariant()

                    match Text.enclosingMember c with
                    | :? MethodDeclarationSyntax as m ->
                        let self = model.GetDeclaredSymbol m

                        let printable =
                            m.ParameterList.Parameters
                            |> Seq.filter (fun p ->
                                match model.GetDeclaredSymbol p with
                                | null -> false
                                | ps -> prints ps.Type)
                            |> List.ofSeq

                        let words =
                            System.Text.RegularExpressions.Regex.Split(lower, @"[^a-z0-9_]+") |> Set.ofArray

                        let mentionsParameter =
                            m.ParameterList.Parameters
                            |> Seq.exists (fun p -> words.Contains(p.Identifier.ValueText.ToLowerInvariant()))

                        let secretSmelling =
                            let names =
                                [
                                    m.Identifier.ValueText
                                    self.ContainingType.Name
                                    self.ContainingNamespace.ToDisplayString()
                                ]
                                @ (m.ParameterList.Parameters
                                   |> Seq.map (fun p -> p.Identifier.ValueText)
                                   |> List.ofSeq)

                            names
                            |> List.exists (fun name ->
                                let l = name.ToLowerInvariant()
                                secretWords |> List.exists (fun w -> l.Contains w))

                        let accessible =
                            match self.DeclaredAccessibility with
                            | Accessibility.Private -> true
                            | Accessibility.Internal -> RuleContext.internalShapeOpen ctx
                            | _ -> RuleContext.publicShapeOpen ctx

                        // an unreachable-by-construction throw is an invariant whatever it says
                        let unreachableType = c.Type.ToString().EndsWith "UnreachableException"

                        // the one-argument constructors of these take the parameter NAME, not a message
                        let nameFirst =
                            c.ArgumentList.Arguments.Count = 1
                            && (let t = c.Type.ToString()

                                t.EndsWith "ArgumentNullException" || t.EndsWith "ArgumentOutOfRangeException")

                        if
                            unreachableType
                            || nameFirst
                            || printable.IsEmpty
                            || printable.Length > 4
                            || message.Contains "{"
                            || message.Contains "}"
                            || message.Trim() = ""
                            || mentionsParameter
                            || lower.Contains(m.Identifier.ValueText.ToLowerInvariant())
                            || invariants |> List.exists (fun i -> lower.Contains i)
                            || secretSmelling
                            || not accessible
                        then
                            None
                        else
                            // the same literal elsewhere in the compilation: a test asserting on it
                            let elsewhere =
                                model.Compilation.SyntaxTrees
                                |> Seq.exists (fun t ->
                                    let count =
                                        System.Text.RegularExpressions.Regex
                                            .Matches(
                                                t.GetRoot().ToString(),
                                                System.Text.RegularExpressions.Regex.Escape(lit.Token.Text)
                                            )
                                            .Count

                                    if t = tree then count > 1 else count > 0)

                            if elsewhere then
                                None
                            else
                                let holes =
                                    printable
                                    |> List.map (fun p ->
                                        p.Identifier.ValueText + ": {" + p.Identifier.ValueText + "}")
                                    |> String.concat ", "

                                // the literal's own spelling, escapes intact, without its quotes
                                let spelled = lit.Token.Text.Substring(1, lit.Token.Text.Length - 2)
                                let trimmed = spelled.TrimEnd('.', '!', ':', ';', ' ')

                                let replacement =
                                    "$\""
                                    + trimmed
                                    + ", calling {nameof("
                                    + m.Identifier.ValueText
                                    + ")} with "
                                    + holes
                                    + "\""

                                let edit = Suggestion.replace lit.Span replacement

                                if Guards.speculativeCheck model [ edit ] then
                                    Some
                                        {
                                            Code = MessageContextCode
                                            Message =
                                                "A constant exception message says what, not with what: quote the caller's arguments (their values, so a PII decision where the data is personal)"
                                            Span = lit.Span
                                            Fixes =
                                                [
                                                    Suggestion.fix
                                                        "Add the arguments to the message"
                                                        MessageContextCode
                                                        [ edit ]
                                                ]
                                        }
                                else
                                    None
                    | _ -> None
                | _ -> None
            | _ -> None)
        |> List.ofSeq

// ---- CR0070 ----

let private carriers =
    [
        "System.Reflection.ReflectionTypeLoadException", [ "LoaderExceptions"; "Types" ]
        "System.AggregateException", [ "InnerExceptions"; "Flatten"; "InnerException"; "Handle" ]
        "System.Net.WebException", [ "Response"; "Status" ]
        "Microsoft.Data.SqlClient.SqlException", [ "Errors"; "Number" ]
        "System.Data.SqlClient.SqlException", [ "Errors"; "Number" ]
        "System.IO.FileNotFoundException", [ "FusionLog"; "FileName" ]
    ]

let private exceptionDetails (tree: SyntaxTree) (model: SemanticModel) : Suggestion list =
    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun n ->
        match n with
        | :? CatchClauseSyntax as c when
            not (isNull c.Declaration)
            && not (c.Declaration.Identifier.IsKind SyntaxKind.None)
            ->
            match model.GetTypeInfo(c.Declaration.Type).Type with
            | null -> None
            | t ->
                match carriers |> List.tryFind (fun (name, _) -> t.ToDisplayString() = name) with
                | None -> None
                | Some(_, members) ->
                    let binder = c.Declaration.Identifier.ValueText

                    let reads (member': string) =
                        (c.Block.ToString().Contains $"{binder}.{member'}")
                        || (not (isNull c.Filter) && c.Filter.ToString().Contains($"{binder}.{member'}"))

                    let readsMessage = c.Block.ToString().Contains(binder + ".Message")

                    if readsMessage && not (members |> List.exists reads) then
                        let all = String.concat "/" members

                        Some(
                            Suggestion.note
                                ExceptionDetailCode
                                $"'{t.Name}' carries the detail in '{members.Head}' and its Message says little: read {all}"
                                c.Declaration.Span
                        )
                    else
                        None
        | _ -> None)
    |> List.ofSeq

let analyze (tree: SyntaxTree) (model: SemanticModel) (ctx: RuleContext) : Suggestion list =
    swallows tree model
    @ filters tree model
    @ throwNotes tree model
    @ messageContexts tree model ctx
    @ exceptionDetails tree model
