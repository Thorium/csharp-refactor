/// CR0005 (idiom, fix): nested ifs merge into one `&&`, in the two shapes
/// that preserve semantics exactly:
///
///     if (a) { if (b) X else E } else E   →  if (a && b) X else E
///     if (a) { if (b) X }                 →  if (a && b) X
///
/// In the first, exactly one of the branches runs either way, so even an
/// effectful E is unchanged; in the second nothing runs on the miss either
/// way. The tempting third shape — inner `if` without `else` while the
/// outer HAS one — is deliberately absent: `if (a && b) X else E` would run
/// E where the original ran nothing.
///
/// Guards: the inner `if` is the ONLY statement of the outer `then`; the
/// two `else` blocks are textually identical, comments included; both
/// conditions are `bool` through built-in operators; an `||`-topped
/// condition gains parentheses before joining the `&&`; no comment or
/// directive is swallowed with the braces; the moved lines hold no
/// multi-line literal.
module CSharp.Refactor.NestedIfMerge

open System
open System.Text.RegularExpressions
open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax
open Microsoft.CodeAnalysis.Text

[<Literal>]
let Code = "CR0005"

let private whitespace = Regex(@"\s+", RegexOptions.Compiled)

let private normalized (n: SyntaxNode) =
    whitespace.Replace(n.ToString(), " ").Trim()

/// The inner `if` when it is the only statement of the outer's `then`.
let private innerIf (s: StatementSyntax) =
    match s with
    | :? BlockSyntax as b when b.Statements.Count = 1 ->
        match b.Statements.[0] with
        | :? IfStatementSyntax as i -> ValueSome(i, Some b)
        | _ -> ValueNone
    | :? IfStatementSyntax as i -> ValueSome(i, None)
    | _ -> ValueNone

/// A condition as an `&&` operand: bracketed where precedence or the
/// reader needs it.
let private operandText (c: ExpressionSyntax) =
    match c with
    // `||` and `??` bind looser than `&&`; comparisons and arithmetic tighter
    | :? BinaryExpressionSyntax as b when
        b.IsKind SyntaxKind.LogicalOrExpression
        || b.IsKind SyntaxKind.CoalesceExpression
        ->
        "(" + c.ToString() + ")"
    | :? ConditionalExpressionSyntax
    | :? AssignmentExpressionSyntax
    | :? LambdaExpressionSyntax -> "(" + c.ToString() + ")"
    | _ -> c.ToString()

let private isBool (model: SemanticModel) (e: ExpressionSyntax) =
    match model.GetTypeInfo(e).Type with
    | null -> false
    | t -> t.SpecialType = SpecialType.System_Boolean

let private spansLines (node: SyntaxNode) =
    node.DescendantTokens() |> Seq.exists (fun t -> t.Text.IndexOf '\n' >= 0)

/// Text starting at `from`, re-based from the column its NODE started at to
/// `toColumn`: every continuation line moves left by the difference where
/// it has room, so a block keeps its shape one level up.
let private rebasedTail (text: SourceText) (node: SyntaxNode) (from: int) (toColumn: int) =
    let line = text.Lines.GetLineFromPosition node.SpanStart
    let fromColumn = node.SpanStart - line.Start
    let delta = fromColumn - toColumn
    let tail = text.ToString(TextSpan.FromBounds(from, node.Span.End))
    let lines = tail.Split '\n'

    lines
    |> Array.mapi (fun i l ->
        if i = 0 then
            l
        else
            let leading = l.Length - l.TrimStart(' ').Length

            if delta > 0 && leading >= delta then
                l.Substring delta
            else
                l)
    |> String.concat "\n"

let analyze (tree: SyntaxTree) (model: SemanticModel) (ctx: RuleContext) : Suggestion list =
    let text = tree.GetText()

    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun node ->
        match node with
        | :? IfStatementSyntax as outer when not (outer.Parent :? ElseClauseSyntax) ->
            match innerIf outer.Statement with
            | ValueSome(inner, block) when
                isBool model outer.Condition
                && isBool model inner.Condition
                && not (spansLines inner)
                // only comments INSIDE the inner if's own span travel with it;
                // one anywhere else in the outer block — above the inner if,
                // beside a brace — would be dropped, and holds the fix
                && (match block with
                    | Some b ->
                        not (
                            b.DescendantTrivia(descendIntoTrivia = true)
                            |> Seq.exists (fun t ->
                                (t.IsKind SyntaxKind.SingleLineCommentTrivia
                                 || t.IsKind SyntaxKind.MultiLineCommentTrivia
                                 || t.IsDirective)
                                && not (inner.Span.Contains t.Span))
                        )
                    | None -> true)
                ->
                let sameElse =
                    match outer.Else, inner.Else with
                    | null, null -> true
                    | o, i when not (isNull o || isNull i) -> normalized o.Statement = normalized i.Statement
                    | _ -> false

                let outerLine = text.Lines.GetLineFromPosition outer.SpanStart
                let outerColumn = outer.SpanStart - outerLine.Start
                let condition = operandText outer.Condition + " && " + operandText inner.Condition
                let wrapColumn = RuleContext.wrapColumn ctx Code

                // a merged condition that runs past the wrap column is not the
                // cleanup it claims to be (two TryGetValue guards joined made a
                // 170-column line): the nesting stays
                if not sameElse || outerColumn + "if (".Length + condition.Length + 2 > wrapColumn then
                    None
                else
                    // the inner if's own tail — its statement and its else, with
                    // the spacing the author laid out — moves up one level; the
                    // outer else was proven identical and goes with the outer if
                    let tail = rebasedTail text inner inner.CloseParenToken.Span.End outerColumn
                    let replacement = $"if ({condition}){tail}"

                    Some
                        {
                            Code = Code
                            Message = "Nested ifs with the same else merge into one '&&'"
                            Span = TextSpan.FromBounds(outer.SpanStart, outer.Condition.Span.End + 1)
                            Fixes =
                                [
                                    Suggestion.fix
                                        "Merge into one condition"
                                        Code
                                        [ Suggestion.replace outer.Span replacement ]
                                ]
                        }
            | _ -> None
        | _ -> None)
    |> List.ofSeq
