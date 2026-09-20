/// Characters and numbers the reader cannot see.
///
/// CR0112 (correctness): invisible or direction-changing characters in
/// source — the bidi controls (U+202A–U+202E, U+2066–U+2069: the "Trojan
/// Source" shape, where a comment or literal reads one way and compiles
/// another), the Unicode tag block (U+E0000–U+E007F: invisible text an
/// LLM prompt can smuggle), zero-width spaces and joiners in identifiers
/// or literals (U+200B, U+200C, U+200D, U+2060, U+FEFF), and a byte-order
/// mark anywhere but the very start. Inside a regular string literal the
/// character becomes its `\uXXXX` escape (the same string, spelled so a
/// reader sees it); everywhere else — a comment, an identifier, a verbatim
/// or raw literal where escapes do not exist — a note. ZWJ/ZWNJ inside a
/// literal are exempt (emoji sequences, Arabic and Persian text).
///
/// CR0113 (correctness, note): `balance + 2_000_000_000`, `seconds *
/// 1_000_000` on an `int` — a literal within a factor of sixteen of the
/// ceiling makes the wrap likely for ordinary operands; `long` literals
/// within a factor of sixteen of theirs likewise (ten-digit `long`s are
/// ids, eighteen-digit ones are magnitudes). `int.MaxValue + e` /
/// `MinValue - e` overflow for every `e` but zero; `MaxValue - e` is the
/// sentinel arithmetic `Random` does on purpose and stays quiet. Decimal
/// spellings only; unsigned skipped; a file inside a `checked` context or
/// a compilation with `CheckOverflow` on is left alone. The editor offers
/// the widening (`(long)a + 2_000_000_000L`) and `checked(…)`.
module CSharp.Refactor.SourceHygiene

open System
open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax
open Microsoft.CodeAnalysis.Text

[<Literal>]
let UnicodeCode = "CR0112"

[<Literal>]
let OverflowCode = "CR0113"

// ---- CR0112 ----

let private isBidi (c: int) =
    (c >= 0x202A && c <= 0x202E) || (c >= 0x2066 && c <= 0x2069)

let private isTag (c: int) = c >= 0xE0000 && c <= 0xE007F

let private isZeroWidth (c: int) = c = 0x200B || c = 0x2060 || c = 0xFEFF

let private isJoiner (c: int) = c = 0x200C || c = 0x200D

let private describe (c: int) =
    if isBidi c then "a bidirectional control"
    elif isTag c then "a Unicode tag character"
    elif c = 0xFEFF then "a byte-order mark"
    elif isJoiner c then "a zero-width joiner"
    else "a zero-width space"

/// The code points of the text with their offsets (a surrogate pair is one).
let private codePoints (text: string) (offset: int) =
    seq {
        let mutable i = 0

        while i < text.Length do
            let c = text.[i]

            if
                Char.IsHighSurrogate c
                && i + 1 < text.Length
                && Char.IsLowSurrogate text.[i + 1]
            then
                yield offset + i, Char.ConvertToUtf32(c, text.[i + 1]), 2
                i <- i + 2
            else
                yield offset + i, int c, 1
                i <- i + 1
    }

let private unicodeHygiene (tree: SyntaxTree) : Suggestion list =
    let text = tree.GetText()
    let full = text.ToString()

    // regular string literal spans: the escape fix lives there
    let regularLiterals =
        tree.GetRoot().DescendantTokens()
        |> Seq.filter (fun t ->
            t.IsKind SyntaxKind.StringLiteralToken
            && t.Text.StartsWith "\""
            && not (t.Text.StartsWith "\"\"\""))
        |> Seq.map (fun t -> t.Span)
        |> List.ofSeq

    let inRegularLiteral (position: int) =
        regularLiterals |> List.exists (fun s -> s.Contains position)

    let literalsForToken (t: SyntaxToken) =
        t.IsKind SyntaxKind.InterpolatedStringTextToken
        && (match t.Parent.Parent with
            | :? InterpolatedStringExpressionSyntax as i -> i.StringStartToken.Text = "$\""
            | _ -> false)

    let interpolatedTexts =
        tree.GetRoot().DescendantTokens(descendIntoTrivia = false)
        |> Seq.filter literalsForToken
        |> Seq.map (fun t -> t.Span)
        |> List.ofSeq

    let inInterpolatedText (position: int) =
        interpolatedTexts |> List.exists (fun s -> s.Contains position)

    codePoints full 0
    |> Seq.choose (fun (position, c, width) ->
        let suspicious =
            isBidi c
            || isTag c
            || isZeroWidth c
            // a joiner only outside literals: text needs it, an identifier does not
            || (isJoiner c
                && not (inRegularLiteral position)
                && not (inInterpolatedText position))
            // a BOM anywhere but the first character
            || (c = 0xFEFF && position > 0)

        if not suspicious || (c = 0xFEFF && position = 0) then
            None
        else
            let span = TextSpan(position, width)
            let what = describe c

            if inRegularLiteral position || inInterpolatedText position then
                let escape =
                    if c > 0xFFFF then
                        "\\U" + c.ToString("X8")
                    else
                        "\\u" + c.ToString("X4")

                Some
                    {
                        Code = UnicodeCode
                        Message = $"{what} (U+{c:X4}) hides in this literal: spell it as an escape so a reader sees it"
                        Span = span
                        Fixes =
                            [
                                Suggestion.fix "Spell it as an escape" UnicodeCode [ Suggestion.replace span escape ]
                            ]
                    }
            else
                Some(
                    Suggestion.note
                        UnicodeCode
                        $"{what} (U+{c:X4}) hides here, where the reader cannot see it: remove it or move it into an escaped literal"
                        span
                ))
    |> List.ofSeq

// ---- CR0113 ----

let private nearCeiling (value: decimal) (max: decimal) = value >= max / 16m

let private literalValue (e: ExpressionSyntax) : (decimal * bool) option =
    match e with
    | :? LiteralExpressionSyntax as l when l.IsKind SyntaxKind.NumericLiteralExpression ->
        let t = l.Token.Text.Replace("_", "").ToLowerInvariant()

        if t.StartsWith "0x" || t.StartsWith "0b" then
            None
        else
            match l.Token.Value with
            | :? int as v -> Some(decimal v, false)
            | :? int64 as v -> Some(decimal v, true)
            | _ -> None
    | _ -> None

let private overflows (tree: SyntaxTree) (model: SemanticModel) : Suggestion list =
    if model.Compilation.Options.CheckOverflow then
        []
    else
        let isInt (t: ITypeSymbol) =
            not (isNull t) && t.SpecialType = SpecialType.System_Int32

        let isLong (t: ITypeSymbol) =
            not (isNull t) && t.SpecialType = SpecialType.System_Int64

        tree.GetRoot().DescendantNodes()
        |> Seq.choose (fun n ->
            match n with
            | :? BinaryExpressionSyntax as b when
                (b.IsKind SyntaxKind.AddExpression
                 || b.IsKind SyntaxKind.SubtractExpression
                 || b.IsKind SyntaxKind.MultiplyExpression)
                && not (
                    b.Ancestors()
                    |> Seq.exists (fun a ->
                        match a with
                        | :? CheckedExpressionSyntax as c -> c.Keyword.IsKind SyntaxKind.CheckedKeyword
                        | :? CheckedStatementSyntax as c -> c.Keyword.IsKind SyntaxKind.CheckedKeyword
                        | _ -> false)
                )
                ->
                let t = model.GetTypeInfo(b).Type

                if not (isInt t || isLong t) then
                    None
                else
                    let ceiling =
                        if isInt t then
                            decimal Int32.MaxValue
                        else
                            decimal Int64.MaxValue

                    let sides = [ b.Left, b.Right; b.Right, b.Left ]

                    // `MaxValue + e`, `MinValue - e`
                    let boundArithmetic =
                        sides
                        |> List.exists (fun (side, _) ->
                            let s = side.ToString()

                            (b.IsKind SyntaxKind.AddExpression && s.EndsWith ".MaxValue")
                            || (b.IsKind SyntaxKind.SubtractExpression && b.Left.ToString().EndsWith ".MinValue"))

                    let bigLiteral =
                        sides
                        |> List.exists (fun (side, _) ->
                            match literalValue side with
                            | Some(v, _) -> nearCeiling v ceiling
                            | None -> false)

                    let withChecked (message: string) =
                        // the editor offers `checked(…)`; the widening is the author's (every operand, back through checked((int)…))
                        let checkedFix =
                            Suggestion.fix
                                "Wrap in checked(…)"
                                OverflowCode
                                [ Suggestion.replace b.Span ("checked(" + b.ToString() + ")") ]
                            |> Suggestion.editorOnly

                        Some
                            {
                                Code = OverflowCode
                                Message = message
                                Span = b.Span
                                Fixes = [ checkedFix ]
                            }

                    if boundArithmetic then
                        withChecked
                            "Arithmetic on the type's bound overflows for every operand but zero: widen to long, or use checked(…)"
                    elif bigLiteral then
                        let typeName = if isInt t then "int" else "long"

                        withChecked
                            $"A literal within a factor of sixteen of {typeName}.MaxValue makes the wrap likely for ordinary operands: widen the arithmetic to a larger type, or use checked(…)"
                    else
                        None
            | _ -> None)
        |> List.ofSeq

let analyze (tree: SyntaxTree) (model: SemanticModel) (_ctx: RuleContext) : Suggestion list =
    unicodeHygiene tree @ overflows tree model
