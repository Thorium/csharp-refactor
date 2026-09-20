/// Two boolean simplifications:
///
/// 1. Identity elements (CR0007, fix): `x && true`, `true && x`,
///    `x || false`, `false || x` — the literal contributes nothing, the
///    expression IS the other operand. `x && false` and `true || x` are
///    deliberately left alone: their VALUE is constant but `x`'s evaluation
///    (and its effects) still happens or is skipped, so the honest rewrite
///    would need to reason about purity for no gain. Both operands must be
///    `bool` through the BUILT-IN operator: a `dynamic` operand binds at run
///    time and `d && true` is `dynamic` where `d` alone is too.
///
/// 2. Idempotent duplicates (CR0008, fix): `a || a` → `a`, `a && a` → `a` —
///    only when the operands are textually identical AND provably pure:
///    short-circuiting means `a || a` evaluates `a` twice on the false
///    path, so a side-effecting `a` collapsed to one evaluation would change
///    behaviour; `TryConnect() || TryConnect()` is the deliberate retry
///    idiom. The message also nudges toward the likelier truth: a
///    duplicated operand is usually a copy-paste that meant another name.
///
/// Both run inside expression trees deliberately: removing a node leaves a
/// strictly simpler tree of shapes the translator already accepted.
module CSharp.Refactor.BooleanSimplify

open System.Text.RegularExpressions
open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax

[<Literal>]
let IdentityCode = "CR0007"

[<Literal>]
let DuplicateCode = "CR0008"

let private isBoolLiteral (e: ExpressionSyntax) (value: bool) =
    match e with
    | :? LiteralExpressionSyntax as l ->
        (value && l.IsKind SyntaxKind.TrueLiteralExpression)
        || (not value && l.IsKind SyntaxKind.FalseLiteralExpression)
    | _ -> false

let private isBool (model: SemanticModel) (e: ExpressionSyntax) =
    match model.GetTypeInfo(e).Type with
    | null -> false
    | t -> t.SpecialType = SpecialType.System_Boolean

let private whitespace = Regex(@"\s+", RegexOptions.Compiled)

let private normalized (e: SyntaxNode) = whitespace.Replace(e.ToString(), "")

let analyze (tree: SyntaxTree) (model: SemanticModel) (_ctx: RuleContext) : Suggestion list =
    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun node ->
        match node with
        | :? BinaryExpressionSyntax as b when
            (b.IsKind SyntaxKind.LogicalAndExpression
             || b.IsKind SyntaxKind.LogicalOrExpression)
            && Guards.isBuiltinOperator model b
            && isBool model b.Left
            && isBool model b.Right
            ->
            let isAnd = b.IsKind SyntaxKind.LogicalAndExpression

            // the identity element of the operator: `true` for &&, `false` for ||
            let identity = isAnd

            let kept =
                if isBoolLiteral b.Right identity then Some b.Left
                elif isBoolLiteral b.Left identity then Some b.Right
                else None

            match kept with
            | Some other ->
                Some
                    {
                        Code = IdentityCode
                        Message =
                            (if isAnd then
                                 "'&& true' contributes nothing; the expression is the other operand"
                             else
                                 "'|| false' contributes nothing; the expression is the other operand")
                        Span = b.Span
                        Fixes =
                            [
                                Suggestion.fix
                                    "Drop the literal"
                                    IdentityCode
                                    [ Suggestion.replace b.Span (other.ToString()) ]
                            ]
                    }
            | None ->
                if normalized b.Left = normalized b.Right && Guards.isPureExpression model b.Left then
                    Some
                        {
                            Code = DuplicateCode
                            Message =
                                "Both operands are the same expression: one suffices — or the second was meant to name something else"
                            Span = b.Span
                            Fixes =
                                [
                                    Suggestion.fix
                                        "Drop the duplicate"
                                        DuplicateCode
                                        [ Suggestion.replace b.Span (b.Left.ToString()) ]
                                ]
                        }
                else
                    None
        | _ -> None)
    |> List.ofSeq
