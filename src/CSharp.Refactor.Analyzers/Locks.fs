/// Locking shapes.
///
/// CR0047 (correctness, note; editor fix): `lock (this)`, `lock ("cache")`,
/// `lock (typeof(T))`, `lock (x.GetType())`, a lock on a boxed value —
/// a lock on something the outside world can lock too (or, for a boxed
/// value, on a fresh box every time). Priority. The editor offers a
/// `private readonly object _gate = new();` (or `System.Threading.Lock`
/// where it resolves) beside the locked value when the locked thing
/// belongs to this file by nature; a process-wide singleton from
/// elsewhere (`Console.Out`) gets the note alone.
///
/// CR0048 (correctness, fix): `Monitor.Enter(x); try { … } finally {
/// Monitor.Exit(x); }` is `lock (x) { … }`. Guards: the single-argument
/// `Enter` (the `ref bool taken` overload carries protocol); identical
/// operand text in `Enter` and `Exit`; the `try`/`finally` immediately
/// follows the `Enter`; the `finally` holds nothing but the `Exit`; no
/// `await` or `yield` in the body (illegal in a `lock`); no directive in
/// the region; no `Monitor.Exit`/`Enter`/`TryEnter` in the body (a
/// `lock` releasing an already released monitor throws); no comment on the
/// `Enter` line or the `finally` (both go); own-line statements. A bare
/// `Enter` with no guarding `try` is the leak note.
module CSharp.Refactor.Locks

open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax
open Microsoft.CodeAnalysis.Text

[<Literal>]
let WeakLockCode = "CR0047"

[<Literal>]
let MonitorCode = "CR0048"

// ---- CR0047 ----

let private weakLocks (tree: SyntaxTree) (model: SemanticModel) (ctx: RuleContext) : Suggestion list =
    let text = tree.GetText()

    let lockType = model.Compilation.GetTypeByMetadataName "System.Threading.Lock"

    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun n ->
        match n with
        | :? LockStatementSyntax as l ->
            let e = l.Expression
            let t = model.GetTypeInfo(e).Type

            let why =
                match e with
                | :? ThisExpressionSyntax -> Some "'this' can be locked by any code holding a reference to the object"
                | :? TypeOfExpressionSyntax -> Some "a Type object is process-wide: any code can lock it"
                | :? LiteralExpressionSyntax as lit when lit.IsKind SyntaxKind.StringLiteralExpression ->
                    Some "a string literal is interned: every 'lock' on the same text shares it"
                | _ when not (isNull t) && t.SpecialType = SpecialType.System_String ->
                    Some "a string may be interned: another 'lock' on equal text shares it"
                | _ when not (isNull t) && t.ToDisplayString() = "System.Type" ->
                    Some "a Type object is process-wide: any code can lock it"
                | _ when not (isNull t) && t.IsValueType ->
                    Some "a value type boxes to a fresh object on every 'lock': nothing is ever held"
                | _ when e.ToString().Contains "GetType()" ->
                    Some "a Type object is process-wide: any code can lock it"
                | _ -> None

            match why with
            | None -> None
            | Some why ->
                // the editor's lock object: a field before the enclosing member
                let ownsIt =
                    match e with
                    | :? ThisExpressionSyntax
                    | :? TypeOfExpressionSyntax
                    | :? LiteralExpressionSyntax -> true
                    | _ -> false

                let member' =
                    l.Ancestors()
                    |> Seq.tryPick (fun a ->
                        match a with
                        | :? MemberDeclarationSyntax as m -> Some m
                        | _ -> None)

                let fixes =
                    match member', ownsIt with
                    | Some m, true when not (Text.mentionsName "_gate" (Text.enclosingMember l).Parent) ->
                        let isStatic =
                            (e :? TypeOfExpressionSyntax)
                            || m.Modifiers |> Seq.exists (fun t -> t.IsKind SyntaxKind.StaticKeyword)

                        // the gate's type and spelling at the file's language version: `Lock` is
                        // C# 13's, the target-typed `new()` C# 9's
                        let gateType, init =
                            if
                                not (isNull lockType)
                                && ctx.LanguageVersion >= LanguageVersion.CSharp13
                                && Linq.resolvesBare model m.SpanStart "System.Threading" "Lock"
                            then
                                "Lock", "new()"
                            elif ctx.LanguageVersion >= LanguageVersion.CSharp9 then
                                "object", "new()"
                            else
                                "object", "new object()"

                        let indent = Text.leadingWhitespace text m.SpanStart
                        let newline = Text.newlineAt text m.SpanStart

                        let declaration =
                            indent
                            + "private "
                            + (if isStatic then "static " else "")
                            + "readonly "
                            + gateType
                            + " _gate = "
                            + init
                            + ";"
                            + newline
                            + newline

                        let line = text.Lines.GetLineFromPosition m.SpanStart

                        [
                            Suggestion.fix
                                "Lock a private gate object"
                                WeakLockCode
                                [ Suggestion.insert line.Start declaration; Suggestion.replace e.Span "_gate" ]
                            |> Suggestion.editorOnly
                        ]
                    | _ -> []

                Some
                    {
                        Code = WeakLockCode
                        Message = "Locking on '" + e.ToString() + "': " + why + "; lock a private object instead"
                        Span = e.Span
                        Fixes = fixes
                    }
        | _ -> None)
    |> List.ofSeq

// ---- CR0048 ----

let private monitorLocks (tree: SyntaxTree) (model: SemanticModel) : Suggestion list =
    let text = tree.GetText()

    let isMonitorCall (name: string) (s: StatementSyntax) : ExpressionSyntax option =
        match s with
        | :? ExpressionStatementSyntax as es ->
            match es.Expression with
            | :? InvocationExpressionSyntax as inv when
                inv.Expression.ToString() = "Monitor." + name
                && inv.ArgumentList.Arguments.Count = 1
                && inv.ArgumentList.Arguments.[0].RefKindKeyword.IsKind SyntaxKind.None
                ->
                match model.GetSymbolInfo(inv).Symbol with
                | :? IMethodSymbol as m when m.ContainingType.ToDisplayString() = "System.Threading.Monitor" ->
                    Some inv.ArgumentList.Arguments.[0].Expression
                | _ -> None
            | _ -> None
        | _ -> None

    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun n ->
        match n with
        | :? BlockSyntax as b ->
            b.Statements
            |> Seq.indexed
            |> Seq.tryPick (fun (i, s) ->
                match isMonitorCall "Enter" s with
                | None -> None
                | Some operand ->
                    let next =
                        if i + 1 < b.Statements.Count then
                            Some b.Statements.[i + 1]
                        else
                            None

                    match next with
                    | Some(:? TryStatementSyntax as t) when
                        t.Catches.Count = 0
                        && not (isNull t.Finally)
                        && t.Finally.Block.Statements.Count = 1
                        && (isMonitorCall "Exit" t.Finally.Block.Statements.[0]
                            |> Option.exists (fun e -> e.ToString() = operand.ToString()))
                        ->
                        let bodyBlocks =
                            t.Block.DescendantNodes()
                            |> Seq.exists (fun x -> x :? AwaitExpressionSyntax || x :? YieldStatementSyntax)

                        // a body that enters or exits the monitor itself is choreography a `lock` would break
                        let handlesMonitor =
                            t.Block.DescendantNodes()
                            |> Seq.exists (fun x ->
                                match x with
                                | :? InvocationExpressionSyntax as inv ->
                                    let e = inv.Expression.ToString()
                                    e = "Monitor.Exit" || e = "Monitor.Enter" || e = "Monitor.TryEnter"
                                | _ -> false)

                        // the Enter line and the finally go: a comment on either would go with them
                        let commented =
                            Text.holdsCommentOrDirective s || Text.holdsCommentOrDirective t.Finally

                        let region = TextSpan.FromBounds(s.SpanStart, t.Span.End)

                        let directive =
                            t.DescendantTrivia(descendIntoTrivia = true)
                            |> Seq.exists (fun tr -> tr.IsDirective)
                            || s.DescendantTrivia(descendIntoTrivia = true)
                               |> Seq.exists (fun tr -> tr.IsDirective)

                        if bodyBlocks || directive || handlesMonitor || commented then
                            None
                        else
                            // `lock (x)` takes the try's block verbatim; the finally's
                            // line and the Enter line go
                            let blockText = text.ToString t.Block.Span

                            let replacement =
                                "lock ("
                                + operand.ToString()
                                + ")"
                                + Text.newlineAt text s.SpanStart
                                + Text.leadingWhitespace text s.SpanStart
                                + blockText

                            Some
                                {
                                    Code = MonitorCode
                                    Message = "Monitor.Enter with a try/finally Exit is the lock statement"
                                    Span = s.Span
                                    Fixes =
                                        [
                                            Suggestion.fix
                                                "Use lock"
                                                MonitorCode
                                                [ Suggestion.replace region replacement ]
                                        ]
                                }
                    | _ ->
                        // no guarding try: an exception leaves the monitor held
                        let guarded =
                            b.Statements
                            |> Seq.skip (i + 1)
                            |> Seq.exists (fun x -> x :? TryStatementSyntax)

                        if guarded then
                            None
                        else
                            Some(
                                Suggestion.note
                                    MonitorCode
                                    "Monitor.Enter without a try/finally: an exception before Exit leaves the monitor held for good — a lock statement releases it"
                                    s.Span
                            ))
        | _ -> None)
    |> List.ofSeq

let analyze (tree: SyntaxTree) (model: SemanticModel) (ctx: RuleContext) : Suggestion list =
    weakLocks tree model ctx @ monitorLocks tree model
