/// CR0001 (idiom, fix): a condition spelled out as `true`/`false` in the
/// two statement shapes the IDE's ternary rule (IDE0075) does not cover:
///
///     if (c) return true; return false;       →  return c;
///     if (c) return true; else return false;  →  return c;
///     if (c) return false; return true;       →  return !c;
///     if (c) x = true; else x = false;        →  x = c;
///
/// Guards: `c` is `bool` (a user-defined `true`/`false` operator is a
/// call); the `return false` (or the `else`) is the IMMEDIATE sibling; the
/// assignment form needs the same target on both sides, and one that is a
/// local or a field — a property setter may act, and the two arms ran it
/// once each; an `if` that is itself another `if`'s `else` clause is not
/// rewritten (the bare expression would glue onto the chain); no comment or
/// directive is swallowed; the negated spelling parenthesises a
/// non-atomic condition.
module CSharp.Refactor.BoolReturn

open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax
open Microsoft.CodeAnalysis.Text

[<Literal>]
let Code = "CR0001"

/// The one statement a branch holds: bare, or alone in a block.
let private single (s: StatementSyntax) =
    match s with
    | :? BlockSyntax as b when b.Statements.Count = 1 -> ValueSome b.Statements.[0]
    | :? BlockSyntax -> ValueNone
    | other -> ValueSome other

let private boolLiteralOf (e: ExpressionSyntax) =
    match e with
    | :? LiteralExpressionSyntax as l when l.IsKind SyntaxKind.TrueLiteralExpression -> Some true
    | :? LiteralExpressionSyntax as l when l.IsKind SyntaxKind.FalseLiteralExpression -> Some false
    | _ -> None

let private returnedLiteral (s: StatementSyntax) =
    match single s with
    | ValueSome(:? ReturnStatementSyntax as r) when not (isNull r.Expression) -> boolLiteralOf r.Expression
    | _ -> None

let private assignedLiteral (s: StatementSyntax) =
    match single s with
    | ValueSome(:? ExpressionStatementSyntax as es) ->
        match es.Expression with
        | :? AssignmentExpressionSyntax as a when a.IsKind SyntaxKind.SimpleAssignmentExpression ->
            boolLiteralOf a.Right |> Option.map (fun v -> a.Left, v)
        | _ -> None
    | _ -> None

let private isAtomic (e: ExpressionSyntax) =
    match e with
    | :? IdentifierNameSyntax
    | :? MemberAccessExpressionSyntax
    | :? InvocationExpressionSyntax
    | :? ParenthesizedExpressionSyntax
    | :? ElementAccessExpressionSyntax
    | :? LiteralExpressionSyntax -> true
    | :? PrefixUnaryExpressionSyntax as u -> u.IsKind SyntaxKind.LogicalNotExpression
    | _ -> false

/// A comparison whose negation is another comparison: `<` ↔ `>=`, `>` ↔ `<=`,
/// `==` ↔ `!=`. Exact only where no NaN and no null can sit on either side —
/// `!(x < 0)` is true for NaN and for a null `int?` where `x >= 0` is false
/// (a lifted comparison is false both ways) — so the ordering flips need
/// the operands proven non-floating and non-nullable; the equality flips
/// are exact for both.
let private flippedComparison (model: SemanticModel) (c: ExpressionSyntax) =
    match c with
    | :? BinaryExpressionSyntax as b when Guards.isBuiltinOperator model b ->
        let ordering =
            Guards.orderingFlipSafe model b.Left && Guards.orderingFlipSafe model b.Right

        let flipped =
            match b.Kind() with
            | SyntaxKind.EqualsExpression -> Some "!="
            | SyntaxKind.NotEqualsExpression -> Some "=="
            | SyntaxKind.LessThanExpression when ordering -> Some ">="
            | SyntaxKind.GreaterThanExpression when ordering -> Some "<="
            | SyntaxKind.LessThanOrEqualExpression when ordering -> Some ">"
            | SyntaxKind.GreaterThanOrEqualExpression when ordering -> Some "<"
            | _ -> None

        flipped
        |> Option.map (fun op -> b.Left.ToString() + " " + op + " " + b.Right.ToString())
    | _ -> None

/// The condition, or its negation, as an expression text: an existing `!`
/// unwraps, a comparison flips where that is exact, an atom takes `!`, the
/// rest `!(…)`.
let conditionText (model: SemanticModel) (c: ExpressionSyntax) (negate: bool) =
    if not negate then
        c.ToString()
    else
        match c with
        | :? PrefixUnaryExpressionSyntax as u when u.IsKind SyntaxKind.LogicalNotExpression -> u.Operand.ToString()
        | :? ParenthesizedExpressionSyntax as p when
            (match p.Expression with
             | :? PrefixUnaryExpressionSyntax as u -> u.IsKind SyntaxKind.LogicalNotExpression
             | _ -> false)
            ->
            (p.Expression :?> PrefixUnaryExpressionSyntax).Operand.ToString()
        | _ ->
            match flippedComparison model c with
            | Some flipped -> flipped
            | None when isAtomic c -> "!" + c.ToString()
            | None -> "!(" + c.ToString() + ")"

let private isLocalOrField (model: SemanticModel) (target: ExpressionSyntax) =
    match model.GetSymbolInfo(target).Symbol with
    | :? ILocalSymbol
    | :? IParameterSymbol -> true
    | :? IFieldSymbol -> true
    | _ -> false

let private hasComment (node: SyntaxNode) = Text.holdsCommentOrDirective node

let private leadingComment (s: StatementSyntax) =
    s.GetLeadingTrivia()
    |> Seq.exists (fun t ->
        t.IsKind SyntaxKind.SingleLineCommentTrivia
        || t.IsKind SyntaxKind.MultiLineCommentTrivia
        || t.IsDirective)

let analyze (tree: SyntaxTree) (model: SemanticModel) (_ctx: RuleContext) : Suggestion list =
    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun node ->
        match node with
        | :? IfStatementSyntax as ifs when
            not (ifs.Parent :? ElseClauseSyntax)
            && (match model.GetTypeInfo(ifs.Condition).Type with
                | null -> false
                | t -> t.SpecialType = SpecialType.System_Boolean)
            ->
            let cond = ifs.Condition

            // the return form: `if (c) return A;` followed by `return B;` as
            // the else, or as the immediately following statement
            let returnForm =
                match returnedLiteral ifs.Statement with
                | Some thenValue ->
                    let elseReturn =
                        if not (isNull ifs.Else) then
                            returnedLiteral ifs.Else.Statement
                            |> Option.map (fun v -> v, ifs.Span.End, (ifs :> SyntaxNode))
                        else
                            match ifs.Parent with
                            | :? BlockSyntax as block ->
                                let i = block.Statements.IndexOf ifs

                                if i >= 0 && i + 1 < block.Statements.Count then
                                    let next = block.Statements.[i + 1]

                                    match next with
                                    | :? ReturnStatementSyntax as r when not (leadingComment next) ->
                                        boolLiteralOf r.Expression
                                        |> Option.map (fun v -> v, next.Span.End, (next :> SyntaxNode))
                                    | _ -> None
                                else
                                    None
                            | _ -> None

                    match elseReturn with
                    | Some(elseValue, endAt, tail) when
                        elseValue <> thenValue && not (hasComment ifs) && not (hasComment tail)
                        ->
                        let span = TextSpan.FromBounds(ifs.SpanStart, endAt)
                        let replacement = "return " + conditionText model cond (not thenValue) + ";"
                        Some(span, replacement)
                    | _ -> None
                | None -> None

            match returnForm with
            | Some(span, replacement) ->
                Some
                    {
                        Code = Code
                        Message = "The branches return the condition's value spelled out; return the condition"
                        Span = span
                        Fixes =
                            [
                                Suggestion.fix "Return the condition" Code [ Suggestion.replace span replacement ]
                            ]
                    }
            | None ->
                // the assignment form: `if (c) x = true; else x = false;`
                match assignedLiteral ifs.Statement with
                | Some(target, thenValue) when not (isNull ifs.Else) ->
                    match assignedLiteral ifs.Else.Statement with
                    | Some(target2, elseValue) when
                        elseValue <> thenValue
                        && target.ToString() = target2.ToString()
                        && isLocalOrField model target
                        && not (hasComment ifs)
                        ->
                        let replacement =
                            target.ToString() + " = " + conditionText model cond (not thenValue) + ";"

                        Some
                            {
                                Code = Code
                                Message = "The branches assign the condition's value spelled out; assign the condition"
                                Span = ifs.Span
                                Fixes =
                                    [
                                        Suggestion.fix
                                            "Assign the condition"
                                            Code
                                            [ Suggestion.replace ifs.Span replacement ]
                                    ]
                            }
                    | _ -> None
                | _ -> None
        | _ -> None)
    |> List.ofSeq
