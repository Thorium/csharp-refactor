/// Shared guards over source text and syntax: the comment guard, the
/// directive guard, and the small helpers every rule reaches for.
module CSharp.Refactor.Text

open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.Text
open System
open System.Runtime.CompilerServices
open System.Text.RegularExpressions

/// Does the list carry only these attributes (short names, with or without the suffix)? Rules that rewrite a declaration
/// read an attribute they don't know as a reason to leave it alone.
let onlyAttributesNamed (names: string list) (lists: SyntaxList<Syntax.AttributeListSyntax>) =
    lists
    |> Seq.forall (fun list ->
        list.Attributes
        |> Seq.forall (fun attribute ->
            let written = attribute.Name.ToString()
            let name = written.Substring(written.LastIndexOf '.' + 1)

            names
            |> List.exists (fun allowed -> name = allowed || name = allowed + "Attribute")))

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

/// The tree's `#pragma warning` directives in source order, read once per
/// tree: the yields-to gate asks per suggestion, and a file with 1500
/// findings rescanned its whole tree 1500 times.
let private pragmasOf =
    let cache =
        ConditionalWeakTable<SyntaxTree, Syntax.PragmaWarningDirectiveTriviaSyntax[]>()

    fun (tree: SyntaxTree) ->
        cache.GetValue(
            tree,
            fun t ->
                match t.GetRoot() with
                | :? CSharpSyntaxNode as root when root.ContainsDirectives ->
                    let rec walk (d: Syntax.DirectiveTriviaSyntax) acc =
                        if isNull d then
                            List.rev acc
                        else
                            walk
                                (d.GetNextDirective())
                                (match d with
                                 | :? Syntax.PragmaWarningDirectiveTriviaSyntax as p -> p :: acc
                                 | _ -> acc)

                    walk (root.GetFirstDirective()) [] |> Array.ofList
                | _ -> [||]
        )

/// A `#pragma warning disable` above the position, not restored before it.
/// `blanket`: does one that lists no ids count as disabling these?
let private pragmaState (blanket: bool) (tree: SyntaxTree) (position: int) (ids: string list) : bool =
    let numbers =
        ids
        |> List.choose (fun pid ->
            match Int32.TryParse(pid.Substring 2) with
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
            |> List.exists (fun pid ->
                String.Equals(pid, name.Identifier.ValueText, StringComparison.OrdinalIgnoreCase))
        | _ -> false

    pragmasOf tree
    |> Array.fold
        (fun (off: bool) pragma ->
            let disables = pragma.DisableOrRestoreKeyword.IsKind SyntaxKind.DisableKeyword

            if pragma.SpanStart >= position then
                off
            elif pragma.ErrorCodes.Count = 0 then
                (if disables then blanket || off else false)
            elif pragma.ErrorCodes |> Seq.exists names then
                disables
            else
                off)
        false

/// A `#pragma warning disable` of one of these ids above the position, not
/// restored before it; one that lists no ids disables them all.
let pragmaOff (tree: SyntaxTree) (position: int) (ids: string list) : bool = pragmaState true tree position ids

/// A `#pragma warning disable` that NAMES one of these ids above the
/// position, not restored before it: the author's decision about that
/// warning at that spot. One that lists no ids says nothing about it, and
/// silences this tool's own diagnostic there through the compiler, where
/// the run counts it.
let pragmaNamesOff (tree: SyntaxTree) (position: int) (ids: string list) : bool = pragmaState false tree position ids

/// Has the author switched one of these compiler warnings off where the
/// position is — a `#pragma warning disable` not restored before it, or
/// the compilation's own setting (`NoWarn`)? A rule that reads the shape
/// the warning is about stands down with it.
let compilerWarningOff (model: SemanticModel) (position: int) (ids: string list) : bool =
    let byOption =
        ids
        |> List.exists (fun pid ->
            match model.Compilation.Options.SpecificDiagnosticOptions.TryGetValue pid with
            | true, setting -> setting = ReportDiagnostic.Suppress
            | _ -> false)

    pragmaOff model.SyntaxTree position ids || byOption

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

/// Does code an `#if` leaves out of this compilation write the name? Another
/// target framework compiles it: a local `#if HAVE_X` assigns stays invisible
/// here (Newtonsoft.Json's `hasIgnoreDataMemberAttribute`), and "nothing writes
/// it" holds for this flavor only. Disabled text is not parsed, so the shapes
/// are read off the text: `name =`, `name += …` and the other compound
/// assignments, `name++`, `--name`, `ref name`, `out name`, `&name`, an
/// element of a deconstruction (`(name, _) = …`, nested too) and a
/// parenthesised store (`(name) = …`); the name may be verbatim (`@name`). A
/// read there (`Use(name)`) is no write.
///
/// Whose name it is decides what may stand before it: a local or the type's
/// own member (`name`, `this.name`); a member, bare or behind any receiver
/// (`name = …`, `o.name = …`); or a member behind a receiver only
/// (`o.name = …`, `Make().name++`, `(o.name, _) = …`), where a bare
/// `name = …` is another type's object initializer, an attribute argument or
/// a local.
[<RequireQualifiedAccess>]
type private Written =
    | Own
    | Member
    | ThroughReceiver

let private writePattern (name: string) (written: Written) =
    let n = @"@?" + Regex.Escape name
    let chain = @"(?:[\w@]+\s*\??\.\s*)"

    // the name where the write starts with it (`name = …`, `name++`), and
    // where a prefix leads (`++name`, `ref name`, `&name`, `(name, _) = …`).
    // Never after `:` or a quote: `xmlns:json="…"` inside a string literal of
    // the disabled text, a named argument `json: …`
    let first, after =
        match written with
        | Written.Own -> @"(?<![\w.:""'@])(?:this\.)?" + n, @"(?:this\.)?" + n
        | Written.Member -> $@"(?<![\w:""'@]){chain}*{n}", $"{chain}*{n}"
        | Written.ThroughReceiver -> @"(?<=\.\s*)" + n, $"{chain}+{n}"

    Regex(
        first
        + @"\s*(?:(?:[-+*/%&|^]|<<|>>|\?\?)?=(?![=>])|\+\+|--)"
        + @"|(?:\+\+|--)\s*"
        + after
        + @"\b|\b(?:ref|out)\s+"
        + after
        + @"\b|(?:[=(,{]|\breturn)\s*&\s*"
        + after
        + @"\b|[(,]\s*"
        + after
        + @"\s*[,)](?:[^;=]*\))?\s*=(?![=>])"
    )

/// Does the text spell the name at all? The cheap question before a
/// pattern is run over it: a megabyte of disabled text is scanned for a
/// substring a thousand times faster than for a write.
let private spells (name: string) (text: string) =
    text.IndexOf(name, StringComparison.Ordinal) >= 0

let writtenInDisabledCode (name: string) (node: SyntaxNode) : bool =
    not (isNull node)
    && node.ContainsDirectives
    && (let write = lazy (writePattern name Written.Own)

        // inside the node: its leading trivia is the code before it (a whole
        // disabled method above, with locals of its own)
        node.DescendantTrivia()
        |> Seq.exists (fun t ->
            t.IsKind SyntaxKind.DisabledTextTrivia
            && node.Span.Contains t.Span
            && (let text = t.ToString()
                spells name text && write.Value.IsMatch text)))

/// The text every `#if` of the compilation leaves out — code another target
/// framework or configuration compiles — one entry per disabled region with
/// the tree it sits in, read once per compilation.
let private disabledTexts =
    ConditionalWeakTable<Compilation, (SyntaxTree * string)[]>()

let private disabledTextOf (compilation: Compilation) : (SyntaxTree * string)[] =
    disabledTexts.GetValue(
        compilation,
        fun c ->
            c.SyntaxTrees
            |> Seq.collect (fun t ->
                let root = t.GetRoot()

                if root.ContainsDirectives then
                    root.DescendantTrivia()
                    |> Seq.filter (fun tr -> tr.IsKind SyntaxKind.DisabledTextTrivia)
                    |> Seq.map (fun tr -> t, tr.ToString())
                else
                    Seq.empty)
            |> Array.ofSeq
    )

/// Does code an `#if` leaves out of the compilation spell the name at all? A
/// method another framework's branch calls is called: a changed signature
/// breaks that build, unseen here.
let mentionedInDisabledCodeOf (compilation: Compilation) (name: string) : bool =
    match disabledTextOf compilation with
    | [||] -> false
    | texts ->
        let mention = lazy (Regex(@"(?<![\w@])@?" + Regex.Escape name + @"\b"))

        texts
        |> Array.exists (fun (_, text) -> spells name text && mention.Value.IsMatch text)

/// Whether disabled code of the compilation writes a member of a name through
/// a receiver, by name: many properties share one (`Name`, `Id`), and each
/// is asked about once per candidate.
let private receiverWrites =
    ConditionalWeakTable<Compilation, System.Collections.Concurrent.ConcurrentDictionary<string, bool>>()

/// Does code an `#if` leaves out of the compilation write a member of this
/// name? Through a receiver anywhere (`o.name = …`, `o.name++`,
/// `(o.name, _) = …`: the name alone says whose member it is, so any type's
/// counts); bare (`name = …`) only in `own`, the trees declaring the
/// member's type and the types derived from it, where a bare name can be
/// that member. A bare `name = …` elsewhere is an object initializer or a
/// `with` (construction, which `init` allows), an attribute argument, or
/// another type's member.
let memberWrittenInDisabledCodeOf (compilation: Compilation) (own: SyntaxTree seq) (name: string) : bool =
    match disabledTextOf compilation with
    | [||] -> false
    | texts ->
        let throughReceiver =
            receiverWrites
                .GetValue(compilation, (fun _ -> System.Collections.Concurrent.ConcurrentDictionary()))
                .GetOrAdd(
                    name,
                    fun name ->
                        let write = lazy (writePattern name Written.ThroughReceiver)

                        texts
                        |> Array.exists (fun (_, text) -> spells name text && write.Value.IsMatch text)
                )

        throughReceiver
        || (let own = System.Collections.Generic.HashSet own
            let write = lazy (writePattern name Written.Member)

            texts
            |> Array.exists (fun (tree, text) -> own.Contains tree && spells name text && write.Value.IsMatch text))

/// Does an identifier token in the scope, outside the span, spell the name?
/// A variable whose scope a fix widens to the scope clashes with, or
/// captures, any such mention.
let mentionsNameOutside (name: string) (scope: SyntaxNode) (inside: TextSpan) : bool =
    not (isNull scope)
    && scope.DescendantTokens()
       |> Seq.exists (fun t ->
           t.IsKind SyntaxKind.IdentifierToken
           && t.ValueText = name
           && not (inside.Contains t.Span))

/// Does a token of the node DECLARE the name — a parameter, a local, a
/// pattern or `out` variable, a `foreach` or `catch` variable, a range
/// variable — rather than read it? An identifier token outside a simple
/// name is a declaration (a member name in a declaration counts too: the
/// answer errs toward "declares").
let declaresName (name: string) (node: SyntaxNode) : bool =
    not (isNull node)
    && node.DescendantTokens()
       |> Seq.exists (fun t ->
           t.IsKind SyntaxKind.IdentifierToken
           && t.ValueText = name
           && not (t.Parent :? Syntax.SimpleNameSyntax))

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

/// The name a catch clause's new exception variable takes: the first of
/// `ex`, `e`, `exception` that nothing in the clause spells (a field read
/// there would be shadowed), no local or parameter of the member declares,
/// and no enclosing `foreach` or `catch` binds (CS0136) — none when all
/// three are taken.
let catchBinderName (clause: Syntax.CatchClauseSyntax) : string option =
    let scope = enclosingMember clause

    let taken =
        declaredLocals scope
        @ (scope.DescendantNodes()
           |> Seq.choose (fun d ->
               match d with
               | :? Syntax.ParameterSyntax as p -> Some p.Identifier.ValueText
               | _ -> None)
           |> List.ofSeq)
        @ (clause.Ancestors()
           |> Seq.choose (fun a ->
               match a with
               | :? Syntax.ForEachStatementSyntax as f -> Some f.Identifier.ValueText
               | :? Syntax.CatchClauseSyntax as c when not (isNull c.Declaration) ->
                   Some c.Declaration.Identifier.ValueText
               | _ -> None)
           |> List.ofSeq)

    [ "ex"; "e"; "exception" ]
    |> List.tryFind (fun candidate -> not (mentionsName candidate clause || List.contains candidate taken))

/// The full line a statement alone occupies, line break included — so a
/// removal takes the line with it — else just the statement.
let statementLineSpan (text: SourceText) (s: SyntaxNode) : TextSpan =
    let line = text.Lines.GetLineFromPosition s.SpanStart

    if line.ToString().Trim() = s.ToString().Trim() then
        line.SpanIncludingLineBreak
    else
        s.Span

/// The span that removes a declaration standing on lines of its own without
/// leaving the blank lines around it to pile up: a blank line below it goes
/// too when another blank line, or the opening brace of the block, is above
/// (StyleCop SA1507 / SA1505), and a blank line above goes when the closing
/// brace of the block is below (SA1508).
let declarationRemovalSpan (text: SourceText) (s: SyntaxNode) : TextSpan =
    let span = statementLineSpan text s
    let line = text.Lines.GetLineFromPosition s.SpanStart

    if span <> line.SpanIncludingLineBreak then
        span
    else
        let blank (l: TextLine) = l.ToString().Trim() = ""

        let above =
            if line.LineNumber > 0 then
                Some text.Lines.[line.LineNumber - 1]
            else
                None

        let below =
            if line.LineNumber + 1 < text.Lines.Count then
                Some text.Lines.[line.LineNumber + 1]
            else
                None

        match above, below with
        | Some a, Some b when blank b && (blank a || a.ToString().TrimEnd().EndsWith "{") ->
            TextSpan.FromBounds(span.Start, b.EndIncludingLineBreak)
        | Some a, Some b when blank a && b.ToString().TrimStart().StartsWith "}" ->
            TextSpan.FromBounds(a.Start, span.End)
        | _ -> span

/// The line ending the file uses at a position.
let newlineAt (text: SourceText) (position: int) : string =
    let line = text.Lines.GetLineFromPosition position
    let brk = text.ToString(TextSpan.FromBounds(line.End, line.EndIncludingLineBreak))
    if brk = "" then Environment.NewLine else brk

/// The text that appends one more argument, inserted at the end of the
/// list's last argument. StyleCop SA1117 wants the arguments all starting on
/// the first one's line or each on a line of its own: `, x` when the
/// arguments share a line (`F(\n a, b)` included), else `,` and a line of
/// its own indented like the last argument — arguments each on their own
/// line, or a single argument that spans lines from a line of its own. A
/// single argument starting on the `(` line keeps `, x` after it: a lambda's
/// `}, token);` is the idiom.
let appendedArgument (text: SourceText) (list: Syntax.BaseArgumentListSyntax) (argument: string) : string =
    let args = list.Arguments

    if args.Count = 0 then
        argument
    else
        let last = args.[args.Count - 1]

        let lineOf (position: int) =
            text.Lines.GetLineFromPosition(position).LineNumber

        let ownLines =
            if args.Count = 1 then
                lineOf last.SpanStart <> lineOf last.Span.End
                && lineOf last.SpanStart <> lineOf list.SpanStart
            else
                lineOf args.[1].SpanStart <> lineOf args.[0].SpanStart

        if not ownLines then
            ", " + argument
        else
            let lead = leadingWhitespace text last.SpanStart
            let column = columnOf text last.SpanStart

            let indent =
                if column <= lead.Length then
                    lead
                else
                    lead + String(' ', column - lead.Length)

            "," + newlineAt text last.SpanStart + indent + argument

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
        header.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0

    says "<auto-generated"
    || says "<autogenerated"
    || says "This code was generated by"

/// The namespace a using names, without the `global::` alias the SDK's
/// generated usings carry (`global using global::Xunit;`).
let private usingName (u: Syntax.UsingDirectiveSyntax) =
    match u.Name with
    | :? Syntax.AliasQualifiedNameSyntax as q when q.Alias.Identifier.IsKind SyntaxKind.GlobalKeyword ->
        q.Name.ToString()
    | name -> name.ToString()

/// The namespaces the `global using` directives of these trees import —
/// a GlobalUsings.cs of the project's own, or the GlobalUsings.g.cs the SDK
/// writes for `<Using Include="..." />`. A C# 12 alias of a type that is no
/// name (`global using P = (int, int);`) has none.
let globalUsingNames (trees: SyntaxTree seq) : Set<string> =
    trees
    |> Seq.collect (fun tree ->
        match tree.GetRoot() with
        | :? Syntax.CompilationUnitSyntax as root ->
            root.Usings
            |> Seq.filter (fun u -> u.GlobalKeyword.IsKind SyntaxKind.GlobalKeyword && not (isNull u.Name))
            |> Seq.map usingName
        | _ -> Seq.empty)
    |> Set.ofSeq

let private compilationGlobalUsings =
    ConditionalWeakTable<Compilation, Set<string>>()

/// The compilation's global usings, read once per compilation.
let globalUsings (compilation: Compilation) : Set<string> =
    compilationGlobalUsings.GetValue(compilation, fun c -> globalUsingNames c.SyntaxTrees)

let private isTestFramework (name: string) =
    name = "Xunit"
    || name.StartsWith "Xunit."
    || name.StartsWith "NUnit.Framework"
    || name.StartsWith "Microsoft.VisualStudio.TestTools.UnitTesting"
    || name.StartsWith "TUnit"

/// Every using directive the file writes: at the top, and inside namespace
/// blocks at any depth.
let fileUsingNames (tree: SyntaxTree) : string seq =
    let rec inNamespaces (members: SyntaxList<Syntax.MemberDeclarationSyntax>) : Syntax.UsingDirectiveSyntax seq =
        members
        |> Seq.collect (fun m ->
            match m with
            | :? Syntax.BaseNamespaceDeclarationSyntax as n -> Seq.append n.Usings (inNamespaces n.Members)
            | _ -> Seq.empty)

    match tree.GetRoot() with
    | :? Syntax.CompilationUnitSyntax as root ->
        Seq.append root.Usings (inNamespaces root.Members)
        |> Seq.filter (fun u -> not (isNull u.Name))
        |> Seq.map usingName
    | _ -> Seq.empty

/// A test file: named like one, or importing a test framework — in a using
/// of its own, at the top or in a namespace block, or in one of
/// `globalNames`, the project's global usings. The one predicate CR0064,
/// CR0069 and CR0146 share — a fixture's throw, swallow or trailing note
/// documents the test, not the code.
let isTestFileWith (globalNames: Set<string>) (tree: SyntaxTree) : bool =
    let name =
        System.IO.Path.GetFileNameWithoutExtension(defaultArg (Option.ofObj tree.FilePath) "")

    name.EndsWith "Tests"
    || name.EndsWith "Test"
    || name.Contains "Fixture"
    || fileUsingNames tree |> Seq.exists isTestFramework
    || globalNames |> Set.exists isTestFramework

/// A test file by its own name and usings alone, for a host without the
/// compilation.
let isTestFile (tree: SyntaxTree) : bool = isTestFileWith Set.empty tree

/// A test file, the compilation's global usings counted.
let isTestFileIn (compilation: Compilation) (tree: SyntaxTree) : bool =
    isTestFileWith (globalUsings compilation) tree

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
