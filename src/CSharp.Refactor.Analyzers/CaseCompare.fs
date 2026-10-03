/// CR0188 (performance, fix): `x.ToLower() == "abc"` lowers a copy of `x` to
/// compare it once - an allocation per call, and a culture's case rules (the
/// Turkish dotless i) where a case-insensitive comparison was meant:
///
///     x.ToLower() == "abc"              x.Equals("abc", StringComparison.OrdinalIgnoreCase)
///     x.ToUpper() != "ABC"         →    !x.Equals("ABC", StringComparison.OrdinalIgnoreCase)
///     x.ToLower().StartsWith("ab")      x.StartsWith("ab", StringComparison.OrdinalIgnoreCase)
///
/// The F# side's FR0039, with its gates. The literal is pure ASCII and
/// already in the lowering's direction (`x.ToLower() == "ABC"` can never be
/// true, and silently making it match changes behaviour). Measured over every
/// UTF-16 character against every ASCII one, the two spellings then differ
/// only where a culture was the bug or no key holds the character: the
/// invariant folds on U+212A KELVIN SIGN and U+017F LONG S; the culture folds
/// on those and the Turkish İ and ı, and under tr-TR on `I`/`i` themselves,
/// which the lowering got wrong. The one-string `StartsWith`, `EndsWith`,
/// `IndexOf` and `LastIndexOf` compare by the current culture, which skips
/// ignorable characters (`"AB\0".ToLower().EndsWith("b")` is true); the
/// ordinal comparison does not, and the culture compares a letter and a
/// combining mark after it as one (`"Café".ToLower().IndexOf("cafe")`
/// is -1, the ordinal 0). `==`, `Equals` and `Contains` were ordinal
/// already. `ToLower()`,
/// `ToUpper()`, `ToLowerInvariant()` and `ToUpperInvariant()` without a
/// culture argument; `==`/`!=` against the literal either way round, and
/// `Equals`, `StartsWith`, `EndsWith`, `Contains`, `IndexOf` and
/// `LastIndexOf` with the literal as their one argument, where the string's
/// `StringComparison` overload of that method resolves (`Contains` from .NET
/// Core 2.1). The instance `Equals` keeps the NullReferenceException a null
/// `x` threw from `ToLower()`, where `string.Equals(x, …)` would answer
/// false. Not in an expression tree: a LINQ provider translates `ToLower`, not
/// the comparison overloads. Yields to CA1862.
module CSharp.Refactor.CaseCompare

open System
open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax

[<Literal>]
let Code = "CR0188"

let private lowerings = set [ "ToLower"; "ToLowerInvariant" ]
let private upperings = set [ "ToUpper"; "ToUpperInvariant" ]

let private comparers =
    set [ "Equals"; "StartsWith"; "EndsWith"; "Contains"; "IndexOf"; "LastIndexOf" ]

/// `x.ToLower()` on a string, no culture argument: the receiver `x` and
/// whether it lowers.
let private caseFolded (model: SemanticModel) (e: ExpressionSyntax) =
    match e with
    | :? InvocationExpressionSyntax as inv when inv.ArgumentList.Arguments.Count = 0 ->
        match inv.Expression with
        | :? MemberAccessExpressionSyntax as ma when ma.IsKind SyntaxKind.SimpleMemberAccessExpression ->
            let name = ma.Name.Identifier.ValueText

            if lowerings.Contains name || upperings.Contains name then
                match model.GetSymbolInfo(inv).Symbol with
                | :? IMethodSymbol as m when m.ContainingType.SpecialType = SpecialType.System_String ->
                    Some(ma.Expression, lowerings.Contains name)
                | _ -> None
            else
                None
        | _ -> None
    | _ -> None

/// A string literal pure ASCII and already in the fold's direction.
let private agreeingLiteral (model: SemanticModel) (lowers: bool) (e: ExpressionSyntax) =
    match model.GetConstantValue e with
    | v when v.HasValue ->
        match v.Value with
        // ASCII holds no letter of the other case: the fold would leave it as it is
        | :? string as s when
            s
            |> Seq.forall (fun c -> int c < 128 && (if lowers then c < 'A' || c > 'Z' else c < 'a' || c > 'z'))
            ->
            true
        | _ -> false
    | _ -> false

/// Does `string` have `name(string, StringComparison)`?
let private comparisonOverload (model: SemanticModel) (name: string) =
    let str = model.Compilation.GetSpecialType SpecialType.System_String

    str.GetMembers name
    |> Seq.exists (fun m ->
        match m with
        | :? IMethodSymbol as md when not md.IsStatic && md.Parameters.Length = 2 ->
            md.Parameters.[0].Type.SpecialType = SpecialType.System_String
            && md.Parameters.[1].Type.Name = "StringComparison"
        | _ -> false)

let analyze (tree: SyntaxTree) (model: SemanticModel) (ctx: RuleContext) : Suggestion list =
    let comparison (at: int) =
        if Usings.imported model at "System" "StringComparison" then
            "StringComparison.OrdinalIgnoreCase"
        else
            "System.StringComparison.OrdinalIgnoreCase"

    let offer (node: ExpressionSyntax) (replacement: string) =
        let edit = Suggestion.replace node.Span replacement

        if Guards.speculativeCheck model [ edit ] then
            Some
                {
                    Code = Code
                    Message =
                        "Lowering a copy to compare it allocates, and follows the culture's case rules: compare ignoring case instead"
                    Span = node.Span
                    Fixes = [ Suggestion.fix "Compare ignoring case" Code [ edit ] ]
                }
        else
            None

    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun n ->
        match n with
        | :? BinaryExpressionSyntax as b when
            (b.IsKind SyntaxKind.EqualsExpression || b.IsKind SyntaxKind.NotEqualsExpression)
            && not (Text.insideExpressionTree model b)
            ->
            let folded, literal =
                match caseFolded model b.Left, caseFolded model b.Right with
                | Some f, None -> Some f, b.Right
                | None, Some f -> Some f, b.Left
                | _ -> None, b.Right

            match folded with
            | Some(receiver, lowers) when agreeingLiteral model lowers literal ->
                let call = $"{Text.asReceiver receiver}.Equals({literal}, {comparison b.SpanStart})"

                offer
                    b
                    (if b.IsKind SyntaxKind.NotEqualsExpression then
                         "!" + call
                     else
                         call)
            | _ -> None
        | :? InvocationExpressionSyntax as inv when
            inv.ArgumentList.Arguments.Count = 1
            && isNull inv.ArgumentList.Arguments.[0].NameColon
            && not (Text.insideExpressionTree model inv)
            ->
            match inv.Expression with
            | :? MemberAccessExpressionSyntax as ma when comparers.Contains ma.Name.Identifier.ValueText ->
                let name = ma.Name.Identifier.ValueText
                let argument = inv.ArgumentList.Arguments.[0].Expression

                match caseFolded model ma.Expression with
                | Some(receiver, lowers) when
                    agreeingLiteral model lowers argument
                    && comparisonOverload model name
                    // the one-string overload is what binds today
                    && (match model.GetSymbolInfo(inv).Symbol with
                        | :? IMethodSymbol as m ->
                            m.Parameters.Length = 1
                            && m.Parameters.[0].Type.SpecialType = SpecialType.System_String
                        | _ -> false)
                    ->
                    offer inv $"{Text.asReceiver receiver}.{name}({argument}, {comparison inv.SpanStart})"
                | _ -> None
            | _ -> None
        | _ -> None)
    |> List.ofSeq
