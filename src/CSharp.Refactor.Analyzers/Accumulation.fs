/// Loops that accumulate one value.
///
/// CR0021 (idiom, fix): a running sum, count or string is the aggregate.
///
///     var total = 0m; foreach (var x in xs) total += x.Price;   →  var total = xs.Sum(x => x.Price);
///     int n = 0; foreach (var x in xs) if (p(x)) n++;           →  int n = xs.Count(x => p(x));
///     var s = ""; foreach (var x in xs) s += x;                  →  var s = string.Concat(xs);
///
/// Guards: the accumulator is a local declared with the identity
/// (`0`/`0L`/`0m`/`0.0`/`""`/`string.Empty`) immediately before the loop,
/// assigned only by that one statement, read nowhere in the loop, never
/// reassigned after; the loop is a `foreach` over a real generic
/// `IEnumerable<T>` with a plain loop variable, its body exactly the one
/// statement (for a count, the one `if` without `else` around `n++`);
/// the selector and predicate mention neither the accumulator nor any
/// assignment. Overflow: `Enumerable.Sum` on `int`/`long` throws where
/// `+=` wraps, so an integral sum rewrites only under `CheckOverflow` or
/// in a `checked` block; `double`/`decimal` sums always (`decimal`
/// throws either way, floating never; `float` is absent, `Sum` accumulating
/// it in a double). `Max`/`Min` are absent: the loop
/// leaves its seed on an empty source where `Max()` throws, and a `>`
/// loop and `Max()` disagree on NaN. A string accumulator becomes
/// `string.Concat` (the pieces typed `string`). When `return total;`
/// immediately follows and nothing else reads the accumulator, the three
/// statements collapse to `return xs.Sum();`.
///
/// CR0022 (idiom, fix): a flag set by a loop is `Any`/`All`.
///
///     bool found = false; foreach (var x in xs) if (p(x)) found = true;   →  bool found = xs.Any(x => p(x));
///     bool ok = true;     foreach (var x in xs) if (!p(x)) ok = false;    →  bool ok = xs.All(x => p(x));
///
/// Guards as CR0021's for the flag, the loop and the body (the one `if`
/// without `else`, whose then-branch assigns the initializer's opposite,
/// optionally followed by `break;`); the predicate is pure through
/// `callsOnlyCore` (short-circuiting must not skip an effect) and fits
/// one line. Measured: parity on arrays; the `List<T>` shape, whose
/// `Enumerable.Any` boxes the struct enumerator, goes behind
/// `csharp_refactor.CR0022.lists` if PerfClaims shows the allocation.
///
/// CR0025 (performance, note): growing by one inside a loop —
/// `arr = arr.Append(x).ToArray()`, `arr = arr.Concat(…).ToArray()`,
/// `Array.Resize(ref arr, arr.Length + 1)`, `imm = imm.Add(x)` on an
/// `ImmutableArray<T>`, `s += piece` on a `string` — copies everything each
/// time: O(n²). The note names the builder (`List<T>`,
/// `ImmutableArray.CreateBuilder`, `StringBuilder`, `string.Join`). The
/// exact string shape CR0021 rewrites gets no note; a numeric literal
/// operand (`i = i + 1`) is not a string append, and string-ness is tested
/// first, being the selective test.
module CSharp.Refactor.Accumulation

open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax
open Microsoft.CodeAnalysis.Text

[<Literal>]
let AggregateCode = "CR0021"

[<Literal>]
let FlagCode = "CR0022"

[<Literal>]
let AppendCode = "CR0025"

let private singleStatement (body: StatementSyntax) =
    match body with
    | :? BlockSyntax as b when b.Statements.Count = 1 -> Some b.Statements.[0]
    | :? BlockSyntax -> None
    | s -> Some s

/// The `var acc = <init>;` declaration immediately before a statement,
/// with its declarator.
let private declarationBefore
    (s: StatementSyntax)
    : (LocalDeclarationStatementSyntax * VariableDeclaratorSyntax) option =
    match s.Parent with
    | :? BlockSyntax as b ->
        let i = b.Statements.IndexOf s

        if i > 0 then
            match b.Statements.[i - 1] with
            | :? LocalDeclarationStatementSyntax as d when
                d.Declaration.Variables.Count = 1
                && not (isNull d.Declaration.Variables.[0].Initializer)
                ->
                Some(d, d.Declaration.Variables.[0])
            | _ -> None
        else
            None
    | _ -> None

/// The statement after a statement in its block, if any.
let private statementAfter (s: StatementSyntax) : StatementSyntax option =
    match s.Parent with
    | :? BlockSyntax as b ->
        let i = b.Statements.IndexOf s

        if i + 1 < b.Statements.Count then
            Some b.Statements.[i + 1]
        else
            None
    | _ -> None

/// References to a local symbol in a node.
let private refsTo (model: SemanticModel) (symbol: ISymbol) (node: SyntaxNode) =
    node.DescendantNodesAndSelf()
    |> Seq.choose (fun n ->
        match n with
        | :? IdentifierNameSyntax as id when
            SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(id).Symbol, symbol)
            ->
            Some id
        | _ -> None)
    |> List.ofSeq

/// The accumulator is untouched after the loop except for reads: the
/// rest of the block never assigns it.
let private onlyReadAfter (model: SemanticModel) (symbol: ISymbol) (loop: StatementSyntax) =
    match loop.Parent with
    | :? BlockSyntax as b ->
        let i = b.Statements.IndexOf loop

        b.Statements
        |> Seq.skip (i + 1)
        |> Seq.forall (fun s -> not (Text.assignsTo symbol.Name s))
    | _ -> false

let private isSourceEnumerable (model: SemanticModel) (f: ForEachStatementSyntax) =
    Linq.isGenericEnumerable (model.GetTypeInfo(f.Expression).Type)
    && f.Identifier.ValueText <> ""

let private literalValue (e: ExpressionSyntax) =
    match e with
    | :? LiteralExpressionSyntax as l -> Some l.Token.ValueText
    | :? MemberAccessExpressionSyntax as m when m.ToString() = "string.Empty" || m.ToString() = "String.Empty" ->
        Some ""
    | :? PrefixUnaryExpressionSyntax as u when u.IsKind SyntaxKind.UnaryMinusExpression ->
        match u.Operand with
        | :? LiteralExpressionSyntax as l -> Some("-" + l.Token.ValueText)
        | _ -> None
    | _ -> None

let private isZero (e: ExpressionSyntax) =
    match literalValue e with
    | Some v ->
        match
            System.Decimal.TryParse(
                v,
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture
            )
        with
        | true, d -> d = 0m
        | _ -> false
    | None -> false

let private isEmptyString (e: ExpressionSyntax) =
    match e with
    | :? LiteralExpressionSyntax as l -> l.IsKind SyntaxKind.StringLiteralExpression && l.Token.ValueText = ""
    | _ -> literalValue e = Some "" && not (e :? LiteralExpressionSyntax)

let private typeOf (model: SemanticModel) (e: ExpressionSyntax) = model.GetTypeInfo(e).Type

let private isIntegral (t: ITypeSymbol) =
    match t with
    | null -> false
    | t ->
        match t.SpecialType with
        | SpecialType.System_Int32
        | SpecialType.System_Int64
        | SpecialType.System_UInt32
        | SpecialType.System_UInt64
        | SpecialType.System_Int16
        | SpecialType.System_UInt16
        | SpecialType.System_Byte
        | SpecialType.System_SByte -> true
        | _ -> false

let private isSummable (t: ITypeSymbol) =
    match t with
    | null -> false
    | t ->
        match t.SpecialType with
        | SpecialType.System_Int32
        | SpecialType.System_Int64
        // `float` is absent: `Enumerable.Sum` accumulates a float sequence in a double
        | SpecialType.System_Double
        | SpecialType.System_Decimal -> true
        | _ -> false

let private inChecked (model: SemanticModel) (node: SyntaxNode) =
    model.Compilation.Options.CheckOverflow
    || node.Ancestors()
       |> Seq.exists (fun a ->
           match a with
           | :? CheckedStatementSyntax as c -> c.Keyword.IsKind SyntaxKind.CheckedKeyword
           | :? CheckedExpressionSyntax as c -> c.Keyword.IsKind SyntaxKind.CheckedKeyword
           | _ -> false)

/// A lambda `x => body` or the bare method when the body is just `x`.
let private selector (loopVar: string) (body: ExpressionSyntax) =
    match body with
    | :? IdentifierNameSyntax as id when id.Identifier.ValueText = loopVar -> None
    | _ -> Some(loopVar + " => " + body.ToString())

let private isPlainForEach (f: ForEachStatementSyntax) =
    not (f.Type :? TupleTypeSyntax) && f.Identifier.ValueText <> ""

// ---- CR0021 ----

type private Aggregate =
    | Sum of selector: string option
    | Count of predicate: string
    | Concat of selector: string option

let private aggregates (tree: SyntaxTree) (model: SemanticModel) : Suggestion list =
    let text = tree.GetText()

    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun node ->
        match node with
        | :? ForEachStatementSyntax as f when isPlainForEach f && isSourceEnumerable model f ->
            match declarationBefore f, singleStatement f.Statement with
            | Some(decl, v), Some body ->
                let acc = v.Identifier.ValueText
                let accSymbol = model.GetDeclaredSymbol v
                let loopVar = f.Identifier.ValueText
                let accType = (accSymbol :?> ILocalSymbol).Type

                let refsInBody = refsTo model accSymbol body

                // the one statement's shape
                let shape: Aggregate option =
                    match body with
                    | :? ExpressionStatementSyntax as s ->
                        match s.Expression with
                        | :? AssignmentExpressionSyntax as a when
                            a.IsKind SyntaxKind.AddAssignmentExpression
                            && a.Left.ToString() = acc
                            && refsInBody.Length = 1
                            && not (Text.mentionsName acc a.Right)
                            && not (Text.assignsTo loopVar a.Right)
                            ->
                            if accType.SpecialType = SpecialType.System_String then
                                if
                                    isEmptyString v.Initializer.Value
                                    && (typeOf model a.Right).SpecialType = SpecialType.System_String
                                then
                                    Some(Concat(selector loopVar a.Right))
                                else
                                    None
                            elif isSummable accType && isZero v.Initializer.Value then
                                let rightType = typeOf model a.Right

                                let overflowSafe = not (isIntegral accType) || inChecked model f

                                if
                                    overflowSafe
                                    && not (isNull rightType)
                                    && SymbolEqualityComparer.Default.Equals(rightType, accType)
                                then
                                    Some(Sum(selector loopVar a.Right))
                                else
                                    None
                            else
                                None
                        | _ -> None
                    | :? IfStatementSyntax as ifs when isNull ifs.Else ->
                        match singleStatement ifs.Statement with
                        | Some(:? ExpressionStatementSyntax as s) ->
                            let increments =
                                match s.Expression with
                                | :? PostfixUnaryExpressionSyntax as u ->
                                    u.IsKind SyntaxKind.PostIncrementExpression && u.Operand.ToString() = acc
                                | :? PrefixUnaryExpressionSyntax as u ->
                                    u.IsKind SyntaxKind.PreIncrementExpression && u.Operand.ToString() = acc
                                | :? AssignmentExpressionSyntax as a ->
                                    a.IsKind SyntaxKind.AddAssignmentExpression
                                    && a.Left.ToString() = acc
                                    && a.Right.ToString() = "1"
                                | _ -> false

                            if
                                increments
                                && accType.SpecialType = SpecialType.System_Int32
                                && isZero v.Initializer.Value
                                && refsInBody.Length = 1
                                && not (Text.mentionsName acc ifs.Condition)
                                && Guards.callsOnlyCore model ifs.Condition
                                && not (Text.assignsTo loopVar ifs.Condition)
                            then
                                Some(Count(loopVar + " => " + ifs.Condition.ToString()))
                            else
                                None
                        | _ -> None
                    | _ -> None

                // Sum and Count are vectorised over an array or a List<T> and 4.7× slower
                // over a lazy sequence (PerfClaims); Concat wins over anything
                let sourceType = model.GetTypeInfo(f.Expression).Type

                let fastSource =
                    match sourceType with
                    | null -> false
                    | :? IArrayTypeSymbol -> true
                    | t -> t.OriginalDefinition.ToDisplayString() = "System.Collections.Generic.List<T>"

                let measuredOk =
                    match shape with
                    | Some(Concat _) -> true
                    | Some _ -> fastSource
                    | None -> false

                match shape with
                | Some shape when
                    measuredOk
                    && onlyReadAfter model accSymbol f
                    && not (Text.holdsCommentOrDirective f)
                    && not (Text.holdsCommentOrDirective decl)
                    ->
                    let source = f.Expression.ToString()

                    let expression =
                        match shape with
                        | Sum None -> source + ".Sum()"
                        | Sum(Some sel) -> $"{source}.Sum({sel})"
                        | Count pred -> $"{source}.Count({pred})"
                        | Concat None -> $"string.Concat({source})"
                        | Concat(Some sel) -> $"string.Concat({source}.Select({sel}))"

                    let needsLinq =
                        match shape with
                        | Concat None -> false
                        | _ -> true

                    let usingEdits =
                        if needsLinq then
                            Usings.importEdit model tree f.SpanStart "System.Linq" "Enumerable"
                        else
                            Some []

                    match usingEdits with
                    | None -> None
                    | Some usingEdits ->
                        // `return acc;` right after, and no other reader: collapse
                        let collapse =
                            match statementAfter f with
                            | Some(:? ReturnStatementSyntax as r) when
                                not (isNull r.Expression)
                                && not (Text.holdsCommentOrDirective r)
                                && r.Expression.ToString() = acc
                                && (refsTo model accSymbol (Text.enclosingMember f)).Length = 2
                                ->
                                Some r
                            | _ -> None

                        let edits =
                            match collapse with
                            | Some r ->
                                usingEdits
                                @ [
                                    Suggestion.replace
                                        (TextSpan.FromBounds(decl.SpanStart, r.Span.End))
                                        ($"return {expression};")
                                ]
                            | None ->
                                usingEdits
                                @ [
                                    Suggestion.replace
                                        (TextSpan.FromBounds(v.Initializer.Value.SpanStart, f.Span.End))
                                        (expression + ";")
                                ]

                        if Guards.speculativeCheck model edits then
                            let what =
                                match shape with
                                | Sum _ -> "sum"
                                | Count _ -> "count"
                                | Concat _ -> "concatenation"

                            Some
                                {
                                    Code = AggregateCode
                                    Message = $"A loop accumulating a {what} is the aggregate"
                                    Span = TextSpan.FromBounds(f.ForEachKeyword.SpanStart, f.CloseParenToken.Span.End)
                                    Fixes = [ Suggestion.fix "Use the aggregate" AggregateCode edits ]
                                }
                        else
                            None
                | _ -> None
            | _ -> None
        | _ -> None)
    |> List.ofSeq

// ---- CR0022 ----

let private flags (tree: SyntaxTree) (model: SemanticModel) (ctx: RuleContext) : Suggestion list =
    let listsAllowed = RuleContext.knobBool ctx FlagCode "lists" true

    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun node ->
        match node with
        | :? ForEachStatementSyntax as f when isPlainForEach f && isSourceEnumerable model f ->
            match declarationBefore f, singleStatement f.Statement with
            | Some(decl, v), Some(:? IfStatementSyntax as ifs) when isNull ifs.Else ->
                let flag = v.Identifier.ValueText
                let flagSymbol = model.GetDeclaredSymbol v
                let loopVar = f.Identifier.ValueText

                let initial =
                    match v.Initializer.Value with
                    | :? LiteralExpressionSyntax as l when l.IsKind SyntaxKind.TrueLiteralExpression -> Some true
                    | :? LiteralExpressionSyntax as l when l.IsKind SyntaxKind.FalseLiteralExpression -> Some false
                    | _ -> None

                // then-branch: `flag = !initial;` optionally followed by `break;`
                let thenStatements =
                    match ifs.Statement with
                    | :? BlockSyntax as b -> List.ofSeq b.Statements
                    | s -> [ s ]

                let assignsOpposite (s: StatementSyntax) (init: bool) =
                    match s with
                    | :? ExpressionStatementSyntax as es ->
                        match es.Expression with
                        | :? AssignmentExpressionSyntax as a when
                            a.IsKind SyntaxKind.SimpleAssignmentExpression && a.Left.ToString() = flag
                            ->
                            match a.Right with
                            | :? LiteralExpressionSyntax as l ->
                                (init && l.IsKind SyntaxKind.FalseLiteralExpression)
                                || (not init && l.IsKind SyntaxKind.TrueLiteralExpression)
                            | _ -> false
                        | _ -> false
                    | _ -> false

                let shapeOk =
                    match initial, thenStatements with
                    | Some init, [ a ] -> assignsOpposite a init
                    | Some init, [ a; (:? BreakStatementSyntax) ] -> assignsOpposite a init
                    | _ -> false

                let sourceType = model.GetTypeInfo(f.Expression).Type

                let isList =
                    not (isNull sourceType)
                    && sourceType.OriginalDefinition.ToDisplayString() = "System.Collections.Generic.List<T>"

                if
                    shapeOk
                    && (listsAllowed || not isList)
                    && (refsTo model flagSymbol f).Length = 1
                    && not (Text.mentionsName flag ifs.Condition)
                    && Guards.callsOnlyCore model ifs.Condition
                    && not (Text.assignsTo loopVar ifs.Condition)
                    && ifs.Condition.ToString().IndexOf '\n' < 0
                    && onlyReadAfter model flagSymbol f
                    && not (Text.holdsCommentOrDirective f)
                then
                    let init = initial.Value
                    let source = f.Expression.ToString()

                    let expression =
                        if init then
                            // `if (c) ok = false;` → All(x => !c)
                            source
                            + ".All("
                            + loopVar
                            + " => "
                            + BoolReturn.conditionText model ifs.Condition true
                            + ")"
                        else
                            source + ".Any(" + loopVar + " => " + ifs.Condition.ToString() + ")"

                    match Usings.importEdit model tree f.SpanStart "System.Linq" "Enumerable" with
                    | None -> None
                    | Some usingEdits ->
                        let edits =
                            usingEdits
                            @ [
                                Suggestion.replace
                                    (TextSpan.FromBounds(v.Initializer.Value.SpanStart, f.Span.End))
                                    (expression + ";")
                            ]

                        if Guards.speculativeCheck model edits then
                            Some
                                {
                                    Code = FlagCode
                                    Message =
                                        (if init then
                                             "A flag cleared by a loop is All"
                                         else
                                             "A flag set by a loop is Any")
                                    Span = TextSpan.FromBounds(f.ForEachKeyword.SpanStart, f.CloseParenToken.Span.End)
                                    Fixes = [ Suggestion.fix (if init then "Use All" else "Use Any") FlagCode edits ]
                                }
                        else
                            None
                else
                    None
            | _ -> None
        | _ -> None)
    |> List.ofSeq

// ---- CR0025 ----

let private appends (tree: SyntaxTree) (model: SemanticModel) : Suggestion list =
    let isString (e: ExpressionSyntax) =
        match model.GetTypeInfo(e).Type with
        | null -> false
        | t -> t.SpecialType = SpecialType.System_String

    let isNumericLiteral (e: ExpressionSyntax) =
        match e with
        | :? LiteralExpressionSyntax as l -> l.IsKind SyntaxKind.NumericLiteralExpression
        | _ -> false

    let isImmutableArray (e: ExpressionSyntax) =
        match model.GetTypeInfo(e).Type with
        | null -> false
        | t -> t.OriginalDefinition.ToDisplayString() = "System.Collections.Immutable.ImmutableArray<T>"

    // the exact shape CR0021 rewrites: no note there
    let isConcatShape (a: AssignmentExpressionSyntax) =
        match a.Parent with
        | :? ExpressionStatementSyntax as s ->
            match s.Parent with
            | :? ForEachStatementSyntax as f -> (declarationBefore f).IsSome
            | :? BlockSyntax as b when b.Statements.Count = 1 ->
                match b.Parent with
                | :? ForEachStatementSyntax as f -> (declarationBefore f).IsSome
                | _ -> false
            | _ -> false
        | _ -> false

    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun node ->
        if not (Linq.insideLoop node) then
            None
        else
            match node with
            | :? AssignmentExpressionSyntax as a when
                a.IsKind SyntaxKind.AddAssignmentExpression
                && not (isNumericLiteral a.Right)
                && isString a.Left
                && not (isConcatShape a)
                ->
                Some(
                    Suggestion.note
                        AppendCode
                        $"'{a.Left}' grows by a piece on every iteration, copying the whole string each time: a StringBuilder, or string.Join over the pieces"
                        a.Span
                )
            | :? AssignmentExpressionSyntax as a when
                a.IsKind SyntaxKind.SimpleAssignmentExpression
                && (match a.Right with
                    | :? BinaryExpressionSyntax as b ->
                        b.IsKind SyntaxKind.AddExpression
                        && b.Left.ToString() = a.Left.ToString()
                        && not (isNumericLiteral b.Right)
                        && isString a.Left
                    | _ -> false)
                && not (isConcatShape a)
                ->
                Some(
                    Suggestion.note
                        AppendCode
                        $"'{a.Left}' grows by a piece on every iteration, copying the whole string each time: a StringBuilder, or string.Join over the pieces"
                        a.Span
                )
            | :? AssignmentExpressionSyntax as a when a.IsKind SyntaxKind.SimpleAssignmentExpression ->
                match a.Right with
                | :? InvocationExpressionSyntax as inv ->
                    let name = Linq.nameOf inv

                    // `arr = arr.Append(x).ToArray()` / `arr.Concat(…).ToArray()`
                    let arrayGrow =
                        (name = "ToArray" || name = "ToList")
                        && (match inv.Expression with
                            | :? MemberAccessExpressionSyntax as m ->
                                match m.Expression with
                                | :? InvocationExpressionSyntax as grow ->
                                    let g = Linq.nameOf grow

                                    (g = "Append" || g = "Concat" || g = "Prepend")
                                    && (Linq.receiverOf grow
                                        |> Option.exists (fun r -> r.ToString() = a.Left.ToString()))
                                | _ -> false
                            | _ -> false)

                    // `imm = imm.Add(x)` on an ImmutableArray
                    let immutableGrow =
                        name = "Add"
                        && (Linq.receiverOf inv |> Option.exists (fun r -> r.ToString() = a.Left.ToString()))
                        && isImmutableArray a.Left

                    if arrayGrow then
                        Some(
                            Suggestion.note
                                AppendCode
                                $"'{a.Left}' is rebuilt one element longer on every iteration: fill a List<T> and convert once"
                                a.Span
                        )
                    elif immutableGrow then
                        Some(
                            Suggestion.note
                                AppendCode
                                $"'{a.Left}' is copied one element longer on every iteration: ImmutableArray.CreateBuilder, then ToImmutable once"
                                a.Span
                        )
                    else
                        None
                | _ -> None
            | :? InvocationExpressionSyntax as inv when
                inv.Expression.ToString() = "Array.Resize"
                && inv.ArgumentList.Arguments.Count = 2
                && (match inv.ArgumentList.Arguments.[1].Expression with
                    | :? BinaryExpressionSyntax as b -> b.IsKind SyntaxKind.AddExpression
                    | _ -> false)
                ->
                Some(
                    Suggestion.note
                        AppendCode
                        "Resizing by one on every iteration copies the whole array each time: fill a List<T> and convert once"
                        inv.Span
                )
            | _ -> None)
    |> List.ofSeq

let analyze (tree: SyntaxTree) (model: SemanticModel) (ctx: RuleContext) : Suggestion list =
    aggregates tree model @ flags tree model ctx @ appends tree model
