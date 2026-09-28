/// CR0181 (idiom, fix): a `switch` statement whose every section returns a
/// value, assigns the one target and breaks, or throws, is a switch
/// expression (C# 8):
///
///     switch (kind)                         return kind switch
///     {                                     {
///         case Kind.A: return "a";              Kind.A => "a",
///         case Kind.B:                   →      Kind.B or Kind.C => "bc",
///         case Kind.C: return "bc";             _ => throw new ArgumentException(),
///         default: throw new …;             };
///     }
///
/// The arms read as a table and the compiler checks them for subsumption.
/// Labels become patterns - `case X:` is `X`, `case P when g:` is `P when
/// g`, several labels of one section `or` (C# 9; not with a `when` or a
/// designation), `default` is `_` and goes last (a statement's `default`
/// matches only where nothing else does, wherever it stands). A switch
/// without `default` takes the `return` right after it as its `_` arm, and
/// is left alone otherwise: a switch expression that matches nothing
/// throws where the statement fell through. Guards: each section one
/// `return e;` or `throw e;`, or `t = e;` and `break;` on one target (a
/// local or parameter not `ref`, a field by name, through `this.` or a
/// type: `o.F = x switch …` evaluates `o` before the scrutinee; a block
/// around them is fine); beside `default` only plain constants (a guard or
/// a property pattern would run in the statement, never under `_`); no
/// user-defined conversion to the governing type (a statement switches on
/// the converted value, an expression on the object); the switch alone on
/// its first and last lines; every value on
/// one line; no comment or directive inside; the arms keep the conversion
/// each took to the target (the typing check CR0002 and CR0173 share); the
/// speculative re-bind settles the rest. The same layout as CR0002's
/// expression form. Yields to IDE0066.
module CSharp.Refactor.SwitchExpression

open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax
open Microsoft.CodeAnalysis.Text

[<Literal>]
let Code = "CR0181"

type private Arm =
    | Returns of ExpressionSyntax
    | Assigns of ExpressionSyntax * ExpressionSyntax
    | Throws of ExpressionSyntax

/// A section's statements without a block around them.
let private flat (statements: SyntaxList<StatementSyntax>) =
    match List.ofSeq statements with
    | [ :? BlockSyntax as b ] -> List.ofSeq b.Statements
    | xs -> xs

let private armOf (section: SwitchSectionSyntax) =
    match flat section.Statements with
    | [ :? ReturnStatementSyntax as r ] when not (isNull r.Expression) -> Some(Returns r.Expression)
    | [ :? ThrowStatementSyntax as t ] when not (isNull t.Expression) -> Some(Throws t.Expression)
    | [ :? ExpressionStatementSyntax as es; :? BreakStatementSyntax ] ->
        match es.Expression with
        | :? AssignmentExpressionSyntax as a when a.IsKind SyntaxKind.SimpleAssignmentExpression ->
            Some(Assigns(a.Left, a.Right))
        | _ -> None
    | _ -> None

/// A label's pattern text, and whether it may join an `or`.
let private patternOf (label: SwitchLabelSyntax) =
    match label with
    | :? CaseSwitchLabelSyntax as c -> Some(c.Value.ToString(), true)
    | :? CasePatternSwitchLabelSyntax as p ->
        let binds =
            p.Pattern.DescendantNodesAndSelf()
            |> Seq.exists (fun n -> n :? SingleVariableDesignationSyntax)

        match p.WhenClause with
        | null -> Some(p.Pattern.ToString(), not binds)
        | w -> Some(p.Pattern.ToString() + " " + w.ToString(), false)
    | _ -> None

/// The governing expression as the receiver of `switch`, which binds tighter
/// than any binary operator: anything but a primary expression in parentheses.
let private scrutineeText (e: ExpressionSyntax) =
    match e with
    | :? IdentifierNameSyntax
    | :? MemberAccessExpressionSyntax
    | :? InvocationExpressionSyntax
    | :? ElementAccessExpressionSyntax
    | :? LiteralExpressionSyntax
    | :? ParenthesizedExpressionSyntax
    | :? TupleExpressionSyntax
    | :? ThisExpressionSyntax -> e.ToString()
    | _ -> "(" + e.ToString() + ")"

let analyze (tree: SyntaxTree) (model: SemanticModel) (ctx: RuleContext) : Suggestion list =
    if not (RuleContext.languageAtLeast ctx 8) then
        []
    else
        let text = tree.GetText()
        let orPatterns = RuleContext.languageAtLeast ctx 9

        tree.GetRoot().DescendantNodes()
        |> Seq.choose (fun node ->
            match node with
            | :? SwitchStatementSyntax as sw when
                sw.Sections.Count > 0
                && not (Text.holdsCommentOrDirective sw)
                // a statement switches on the value of the one user-defined implicit
                // conversion to a governing type; an expression switches on the object
                && not (
                    match model.GetOperation sw with
                    | :? Operations.ISwitchOperation as op ->
                        match op.Value with
                        | :? Operations.IConversionOperation as c -> c.Conversion.IsUserDefined
                        | _ -> false
                    | _ -> true
                )
                ->
                let sections =
                    sw.Sections
                    |> List.ofSeq
                    |> List.map (fun s ->
                        let isDefault = s.Labels |> Seq.exists (fun l -> l :? DefaultSwitchLabelSyntax)
                        let patterns = s.Labels |> List.ofSeq |> List.map patternOf
                        s, isDefault, patterns, armOf s)

                let allArms = sections |> List.forall (fun (_, _, _, a) -> a.IsSome)

                // the labels of one section as one pattern
                let label (section: SwitchSectionSyntax) (isDefault: bool) (patterns: (string * bool) option list) =
                    // beside `default` only plain constants: a guard, a property pattern or a
                    // Deconstruct would run in the statement and never under `_`
                    if isDefault then
                        if
                            section.Labels
                            |> Seq.forall (fun l -> l :? DefaultSwitchLabelSyntax || l :? CaseSwitchLabelSyntax)
                        then
                            Some "_"
                        else
                            None
                    elif patterns |> List.forall Option.isSome then
                        match patterns |> List.map Option.get with
                        | [ (p, _) ] -> Some p
                        | ps when orPatterns && ps |> List.forall snd ->
                            Some(ps |> List.map fst |> String.concat " or ")
                        | _ -> None
                    else
                        None

                let labelled =
                    sections
                    |> List.map (fun (section, isDefault, patterns, arm) ->
                        label section isDefault patterns, isDefault, arm)

                let hasDefault = sections |> List.exists (fun (_, d, _, _) -> d)

                // no default: the `return` right after the switch is the `_` arm
                let following =
                    match sw.Parent with
                    | :? BlockSyntax as b ->
                        let i = b.Statements.IndexOf sw

                        if i >= 0 && i + 1 < b.Statements.Count then
                            match b.Statements.[i + 1] with
                            | :? ReturnStatementSyntax as r when
                                not (isNull r.Expression || Text.holdsCommentOrDirective r)
                                ->
                                Some r
                            | _ -> None
                        else
                            None
                    | _ -> None

                if not allArms || labelled |> List.exists (fun (l, _, _) -> l.IsNone) then
                    None
                else
                    // `drop_throwing_default`: a `default: throw …;` beside arms naming every
                    // member of a plain enum goes, so CS8509 flags the member added later
                    let dropDefault =
                        RuleContext.knobBool ctx Code "drop_throwing_default" false
                        && (match model.GetTypeInfo(sw.Expression).Type with
                            | :? INamedTypeSymbol as t when t.TypeKind = TypeKind.Enum ->
                                let flags =
                                    t.GetAttributes()
                                    |> Seq.exists (fun a -> a.AttributeClass.Name = "FlagsAttribute")

                                let named =
                                    t.GetMembers()
                                    |> Seq.choose (fun m ->
                                        match m with
                                        | :? IFieldSymbol as f when f.HasConstantValue ->
                                            Some(System.Convert.ToDecimal f.ConstantValue)
                                        | _ -> None)
                                    |> Set.ofSeq

                                // the values the other sections name, by plain constant labels only
                                let covered =
                                    sections
                                    |> List.filter (fun (_, d, _, _) -> not d)
                                    |> List.collect (fun (s, _, _, _) ->
                                        s.Labels
                                        |> Seq.choose (fun l ->
                                            match l with
                                            | :? CaseSwitchLabelSyntax as c ->
                                                let v = model.GetConstantValue c.Value

                                                if v.HasValue && not (isNull v.Value) then
                                                    Some(System.Convert.ToDecimal v.Value)
                                                else
                                                    None
                                            | _ -> None)
                                        |> List.ofSeq)
                                    |> Set.ofList

                                not flags
                                && not named.IsEmpty
                                && Set.isSubset named covered
                                && sections
                                   |> List.exists (fun (s, d, _, arm) ->
                                       d
                                       && s.Labels.Count = 1
                                       && (match arm with
                                           | Some(Throws _) -> true
                                           | _ -> false))
                            | _ -> false)

                    let ordered =
                        (labelled |> List.filter (fun (_, d, _) -> not d))
                        @ (if dropDefault then
                               []
                           else
                               labelled |> List.filter (fun (_, d, _) -> d))
                        |> List.map (fun (l, _, a) -> l.Value, a.Value)

                    let tail, endAt =
                        match hasDefault, following with
                        | true, _ -> [], ValueSome sw.Span.End
                        | false, Some r -> [ "_", Returns r.Expression ], ValueSome r.Span.End
                        | false, None -> [], ValueNone

                    let arms = ordered @ tail

                    let kinds =
                        arms
                        |> List.choose (fun (_, a) ->
                            match a with
                            | Returns _ -> Some "return"
                            | Assigns(t, _) -> Some("assign " + t.ToString())
                            | Throws _ -> None)
                        |> List.distinct

                    let targetOk =
                        arms
                        |> List.forall (fun (_, a) ->
                            match a with
                            | Assigns(t, _) -> Guards.assignableInPlace model t
                            | Returns _
                            | Throws _ -> true)

                    let exprText a =
                        match a with
                        | Returns e
                        | Assigns(_, e) -> e.ToString()
                        | Throws e -> "throw " + e.ToString()

                    let singleLine =
                        arms |> List.forall (fun (_, a) -> not ((exprText a).Contains "\n"))

                    match kinds, endAt with
                    | [ kind ], ValueSome endAt when
                        targetOk
                        && singleLine
                        // the table is laid out a line an arm: the switch must own its first and last lines
                        && text
                            .ToString(
                                TextSpan.FromBounds(text.Lines.GetLineFromPosition(sw.SpanStart).Start, sw.SpanStart)
                            )
                            .Trim()
                            =
                            ""
                        && text.ToString(TextSpan.FromBounds(endAt, text.Lines.GetLineFromPosition(endAt).End)).Trim() =
                            ""
                        ->
                        let lead =
                            if kind = "return" then
                                "return "
                            else
                                kind.Substring "assign ".Length + " = "

                        let indent = Text.leadingWhitespace text sw.SpanStart
                        let unit = Text.indentStep text sw
                        let newline = Text.newlineAt text sw.SpanStart

                        let replacement =
                            [ $"{lead}{scrutineeText sw.Expression} switch"; indent + "{" ]
                            @ (arms |> List.map (fun (l, a) -> indent + unit + l + " => " + exprText a + ","))
                            @ [ indent + "};" ]
                            |> String.concat newline

                        let span = TextSpan.FromBounds(sw.SpanStart, endAt)
                        let edit = Suggestion.replace span replacement

                        let values =
                            arms
                            |> List.choose (fun (_, a) ->
                                match a with
                                | Returns e
                                | Assigns(_, e) -> Some e
                                | Throws _ -> None)

                        if
                            not values.IsEmpty
                            && Guards.speculativeCheck model [ edit ]
                            && Guards.armsConvertAlike model edit values
                        then
                            let message, title =
                                if kind = "return" then
                                    "Every section returns a value: return a switch expression",
                                    "Return a switch expression"
                                else
                                    "Every section assigns the target: assign a switch expression",
                                    "Assign a switch expression"

                            Some
                                {
                                    Code = Code
                                    Message = message
                                    Span = sw.SwitchKeyword.Span
                                    Fixes = [ Suggestion.fix title Code [ edit ] ]
                                }
                        else
                            None
                    | _ -> None
            | _ -> None)
        |> List.ofSeq
