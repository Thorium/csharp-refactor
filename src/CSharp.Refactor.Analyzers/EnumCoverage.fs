/// Two notes on `switch` over an enum, where a member the author forgot
/// hides behind the syntax.
///
/// CR0013 (correctness, note; editor fix): a `default:` / `_ =>` arm that
/// stands in for one or two named members. The reader cannot tell whether
/// `Blue` was meant to share the default's behaviour or was forgotten; the
/// editor's expansion names the hidden members on the arm and lets a new
/// `default` throw — C# enums are open (any underlying value converts), so
/// the throwing `default` is what the named arms need, and the sweep does
/// not apply this (expanding a default changes what an unnamed value does).
///
/// CR0014 (correctness, note; editor fix): a `switch` STATEMENT with no
/// `default` whose every arm exits the member or the loop (`return`,
/// `throw`, `continue`), with members unhandled: a value that matches
/// nothing falls out silently. The compiler warns on the expression form
/// (CS8509) and says nothing here. The editor's fix adds a throwing arm
/// per missing member (at most three).
///
/// Shared guards: the scrutinee is typed as the enum itself (not nullable,
/// where `default` also covers `null`), not `[Flags]`; every arm is a plain
/// member constant (`case Color.Red:`, `Color.Red =>`, `or` combinations),
/// no `when`, no other pattern — with a guard or a type pattern the
/// coverage is unknowable; members are counted by value, so an alias of a
/// covered value is covered.
module CSharp.Refactor.EnumCoverage

open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax
open Microsoft.CodeAnalysis.Text

[<Literal>]
let HiddenMembersCode = "CR0013"

[<Literal>]
let MissingMembersCode = "CR0014"

let private enumOf (model: SemanticModel) (scrutinee: ExpressionSyntax) : INamedTypeSymbol option =
    match model.GetTypeInfo(scrutinee).Type with
    | :? INamedTypeSymbol as t when t.TypeKind = TypeKind.Enum ->
        let flags =
            t.GetAttributes()
            |> Seq.exists (fun a ->
                not (isNull a.AttributeClass)
                && a.AttributeClass.ToDisplayString() = "System.FlagsAttribute")

        // a BCL enum's `None`/`Undefined` is what a default is for; only the
        // author's own enums, whose members change under them, count
        let inSource = t.Locations |> Seq.exists (fun l -> l.IsInSource)

        if flags || not inSource then None else Some t
    | _ -> None

/// The enum's members: name and constant value.
let private members (t: INamedTypeSymbol) =
    t.GetMembers()
    |> Seq.choose (fun m ->
        match m with
        | :? IFieldSymbol as f when f.HasConstantValue -> Some(f.Name, f.ConstantValue)
        | _ -> None)
    |> List.ofSeq

/// The value of a member of THIS enum, spelled as a constant expression.
let private memberValue (model: SemanticModel) (enumType: INamedTypeSymbol) (e: ExpressionSyntax) =
    match model.GetSymbolInfo(e).Symbol with
    | :? IFieldSymbol as f when
        f.HasConstantValue
        && SymbolEqualityComparer.Default.Equals(f.ContainingType, enumType)
        ->
        Some f.ConstantValue
    | _ -> None

let rec private armPattern (model: SemanticModel) (enumType: INamedTypeSymbol) (p: PatternSyntax) : obj list option =
    match p with
    | :? ConstantPatternSyntax as c -> memberValue model enumType c.Expression |> Option.map List.singleton
    | :? BinaryPatternSyntax as b when b.IsKind SyntaxKind.OrPattern ->
        match armPattern model enumType b.Left, armPattern model enumType b.Right with
        | Some l, Some r -> Some(l @ r)
        | _ -> None
    | :? ParenthesizedPatternSyntax as p -> armPattern model enumType p.Pattern
    | _ -> None

/// The members a label names, or None when the label is not a plain
/// member constant (a guard, another pattern, a foreign constant).
let private coveredBy (model: SemanticModel) (enumType: INamedTypeSymbol) (label: SwitchLabelSyntax) : obj list option =
    match label with
    | :? CaseSwitchLabelSyntax as c -> memberValue model enumType c.Value |> Option.map List.singleton
    | :? CasePatternSwitchLabelSyntax as c when isNull c.WhenClause -> armPattern model enumType c.Pattern
    | _ -> None

let private isDiscard (p: PatternSyntax) =
    match p with
    | :? DiscardPatternSyntax -> true
    | _ -> false

/// Names of the members whose value is not covered, in declaration order.
let private missing (enumType: INamedTypeSymbol) (covered: obj list) =
    members enumType
    |> List.filter (fun (_, value) -> not (covered |> List.exists (fun c -> c = value)))
    |> List.distinctBy snd
    |> List.map fst

let private qualified (enumType: INamedTypeSymbol) (name: string) = $"{enumType.Name}.{name}"

let private outOfRange (model: SemanticModel) (position: int) (scrutinee: ExpressionSyntax) =
    let name = Guards.typeText model position "System" "ArgumentOutOfRangeException"

    match scrutinee with
    | :? IdentifierNameSyntax as i -> $"throw new {name}(nameof({i.Identifier.ValueText}))"
    | _ -> $"throw new {name}()"

let private exits (s: StatementSyntax) =
    s :? ReturnStatementSyntax
    || s :? ThrowStatementSyntax
    || s :? ContinueStatementSyntax

let private statements (tree: SyntaxTree) (model: SemanticModel) : Suggestion list =
    let text = tree.GetText()

    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun node ->
        match node with
        | :? SwitchStatementSyntax as s ->
            match enumOf model s.Expression with
            | None -> None
            | Some enumType ->
                let sections = List.ofSeq s.Sections
                let labels = sections |> List.collect (fun sec -> List.ofSeq sec.Labels)

                let defaultLabel =
                    labels
                    |> List.tryPick (fun l ->
                        match l with
                        | :? DefaultSwitchLabelSyntax as d -> Some d
                        | _ -> None)

                let named = labels |> List.filter (fun l -> not (l :? DefaultSwitchLabelSyntax))
                let covered = named |> List.map (coveredBy model enumType)

                if covered |> List.exists Option.isNone then
                    None
                else
                    let missingNames = missing enumType (covered |> List.choose id |> List.concat)
                    let newline = SwitchRewrite.newlineAt text s.SpanStart

                    match defaultLabel with
                    | Some d when missingNames.Length >= 1 && missingNames.Length <= 2 ->
                        let section = d.Parent :?> SwitchSectionSyntax
                        let labelIndent = Text.leadingWhitespace text d.SpanStart

                        let stmtIndent =
                            if section.Statements.Count > 0 then
                                Text.leadingWhitespace text section.Statements.[0].SpanStart
                            else
                                labelIndent + "    "

                        let labelsText =
                            missingNames
                            |> List.map (fun n -> "case " + qualified enumType n + ":")
                            |> String.concat (newline + labelIndent)

                        let newDefault =
                            newline
                            + labelIndent
                            + "default:"
                            + newline
                            + stmtIndent
                            + outOfRange model s.SpanStart s.Expression
                            + ";"

                        let names = missingNames |> List.map (qualified enumType) |> String.concat ", "

                        Some
                            {
                                Code = HiddenMembersCode
                                Message = $"'default' stands in for {names}: name them, and let 'default' throw"
                                Span = d.Span
                                Fixes =
                                    [
                                        Suggestion.fix
                                            "Name the hidden members; throw on default"
                                            HiddenMembersCode
                                            [
                                                Suggestion.replace d.Span labelsText
                                                Suggestion.insert section.Span.End newDefault
                                            ]
                                        |> Suggestion.editorOnly
                                    ]
                            }
                    | None when
                        missingNames.Length >= 1
                        && missingNames.Length <= 3
                        && not sections.IsEmpty
                        && sections
                           |> List.forall (fun sec ->
                               sec.Statements.Count > 0 && exits (List.last (List.ofSeq sec.Statements)))
                        ->
                        let last = List.last sections
                        let labelIndent = Text.leadingWhitespace text last.Labels.[0].SpanStart
                        let stmtIndent = Text.leadingWhitespace text last.Statements.[0].SpanStart

                        let throwText =
                            "throw new "
                            + Guards.typeText model s.SpanStart "System" "NotImplementedException"
                            + "();"

                        let arms =
                            missingNames
                            |> List.map (fun n ->
                                newline
                                + labelIndent
                                + "case "
                                + qualified enumType n
                                + ":"
                                + newline
                                + stmtIndent
                                + throwText)
                            |> String.concat ""

                        let names = missingNames |> List.map (qualified enumType) |> String.concat ", "

                        Some
                            {
                                Code = MissingMembersCode
                                Message = $"{names} match no arm and fall out silently"
                                Span = TextSpan.FromBounds(s.SwitchKeyword.SpanStart, s.CloseParenToken.Span.End)
                                Fixes =
                                    [
                                        Suggestion.fix
                                            "Add a throwing arm per missing member"
                                            MissingMembersCode
                                            [ Suggestion.insert last.Span.End arms ]
                                        |> Suggestion.editorOnly
                                    ]
                            }
                    | _ -> None
        | _ -> None)
    |> List.ofSeq

let private expressions (tree: SyntaxTree) (model: SemanticModel) : Suggestion list =
    let text = tree.GetText()

    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun node ->
        match node with
        | :? SwitchExpressionSyntax as s ->
            match enumOf model s.GoverningExpression with
            | None -> None
            | Some enumType ->
                let arms = List.ofSeq s.Arms

                let discard =
                    arms |> List.tryFind (fun a -> isDiscard a.Pattern && isNull a.WhenClause)

                let named = arms |> List.filter (fun a -> not (isDiscard a.Pattern))

                let covered =
                    named
                    |> List.map (fun a ->
                        if isNull a.WhenClause then
                            armPattern model enumType a.Pattern
                        else
                            None)

                match discard with
                | Some d when covered |> List.forall Option.isSome ->
                    let missingNames = missing enumType (covered |> List.choose id |> List.concat)

                    if missingNames.Length >= 1 && missingNames.Length <= 2 then
                        let newline = SwitchRewrite.newlineAt text s.SpanStart
                        let armIndent = Text.leadingWhitespace text d.SpanStart

                        let patternText =
                            missingNames |> List.map (qualified enumType) |> String.concat " or "

                        let newArm =
                            ","
                            + newline
                            + armIndent
                            + "_ => "
                            + outOfRange model s.SpanStart s.GoverningExpression

                        let names = missingNames |> List.map (qualified enumType) |> String.concat ", "

                        Some
                            {
                                Code = HiddenMembersCode
                                Message = $"'_' stands in for {names}: name them, and let '_' throw"
                                Span = d.Pattern.Span
                                Fixes =
                                    [
                                        Suggestion.fix
                                            "Name the hidden members; throw on _"
                                            HiddenMembersCode
                                            [
                                                Suggestion.replace d.Pattern.Span patternText
                                                Suggestion.insert d.Expression.Span.End newArm
                                            ]
                                        |> Suggestion.editorOnly
                                    ]
                            }
                    else
                        None
                | _ -> None
        | _ -> None)
    |> List.ofSeq

let analyze (tree: SyntaxTree) (model: SemanticModel) (_ctx: RuleContext) : Suggestion list =
    statements tree model @ expressions tree model
