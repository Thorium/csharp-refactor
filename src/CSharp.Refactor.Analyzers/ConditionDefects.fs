/// Tests and handlers that cannot do what they read as doing: an
/// assignment where a comparison was meant, a null test of something that
/// is never null, a catch the exception never reaches.
///
/// CR0195 (correctness, fix): a simple assignment of type `bool` standing
/// as a condition — the whole condition of `if`/`while`/`do`/`for`/`?:`/a
/// query's `where`, an operand of `&&`/`||`/`!` in one, or the expression
/// body of a lambda returning `bool` that takes a parameter. Three shapes
/// by what is assigned:
///
///   - `true`/`false` (`if (done = false)`): the condition is constant and
///     the variable overwritten. The fix writes `==`; the editor also
///     offers the bare test (`done`, `!done`).
///   - a plain variable — a chain of locals, parameters, fields and
///     properties (`if (a = b)`): the fix writes `==`.
///   - anything that runs (`if (found = map.TryGetValue(k, out v))`): the
///     assign-and-test idiom, a note; the editor offers `==`.
///
/// Guards: both sides are `bool` themselves, so `==` is the language's; an
/// assignment under a comparison (`(line = r.ReadLine()) != null`) is not
/// a condition; one pair of parentheses more than the place needs (`if ((a
/// = b))`) marks the assignment as meant and is quiet; a local the
/// assignment is the first write of keeps the assignment (a note with the
/// offer: `==` would read it unassigned); in a lambda or a query clause
/// the comparison is an editor offer (a predicate run per element may be
/// written to assign: `xs.All(x => x.Done = true)` marks every element),
/// and so is a variable assigned in a loop's condition (it is read anew on
/// every pass, as an assign-and-test is).
///
/// CR0196 (correctness, fix): a value of a non-nullable struct or enum
/// type compared with `null` — always false for `==`, always true for
/// `!=`. Where no value is possible the type should be nullable; where it
/// is not, the test and what it guards are dead. Fixes, for an operand that
/// is a chain of locals, parameters, fields and properties:
///
///   - `if (d == null) S` is removed, or replaced by its `else` body;
///     `if (d != null) S` becomes `S`, its `else` dropped;
///   - `d == null ? a : b` is `b`, `d != null ? a : b` is `a`, where the
///     kept arm has the conditional's type;
///   - `d == null || p` and `d != null && p` are `p`.
///
/// Anything else is a note. Guards: the comparison is the lifted one (a
/// user-defined operator taking the null may answer anything); the `if`
/// is a statement of a block; a kept block loses its braces unless it
/// declares a name, and keeps its lines when one of them cannot be
/// re-indented; a kept statement that never completes (a `return`) with
/// statements after it stays a note (they would become unreachable); a
/// rewrite that would drop a comment is the editor's, one that would
/// cross a directive is not offered; nothing is rewritten inside an
/// expression tree.
///
/// CR0197 (correctness, note; editor offers): `try { t.Wait(); } catch
/// (IOException) { … }` — a blocking wait (`Wait()`, `Result`,
/// `Task.WaitAll`) throws the task's exception wrapped in an
/// `AggregateException`, which a catch of the exception's own type never
/// sees. The editor offers a second clause that catches the wrapper when
/// its inner exception (one level, or the deepest) is of that type, and,
/// where the `try` holds nothing but the waits, the removal of the clause
/// that cannot be reached. Guards: no clause of the `try` catches
/// `AggregateException`; the clause's type is neither it nor a base of it;
/// not a type the wait itself throws (`ObjectDisposedException`, the
/// thread-interruption ones, and `OperationCanceledException` or an
/// argument exception where a wait takes a token or a timeout); a wait
/// under a nested `try` or in a closure is another handler's; `Task.WaitAny`
/// and `GetAwaiter().GetResult()` throw no wrapper; an `async` function is
/// left to the rule that makes the wait an `await`.
module CSharp.Refactor.ConditionDefects

open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax
open Microsoft.CodeAnalysis.Operations
open Microsoft.CodeAnalysis.Text

[<Literal>]
let AssignmentTestCode = "CR0195"

[<Literal>]
let NeverNullCode = "CR0196"

[<Literal>]
let WrappedCatchCode = "CR0197"

[<TailCall>]
let rec private bare (e: ExpressionSyntax) : ExpressionSyntax =
    match e with
    | :? ParenthesizedExpressionSyntax as p -> bare p.Expression
    | _ -> e

let private isClosure (n: SyntaxNode) =
    n :? AnonymousFunctionExpressionSyntax || n :? LocalFunctionStatementSyntax

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

let private isBool (t: ITypeSymbol) =
    not (isNull t) && t.SpecialType = SpecialType.System_Boolean

/// Is only whitespace between the start of the position's line and it?
let private firstOnLine (text: SourceText) (position: int) =
    let line = text.Lines.GetLineFromPosition position
    text.ToString(TextSpan.FromBounds(line.Start, position)).Trim() = ""

/// The lines from `first` to `last` moved from one indentation to another,
/// the first line's own indentation left off (it lands where an indented
/// node stood). None where a line does not start with the indentation.
let private reindent (text: SourceText) (first: int) (last: int) (from: string) (onto: string) : string option =
    let firstLine = text.Lines.GetLineFromPosition(first).LineNumber
    let lastLine = text.Lines.GetLineFromPosition(last).LineNumber
    let nl = Text.newlineAt text first

    let lines =
        [
            for i in firstLine..lastLine do
                let line = text.Lines.[i]

                let written =
                    if i = lastLine then
                        text.ToString(TextSpan.FromBounds(line.Start, last))
                    else
                        line.ToString()

                if System.String.IsNullOrWhiteSpace written then
                    Some ""
                elif written.StartsWith from then
                    Some(onto + written.Substring from.Length)
                else
                    None
        ]

    if lines |> List.forall Option.isSome then
        let joined = lines |> List.map Option.get |> String.concat nl
        Some(joined.Substring(min onto.Length joined.Length))
    else
        None

/// A block's statements as they read in the block's place, one level out.
/// None where they cannot be moved: an empty block, a token spanning lines,
/// a layout that is not one statement per line.
let private unwrapped (text: SourceText) (block: BlockSyntax) (outer: string) : string option =
    if block.Statements.Count = 0 then
        None
    else
        let first = block.Statements.[0]
        let last = block.Statements.[block.Statements.Count - 1]

        let lineOf (position: int) =
            text.Lines.GetLineFromPosition(position).LineNumber

        if lineOf first.SpanStart = lineOf block.OpenBraceToken.SpanStart then
            // `{ A(); B(); }` on one line
            if lineOf block.CloseBraceToken.SpanStart = lineOf first.SpanStart then
                Some(text.ToString(TextSpan.FromBounds(first.SpanStart, last.Span.End)))
            else
                None
        elif
            Text.spansLines block
            || not (firstOnLine text first.SpanStart)
            || lineOf block.CloseBraceToken.SpanStart = lineOf last.Span.End
        then
            None
        else
            let inner = Text.leadingWhitespace text first.SpanStart

            if inner.Length > outer.Length && inner.StartsWith outer then
                reindent text first.SpanStart last.Span.End inner outer
            else
                None

/// The whole lines a statement occupies alone, line break included, else
/// its own span.
let private wholeLines (text: SourceText) (s: SyntaxNode) : TextSpan =
    let endLine = text.Lines.GetLineFromPosition s.Span.End

    if
        firstOnLine text s.SpanStart
        && text.ToString(TextSpan.FromBounds(s.Span.End, endLine.End)).Trim() = ""
    then
        TextSpan.FromBounds(text.Lines.GetLineFromPosition(s.SpanStart).Start, endLine.EndIncludingLineBreak)
    else
        s.Span

// ---- CR0195 ----

let private assignmentTests (tree: SyntaxTree) (model: SemanticModel) : Suggestion list =
    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun n ->
        match n with
        | :? AssignmentExpressionSyntax as a when a.IsKind SyntaxKind.SimpleAssignmentExpression ->
            // the assignment with the parentheses written around it
            let rec wrapped (e: ExpressionSyntax) (depth: int) =
                match e.Parent with
                | :? ParenthesizedExpressionSyntax as p -> wrapped p (depth + 1)
                | _ -> e, depth

            let outer, parentheses = wrapped a 0

            // an operand needs one pair; a second pair says the assignment is meant
            let needed =
                match outer.Parent with
                | :? BinaryExpressionSyntax as b when
                    b.IsKind SyntaxKind.LogicalAndExpression
                    || b.IsKind SyntaxKind.LogicalOrExpression
                    ->
                    Some 1
                | :? PrefixUnaryExpressionSyntax as u when u.IsKind SyntaxKind.LogicalNotExpression -> Some 1
                | :? ConditionalExpressionSyntax as c when obj.ReferenceEquals(c.Condition, outer) -> Some 1
                | :? IfStatementSyntax
                | :? WhileStatementSyntax
                | :? DoStatementSyntax
                | :? ForStatementSyntax
                | :? WhereClauseSyntax
                | :? WhenClauseSyntax
                | :? LambdaExpressionSyntax -> Some 0
                | _ -> None

            // up through `&&`, `||`, `!` and their parentheses to what the condition belongs to:
            // Some(Some lambda) for a lambda's body, Some None for a statement's or clause's condition
            let rec slot (e: ExpressionSyntax) : LambdaExpressionSyntax option option =
                match e.Parent with
                | :? ParenthesizedExpressionSyntax as p -> slot p
                | :? BinaryExpressionSyntax as b when
                    b.IsKind SyntaxKind.LogicalAndExpression
                    || b.IsKind SyntaxKind.LogicalOrExpression
                    ->
                    slot b
                | :? PrefixUnaryExpressionSyntax as u when u.IsKind SyntaxKind.LogicalNotExpression -> slot u
                | :? IfStatementSyntax as s when obj.ReferenceEquals(s.Condition, e) -> Some None
                | :? WhileStatementSyntax as s when obj.ReferenceEquals(s.Condition, e) -> Some None
                | :? DoStatementSyntax as s when obj.ReferenceEquals(s.Condition, e) -> Some None
                | :? ForStatementSyntax as s when obj.ReferenceEquals(s.Condition, e) -> Some None
                | :? ConditionalExpressionSyntax as c when obj.ReferenceEquals(c.Condition, e) -> Some None
                | :? WhereClauseSyntax as w when obj.ReferenceEquals(w.Condition, e) -> Some None
                | :? WhenClauseSyntax as w when obj.ReferenceEquals(w.Condition, e) -> Some None
                | :? LambdaExpressionSyntax as l when obj.ReferenceEquals(l.Body, e) ->
                    match model.GetSymbolInfo(l).Symbol with
                    | :? IMethodSymbol as m when isBool m.ReturnType && m.Parameters.Length > 0 -> Some(Some l)
                    | _ -> None
                | _ -> None

            let right = bare a.Right

            if
                needed <> Some parentheses
                || not (isBool (model.GetTypeInfo(a).Type))
                || not (isBool (model.GetTypeInfo(a.Left).Type))
                || not (isBool (model.GetTypeInfo(right).Type))
            then
                None
            else
                match slot outer with
                | None -> None
                | Some lambda ->
                    let leftText = a.Left.ToString()

                    // `==` binds tighter than the assignment did: a right side that binds
                    // looser than `==` takes parentheses
                    let compareEdits =
                        Suggestion.replace a.OperatorToken.Span "=="
                        :: (match a.Right with
                            | :? BinaryExpressionSyntax
                            | :? ConditionalExpressionSyntax
                            | :? AssignmentExpressionSyntax
                            | :? AnonymousFunctionExpressionSyntax
                            | :? IsPatternExpressionSyntax
                            | :? SwitchExpressionSyntax
                            | :? ThrowExpressionSyntax ->
                                [
                                    Suggestion.insert a.Right.SpanStart "("
                                    Suggestion.insert a.Right.Span.End ")"
                                ]
                            | _ -> [])

                    let compare = Suggestion.fix "Compare with ==" AssignmentTestCode compareEdits

                    // without the assignment the left side must already hold a value
                    let assignedBefore =
                        let onEntry (symbol: ISymbol) =
                            try
                                let flow = model.AnalyzeDataFlow a
                                flow.Succeeded && flow.DefinitelyAssignedOnEntry.Contains symbol
                            with :? System.ArgumentException ->
                                false

                        match model.GetSymbolInfo(a.Left).Symbol with
                        | :? ILocalSymbol as local -> onEntry local
                        | :? IParameterSymbol as p when p.RefKind = RefKind.Out -> onEntry p
                        | null -> false
                        | _ -> true

                    let literal =
                        if right.IsKind SyntaxKind.TrueLiteralExpression then
                            Some true
                        elif right.IsKind SyntaxKind.FalseLiteralExpression then
                            Some false
                        else
                            None

                    // where the assignment may be the point, the comparison is the editor's:
                    // a predicate run per element (`xs.All(x => x.Done = true)` marks every
                    // element), and a loop's condition assigned from a variable, which is
                    // read anew on every pass
                    let perElement =
                        lambda.IsSome
                        || a.Ancestors() |> Seq.exists (fun x -> x :? QueryExpressionSyntax)

                    let loopRead =
                        literal.IsNone
                        && (match a.Ancestors() |> Seq.tryFind (fun x -> not (x :? ExpressionSyntax)) with
                            | Some(:? WhileStatementSyntax)
                            | Some(:? DoStatementSyntax)
                            | Some(:? ForStatementSyntax) -> true
                            | _ -> false)

                    let sweeps = assignedBefore && not perElement && not loopRead
                    let primary = if sweeps then compare else Suggestion.editorOnly compare

                    match literal with
                    // the compiler's own warning for the shape, switched off, is the author's "I know"
                    | Some _ when Text.compilerWarningOff model a.SpanStart [ "CS0665" ] -> None
                    | Some value ->
                        let bareTest = if value then leftText else "!" + leftText
                        let always = if value then "true" else "false"

                        Some
                            {
                                Code = AssignmentTestCode
                                Message =
                                    $"'{a}' assigns: the condition is always {always} and '{leftText}' is overwritten — a comparison was meant (the compiler's CS0665)"
                                Span = a.Span
                                Fixes =
                                    primary
                                    // the bare test replaces the whole assignment: not across a directive
                                    :: (if Text.crossesDirective a then
                                            []
                                        else
                                            [
                                                Suggestion.fix
                                                    $"Test '{bareTest}'"
                                                    (AssignmentTestCode + ".bare")
                                                    [ Suggestion.replace a.Span bareTest ]
                                                |> Suggestion.editorOnly
                                            ])
                            }
                    | None when plainRead model right ->
                        Some
                            {
                                Code = AssignmentTestCode
                                Message =
                                    $"'{a}' assigns '{right}' to '{leftText}' and tests the result — '==' compares the two"
                                Span = a.Span
                                Fixes = [ primary ]
                            }
                    | None ->
                        Some
                            {
                                Code = AssignmentTestCode
                                Message =
                                    $"'{leftText} = …' assigns inside a condition and tests the result: if a comparison was meant it is '==', and an assignment that is meant reads clearer on a line of its own"
                                Span = a.Span
                                Fixes = [ Suggestion.editorOnly compare ]
                            }
        | _ -> None)
    |> List.ofSeq

// ---- CR0196 ----

/// A struct or enum that is never null: not `Nullable<T>`, not a type
/// parameter, not a ref struct.
let private neverNull (t: ITypeSymbol) =
    not (isNull t)
    && t.IsValueType
    && (t.TypeKind = TypeKind.Struct || t.TypeKind = TypeKind.Enum)
    && t.OriginalDefinition.SpecialType <> SpecialType.System_Nullable_T
    && not t.IsRefLikeType

let private neverNulls (tree: SyntaxTree) (model: SemanticModel) : Suggestion list =
    let text = tree.GetText()

    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun n ->
        match n with
        | :? BinaryExpressionSyntax as b when
            b.IsKind SyntaxKind.EqualsExpression || b.IsKind SyntaxKind.NotEqualsExpression
            ->
            let isNullLiteral (e: ExpressionSyntax) =
                (bare e).IsKind SyntaxKind.NullLiteralExpression

            let operand =
                if isNullLiteral b.Right then Some b.Left
                elif isNullLiteral b.Left then Some b.Right
                else None

            match operand with
            | Some operand when
                neverNull (model.GetTypeInfo(operand).Type)
                // the compiler's own warning for the shape, switched off, is the author's "I know"
                && not (Text.compilerWarningOff model b.SpanStart [ "CS0472"; "CS8073" ])
                // the lifted comparison: one a type declares for the null itself may answer anything
                && (match model.GetOperation b with
                    | :? IBinaryOperation as op -> op.IsLifted
                    | _ -> false)
                ->
                let alwaysTrue = b.IsKind SyntaxKind.NotEqualsExpression

                let typeText =
                    model.GetTypeInfo(operand).Type.ToMinimalDisplayString(model, operand.SpanStart)

                // the test as written, parentheses and all
                let rec written (e: ExpressionSyntax) : ExpressionSyntax =
                    match e.Parent with
                    | :? ParenthesizedExpressionSyntax as p -> written p
                    | _ -> e

                let test = written b

                // (the edits, the node whose text they drop)
                let rewrite: (TextEdit list * SyntaxNode) option =
                    if not (plainRead model operand) || Text.insideExpressionTree model b then
                        None
                    else
                        match test.Parent with
                        | :? IfStatementSyntax as s when
                            obj.ReferenceEquals(s.Condition, test)
                            && (s.Parent :? BlockSyntax)
                            // on lines of its own, or whole on a line it shares
                            && (firstOnLine text s.SpanStart || not (Text.multiLine text s))
                            ->
                            let block = s.Parent :?> BlockSyntax

                            let kept: StatementSyntax =
                                if alwaysTrue then s.Statement
                                elif isNull s.Else then null
                                else s.Else.Statement

                            let lastOfBlock =
                                obj.ReferenceEquals(block.Statements.[block.Statements.Count - 1], s)

                            // its own lines go whole; on a shared line, the statement and the
                            // space up to what follows it
                            let removed =
                                if firstOnLine text s.SpanStart then
                                    wholeLines text s
                                else
                                    let next = s.GetLastToken().GetNextToken()
                                    let line = text.Lines.GetLineFromPosition s.Span.End

                                    if not (next.IsKind SyntaxKind.None) && next.SpanStart <= line.End then
                                        TextSpan.FromBounds(s.SpanStart, next.SpanStart)
                                    else
                                        s.Span

                            let removal = Some([ Suggestion.replace removed "" ], s :> SyntaxNode)

                            match kept with
                            | null -> removal
                            | :? BlockSyntax as empty when empty.Statements.Count = 0 -> removal
                            | kept ->
                                let completes =
                                    let flow = model.AnalyzeControlFlow kept
                                    flow.Succeeded && flow.EndPointIsReachable

                                let labelled =
                                    kept.DescendantNodesAndSelf()
                                    |> Seq.exists (fun d -> d :? LabeledStatementSyntax)

                                if labelled || (not (completes || lastOfBlock)) then
                                    None
                                else
                                    let outer = Text.leadingWhitespace text s.SpanStart

                                    let keptText =
                                        match kept with
                                        | :? BlockSyntax as body ->
                                            let declares =
                                                not (Text.declaredLocals body).IsEmpty
                                                || body.DescendantNodes()
                                                   |> Seq.exists (fun d -> d :? LocalFunctionStatementSyntax)

                                            if declares then
                                                Some(body.ToString())
                                            else
                                                unwrapped text body outer |> Option.orElse (Some(body.ToString()))
                                        | single when Text.multiLine text single && firstOnLine text single.SpanStart ->
                                            if Text.spansLines single then
                                                None
                                            else
                                                reindent
                                                    text
                                                    single.SpanStart
                                                    single.Span.End
                                                    (Text.leadingWhitespace text single.SpanStart)
                                                    outer
                                        | single -> Some(single.ToString())

                                    // a block kept with its braces stands where the `if` stood only
                                    // when its lines already sit at the `if`'s indentation
                                    let aligned =
                                        match kept with
                                        | :? BlockSyntax as body ->
                                            Text.leadingWhitespace text body.CloseBraceToken.SpanStart = outer
                                            || not (Text.multiLine text body)
                                        | _ -> true

                                    match keptText with
                                    | Some replacement when aligned ->
                                        Some([ Suggestion.replace s.Span replacement ], s :> SyntaxNode)
                                    | _ -> None
                        | :? ConditionalExpressionSyntax as c when obj.ReferenceEquals(c.Condition, test) ->
                            let kept = if alwaysTrue then c.WhenTrue else c.WhenFalse
                            let whole = model.GetTypeInfo(c).Type
                            let arm = model.GetTypeInfo(kept).Type

                            // the conditional's type is the arms' common one: an arm of another
                            // type (`1` beside `2.0`, a bare `null`) would change what is computed
                            if
                                not (isNull whole)
                                && not (isNull arm)
                                && SymbolEqualityComparer.Default.Equals(whole, arm)
                                // an arm typed by the other arm has no type left alone
                                && (match bare kept with
                                    | :? ThrowExpressionSyntax
                                    | :? ImplicitObjectCreationExpressionSyntax
                                    | :? CollectionExpressionSyntax
                                    | :? AnonymousFunctionExpressionSyntax
                                    | :? ConditionalExpressionSyntax
                                    | :? SwitchExpressionSyntax -> false
                                    | :? LiteralExpressionSyntax as l ->
                                        not (
                                            l.IsKind SyntaxKind.DefaultLiteralExpression
                                            || l.IsKind SyntaxKind.NullLiteralExpression
                                        )
                                    | _ -> true)
                            then
                                Some([ Suggestion.replace c.Span (kept.ToString()) ], c :> SyntaxNode)
                            else
                                None
                        | :? BinaryExpressionSyntax as chain when
                            // false is neutral in `||`, true in `&&`
                            (chain.IsKind SyntaxKind.LogicalOrExpression && not alwaysTrue)
                            || (chain.IsKind SyntaxKind.LogicalAndExpression && alwaysTrue)
                            ->
                            let other =
                                if obj.ReferenceEquals(chain.Left, test) then
                                    chain.Right
                                else
                                    chain.Left

                            if isBool (model.GetTypeInfo(other).Type) then
                                Some([ Suggestion.replace chain.Span (other.ToString()) ], chain :> SyntaxNode)
                            else
                                None
                        | _ -> None

                let fixes =
                    match rewrite with
                    | None -> []
                    // a directive in what is rewritten would lose its other half
                    | Some(_, dropped) when Text.crossesDirective dropped -> []
                    | Some(edits, dropped) ->
                        let fix = Suggestion.fix "Remove the test that never differs" NeverNullCode edits

                        // a comment in what goes would go with it
                        if Text.holdsComment dropped then
                            [ Suggestion.editorOnly fix ]
                        else
                            [ fix ]

                let always = if alwaysTrue then "true" else "false"

                Some(
                    {
                        Code = NeverNullCode
                        Message =
                            $"'{operand}' is a {typeText} and is never null: '{b}' is always {always} — if no value is possible here the type should be nullable ({typeText}?), otherwise the test is dead"
                        Span = b.Span
                        Fixes = fixes
                    }
                    |> Guards.verified model
                )
            | _ -> None
        | _ -> None)
    |> List.ofSeq

// ---- CR0197 ----

let private isTask (t: ITypeSymbol) =
    not (isNull t)
    && t.Name = "Task"
    && not (isNull t.ContainingNamespace)
    && t.ContainingNamespace.ToDisplayString() = "System.Threading.Tasks"

let private inherits (t: ITypeSymbol) (ancestor: string) =
    let rec up (x: ITypeSymbol) =
        not (isNull x) && (x.ToDisplayString() = ancestor || up x.BaseType)

    up t

/// The blocking waits of a `try` block that this `try` answers for — not
/// in a closure, not under a nested `try` — each with whether it takes a
/// token or a timeout (and so throws a cancellation or an argument
/// exception itself).
let private blockingWaits (model: SemanticModel) (block: BlockSyntax) : (ExpressionSyntax * bool) list =
    block.DescendantNodes(fun d -> not (isClosure d || d :? TryStatementSyntax))
    |> Seq.choose (fun n ->
        match n with
        | :? InvocationExpressionSyntax as inv ->
            match model.GetSymbolInfo(inv).Symbol with
            | :? IMethodSymbol as m when isTask m.ContainingType && m.Name = "Wait" && not m.IsStatic ->
                Some(inv :> ExpressionSyntax, inv.ArgumentList.Arguments.Count > 0)
            | :? IMethodSymbol as m when isTask m.ContainingType && m.Name = "WaitAll" && m.IsStatic ->
                let controlled =
                    m.Parameters
                    |> Seq.exists (fun p ->
                        p.Type.Name = "CancellationToken"
                        || p.Type.Name = "TimeSpan"
                        || p.Type.SpecialType = SpecialType.System_Int32)

                Some(inv :> ExpressionSyntax, controlled)
            | _ -> None
        | :? MemberAccessExpressionSyntax as ma when ma.Name.Identifier.ValueText = "Result" ->
            match model.GetSymbolInfo(ma).Symbol with
            | :? IPropertySymbol as p when isTask p.ContainingType -> Some(ma :> ExpressionSyntax, false)
            | _ -> None
        | _ -> None)
    |> List.ofSeq

let private wrappedCatches (tree: SyntaxTree) (model: SemanticModel) (ctx: RuleContext) : Suggestion list =
    let text = tree.GetText()
    let aggregate = model.Compilation.GetTypeByMetadataName "System.AggregateException"

    if isNull aggregate then
        []
    else
        tree.GetRoot().DescendantNodes()
        |> Seq.collect (fun n ->
            match n with
            | :? TryStatementSyntax as t when t.Catches.Count > 0 ->
                let caught (c: CatchClauseSyntax) : ITypeSymbol =
                    if isNull c.Declaration then
                        null
                    else
                        model.GetTypeInfo(c.Declaration.Type).Type

                // a clause that receives the wrapper: a catch-all, AggregateException, a base of it
                let receivesWrapper (c: CatchClauseSyntax) =
                    match caught c with
                    | null -> true
                    | ct -> inherits aggregate (ct.ToDisplayString())

                let catchesWrapperItself =
                    t.Catches
                    |> Seq.exists (fun c ->
                        match caught c with
                        | null -> false
                        | ct -> inherits ct "System.AggregateException")

                // an `async` function's wait is an `await` in waiting, which unwraps
                let inAsync =
                    t.Ancestors()
                    |> Seq.tryPick (fun a ->
                        match a with
                        | :? AnonymousFunctionExpressionSyntax as f ->
                            Some(not (f.AsyncKeyword.IsKind SyntaxKind.None))
                        | :? LocalFunctionStatementSyntax as f ->
                            Some(f.Modifiers |> Seq.exists (fun m -> m.IsKind SyntaxKind.AsyncKeyword))
                        | :? BaseMethodDeclarationSyntax as f ->
                            Some(f.Modifiers |> Seq.exists (fun m -> m.IsKind SyntaxKind.AsyncKeyword))
                        | _ -> None)
                    |> Option.defaultValue false

                let waits =
                    if catchesWrapperItself || inAsync then
                        []
                    else
                        blockingWaits model t.Block

                if waits.IsEmpty then
                    []
                else
                    let controlled = waits |> List.exists snd
                    let generalBelow = t.Catches |> Seq.exists receivesWrapper

                    // a plain wait and nothing else in the `try`: no statement of it throws the type itself
                    let onlyWaits =
                        t.Block.Statements.Count > 0
                        && t.Block.Statements
                           |> Seq.forall (fun s ->
                               match s with
                               | :? ExpressionStatementSyntax as es ->
                                   match es.Expression with
                                   | :? InvocationExpressionSyntax as inv when
                                       waits |> List.exists (fun (w, _) -> obj.ReferenceEquals(w, inv))
                                       ->
                                       (match inv.Expression with
                                        | :? MemberAccessExpressionSyntax as ma -> plainRead model ma.Expression
                                        | _ -> false)
                                       && inv.ArgumentList.Arguments
                                          |> Seq.forall (fun x -> plainRead model x.Expression)
                                   | _ -> false
                               | :? LocalDeclarationStatementSyntax as d ->
                                   d.Declaration.Variables.Count = 1
                                   && (match d.Declaration.Variables.[0].Initializer with
                                       | null -> false
                                       | init ->
                                           match init.Value with
                                           | :? MemberAccessExpressionSyntax as ma when
                                               waits |> List.exists (fun (w, _) -> obj.ReferenceEquals(w, ma))
                                               ->
                                               plainRead model ma.Expression
                                           | _ -> false)
                               | _ -> false)

                    t.Catches
                    |> Seq.choose (fun c ->
                        match caught c with
                        | null -> None
                        | ct when ct.TypeKind = TypeKind.Error || receivesWrapper c -> None
                        | ct when
                            // what the wait itself throws, unwrapped
                            inherits ct "System.ObjectDisposedException"
                            || inherits ct "System.Threading.ThreadInterruptedException"
                            || inherits ct "System.Threading.ThreadAbortException"
                            || (controlled
                                && (inherits ct "System.OperationCanceledException"
                                    || inherits ct "System.ArgumentException"))
                            ->
                            None
                        | ct ->
                            let typeText = c.Declaration.Type.ToString()

                            let binder =
                                if c.Declaration.Identifier.IsKind SyntaxKind.None then
                                    None
                                else
                                    Some c.Declaration.Identifier.ValueText

                            let previousEnd = c.GetFirstToken().GetPreviousToken().Span.End
                            let separator = text.ToString(TextSpan.FromBounds(previousEnd, c.SpanStart))

                            // filters need C# 6, the bound variable C# 7
                            let languageAllows =
                                RuleContext.languageAtLeast ctx (if binder.IsSome then 7 else 6)

                            let offers =
                                if
                                    not (isNull c.Filter)
                                    // a copied or removed clause would take half a directive pair along
                                    || Text.crossesDirective t
                                    || not (System.String.IsNullOrWhiteSpace separator)
                                    || Text.holdsCommentOrDirective c.Declaration
                                then
                                    []
                                else
                                    let scope = Text.enclosingMember t

                                    let fresh =
                                        Seq.append [ "ae" ] (Seq.initInfinite (fun i -> $"ae{i + 2}"))
                                        |> Seq.find (fun candidate ->
                                            not (Text.mentionsName candidate scope)
                                            && model.LookupSymbols(c.SpanStart, name = candidate).IsEmpty)

                                    let wrapper = Guards.typeText model c.SpanStart "System" "AggregateException"

                                    let between =
                                        text.ToString(TextSpan.FromBounds(c.Declaration.Span.End, c.Block.SpanStart))

                                    let bound =
                                        match binder with
                                        | Some name -> " " + name
                                        | None -> ""

                                    let clause (inner: string) =
                                        $"{separator}catch ({wrapper} {fresh}) when ({fresh}.{inner} is {typeText}{bound}){between}{c.Block}"

                                    // a copied `throw;` would rethrow the wrapper where the
                                    // clause's own rethrows the exception it names
                                    let rethrows =
                                        c.Block.DescendantNodes(fun d -> not (isClosure d || d :? CatchClauseSyntax))
                                        |> Seq.exists (fun d ->
                                            match d with
                                            | :? ThrowStatementSyntax as rethrow -> isNull rethrow.Expression
                                            | _ -> false)

                                    let unwrapping =
                                        if languageAllows && not rethrows then
                                            [
                                                Suggestion.fix
                                                    $"Also catch an AggregateException whose InnerException is {typeText}"
                                                    (WrappedCatchCode + ".inner")
                                                    [ Suggestion.insert c.Span.End (clause "InnerException") ]
                                                |> Suggestion.editorOnly
                                                Suggestion.fix
                                                    $"Also catch an AggregateException whose deepest exception is {typeText}"
                                                    (WrappedCatchCode + ".base")
                                                    [ Suggestion.insert c.Span.End (clause "GetBaseException()") ]
                                                |> Suggestion.editorOnly
                                            ]
                                        else
                                            []

                                    // the clause no statement of the `try` can reach
                                    let removal =
                                        if
                                            not onlyWaits
                                            || inherits ct "System.NullReferenceException"
                                            || Text.holdsCommentOrDirective c
                                        then
                                            []
                                        elif t.Catches.Count > 1 || not (isNull t.Finally) then
                                            [
                                                Suggestion.fix
                                                    $"Remove the catch ({typeText}) nothing reaches"
                                                    (WrappedCatchCode + ".remove")
                                                    [
                                                        Suggestion.replace
                                                            (TextSpan.FromBounds(previousEnd, c.Span.End))
                                                            ""
                                                    ]
                                                |> Suggestion.editorOnly
                                            ]
                                        else
                                            // the only clause: the `try` goes with it, its statements stay
                                            let declared = Text.declaredLocals t.Block

                                            let clashes =
                                                declared
                                                |> List.exists (fun name ->
                                                    scope.DescendantTokens()
                                                    |> Seq.exists (fun token ->
                                                        token.IsKind SyntaxKind.IdentifierToken
                                                        && token.ValueText = name
                                                        && not (t.Span.Contains token.Span)))

                                            if
                                                clashes
                                                || not (t.Parent :? BlockSyntax)
                                                || not (firstOnLine text t.SpanStart)
                                            then
                                                []
                                            else
                                                match
                                                    unwrapped text t.Block (Text.leadingWhitespace text t.SpanStart)
                                                with
                                                | Some statements when not (Text.holdsCommentOrDirective t) ->
                                                    [
                                                        Suggestion.fix
                                                            $"Remove the try whose catch ({typeText}) nothing reaches"
                                                            (WrappedCatchCode + ".remove")
                                                            [ Suggestion.replace t.Span statements ]
                                                        |> Suggestion.editorOnly
                                                    ]
                                                | _ -> []

                                    unwrapping @ removal

                            let general =
                                if generalBelow then
                                    " — the general catch below is what receives it"
                                else
                                    ""

                            Some
                                {
                                    Code = WrappedCatchCode
                                    Message =
                                        $"A blocking wait throws the task's exception wrapped in an AggregateException: 'catch ({typeText})' sees a {ct.Name} the try throws itself, never the task's{general}"
                                    Span = TextSpan.FromBounds(c.CatchKeyword.SpanStart, c.Declaration.Span.End)
                                    Fixes = offers
                                })
                    |> List.ofSeq
            | _ -> [])
        |> List.ofSeq

let analyze (tree: SyntaxTree) (model: SemanticModel) (ctx: RuleContext) : Suggestion list =
    assignmentTests tree model
    @ neverNulls tree model
    @ wrappedCatches tree model ctx
