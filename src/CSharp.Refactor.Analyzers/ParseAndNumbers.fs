/// Values that are cheaper, or different, than they look: a parse driven
/// by its exception, floating-point equality, an integer division that
/// lands in a double, a local clock compared with a UTC one.
///
/// CR0166 (performance, fix): `try { v = int.Parse(s); } catch
/// (FormatException) { … }` — the failure path is an exception (a stack
/// walk per bad input) for a question `TryParse` answers with a bool. The
/// fix: `if (!int.TryParse(s, out v)) { … }`, or bare `int.TryParse(s,
/// out v);` for an empty catch, or `return int.TryParse(s, out var parsed)
/// ? parsed : d;` for the return form. Guards: the `try` holds exactly the
/// one parse statement (nothing else is under the catch); the target is a
/// local or a field; one filter-less catch of `FormatException`/
/// `OverflowException`/`ArgumentException`/`Exception`, no `finally`; the
/// catch variable is unread; on the failure path `TryParse` sets the
/// target to default where `Parse` left it — so a local keeps the fix only
/// when it was declared without a value (or with `default`), the catch
/// assigns it, or the catch leaves (`return`/`throw`/`continue`); a field
/// only when the catch assigns it; the `TryParse` overload with the same
/// arguments binds (the speculative check proves it). Another `try` whose
/// catch names `FormatException` around a `Parse` is a note.
///
/// CR0167 (correctness, note): `a == b` on `float`/`double`/`Half`
/// operands, neither a literal or constant: two computations of the
/// same value rarely share a representation. Rounded operands
/// (`Math.Round`/`Floor`/`Ceiling`/`Truncate`) compare exactly and are
/// exempt; so are test files.
///
/// CR0168 (correctness, note; editor fix): `double avg = sum / count;`
/// with two integral operands — the division truncates before the
/// conversion. The typed tree says where the result goes (its converted
/// type), so a declaration, an assignment, a return, an argument and an
/// operand of a floating operator all count. The editor offers a cast on
/// the left operand; a sweep never applies it (the truncation is
/// occasionally meant).
///
/// CR0169 (correctness, note): `DateTime.Now`/`Today` compared with,
/// subtracted from or `CompareTo`'d against `DateTime.UtcNow` — directly,
/// through `.Date`/`.AddX(…)`, or through a local, field or property of
/// this file written once from one of them. The two kinds differ by the
/// machine's offset and the comparison flips with the timezone.
/// `ToUniversalTime`/`ToLocalTime`/`SpecifyKind` make the kind explicit
/// and stand the rule down.
module CSharp.Refactor.ParseAndNumbers

open System.Collections.Generic
open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax
open Microsoft.CodeAnalysis.Text

[<Literal>]
let TryParseCode = "CR0166"

[<Literal>]
let FloatEqualityCode = "CR0167"

[<Literal>]
let IntegerDivisionCode = "CR0168"

[<Literal>]
let MixedKindCode = "CR0169"

let private symbolOf (model: SemanticModel) (e: SyntaxNode) =
    let info = model.GetSymbolInfo e

    if isNull info.Symbol then
        match info.CandidateSymbols |> Seq.tryHead with
        | Some s -> s
        | None -> null
    else
        info.Symbol

let private sameSymbol (a: ISymbol) (b: ISymbol) =
    SymbolEqualityComparer.Default.Equals(a, b)

// ---- CR0166 ----

let private parseOwners =
    set
        [
            "System.Int32"
            "System.Int64"
            "System.Int16"
            "System.Byte"
            "System.SByte"
            "System.UInt32"
            "System.UInt64"
            "System.UInt16"
            "System.Single"
            "System.Double"
            "System.Decimal"
            "System.Boolean"
            "System.Char"
            "System.Guid"
            "System.DateTime"
            "System.DateTimeOffset"
            "System.TimeSpan"
        ]

/// The catch types a parse's failures fall under. `Exception` and
/// `SystemException` cover every failure; `FormatException` alone leaves
/// an overflow (the numeric parses, `TimeSpan`) and a null input
/// (`ArgumentNullException`) to propagate, where `TryParse` would answer
/// false — so it carries the fix only for a parse that cannot overflow and
/// an argument the flow analysis proves non-null. `ArgumentException`
/// never caught a format error at all: a note.
let private caughtTypes =
    set
        [
            "System.FormatException"
            "System.OverflowException"
            "System.ArgumentException"
            "System.ArgumentNullException"
            "System.Exception"
            "System.SystemException"
        ]

/// The parses whose only failure (beyond a null input) is a `FormatException`.
let private noOverflow =
    set [ "Boolean"; "Char"; "Guid"; "DateTime"; "DateTimeOffset"; "Enum" ]

/// Does the catch cover every failure the `TryParse` twin would turn into
/// `false`? Otherwise the rewrite swallows what used to propagate.
let private catchCovers (model: SemanticModel) (caughtType: string) (parse: InvocationExpressionSyntax) =
    match caughtType with
    | "System.Exception"
    | "System.SystemException" -> true
    | "System.FormatException" ->
        let owner =
            match model.GetSymbolInfo(parse).Symbol with
            | :? IMethodSymbol as m -> m.ContainingType.Name
            | _ -> ""

        let argument = parse.ArgumentList.Arguments.[0].Expression

        let nonNull =
            (model.GetNullableContext argument.SpanStart).HasFlag NullableContext.AnnotationsEnabled
            && model.GetTypeInfo(argument).Nullability.FlowState = NullableFlowState.NotNull

        noOverflow.Contains owner && nonNull
    | _ -> false

/// A `T.Parse(args)` / `Enum.Parse<E>(args)` call whose `TryParse` twin
/// takes the same arguments: the callee text and the argument text.
let private parseCall
    (model: SemanticModel)
    (e: ExpressionSyntax)
    : (InvocationExpressionSyntax * string * string) option =
    match e with
    | :? InvocationExpressionSyntax as inv ->
        match inv.Expression, symbolOf model inv with
        | (:? MemberAccessExpressionSyntax as ma), (:? IMethodSymbol as m) when m.IsStatic && m.Name = "Parse" ->
            let owner =
                m.ContainingType.ContainingNamespace.ToDisplayString()
                + "."
                + m.ContainingType.Name

            let args = inv.ArgumentList.Arguments |> List.ofSeq

            let plain =
                args
                |> List.forall (fun a -> isNull a.NameColon && a.RefKindKeyword.IsKind SyntaxKind.None)

            if not plain || args.IsEmpty then
                None
            elif parseOwners.Contains owner then
                Some(inv, ma.Expression.ToString(), inv.ArgumentList.Arguments.ToString())
            elif owner = "System.Enum" && m.IsGenericMethod && m.TypeArguments.Length = 1 then
                match ma.Name with
                | :? GenericNameSyntax as g ->
                    Some(inv, $"{ma.Expression}.TryParse{g.TypeArgumentList}", inv.ArgumentList.Arguments.ToString())
                | _ -> None
            else
                None
        | _ -> None
    | _ -> None

let private leaves (block: BlockSyntax) =
    match block.Statements |> Seq.tryLast with
    | Some(:? ReturnStatementSyntax)
    | Some(:? ThrowStatementSyntax)
    | Some(:? ContinueStatementSyntax) -> true
    | _ -> false

let private tryParses (tree: SyntaxTree) (model: SemanticModel) : Suggestion list =
    let text = tree.GetText()

    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun n ->
        match n with
        | :? TryStatementSyntax as t when t.Catches.Count = 1 && isNull t.Finally ->
            let c = t.Catches.[0]

            let caughtType =
                if isNull c.Declaration then
                    "System.Exception"
                else
                    match model.GetTypeInfo(c.Declaration.Type).Type with
                    | null -> ""
                    | ct -> ct.ToDisplayString()

            let binderRead =
                not (isNull c.Declaration)
                && not (c.Declaration.Identifier.IsKind SyntaxKind.None)
                && Text.mentionsName c.Declaration.Identifier.ValueText c.Block

            let parseInside =
                t.Block.DescendantNodes()
                |> Seq.exists (fun d ->
                    match d with
                    | :? InvocationExpressionSyntax as inv -> (parseCall model inv).IsSome
                    | _ -> false)

            let note () =
                if caughtType = "System.FormatException" && parseInside then
                    Some(
                        Suggestion.note
                            TryParseCode
                            "A parse driven by its exception: every bad input pays a throw — TryParse answers the same question with a bool"
                            t.TryKeyword.Span
                    )
                else
                    None

            if (not (isNull c.Filter && caughtTypes.Contains caughtType)) || binderRead then
                note ()
            elif t.Block.Statements.Count <> 1 then
                note ()
            else
                let tryText (replacement: string) = Suggestion.replace t.Span replacement

                let callText (callee: string) (args: string) (target: string) =
                    // `T.Parse(…)` → `T.TryParse(…, out target)`; the Enum form already carries TryParse
                    if callee.Contains ".TryParse" then
                        $"{callee}({args}, out {target})"
                    else
                        $"{callee}.TryParse({args}, out {target})"

                let catchText = c.Block.ToString()
                let indent = Text.leadingWhitespace text t.SpanStart
                let nl = Text.newlineAt text t.SpanStart

                let emptyCatch = c.Block.Statements.Count = 0

                match t.Block.Statements.[0] with
                | :? ExpressionStatementSyntax as es ->
                    match es.Expression with
                    | :? AssignmentExpressionSyntax as a when a.IsKind SyntaxKind.SimpleAssignmentExpression ->
                        match parseCall model a.Right with
                        | None -> note ()
                        | Some(parse, _, _) when not (catchCovers model caughtType parse) -> note ()
                        | Some(_, callee, args) ->
                            let target = a.Left
                            let targetText = target.ToString()

                            let defaultSafe =
                                match symbolOf model target with
                                | :? ILocalSymbol as l ->
                                    let declaredBare =
                                        match l.DeclaringSyntaxReferences |> Seq.tryHead with
                                        | Some r ->
                                            match r.GetSyntax() with
                                            | :? VariableDeclaratorSyntax as d ->
                                                isNull d.Initializer
                                                || d.Initializer.Value.IsKind SyntaxKind.DefaultLiteralExpression
                                                || d.Initializer.Value.IsKind SyntaxKind.DefaultExpression
                                                || (match model.GetConstantValue d.Initializer.Value with
                                                    | v when v.HasValue ->
                                                        (match v.Value with
                                                         | :? int as i -> i = 0
                                                         | :? bool as b -> not b
                                                         | null -> true
                                                         | _ -> false)
                                                    | _ -> false)
                                            | _ -> false
                                        | None -> false

                                    declaredBare || Text.assignsTo targetText c.Block || leaves c.Block
                                | :? IFieldSymbol as f when not f.IsReadOnly -> Text.assignsTo targetText c.Block
                                | _ -> false

                            if not defaultSafe then
                                note ()
                            else
                                let call = callText callee args targetText

                                let replacement =
                                    if emptyCatch then
                                        $"{call};"
                                    elif Text.multiLine text c.Block then
                                        $"if (!{call}){nl}{indent}{catchText}"
                                    else
                                        // a one-line catch stays one line: `if (!…) { v = -1; }`
                                        $"if (!{call}) {catchText}"

                                Some(
                                    {
                                        Code = TryParseCode
                                        Message =
                                            $"'{targetText}' is parsed through an exception: every bad input pays a throw — TryParse answers with a bool"
                                        Span = t.TryKeyword.Span
                                        Fixes = [ Suggestion.fix "Use TryParse" TryParseCode [ tryText replacement ] ]
                                    }
                                    |> Guards.checked model
                                )
                    | _ -> note ()
                | :? ReturnStatementSyntax as r when not (isNull r.Expression) ->
                    match parseCall model r.Expression with
                    | None -> note ()
                    | Some(parse, _, _) when not (catchCovers model caughtType parse) -> note ()
                    | Some(_, callee, args) ->
                        match c.Block.Statements |> List.ofSeq with
                        | [ :? ReturnStatementSyntax as fallback ] when
                            not (isNull fallback.Expression)
                            && Guards.isPureExpression model fallback.Expression
                            ->
                            let scope = Text.enclosingMember t

                            let name =
                                Seq.append [ "parsed" ] (Seq.initInfinite (fun i -> $"parsed{i + 1}"))
                                |> Seq.find (fun candidate -> not (Text.mentionsName candidate scope))

                            let call = callText callee args $"var {name}"

                            let replacement = $"return {call} ? {name} : {fallback.Expression};"

                            Some(
                                {
                                    Code = TryParseCode
                                    Message =
                                        "The value is parsed through an exception: every bad input pays a throw — TryParse answers with a bool"
                                    Span = t.TryKeyword.Span
                                    Fixes = [ Suggestion.fix "Use TryParse" TryParseCode [ tryText replacement ] ]
                                }
                                |> Guards.checked model
                            )
                        | _ -> note ()
                | _ -> note ()
        | _ -> None)
    |> List.ofSeq

// ---- CR0167 ----

let private isFloating (t: ITypeSymbol) =
    if isNull t then
        false
    else
        let t =
            if t.OriginalDefinition.SpecialType = SpecialType.System_Nullable_T then
                (t :?> INamedTypeSymbol).TypeArguments.[0]
            else
                t

        t.SpecialType = SpecialType.System_Single
        || t.SpecialType = SpecialType.System_Double
        || (t.Name = "Half" && t.ContainingNamespace.ToDisplayString() = "System")

let private roundingCalls = set [ "Round"; "Floor"; "Ceiling"; "Truncate" ]

[<TailCall>]
let rec private exactOperand (model: SemanticModel) (e: ExpressionSyntax) =
    match e with
    | :? ParenthesizedExpressionSyntax as p -> exactOperand model p.Expression
    | :? LiteralExpressionSyntax -> true
    | :? DefaultExpressionSyntax -> true
    | :? InvocationExpressionSyntax as inv ->
        match inv.Expression with
        | :? MemberAccessExpressionSyntax as ma when roundingCalls.Contains ma.Name.Identifier.ValueText ->
            match symbolOf model inv with
            | :? IMethodSymbol as m ->
                let owner = m.ContainingType.ToDisplayString()
                owner = "System.Math" || owner = "System.MathF"
            | _ -> false
        | _ -> false
    | _ -> model.GetConstantValue(e).HasValue

/// Is a floating operand computed here — arithmetic, a call, a conversion —
/// rather than a stored value read back? Two stored copies of one value
/// compare exactly (a tie test after `>=`, a de-duplication); two
/// computations of it rarely do.
[<TailCall>]
let rec private computed (e: ExpressionSyntax) =
    match e with
    | :? ParenthesizedExpressionSyntax as p -> computed p.Expression
    | :? BinaryExpressionSyntax as b ->
        b.IsKind SyntaxKind.AddExpression
        || b.IsKind SyntaxKind.SubtractExpression
        || b.IsKind SyntaxKind.MultiplyExpression
        || b.IsKind SyntaxKind.DivideExpression
        || b.IsKind SyntaxKind.ModuloExpression
    | :? InvocationExpressionSyntax
    | :? CastExpressionSyntax -> true
    | :? PrefixUnaryExpressionSyntax as u -> computed u.Operand
    | _ -> false

let private floatEqualities (tree: SyntaxTree) (model: SemanticModel) : Suggestion list =
    if Text.isTestFile tree then
        []
    else
        tree.GetRoot().DescendantNodes()
        |> Seq.choose (fun n ->
            match n with
            | :? BinaryExpressionSyntax as b when
                (b.IsKind SyntaxKind.EqualsExpression || b.IsKind SyntaxKind.NotEqualsExpression)
                && isFloating (model.GetTypeInfo(b.Left).Type)
                && isFloating (model.GetTypeInfo(b.Right).Type)
                && not (exactOperand model b.Left)
                && not (exactOperand model b.Right)
                && (computed b.Left || computed b.Right)
                ->
                Some(
                    Suggestion.note
                        FloatEqualityCode
                        "Floating-point equality: two computations of the same value rarely share a representation — compare Math.Abs(a - b) against a tolerance, or use decimal where the values are exact"
                        b.Span
                )
            | _ -> None)
        |> List.ofSeq

// ---- CR0168 ----

let private isIntegral (t: ITypeSymbol) =
    not (isNull t)
    && (match t.SpecialType with
        | SpecialType.System_Int32
        | SpecialType.System_Int64
        | SpecialType.System_Int16
        | SpecialType.System_Byte
        | SpecialType.System_SByte
        | SpecialType.System_UInt32
        | SpecialType.System_UInt64
        | SpecialType.System_UInt16
        | SpecialType.System_Char -> true
        | _ -> false)

let private floatingTarget (t: ITypeSymbol) =
    not (isNull t)
    && (match t.SpecialType with
        | SpecialType.System_Single
        | SpecialType.System_Double
        | SpecialType.System_Decimal -> true
        | _ -> t.Name = "Half" && t.ContainingNamespace.ToDisplayString() = "System")

let private keyword (t: ITypeSymbol) =
    match t.SpecialType with
    | SpecialType.System_Single -> "float"
    | SpecialType.System_Double -> "double"
    | SpecialType.System_Decimal -> "decimal"
    | _ -> t.Name

let private integerDivisions (tree: SyntaxTree) (model: SemanticModel) : Suggestion list =
    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun n ->
        match n with
        | :? BinaryExpressionSyntax as b when b.IsKind SyntaxKind.DivideExpression ->
            let info = model.GetTypeInfo b

            let isOne (e: ExpressionSyntax) =
                match model.GetConstantValue e with
                | v when v.HasValue ->
                    (match v.Value with
                     | :? int as i -> i = 1
                     | :? int64 as i -> i = 1L
                     | _ -> false)
                | _ -> false

            let flooredAnyway =
                match b.Parent with
                | :? ParenthesizedExpressionSyntax as p ->
                    match p.Parent with
                    | :? CastExpressionSyntax -> true
                    | _ -> false
                | :? CastExpressionSyntax -> true
                | :? ArgumentSyntax as a ->
                    match a.Parent.Parent with
                    | :? InvocationExpressionSyntax as inv ->
                        match inv.Expression with
                        | :? MemberAccessExpressionSyntax as ma -> roundingCalls.Contains ma.Name.Identifier.ValueText
                        | _ -> false
                    | _ -> false
                | _ -> false

            if
                isIntegral info.Type
                && isIntegral (model.GetTypeInfo(b.Left).Type)
                && isIntegral (model.GetTypeInfo(b.Right).Type)
                && floatingTarget info.ConvertedType
                && not (isOne b.Left || isOne b.Right)
                && not (model.GetConstantValue(b).HasValue)
                && not flooredAnyway
            then
                let target = keyword info.ConvertedType

                let leftText =
                    match b.Left with
                    | :? BinaryExpressionSyntax
                    | :? ConditionalExpressionSyntax -> $"({target})({b.Left})"
                    | _ -> $"({target}){b.Left}"

                Some
                    {
                        Code = IntegerDivisionCode
                        Message =
                            $"'{b}' divides two integers and the whole part lands in a {target}: the fraction is gone before the conversion — cast an operand first (`{leftText} / {b.Right}`) if the fraction was meant"
                        Span = b.Span
                        Fixes =
                            [
                                Suggestion.fix
                                    $"Cast the left operand to {target}"
                                    IntegerDivisionCode
                                    [ Suggestion.replace b.Left.Span leftText ]
                                |> Suggestion.editorOnly
                            ]
                    }
            else
                None
        | _ -> None)
    |> List.ofSeq

// ---- CR0169 ----

type private Kind =
    | Local
    | Utc

let private isDateTime (t: ITypeSymbol) =
    not (isNull t)
    && (t.SpecialType = SpecialType.System_DateTime
        || (t.OriginalDefinition.SpecialType = SpecialType.System_Nullable_T
            && (t :?> INamedTypeSymbol).TypeArguments.[0].SpecialType = SpecialType.System_DateTime))

let private explicitKind = set [ "ToUniversalTime"; "ToLocalTime"; "SpecifyKind" ]

let private mixedKinds (tree: SyntaxTree) (model: SemanticModel) : Suggestion list =
    let root = tree.GetRoot()
    let cache = Dictionary<ISymbol, Kind option>(SymbolEqualityComparer.Default)

    // every write of a symbol in this file: initialisers and assignments
    let writesOf (s: ISymbol) : ExpressionSyntax list =
        root.DescendantNodes()
        |> Seq.choose (fun n ->
            match n with
            | :? VariableDeclaratorSyntax as d when not (isNull d.Initializer) ->
                match model.GetDeclaredSymbol d with
                | null -> None
                | ds when sameSymbol ds s -> Some d.Initializer.Value
                | _ -> None
            | :? PropertyDeclarationSyntax as p when not (isNull p.Initializer) ->
                match model.GetDeclaredSymbol p with
                | null -> None
                | ps when sameSymbol ps s -> Some p.Initializer.Value
                | _ -> None
            | :? AssignmentExpressionSyntax as a when a.IsKind SyntaxKind.SimpleAssignmentExpression ->
                match a.Left with
                | :? IdentifierNameSyntax
                | :? MemberAccessExpressionSyntax ->
                    if sameSymbol (symbolOf model a.Left) s then
                        Some a.Right
                    else
                        None
                | _ -> None
            | _ -> None)
        |> List.ofSeq

    let rec kindOf (depth: int) (e: ExpressionSyntax) : Kind option =
        if depth > 3 then
            None
        else
            match e with
            | :? ParenthesizedExpressionSyntax as p -> kindOf depth p.Expression
            | :? MemberAccessExpressionSyntax as ma ->
                let name = ma.Name.Identifier.ValueText

                match symbolOf model ma with
                | :? IPropertySymbol as p when p.IsStatic && p.ContainingType.SpecialType = SpecialType.System_DateTime ->
                    match name with
                    | "Now"
                    | "Today" -> Some Local
                    | "UtcNow" -> Some Utc
                    | _ -> None
                | :? IPropertySymbol as p when
                    not p.IsStatic
                    && p.ContainingType.SpecialType = SpecialType.System_DateTime
                    && name = "Date"
                    ->
                    kindOf depth ma.Expression
                | :? IPropertySymbol as p when
                    not (isNull p.ContainingType)
                    && p.Type.SpecialType = SpecialType.System_DateTime
                    ->
                    tracked (depth + 1) p
                | :? IFieldSymbol as f when f.Type.SpecialType = SpecialType.System_DateTime -> tracked (depth + 1) f
                | _ -> None
            | :? IdentifierNameSyntax as id ->
                match symbolOf model id with
                | :? ILocalSymbol as l when l.Type.SpecialType = SpecialType.System_DateTime -> tracked (depth + 1) l
                | :? IFieldSymbol as f when f.Type.SpecialType = SpecialType.System_DateTime -> tracked (depth + 1) f
                | :? IPropertySymbol as p when p.Type.SpecialType = SpecialType.System_DateTime -> tracked (depth + 1) p
                | _ -> None
            | :? InvocationExpressionSyntax as inv ->
                match inv.Expression with
                | :? MemberAccessExpressionSyntax as ma ->
                    let name = ma.Name.Identifier.ValueText

                    if explicitKind.Contains name then
                        None
                    elif name.StartsWith "Add" || name = "Subtract" then
                        match symbolOf model inv with
                        | :? IMethodSymbol as m when m.ContainingType.SpecialType = SpecialType.System_DateTime ->
                            kindOf depth ma.Expression
                        | _ -> None
                    else
                        None
                | _ -> None
            | _ -> None

    and tracked (depth: int) (s: ISymbol) : Kind option =
        match cache.TryGetValue s with
        | true, k -> k
        | _ ->
            cache.[s] <- None // a cycle reads as unknown

            let k =
                match s.DeclaringSyntaxReferences |> Seq.tryHead with
                | Some r when obj.ReferenceEquals(r.SyntaxTree, tree) ->
                    match writesOf s with
                    | [ single ] -> kindOf depth single
                    | _ -> None
                | _ -> None

            cache.[s] <- k
            k

    let pair (a: ExpressionSyntax) (b: ExpressionSyntax) =
        isDateTime (model.GetTypeInfo(a).Type)
        && isDateTime (model.GetTypeInfo(b).Type)
        && (match kindOf 0 a, kindOf 0 b with
            | Some Local, Some Utc
            | Some Utc, Some Local -> true
            | _ -> false)

    let message =
        "A local time is compared with a UTC time: the two differ by the machine's offset and the result flips with the timezone — use one kind on both sides (DateTime.UtcNow throughout, or ToUniversalTime() on the local one)"

    root.DescendantNodes()
    |> Seq.choose (fun n ->
        match n with
        | :? BinaryExpressionSyntax as b when
            (match b.Kind() with
             | SyntaxKind.EqualsExpression
             | SyntaxKind.NotEqualsExpression
             | SyntaxKind.LessThanExpression
             | SyntaxKind.LessThanOrEqualExpression
             | SyntaxKind.GreaterThanExpression
             | SyntaxKind.GreaterThanOrEqualExpression
             | SyntaxKind.SubtractExpression -> true
             | _ -> false)
            && pair b.Left b.Right
            ->
            Some(Suggestion.note MixedKindCode message b.Span)
        | :? InvocationExpressionSyntax as inv ->
            match inv.Expression with
            | :? MemberAccessExpressionSyntax as ma when
                (ma.Name.Identifier.ValueText = "CompareTo"
                 || ma.Name.Identifier.ValueText = "Equals"
                 || ma.Name.Identifier.ValueText = "Subtract")
                && inv.ArgumentList.Arguments.Count = 1
                && pair ma.Expression inv.ArgumentList.Arguments.[0].Expression
                ->
                Some(Suggestion.note MixedKindCode message inv.Span)
            | :? MemberAccessExpressionSyntax as ma when
                ma.Name.Identifier.ValueText = "Compare"
                && inv.ArgumentList.Arguments.Count = 2
                && pair inv.ArgumentList.Arguments.[0].Expression inv.ArgumentList.Arguments.[1].Expression
                ->
                Some(Suggestion.note MixedKindCode message inv.Span)
            | _ -> None
        | _ -> None)
    |> List.ofSeq

let analyze (tree: SyntaxTree) (model: SemanticModel) (_ctx: RuleContext) : Suggestion list =
    tryParses tree model
    @ floatEqualities tree model
    @ integerDivisions tree model
    @ mixedKinds tree model
