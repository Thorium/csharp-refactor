/// Four redundancy fixes, in the ReSharper tradition, all readable off the
/// syntax:
///
/// 1. Attribute suffix (CR0140): `[SerializableAttribute]` → `[Serializable]`
///    — the compiler resolves the short form. Held where a type declared
///    in THIS FILE under the short name would win the lookup (attribute
///    resolution tries the exact name before appending `Attribute`); with
///    a model, the speculative check settles the rest.
/// 2. Attribute parens (CR0141): `[Foo()]` → `[Foo]` — an empty argument
///    list on an attribute says nothing.
/// 3. Redundant `@` (CR0143): `@name` where `name` is neither a keyword nor
///    a contextual keyword — the quoting does nothing at this site,
///    independently of any other. `@_` stays: bare `_` is a discard.
/// 4. `else { if }` (CR0144): an `else` block holding exactly one `if`
///    statement and nothing else — not a comment, not a directive — is
///    the `else if` that was meant; the nested `if`'s lines move left by
///    the block's indentation.
module CSharp.Refactor.RedundantSyntax

open System
open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax
open Microsoft.CodeAnalysis.Text

[<Literal>]
let AttributeSuffixCode = "CR0140"

[<Literal>]
let AttributeParensCode = "CR0141"

[<Literal>]
let VerbatimIdentifierCode = "CR0143"

[<Literal>]
let ElseIfCode = "CR0144"

/// The identifier token an attribute's name ends in: `Foo` of `Foo`,
/// `System.Foo`, `Foo<T>`.
let private attributeNameToken (name: NameSyntax) =
    match name with
    | :? IdentifierNameSyntax as i -> ValueSome i.Identifier
    | :? QualifiedNameSyntax as q ->
        match q.Right with
        | :? IdentifierNameSyntax as i -> ValueSome i.Identifier
        | :? GenericNameSyntax as g -> ValueSome g.Identifier
        | _ -> ValueNone
    | :? GenericNameSyntax as g -> ValueSome g.Identifier
    | _ -> ValueNone

/// The type names this file declares — the ones that would shadow a
/// trimmed attribute name.
let private declaredTypeNames (root: SyntaxNode) =
    root.DescendantNodes()
    |> Seq.choose (fun n ->
        match n with
        | :? BaseTypeDeclarationSyntax as t -> Some t.Identifier.ValueText
        | :? DelegateDeclarationSyntax as d -> Some d.Identifier.ValueText
        | _ -> None)
    |> Set.ofSeq

let private attributeSuffix (root: SyntaxNode) (model: SemanticModel option) : Suggestion list =
    let declared = declaredTypeNames root

    root.DescendantNodes()
    |> Seq.choose (fun n ->
        match n with
        | :? AttributeSyntax as a ->
            match attributeNameToken a.Name with
            | ValueSome token when
                token.ValueText.EndsWith "Attribute"
                && token.ValueText.Length > "Attribute".Length
                && not (token.Text.StartsWith "@")
                ->
                let short =
                    token.ValueText.Substring(0, token.ValueText.Length - "Attribute".Length)

                if declared.Contains short then
                    None
                else
                    let edit = Suggestion.replace token.Span short

                    // a type named like the short form in ANOTHER file of the
                    // compilation would win the lookup too, and only a model
                    // can see it: without one the finding is a note
                    match model with
                    | Some m when not (Guards.speculativeCheck m [ edit ]) -> None
                    | _ ->
                        Some
                            {
                                Code = AttributeSuffixCode
                                Message = $"The compiler resolves '{short}' without the 'Attribute' suffix"
                                Span = token.Span
                                Fixes =
                                    (if model.IsSome then
                                         [ Suggestion.fix "Drop the 'Attribute' suffix" AttributeSuffixCode [ edit ] ]
                                     else
                                         [])
                            }
            | _ -> None
        | _ -> None)
    |> List.ofSeq

let private attributeParens (root: SyntaxNode) : Suggestion list =
    root.DescendantNodes()
    |> Seq.choose (fun n ->
        match n with
        | :? AttributeSyntax as a when
            not (isNull a.ArgumentList)
            && a.ArgumentList.Arguments.Count = 0
            && not (Text.holdsCommentOrDirective a.ArgumentList)
            ->
            Some
                {
                    Code = AttributeParensCode
                    Message = "An empty attribute argument list says nothing"
                    Span = a.ArgumentList.Span
                    Fixes =
                        [
                            Suggestion.fix
                                "Drop the empty argument list"
                                AttributeParensCode
                                [ Suggestion.replace a.ArgumentList.Span "" ]
                        ]
                }
        | _ -> None)
    |> List.ofSeq

/// Is the bare word a keyword, or a contextual keyword in some position?
/// Contextual keywords (`var`, `async`, `field`, `record`, `nameof`, …) are
/// legal identifiers almost everywhere, but not everywhere, and the
/// difference is the reader's; the `@` stays on those.
let private isReservedWord (word: string) =
    SyntaxFacts.GetKeywordKind word <> SyntaxKind.None
    || SyntaxFacts.GetContextualKeywordKind word <> SyntaxKind.None

let private verbatimIdentifiers (root: SyntaxNode) : Suggestion list =
    root.DescendantTokens()
    |> Seq.choose (fun t ->
        if
            t.IsKind SyntaxKind.IdentifierToken
            && t.Text.StartsWith "@"
            && t.ValueText <> "_"
            && not (isReservedWord t.ValueText)
        then
            Some
                {
                    Code = VerbatimIdentifierCode
                    Message = $"'@' does nothing on '{t.ValueText}', which is no keyword"
                    Span = t.Span
                    Fixes =
                        [
                            Suggestion.fix
                                "Drop the '@'"
                                VerbatimIdentifierCode
                                [ Suggestion.replace (TextSpan(t.SpanStart, 1)) "" ]
                        ]
                }
        else
            None)
    |> List.ofSeq

/// A token whose text spans lines: re-indenting it would change the string.
let private spansLines (node: SyntaxNode) =
    node.DescendantTokens() |> Seq.exists (fun t -> t.Text.IndexOf '\n' >= 0)

/// The `else { if … }` collapse: the block goes, the inner `if` text moves
/// up beside the `else`, and its continuation lines move left by the
/// block's extra indentation where they have room.
let private elseIf (root: SyntaxNode) (text: SourceText) : Suggestion list =
    root.DescendantNodes()
    |> Seq.choose (fun n ->
        match n with
        | :? ElseClauseSyntax as e ->
            match e.Statement with
            | :? BlockSyntax as block when
                block.Statements.Count = 1
                && (block.Statements.[0] :? IfStatementSyntax)
                // only comments INSIDE the inner if's own span travel with it;
                // one anywhere else in the block (its leading trivia included)
                // would be dropped, and holds the fix
                && not (
                    block.DescendantTrivia(descendIntoTrivia = true)
                    |> Seq.exists (fun t ->
                        (t.IsKind SyntaxKind.SingleLineCommentTrivia
                         || t.IsKind SyntaxKind.MultiLineCommentTrivia
                         || t.IsDirective)
                        && not (block.Statements.[0].Span.Contains t.Span))
                )
                ->
                let inner = block.Statements.[0] :?> IfStatementSyntax

                if spansLines inner then
                    None
                else
                    let elseLine = text.Lines.GetLineFromPosition e.ElseKeyword.SpanStart
                    let innerLine = text.Lines.GetLineFromPosition inner.SpanStart
                    let elseColumn = e.ElseKeyword.SpanStart - elseLine.Start
                    let innerColumn = inner.SpanStart - innerLine.Start
                    let delta = innerColumn - elseColumn

                    let lines = inner.ToString().Split '\n'

                    let dedented =
                        lines
                        |> Array.mapi (fun i line ->
                            if i = 0 then
                                line
                            else
                                let leading = line.Length - line.TrimStart(' ').Length

                                if delta > 0 && leading >= delta then
                                    line.Substring delta
                                else
                                    line)
                        |> String.concat "\n"

                    // from the `else` keyword through the closing brace
                    let span =
                        TextSpan.FromBounds(e.ElseKeyword.SpanStart, block.CloseBraceToken.Span.End)

                    Some
                        {
                            Code = ElseIfCode
                            Message = "An 'else' holding only an 'if' is an 'else if'"
                            Span = TextSpan.FromBounds(e.ElseKeyword.SpanStart, block.OpenBraceToken.Span.End)
                            Fixes =
                                [
                                    Suggestion.fix
                                        "Flatten to 'else if'"
                                        ElseIfCode
                                        [ Suggestion.replace span ("else " + dedented) ]
                                ]
                        }
            | _ -> None
        | _ -> None)
    |> List.ofSeq

let private analyzeWith (tree: SyntaxTree) (model: SemanticModel option) : Suggestion list =
    let root = tree.GetRoot()
    let text = tree.GetText()

    attributeSuffix root model
    @ attributeParens root
    @ verbatimIdentifiers root
    @ elseIf root text

let analyze (tree: SyntaxTree) (_ctx: RuleContext) : Suggestion list = analyzeWith tree None

let analyzeTyped (tree: SyntaxTree) (model: SemanticModel) (_ctx: RuleContext) : Suggestion list =
    analyzeWith tree (Some model)
