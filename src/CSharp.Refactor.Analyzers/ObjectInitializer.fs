/// CR0179 (idiom, fix): a local constructed and then set, member by member,
/// in the statements straight after is one object initializer:
///
///     var o = new Order();              var o = new Order()
///     o.Id = id;                   →    {
///     o.Total = total;                      Id = id,
///                                           Total = total
///                                       };
///
/// The same assignments in the same order, run on the new object before it
/// has a name: the object reads as constructed, not assembled, and CR0083
/// then sees setters used only while constructing. FR0140 is the F# twin.
/// Guards: a declaration of one local (no `using`, no `const`) initialised by
/// `new T(…)` or `new(…)` with no initializer of its own, the local of the
/// created type (a base-typed local could resolve a hidden member
/// differently); only the uninterrupted run of `o.P = value;` right after it
/// folds - anything between could observe the half-built object; each P an
/// instance field or property of the object set directly (not `o.A.B`, not an
/// indexer, not `+=`), none twice; no value mentions the local, declares a
/// variable (`out var`, a pattern) or spans lines; no comment or directive on
/// a folded statement or after the construction; no `goto` or label in the
/// member (a jump back over the declaration reuses the captured variable).
/// One line when it fits the wrap column, else one member per line under
/// the declaration's indent where the declaration and the last set own
/// their lines. The speculative re-bind settles
/// the rest (a read-only member, accessibility). Yields to IDE0017.
module CSharp.Refactor.ObjectInitializer

open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax
open Microsoft.CodeAnalysis.Text

[<Literal>]
let Code = "CR0179"

let private commentOrDirective (t: SyntaxTrivia) =
    t.IsDirective
    || t.IsKind SyntaxKind.SingleLineCommentTrivia
    || t.IsKind SyntaxKind.MultiLineCommentTrivia

/// The statement list a statement sits in: a block's or a switch section's.
let private siblings (s: StatementSyntax) =
    match s.Parent with
    | :? BlockSyntax as b -> ValueSome b.Statements
    | :? SwitchSectionSyntax as sec -> ValueSome sec.Statements
    | _ -> ValueNone

/// The member an `o.P = value;` statement sets on the local, and the value.
let private memberSet (model: SemanticModel) (local: ILocalSymbol) (s: StatementSyntax) =
    match s with
    | :? ExpressionStatementSyntax as es when not (s.DescendantTrivia() |> Seq.exists commentOrDirective) ->
        match es.Expression with
        | :? AssignmentExpressionSyntax as a when a.IsKind SyntaxKind.SimpleAssignmentExpression ->
            match a.Left with
            | :? MemberAccessExpressionSyntax as ma when
                ma.IsKind SyntaxKind.SimpleMemberAccessExpression
                && (ma.Name :? IdentifierNameSyntax)
                ->
                match ma.Expression with
                | :? IdentifierNameSyntax as id when
                    SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(id).Symbol, local)
                    ->
                    let settable =
                        match model.GetSymbolInfo(ma.Name).Symbol with
                        | :? IPropertySymbol as p -> not (p.IsStatic || p.IsIndexer)
                        | :? IFieldSymbol as f -> not f.IsStatic
                        | _ -> false

                    let value = a.Right
                    let name = local.Name

                    if
                        settable
                        && not (Text.mentionsName name value)
                        && not (value.ToString().Contains "\n")
                        && not (
                            value.DescendantNodesAndSelf()
                            |> Seq.exists (fun n ->
                                n :? DeclarationExpressionSyntax || n :? SingleVariableDesignationSyntax)
                        )
                    then
                        ValueSome(ma.Name.Identifier.ValueText, value)
                    else
                        ValueNone
                | _ -> ValueNone
            | _ -> ValueNone
        | _ -> ValueNone
    | _ -> ValueNone

let analyze (tree: SyntaxTree) (model: SemanticModel) (ctx: RuleContext) : Suggestion list =
    let text = tree.GetText()
    let wrapAt = RuleContext.wrapColumn ctx Code

    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun node ->
        match node with
        | :? LocalDeclarationStatementSyntax as decl when
            decl.Modifiers.Count = 0
            && decl.UsingKeyword.IsKind SyntaxKind.None
            && decl.Declaration.Variables.Count = 1
            && not (isNull decl.Declaration.Variables.[0].Initializer)
            ->
            match decl.Declaration.Variables.[0].Initializer.Value, siblings decl with
            | :? BaseObjectCreationExpressionSyntax as creation, ValueSome statements when
                isNull creation.Initializer
                // a comment after the construction sits in the folded range
                && not (
                    text.ToString(TextSpan.FromBounds(creation.Span.End, decl.FullSpan.End)).Contains "//"
                    || text.ToString(TextSpan.FromBounds(creation.Span.End, decl.FullSpan.End)).Contains "/*"
                )
                // a `goto` back over the declaration reuses the one captured variable: a
                // closure made on the last pass sees the new object only while it is assembled
                && not (
                    (Text.enclosingMember decl).DescendantNodes()
                    |> Seq.exists (fun n -> n :? GotoStatementSyntax || n :? LabeledStatementSyntax)
                )
                ->
                match model.GetDeclaredSymbol decl.Declaration.Variables.[0] with
                | :? ILocalSymbol as local when
                    SymbolEqualityComparer.Default.Equals(model.GetTypeInfo(creation).Type, local.Type)
                    ->
                    let start = statements.IndexOf decl

                    // the uninterrupted run of distinct member sets right after
                    let run =
                        seq { start + 1 .. statements.Count - 1 }
                        |> Seq.map (fun i -> statements.[i], memberSet model local statements.[i])
                        |> Seq.takeWhile (fun (_, set) -> set.IsSome)
                        |> Seq.map (fun (s, set) -> s, set.Value)
                        |> Seq.fold
                            (fun (acc, seen: Set<string>, stopped) (s, (name, value)) ->
                                if stopped || seen.Contains name then
                                    acc, seen, true
                                else
                                    (s, name, value) :: acc, seen.Add name, false)
                            ([], Set.empty, false)
                        |> fun (acc, _, _) -> List.rev acc

                    match run with
                    | [] -> None
                    | _ ->
                        let last, _, _ = List.last run
                        let span = TextSpan.FromBounds(creation.Span.End, last.Span.End)
                        let members = run |> List.map (fun (_, name, value) -> $"{name} = {value}")
                        let own = Text.leadingWhitespace text decl.SpanStart
                        let head = text.ToString(TextSpan.FromBounds(decl.SpanStart, creation.Span.End))
                        let oneLine = " { " + String.concat ", " members + " };"

                        // the declaration and the last set each alone on their lines: a one-line
                        // block (`{ var o = new T(); o.A = 1; … }`) takes the one-line form or none
                        let ownLines =
                            let first = text.Lines.GetLineFromPosition decl.SpanStart
                            let lastLine = text.Lines.GetLineFromPosition last.Span.End

                            text.ToString(TextSpan.FromBounds(first.Start, decl.SpanStart)).Trim() = ""
                            && text.ToString(TextSpan.FromBounds(last.Span.End, lastLine.End)).Trim() = ""

                        let replacement =
                            if own.Length + head.Length + oneLine.Length <= wrapAt then
                                Some oneLine
                            elif ownLines then
                                let nl = Text.newlineAt text decl.SpanStart
                                let inner = own + Text.indentStep text decl

                                Some(
                                    nl
                                    + own
                                    + "{"
                                    + nl
                                    + (members |> List.map (fun m -> inner + m) |> String.concat ("," + nl))
                                    + nl
                                    + own
                                    + "};"
                                )
                            else
                                None

                        let edit = Suggestion.replace span (defaultArg replacement "")

                        if replacement.IsSome && Guards.speculativeCheck model [ edit ] then
                            Some
                                {
                                    Code = Code
                                    Message =
                                        "The new object is set up member by member straight after: an object initializer constructs it in one expression"
                                    Span = decl.Declaration.Variables.[0].Identifier.Span
                                    Fixes = [ Suggestion.fix "Use an object initializer" Code [ edit ] ]
                                }
                        else
                            None
                | _ -> None
            | _ -> None
        | _ -> None)
    |> List.ofSeq
