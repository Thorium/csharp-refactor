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

/// Does the node hold a comment, its own leading ones included? A fix
/// that removes the node would lose it.
let holdsComment (node: SyntaxNode) : bool =
    node.DescendantTrivia(descendIntoTrivia = true)
    |> Seq.exists (fun t ->
        t.IsKind SyntaxKind.SingleLineCommentTrivia
        || t.IsKind SyntaxKind.MultiLineCommentTrivia
        || t.IsKind SyntaxKind.SingleLineDocumentationCommentTrivia
        || t.IsKind SyntaxKind.MultiLineDocumentationCommentTrivia)

/// Does a preprocessor directive stand between the node's first and last
/// token? An edit over the node's span would take one half of a pair. A
/// directive before the node (the `#if` whose branch it sits in) or after
/// it is outside the span and stays.
let crossesDirective (node: SyntaxNode) : bool =
    node.ContainsDirectives
    && node.DescendantTrivia(descendIntoTrivia = true)
       |> Seq.exists (fun t -> t.IsDirective && node.Span.Contains t.Span)

/// Has the author switched one of these compiler warnings off where the
/// position is — a `#pragma warning disable` not restored before it, or
/// the compilation's own setting (`NoWarn`)? A rule that reads the shape
/// the warning is about stands down with it.
let compilerWarningOff (model: SemanticModel) (position: int) (ids: string list) : bool =
    let numbers =
        ids
        |> List.choose (fun id ->
            match System.Int32.TryParse(id.Substring 2) with
            | true, n -> Some n
            | _ -> None)

    let names (code: Syntax.ExpressionSyntax) =
        match code with
        | :? Syntax.LiteralExpressionSyntax as l ->
            (match l.Token.Value with
             | :? int as n -> List.contains n numbers
             | _ -> false)
        | :? Syntax.IdentifierNameSyntax as name ->
            ids
            |> List.exists (fun id ->
                System.String.Equals(id, name.Identifier.ValueText, System.StringComparison.OrdinalIgnoreCase))
        | _ -> false

    let byPragma =
        let root = model.SyntaxTree.GetRoot()

        root.ContainsDirectives
        && (root.DescendantNodes(descendIntoTrivia = true)
            |> Seq.fold
                (fun (off: bool) n ->
                    match n with
                    | :? Syntax.PragmaWarningDirectiveTriviaSyntax as pragma when
                        pragma.SpanStart < position
                        && (pragma.ErrorCodes.Count = 0 || pragma.ErrorCodes |> Seq.exists names)
                        ->
                        pragma.DisableOrRestoreKeyword.IsKind SyntaxKind.DisableKeyword
                    | _ -> off)
                false)

    let byOption =
        ids
        |> List.exists (fun id ->
            match model.Compilation.Options.SpecificDiagnosticOptions.TryGetValue id with
            | true, setting -> setting = ReportDiagnostic.Suppress
            | _ -> false)

    byPragma || byOption

/// Is the node inside an expression tree — a lambda converted to
/// `Expression<TDelegate>`, or a query expression over an `IQueryable`,
/// whose clauses are such lambdas without the arrow? There the shape is what
/// a LINQ provider translates, and a rewrite that is fine in code may not
/// translate: `$"Row {r.Name}"` is `string.Format` in a tree, which EF6
/// refuses anywhere and EF Core in a `where`.
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
        // the first `from`'s source runs where it stands, before the provider sees anything
        | :? Syntax.QueryExpressionSyntax as q when not (q.FromClause.Expression.Span.Contains node.Span) ->
            let t = model.GetTypeInfo(q.FromClause.Expression).Type

            not (isNull t)
            && (t.Name = "IQueryable"
                || t.AllInterfaces |> Seq.exists (fun i -> i.Name = "IQueryable"))
        | _ -> false)

/// Does any identifier token in the node spell the name? No node (a root's
/// missing parent) mentions nothing.
let mentionsName (name: string) (node: SyntaxNode) : bool =
    not (isNull node)
    && node.DescendantTokens()
       |> Seq.exists (fun t -> t.IsKind SyntaxKind.IdentifierToken && t.ValueText = name)

/// The enclosing member declaration — the scope a fresh name must be
/// unused in, and where a local may be written — or the root when the node
/// is not in one. A top-level statement is one member of the compilation
/// unit, but its locals are visible to every other top-level statement (a
/// later local function included): there the scope is the whole unit.
let enclosingMember (node: SyntaxNode) : SyntaxNode =
    node.Ancestors()
    |> Seq.tryFind (fun a -> a :? Syntax.MemberDeclarationSyntax)
    |> Option.map (fun m ->
        match m with
        | :? Syntax.GlobalStatementSyntax when not (isNull m.Parent) -> m.Parent
        | _ -> m)
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

/// One indent level deeper than a statement's own indent: the step from its
/// block's brace to the statement, else a tab or four spaces as the line
/// uses.
let indentStep (text: SourceText) (statement: SyntaxNode) : string =
    let own = leadingWhitespace text statement.SpanStart

    let braceIndent =
        match statement.Parent with
        | :? Syntax.BlockSyntax as b -> ValueSome(leadingWhitespace text b.OpenBraceToken.SpanStart)
        | _ -> ValueNone

    match braceIndent with
    | ValueSome b when own.Length > b.Length && own.StartsWith b -> own.Substring b.Length
    | _ -> if own.Contains "\t" then "\t" else "    "

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

/// A generated file: the `<auto-generated>` header tools write (T4, the
/// SDK's AssemblyInfo, resx designers), or an assembly-level
/// `[GeneratedCode]`. Roslyn's analyzer host skips such files by itself;
/// the tool applies fixes without that host, so the rules answer nothing
/// here and both agree. A file the generator will overwrite is not a file
/// to tidy.
let isGeneratedFile (tree: SyntaxTree) : bool =
    let root = tree.GetRoot()

    let header =
        root.GetLeadingTrivia()
        |> Seq.filter (fun t ->
            t.IsKind SyntaxKind.SingleLineCommentTrivia
            || t.IsKind SyntaxKind.MultiLineCommentTrivia
            || t.IsKind SyntaxKind.SingleLineDocumentationCommentTrivia
            || t.IsKind SyntaxKind.MultiLineDocumentationCommentTrivia)
        |> Seq.map (fun t -> t.ToString())
        |> String.concat "\n"

    let says (marker: string) =
        header.IndexOf(marker, System.StringComparison.OrdinalIgnoreCase) >= 0

    says "<auto-generated"
    || says "<autogenerated"
    || says "This code was generated by"

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
            // a C# 12 alias of a type that is no name (`using P = (int, int);`) has none
            |> Seq.filter (fun u -> not (isNull u.Name))
            |> Seq.exists (fun u ->
                let n = u.Name.ToString()

                n = "Xunit"
                || n.StartsWith "Xunit."
                || n.StartsWith "NUnit.Framework"
                || n.StartsWith "Microsoft.VisualStudio.TestTools.UnitTesting"
                || n.StartsWith "TUnit")
        | _ -> false)

/// An expression's text as the receiver of a member access: a primary
/// expression as written, anything else in parentheses. Spliced bare, `a ?? b`,
/// `a + b` or a cast would hand the member to their last operand:
/// `Regex.Replace(s ?? "", …)` must become `(s ?? "").Replace(…)`, not
/// `s ?? "".Replace(…)`, which compiles and replaces nothing.
let asReceiver (e: Syntax.ExpressionSyntax) : string =
    match e with
    | :? Syntax.IdentifierNameSyntax
    | :? Syntax.GenericNameSyntax
    | :? Syntax.MemberAccessExpressionSyntax
    | :? Syntax.InvocationExpressionSyntax
    | :? Syntax.ElementAccessExpressionSyntax
    | :? Syntax.LiteralExpressionSyntax
    | :? Syntax.InterpolatedStringExpressionSyntax
    | :? Syntax.ParenthesizedExpressionSyntax
    | :? Syntax.ThisExpressionSyntax -> e.ToString()
    | _ -> "(" + e.ToString() + ")"
