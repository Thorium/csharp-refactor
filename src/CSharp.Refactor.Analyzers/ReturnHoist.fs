/// CR0173 (idiom, fix): a `return` (or an assignment to one target) that
/// every branch of an `if` performs, on a different value, is one `return`
/// of a conditional:
///
///     if (a) return "1"; else return "2";   →  return a ? "1" : "2";
///     if (a) return "1"; return "2";        →  return a ? "1" : "2";
///     if (a) x = f(); else x = g();         →  x = a ? f() : g();
///     T x; if (a) x = f(); else x = g();    →  var x = a ? f() : g();
///
/// The last joins a bare declaration right above the `if` into one statement,
/// F#'s `let x = if a then f () else g ()`. It spells `var` only where both
/// arms are of the declared type, so the local's type never changes, and the
/// declared type otherwise; nothing turns an existing declaration into `var`.
///
/// An `if`/`else if` chain whose every link returns (or assigns the one
/// target, closed by an `else`) is a ladder, nesting on the else side only
/// so a reader walks one path:
///
///     if (n < 10) return "small";           return n < 10 ? "small"
///     else if (n < 100) return "medium";  →     : n < 100 ? "medium"
///     else return "large";                      : "large";
///
/// one line when it fits, else an arm a line, else the condition and the
/// value on lines of their own. A chain comparing one scrutinee with `==`
/// against constants is CR0002's `switch` and is left to it.
///
/// The bool-literal spellings (`return true` / `return false`) are
/// CR0001's, which returns the condition itself; this rule stands down
/// for them. Guards: each branch is exactly one statement, a `return`
/// with an expression or an assignment to the same local or parameter (not
/// `ref`) or field by name, through `this.` or a type - `o.F = c ? … : …`
/// evaluates `o` before `c` (a property setter may act, and the branches
/// ran it once each); the
/// else-less form takes the `return` that immediately follows the `if`;
/// the two values differ in text (`if (a) return x; else return x;` is a
/// different smell); no comment or directive inside is swallowed; an `if`
/// that is another `if`'s `else` is left to the chain; the result is one
/// line that fits the wrap column (a conditional over two multi-line arms
/// is no clearer than the `if`); a branch value that is itself a
/// conditional, an assignment or a lambda is parenthesised, and a `throw`
/// branch is left alone; the speculative
/// re-bind settles the conditional's typing (a natural common type, or
/// the target type from C# 9), and every arm must still convert to the
/// type it converted to before — arms of one type, a natural type equal to
/// the target, or a target-typed conditional (`object M(bool a) { if (a)
/// return 1; else return 2.0; }` would box a double; `a ? i : f` into a
/// `double` rounds the int through `float`). IDE0046 and IDE0045 offer the same
/// rewrite in the editor; where they are on, this rule yields.
module CSharp.Refactor.ReturnHoist

open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax
open Microsoft.CodeAnalysis.Text

[<Literal>]
let Code = "CR0173"

/// The one statement a branch holds: bare, or alone in a block.
let private single (s: StatementSyntax) =
    match s with
    | :? BlockSyntax as b when b.Statements.Count = 1 -> ValueSome b.Statements.[0]
    | :? BlockSyntax -> ValueNone
    | other -> ValueSome other

let private isBoolLiteral (e: ExpressionSyntax) =
    e.IsKind SyntaxKind.TrueLiteralExpression
    || e.IsKind SyntaxKind.FalseLiteralExpression

/// A branch's value as a conditional arm: the low-precedence shapes take
/// parentheses (`a ? (b ? c : d) : e` reads; `a ? b ? c : d : e` does too,
/// to the compiler, but not to anyone else).
let private armText (e: ExpressionSyntax) =
    match e with
    | :? ConditionalExpressionSyntax
    | :? AssignmentExpressionSyntax
    | :? LambdaExpressionSyntax
    | :? SwitchExpressionSyntax
    | :? QueryExpressionSyntax
    | :? ThrowExpressionSyntax -> "(" + e.ToString() + ")"
    | _ -> e.ToString()

/// The condition as the head of a conditional: an assignment or a lower
/// conditional inside it takes parentheses.
let private conditionText (c: ExpressionSyntax) =
    match c with
    | :? ConditionalExpressionSyntax
    | :? AssignmentExpressionSyntax -> "(" + c.ToString() + ")"
    | _ -> c.ToString()

let private returned (s: StatementSyntax) =
    match single s with
    | ValueSome(:? ReturnStatementSyntax as r) when not (isNull r.Expression || r.Expression :? ThrowExpressionSyntax) ->
        Some r.Expression
    | _ -> None

let private assigned (s: StatementSyntax) =
    match single s with
    | ValueSome(:? ExpressionStatementSyntax as es) ->
        match es.Expression with
        | :? AssignmentExpressionSyntax as a when a.IsKind SyntaxKind.SimpleAssignmentExpression ->
            ValueSome(a.Left, a.Right)
        | _ -> ValueNone
    | _ -> ValueNone

let private leadingComment (s: StatementSyntax) =
    s.GetLeadingTrivia()
    |> Seq.exists (fun t ->
        t.IsKind SyntaxKind.SingleLineCommentTrivia
        || t.IsKind SyntaxKind.MultiLineCommentTrivia
        || t.IsDirective)

/// The statement `offset` places from an `if` in its block.
let private sibling (offset: int) (ifs: IfStatementSyntax) =
    match ifs.Parent with
    | :? BlockSyntax as block ->
        let i = block.Statements.IndexOf ifs

        if i >= 0 && i + offset >= 0 && i + offset < block.Statements.Count then
            ValueSome block.Statements.[i + offset]
        else
            ValueNone
    | _ -> ValueNone

/// The statement right after an `if` in its block, when the `if` has no `else`.
let private following (ifs: IfStatementSyntax) = sibling 1 ifs

let private commentOrDirective (t: SyntaxTrivia) =
    t.IsDirective
    || t.IsKind SyntaxKind.SingleLineCommentTrivia
    || t.IsKind SyntaxKind.MultiLineCommentTrivia

/// The bare declaration of the assigned local right above the `if` - `T v;`,
/// one variable, no initializer, no modifier, no comment after its start (one
/// above it stays where it is) - with the type the joined declaration spells:
/// `var` where both arms are of the declared type already, so the local's type
/// is unchanged, and the declared type otherwise (`long v` over two int arms,
/// `object`, `dynamic`, a null arm).
let private bareDeclaration (model: SemanticModel) (ifs: IfStatementSyntax) (target: ExpressionSyntax) arms =
    match target, sibling -1 ifs with
    | :? IdentifierNameSyntax as name, ValueSome(:? LocalDeclarationStatementSyntax as decl) when
        decl.Modifiers.Count = 0
        && decl.UsingKeyword.IsKind SyntaxKind.None
        && decl.Declaration.Variables.Count = 1
        && isNull decl.Declaration.Variables.[0].Initializer
        && decl.Declaration.Variables.[0].Identifier.ValueText = name.Identifier.ValueText
        && not decl.Declaration.Type.IsVar
        && not (
            decl.DescendantTokens()
            |> Seq.indexed
            |> Seq.exists (fun (i, t) ->
                (i > 0 && t.LeadingTrivia |> Seq.exists commentOrDirective)
                || t.TrailingTrivia |> Seq.exists commentOrDirective)
        )
        // the condition and the arms never touch the local: it is unassigned there
        && not (Text.mentionsName name.Identifier.ValueText ifs.Condition)
        && arms
           |> List.forall (fun (a: ExpressionSyntax) -> not (Text.mentionsName name.Identifier.ValueText a))
        ->
        match model.GetDeclaredSymbol decl.Declaration.Variables.[0] with
        | :? ILocalSymbol as local ->
            let armOfLocalType (a: ExpressionSyntax) =
                match model.GetTypeInfo(a).Type with
                | null -> false
                | t -> SymbolEqualityComparer.Default.Equals(t, local.Type)

            let typeText =
                if arms |> List.forall armOfLocalType then
                    "var"
                else
                    decl.Declaration.Type.ToString()

            ValueSome(decl, typeText)
        | _ -> ValueNone
    | _ -> ValueNone

/// An empty statement right after the `if` - the `;` of `if (a) { … } else { … };` -
/// with no comment on it: it goes with the `if`, or the fix leaves `x = …;;`.
let private strayEnd (ifs: IfStatementSyntax) =
    match following ifs with
    | ValueSome(:? EmptyStatementSyntax as e) when
        not (e.GetLeadingTrivia() |> Seq.exists commentOrDirective)
        && not (e.GetTrailingTrivia() |> Seq.exists commentOrDirective)
        ->
        e.Span.End
    | _ -> ifs.Span.End

let analyze (tree: SyntaxTree) (model: SemanticModel) (ctx: RuleContext) : Suggestion list =
    let text = tree.GetText()
    let wrapAt = RuleContext.wrapColumn ctx Code

    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun node ->
        match node with
        | :? IfStatementSyntax as ifs when
            not (ifs.Parent :? ElseClauseSyntax)
            && not (Text.holdsCommentOrDirective ifs)
            && (match model.GetTypeInfo(ifs.Condition).Type with
                | null -> false
                | t -> t.SpecialType = SpecialType.System_Boolean)
            ->
            let cond = conditionText ifs.Condition

            // one line that fits: a conditional over two multi-line arms (an `Ok(new …)`
            // against a `NotFound(CreateErrorResponse(…))` of five lines) is no clearer
            // than the `if` it replaces, and the else-less form is the reader's guard
            let fits (replacement: string) =
                not (replacement.Contains "\n")
                && (Text.leadingWhitespace text ifs.SpanStart).Length + replacement.Length
                   <= wrapAt

            let offer
                (span: TextSpan)
                (arms: ExpressionSyntax list)
                (replacement: string)
                (message: string)
                (title: string)
                =
                let edit = Suggestion.replace span replacement

                if
                    fits replacement
                    && Guards.speculativeCheck model [ edit ]
                    && Guards.armsConvertAlike model edit arms
                then
                    Some
                        {
                            Code = Code
                            Message = message
                            Span = span
                            Fixes = [ Suggestion.fix title Code [ edit ] ]
                        }
                else
                    None

            // the chain form: `if … else if … else …`, every link returning or
            // assigning one target, is a ladder `c1 ? v1 : c2 ? v2 : v3` - one
            // line when it fits, else one arm a line under the statement:
            //     return c1 ? v1
            //         : c2 ? v2
            //         : v3;
            let chainForm () =
                let rec links (i: IfStatementSyntax) acc =
                    let acc = (i.Condition, i.Statement) :: acc

                    match i.Else with
                    | null -> List.rev acc, None
                    | e ->
                        match e.Statement with
                        | :? IfStatementSyntax as next -> links next acc
                        | last -> List.rev acc, Some last

                let chain, terminal = links ifs []
                let conditions = chain |> List.map fst

                let boolConditions =
                    conditions
                    |> List.forall (fun c ->
                        match model.GetTypeInfo(c).Type with
                        | null -> false
                        | t -> t.SpecialType = SpecialType.System_Boolean)

                // one scrutinee `==` constants all the way: CR0002's switch
                let rec scrutinees (c: ExpressionSyntax) =
                    match c with
                    | :? ParenthesizedExpressionSyntax as p -> scrutinees p.Expression
                    | :? BinaryExpressionSyntax as b when b.IsKind SyntaxKind.LogicalOrExpression ->
                        match scrutinees b.Left, scrutinees b.Right with
                        | Some l, Some r -> Some(l @ r)
                        | _ -> None
                    | :? BinaryExpressionSyntax as b when b.IsKind SyntaxKind.EqualsExpression ->
                        if model.GetConstantValue(b.Right).HasValue then
                            Some [ b.Left ]
                        elif model.GetConstantValue(b.Left).HasValue then
                            Some [ b.Right ]
                        else
                            None
                    | _ -> None

                // exactly the chains CR0002 takes: three comparisons at least, of a local,
                // parameter or readonly field, of a type a switch takes; the rest stay here
                let tableShaped =
                    let all = conditions |> List.map scrutinees

                    if not (all |> List.forall Option.isSome) then
                        false
                    else
                        let reads = all |> List.collect Option.get

                        let switchable (t: ITypeSymbol) =
                            let t =
                                match t with
                                | :? INamedTypeSymbol as n when
                                    n.OriginalDefinition.SpecialType = SpecialType.System_Nullable_T
                                    ->
                                    n.TypeArguments.[0]
                                | t -> t

                            t.TypeKind = TypeKind.Enum
                            || (match t.SpecialType with
                                | SpecialType.System_Boolean
                                | SpecialType.System_Char
                                | SpecialType.System_String
                                | SpecialType.System_SByte
                                | SpecialType.System_Byte
                                | SpecialType.System_Int16
                                | SpecialType.System_UInt16
                                | SpecialType.System_Int32
                                | SpecialType.System_UInt32
                                | SpecialType.System_Int64
                                | SpecialType.System_UInt64 -> true
                                | _ -> false)

                        let stable (e: ExpressionSyntax) =
                            match e, model.GetSymbolInfo(e).Symbol with
                            | :? IdentifierNameSyntax, (:? ILocalSymbol | :? IParameterSymbol) -> true
                            | :? IdentifierNameSyntax, (:? IFieldSymbol as f) -> f.IsReadOnly
                            | _ -> false

                        reads.Length >= 3
                        && (reads |> List.map (fun e -> e.ToString()) |> List.distinct |> List.length) = 1
                        && stable reads.Head
                        && (match model.GetTypeInfo(reads.Head).Type with
                            | null -> false
                            | t -> switchable t)

                let ladder (head: string) (values: ExpressionSyntax list) (last: ExpressionSyntax) =
                    let arms =
                        List.map2 (fun c v -> $"{conditionText c} ? {armText v}") conditions values

                    let oneLine = head + String.concat " : " arms + " : " + armText last + ";"

                    if fits oneLine then
                        Some oneLine
                    else
                        let own = Text.leadingWhitespace text ifs.SpanStart
                        let under = own + Text.indentStep text ifs
                        let nl = Text.newlineAt text ifs.SpanStart

                        let within (lines: string list) =
                            let widest =
                                lines
                                |> List.mapi (fun i l -> if i = 0 then own.Length + l.Length else l.Length)
                                |> List.max

                            if lines |> List.exists (fun l -> l.Contains "\n") || widest > wrapAt then
                                None
                            else
                                Some(String.concat nl lines)

                        // an arm a line: `c ? v` under `: `
                        let armPerLine =
                            (head + List.head arms)
                            :: (List.tail arms |> List.map (fun a -> under + ": " + a))
                            @ [ under + ": " + armText last + ";" ]

                        // too wide for that: the condition and the value on lines of their own
                        let partPerLine =
                            (head + conditionText (List.head conditions))
                            :: (List.zip conditions values
                                |> List.mapi (fun i (c, v) ->
                                    [
                                        if i > 0 then
                                            under + ": " + conditionText c
                                        under + "? " + armText v
                                    ])
                                |> List.concat)
                            @ [ under + ": " + armText last + ";" ]

                        match within armPerLine with
                        | Some s -> Some s
                        | None -> within partPerLine

                let offerLadder span arms replacement message title =
                    match replacement with
                    | Some replacement ->
                        let edit = Suggestion.replace span replacement

                        if
                            Guards.speculativeCheck model [ edit ]
                            && Guards.armsConvertAlike model edit arms
                        then
                            Some
                                {
                                    Code = Code
                                    Message = message
                                    Span = span
                                    Fixes = [ Suggestion.fix title Code [ edit ] ]
                                }
                        else
                            None
                    | None -> None

                if not boolConditions || tableShaped then
                    None
                // the same value on every path is another smell, not a ladder
                elif
                    (chain |> List.map (snd >> returned) |> List.forall Option.isSome)
                    && (chain
                        |> List.map (fun (_, st) -> (returned st).Value.ToString())
                        |> List.distinct
                        |> List.length)
                        =
                        1
                    && (match terminal with
                        | Some t ->
                            match returned t with
                            | Some v -> v.ToString() = (returned (snd chain.Head)).Value.ToString()
                            | None -> false
                        | None -> false)
                then
                    None
                else
                    let returns = chain |> List.map (snd >> returned)

                    let returnTerminal =
                        match terminal with
                        | Some t -> returned t |> Option.map (fun v -> v, ifs.Span.End)
                        | None ->
                            match following ifs with
                            | ValueSome next when not (leadingComment next || Text.holdsCommentOrDirective next) ->
                                returned next |> Option.map (fun v -> v, next.Span.End)
                            | _ -> None

                    let noBoolLiteral (vs: ExpressionSyntax list) =
                        vs |> List.forall (isBoolLiteral >> not)

                    match returnTerminal with
                    | Some(last, endAt) when
                        returns |> List.forall Option.isSome
                        && noBoolLiteral (last :: (returns |> List.map Option.get))
                        ->
                        let values = returns |> List.map Option.get

                        offerLadder
                            (TextSpan.FromBounds(ifs.SpanStart, endAt))
                            (values @ [ last ])
                            (ladder "return " values last)
                            "Every branch of the chain returns: return the conditional"
                            "Return the conditional"
                    | _ ->
                        let sets = chain |> List.map (snd >> assigned)

                        let terminalSet =
                            match terminal with
                            | Some t -> assigned t
                            | None -> ValueNone

                        match terminalSet with
                        | ValueSome(target, last) when
                            sets |> List.forall (fun s -> s.IsSome)
                            && sets |> List.forall (fun s -> (fst s.Value).ToString() = target.ToString())
                            && noBoolLiteral (last :: (sets |> List.map (fun s -> snd s.Value)))
                            && ((last :: (sets |> List.map (fun s -> snd s.Value)))
                                |> List.map string
                                |> List.distinct
                                |> List.length)
                                >
                                1
                            && Guards.assignableInPlace model target
                            ->
                            let values = sets |> List.map (fun s -> snd s.Value)
                            let arms = values @ [ last ]
                            let endAt = strayEnd ifs

                            let joined =
                                match bareDeclaration model ifs target arms with
                                | ValueSome(decl, typeText) when
                                    conditions
                                    |> List.forall (fun c -> not (Text.mentionsName (target.ToString()) c))
                                    ->
                                    offerLadder
                                        (TextSpan.FromBounds(decl.SpanStart, endAt))
                                        arms
                                        (ladder $"{typeText} {target} = " values last)
                                        "Every branch of the chain assigns the local declared above: declare it with the conditional"
                                        "Declare with the conditional"
                                | _ -> None

                            match joined with
                            | Some s -> Some s
                            | None ->
                                offerLadder
                                    (TextSpan.FromBounds(ifs.SpanStart, endAt))
                                    arms
                                    (ladder $"{target} = " values last)
                                    "Every branch of the chain assigns the target: assign the conditional"
                                    "Assign the conditional"
                        | _ -> None

            // the return form: the `else` returns, or the next statement does
            let returnForm =
                match returned ifs.Statement with
                | Some thenValue when not (isBoolLiteral thenValue) ->
                    let elseValue =
                        if not (isNull ifs.Else) then
                            returned ifs.Else.Statement |> Option.map (fun v -> v, ifs.Span.End)
                        else
                            match following ifs with
                            | ValueSome next when not (leadingComment next || Text.holdsCommentOrDirective next) ->
                                returned next |> Option.map (fun v -> v, next.Span.End)
                            | _ -> None

                    match elseValue with
                    | Some(elseValue, endAt) when
                        not (isBoolLiteral elseValue) && thenValue.ToString() <> elseValue.ToString()
                        ->
                        offer
                            (TextSpan.FromBounds(ifs.SpanStart, endAt))
                            [ thenValue; elseValue ]
                            $"return {cond} ? {armText thenValue} : {armText elseValue};"
                            "Both branches return: return the conditional"
                            "Return the conditional"
                    | _ -> None
                | _ -> None

            let isChain = not (isNull ifs.Else) && (ifs.Else.Statement :? IfStatementSyntax)

            match (if isChain then chainForm () else returnForm) with
            | Some s -> Some s
            | None ->
                // the assignment form: one target, both branches
                match assigned ifs.Statement with
                | ValueSome(target, thenValue) when not (isNull ifs.Else || isBoolLiteral thenValue) ->
                    match assigned ifs.Else.Statement with
                    | ValueSome(target2, elseValue) when
                        not (isBoolLiteral elseValue)
                        && target.ToString() = target2.ToString()
                        && thenValue.ToString() <> elseValue.ToString()
                        && Guards.assignableInPlace model target
                        ->
                        let arms = [ thenValue; elseValue ]
                        let conditional = $"{cond} ? {armText thenValue} : {armText elseValue};"
                        let endAt = strayEnd ifs

                        // `T v;` right above: one declaration, as F#'s `let v = if a then … else …`
                        let joined =
                            match bareDeclaration model ifs target arms with
                            | ValueSome(decl, typeText) ->
                                offer
                                    (TextSpan.FromBounds(decl.SpanStart, endAt))
                                    arms
                                    $"{typeText} {target} = {conditional}"
                                    "Both branches assign the local declared above: declare it with the conditional"
                                    "Declare with the conditional"
                            | ValueNone -> None

                        match joined with
                        | Some s -> Some s
                        | None ->
                            offer
                                (TextSpan.FromBounds(ifs.SpanStart, endAt))
                                arms
                                $"{target} = {conditional}"
                                "Both branches assign the target: assign the conditional"
                                "Assign the conditional"
                    | _ -> None
                | _ -> None
        | _ -> None)
    |> List.ofSeq
