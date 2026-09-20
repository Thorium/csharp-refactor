/// CR0016 (idiom, note, off by default): a loop steered by a `bool` flag
/// keeps running the rest of its body after the decision is made.
///
///     bool done = false;
///     while (!done && reader.Read())
///     {
///         if (reader.IsEnd) done = true;
///         Process(reader);                 // runs once more after the decision
///     }
///
/// C# has `break`: the decision is the place to leave. The note counts the
/// statements that still run after the flag is raised and says nothing
/// when there are none (a raise as the last statement is already a
/// `break` in spirit).
///
/// Guards: the flag is a `bool` local declared before the loop; the loop
/// condition names it negated (`!done`, also `!(done || other)`); the body
/// assigns it `true` (a flag only written elsewhere, or read after the
/// loop, still gets the note — the `break` can sit beside the raise).
module CSharp.Refactor.FlagLoop

open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax

[<Literal>]
let Code = "CR0016"

/// The names a condition tests negated: `!a`, `!(a || b)`, `!a && c`.
let rec private negatedNames (e: ExpressionSyntax) : string list =
    match e with
    | :? ParenthesizedExpressionSyntax as p -> negatedNames p.Expression
    | :? PrefixUnaryExpressionSyntax as u when u.IsKind SyntaxKind.LogicalNotExpression ->
        let rec names (x: ExpressionSyntax) =
            match x with
            | :? ParenthesizedExpressionSyntax as p -> names p.Expression
            | :? IdentifierNameSyntax as i -> [ i.Identifier.ValueText ]
            | :? BinaryExpressionSyntax as b when b.IsKind SyntaxKind.LogicalOrExpression ->
                names b.Left @ names b.Right
            | _ -> []

        names u.Operand
    | :? BinaryExpressionSyntax as b when b.IsKind SyntaxKind.LogicalAndExpression ->
        negatedNames b.Left @ negatedNames b.Right
    | _ -> []

let private isTrue (e: ExpressionSyntax) =
    match e with
    | :? LiteralExpressionSyntax as l -> l.IsKind SyntaxKind.TrueLiteralExpression
    | _ -> false

/// Statements that run after `raise` before the loop tests again: those
/// after it in its block, and after each enclosing statement up to the
/// loop body.
let private statementsAfter (body: StatementSyntax) (raise: StatementSyntax) =
    let rec go (s: SyntaxNode) (acc: int) =
        if obj.ReferenceEquals(s, body) then
            acc
        else
            match s.Parent with
            | :? BlockSyntax as b ->
                let idx = b.Statements.IndexOf((s :?> StatementSyntax))
                let after = b.Statements.Count - idx - 1

                if obj.ReferenceEquals(b, body) then
                    acc + after
                else
                    go b (acc + after)
            | :? StatementSyntax as p -> go p acc
            | :? ElseClauseSyntax as e -> go e.Parent acc
            | :? SwitchSectionSyntax as sec ->
                let idx = sec.Statements.IndexOf((s :?> StatementSyntax))
                go sec.Parent (acc + sec.Statements.Count - idx - 1)
            | _ -> acc

    go raise 0

let analyze (tree: SyntaxTree) (model: SemanticModel) (_ctx: RuleContext) : Suggestion list =
    tree.GetRoot().DescendantNodes()
    |> Seq.collect (fun node ->
        let loop =
            match node with
            | :? WhileStatementSyntax as w -> Some(w.Condition, w.Statement)
            | :? ForStatementSyntax as f when not (isNull f.Condition) -> Some(f.Condition, f.Statement)
            | :? DoStatementSyntax as d -> Some(d.Condition, d.Statement)
            | _ -> None

        match loop with
        | None -> []
        | Some(condition, body) ->
            negatedNames condition
            |> List.choose (fun name ->
                let isBoolLocal =
                    condition.DescendantNodesAndSelf()
                    |> Seq.exists (fun n ->
                        match n with
                        | :? IdentifierNameSyntax as i when i.Identifier.ValueText = name ->
                            match model.GetSymbolInfo(i).Symbol with
                            | :? ILocalSymbol as l ->
                                l.Type.SpecialType = SpecialType.System_Boolean
                                && l.DeclaringSyntaxReferences
                                   |> Seq.forall (fun r -> r.Span.Start < node.SpanStart)
                            | _ -> false
                        | _ -> false)

                if not isBoolLocal then
                    None
                else
                    // the raise: `name = true;` as a statement inside the body
                    let raises =
                        body.DescendantNodesAndSelf()
                        |> Seq.choose (fun n ->
                            match n with
                            | :? ExpressionStatementSyntax as s ->
                                match s.Expression with
                                | :? AssignmentExpressionSyntax as a when
                                    a.IsKind SyntaxKind.SimpleAssignmentExpression
                                    && a.Left.ToString() = name
                                    && isTrue a.Right
                                    ->
                                    Some s
                                | _ -> None
                            | _ -> None)
                        |> List.ofSeq

                    raises
                    |> List.tryPick (fun raise ->
                        let after = statementsAfter body raise

                        if after > 0 then
                            Some(
                                Suggestion.note
                                    Code
                                    $"'{name}' is raised here and {after} more statement(s) run before the loop tests it: 'break' at the decision"
                                    raise.Span
                            )
                        else
                            None)))
    |> List.ofSeq
