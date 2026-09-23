/// CR0006 (idiom, fix, off by default): a long happy path under an `if`
/// with a short exiting `else` reads better as a guard clause.
///
///     if (ok)                          if (!ok)
///     {                                {
///         … twenty lines …         →       return;
///     }                                }
///     else                             … twenty lines, one level left …
///     {
///         return;
///     }
///
/// Off by default: happy-path-first is a house style too. Knobs
/// `csharp_refactor.CR0006.then_at_least` (20) and `else_at_most` (3).
///
/// Guards: a plain `if`/`else` pair, both blocks (a chain is a different
/// rewrite); the `else` ends in `return`/`throw`/`continue`/`break`, so the
/// flow after the flip is the same; the condition is `bool` (its negation
/// unwraps an existing `!` or flips a comparison where exact); the `if` is
/// a statement of a block, so the freed lines become its siblings; no
/// comment or directive outside the two blocks (the `else` line goes); no
/// multi-line literal in the moved lines; no `using var` declared directly
/// in the `then` block (its disposal would move to the end of the enclosing
/// block); the speculative check catches a
/// local of the `then` block clashing with a later sibling.
module CSharp.Refactor.PyramidFlip

open System
open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax
open Microsoft.CodeAnalysis.Text

[<Literal>]
let Code = "CR0006"

let private exits (s: StatementSyntax) =
    s :? ReturnStatementSyntax
    || s :? ThrowStatementSyntax
    || s :? ContinueStatementSyntax
    || s :? BreakStatementSyntax

let private inner (b: BlockSyntax) =
    TextSpan.FromBounds(b.OpenBraceToken.Span.End, b.CloseBraceToken.SpanStart)

/// Lines from the first to the last statement, inclusive.
let private lineCount (text: SourceText) (b: BlockSyntax) =
    if b.Statements.Count = 0 then
        0
    else
        let first = text.Lines.GetLineFromPosition(b.Statements.[0].SpanStart).LineNumber

        let last =
            text.Lines.GetLineFromPosition(b.Statements.[b.Statements.Count - 1].Span.End).LineNumber

        last - first + 1

let private isBool (model: SemanticModel) (e: ExpressionSyntax) =
    match model.GetTypeInfo(e).Type with
    | null -> false
    | t -> t.SpecialType = SpecialType.System_Boolean

let analyze (tree: SyntaxTree) (model: SemanticModel) (ctx: RuleContext) : Suggestion list =
    let text = tree.GetText()
    let thenAtLeast = RuleContext.knobInt ctx Code "then_at_least" 20
    let elseAtMost = RuleContext.knobInt ctx Code "else_at_most" 3

    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun node ->
        match node with
        | :? IfStatementSyntax as ifs when
            not (isNull ifs.Else)
            && not (ifs.Parent :? ElseClauseSyntax)
            && (ifs.Parent :? BlockSyntax || ifs.Parent :? SwitchSectionSyntax)
            ->
            match ifs.Statement, ifs.Else.Statement with
            | (:? BlockSyntax as thenBlock), (:? BlockSyntax as elseBlock) when
                elseBlock.Statements.Count > 0
                && exits elseBlock.Statements.[elseBlock.Statements.Count - 1]
                && lineCount text thenBlock >= thenAtLeast
                && lineCount text elseBlock <= elseAtMost
                && isBool model ifs.Condition
                && not (Text.spansLines thenBlock)
                // a `using var` disposes at the end of its block: freed into the
                // enclosing block it would live on past the old `}`
                && not (
                    thenBlock.Statements
                    |> Seq.exists (fun s ->
                        match s with
                        | :? LocalDeclarationStatementSyntax as d -> not (d.UsingKeyword.IsKind SyntaxKind.None)
                        | _ -> false)
                )
                ->
                let spans = [ inner thenBlock; inner elseBlock ]

                let commentOutside =
                    ifs.DescendantTrivia(descendIntoTrivia = true)
                    |> Seq.exists (fun t ->
                        (t.IsDirective
                         || t.IsKind SyntaxKind.SingleLineCommentTrivia
                         || t.IsKind SyntaxKind.MultiLineCommentTrivia)
                        && not (spans |> List.exists (fun s -> s.Contains t.Span)))

                if commentOutside then
                    None
                else
                    let indent = Text.leadingWhitespace text ifs.SpanStart
                    let newline = SwitchRewrite.newlineAt text ifs.SpanStart

                    let unit =
                        let stmtIndent = Text.leadingWhitespace text thenBlock.Statements.[0].SpanStart

                        if stmtIndent.StartsWith indent && stmtIndent.Length > indent.Length then
                            stmtIndent.Substring indent.Length
                        else
                            "    "

                    let trimmedLines (b: BlockSyntax) =
                        text.ToString(inner b).Replace("\r\n", "\n").Split '\n'
                        |> Array.map (fun l -> l.TrimEnd())
                        |> List.ofArray
                        |> List.skipWhile String.IsNullOrWhiteSpace
                        |> List.rev
                        |> List.skipWhile String.IsNullOrWhiteSpace
                        |> List.rev

                    let guardLines = trimmedLines elseBlock

                    let bodyLines =
                        trimmedLines thenBlock
                        |> List.map (fun l ->
                            if l.StartsWith unit then
                                l.Substring unit.Length
                            else
                                l.TrimStart())

                    let head = "if (" + BoolReturn.conditionText model ifs.Condition true + ")"

                    let replacement =
                        [ head; indent + "{" ] @ guardLines @ [ indent + "}"; "" ] @ bodyLines
                        |> String.concat newline

                    let edit =
                        Suggestion.replace
                            (TextSpan.FromBounds(ifs.SpanStart, elseBlock.CloseBraceToken.Span.End))
                            replacement

                    if Guards.speculativeCheck model [ edit ] then
                        Some
                            {
                                Code = Code
                                Message = "A long happy path under a short exiting else reads better as a guard clause"
                                Span = TextSpan.FromBounds(ifs.IfKeyword.SpanStart, ifs.CloseParenToken.Span.End)
                                Fixes = [ Suggestion.fix "Flip to a guard clause" Code [ edit ] ]
                            }
                    else
                        None
            | _ -> None
        | _ -> None)
    |> List.ofSeq
