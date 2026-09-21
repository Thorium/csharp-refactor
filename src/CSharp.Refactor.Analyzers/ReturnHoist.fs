/// CR0173 (idiom, fix): a `return` (or an assignment to one target) that
/// every branch of an `if` performs, on a different value, is one `return`
/// of a conditional:
///
///     if (a) return "1"; else return "2";   →  return a ? "1" : "2";
///     if (a) return "1"; return "2";        →  return a ? "1" : "2";
///     if (a) x = f(); else x = g();         →  x = a ? f() : g();
///
/// The bool-literal spellings (`return true` / `return false`) are
/// CR0001's, which returns the condition itself; this rule stands down
/// for them. Guards: each branch is exactly one statement, a `return`
/// with an expression or an assignment to the same local or field (a
/// property setter may act, and the branches ran it once each); the
/// else-less form takes the `return` that immediately follows the `if`;
/// the two values differ in text (`if (a) return x; else return x;` is a
/// different smell); no comment or directive inside is swallowed; an `if`
/// that is another `if`'s `else` is left to the chain; the result is one
/// line that fits the wrap column (a conditional over two multi-line arms
/// is no clearer than the `if`); a branch value that is itself a
/// conditional, an assignment or a lambda is parenthesised, and a `throw`
/// branch is left alone; the speculative
/// re-bind settles the conditional's typing (a natural common type, or
/// the target type from C# 9). IDE0046 and IDE0045 offer the same
/// rewrite in the editor; where they are on, this rule yields.
module CSharp.Refactor.ReturnHoist

open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax
open Microsoft.CodeAnalysis.Text

[<Literal>]
let Code = "CR0173"

/// The one statement a branch holds: bare, or alone in a block.
let private single (s: StatementSyntax) =
    match s with
    | :? BlockSyntax as b when b.Statements.Count = 1 -> ValueSome b.Statements.[0]
    | :? BlockSyntax -> ValueNone
    | other -> ValueSome other

let private isBoolLiteral (e: ExpressionSyntax) =
    e.IsKind SyntaxKind.TrueLiteralExpression
    || e.IsKind SyntaxKind.FalseLiteralExpression

/// A branch's value as a conditional arm: the low-precedence shapes take
/// parentheses (`a ? (b ? c : d) : e` reads; `a ? b ? c : d : e` does too,
/// to the compiler, but not to anyone else).
let private armText (e: ExpressionSyntax) =
    match e with
    | :? ConditionalExpressionSyntax
    | :? AssignmentExpressionSyntax
    | :? LambdaExpressionSyntax
    | :? SwitchExpressionSyntax
    | :? QueryExpressionSyntax
    | :? ThrowExpressionSyntax -> "(" + e.ToString() + ")"
    | _ -> e.ToString()

/// The condition as the head of a conditional: an assignment or a lower
/// conditional inside it takes parentheses.
let private conditionText (c: ExpressionSyntax) =
    match c with
    | :? ConditionalExpressionSyntax
    | :? AssignmentExpressionSyntax -> "(" + c.ToString() + ")"
    | _ -> c.ToString()

let private returned (s: StatementSyntax) =
    match single s with
    | ValueSome(:? ReturnStatementSyntax as r) when not (isNull r.Expression || r.Expression :? ThrowExpressionSyntax) ->
        Some r.Expression
    | _ -> None

let private assigned (s: StatementSyntax) =
    match single s with
    | ValueSome(:? ExpressionStatementSyntax as es) ->
        match es.Expression with
        | :? AssignmentExpressionSyntax as a when a.IsKind SyntaxKind.SimpleAssignmentExpression ->
            ValueSome(a.Left, a.Right)
        | _ -> ValueNone
    | _ -> ValueNone

let private isLocalOrField (model: SemanticModel) (target: ExpressionSyntax) =
    match model.GetSymbolInfo(target).Symbol with
    | :? ILocalSymbol
    | :? IParameterSymbol
    | :? IFieldSymbol -> true
    | _ -> false

let private leadingComment (s: StatementSyntax) =
    s.GetLeadingTrivia()
    |> Seq.exists (fun t ->
        t.IsKind SyntaxKind.SingleLineCommentTrivia
        || t.IsKind SyntaxKind.MultiLineCommentTrivia
        || t.IsDirective)

/// The statement right after an `if` in its block, when the `if` has no `else`.
let private following (ifs: IfStatementSyntax) =
    match ifs.Parent with
    | :? BlockSyntax as block ->
        let i = block.Statements.IndexOf ifs

        if i >= 0 && i + 1 < block.Statements.Count then
            ValueSome block.Statements.[i + 1]
        else
            ValueNone
    | _ -> ValueNone

let analyze (tree: SyntaxTree) (model: SemanticModel) (ctx: RuleContext) : Suggestion list =
    let text = tree.GetText()
    let wrapAt = RuleContext.wrapColumn ctx Code

    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun node ->
        match node with
        | :? IfStatementSyntax as ifs when
            not (ifs.Parent :? ElseClauseSyntax)
            && not (Text.holdsCommentOrDirective ifs)
            && (match model.GetTypeInfo(ifs.Condition).Type with
                | null -> false
                | t -> t.SpecialType = SpecialType.System_Boolean)
            ->
            let cond = conditionText ifs.Condition

            // one line that fits: a conditional over two multi-line arms (an `Ok(new …)`
            // against a `NotFound(CreateErrorResponse(…))` of five lines) is no clearer
            // than the `if` it replaces, and the else-less form is the reader's guard
            let fits (replacement: string) =
                not (replacement.Contains "\n")
                && (Text.leadingWhitespace text ifs.SpanStart).Length + replacement.Length
                   <= wrapAt

            let offer (span: TextSpan) (replacement: string) (message: string) (title: string) =
                let edit = Suggestion.replace span replacement

                if fits replacement && Guards.speculativeCheck model [ edit ] then
                    Some
                        {
                            Code = Code
                            Message = message
                            Span = span
                            Fixes = [ Suggestion.fix title Code [ edit ] ]
                        }
                else
                    None

            // the return form: the `else` returns, or the next statement does
            let returnForm =
                match returned ifs.Statement with
                | Some thenValue when not (isBoolLiteral thenValue) ->
                    let elseValue =
                        if not (isNull ifs.Else) then
                            returned ifs.Else.Statement |> Option.map (fun v -> v, ifs.Span.End)
                        else
                            match following ifs with
                            | ValueSome next when not (leadingComment next || Text.holdsCommentOrDirective next) ->
                                returned next |> Option.map (fun v -> v, next.Span.End)
                            | _ -> None

                    match elseValue with
                    | Some(elseValue, endAt) when
                        not (isBoolLiteral elseValue) && thenValue.ToString() <> elseValue.ToString()
                        ->
                        offer
                            (TextSpan.FromBounds(ifs.SpanStart, endAt))
                            $"return {cond} ? {armText thenValue} : {armText elseValue};"
                            "Both branches return: return the conditional"
                            "Return the conditional"
                    | _ -> None
                | _ -> None

            match returnForm with
            | Some s -> Some s
            | None ->
                // the assignment form: one target, both branches
                match assigned ifs.Statement with
                | ValueSome(target, thenValue) when not (isNull ifs.Else || isBoolLiteral thenValue) ->
                    match assigned ifs.Else.Statement with
                    | ValueSome(target2, elseValue) when
                        not (isBoolLiteral elseValue)
                        && target.ToString() = target2.ToString()
                        && thenValue.ToString() <> elseValue.ToString()
                        && isLocalOrField model target
                        ->
                        offer
                            ifs.Span
                            $"{target} = {cond} ? {armText thenValue} : {armText elseValue};"
                            "Both branches assign the target: assign the conditional"
                            "Assign the conditional"
                    | _ -> None
                | _ -> None
        | _ -> None)
    |> List.ofSeq
