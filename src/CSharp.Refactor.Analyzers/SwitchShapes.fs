/// Two switch cleanups and one switch defect, on statements and
/// expressions alike:
///
/// 1. Guard that IS the constant (CR0010, fix): `case var x when x == "A":`
///    is `case "A":`, and `var x when x == 3 =>` is `3 =>`. Per arm, gated
///    on the pattern being a bare `var` binder, the guard EXACTLY `x == c`
///    (either side) through the built-in `==` against a constant a pattern
///    can spell, and the body never mentioning the binder — after the
///    rewrite it no longer exists.
///
/// 2. Adjacent same-body arms fold (CR0009, fix): contiguous switch
///    sections with textually identical bodies and no guards stack their
///    labels; contiguous switch-expression arms with identical bodies
///    become one `or` pattern. Match order is semantics, so only a
///    CONTIGUOUS run merges, in place. Patterns must bind nothing (a
///    designation or `var` refuses); a comment inside a dropped body holds
///    the fix (the comment guard), while each kept label keeps its own.
///
/// 3. Unfinished branch (CR0012, correctness, fix): an arm that SAYS it is
///    unfinished — a comment between the label and its value reading
///    "not supported", "not implemented", "unsupported", "not yet", "NYI",
///    "stub", "placeholder", "unfinished", "TBD" — and returns a stand-in
///    (`null`, `default`, `false`, `0`, `-1`, `""`, `string.Empty`, an
///    empty collection) becomes `throw new NotImplementedException()`, so
///    the gap reports itself instead of reaching callers as a real-looking
///    result. Only where sibling arms actually compute (a table of
///    constants is data); a bare TODO/FIXME never accuses; commented-out
///    code (an identifier applied, a string literal, a format hole) is not
///    a note; a `null` returned by a method whose return type is nullable
///    (`T?`) and a `false` from a `Try…(out …)` are the legitimate "no
///    result" and stay.
module CSharp.Refactor.SwitchShapes

open System
open System.Text.RegularExpressions
open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax
open Microsoft.CodeAnalysis.Text

[<Literal>]
let GuardCode = "CR0010"

[<Literal>]
let MergeCode = "CR0009"

[<Literal>]
let UnimplementedCode = "CR0012"

let private whitespace = Regex(@"\s+", RegexOptions.Compiled)
let private normalized (s: string) = whitespace.Replace(s, " ").Trim()

let private commentTrivia (t: SyntaxTrivia) =
    t.IsKind SyntaxKind.SingleLineCommentTrivia
    || t.IsKind SyntaxKind.MultiLineCommentTrivia

// ---- CR0010: the guard that is the constant ----

/// A constant a pattern can spell: a literal, a `const`, an enum member.
let private patternConstant (model: SemanticModel) (e: ExpressionSyntax) =
    let cv = model.GetConstantValue e

    if cv.HasValue then
        match e with
        | :? LiteralExpressionSyntax
        | :? IdentifierNameSyntax
        | :? MemberAccessExpressionSyntax -> Some(e.ToString())
        | :? PrefixUnaryExpressionSyntax as u when u.IsKind SyntaxKind.UnaryMinusExpression -> Some(e.ToString())
        | _ -> None
    else
        None

let private mentions (name: string) (node: SyntaxNode) =
    node.DescendantTokens()
    |> Seq.exists (fun t -> t.IsKind SyntaxKind.IdentifierToken && t.ValueText = name)

/// `x == c` / `c == x` for the binder `x`: the constant's text.
let private guardConstant (model: SemanticModel) (binder: string) (guard: ExpressionSyntax) =
    match guard with
    | :? BinaryExpressionSyntax as b when b.IsKind SyntaxKind.EqualsExpression && Guards.isBuiltinOperator model b ->
        let isBinder (e: ExpressionSyntax) =
            match e with
            | :? IdentifierNameSyntax as i -> i.Identifier.ValueText = binder
            | _ -> false

        if isBinder b.Left then patternConstant model b.Right
        elif isBinder b.Right then patternConstant model b.Left
        else None
    | _ -> None

let private varBinder (p: PatternSyntax) =
    match p with
    | :? VarPatternSyntax as v ->
        match v.Designation with
        | :? SingleVariableDesignationSyntax as d -> ValueSome d.Identifier.ValueText
        | _ -> ValueNone
    | _ -> ValueNone

let private guardIsConstant (root: SyntaxNode) (model: SemanticModel) : Suggestion list =
    root.DescendantNodes()
    |> Seq.choose (fun node ->
        match node with
        | :? CasePatternSwitchLabelSyntax as label when not (isNull label.WhenClause) ->
            match varBinder label.Pattern with
            | ValueSome binder ->
                match guardConstant model binder label.WhenClause.Condition with
                | Some constant ->
                    let section = label.Parent :?> SwitchSectionSyntax

                    if section.Statements |> Seq.exists (mentions binder) then
                        None
                    else
                        let span = TextSpan.FromBounds(label.Pattern.SpanStart, label.WhenClause.Span.End)

                        Some
                            {
                                Code = GuardCode
                                Message = $"The guard only tests the binder against {constant}; that is the pattern"
                                Span = span
                                Fixes =
                                    [
                                        Suggestion.fix
                                            "Use the constant pattern"
                                            GuardCode
                                            [ Suggestion.replace span constant ]
                                    ]
                            }
                | None -> None
            | ValueNone -> None
        | :? SwitchExpressionArmSyntax as arm when not (isNull arm.WhenClause) ->
            match varBinder arm.Pattern with
            | ValueSome binder ->
                match guardConstant model binder arm.WhenClause.Condition with
                | Some constant when not (mentions binder arm.Expression) ->
                    let span = TextSpan.FromBounds(arm.Pattern.SpanStart, arm.WhenClause.Span.End)

                    Some
                        {
                            Code = GuardCode
                            Message = $"The guard only tests the binder against {constant}; that is the pattern"
                            Span = span
                            Fixes =
                                [
                                    Suggestion.fix
                                        "Use the constant pattern"
                                        GuardCode
                                        [ Suggestion.replace span constant ]
                                ]
                        }
                | _ -> None
            | ValueNone -> None
        | _ -> None)
    |> List.ofSeq

// ---- CR0009: adjacent same-body arms ----

/// A pattern that provably binds nothing.
let rec private bindsNothing (p: PatternSyntax) =
    match p with
    | :? ConstantPatternSyntax
    | :? DiscardPatternSyntax
    | :? TypePatternSyntax
    | :? RelationalPatternSyntax -> true
    | :? UnaryPatternSyntax as u -> bindsNothing u.Pattern
    | :? BinaryPatternSyntax as b -> bindsNothing b.Left && bindsNothing b.Right
    | :? ParenthesizedPatternSyntax as pp -> bindsNothing pp.Pattern
    | _ -> false

/// A switch-expression arm that can join an `or` run: the discard arm is the
/// `default` of the expression form — `"A" or _` is legal and reads as a
/// mistake, and the explicit arm before it is usually deliberate.
let private armFoldable (p: PatternSyntax) =
    bindsNothing p && not (p :? DiscardPatternSyntax)

let private labelBindsNothing (l: SwitchLabelSyntax) =
    match l with
    | :? CaseSwitchLabelSyntax -> true
    | :? CasePatternSwitchLabelSyntax as c -> isNull c.WhenClause && bindsNothing c.Pattern
    | _ -> false // default: stacking it with cases is legal but reads as a mistake

let private hasComment (node: SyntaxNode) = Text.holdsCommentOrDirective node

let private endOfLine (token: SyntaxToken) =
    token.TrailingTrivia
    |> Seq.tryFind (fun t -> t.IsKind SyntaxKind.EndOfLineTrivia)
    |> Option.map (fun t -> t.ToString())
    |> Option.defaultValue "\n"

/// Contiguous runs of a list, grouped by a key.
let private runsBy (key: 'a -> string option) (items: 'a list) : 'a list list =
    let rec go (acc: 'a list list) (current: 'a list) (currentKey: string option) (rest: 'a list) =
        match rest with
        | [] -> List.rev (if current.IsEmpty then acc else List.rev current :: acc)
        | x :: xs ->
            match key x with
            | Some k when currentKey = Some k -> go acc (x :: current) currentKey xs
            | Some k -> go (if current.IsEmpty then acc else List.rev current :: acc) [ x ] (Some k) xs
            | None -> go (if current.IsEmpty then acc else List.rev current :: acc) [] None xs

    go [] [] None items

let private mergeArms (root: SyntaxNode) (text: SourceText) (wrapColumn: int) : Suggestion list =
    let sections =
        root.DescendantNodes()
        |> Seq.choose (fun n ->
            match n with
            | :? SwitchStatementSyntax as s ->
                let runs =
                    s.Sections
                    |> List.ofSeq
                    |> runsBy (fun section ->
                        if section.Labels |> Seq.forall labelBindsNothing && section.Statements.Count > 0 then
                            Some(
                                section.Statements
                                |> Seq.map (fun st -> normalized (st.ToString()))
                                |> String.concat "|"
                            )
                        else
                            None)
                    |> List.filter (fun run -> run.Length > 1)

                Some(
                    runs
                    |> List.choose (fun run ->
                        let dropped = run |> List.take (run.Length - 1)

                        if dropped |> List.exists (fun sec -> sec.Statements |> Seq.exists hasComment) then
                            None
                        else
                            let edits =
                                dropped
                                |> List.map (fun sec ->
                                    let lastLabel = sec.Labels.[sec.Labels.Count - 1]
                                    let colon = lastLabel.ColonToken
                                    let lastStatement = sec.Statements.[sec.Statements.Count - 1]
                                    let span = TextSpan.FromBounds(colon.Span.End, lastStatement.FullSpan.End)
                                    Suggestion.replace span (endOfLine colon))

                            let first = run.Head
                            let last = run.[run.Length - 1]

                            Some
                                {
                                    Code = MergeCode
                                    Message = $"{run.Length} adjacent cases share one body; stack their labels"
                                    Span = TextSpan.FromBounds(first.SpanStart, last.Span.End)
                                    Fixes = [ Suggestion.fix "Stack the labels" MergeCode edits ]
                                })
                )
            | :? SwitchExpressionSyntax as s ->
                let runs =
                    s.Arms
                    |> List.ofSeq
                    |> runsBy (fun arm ->
                        if isNull arm.WhenClause && armFoldable arm.Pattern then
                            Some(normalized (arm.Expression.ToString()))
                        else
                            None)
                    |> List.filter (fun run -> run.Length > 1)

                Some(
                    runs
                    |> List.choose (fun run ->
                        let last = run.[run.Length - 1]
                        let dropped = run |> List.take (run.Length - 1)
                        let span = TextSpan.FromBounds(run.Head.SpanStart, last.SpanStart)

                        // everything between the first arm's start and the
                        // last arm's start goes: the dropped arms, commas and
                        // line breaks — a comment there would go with them
                        let between = TextSpan.FromBounds(run.Head.SpanStart, last.SpanStart)

                        let commentBetween =
                            root.DescendantTrivia(between, descendIntoTrivia = true)
                            |> Seq.exists (fun t -> commentTrivia t || t.IsDirective)

                        if commentBetween then
                            None
                        else
                            // one line while it fits; past the wrap column each
                            // pattern takes its own line, the `or`s indented under
                            // the first (six event types joined made a 200-columns)
                            let armLine = text.Lines.GetLineFromPosition run.Head.SpanStart
                            let armColumn = run.Head.SpanStart - armLine.Start
                            let names = run |> List.map (fun a -> a.Pattern.ToString())
                            let oneLine = String.Join(" or ", names) + " => " + last.Expression.ToString()

                            let joiner =
                                if armColumn + oneLine.Length + 1 <= wrapColumn then
                                    " or "
                                else
                                    "\n" + String(' ', armColumn + 4) + "or "

                            let patterns =
                                dropped |> List.map (fun a -> a.Pattern.ToString()) |> String.concat joiner

                            Some
                                {
                                    Code = MergeCode
                                    Message =
                                        $"{run.Length} adjacent arms share one body; fold them into an 'or' pattern"
                                    Span = TextSpan.FromBounds(run.Head.SpanStart, last.Span.End)
                                    Fixes =
                                        [
                                            Suggestion.fix
                                                "Fold into an 'or' pattern"
                                                MergeCode
                                                [ Suggestion.replace span (patterns + joiner) ]
                                        ]
                                })
                )
            | _ -> None)
        |> List.concat

    sections

// ---- CR0012: the unfinished branch ----

let private stubPhrases =
    [
        "not supported"
        "unsupported"
        "not implemented"
        "unimplemented"
        "not yet"
        "nyi"
        "stub"
        "placeholder"
        "unfinished"
        "to be implemented"
        "tbd"
        "not handled"
        "unhandled yet"
    ]

/// Commented-OUT code is not a note about the branch.
let private looksLikeCode (comment: string) =
    let body = comment.TrimStart('/', '*', ' ').TrimEnd('*', '/', ' ')

    Regex.IsMatch(body, @"^[A-Za-z_][\w.]*\s*(\(|""|\$"")")
    || body.Contains "Console.Write"
    || body.Contains ";"
    || Regex.IsMatch(body, @"\{\w+\}")

let private saysUnfinished (comment: string) =
    let lower = comment.ToLowerInvariant()
    not (looksLikeCode comment) && stubPhrases |> List.exists lower.Contains

/// A value that stands in for a result — only a comment turns it into
/// evidence.
let private isPlaceholder (e: ExpressionSyntax) =
    match e with
    | :? LiteralExpressionSyntax as l ->
        l.IsKind SyntaxKind.NullLiteralExpression
        || l.IsKind SyntaxKind.DefaultLiteralExpression
        || l.IsKind SyntaxKind.FalseLiteralExpression
        || (l.IsKind SyntaxKind.NumericLiteralExpression && (l.Token.ValueText = "0"))
        || (l.IsKind SyntaxKind.StringLiteralExpression && l.Token.ValueText = "")
    | :? PrefixUnaryExpressionSyntax as u when u.IsKind SyntaxKind.UnaryMinusExpression ->
        match u.Operand with
        | :? LiteralExpressionSyntax as l -> l.Token.ValueText = "1"
        | _ -> false
    | :? DefaultExpressionSyntax -> true
    | :? MemberAccessExpressionSyntax as m -> m.ToString() = "string.Empty" || m.ToString() = "String.Empty"
    | :? CollectionExpressionSyntax as c -> c.Elements.Count = 0
    | :? InvocationExpressionSyntax as i -> i.ToString().StartsWith "Array.Empty<"
    | :? ArrayCreationExpressionSyntax as a -> a.ToString().EndsWith "[0]"
    | _ -> false

/// The comments in a span, as text.
let private commentsIn (root: SyntaxNode) (span: TextSpan) =
    root.DescendantTrivia(span, descendIntoTrivia = true)
    |> Seq.filter commentTrivia
    |> Seq.map (fun t -> t.ToString())
    |> List.ofSeq

/// Does a sibling arm compute rather than hand back a literal?
let private computes (e: ExpressionSyntax) =
    match e with
    | :? LiteralExpressionSyntax
    | :? DefaultExpressionSyntax -> false
    | :? MemberAccessExpressionSyntax as m -> not (m.ToString().StartsWith "string.Empty")
    | _ -> true

/// The method whose result this is: nullable return or a `Try…(out)`
/// probe makes `null`/`false` the legitimate no-result.
let private legitimateNoResult (model: SemanticModel) (node: SyntaxNode) (value: ExpressionSyntax) =
    let enclosing =
        node.Ancestors()
        |> Seq.tryPick (fun a ->
            match a with
            | :? MethodDeclarationSyntax as m -> Some(m :> SyntaxNode)
            | :? LocalFunctionStatementSyntax as l -> Some(l :> SyntaxNode)
            | :? LambdaExpressionSyntax as l -> Some(l :> SyntaxNode)
            | _ -> None)

    match enclosing with
    | Some(:? MethodDeclarationSyntax as m) ->
        let returnsNullable =
            match model.GetDeclaredSymbol m with
            | null -> false
            | s ->
                s.ReturnType.NullableAnnotation = NullableAnnotation.Annotated
                || (s.ReturnType.OriginalDefinition.SpecialType = SpecialType.System_Nullable_T)

        let isTryProbe =
            m.Identifier.ValueText.StartsWith "Try"
            && m.ParameterList.Parameters
               |> Seq.exists (fun p -> p.Modifiers |> Seq.exists (fun md -> md.IsKind SyntaxKind.OutKeyword))

        let isNullish =
            match value with
            | :? LiteralExpressionSyntax as l ->
                l.IsKind SyntaxKind.NullLiteralExpression
                || l.IsKind SyntaxKind.DefaultLiteralExpression
            | :? DefaultExpressionSyntax -> true
            | _ -> false

        let isFalse =
            match value with
            | :? LiteralExpressionSyntax as l -> l.IsKind SyntaxKind.FalseLiteralExpression
            | _ -> false

        (returnsNullable && isNullish) || (isTryProbe && isFalse)
    | _ -> false

/// The spelling of NotImplementedException that resolves at the site.
let private throwText (model: SemanticModel) (position: int) =
    let resolves =
        model.LookupNamespacesAndTypes(position, name = "NotImplementedException")
        |> Seq.exists (fun s -> s.ToDisplayString() = "System.NotImplementedException")

    if resolves then
        "new NotImplementedException()"
    else
        "new System.NotImplementedException()"

let private unimplemented (root: SyntaxNode) (model: SemanticModel) : Suggestion list =
    root.DescendantNodes()
    |> Seq.choose (fun node ->
        match node with
        | :? SwitchSectionSyntax as section when section.Statements.Count = 1 ->
            match section.Statements.[0] with
            | :? ReturnStatementSyntax as r when not (isNull r.Expression) && isPlaceholder r.Expression ->
                let lastLabel = section.Labels.[section.Labels.Count - 1]
                let between = TextSpan.FromBounds(lastLabel.ColonToken.Span.End, r.SpanStart)
                let accused = commentsIn root between |> List.exists saysUnfinished
                let switch = section.Parent :?> SwitchStatementSyntax

                let siblingsCompute =
                    switch.Sections
                    |> Seq.filter (fun s -> not (obj.ReferenceEquals(s, section)))
                    |> Seq.exists (fun s ->
                        s.Statements
                        |> Seq.exists (fun st ->
                            match st with
                            | :? ReturnStatementSyntax as rr -> not (isNull rr.Expression) && computes rr.Expression
                            | :? BreakStatementSyntax
                            | :? ThrowStatementSyntax -> false
                            | _ -> true))

                if
                    accused
                    && siblingsCompute
                    && not (legitimateNoResult model section r.Expression)
                then
                    Some
                        {
                            Code = UnimplementedCode
                            Message =
                                "The branch says it is unfinished and returns a stand-in; throw NotImplementedException so the gap reports itself"
                            Span = r.Span
                            Fixes =
                                [
                                    Suggestion.fix
                                        "Throw NotImplementedException"
                                        UnimplementedCode
                                        [ Suggestion.replace r.Span ("throw " + throwText model r.SpanStart + ";") ]
                                ]
                        }
                else
                    None
            | _ -> None
        | :? SwitchExpressionArmSyntax as arm when isPlaceholder arm.Expression ->
            let between =
                TextSpan.FromBounds(arm.EqualsGreaterThanToken.Span.End, arm.Expression.SpanStart)

            let accused = commentsIn root between |> List.exists saysUnfinished
            let switch = arm.Parent :?> SwitchExpressionSyntax

            let siblingsCompute =
                switch.Arms
                |> Seq.filter (fun a -> not (obj.ReferenceEquals(a, arm)))
                |> Seq.exists (fun a -> computes a.Expression)

            if accused && siblingsCompute && not (legitimateNoResult model arm arm.Expression) then
                Some
                    {
                        Code = UnimplementedCode
                        Message =
                            "The arm says it is unfinished and returns a stand-in; throw NotImplementedException so the gap reports itself"
                        Span = arm.Expression.Span
                        Fixes =
                            [
                                Suggestion.fix
                                    "Throw NotImplementedException"
                                    UnimplementedCode
                                    [
                                        Suggestion.replace
                                            arm.Expression.Span
                                            ("throw " + throwText model arm.Expression.SpanStart)
                                    ]
                            ]
                    }
            else
                None
        | _ -> None)
    |> List.ofSeq

let analyze (tree: SyntaxTree) (model: SemanticModel) (ctx: RuleContext) : Suggestion list =
    let root = tree.GetRoot()
    let wrapColumn = RuleContext.wrapColumn ctx MergeCode

    guardIsConstant root model
    @ mergeArms root (tree.GetText()) wrapColumn
    @ unimplemented root model
