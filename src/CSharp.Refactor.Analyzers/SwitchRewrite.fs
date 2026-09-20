/// The `if`/`else if` chain to `switch` rewrite shared by CR0002 (constant
/// comparisons) and CR0003 (type tests): the chain walk, the guards a
/// switch statement needs whatever its labels are, and the rendering.
///
/// Guards common to both rules:
///
/// - only the HEAD of a chain fires — an `if` that is another's `else`
///   yields its own overlapping suggestion, and the outer wins;
/// - a `break` in a branch that is not inside a nested loop or switch
///   would, after the rewrite, leave the switch instead of the enclosing
///   loop; a `goto` is not worth reasoning about; both keep the chain;
/// - a branch body's end point that is reachable gains `break;` (the
///   compiler's own reachability, so `return` on both sides of an inner
///   `if` needs none);
/// - locals declared in two branches under one name were two scopes and
///   would be one: those sections keep their braces;
/// - no comment or directive sits outside the branch bodies (the `else`
///   lines vanish, and a comment on them would go with them); a body
///   holding a multi-line literal is not re-indented;
/// - the speculative check settles anything the above missed.
module CSharp.Refactor.SwitchRewrite

open System
open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax
open Microsoft.CodeAnalysis.Text

/// One `if` link of a chain: its condition and body.
type Link =
    {
        Condition: ExpressionSyntax
        Then: StatementSyntax
    }

/// A chain from its head: every `if` link in order and the terminal
/// `else` body, if any.
let chain (head: IfStatementSyntax) : Link list * StatementSyntax option =
    let rec go (ifs: IfStatementSyntax) (acc: Link list) =
        let acc =
            {
                Condition = ifs.Condition
                Then = ifs.Statement
            }
            :: acc

        match ifs.Else with
        | null -> List.rev acc, None
        | e ->
            match e.Statement with
            | :? IfStatementSyntax as next -> go next acc
            | terminal -> List.rev acc, Some terminal

    go head []

/// Is this `if` the head of its chain — not the statement of an `else`?
let isHead (ifs: IfStatementSyntax) = not (ifs.Parent :? ElseClauseSyntax)

/// A `break` that would change meaning, or a `goto`, inside a body.
let private hasEscapingJump (body: SyntaxNode) =
    body.DescendantNodesAndSelf()
    |> Seq.exists (fun n ->
        match n with
        | :? GotoStatementSyntax -> true
        | :? BreakStatementSyntax as b ->
            // a break inside a nested loop or switch (below the body) is that
            // construct's own
            let nested =
                b.Ancestors()
                |> Seq.takeWhile (fun a -> not (obj.ReferenceEquals(a, body.Parent)))
                |> Seq.exists (fun a ->
                    a :? ForStatementSyntax
                    || a :? ForEachStatementSyntax
                    || a :? WhileStatementSyntax
                    || a :? DoStatementSyntax
                    || a :? SwitchStatementSyntax
                    // a lambda or local function is its own world
                    || a :? AnonymousFunctionExpressionSyntax
                    || a :? LocalFunctionStatementSyntax)

            not nested
        | _ -> false)

/// The statements of a body: a block's, or the single statement.
let private statementsOf (body: StatementSyntax) =
    match body with
    | :? BlockSyntax as b -> List.ofSeq b.Statements
    | s -> [ s ]

/// Every comment or directive of the whole chain must sit inside one of the
/// bodies; the rest of the chain's text goes.
let private commentsOutsideBodies (whole: SyntaxNode) (bodies: StatementSyntax list) =
    let inner (b: StatementSyntax) =
        match b with
        | :? BlockSyntax as block -> TextSpan.FromBounds(block.OpenBraceToken.Span.End, block.CloseBraceToken.SpanStart)
        | s -> s.Span

    let spans = bodies |> List.map inner

    whole.DescendantTrivia(descendIntoTrivia = true)
    |> Seq.exists (fun t ->
        (t.IsDirective
         || t.IsKind SyntaxKind.SingleLineCommentTrivia
         || t.IsKind SyntaxKind.MultiLineCommentTrivia)
        && not (spans |> List.exists (fun s -> s.Contains t.Span)))

/// A section of the switch: its labels (each on its own line, `case X:`
/// or `default:`), the body it came from, and the changes to apply to
/// that body's text (absolute spans) before it moves.
type Section =
    {
        Labels: string list
        Body: StatementSyntax
        Changes: TextChange list
        /// Names the labels bind, scoped to the section like its locals.
        Binders: string list
    }

/// Is the chain held back by a guard every switch rewrite shares?
let blocked (whole: IfStatementSyntax) (sections: Section list) : bool =
    let bodies = sections |> List.map (fun s -> s.Body)

    bodies |> List.exists (fun b -> hasEscapingJump b)
    || bodies |> List.exists Text.spansLines
    || commentsOutsideBodies whole bodies
    // a non-block body on several lines would not re-indent
    || bodies
       |> List.exists (fun b -> not (b :? BlockSyntax) && b.ToString().IndexOf '\n' >= 0)

let private isJump (s: StatementSyntax) =
    s :? ReturnStatementSyntax
    || s :? ThrowStatementSyntax
    || s :? ContinueStatementSyntax
    || s :? BreakStatementSyntax
    || s :? GotoStatementSyntax

/// Does control reach the end of the body — so the section needs `break;`?
let private endReachable (model: SemanticModel) (body: StatementSyntax) =
    match statementsOf body with
    | [] -> true
    | stmts ->
        let last = List.last stmts

        if isJump last then
            false
        else
            match body with
            | :? BlockSyntax as b when b.Statements.Count > 0 ->
                let flow =
                    model.AnalyzeControlFlow(b.Statements.[0], b.Statements.[b.Statements.Count - 1])

                (not flow.Succeeded) || flow.EndPointIsReachable
            | :? BlockSyntax -> true
            | s ->
                let flow = model.AnalyzeControlFlow s
                (not flow.Succeeded) || flow.EndPointIsReachable

/// The body's text as lines, after its changes, with blank edges trimmed
/// and every line shifted from the body's indentation to `indent`.
let private bodyLines (text: SourceText) (section: Section) (indent: string) =
    let inner =
        match section.Body with
        | :? BlockSyntax as b -> TextSpan.FromBounds(b.OpenBraceToken.Span.End, b.CloseBraceToken.SpanStart)
        | s -> s.Span

    let rebased =
        section.Changes
        |> List.filter (fun c -> inner.Contains c.Span)
        |> List.map (fun c -> TextChange(TextSpan(c.Span.Start - inner.Start, c.Span.Length), c.NewText))

    let changed = SourceText.From(text.ToString inner).WithChanges(rebased).ToString()

    let lines =
        changed.Replace("\r\n", "\n").Split '\n'
        |> Array.map (fun l -> l.TrimEnd())
        |> List.ofArray

    let trimmed =
        lines
        |> List.skipWhile String.IsNullOrWhiteSpace
        |> List.rev
        |> List.skipWhile String.IsNullOrWhiteSpace
        |> List.rev

    let fromColumn =
        trimmed
        |> List.filter (fun l -> not (String.IsNullOrWhiteSpace l))
        |> List.map (fun l -> l.Length - l.TrimStart([| ' '; '\t' |]).Length)
        |> function
            | [] -> 0
            | cols -> List.min cols

    trimmed
    |> List.map (fun l ->
        if String.IsNullOrWhiteSpace l then
            ""
        elif l.Length >= fromColumn then
            indent + l.Substring fromColumn
        else
            indent + l.TrimStart())

/// Render the switch statement for a chain in place of it. The `if`'s own
/// indentation and the file's indentation unit (the step from the `if` to
/// its first block statement, else four spaces) set the layout.
let renderStatement
    (model: SemanticModel)
    (text: SourceText)
    (whole: IfStatementSyntax)
    (subject: string)
    (sections: Section list)
    (newline: string)
    : string =
    let indent = Text.leadingWhitespace text whole.SpanStart

    let unit =
        match whole.Statement with
        | :? BlockSyntax as b when b.Statements.Count > 0 ->
            let stmtIndent = Text.leadingWhitespace text b.Statements.[0].SpanStart

            if stmtIndent.StartsWith indent && stmtIndent.Length > indent.Length then
                stmtIndent.Substring indent.Length
            else
                "    "
        | _ -> "    "

    // locals declared under the same name in two bodies keep their braces
    let declared =
        sections
        |> List.map (fun s -> Set.ofList (Text.declaredLocals s.Body @ s.Binders))

    let clashing (i: int) =
        declared
        |> List.mapi (fun j d -> j, d)
        |> List.exists (fun (j, d) -> j <> i && not (Set.intersect d declared.[i]).IsEmpty)

    let sectionLines =
        sections
        |> List.mapi (fun i s ->
            let labels = s.Labels |> List.map (fun l -> indent + unit + l)
            let braced = clashing i
            let bodyIndent = indent + unit + unit + (if braced then unit else "")
            let body = bodyLines text s bodyIndent

            let tail =
                if endReachable model s.Body then
                    [ bodyIndent + "break;" ]
                else
                    []

            if braced then
                labels
                @ [ indent + unit + unit + "{" ]
                @ body
                @ tail
                @ [ indent + unit + unit + "}" ]
            else
                labels @ body @ tail)
        |> List.concat

    [ $"switch ({subject})"; indent + "{" ] @ sectionLines @ [ indent + "}" ]
    |> String.concat newline

let newlineAt = Text.newlineAt
