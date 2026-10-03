/// Code that cannot do what it reads as doing: a test with one answer, a
/// read that always throws, a call whose whole effect is thrown away.
/// Notes: each names a defect, none knows which correction was meant.
///
/// CR0192 (correctness, note; editor offer): two operands of one `||` or
/// `&&` chain that settle it between them.
///
///   - `x != A || x != B` with two different constants: always true (no
///     value equals both). The editor offers `&&`.
///   - `x == A && x == B` with two different constants: always false. The
///     editor offers `||`.
///   - `x > E && x < E` — the same two expressions ordered both ways, at
///     least one comparison strict: always false. `x >= E && x <= E` is an
///     equality and quiet.
///
/// Guards: the two comparisons are direct operands of the same chain
/// (others may stand between); the constants are the semantic model's, so
/// a literal, a `const` and an enum member all count, two names of one
/// value are not different and constants of two types are not compared;
/// the operator is the language's or a `System` type's, never a
/// user-defined one; the compared expression is written alike on
/// both sides and holds no call, `await`, assignment, `++`/`--` or
/// construction; the operator swap is offered only where the chain is the
/// two comparisons and nothing else (in a longer chain one swapped operator
/// regroups the rest), and never applied by a sweep.
///
/// CR0193 (correctness, note): `.Value` of a `Nullable<T>` read in the
/// branch its own test proved empty — the `else` of `if (x.HasValue)`, the
/// body of `if (!x.HasValue)` or `if (x == null)`, the matching arm of a
/// conditional expression. The read throws `InvalidOperationException`
/// every time the branch runs. Guards: the condition is the bare test
/// (parenthesised or negated, never joined to another by `&&`/`||`); the
/// tested expression is a chain of locals, parameters, fields and
/// properties, and the read is written alike; nothing in the branch
/// assigns it, passes it by `ref`/`out` or steps it; a read inside a
/// lambda or local function of the branch runs later and is not counted; a
/// local a closure of the member writes, or a `ref` is taken of, is left
/// alone; for a field or
/// property, no call, construction or `await` completes before the read
/// or shares a loop with it (it may fill the value).
///
/// CR0194 (correctness, note; editor offer): a statement that calls a
/// method of an immutable collection and drops what it returns —
/// `list.Add(x);` on an `ImmutableList<T>` changes nothing, the new
/// collection is the return value. Decided by the method, not by a name
/// list: an instance method declared on a type of
/// `System.Collections.Immutable` whose return type is one too (the nested
/// builders and enumerators are neither). `_ = list.Add(x);` is the same
/// defect spelled out. The same for a `string` method returning a `string`
/// (`s.Trim();`), where an explicit discard is taken as meant and the shape
/// is left to CA1806 where that rule is on. A call passing `out`/`ref`
/// (`stack.Pop(out var top)`) has another result and is quiet. The editor
/// offers `x = x.Add(…)` for a receiver that is a plain local or value
/// parameter of the function the statement runs in, which the result
/// converts to. `s.ToString();` is written to throw on null and is quiet.
module CSharp.Refactor.DefectNotes

open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax
open Microsoft.CodeAnalysis.Text

[<Literal>]
let TautologyCode = "CR0192"

[<Literal>]
let EmptyValueCode = "CR0193"

[<Literal>]
let DroppedResultCode = "CR0194"

[<TailCall>]
let rec private bare (e: ExpressionSyntax) : ExpressionSyntax =
    match e with
    | :? ParenthesizedExpressionSyntax as p -> bare p.Expression
    | _ -> e

/// Two expressions written alike, whitespace and outer parentheses aside.
let private sameText (a: ExpressionSyntax) (b: ExpressionSyntax) =
    SyntaxFactory.AreEquivalent(bare a, bare b)

let private steps (n: SyntaxNode) =
    n.IsKind SyntaxKind.PreIncrementExpression
    || n.IsKind SyntaxKind.PreDecrementExpression
    || n.IsKind SyntaxKind.PostIncrementExpression
    || n.IsKind SyntaxKind.PostDecrementExpression

// ---- CR0192 ----

/// Reading the expression twice gives one value and does nothing: no call,
/// `await`, assignment, step or construction anywhere in it.
let private quietRead (e: ExpressionSyntax) =
    e.DescendantNodesAndSelf()
    |> Seq.forall (fun n ->
        match n with
        | :? InvocationExpressionSyntax
        | :? AwaitExpressionSyntax
        | :? AssignmentExpressionSyntax
        | :? BaseObjectCreationExpressionSyntax
        | :? AnonymousFunctionExpressionSyntax -> false
        | _ -> not (steps n))

/// The operands of a `||` (or `&&`) chain as written, a parenthesised
/// part of the same chain opened up.
let rec private operands (kind: SyntaxKind) (e: ExpressionSyntax) : ExpressionSyntax list =
    match bare e with
    | :? BinaryExpressionSyntax as b when b.IsKind kind -> operands kind b.Left @ operands kind b.Right
    | _ -> [ e ]

/// The outermost expression of its chain: no operand of a longer one.
let private isChainTop (b: BinaryExpressionSyntax) =
    let rec outward (n: SyntaxNode) =
        match n with
        | :? ParenthesizedExpressionSyntax -> outward n.Parent
        | _ -> n

    match outward b.Parent with
    | :? BinaryExpressionSyntax as parent -> not (parent.IsKind(b.Kind()))
    | _ -> true

/// Is the comparison one whose meaning is known: the language's own
/// operator, or one a type of `System` declares (`string`, `decimal`,
/// `DateTime`)? A user-defined operator may answer anything.
let private knownOperator (model: SemanticModel) (e: ExpressionSyntax) =
    match model.GetSymbolInfo(e).Symbol with
    | :? IMethodSymbol as m ->
        m.MethodKind = MethodKind.BuiltinOperator
        || (not (isNull m.ContainingType)
            && not (isNull m.ContainingType.ContainingNamespace)
            && m.ContainingType.ContainingNamespace.ToDisplayString() = "System")
    | _ -> false

/// `x op K` either way round, K a constant and x not: x, K's value, K.
let private againstConstant
    (model: SemanticModel)
    (op: SyntaxKind)
    (e: ExpressionSyntax)
    : (ExpressionSyntax * obj * ExpressionSyntax) option =
    match e with
    | :? BinaryExpressionSyntax as b when b.IsKind op && knownOperator model b ->
        let left = model.GetConstantValue b.Left
        let right = model.GetConstantValue b.Right

        if right.HasValue && not left.HasValue then
            Some(b.Left, right.Value, b.Right)
        elif left.HasValue && not right.HasValue then
            Some(b.Right, left.Value, b.Left)
        else
            None
    | _ -> None

/// Two constants no one value equals: of one type and unequal, or one of
/// them null.
let private differentConstants (a: obj) (b: obj) =
    match a, b with
    | null, null -> false
    | null, _
    | _, null -> true
    | _ -> a.GetType() = b.GetType() && not (a.Equals b)

/// A relational comparison as (the lesser side, the greater side, strict).
let private ordered (e: ExpressionSyntax) : (ExpressionSyntax * ExpressionSyntax * bool) option =
    match e with
    | :? BinaryExpressionSyntax as b ->
        match b.Kind() with
        | SyntaxKind.LessThanExpression -> Some(b.Left, b.Right, true)
        | SyntaxKind.LessThanOrEqualExpression -> Some(b.Left, b.Right, false)
        | SyntaxKind.GreaterThanExpression -> Some(b.Right, b.Left, true)
        | SyntaxKind.GreaterThanOrEqualExpression -> Some(b.Right, b.Left, false)
        | _ -> None
    | _ -> None

let private tautologies (tree: SyntaxTree) (model: SemanticModel) : Suggestion list =
    tree.GetRoot().DescendantNodes()
    |> Seq.collect (fun n ->
        match n with
        | :? BinaryExpressionSyntax as chain when
            (chain.IsKind SyntaxKind.LogicalOrExpression
             || chain.IsKind SyntaxKind.LogicalAndExpression)
            && isChainTop chain
            ->
            let isOr = chain.IsKind SyntaxKind.LogicalOrExpression
            let parts = operands (chain.Kind()) chain |> Array.ofList

            let pairs =
                [|
                    for i in 0 .. parts.Length - 1 do
                        for j in i + 1 .. parts.Length - 1 -> parts.[i], parts.[j]
                |]

            let span (p: ExpressionSyntax) (q: ExpressionSyntax) =
                TextSpan.FromBounds(p.SpanStart, q.Span.End)

            // in a longer chain one swapped operator regroups the other operands
            let swap (title: string) (operator: string) =
                if parts.Length = 2 then
                    [
                        Suggestion.fix title TautologyCode [ Suggestion.replace chain.OperatorToken.Span operator ]
                        |> Suggestion.editorOnly
                    ]
                else
                    []

            let constants =
                let op =
                    if isOr then
                        SyntaxKind.NotEqualsExpression
                    else
                        SyntaxKind.EqualsExpression

                pairs
                |> Seq.tryPick (fun (p, q) ->
                    match againstConstant model op (bare p), againstConstant model op (bare q) with
                    | Some(x, a, aText), Some(y, b, bText) when sameText x y && quietRead x && differentConstants a b ->
                        Some
                            {
                                Code = TautologyCode
                                Message =
                                    if isOr then
                                        $"'{p} || {q}' is always true: '{bare x}' cannot equal both {aText} and {bText}, so one side always holds — '&&' was almost certainly meant"
                                    else
                                        $"'{p} && {q}' is always false: '{bare x}' cannot equal both {aText} and {bText} — '||' was almost certainly meant"
                                Span = span p q
                                Fixes = if isOr then swap "Use &&" "&&" else swap "Use ||" "||"
                            }
                    | _ -> None)

            let ranges =
                if isOr then
                    None
                else
                    pairs
                    |> Seq.tryPick (fun (p, q) ->
                        match ordered (bare p), ordered (bare q) with
                        | Some(lesser, greater, strict), Some(lesser2, greater2, strict2) when
                            (strict || strict2)
                            && sameText lesser greater2
                            && sameText greater lesser2
                            && not (sameText lesser greater)
                            && knownOperator model (bare p)
                            && knownOperator model (bare q)
                            && quietRead lesser
                            && quietRead greater
                            ->
                            Some(
                                Suggestion.note
                                    TautologyCode
                                    $"'{p} && {q}' is always false: the two comparisons order '{bare lesser}' and '{bare greater}' both ways, and no value satisfies both"
                                    (span p q)
                            )
                        | _ -> None)

            Option.toList constants @ Option.toList ranges
        | _ -> [])
    |> List.ofSeq

// ---- CR0193 ----

let private isNullable (model: SemanticModel) (e: ExpressionSyntax) =
    match model.GetTypeInfo(e).Type with
    | null -> false
    | t -> t.OriginalDefinition.SpecialType = SpecialType.System_Nullable_T

/// A chain of locals, parameters, fields and properties: no call runs
/// when it is read.
let rec private plainRead (model: SemanticModel) (e: ExpressionSyntax) : bool =
    match e with
    | :? ParenthesizedExpressionSyntax as p -> plainRead model p.Expression
    | :? ThisExpressionSyntax -> true
    | :? IdentifierNameSyntax
    | :? MemberAccessExpressionSyntax ->
        match model.GetSymbolInfo(e).Symbol with
        | :? ILocalSymbol
        | :? IParameterSymbol
        | :? INamedTypeSymbol
        | :? INamespaceSymbol -> true
        | :? IFieldSymbol
        | :? IPropertySymbol ->
            match e with
            | :? MemberAccessExpressionSyntax as ma ->
                ma.IsKind SyntaxKind.SimpleMemberAccessExpression
                && plainRead model ma.Expression
            | _ -> true
        | _ -> false
    | _ -> false

/// A bare emptiness test: the tested expression, and whether it is EMPTY
/// where the test holds.
let rec private emptinessTest (condition: ExpressionSyntax) : (ExpressionSyntax * bool) option =
    let isNull (e: ExpressionSyntax) =
        (bare e).IsKind SyntaxKind.NullLiteralExpression

    match condition with
    | :? ParenthesizedExpressionSyntax as p -> emptinessTest p.Expression
    | :? PrefixUnaryExpressionSyntax as u when u.IsKind SyntaxKind.LogicalNotExpression ->
        emptinessTest u.Operand |> Option.map (fun (x, empty) -> x, not empty)
    | :? MemberAccessExpressionSyntax as ma when
        ma.IsKind SyntaxKind.SimpleMemberAccessExpression
        && ma.Name.Identifier.ValueText = "HasValue"
        ->
        Some(ma.Expression, false)
    | :? BinaryExpressionSyntax as b when
        b.IsKind SyntaxKind.EqualsExpression || b.IsKind SyntaxKind.NotEqualsExpression
        ->
        let tested =
            if isNull b.Right then Some b.Left
            elif isNull b.Left then Some b.Right
            else None

        tested |> Option.map (fun x -> x, b.IsKind SyntaxKind.EqualsExpression)
    | :? IsPatternExpressionSyntax as p ->
        let nullPattern (pattern: PatternSyntax) =
            match pattern with
            | :? ConstantPatternSyntax as c -> isNull c.Expression
            | _ -> false

        match p.Pattern with
        | pattern when nullPattern pattern -> Some(p.Expression, true)
        | :? UnaryPatternSyntax as u when u.IsKind SyntaxKind.NotPattern && nullPattern u.Pattern ->
            Some(p.Expression, false)
        | _ -> None
    | _ -> None

/// Is the node a write of one of the expressions: an assignment to it, a
/// `ref`/`out`/`in` pass, a `ref` taken, a step?
let private writes (targets: ExpressionSyntax list) (n: SyntaxNode) =
    let isTarget (e: ExpressionSyntax) = targets |> List.exists (sameText e)

    match n with
    | :? AssignmentExpressionSyntax as a ->
        isTarget a.Left
        || (match a.Left with
            | :? TupleExpressionSyntax as t -> t.Arguments |> Seq.exists (fun x -> isTarget x.Expression)
            | _ -> false)
    | :? ArgumentSyntax as a -> not (a.RefKindKeyword.IsKind SyntaxKind.None) && isTarget a.Expression
    | :? RefExpressionSyntax as r -> isTarget r.Expression
    | :? PrefixUnaryExpressionSyntax as u when steps u -> isTarget u.Operand
    | :? PostfixUnaryExpressionSyntax as u when steps u -> isTarget u.Operand
    | _ -> false

let private isClosure (n: SyntaxNode) =
    n :? AnonymousFunctionExpressionSyntax || n :? LocalFunctionStatementSyntax

let private isLoop (n: SyntaxNode) =
    n :? ForStatementSyntax
    || n :? CommonForEachStatementSyntax
    || n :? WhileStatementSyntax
    || n :? DoStatementSyntax

/// The `.Value` reads of `x` the branch runs while `x` is empty.
let private emptyReads (model: SemanticModel) (x: ExpressionSyntax) (branch: SyntaxNode) : Suggestion list =
    // the expression and every receiver it is read through: a write of `a` changes `a.b`
    let rec chainOf (e: ExpressionSyntax) : ExpressionSyntax list =
        match bare e with
        | :? MemberAccessExpressionSyntax as ma -> (ma :> ExpressionSyntax) :: chainOf ma.Expression
        | other -> [ other ]

    let targets = chainOf x

    let reads =
        branch.DescendantNodesAndSelf()
        |> Seq.choose (fun n ->
            match n with
            | :? MemberAccessExpressionSyntax as ma when
                ma.IsKind SyntaxKind.SimpleMemberAccessExpression
                && ma.Name.Identifier.ValueText = "Value"
                && sameText ma.Expression x
                // a closure of the branch runs later, `nameof` never
                && not (
                    ma.Ancestors()
                    |> Seq.takeWhile (fun a -> not (obj.ReferenceEquals(a, branch)))
                    |> Seq.exists (fun a ->
                        isClosure a
                        || (match a with
                            | :? InvocationExpressionSyntax as inv -> inv.Expression.ToString() = "nameof"
                            | _ -> false))
                )
                ->
                match model.GetSymbolInfo(ma).Symbol with
                | :? IPropertySymbol as p when
                    not (isNull p.ContainingType)
                    && p.ContainingType.OriginalDefinition.SpecialType = SpecialType.System_Nullable_T
                    ->
                    Some ma
                | _ -> None
            | _ -> None)
        |> List.ofSeq

    if reads.IsEmpty || branch.DescendantNodesAndSelf() |> Seq.exists (writes targets) then
        []
    else
        let isLocal =
            match model.GetSymbolInfo(bare x).Symbol with
            | :? ILocalSymbol
            | :? IParameterSymbol -> true
            | _ -> false

        // a local written by a closure of the member can be filled by any call,
        // one a `ref` was taken of by any write through the alias
        let writtenByClosure () =
            (Text.enclosingMember branch).DescendantNodes()
            |> Seq.exists (fun n ->
                match n with
                | :? RefExpressionSyntax as r -> targets |> List.exists (sameText r.Expression)
                | _ -> isClosure n && n.DescendantNodes() |> Seq.exists (writes targets))

        // a field or property can be filled by whatever runs before the read
        let mayFill (n: SyntaxNode) =
            n :? InvocationExpressionSyntax
            || n :? BaseObjectCreationExpressionSyntax
            || n :? AwaitExpressionSyntax

        let filledBefore (read: MemberAccessExpressionSyntax) =
            let loops =
                read.Ancestors()
                |> Seq.takeWhile (fun a -> not (obj.ReferenceEquals(a, branch)))
                |> Seq.filter isLoop
                |> List.ofSeq

            branch.DescendantNodesAndSelf()
            |> Seq.exists (fun n ->
                mayFill n
                && (n.Span.End <= read.SpanStart
                    || loops |> List.exists (fun loop -> loop.Span.Contains n.Span)))

        if isLocal && writtenByClosure () then
            []
        else
            reads
            |> List.filter (fun read -> isLocal || not (filledBefore read))
            |> List.map (fun read ->
                Suggestion.note
                    EmptyValueCode
                    $"'{read}' is read where '{bare x}' was just tested to have no value: it throws InvalidOperationException every time this branch runs — the test is inverted, or the read belongs to the other branch"
                    read.Span)

let private emptyValues (tree: SyntaxTree) (model: SemanticModel) : Suggestion list =
    let governed (condition: ExpressionSyntax) (whenTrue: SyntaxNode) (whenFalse: SyntaxNode) =
        match emptinessTest condition with
        | Some(x, emptyWhenTrue) when isNullable model x && plainRead model x ->
            match (if emptyWhenTrue then whenTrue else whenFalse) with
            | null -> []
            | branch -> emptyReads model x branch
        | _ -> []

    tree.GetRoot().DescendantNodes()
    |> Seq.collect (fun n ->
        match n with
        | :? IfStatementSyntax as i ->
            governed
                i.Condition
                i.Statement
                (match i.Else with
                 | null -> null
                 | e -> e.Statement :> SyntaxNode)
        | :? ConditionalExpressionSyntax as c -> governed c.Condition c.WhenTrue c.WhenFalse
        | _ -> [])
    // a read under two nested tests is one read
    |> Seq.distinctBy (fun s -> s.Span)
    |> List.ofSeq

// ---- CR0194 ----

/// A collection type or interface of `System.Collections.Immutable`
/// itself: the nested builders and enumerators are not.
let private immutableCollection (t: ITypeSymbol) =
    match t with
    | :? INamedTypeSymbol as named ->
        isNull named.ContainingType
        && not (isNull named.ContainingNamespace)
        && named.ContainingNamespace.ToDisplayString() = "System.Collections.Immutable"
    | _ -> false

let private droppedResults (tree: SyntaxTree) (model: SemanticModel) (ctx: RuleContext) : Suggestion list =
    let stringsYield = RuleContext.shadowedRuleOn ctx [ "CA1806" ]

    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun n ->
        match n with
        | :? ExpressionStatementSyntax as statement ->
            let expression, discarded =
                match bare statement.Expression with
                | :? AssignmentExpressionSyntax as a when
                    a.IsKind SyntaxKind.SimpleAssignmentExpression
                    && (match model.GetSymbolInfo(a.Left).Symbol with
                        | :? IDiscardSymbol -> true
                        | _ -> false)
                    ->
                    bare a.Right, true
                | e -> e, false

            let call, conditional =
                match expression with
                | :? InvocationExpressionSyntax as inv -> Some inv, false
                | :? ConditionalAccessExpressionSyntax as c ->
                    (match c.WhenNotNull with
                     | :? InvocationExpressionSyntax as inv -> Some inv
                     | _ -> None),
                    true
                | _ -> None, false

            match call |> Option.map (fun inv -> inv, model.GetSymbolInfo(inv).Symbol) with
            | Some(inv, (:? IMethodSymbol as m)) when
                not m.IsStatic
                && m.MethodKind = MethodKind.Ordinary
                && not (isNull m.ContainingType)
                // an `out` or `ref` argument is a result the statement does keep
                && inv.ArgumentList.Arguments
                   |> Seq.forall (fun a -> a.RefKindKeyword.IsKind SyntaxKind.None)
                ->
                let collection =
                    immutableCollection m.ContainingType && immutableCollection m.ReturnType

                let text =
                    m.ContainingType.SpecialType = SpecialType.System_String
                    && m.ReturnType.SpecialType = SpecialType.System_String
                    // `s.ToString();` builds nothing: it is written to throw on null
                    && m.Name <> "ToString"
                    && not discarded
                    && not stringsYield

                if not (collection || text) then
                    None
                else
                    // `x.M(…);` on a local or value parameter the result fits: `x = x.M(…);`
                    let offer =
                        match inv.Expression with
                        | :? MemberAccessExpressionSyntax as ma when
                            not discarded
                            && not conditional
                            && ma.IsKind SyntaxKind.SimpleMemberAccessExpression
                            ->
                            match ma.Expression with
                            | :? IdentifierNameSyntax as receiver ->
                                let symbol = model.GetSymbolInfo(receiver).Symbol

                                // the variable belongs to the function the statement runs in: an
                                // assignment to a captured one changes what the outer code sees later
                                let ownFunction =
                                    let closureOf (node: SyntaxNode) =
                                        node.AncestorsAndSelf() |> Seq.tryFind isClosure

                                    match symbol with
                                    | null -> false
                                    | s ->
                                        match s.DeclaringSyntaxReferences |> Seq.tryHead with
                                        | Some declared -> closureOf (declared.GetSyntax()) = closureOf statement
                                        | None -> false

                                let target =
                                    match symbol with
                                    | _ when not ownFunction -> null
                                    | :? ILocalSymbol as l when
                                        not l.IsConst
                                        && not l.IsForEach
                                        && not l.IsUsing
                                        && not l.IsFixed
                                        && l.RefKind = RefKind.None
                                        ->
                                        l.Type
                                    | :? IParameterSymbol as p when
                                        p.RefKind = RefKind.None
                                        && (match p.ContainingSymbol with
                                            | :? IMethodSymbol as owner -> owner.MethodKind <> MethodKind.Constructor
                                            | _ -> false)
                                        ->
                                        p.Type
                                    | _ -> null

                                if
                                    not (isNull target)
                                    && model.Compilation.HasImplicitConversion(m.ReturnType, target)
                                then
                                    [
                                        Suggestion.fix
                                            $"Assign the result to '{receiver.Identifier.Text}'"
                                            DroppedResultCode
                                            [ Suggestion.insert statement.SpanStart $"{receiver.Identifier.Text} = " ]
                                        |> Suggestion.editorOnly
                                    ]
                                else
                                    []
                            | _ -> []
                        | _ -> []

                    let subject, result =
                        if collection then
                            $"{m.ContainingType.Name} is immutable", "the new collection"
                        else
                            "A string is immutable", "the new string"

                    Some
                        {
                            Code = DroppedResultCode
                            Message =
                                $"{subject}: {m.Name} returns {result} and the statement drops it, so nothing changes — assign the result"
                            Span = statement.Expression.Span
                            Fixes = offer
                        }
            | _ -> None
        | _ -> None)
    |> List.ofSeq

let analyze (tree: SyntaxTree) (model: SemanticModel) (ctx: RuleContext) : Suggestion list =
    tautologies tree model @ emptyValues tree model @ droppedResults tree model ctx
