/// CR0146 (idiom, fix): a trailing `//` note on a public declaration's
/// header line is the summary its XML doc lacks.
///
///     public decimal Rate(int n) => …;   // monthly, non-compounding
///
///     /// <summary>monthly, non-compounding</summary>
///     public decimal Rate(int n) => …;
///
/// Guards: the declaration is public (or protected) and has no XML doc;
/// the comment ends the HEADER line (the line the declaration's identifier
/// sits on) with real code before it; it reads as a summary — not a
/// punctuation marker (`// ^ index`), a single word, fewer than twelve
/// characters, an instruction (`TODO`, `HACK`, `FIXME`, `NOTE:`), a
/// `pragma`-like directive, or a code fragment; it holds no `<` or `&`
/// (XML text; escaping would break the original-text proof); the file is
/// not a test file (a fixture's note labels the fixture). The doc line
/// goes above the whole declaration, attributes included — a `///`
/// between an attribute and its member is CS1587.
module CSharp.Refactor.CommentDoc

open System
open System.Text.RegularExpressions
open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax
open Microsoft.CodeAnalysis.Text

[<Literal>]
let Code = "CR0146"

let private instruction =
    Regex(
        @"^\s*(TODO|FIXME|HACK|NOTE|XXX|BUG|UNDONE|pragma|nopragma|ReSharper|csharpier|dotnet_)\b",
        RegexOptions.Compiled ||| RegexOptions.IgnoreCase
    )

let private wRegex = Regex @"^\w+\(.*\)$"

let private looksLikeCode (s: string) =
    s.Contains "=>"
    || s.Contains "();"
    || s.EndsWith ";"
    || s.Contains " = "
    || s.Contains "return "
    || s.Contains "if ("
    || s.Contains "var "
    || wRegex.IsMatch s

/// Does the note read as a summary?
let readsAsSummary (comment: string) =
    let s = comment.Trim()

    s.Length >= 12
    && not (s.Contains "<" || s.Contains "&")
    && not (instruction.IsMatch s)
    && not (looksLikeCode s)
    && (s |> Seq.exists Char.IsWhiteSpace) // more than one word
    && Char.IsLetterOrDigit s.[0] // not a marker like `^ index` or `-- node`
    && not (s.StartsWith "//")

let private isPublicish (m: MemberDeclarationSyntax) =
    m.Modifiers
    |> Seq.exists (fun t -> t.IsKind SyntaxKind.PublicKeyword || t.IsKind SyntaxKind.ProtectedKeyword)

let private hasDoc (m: MemberDeclarationSyntax) =
    m.GetLeadingTrivia()
    |> Seq.exists (fun t ->
        t.IsKind SyntaxKind.SingleLineDocumentationCommentTrivia
        || t.IsKind SyntaxKind.MultiLineDocumentationCommentTrivia)

/// The identifier token the declaration is named by.
let private identifierOf (m: MemberDeclarationSyntax) =
    match m with
    | :? MethodDeclarationSyntax as d -> ValueSome d.Identifier
    | :? PropertyDeclarationSyntax as d -> ValueSome d.Identifier
    | :? EventDeclarationSyntax as d -> ValueSome d.Identifier
    | :? BaseTypeDeclarationSyntax as d -> ValueSome d.Identifier
    | :? DelegateDeclarationSyntax as d -> ValueSome d.Identifier
    | :? ConstructorDeclarationSyntax as d -> ValueSome d.Identifier
    | :? FieldDeclarationSyntax as d when d.Declaration.Variables.Count = 1 ->
        ValueSome d.Declaration.Variables.[0].Identifier
    | :? EventFieldDeclarationSyntax as d when d.Declaration.Variables.Count = 1 ->
        ValueSome d.Declaration.Variables.[0].Identifier
    | :? IndexerDeclarationSyntax as d -> ValueSome d.ThisKeyword
    | :? EnumMemberDeclarationSyntax as d -> ValueSome d.Identifier
    | _ -> ValueNone

let analyze (tree: SyntaxTree) (_ctx: RuleContext) : Suggestion list =
    if Text.isTestFile tree then
        []
    else
        let text = tree.GetText()

        tree.GetRoot().DescendantNodes()
        |> Seq.choose (fun node ->
            match node with
            | :? MemberDeclarationSyntax as m when
                (isPublicish m || m :? EnumMemberDeclarationSyntax) && not (hasDoc m)
                ->
                match identifierOf m with
                | ValueNone -> None
                | ValueSome id ->
                    let headerLine = text.Lines.GetLineFromPosition id.SpanStart

                    // an enum member's span stops before the `,` that separates
                    // it from the next one — the separator belongs to the enum's
                    // list, not to the member — so the note trailing `Value,`
                    // lives outside the member and only the last member, which
                    // has no comma, would ever be seen
                    let separatorTrivia =
                        match m with
                        | :? EnumMemberDeclarationSyntax ->
                            let next = m.GetLastToken().GetNextToken()

                            if next.IsKind SyntaxKind.CommaToken then
                                next.TrailingTrivia :> seq<SyntaxTrivia>
                            else
                                Seq.empty
                        | _ -> Seq.empty

                    // the trailing comment on the header line: trivia after the
                    // last token of that line, inside this declaration's span
                    let trailing =
                        Seq.append (m.DescendantTrivia(descendIntoTrivia = false)) separatorTrivia
                        |> Seq.tryFind (fun t ->
                            t.IsKind SyntaxKind.SingleLineCommentTrivia
                            && text.Lines.GetLineFromPosition(t.SpanStart).LineNumber = headerLine.LineNumber
                            && text.Lines.GetLineFromPosition(t.Span.End).LineNumber = headerLine.LineNumber
                            && t.Span.End = headerLine.End)

                    match trailing with
                    | None -> None
                    | Some t ->
                        let comment = t.ToString().Substring(2).Trim()

                        // real code before the comment on that line
                        let codeBefore =
                            text.ToString(TextSpan.FromBounds(headerLine.Start, t.SpanStart)).Trim()

                        if not (readsAsSummary comment) || codeBefore = "" then
                            None
                        else
                            // the whitespace before the comment goes with it
                            let removeStart =
                                let before = text.ToString(TextSpan.FromBounds(headerLine.Start, t.SpanStart))
                                headerLine.Start + before.TrimEnd().Length

                            let declLine = text.Lines.GetLineFromPosition m.SpanStart
                            let indent = Text.leadingWhitespace text m.SpanStart
                            let newline = SwitchRewrite.newlineAt text m.SpanStart

                            Some
                                {
                                    Code = Code
                                    Message =
                                        "A trailing note on a public declaration is the summary its XML doc lacks"
                                    Span = t.Span
                                    Fixes =
                                        [
                                            Suggestion.fix
                                                "Make it the <summary>"
                                                Code
                                                [
                                                    Suggestion.insert
                                                        declLine.Start
                                                        (indent + "/// <summary>" + comment + "</summary>" + newline)
                                                    Suggestion.replace
                                                        (TextSpan.FromBounds(removeStart, t.Span.End))
                                                        ""
                                                ]
                                        ]
                                }
            | _ -> None)
        |> List.ofSeq
