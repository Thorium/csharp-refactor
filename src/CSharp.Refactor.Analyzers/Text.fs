/// Shared guards over source text and syntax: the comment guard, the
/// directive guard, and the small helpers every rule reaches for.
module CSharp.Refactor.Text

open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.Text

/// The source text of a span, as written.
let textOf (tree: SyntaxTree) (span: TextSpan) : string = tree.GetText().ToString span

/// Does the span hold a comment or a preprocessor directive? A fix whose
/// removed range covers one stands down: the comment would be lost, and a
/// multi-line edit crossing `#if`/`#region` splits the region.
let holdsCommentOrDirective (node: SyntaxNode) : bool =
    node.DescendantTrivia(descendIntoTrivia = true)
    |> Seq.exists (fun t ->
        t.IsDirective
        || t.IsKind SyntaxKind.SingleLineCommentTrivia
        || t.IsKind SyntaxKind.MultiLineCommentTrivia
        || t.IsKind SyntaxKind.SingleLineDocumentationCommentTrivia
        || t.IsKind SyntaxKind.MultiLineDocumentationCommentTrivia)

/// Is the node inside an expression tree — a lambda converted to
/// `Expression<TDelegate>`? There the shape is what a LINQ provider
/// translates, and a rewrite that is fine in code may not translate.
let insideExpressionTree (model: SemanticModel) (node: SyntaxNode) : bool =
    node.Ancestors()
    |> Seq.exists (fun a ->
        match a with
        | :? Syntax.LambdaExpressionSyntax as lambda ->
            let ti = model.GetTypeInfo lambda
            let t = ti.ConvertedType

            not (isNull t)
            && t.Name = "Expression"
            && t.ContainingNamespace.ToDisplayString() = "System.Linq.Expressions"
        | _ -> false)

/// Does any identifier token in the node spell the name?
let mentionsName (name: string) (node: SyntaxNode) : bool =
    node.DescendantTokens()
    |> Seq.exists (fun t -> t.IsKind SyntaxKind.IdentifierToken && t.ValueText = name)

/// The enclosing member declaration — the scope a fresh name must be
/// unused in — or the root when the node is not in one.
let enclosingMember (node: SyntaxNode) : SyntaxNode =
    node.Ancestors()
    |> Seq.tryFind (fun a -> a :? Syntax.MemberDeclarationSyntax)
    |> Option.defaultValue (node.SyntaxTree.GetRoot())

/// Is the expression (by its text) assigned, incremented or passed by
/// reference anywhere in the node?
let assignsTo (target: string) (node: SyntaxNode) : bool =
    let same (e: SyntaxNode) = e.ToString() = target

    node.DescendantNodesAndSelf()
    |> Seq.exists (fun n ->
        match n with
        | :? Syntax.AssignmentExpressionSyntax as a -> same a.Left
        | :? Syntax.ArgumentSyntax as a -> not (a.RefKindKeyword.IsKind SyntaxKind.None) && same a.Expression
        | :? Syntax.PostfixUnaryExpressionSyntax as u -> same u.Operand
        | :? Syntax.PrefixUnaryExpressionSyntax as u when
            u.IsKind SyntaxKind.PreIncrementExpression
            || u.IsKind SyntaxKind.PreDecrementExpression
            ->
            same u.Operand
        | _ -> false)

/// A token whose text spans lines — a verbatim or raw literal — cannot be
/// re-indented without changing the string.
let spansLines (node: SyntaxNode) : bool =
    node.DescendantTokens() |> Seq.exists (fun t -> t.Text.IndexOf '\n' >= 0)

/// A node whose first and last tokens sit on different lines.
let multiLine (text: SourceText) (node: SyntaxNode) : bool =
    text.Lines.GetLineFromPosition(node.SpanStart).LineNumber
    <> text.Lines.GetLineFromPosition(node.Span.End).LineNumber

/// The column of a position on its line.
let columnOf (text: SourceText) (position: int) : int =
    position - text.Lines.GetLineFromPosition(position).Start

/// The whitespace a position's line starts with.
let leadingWhitespace (text: SourceText) (position: int) : string =
    let line = text.Lines.GetLineFromPosition position
    let s = line.ToString()
    s.Substring(0, s.Length - s.TrimStart([| ' '; '\t' |]).Length)

/// The local names a statement list declares directly — declarations,
/// `out var`, patterns and deconstructions — for a scope-clash check.
let declaredLocals (node: SyntaxNode) : string list =
    node.DescendantNodesAndSelf()
    |> Seq.choose (fun n ->
        match n with
        | :? Syntax.VariableDeclaratorSyntax as v -> Some v.Identifier.ValueText
        | :? Syntax.SingleVariableDesignationSyntax as d -> Some d.Identifier.ValueText
        | _ -> None)
    |> List.ofSeq

/// The full line a statement alone occupies, line break included — so a
/// removal takes the line with it — else just the statement.
let statementLineSpan (text: SourceText) (s: SyntaxNode) : TextSpan =
    let line = text.Lines.GetLineFromPosition s.SpanStart

    if line.ToString().Trim() = s.ToString().Trim() then
        line.SpanIncludingLineBreak
    else
        s.Span

/// The line ending the file uses at a position.
let newlineAt (text: SourceText) (position: int) : string =
    let line = text.Lines.GetLineFromPosition position
    let brk = text.ToString(TextSpan.FromBounds(line.End, line.EndIncludingLineBreak))
    if brk = "" then System.Environment.NewLine else brk

/// A test file: named like one, or importing a test framework. The one
/// predicate CR0064, CR0069 and CR0146 share — a fixture's throw, swallow
/// or trailing note documents the test, not the code.
let isTestFile (tree: SyntaxTree) : bool =
    let name =
        System.IO.Path.GetFileNameWithoutExtension(defaultArg (Option.ofObj tree.FilePath) "")

    name.EndsWith "Tests"
    || name.EndsWith "Test"
    || name.Contains "Fixture"
    || (match tree.GetRoot() with
        | :? Syntax.CompilationUnitSyntax as root ->
            root.Usings
            |> Seq.exists (fun u ->
                let n = u.Name.ToString()

                n = "Xunit"
                || n.StartsWith "Xunit."
                || n.StartsWith "NUnit.Framework"
                || n.StartsWith "Microsoft.VisualStudio.TestTools.UnitTesting"
                || n.StartsWith "TUnit")
        | _ -> false)
