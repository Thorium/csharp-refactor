/// CR0103 — cosmetic. `$"no holes"` is a plain string with an
/// interpolation marker that does nothing; the fix drops the `$`. Skipped
/// when the text holds `{{` or `}}` (those escapes would need unescaping),
/// for raw strings, and — the case the F# twin FR0086 found the hard way —
/// wherever the site EXPECTS an interpolated string rather than a plain
/// one: a `FormattableString`/`IFormattable` target, or an interpolated
/// string handler parameter (`ILogger`'s, `Debug.Assert`'s): there a plain
/// string is a different conversion, or none. With a semantic model the
/// converted type answers; without one, any argument position or
/// explicitly typed declaration keeps its `$`.
module CSharp.Refactor.InterpolationHoles

open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax

[<Literal>]
let Code = "CR0103"

/// Is the string converted to `System.String` here — not to
/// `FormattableString`, `IFormattable`, a handler struct, `object`
/// (boxing keeps a string, fine) or something else?
let private convertsToPlainString (model: SemanticModel) (s: InterpolatedStringExpressionSyntax) =
    let info = model.GetTypeInfo s
    let converted = info.ConvertedType

    if isNull converted then
        // nothing expected: an expression statement, a discard
        true
    else
        match converted.SpecialType with
        | SpecialType.System_String
        | SpecialType.System_Object -> true
        | _ -> false

/// Without a model: positions where something other than a plain string
/// may be expected. An argument (a handler or FormattableString
/// parameter), a declaration with a spelled type, a return, an
/// initializer of a member — any of them may want the `$`; `var x = $"…"`,
/// a `+` operand, an expression statement cannot.
let private mayExpectFormattable (s: InterpolatedStringExpressionSyntax) =
    let parent = s.Parent

    match parent with
    | :? ArgumentSyntax
    | :? AttributeArgumentSyntax
    | :? ReturnStatementSyntax
    | :? ArrowExpressionClauseSyntax
    | :? YieldStatementSyntax -> true
    | :? EqualsValueClauseSyntax as ev ->
        match ev.Parent with
        | :? VariableDeclaratorSyntax as d ->
            match d.Parent with
            | :? VariableDeclarationSyntax as decl -> not decl.Type.IsVar
            | _ -> true
        | _ -> true
    | :? AssignmentExpressionSyntax
    | :? CastExpressionSyntax
    | :? ConditionalExpressionSyntax
    | :? SwitchExpressionArmSyntax -> true
    | _ -> false

let private analyzeWith (tree: SyntaxTree) (model: SemanticModel option) : Suggestion list =
    let root = tree.GetRoot()

    root.DescendantNodes()
    |> Seq.choose (fun node ->
        match node with
        | :? InterpolatedStringExpressionSyntax as s ->
            let holeFree =
                s.Contents
                |> Seq.forall (fun c ->
                    match c with
                    | :? InterpolatedStringTextSyntax as t ->
                        let text = t.TextToken.Text
                        not (text.Contains "{{" || text.Contains "}}")
                    | _ -> false)

            // a raw interpolated string ($""" """) keeps its shape; the
            // start token tells the kinds apart
            let plainStart = s.StringStartToken.IsKind SyntaxKind.InterpolatedStringStartToken

            let verbatimStart =
                s.StringStartToken.IsKind SyntaxKind.InterpolatedVerbatimStringStartToken

            let targetIsString =
                match model with
                | Some m -> convertsToPlainString m s
                | None -> not (mayExpectFormattable s)

            if holeFree && (plainStart || verbatimStart) && targetIsString then
                let start = s.StringStartToken.Span
                // `$"` → `"`, `$@"` / `@$"` → `@"`
                let startText = s.StringStartToken.Text
                let replacement = startText.Replace("$", "")

                Some
                    {
                        Code = Code
                        Message = "Interpolated string has no holes; a plain string says the same"
                        Span = s.Span
                        Fixes = [ Suggestion.fix "Remove '$'" Code [ Suggestion.replace start replacement ] ]
                    }
            else
                None
        | _ -> None)
    |> List.ofSeq

/// Parse-only: the conservative positional gate stands in for the type.
let analyze (tree: SyntaxTree) (_ctx: RuleContext) : Suggestion list = analyzeWith tree None

/// Typed: the converted type decides.
let analyzeTyped (tree: SyntaxTree) (model: SemanticModel) (_ctx: RuleContext) : Suggestion list =
    analyzeWith tree (Some model)
