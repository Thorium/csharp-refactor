/// CR0002 (idiom, fix): an `if`/`else if` chain comparing one scrutinee
/// against constants is a `switch`: the scrutinee is read once, the arms
/// read as a table, and the compiler checks the constants for duplicates.
///
///     if (k == 1) return "one";            return k switch
///     else if (k == 2) return "two";       {
///     else return "many";                      1 => "one",
///                                              2 => "two",
///                                              _ => "many",
///                                          };
///
/// The expression form is offered where every branch is one `return e;`
/// (or `x = e;` on one target, or `throw e;`) and a terminal `else`
/// closes the chain (C# 8, `1 or 2 =>` from C# 9); otherwise the statement
/// form, with `case 1: case 2:` stacked and `default:` for the `else`.
///
/// Guards: the scrutinee is a local, parameter or `readonly` field read by
/// its plain name — after the rewrite it is evaluated once, where the chain
/// evaluated it per comparison; every comparison is `==` (either way
/// round) against a compile-time constant, through the built-in operator,
/// on an integral, `char`, `string`, `bool` or enum scrutinee (nullable of
/// those included); a chain link may be an `||` of such comparisons; three
/// comparisons at least — two read fine as `if`/`else`; no constant
/// repeats (the second arm is dead, and the switch would not compile). The
/// shared switch guards (SwitchRewrite) apply to the statement form.
module CSharp.Refactor.IfChainSwitch

open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax
open Microsoft.CodeAnalysis.Text

[<Literal>]
let Code = "CR0002"

let private switchable (t: ITypeSymbol) =
    let rec go (t: ITypeSymbol) =
        match t with
        | null -> false
        | t when t.OriginalDefinition.SpecialType = SpecialType.System_Nullable_T ->
            match t with
            | :? INamedTypeSymbol as n when n.TypeArguments.Length = 1 -> go n.TypeArguments.[0]
            | _ -> false
        | t when t.TypeKind = TypeKind.Enum -> true
        | t ->
            match t.SpecialType with
            | SpecialType.System_Int32
            | SpecialType.System_Int64
            | SpecialType.System_Int16
            | SpecialType.System_Byte
            | SpecialType.System_SByte
            | SpecialType.System_UInt16
            | SpecialType.System_UInt32
            | SpecialType.System_UInt64
            | SpecialType.System_Char
            | SpecialType.System_String
            | SpecialType.System_Boolean -> true
            | _ -> false

    go t

/// A scrutinee read once is the same as read per comparison only when
/// nothing between the comparisons can change it.
let private isStableRead (model: SemanticModel) (e: ExpressionSyntax) =
    match e with
    | :? IdentifierNameSyntax ->
        match model.GetSymbolInfo(e).Symbol with
        | :? ILocalSymbol -> true
        | :? IParameterSymbol as p -> p.RefKind = RefKind.None || p.RefKind = RefKind.In
        | :? IFieldSymbol as f -> f.IsReadOnly || f.IsConst
        | _ -> false
    | _ -> false

/// `k == C` or `C == k`: the scrutinee and the constant's text.
let private comparison (model: SemanticModel) (e: ExpressionSyntax) : (ExpressionSyntax * ExpressionSyntax) option =
    match e with
    | :? BinaryExpressionSyntax as b when b.IsKind SyntaxKind.EqualsExpression && Guards.isBuiltinOperator model b ->
        let isConstant (x: ExpressionSyntax) = model.GetConstantValue(x).HasValue

        if isStableRead model b.Left && isConstant b.Right && not (isConstant b.Left) then
            Some(b.Left, b.Right)
        elif isStableRead model b.Right && isConstant b.Left && not (isConstant b.Right) then
            Some(b.Right, b.Left)
        else
            None
    | _ -> None

/// A condition as the comparisons it `||`s together, all on one scrutinee.
let private comparisons
    (model: SemanticModel)
    (c: ExpressionSyntax)
    : (ExpressionSyntax * ExpressionSyntax list) option =
    let rec flatten (e: ExpressionSyntax) =
        match e with
        | :? ParenthesizedExpressionSyntax as p -> flatten p.Expression
        | :? BinaryExpressionSyntax as b when b.IsKind SyntaxKind.LogicalOrExpression ->
            flatten b.Left @ flatten b.Right
        | e -> [ e ]

    let parts = flatten c |> List.map (comparison model)

    if parts |> List.exists Option.isNone then
        None
    else
        let parts = parts |> List.choose id
        let scrutinee = fst parts.Head

        if parts |> List.forall (fun (s, _) -> s.ToString() = scrutinee.ToString()) then
            Some(scrutinee, parts |> List.map snd)
        else
            None

/// The one expression a branch yields, for the expression form.
type private Arm =
    | Returns of ExpressionSyntax
    | Assigns of target: string * ExpressionSyntax
    | Throws of ExpressionSyntax

let private armOf (body: StatementSyntax) =
    let single =
        match body with
        | :? BlockSyntax as b when b.Statements.Count = 1 -> Some b.Statements.[0]
        | :? BlockSyntax -> None
        | s -> Some s

    match single with
    | Some(:? ReturnStatementSyntax as r) when not (isNull r.Expression) -> Some(Returns r.Expression)
    | Some(:? ThrowStatementSyntax as t) when not (isNull t.Expression) -> Some(Throws t.Expression)
    | Some(:? ExpressionStatementSyntax as s) ->
        match s.Expression with
        | :? AssignmentExpressionSyntax as a when a.IsKind SyntaxKind.SimpleAssignmentExpression ->
            Some(Assigns(a.Left.ToString(), a.Right))
        | _ -> None
    | _ -> None

let private singleLine (e: SyntaxNode) = e.ToString().IndexOf '\n' < 0

let analyze (tree: SyntaxTree) (model: SemanticModel) (ctx: RuleContext) : Suggestion list =
    let text = tree.GetText()
    let expressionForm = ctx.LanguageVersion >= LanguageVersion.CSharp8
    let orPatterns = ctx.LanguageVersion >= LanguageVersion.CSharp9

    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun node ->
        match node with
        | :? IfStatementSyntax as head when SwitchRewrite.isHead head ->
            let links, terminal = SwitchRewrite.chain head
            let parsed = links |> List.map (fun l -> comparisons model l.Condition)

            match parsed with
            | Some(scrutinee, _) :: _ when
                parsed |> List.forall Option.isSome
                && switchable (model.GetTypeInfo(scrutinee).Type)
                ->
                let arms = parsed |> List.choose id
                let scrutineeText = scrutinee.ToString()
                let constants = arms |> List.collect snd

                if
                    constants.Length < 3
                    || not (arms |> List.forall (fun (s, _) -> s.ToString() = scrutineeText))
                    || (constants
                        |> List.map (fun c -> model.GetConstantValue(c).Value)
                        |> List.distinct)
                        .Length
                       <> constants.Length
                    || Guards.insideExpressionTree model head
                then
                    None
                else
                    let bodies = (links |> List.map (fun l -> l.Then)) @ Option.toList terminal
                    let newline = SwitchRewrite.newlineAt text head.SpanStart
                    let indent = Text.leadingWhitespace text head.SpanStart

                    let unit =
                        match head.Statement with
                        | :? BlockSyntax as b when b.Statements.Count > 0 ->
                            let stmtIndent = Text.leadingWhitespace text b.Statements.[0].SpanStart

                            if stmtIndent.StartsWith indent && stmtIndent.Length > indent.Length then
                                stmtIndent.Substring indent.Length
                            else
                                "    "
                        | _ -> "    "

                    // the expression form: one yield per branch, one kind, a terminal else
                    let expression =
                        match terminal with
                        | Some _ when
                            expressionForm
                            && (orPatterns || arms |> List.forall (fun (_, cs) -> cs.Length = 1))
                            ->
                            let armsOf = bodies |> List.map armOf

                            if
                                armsOf |> List.exists Option.isNone || Text.holdsCommentOrDirective head // a comment anywhere: the statement form keeps it
                            then
                                None
                            else
                                let armsOf = armsOf |> List.choose id

                                let exprs =
                                    armsOf
                                    |> List.map (function
                                        | Returns e -> e.ToString()
                                        | Assigns(_, e) -> e.ToString()
                                        | Throws e -> "throw " + e.ToString())

                                let kinds =
                                    armsOf
                                    |> List.choose (function
                                        | Returns _ -> Some "return"
                                        | Assigns(t, _) -> Some("assign " + t)
                                        | Throws _ -> None)
                                    |> List.distinct

                                let allLines =
                                    armsOf
                                    |> List.forall (function
                                        | Returns e
                                        | Assigns(_, e)
                                        | Throws e -> singleLine e)

                                match kinds with
                                | [ kind ] when allLines ->
                                    let lead =
                                        if kind = "return" then
                                            "return "
                                        else
                                            kind.Substring "assign ".Length + " = "

                                    let labels =
                                        (arms
                                         |> List.map (fun (_, cs) ->
                                             cs |> List.map (fun c -> c.ToString()) |> String.concat " or "))
                                        @ [ "_" ]

                                    let armLines =
                                        List.zip labels exprs
                                        |> List.map (fun (l, e) -> indent + unit + l + " => " + e + ",")

                                    Some(
                                        [ $"{lead}{scrutineeText} switch"; indent + "{" ]
                                        @ armLines
                                        @ [ indent + "};" ]
                                        |> String.concat newline
                                    )
                                | _ -> None
                        | _ -> None

                    let replacement =
                        match expression with
                        | Some r -> Some r
                        | None ->
                            let sections =
                                (List.zip links arms
                                 |> List.map (fun (link, (_, cs)) ->
                                     {
                                         SwitchRewrite.Labels = cs |> List.map (fun c -> "case " + c.ToString() + ":")
                                         SwitchRewrite.Body = link.Then
                                         SwitchRewrite.Changes = []
                                         SwitchRewrite.Binders = []
                                     }))
                                @ (terminal
                                   |> Option.map (fun body ->
                                       {
                                           SwitchRewrite.Labels = [ "default:" ]
                                           SwitchRewrite.Body = body
                                           SwitchRewrite.Changes = []
                                           SwitchRewrite.Binders = []
                                       })
                                   |> Option.toList)

                            if SwitchRewrite.blocked head sections then
                                None
                            else
                                Some(SwitchRewrite.renderStatement model text head scrutineeText sections newline)

                    replacement
                    |> Option.bind (fun replacement ->
                        let edit = Suggestion.replace head.Span replacement

                        if Guards.speculativeCheck model [ edit ] then
                            Some
                                {
                                    Code = Code
                                    Message = $"A chain comparing '{scrutineeText}' against constants is a switch"
                                    Span =
                                        TextSpan.FromBounds(head.IfKeyword.SpanStart, head.CloseParenToken.Span.End)
                                    Fixes = [ Suggestion.fix "Switch on the value" Code [ edit ] ]
                                }
                        else
                            None)
            | _ -> None
        | _ -> None)
    |> List.ofSeq
