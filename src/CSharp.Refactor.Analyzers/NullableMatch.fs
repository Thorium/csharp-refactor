/// CR0004 (idiom, fix): a `HasValue` test followed by `.Value` reads is a
/// pattern match without the throwing accessor.
///
///     x.HasValue ? x.Value + 1 : 0        →  x is { } v ? v + 1 : 0
///     if (x.HasValue) Use(x.Value);       →  if (x is { } v) Use(v);
///     x.HasValue && p(x.Value)            →  x is { } v && p(v)
///     !x.HasValue || p(x.Value)           →  x is not { } v || p(v)
///
/// `.Value` throws when the nullable is empty; after the rewrite the value
/// is only in scope where it exists. The `!HasValue`, `x == null` and
/// `x != null` spellings are the same test with the branches swapped.
///
/// Guards: the receiver is an identifier or dotted pure read typed
/// `System.Nullable<T>` (a custom `HasValue` never matches); the payload
/// branch reads `x.Value` at least once and the other branch never (that
/// code throws today and is not ours to rewrite); the binder is `v`, else
/// `<x>Value`, else `value`, and must not appear anywhere in the enclosing
/// member; the receiver is not assigned inside the branch (a rebinding
/// would leave the pattern variable stale); a pass-through payload
/// (`x.HasValue ? x.Value : d`) is IDE0029/IDE0270's `??` and stays; the
/// shape needs C# 8 (property patterns) and `is not` C# 9; nothing is
/// rewritten inside an expression tree, where patterns cannot go.
module CSharp.Refactor.NullableMatch

open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax
open Microsoft.CodeAnalysis.Text
open System.Collections.Generic

[<Literal>]
let Code = "CR0004"

let private isNullable (model: SemanticModel) (e: ExpressionSyntax) =
    match model.GetTypeInfo(e).Type with
    | null -> false
    | t -> t.OriginalDefinition.SpecialType = SpecialType.System_Nullable_T

/// A receiver a pattern can test: a name or a dotted pure read.
[<TailCall>]
let rec private isReceiver (e: ExpressionSyntax) =
    match e with
    | :? IdentifierNameSyntax -> true
    | :? MemberAccessExpressionSyntax as m -> isReceiver m.Expression
    | :? ThisExpressionSyntax -> true
    | _ -> false

/// `x.HasValue` → x; `!x.HasValue` → x negated; `x == null` / `x != null`
/// with a nullable `x` (either order).
let private nullableTest (model: SemanticModel) (e: ExpressionSyntax) : (ExpressionSyntax * bool) option =
    let rec go (e: ExpressionSyntax) (negated: bool) =
        match e with
        | :? ParenthesizedExpressionSyntax as p -> go p.Expression negated
        | :? PrefixUnaryExpressionSyntax as u when u.IsKind SyntaxKind.LogicalNotExpression ->
            go u.Operand (not negated)
        | :? MemberAccessExpressionSyntax as m when
            m.Name.Identifier.ValueText = "HasValue"
            && isReceiver m.Expression
            && isNullable model m.Expression
            ->
            Some(m.Expression, negated)
        | :? BinaryExpressionSyntax as b when
            (b.IsKind SyntaxKind.EqualsExpression || b.IsKind SyntaxKind.NotEqualsExpression)
            ->
            let isNullLiteral (x: ExpressionSyntax) =
                match x with
                | :? LiteralExpressionSyntax as l -> l.IsKind SyntaxKind.NullLiteralExpression
                | _ -> false

            let receiver =
                if isNullLiteral b.Right then Some b.Left
                elif isNullLiteral b.Left then Some b.Right
                else None

            match receiver with
            | Some r when isReceiver r && isNullable model r ->
                // `x != null` is the positive test, `x == null` the negated one
                let positive = b.IsKind SyntaxKind.NotEqualsExpression
                Some(r, if positive then negated else not negated)
            | _ -> None
        | _ -> None

    go e false

let private sameText (a: SyntaxNode) (b: SyntaxNode) = a.ToString() = b.ToString()

/// Every `x.Value` read of the receiver inside a node.
let private valueReads (model: SemanticModel) (receiver: ExpressionSyntax) (node: SyntaxNode) =
    node.DescendantNodesAndSelf()
    |> Seq.choose (fun n ->
        match n with
        | :? MemberAccessExpressionSyntax as m when
            m.Name.Identifier.ValueText = "Value"
            && Guards.sameReference model m.Expression receiver
            ->
            Some m
        | _ -> None)
    |> List.ofSeq

/// The binder: `v` for a plain local, the member's own name for a dotted
/// read (`bound.Min` → `min`), then `<name>Value`, then `value`; never a
/// keyword, never a name the enclosing member already uses, and never one
/// an earlier site in the same member took — a pattern variable declared
/// in a statement's expression is in scope for the rest of the block.
let private binderFor (receiver: ExpressionSyntax) (scope: SyntaxNode) (taken: HashSet<string>) =
    let lowerFirst (s: string) =
        if s.Length > 0 && s.ToUpperInvariant() = s then
            s.ToLowerInvariant() // `BOOL` → `bool`, not `bOOL`
        elif s.Length > 0 then
            string (System.Char.ToLowerInvariant s.[0]) + s.Substring 1
        else
            s

    let candidates =
        match receiver with
        | :? MemberAccessExpressionSyntax as m ->
            let name = lowerFirst m.Name.Identifier.ValueText
            [ name; name + "Value"; "v"; "value" ]
        | :? IdentifierNameSyntax as i ->
            let name = i.Identifier.ValueText
            [ "v"; name + "Value"; "value" ]
        | _ -> [ "v"; "value" ]

    candidates
    |> List.filter (fun name ->
        SyntaxFacts.GetKeywordKind name = SyntaxKind.None
        && SyntaxFacts.GetContextualKeywordKind name = SyntaxKind.None)
    |> List.tryFind (fun name -> not (taken.Contains name || Text.mentionsName name scope))
    |> Option.map (fun name ->
        taken.Add name |> ignore
        name)

/// The payload branch's text with every `x.Value` spelled as the binder.
let private substituted
    (text: SourceText)
    (branch: SyntaxNode)
    (reads: MemberAccessExpressionSyntax list)
    (binder: string)
    (removed: StatementSyntax option)
    =
    let changes =
        (reads
         |> List.filter (fun r -> removed |> Option.forall (fun s -> not (s.Span.Contains r.Span)))
         |> List.map (fun r -> TextChange(r.Span, binder)))
        @ (removed
           |> Option.map (fun s -> TextChange(Text.statementLineSpan text s, ""))
           |> Option.toList)

    let branchText = text.GetSubText branch.Span
    // spans are absolute; re-base them onto the branch's own text
    let rebased =
        changes
        |> List.map (fun c -> TextChange(TextSpan(c.Span.Start - branch.SpanStart, c.Span.Length), c.NewText))

    branchText.WithChanges(rebased).ToString()

type private Shape =
    /// `test ? whenTrue : whenFalse`
    | Conditional of ConditionalExpressionSyntax
    /// `if (test) then else`
    | IfStatement of IfStatementSyntax
    /// `test && rest` / `test || rest`: the outermost chain node
    | Chain of BinaryExpressionSyntax

let analyze (tree: SyntaxTree) (model: SemanticModel) (ctx: RuleContext) : Suggestion list =
    if ctx.LanguageVersion < LanguageVersion.CSharp8 then
        []
    else
        let text = tree.GetText()
        // binders taken so far, per enclosing member
        let takenIn = Dictionary<int, HashSet<string>>()

        let canNot = ctx.LanguageVersion >= LanguageVersion.CSharp9

        let suggest
            (whole: SyntaxNode)
            (test: ExpressionSyntax)
            (receiver: ExpressionSyntax)
            (negatedPattern: bool)
            (positiveBranch: SyntaxNode)
            (negativeBranch: SyntaxNode option)
            (alias: (string * StatementSyntax) option)
            (render: string -> string -> string)
            =
            if Guards.insideExpressionTree model whole then
                None
            else
                let payloadReads = valueReads model receiver positiveBranch

                let otherReads =
                    negativeBranch
                    |> Option.map (valueReads model receiver)
                    |> Option.defaultValue []

                if
                    payloadReads.IsEmpty
                    || not otherReads.IsEmpty
                    || Text.assignsTo (receiver.ToString()) positiveBranch
                then
                    None
                else
                    let scope = Text.enclosingMember whole

                    let taken =
                        match takenIn.TryGetValue scope.SpanStart with
                        | true, set -> set
                        | _ ->
                            let set = HashSet<string>()
                            takenIn.[scope.SpanStart] <- set
                            set

                    // `var name = x.Value;` heading the branch names the binder and goes
                    let mentionedOutside (name: string) =
                        scope.DescendantTokens()
                        |> Seq.exists (fun t ->
                            t.IsKind SyntaxKind.IdentifierToken
                            && t.ValueText = name
                            && not (positiveBranch.Span.Contains t.Span))

                    let choice =
                        match alias with
                        | Some(name, stmt) when
                            not (taken.Contains name)
                            && not (mentionedOutside name)
                            && not (Text.assignsTo name positiveBranch)
                            ->
                            taken.Add name |> ignore
                            Some(name, Some stmt)
                        | _ -> binderFor receiver scope taken |> Option.map (fun b -> b, None)

                    match choice with
                    | None -> None
                    | Some(binder, removed) ->
                        let pattern =
                            if negatedPattern then
                                (if canNot then
                                     Some(receiver.ToString() + " is not { } " + binder)
                                 else
                                     None)
                            else
                                Some(receiver.ToString() + " is { } " + binder)

                        pattern
                        |> Option.map (fun pattern ->
                            let payload = substituted text positiveBranch payloadReads binder removed
                            let replacement = render pattern payload

                            {
                                Code = Code
                                Message =
                                    "A HasValue test followed by .Value is a pattern match without the throwing accessor"
                                Span = test.Span
                                Fixes =
                                    [
                                        Suggestion.fix
                                            "Match the value"
                                            Code
                                            [ Suggestion.replace whole.Span replacement ]
                                    ]
                            })

        tree.GetRoot().DescendantNodes()
        |> Seq.choose (fun node ->
            match node with
            | :? ConditionalExpressionSyntax as c ->
                match nullableTest model c.Condition with
                | Some(receiver, negated) ->
                    let payload, other =
                        if negated then
                            c.WhenFalse, c.WhenTrue
                        else
                            c.WhenTrue, c.WhenFalse

                    // a pass-through payload is `x ?? d`, the IDE's
                    if sameText payload (SyntaxFactory.ParseExpression(receiver.ToString() + ".Value")) then
                        None
                    else
                        // the swapped spelling keeps the positive test: `x is { } v ? v + 1 : d`
                        // the layout between the three parts stays as written
                        let gap1 =
                            text.ToString(TextSpan.FromBounds(c.Condition.Span.End, c.WhenTrue.SpanStart))

                        let gap2 =
                            text.ToString(TextSpan.FromBounds(c.WhenTrue.Span.End, c.WhenFalse.SpanStart))

                        suggest c c.Condition receiver false payload (Some other) None (fun pattern payloadText ->
                            pattern + gap1 + payloadText + gap2 + other.ToString())
                | None -> None
            | :? IfStatementSyntax as ifs ->
                match nullableTest model ifs.Condition with
                | Some(receiver, negated) ->
                    let positive, other =
                        if negated then
                            (if isNull ifs.Else then
                                 None
                             else
                                 Some(ifs.Else.Statement :> SyntaxNode)),
                            Some(ifs.Statement :> SyntaxNode)
                        else
                            Some(ifs.Statement :> SyntaxNode),
                            (if isNull ifs.Else then
                                 None
                             else
                                 Some(ifs.Else.Statement :> SyntaxNode))

                    match positive with
                    | None -> None
                    | Some payload ->
                        let alias =
                            match payload with
                            | :? BlockSyntax as b when b.Statements.Count > 0 ->
                                match b.Statements.[0] with
                                | :? LocalDeclarationStatementSyntax as d when
                                    d.Declaration.Type.IsVar
                                    && d.Declaration.Variables.Count = 1
                                    && not (isNull d.Declaration.Variables.[0].Initializer)
                                    && (match d.Declaration.Variables.[0].Initializer.Value with
                                        | :? MemberAccessExpressionSyntax as m ->
                                            m.Name.Identifier.ValueText = "Value"
                                            && Guards.sameReference model m.Expression receiver
                                        | _ -> false)
                                    ->
                                    Some(d.Declaration.Variables.[0].Identifier.ValueText, d :> StatementSyntax)
                                | _ -> None
                            | _ -> None

                        suggest ifs ifs.Condition receiver false payload other alias (fun pattern payloadText ->
                            // the branches swap with the test: the payload branch
                            // follows the pattern, the other becomes the else
                            let head = "if (" + pattern + ")"

                            let gap =
                                text.ToString(
                                    TextSpan.FromBounds(ifs.CloseParenToken.Span.End, ifs.Statement.SpanStart)
                                )

                            match other with
                            | Some o ->
                                let elseGap =
                                    text.ToString(TextSpan.FromBounds(ifs.Statement.Span.End, ifs.Else.SpanStart))

                                let elseGapInner =
                                    text.ToString(
                                        TextSpan.FromBounds(
                                            ifs.Else.ElseKeyword.Span.End,
                                            ifs.Else.Statement.SpanStart
                                        )
                                    )

                                head + gap + payloadText + elseGap + "else" + elseGapInner + o.ToString()
                            | None -> head + gap + payloadText)
                | None -> None
            | :? BinaryExpressionSyntax as b when
                (b.IsKind SyntaxKind.LogicalAndExpression
                 || b.IsKind SyntaxKind.LogicalOrExpression)
                && not (b.Parent :? BinaryExpressionSyntax && b.Parent.IsKind(b.Kind()))
                ->
                // the outermost chain node: `x.HasValue && p && q` is
                // App(App(x.HasValue, p), q) — the test is the leftmost operand
                let rec leftmost (e: ExpressionSyntax) =
                    match e with
                    | :? BinaryExpressionSyntax as inner when inner.IsKind(b.Kind()) -> leftmost inner.Left
                    | other -> other

                let test = leftmost b

                match nullableTest model test with
                | Some(receiver, negated) ->
                    // `&&` wants the positive test, `||` the negated one
                    let wants = b.IsKind SyntaxKind.LogicalOrExpression

                    if negated <> wants then
                        None
                    else
                        let rest = TextSpan.FromBounds(test.Span.End, b.Span.End)
                        let restNode = b // the reads are searched in the whole chain minus the test

                        suggest b test receiver wants restNode None None (fun pattern _ ->
                            let restText = text.ToString rest

                            let reads =
                                valueReads model receiver b
                                |> List.filter (fun r -> r.SpanStart >= test.Span.End)

                            let binder = pattern.Substring(pattern.LastIndexOf ' ' + 1)

                            let restChanged =
                                SourceText
                                    .From(restText)
                                    .WithChanges(
                                        reads
                                        |> List.map (fun r ->
                                            TextChange(TextSpan(r.SpanStart - rest.Start, r.Span.Length), binder))
                                    )
                                    .ToString()

                            pattern + restChanged)
                | None -> None
            | _ -> None)
        |> List.ofSeq
