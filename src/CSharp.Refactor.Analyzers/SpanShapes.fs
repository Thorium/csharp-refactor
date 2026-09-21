/// Copies of a string the consumer reads once. Three rules, the F# side's
/// FR0106, FR0166 and FR0167.
///
/// CR0174 (performance, fix): `Substring` — or a C# 8 range, `s[6..]`,
/// `s[6..11]` — handed to a consumer whose `ReadOnlySpan<char>` overload
/// means the same is `AsSpan`, the same characters without the copy:
///
///     int.Parse(s.Substring(6, 5))     →  int.Parse(s.AsSpan(6, 5))
///     sb.Append(s.Substring(6))        →  sb.Append(s.AsSpan(6))
///     writer.Write(s[6..])             →  writer.Write(s.AsSpan(6))
///     int.Parse(s[6..11])              →  int.Parse(s.AsSpan()[6..11])
///
/// The consumers: the BCL's `Parse`/`TryParse` (a `System` type: the span
/// overload takes the same culture and style; a user type's two are its
/// author's to keep alike), `StringBuilder.Append`, `TextWriter.Write`
/// and `WriteLine` (and a writer derived from it), `string.Concat` — a
/// one-identifier swap with no culture question. `StartsWith`, `Equals`,
/// `IndexOf` and their kind stay out: the string overloads are
/// culture-sensitive and the span twins ordinal, so the swap would change
/// the comparison. Guards: the receiver is a `string`; the copy is
/// directly the argument; the consumer's own type declares the overload
/// with `ReadOnlySpan<char>` at that position and the other parameters
/// unchanged; `AsSpan` resolves (`using System;` added); the speculative
/// re-bind settles the overload.
///
/// CR0175 (performance, fix): a prefix or suffix cut out only to be
/// compared with a literal is a `StartsWith`/`EndsWith` that cuts nothing:
///
///     s.Substring(0, 6) == "ORDER-"    →  s.StartsWith("ORDER-", StringComparison.Ordinal)
///     s[..6] == "ORDER-"               →  the same
///     s.Substring(s.Length - 3) != "MED"   →  !s.EndsWith("MED", StringComparison.Ordinal)
///     s[^3..] == "MED"                 →  s.EndsWith("MED", StringComparison.Ordinal)
///
/// C#'s `==` on strings is ordinal, so `StringComparison.Ordinal` is the
/// same comparison spelled out — the culture-sensitive `StartsWith(string)`
/// is exactly what the rewrite must not emit. The literal's length must
/// equal the cut's (`s.Substring(0, 3) == "ab"` can never hold: a bug, not
/// this rule's). A `Substring` or a range on a string shorter than the cut
/// throws where `StartsWith` answers false, so the site must sit under a
/// length guard on the same string: a conjunct before it (`s.Length >= 6
/// && …`), a disjunct before a `!=` (`s.Length < 6 || …`), or the
/// condition of the enclosing `if` whose then-branch holds it — with the
/// string not assigned inside that `if`; the receiver a name or a chain
/// of names, since a call may answer the guard and the cut differently.
/// An unguarded site is offered in the editor only, never by a sweep.
///
/// CR0176 (performance, fix): `ToCharArray()` copies the whole string
/// into an array a `foreach` then reads once — a string already
/// enumerates its characters:
///
///     foreach (var c in s.ToCharArray())   →  foreach (var c in s)
///
/// The `foreach` over a string compiles to an indexed loop, faster than
/// the array's copy and walk (measured in PerfClaims). A LINQ consumer
/// (`s.ToCharArray().Any(…)`) stays: over a string, LINQ walks a boxed
/// `CharEnumerator`, measured slower than the array it would save. Guards:
/// the argument-free `ToCharArray()` (the two-argument form slices), on a
/// `string`, directly the loop's source.
module CSharp.Refactor.SpanShapes

open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax
open Microsoft.CodeAnalysis.Text

[<Literal>]
let SpanCode = "CR0174"

[<Literal>]
let PrefixCode = "CR0175"

[<Literal>]
let CharArrayCode = "CR0176"

let private isString (model: SemanticModel) (e: ExpressionSyntax) =
    match model.GetTypeInfo(e).Type with
    | null -> false
    | t -> t.SpecialType = SpecialType.System_String

/// A copy of part of a string: `s.Substring(a)`, `s.Substring(a, b)`, `s[r]`
/// with a range. The receiver, and the `AsSpan` spelling of the same part.
type private Cut =
    {
        Receiver: ExpressionSyntax
        /// `AsSpan(6, 5)`, `AsSpan(6)`, `AsSpan()[6..11]`
        SpanCall: string
        /// The prefix length, when the cut is `Substring(0, n)` / `[..n]` / `[0..n]`.
        PrefixLength: int option
        /// The suffix length, when the cut is `Substring(s.Length - n)` / `[^n..]`.
        SuffixLength: int option
    }

let private intLiteral (e: ExpressionSyntax) =
    match e with
    | :? LiteralExpressionSyntax as l when l.IsKind SyntaxKind.NumericLiteralExpression ->
        match l.Token.Value with
        | :? int as n -> Some n
        | _ -> None
    | _ -> None

/// `s.Length - n` on the receiver, with `n` a literal.
let private lengthMinus (receiver: ExpressionSyntax) (e: ExpressionSyntax) =
    match e with
    | :? BinaryExpressionSyntax as b when b.IsKind SyntaxKind.SubtractExpression ->
        match b.Left with
        | :? MemberAccessExpressionSyntax as ma when
            ma.Name.Identifier.ValueText = "Length"
            && ma.Expression.ToString() = receiver.ToString()
            ->
            intLiteral b.Right
        | _ -> None
    | _ -> None

let private cutOf (model: SemanticModel) (e: ExpressionSyntax) : Cut option =
    match e with
    | :? InvocationExpressionSyntax as inv ->
        match inv.Expression with
        | :? MemberAccessExpressionSyntax as ma when
            ma.Name.Identifier.ValueText = "Substring"
            && isString model ma.Expression
            && (match model.GetSymbolInfo(inv).Symbol with
                | :? IMethodSymbol as m -> m.ContainingType.SpecialType = SpecialType.System_String
                | _ -> false)
            && inv.ArgumentList.Arguments |> Seq.forall (fun a -> isNull a.NameColon)
            ->
            let args = inv.ArgumentList.Arguments

            match args.Count with
            | 1 ->
                Some
                    {
                        Receiver = ma.Expression
                        SpanCall = $"AsSpan({args.[0].Expression})"
                        PrefixLength = None
                        SuffixLength = lengthMinus ma.Expression args.[0].Expression
                    }
            | 2 ->
                Some
                    {
                        Receiver = ma.Expression
                        SpanCall = $"AsSpan({args.[0].Expression}, {args.[1].Expression})"
                        PrefixLength =
                            (match intLiteral args.[0].Expression, intLiteral args.[1].Expression with
                             | Some 0, Some n -> Some n
                             | _ -> None)
                        SuffixLength = None
                    }
            | _ -> None
        | _ -> None
    | :? ElementAccessExpressionSyntax as ea when
        isString model ea.Expression
        && ea.ArgumentList.Arguments.Count = 1
        && (ea.ArgumentList.Arguments.[0].Expression :? RangeExpressionSyntax)
        ->
        let range = ea.ArgumentList.Arguments.[0].Expression :?> RangeExpressionSyntax

        let fromStart =
            match range.LeftOperand with
            | null -> Some 0
            | l -> intLiteral l

        let suffix =
            match range.LeftOperand, range.RightOperand with
            | (:? PrefixUnaryExpressionSyntax as hat), null when hat.IsKind SyntaxKind.IndexExpression ->
                intLiteral hat.Operand
            | l, null when not (isNull l) -> lengthMinus ea.Expression l
            | _ -> None

        Some
            {
                Receiver = ea.Expression
                SpanCall =
                    (match range.LeftOperand, range.RightOperand with
                     | l, null when not (isNull l || l :? PrefixUnaryExpressionSyntax) -> $"AsSpan({l})"
                     | _ -> $"AsSpan(){ea.ArgumentList}")
                PrefixLength =
                    (match fromStart, range.RightOperand with
                     | Some 0, (:? LiteralExpressionSyntax as r) -> intLiteral r
                     | _ -> None)
                SuffixLength = suffix
            }
    | _ -> None

// ---- CR0174 ----

/// The consumers whose span overload is the same operation.
let private spanConsumer (m: IMethodSymbol) =
    let owner = m.ContainingType

    let rec derivesFrom (t: INamedTypeSymbol) (name: string) =
        not (isNull t) && (t.ToDisplayString() = name || derivesFrom t.BaseType name)

    match m.Name with
    | "Parse"
    | "TryParse" ->
        // the BCL's own: a user type's two overloads are its author's to keep alike
        m.IsStatic
        && (let ns = owner.ContainingNamespace.ToDisplayString() in ns = "System" || ns = "System.Numerics")
    | "Append" -> owner.ToDisplayString() = "System.Text.StringBuilder"
    | "Write"
    | "WriteLine" -> derivesFrom owner "System.IO.TextWriter"
    | "Concat" -> owner.SpecialType = SpecialType.System_String
    | _ -> false

let private readOnlySpanOfChar (t: ITypeSymbol) =
    match t with
    | :? INamedTypeSymbol as n ->
        n.OriginalDefinition.ToDisplayString() = "System.ReadOnlySpan<T>"
        && n.TypeArguments.Length = 1
        && n.TypeArguments.[0].SpecialType = SpecialType.System_Char
    | _ -> false

/// Does the consumer's type declare the overload with `ReadOnlySpan<char>`
/// at the position, the other parameters as they are?
let private hasSpanOverload (m: IMethodSymbol) (position: int) =
    m.ContainingType.GetMembers m.Name
    |> Seq.exists (fun member' ->
        match member' with
        | :? IMethodSymbol as o when
            // `int.Parse(ReadOnlySpan<char>, NumberStyles = …, IFormatProvider = null)`:
            // the span twin may carry optional parameters the string one spells out
            o.Parameters.Length >= m.Parameters.Length
            && o.IsStatic = m.IsStatic
            && not (SymbolEqualityComparer.Default.Equals(o, m))
            ->
            o.Parameters
            |> Seq.mapi (fun i p -> i, p)
            |> Seq.forall (fun (i, p) ->
                if i = position then
                    readOnlySpanOfChar p.Type
                elif i >= m.Parameters.Length then
                    p.IsOptional
                else
                    // `string.Concat(ReadOnlySpan<char>, ReadOnlySpan<char>)`: a string
                    // argument converts to the span implicitly, the same characters
                    (SymbolEqualityComparer.Default.Equals(p.Type, m.Parameters.[i].Type)
                     || (m.Parameters.[i].Type.SpecialType = SpecialType.System_String
                         && readOnlySpanOfChar p.Type))
                    && p.RefKind = m.Parameters.[i].RefKind)
        | _ -> false)

let private asSpanResolves (compilation: Compilation) =
    not (isNull (compilation.GetTypeByMetadataName "System.MemoryExtensions"))

let private spans (tree: SyntaxTree) (model: SemanticModel) : Suggestion list =
    if not (asSpanResolves model.Compilation) then
        []
    else
        tree.GetRoot().DescendantNodes()
        |> Seq.choose (fun node ->
            match node with
            | :? ArgumentSyntax as arg when
                arg.RefKindKeyword.IsKind SyntaxKind.None
                && isNull arg.NameColon
                && (arg.Parent.Parent :? InvocationExpressionSyntax)
                && not (Text.insideExpressionTree model arg)
                && not (Text.holdsCommentOrDirective arg)
                ->
                let inv = arg.Parent.Parent :?> InvocationExpressionSyntax
                let position = inv.ArgumentList.Arguments.IndexOf arg

                match model.GetSymbolInfo(inv).Symbol, cutOf model arg.Expression with
                | (:? IMethodSymbol as m), Some cut when
                    position >= 0
                    && position < m.Parameters.Length
                    && m.Parameters.[position].Type.SpecialType = SpecialType.System_String
                    && spanConsumer m
                    && hasSpanOverload m position
                    ->
                    let replacement = $"{cut.Receiver}.{cut.SpanCall}"

                    Usings.importEdit model tree arg.SpanStart "System" "MemoryExtensions"
                    |> Option.map (fun usingEdits ->
                        usingEdits @ [ Suggestion.replace arg.Expression.Span replacement ])
                    |> Option.filter (Guards.speculativeCheck model)
                    |> Option.map (fun edits ->
                        {
                            Code = SpanCode
                            Message =
                                "The consumer reads a span: AsSpan hands it the same characters without the copy"
                            Span = arg.Expression.Span
                            Fixes = [ Suggestion.fix "Pass the span" SpanCode edits ]
                        })
                | _ -> None
            | _ -> None)
        |> List.ofSeq

// ---- CR0175 ----

/// `s.Length OP k` on the receiver: the bound it proves, as the least length
/// the string is known to have when the expression is true (`>= k` → k,
/// `> k` → k + 1, `== k` → k), or when false for the disjunct form (`< k` →
/// k, `<= k` → k + 1).
let private lengthBound (receiver: string) (e: ExpressionSyntax) (whenTrue: bool) =
    match e with
    | :? BinaryExpressionSyntax as b ->
        let lengthOf (x: ExpressionSyntax) =
            match x with
            | :? MemberAccessExpressionSyntax as ma when
                ma.Name.Identifier.ValueText = "Length" && ma.Expression.ToString() = receiver
                ->
                true
            | _ -> false

        let kind = b.Kind()

        let bound (k: int) (op: SyntaxKind) =
            match op, whenTrue with
            | SyntaxKind.GreaterThanOrEqualExpression, true
            | SyntaxKind.EqualsExpression, true
            | SyntaxKind.LessThanExpression, false -> Some k
            | SyntaxKind.GreaterThanExpression, true
            | SyntaxKind.LessThanOrEqualExpression, false -> Some(k + 1)
            | _ -> None

        // the mirrored spelling: `6 <= s.Length`
        let mirror (op: SyntaxKind) =
            match op with
            | SyntaxKind.GreaterThanOrEqualExpression -> SyntaxKind.LessThanOrEqualExpression
            | SyntaxKind.LessThanOrEqualExpression -> SyntaxKind.GreaterThanOrEqualExpression
            | SyntaxKind.GreaterThanExpression -> SyntaxKind.LessThanExpression
            | SyntaxKind.LessThanExpression -> SyntaxKind.GreaterThanExpression
            | other -> other

        match lengthOf b.Left, intLiteral b.Right, lengthOf b.Right, intLiteral b.Left with
        | true, Some k, _, _ -> bound k kind
        | _, _, true, Some k -> bound k (mirror kind)
        | _ -> None
    | _ -> None

/// Is the comparison guarded: does the string it cuts have at least `n`
/// characters wherever it is evaluated?
let private guarded (receiver: string) (n: int) (comparison: BinaryExpressionSyntax) =
    let proves (e: ExpressionSyntax) (whenTrue: bool) =
        match lengthBound receiver e whenTrue with
        | Some k -> k >= n
        | None -> false

    // no assignment to the receiver inside the guarding statement
    // no write to the receiver, nor to any prefix of its chain (`p` under a
    // guard on `p.S`), between the guard and the cut: an assignment, a step,
    // a `ref`/`out` argument. (A lambda, `foreach` or pattern cannot rebind
    // the name in C# — CS0136 — so a rebinding is the one threat F# has
    // that this side does not.)
    let prefixes =
        let parts = receiver.Split '.'
        set [ for i in 1 .. parts.Length -> String.concat "." (Array.truncate i parts) ]

    let untouched (scope: SyntaxNode) =
        scope.DescendantNodes()
        |> Seq.forall (fun d ->
            match d with
            | :? AssignmentExpressionSyntax as a -> not (prefixes.Contains(a.Left.ToString()))
            | :? PostfixUnaryExpressionSyntax as u -> not (prefixes.Contains(u.Operand.ToString()))
            | :? PrefixUnaryExpressionSyntax as u -> not (prefixes.Contains(u.Operand.ToString()))
            | :? ArgumentSyntax as arg when not (arg.RefKindKeyword.IsKind SyntaxKind.None) ->
                not (prefixes.Contains(arg.Expression.ToString()))
            | _ -> true)

    // does the expression, known true (or known false), prove the bound: a
    // conjunct of an `&&` when true, a disjunct of an `||` when false
    let rec proveIn (e: ExpressionSyntax) (whenTrue: bool) =
        match e with
        | :? BinaryExpressionSyntax as b when whenTrue && b.IsKind SyntaxKind.LogicalAndExpression ->
            proveIn b.Left true || proveIn b.Right true
        | :? BinaryExpressionSyntax as b when not whenTrue && b.IsKind SyntaxKind.LogicalOrExpression ->
            proveIn b.Left false || proveIn b.Right false
        | :? ParenthesizedExpressionSyntax as p -> proveIn p.Expression whenTrue
        | e -> proves e whenTrue

    // the operand before the comparison in its own chain: evaluated true when
    // an `&&` reaches the right side, false when an `||` does
    let rec before (node: SyntaxNode) =
        match node.Parent with
        | :? BinaryExpressionSyntax as b when b.IsKind SyntaxKind.LogicalAndExpression ->
            (obj.ReferenceEquals(b.Right, node) && proveIn b.Left true) || before b
        | :? BinaryExpressionSyntax as b when b.IsKind SyntaxKind.LogicalOrExpression ->
            (obj.ReferenceEquals(b.Right, node) && proveIn b.Left false) || before b
        | :? ParenthesizedExpressionSyntax as p -> before p
        | _ -> false

    let underIf =
        comparison.Ancestors()
        |> Seq.exists (fun a ->
            match a with
            | :? IfStatementSyntax as ifs ->
                ifs.Statement.Span.Contains comparison.Span
                && proveIn ifs.Condition true
                && untouched ifs
            | :? ConditionalExpressionSyntax as c ->
                c.WhenTrue.Span.Contains comparison.Span && proveIn c.Condition true
            | _ -> false)

    before comparison || underIf

let private prefixes (tree: SyntaxTree) (model: SemanticModel) : Suggestion list =
    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun node ->
        match node with
        | :? BinaryExpressionSyntax as b when
            (b.IsKind SyntaxKind.EqualsExpression || b.IsKind SyntaxKind.NotEqualsExpression)
            && not (Text.holdsCommentOrDirective b)
            && not (Text.insideExpressionTree model b)
            && Guards.isBuiltinOperator model b
            ->
            let literalOf (e: ExpressionSyntax) =
                match e with
                | :? LiteralExpressionSyntax as l when l.IsKind SyntaxKind.StringLiteralExpression ->
                    Some(l.Token.ValueText, l.Token.Text)
                | _ -> None

            let sides =
                match literalOf b.Right, literalOf b.Left with
                | Some lit, _ -> Some(b.Left, lit)
                | _, Some lit -> Some(b.Right, lit)
                | _ -> None

            match sides with
            | Some(cutSide, (value, spelled)) ->
                match cutOf model cutSide with
                | Some cut ->
                    let receiver = cut.Receiver.ToString()

                    // the guard reads the receiver once and the cut reads it again: a
                    // receiver that may answer differently each time (a call, an
                    // indexer) is guarded for one string and cut on another; a name
                    // or a chain of names is what the author already reads twice
                    let rec chainOfNames (e: ExpressionSyntax) =
                        match e with
                        | :? IdentifierNameSyntax
                        | :? ThisExpressionSyntax -> true
                        | :? MemberAccessExpressionSyntax as ma when ma.IsKind SyntaxKind.SimpleMemberAccessExpression ->
                            chainOfNames ma.Expression
                        | _ -> false

                    let stable = chainOfNames cut.Receiver

                    // the call, and whether the site is guarded: a guarded one is applied by
                    // a sweep; an unguarded one is offered in the editor only, where the
                    // author sees whether the string can be short (the F# side does the
                    // same for its `Substring` form)
                    let call =
                        match cut.PrefixLength, cut.SuffixLength with
                        | Some n, _ when stable && n = value.Length -> Some("StartsWith", guarded receiver n b)
                        | _, Some n when stable && n = value.Length -> Some("EndsWith", guarded receiver n b)
                        | _ -> None

                    match call with
                    | Some(name, isGuarded) ->
                        let negated = b.IsKind SyntaxKind.NotEqualsExpression

                        let replacement =
                            (if negated then "!" else "")
                            + $"{receiver}.{name}({spelled}, StringComparison.Ordinal)"

                        Usings.importEdit model tree b.SpanStart "System" "StringComparison"
                        |> Option.map (fun usingEdits -> usingEdits @ [ Suggestion.replace b.Span replacement ])
                        |> Option.filter (Guards.speculativeCheck model)
                        |> Option.map (fun edits ->
                            {
                                Code = PrefixCode
                                Message =
                                    $"The cut is compared with a literal and nothing else: {name} says it without the copy"
                                Span = b.Span
                                Fixes =
                                    [
                                        (if isGuarded then
                                             Suggestion.fix $"Use {name}" PrefixCode edits
                                         else
                                             Suggestion.fix
                                                 $"Use {name} (a string shorter than {value.Length} threw here; it answers false now)"
                                                 PrefixCode
                                                 edits
                                             |> Suggestion.editorOnly)
                                    ]
                            })
                    | None -> None
                | None -> None
            | None -> None
        | _ -> None)
    |> List.ofSeq

// ---- CR0176 ----

let private charArrays (tree: SyntaxTree) (model: SemanticModel) : Suggestion list =
    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun node ->
        match node with
        | :? ForEachStatementSyntax as fe ->
            match fe.Expression with
            | :? InvocationExpressionSyntax as inv when
                inv.ArgumentList.Arguments.Count = 0
                && not (Text.holdsCommentOrDirective inv)
                && (match inv.Expression with
                    | :? MemberAccessExpressionSyntax as ma ->
                        ma.Name.Identifier.ValueText = "ToCharArray" && isString model ma.Expression
                    | _ -> false)
                ->
                let receiver = (inv.Expression :?> MemberAccessExpressionSyntax).Expression
                let edit = Suggestion.replace inv.Span (receiver.ToString())

                if Guards.speculativeCheck model [ edit ] then
                    Some
                        {
                            Code = CharArrayCode
                            Message = "The loop reads the copy once: a string enumerates its characters as it is"
                            Span = inv.Span
                            Fixes = [ Suggestion.fix "Enumerate the string" CharArrayCode [ edit ] ]
                        }
                else
                    None
            | _ -> None
        | _ -> None)
    |> List.ofSeq

let analyze (tree: SyntaxTree) (model: SemanticModel) (_ctx: RuleContext) : Suggestion list =
    spans tree model @ prefixes tree model @ charArrays tree model
