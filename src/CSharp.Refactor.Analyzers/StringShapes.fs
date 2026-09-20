/// String building and string tests.
///
/// CR0100 (idiom, fix): `"Hello " + name + "!"` is `$"Hello {name}!"`.
/// Guards: three or more operands with at least one literal and one
/// non-literal; every `+` is the built-in string concatenation (a
/// user-defined `+` never rewrites), and only those nodes split — `1 + 2
/// + "x"` keeps `1 + 2` as one hole, as C# evaluates it; literals are
/// regular (`@""` and raw strings cannot share an interpolation with a
/// regular one) and hold no `{`/`}`; an interpolated operand `$"…"` joins
/// with its text; the chain sits on one line and lands within the wrap
/// column; a hole holding a raw literal or a brace is refused, one
/// holding a `:` or a `?:` is parenthesised; with
/// `DefaultInterpolatedStringHandler` resolvable any hole type is fine,
/// without it (netstandard2.0, net4x) an interpolation with a non-string
/// hole lowers to `string.Format`, so only all-string chains rewrite
/// there; never inside an expression tree (a provider translates `+`).
///
/// CR0101 (idiom, fix): `string.Format("{0} of {1:N2}", a, b)` is `$"{a}
/// of {b:N2}"`. Guards: a literal format bound to the `string` overload
/// (no `IFormatProvider` — culture is a decision); the arguments are
/// passed one per placeholder, never as one `params` array; every index
/// within range (CA2241's business otherwise); format and alignment
/// carried into the hole, `{{`/`}}` kept; an argument used twice must be
/// a pure atom; the handler gate of CR0100.
///
/// CR0102 (performance, fix): `$"{x.ToString()} items"` is `$"{x}
/// items"` and `string.Join(", ", xs.Select(x => x.ToString()))` is
/// `string.Join(", ", xs)`. Typed: the `ToString()` is parameterless, the
/// hole has no format or alignment, and the receiver cannot be null (a
/// non-nullable value type, or a reference type the nullable context
/// declares not null — `null.ToString()` throws where `{null}` prints
/// nothing); the `Join` receives an `IEnumerable<T>` whose `T` is the
/// projected element, with the generic `Join<T>` resolvable.
///
/// CR0104 (idiom, fix): `x == null || x == ""` is `string.IsNullOrEmpty(x)`,
/// `x == null || x.Trim() == ""` is `string.IsNullOrWhiteSpace(x)`, and
/// the `&&`-negated forms are the `!` of those. Guards: the subject is an
/// identifier or a dotted pure read spelled the same on both sides; the
/// emptiness test is `== ""`, `== string.Empty`, `.Length == 0` (or the
/// `Trim()` spellings, `Trim()` with no arguments stripping exactly the
/// `IsWhiteSpace` set); the null test leads — a `Trim()` spelling that
/// leads throws on null today, so that order is editor-only;
/// `string.IsNullOrEmpty(x.Trim())` is `IsNullOrWhiteSpace(x)` editor-only
/// for the same reason; never inside an expression tree.
module CSharp.Refactor.StringShapes

open System
open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax
open Microsoft.CodeAnalysis.Text

[<Literal>]
let ConcatCode = "CR0100"

[<Literal>]
let FormatCode = "CR0101"

[<Literal>]
let ToStringCode = "CR0102"

[<Literal>]
let EmptinessCode = "CR0104"

let private isStringType (t: ITypeSymbol) =
    not (isNull t) && t.SpecialType = SpecialType.System_String

/// Interpolation with a non-string hole needs the handler; below it the
/// compiler lowers to `string.Format`, which boxes and formats differently.
let private handlerAvailable (model: SemanticModel) =
    not (
        isNull (
            model.Compilation.GetTypeByMetadataName "System.Runtime.CompilerServices.DefaultInterpolatedStringHandler"
        )
    )

/// Does an interpolation hole print the value as `+` and `ToString()` would?
/// The handler formats an `IFormattable` through `ToString(null, provider)`,
/// which a BCL type spells like `ToString()` and a user type may not; a
/// user type without `IFormattable` prints through `ToString()` either way.
let private formatsLikeToString (t: ITypeSymbol) =
    match t with
    | null -> false
    | t when t.SpecialType <> SpecialType.None -> true
    | t when t.TypeKind = TypeKind.Enum -> true
    | t when
        (not (isNull t.ContainingNamespace))
        && t.ContainingNamespace.ToDisplayString() = "System"
        ->
        true
    | t ->
        not (
            t.AllInterfaces
            |> Seq.exists (fun i ->
                i.ToDisplayString() = "System.IFormattable"
                || i.ToDisplayString() = "System.ISpanFormattable")
        )

/// A regular string literal's own spelling without its quotes, or None
/// for a verbatim or raw one, or one holding a brace.
let private regularText (token: SyntaxToken) : string option =
    let t = token.Text

    if
        t.StartsWith "\""
        && t.EndsWith "\""
        && t.Length >= 2
        && not (t.StartsWith "\"\"\"")
    then
        let inner = t.Substring(1, t.Length - 2)

        if inner.Contains "{" || inner.Contains "}" then
            None
        else
            Some inner
    else
        None

/// The text a hole needs for an expression: parenthesised where a `:`
/// would start a format or a `?:` would confuse the reader.
let private holeText (e: ExpressionSyntax) =
    let text = e.ToString()

    // a `:` that would start a format: one outside the expression's own
    // string literals (`x.ToString("H:mm")` needs no parentheses)
    let colonOutsideLiterals =
        e.DescendantTokens() |> Seq.exists (fun t -> t.IsKind SyntaxKind.ColonToken)
        || (match e with
            | :? ConditionalExpressionSyntax -> true
            | _ -> false)

    match e with
    | :? ParenthesizedExpressionSyntax -> text
    | _ when colonOutsideLiterals -> $"({text})"
    | _ -> text

/// The pieces of an interpolation: literal text (already spelled for a
/// regular string) or a hole.
type private Piece =
    | Lit of string
    | Hole of ExpressionSyntax

let private render (pieces: Piece list) =
    "$\""
    + (pieces
       |> List.map (fun p ->
           match p with
           | Lit s -> s
           | Hole e -> "{" + holeText e + "}")
       |> String.concat "")
    + "\""

// ---- CR0100 ----

[<return: Struct>]
let inline private (|SpansLines|_|) input =
    if Text.spansLines input then ValueSome input else ValueNone

let private concatenations (tree: SyntaxTree) (model: SemanticModel) (ctx: RuleContext) : Suggestion list =
    let handler = handlerAvailable model
    let text = tree.GetText()
    let wrapColumn = RuleContext.wrapColumn ctx ConcatCode

    let isStringPlus (b: BinaryExpressionSyntax) =
        b.IsKind SyntaxKind.AddExpression
        && isStringType (model.GetTypeInfo(b).Type)
        && Guards.isBuiltinOperator model b

    // the operands of the concatenation, left to right; a non-string `+` stays one operand
    let rec operands (e: ExpressionSyntax) : ExpressionSyntax list =
        match e with
        | :? BinaryExpressionSyntax as b when isStringPlus b -> operands b.Left @ [ b.Right ]
        | e -> [ e ]

    // each operand as pieces, or None where an operand cannot join an interpolation
    let pieceOf (e: ExpressionSyntax) : Piece list option =
        match e with
        | :? LiteralExpressionSyntax as l when l.IsKind SyntaxKind.StringLiteralExpression ->
            regularText l.Token |> Option.map (fun s -> [ Lit s ])
        | :? InterpolatedStringExpressionSyntax as i when i.StringStartToken.Text = "$\"" ->
            // its own contents join as they are
            let inner = i.Contents |> Seq.map (fun c -> c.ToString()) |> String.concat ""
            Some [ Lit inner ]
        | :? InterpolatedStringExpressionSyntax -> None
        // a raw literal or a brace inside the hole: illegal before C# 11, unreadable after
        | e when
            e.ToString().Contains "\"\"\""
            || e.ToString().Contains "{"
            || e.ToString().Contains "}"
            ->
            None
        | SpansLines _ -> None
        | e when not (handler || isStringType (model.GetTypeInfo(e).Type)) -> None
        | e when not (formatsLikeToString (model.GetTypeInfo(e).Type)) -> None
        | e -> Some [ Hole e ]

    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun n ->
        match n with
        | :? BinaryExpressionSyntax as b when
            isStringPlus b
            // the outermost `+` of its chain
            && not (
                match b.Parent with
                | :? BinaryExpressionSyntax as p -> isStringPlus p
                | _ -> false
            )
            && not (Text.insideExpressionTree model b)
            && not (Text.holdsCommentOrDirective b)
            // a chain laid out over lines keeps its layout: an interpolation cannot
            && not (Text.multiLine text b)
            ->
            let parts = operands b

            let literals =
                parts
                |> List.filter (fun p -> p :? LiteralExpressionSyntax || p :? InterpolatedStringExpressionSyntax)

            if parts.Length < 3 || literals.IsEmpty || literals.Length = parts.Length then
                None
            else
                let pieces = parts |> List.map pieceOf

                if pieces |> List.exists Option.isNone then
                    None
                else
                    let replacement = render (pieces |> List.collect Option.get)

                    // `(a + b).Length` kept its parentheses for the `+`: a literal needs none
                    let target: SyntaxNode =
                        match b.Parent with
                        | :? ParenthesizedExpressionSyntax as p when not (Text.holdsCommentOrDirective p) -> p
                        | _ -> b

                    let edit = Suggestion.replace target.Span replacement

                    // the line it lands on stays within the wrap column
                    let line = text.Lines.GetLineFromPosition target.SpanStart

                    let newLength =
                        (target.SpanStart - line.Start)
                        + replacement.Length
                        + (line.End - target.Span.End)

                    // as an argument, the interpolated string must land on the same overload
                    let overloadKept =
                        match Guards.enclosingCall target with
                        | Some call -> Guards.bindingKept model [ edit ] call
                        | None -> true

                    if
                        newLength <= wrapColumn
                        && overloadKept
                        && Guards.speculativeCheck model [ edit ]
                    then
                        Some
                            {
                                Code = ConcatCode
                                Message = "A concatenation of literals and values reads as an interpolated string"
                                Span = b.Span
                                Fixes = [ Suggestion.fix "Use an interpolated string" ConcatCode [ edit ] ]
                            }
                    else
                        None
        | _ -> None)
    |> List.ofSeq

// ---- CR0101 ----

/// A placeholder of a composite format: index, alignment text, format text.
type private Placeholder =
    {
        Index: int
        Alignment: string
        Format: string
        Start: int
        End: int
    }

/// The placeholders of a format string (the literal's spelling), or None
/// where the format is malformed.
let private placeholders (format: string) : Placeholder list option =
    let mutable i = 0
    let mutable ok = true

    let result: Placeholder list =
        [
            while ok && i < format.Length do
                match format.[i] with
                | '{' when i + 1 < format.Length && format.[i + 1] = '{' -> i <- i + 2
                | '{' ->
                    let close = format.IndexOf('}', i)

                    if close < 0 then
                        ok <- false
                    else
                        let body = format.Substring(i + 1, close - i - 1)
                        let colon = body.IndexOf ':'
                        let head = if colon >= 0 then body.Substring(0, colon) else body
                        let fmt = if colon >= 0 then body.Substring(colon + 1) else ""
                        let comma = head.IndexOf ','
                        let indexText = if comma >= 0 then head.Substring(0, comma) else head
                        let align = if comma >= 0 then head.Substring comma else ""

                        match Int32.TryParse(indexText.Trim()) with
                        | true, index when index >= 0 && not (fmt.Contains "{") ->
                            {
                                Index = index
                                Alignment = align
                                Format = (if fmt = "" then "" else ":" + fmt)
                                Start = i
                                End = close + 1
                            }

                            i <- close + 1
                        | _ -> ok <- false
                | '}' when i + 1 < format.Length && format.[i + 1] = '}' -> i <- i + 2
                | '}' -> ok <- false
                | _ -> i <- i + 1
        ]

    if ok then Some result else None

[<TailCall>]
let rec private isAtom (e: ExpressionSyntax) =
    match e with
    | :? IdentifierNameSyntax
    | :? LiteralExpressionSyntax
    | :? ThisExpressionSyntax -> true
    | :? MemberAccessExpressionSyntax as m -> isAtom m.Expression
    | _ -> false

let private formats (tree: SyntaxTree) (model: SemanticModel) (ctx: RuleContext) : Suggestion list =
    let handler = handlerAvailable model
    let text = tree.GetText()
    let wrapColumn = RuleContext.wrapColumn ctx FormatCode

    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun n ->
        match n with
        | :? InvocationExpressionSyntax as inv when
            (let e = inv.Expression.ToString()
             e = "string.Format" || e = "String.Format")
            && inv.ArgumentList.Arguments.Count >= 2
            && not (Text.insideExpressionTree model inv)
            && not (Text.holdsCommentOrDirective inv)
            ->
            match model.GetSymbolInfo(inv).Symbol, inv.ArgumentList.Arguments.[0].Expression with
            | (:? IMethodSymbol as m), (:? LiteralExpressionSyntax as lit) when
                m.ContainingType.SpecialType = SpecialType.System_String
                && lit.IsKind SyntaxKind.StringLiteralExpression
                && m.Parameters.Length >= 1
                && m.Parameters.[0].Type.SpecialType = SpecialType.System_String
                ->
                let args = inv.ArgumentList.Arguments |> Seq.skip 1 |> List.ofSeq

                // one array handed whole: its count is invisible
                let passedWhole =
                    args.Length = 1
                    && (match model.GetTypeInfo(args.[0].Expression).Type with
                        | :? IArrayTypeSymbol -> true
                        | _ -> false)

                let verbatim = lit.Token.Text.StartsWith "@"

                let inner =
                    if verbatim then
                        lit.Token.Text.Substring(2, lit.Token.Text.Length - 3)
                    else
                        lit.Token.Text.Substring(1, lit.Token.Text.Length - 2)

                match placeholders inner with
                | Some holes when
                    not passedWhole
                    && not holes.IsEmpty
                    && holes |> List.forall (fun h -> h.Index < args.Length)
                    && args
                       |> List.forall (fun a -> isNull a.NameColon && a.RefKindKeyword.IsKind SyntaxKind.None)
                    ->
                    let argExpressions = args |> List.map (fun a -> a.Expression)

                    let usedTwice =
                        holes
                        |> List.countBy (fun h -> h.Index)
                        |> List.exists (fun (index, count) ->
                            count > 1
                            && not (
                                isAtom argExpressions.[index]
                                && Guards.isPureExpression model argExpressions.[index]
                            ))

                    let unused =
                        [ 0 .. args.Length - 1 ]
                        |> List.exists (fun i -> not (holes |> List.exists (fun h -> h.Index = i)))

                    // `string.Format` and the handler both format an `IFormattable` through
                    // `ToString(format, provider)`: no `ToString()` gap here
                    let typesOk =
                        handler
                        || argExpressions
                           |> List.forall (fun e -> isStringType (model.GetTypeInfo(e).Type))

                    let multiLine = argExpressions |> List.exists (Text.multiLine (tree.GetText()))

                    if usedTwice || unused || not typesOk || multiLine then
                        None
                    else
                        // rebuild the text: holes replaced by their arguments
                        let sb = System.Text.StringBuilder()
                        let mutable pos = 0

                        for h in holes do
                            sb.Append(inner.Substring(pos, h.Start - pos)) |> ignore

                            // a regular literal argument of a regular template is text, not a
                            // hole: `{"label"}` reads as a mistake (the escapes of the two
                            // literal kinds agree only when neither is verbatim)
                            let spliced =
                                if h.Alignment <> "" || h.Format <> "" then
                                    None
                                else
                                    match argExpressions.[h.Index] with
                                    | :? LiteralExpressionSyntax as l when l.IsKind SyntaxKind.StringLiteralExpression ->
                                        // into a verbatim template only text both kinds spell alike:
                                        // no escape, no quote
                                        regularText l.Token
                                        |> Option.filter (fun s ->
                                            not (verbatim && (s.Contains "\\" || s.Contains "\"")))
                                    | _ -> None

                            match spliced with
                            | Some text -> sb.Append text |> ignore
                            | None ->
                                sb
                                    .Append('{')
                                    .Append(holeText argExpressions.[h.Index])
                                    .Append(h.Alignment)
                                    .Append(h.Format)
                                    .Append
                                    '}'
                                |> ignore

                            pos <- h.End

                        sb.Append(inner.Substring pos) |> ignore

                        let replacement = (if verbatim then "$@\"" else "$\"") + sb.ToString() + "\""

                        let edit = Suggestion.replace inv.Span replacement

                        // a template laid out over lines becomes one: the line it lands on
                        // must stay within the wrap column
                        let line = text.Lines.GetLineFromPosition inv.SpanStart

                        let fits =
                            not (Text.multiLine text inv)
                            || (inv.SpanStart - line.Start) + replacement.Length <= wrapColumn

                        // as an argument, the interpolated string must land on the same overload
                        let overloadKept =
                            match Guards.enclosingCall inv with
                            | Some call -> Guards.bindingKept model [ edit ] call
                            | None -> true

                        if fits && overloadKept && Guards.speculativeCheck model [ edit ] then
                            Some
                                {
                                    Code = FormatCode
                                    Message = "string.Format with a literal template is an interpolated string"
                                    Span = inv.Span
                                    Fixes = [ Suggestion.fix "Use an interpolated string" FormatCode [ edit ] ]
                                }
                        else
                            None
                | _ -> None
            | _ -> None
        | _ -> None)
    |> List.ofSeq

// ---- CR0102 ----

/// Can the expression be null at the call — a nullable value type, or a
/// reference type the nullable context does not declare not-null?
let private mayBeNull (model: SemanticModel) (e: ExpressionSyntax) =
    let info = model.GetTypeInfo e

    match info.Type with
    | null -> true
    | t when t.IsValueType -> t.OriginalDefinition.SpecialType = SpecialType.System_Nullable_T
    | _ -> info.Nullability.FlowState <> NullableFlowState.NotNull

let private isParameterlessToString (model: SemanticModel) (inv: InvocationExpressionSyntax) =
    Linq.nameOf inv = "ToString"
    && inv.ArgumentList.Arguments.Count = 0
    && (match model.GetSymbolInfo(inv).Symbol with
        | :? IMethodSymbol as m -> m.Parameters.Length = 0
        | _ -> false)

let private redundantToStrings (tree: SyntaxTree) (model: SemanticModel) : Suggestion list =
    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun n ->
        match n with
        // `{x.ToString()}` with no format or alignment
        | :? InterpolationSyntax as hole when
            isNull hole.AlignmentClause
            && isNull hole.FormatClause
            && not (Text.insideExpressionTree model hole)
            ->
            match hole.Expression with
            | :? InvocationExpressionSyntax as inv when isParameterlessToString model inv ->
                match inv.Expression with
                | :? MemberAccessExpressionSyntax as m when
                    not (mayBeNull model m.Expression)
                    && formatsLikeToString (model.GetTypeInfo(m.Expression).Type)
                    ->
                    let edit = Suggestion.replace inv.Span (m.Expression.ToString())

                    Some
                        {
                            Code = ToStringCode
                            Message = "The hole formats the value itself: ToString() is a copy"
                            Span = TextSpan.FromBounds(m.OperatorToken.SpanStart, inv.Span.End)
                            Fixes = [ Suggestion.fix "Drop ToString()" ToStringCode [ edit ] ]
                        }
                | _ -> None
            | _ -> None
        // `string.Join(sep, xs.Select(x => x.ToString()))`
        | :? InvocationExpressionSyntax as join when
            (let e = join.Expression.ToString()
             e = "string.Join" || e = "String.Join")
            && join.ArgumentList.Arguments.Count = 2
            && not (Text.insideExpressionTree model join)
            ->
            match join.ArgumentList.Arguments.[1].Expression with
            | :? InvocationExpressionSyntax as select when
                Linq.nameOf select = "Select"
                && select.ArgumentList.Arguments.Count = 1
                && (Linq.enumerableCall model select).IsSome
                ->
                match select.ArgumentList.Arguments.[0].Expression, Linq.receiverOf select with
                | (:? SimpleLambdaExpressionSyntax as l), Some source when not (isNull l.ExpressionBody) ->
                    match l.ExpressionBody with
                    | :? InvocationExpressionSyntax as inv when isParameterlessToString model inv ->
                        match inv.Expression with
                        | :? MemberAccessExpressionSyntax as m when
                            m.Expression.ToString() = l.Parameter.Identifier.ValueText
                            && not (mayBeNull model m.Expression)
                            ->
                            let edit = Suggestion.replace select.Span (source.ToString())

                            if Guards.speculativeCheck model [ edit ] then
                                Some
                                    {
                                        Code = ToStringCode
                                        Message = "string.Join formats each element itself: the Select is a copy"
                                        Span = TextSpan.FromBounds(source.Span.End, select.Span.End)
                                        Fixes = [ Suggestion.fix "Drop the Select" ToStringCode [ edit ] ]
                                    }
                            else
                                None
                        | _ -> None
                    | _ -> None
                | _ -> None
            | _ -> None
        | _ -> None)
    |> List.ofSeq

// ---- CR0104 ----

type private Test =
    | IsNull of subject: ExpressionSyntax
    | IsEmpty of subject: ExpressionSyntax
    | IsBlank of subject: ExpressionSyntax // a Trim() spelling: throws on null

[<TailCall>]
let rec private isSubject (e: ExpressionSyntax) =
    match e with
    | :? IdentifierNameSyntax -> true
    | :? MemberAccessExpressionSyntax as m -> isSubject m.Expression
    | :? ThisExpressionSyntax -> true
    | _ -> false

let private isEmptyLiteral (e: ExpressionSyntax) =
    match e with
    | :? LiteralExpressionSyntax as l -> l.IsKind SyntaxKind.StringLiteralExpression && l.Token.ValueText = ""
    | :? MemberAccessExpressionSyntax as m -> m.ToString() = "string.Empty" || m.ToString() = "String.Empty"
    | _ -> false

let private isZero (e: ExpressionSyntax) =
    match e with
    | :? LiteralExpressionSyntax as l -> l.Token.ValueText = "0"
    | _ -> false

/// `x.Trim()` with no arguments: the subject.
let private trimmed (e: ExpressionSyntax) : ExpressionSyntax option =
    match e with
    | :? InvocationExpressionSyntax as inv when Linq.nameOf inv = "Trim" && inv.ArgumentList.Arguments.Count = 0 ->
        match inv.Expression with
        | :? MemberAccessExpressionSyntax as m when isSubject m.Expression -> Some m.Expression
        | _ -> None
    | _ -> None

/// `x.Length` on a subject.
let private lengthOf (e: ExpressionSyntax) : ExpressionSyntax option =
    match e with
    | :? MemberAccessExpressionSyntax as m when m.Name.Identifier.ValueText = "Length" && isSubject m.Expression ->
        Some m.Expression
    | _ -> None

/// One side of the `||`: a null test or an emptiness test, positive
/// (`==`, `is null`) or negated (`!=`, `is not null`); returns the test
/// and whether it was negated.
let private classify (model: SemanticModel) (e: ExpressionSyntax) : (Test * bool) option =
    let stringTyped (s: ExpressionSyntax) =
        isStringType (model.GetTypeInfo(s).Type)

    match e with
    | :? BinaryExpressionSyntax as b when
        b.IsKind SyntaxKind.EqualsExpression || b.IsKind SyntaxKind.NotEqualsExpression
        ->
        let negated = b.IsKind SyntaxKind.NotEqualsExpression

        let sides = [ b.Left, b.Right; b.Right, b.Left ]

        sides
        |> List.tryPick (fun (subject, other) ->
            if
                other.IsKind SyntaxKind.NullLiteralExpression
                && isSubject subject
                && stringTyped subject
            then
                Some(IsNull subject, negated)
            elif isEmptyLiteral other && isSubject subject && stringTyped subject then
                Some(IsEmpty subject, negated)
            elif isEmptyLiteral other then
                trimmed subject
                |> Option.filter stringTyped
                |> Option.map (fun s -> IsBlank s, negated)
            elif isZero other then
                match lengthOf subject with
                | Some s when stringTyped s -> Some(IsEmpty s, negated)
                | _ ->
                    match subject with
                    | :? MemberAccessExpressionSyntax as m when m.Name.Identifier.ValueText = "Length" ->
                        trimmed m.Expression
                        |> Option.filter stringTyped
                        |> Option.map (fun s -> IsBlank s, negated)
                    | _ -> None
            else
                None)
    | :? IsPatternExpressionSyntax as p when isSubject p.Expression && stringTyped p.Expression ->
        match p.Pattern with
        | :? ConstantPatternSyntax as c when c.Expression.IsKind SyntaxKind.NullLiteralExpression ->
            Some(IsNull p.Expression, false)
        | :? UnaryPatternSyntax as u when u.IsKind SyntaxKind.NotPattern ->
            match u.Pattern with
            | :? ConstantPatternSyntax as c when c.Expression.IsKind SyntaxKind.NullLiteralExpression ->
                Some(IsNull p.Expression, true)
            | _ -> None
        | _ -> None
    | _ -> None

let private emptiness (tree: SyntaxTree) (model: SemanticModel) : Suggestion list =
    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun n ->
        match n with
        | :? BinaryExpressionSyntax as b when
            (b.IsKind SyntaxKind.LogicalOrExpression
             || b.IsKind SyntaxKind.LogicalAndExpression)
            && not (Text.insideExpressionTree model b)
            && not (Text.holdsCommentOrDirective b)
            ->
            let orForm = b.IsKind SyntaxKind.LogicalOrExpression

            match classify model b.Left, classify model b.Right with
            | Some(left, ln), Some(right, rn) when ln = rn && ln <> orForm ->
                // `||` joins positive tests, `&&` joins negated ones
                let combine (first: Test) (second: Test) =
                    match first, second with
                    | IsNull s, IsEmpty t when Guards.sameReference model s t -> Some("IsNullOrEmpty", s, false)
                    | IsNull s, IsBlank t when Guards.sameReference model s t -> Some("IsNullOrWhiteSpace", s, false)
                    | IsEmpty t, IsNull s when Guards.sameReference model s t -> Some("IsNullOrEmpty", s, false)
                    // the Trim() runs first here and throws on null today: editor-only
                    | IsBlank t, IsNull s when Guards.sameReference model s t -> Some("IsNullOrWhiteSpace", s, true)
                    | _ -> None

                match combine left right with
                | Some(name, subject, editorOnly) ->
                    let call = "string." + name + "(" + subject.ToString() + ")"
                    let replacement = if ln then "!" + call else call
                    let edit = Suggestion.replace b.Span replacement

                    let fix =
                        Suggestion.fix ("Use string." + name) EmptinessCode [ edit ]
                        |> (if editorOnly then Suggestion.editorOnly else id)

                    Some
                        {
                            Code = EmptinessCode
                            Message = $"A null-or-empty test spelled out is string.{name}"
                            Span = b.Span
                            Fixes = [ fix ]
                        }
                | None -> None
            | _ -> None
        // `string.IsNullOrEmpty(x.Trim())`
        | :? InvocationExpressionSyntax as inv when
            (let e = inv.Expression.ToString()
             e = "string.IsNullOrEmpty" || e = "String.IsNullOrEmpty")
            && inv.ArgumentList.Arguments.Count = 1
            && not (Text.insideExpressionTree model inv)
            ->
            match trimmed inv.ArgumentList.Arguments.[0].Expression with
            | Some subject ->
                let replacement = "string.IsNullOrWhiteSpace(" + subject.ToString() + ")"

                Some
                    {
                        Code = EmptinessCode
                        Message =
                            "IsNullOrEmpty of a Trim() is IsNullOrWhiteSpace (which also takes null, where Trim() throws today)"
                        Span = inv.Span
                        Fixes =
                            [
                                Suggestion.fix
                                    "Use string.IsNullOrWhiteSpace"
                                    EmptinessCode
                                    [ Suggestion.replace inv.Span replacement ]
                                |> Suggestion.editorOnly
                            ]
                    }
            | None -> None
        | _ -> None)
    |> List.ofSeq

let analyze (tree: SyntaxTree) (model: SemanticModel) (ctx: RuleContext) : Suggestion list =
    concatenations tree model ctx
    @ formats tree model ctx
    @ redundantToStrings tree model
    @ emptiness tree model
