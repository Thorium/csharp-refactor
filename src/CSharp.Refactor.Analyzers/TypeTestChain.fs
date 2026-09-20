/// CR0003 (idiom, fix): an `if`/`else if` chain of type tests on one
/// subject, each branch casting the subject to its tested type, is a
/// `switch` over type patterns: the cast becomes the pattern variable and
/// the throwing accessor goes.
///
///     if (s is Circle) { var c = (Circle)s; return c.R; }
///     else if (s is Rect) { return ((Rect)s).W; }
///     else { throw new ArgumentException(); }
///
///     switch (s)
///     {
///         case Circle c:
///             return c.R;
///         case Rect rect:
///             return rect.W;
///         default:
///             throw new ArgumentException();
///     }
///
/// Guards: two or more links, every one `s is T`, `s is T name`, `s is
/// null` or `s is not null` (C# 9) on the SAME plain identifier; every
/// cast of `s` in a branch — `(T)s`, `s as T`, a leading `var x = (T)s;` —
/// targets that branch's own `T`, a cross-cast keeps the chain; the
/// branches never assign `s`; the binder is the declaration's name, else
/// a name from the type unused in the enclosing member; C# 7 for the
/// patterns, C# 9 for `case T:` without a binder (below it `case T _:`).
/// The shared switch guards (SwitchRewrite) apply.
module CSharp.Refactor.TypeTestChain

open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax
open Microsoft.CodeAnalysis.Text

[<Literal>]
let Code = "CR0003"

type private Test =
    /// `s is T` / `s is T name`
    | TypeTest of typeSyntax: TypeSyntax * ty: ITypeSymbol * given: string option
    /// `s is null`
    | NullTest
    /// `s is not null`
    | NotNullTest

/// The subject identifier and the test of one condition, if it is one.
let private parseTest (model: SemanticModel) (c: ExpressionSyntax) : (IdentifierNameSyntax * Test) option =
    let rec unparen (e: ExpressionSyntax) =
        match e with
        | :? ParenthesizedExpressionSyntax as p -> unparen p.Expression
        | e -> e

    let typeOf (t: TypeSyntax) =
        match model.GetTypeInfo(t).Type with
        | null -> None
        | ty when ty.TypeKind = TypeKind.Error -> None
        | ty -> Some ty

    match unparen c with
    | :? BinaryExpressionSyntax as b when b.IsKind SyntaxKind.IsExpression ->
        match b.Left, b.Right with
        | (:? IdentifierNameSyntax as s), (:? TypeSyntax as t) ->
            typeOf t |> Option.map (fun ty -> s, TypeTest(t, ty, None))
        | _ -> None
    | :? IsPatternExpressionSyntax as p ->
        match p.Expression with
        | :? IdentifierNameSyntax as s ->
            match p.Pattern with
            | :? DeclarationPatternSyntax as d ->
                match d.Designation with
                | :? SingleVariableDesignationSyntax as v ->
                    typeOf d.Type
                    |> Option.map (fun ty -> s, TypeTest(d.Type, ty, Some v.Identifier.ValueText))
                | _ -> None
            | :? TypePatternSyntax as t -> typeOf t.Type |> Option.map (fun ty -> s, TypeTest(t.Type, ty, None))
            | :? ConstantPatternSyntax as k when k.Expression.IsKind SyntaxKind.NullLiteralExpression ->
                Some(s, NullTest)
            | :? UnaryPatternSyntax as u when u.IsKind SyntaxKind.NotPattern ->
                match u.Pattern with
                | :? ConstantPatternSyntax as k when k.Expression.IsKind SyntaxKind.NullLiteralExpression ->
                    Some(s, NotNullTest)
                | _ -> None
            | _ -> None
        | _ -> None
    | _ -> None

/// A cast of the subject inside a body: the node to replace (a
/// parenthesised cast goes with its parentheses) and the target type.
let private casts
    (model: SemanticModel)
    (subject: ExpressionSyntax)
    (body: SyntaxNode)
    : (SyntaxNode * ITypeSymbol) list =
    body.DescendantNodesAndSelf()
    |> Seq.choose (fun n ->
        let target (t: TypeSyntax) =
            match model.GetTypeInfo(t).Type with
            | null -> None
            | ty -> Some ty

        match n with
        | :? CastExpressionSyntax as c when Guards.sameReference model c.Expression subject ->
            let node =
                match c.Parent with
                | :? ParenthesizedExpressionSyntax as p -> p :> SyntaxNode
                | _ -> c :> SyntaxNode

            target c.Type |> Option.map (fun ty -> node, ty)
        | :? BinaryExpressionSyntax as b when
            b.IsKind SyntaxKind.AsExpression && Guards.sameReference model b.Left subject
            ->
            match b.Right with
            | :? TypeSyntax as t ->
                let node =
                    match b.Parent with
                    | :? ParenthesizedExpressionSyntax as p -> p :> SyntaxNode
                    | _ -> b :> SyntaxNode

                target t |> Option.map (fun ty -> node, ty)
            | _ -> None
        | _ -> None)
    |> List.ofSeq

/// The leading `var x = (T)s;` / `T x = s as T;` of a body: its name and
/// the statement.
let private leadingCastDeclaration (model: SemanticModel) (subject: ExpressionSyntax) (body: StatementSyntax) =
    let first =
        match body with
        | :? BlockSyntax as b when b.Statements.Count > 0 -> Some b.Statements.[0]
        | _ -> None

    match first with
    | Some(:? LocalDeclarationStatementSyntax as d) when d.Declaration.Variables.Count = 1 ->
        let v = d.Declaration.Variables.[0]

        match v.Initializer with
        | null -> None
        | init ->
            let rec unparen (e: ExpressionSyntax) =
                match e with
                | :? ParenthesizedExpressionSyntax as p -> unparen p.Expression
                | e -> e

            match unparen init.Value with
            | :? CastExpressionSyntax as c when Guards.sameReference model c.Expression subject ->
                Some(v.Identifier.ValueText, d)
            | :? BinaryExpressionSyntax as b when
                b.IsKind SyntaxKind.AsExpression && Guards.sameReference model b.Left subject
                ->
                Some(v.Identifier.ValueText, d)
            | _ -> None
    | _ -> None

let private lowerFirst (s: string) =
    if s.Length = 0 then
        s
    elif s.ToUpperInvariant() = s then
        s.ToLowerInvariant() // `URI` → `uri`, not `uRI`
    else
        string (System.Char.ToLowerInvariant s.[0]) + s.Substring 1

/// A binder name from the type, unused in the scope and no keyword.
let private binderFor (t: TypeSyntax) (scope: SyntaxNode) (taken: Set<string>) =
    let baseName =
        match t with
        | :? IdentifierNameSyntax as i -> i.Identifier.ValueText
        | :? GenericNameSyntax as g -> g.Identifier.ValueText
        | :? QualifiedNameSyntax as q ->
            match q.Right with
            | :? GenericNameSyntax as g -> g.Identifier.ValueText
            | r -> r.ToString()
        | :? PredefinedTypeSyntax as p -> p.Keyword.ValueText
        | :? NullableTypeSyntax as n -> n.ElementType.ToString()
        | :? ArrayTypeSyntax -> "items"
        | other -> other.ToString()

    let lowered = lowerFirst baseName

    [ lowered; string lowered.[0]; lowered + "Value" ]
    |> List.filter (fun name ->
        name.Length > 0
        && SyntaxFacts.GetKeywordKind name = SyntaxKind.None
        && SyntaxFacts.GetContextualKeywordKind name = SyntaxKind.None
        && SyntaxFacts.IsValidIdentifier name)
    |> List.tryFind (fun name -> not (taken.Contains name || Text.mentionsName name scope))

let analyze (tree: SyntaxTree) (model: SemanticModel) (ctx: RuleContext) : Suggestion list =
    if ctx.LanguageVersion < LanguageVersion.CSharp7 then
        []
    else
        let text = tree.GetText()
        let bareTypePattern = ctx.LanguageVersion >= LanguageVersion.CSharp9

        tree.GetRoot().DescendantNodes()
        |> Seq.choose (fun node ->
            match node with
            | :? IfStatementSyntax as head when SwitchRewrite.isHead head ->
                let links, terminal = SwitchRewrite.chain head
                let tests = links |> List.map (fun l -> parseTest model l.Condition)

                let subject =
                    match tests with
                    | Some(s, _) :: _ -> Some s
                    | _ -> None

                let typeTests =
                    tests
                    |> List.filter (function
                        | Some(_, TypeTest _) -> true
                        | _ -> false)

                match subject with
                | Some s when
                    links.Length >= 2
                    && typeTests.Length >= 2
                    && tests
                       |> List.forall (function
                           | Some(s', _) -> s'.Identifier.ValueText = s.Identifier.ValueText
                           | None -> false)
                    // `case not null:` is C# 9
                    && (bareTypePattern
                        || not (
                            tests
                            |> List.exists (function
                                | Some(_, NotNullTest) -> true
                                | _ -> false)
                        ))
                    ->
                    let subjectText = s.Identifier.ValueText
                    let bodies = (links |> List.map (fun l -> l.Then)) @ Option.toList terminal
                    let scope = Text.enclosingMember head

                    if bodies |> List.exists (Text.assignsTo subjectText) then
                        None
                    else
                        // every branch: its label, changes and binder, or a reason to stop
                        let sections =
                            (Set.empty, List.zip links tests)
                            ||> List.mapFold (fun taken (link, test) ->
                                match test with
                                | Some(_, NullTest) ->
                                    Some
                                        {
                                            SwitchRewrite.Labels = [ "case null:" ]
                                            SwitchRewrite.Body = link.Then
                                            SwitchRewrite.Changes = []
                                            SwitchRewrite.Binders = []
                                        },
                                    taken
                                | Some(_, NotNullTest) ->
                                    Some
                                        {
                                            SwitchRewrite.Labels = [ "case not null:" ]
                                            SwitchRewrite.Body = link.Then
                                            SwitchRewrite.Changes = []
                                            SwitchRewrite.Binders = []
                                        },
                                    taken
                                | Some(_, TypeTest(typeSyntax, ty, given)) ->
                                    let bodyCasts = casts model s link.Then

                                    let crossCast =
                                        bodyCasts
                                        |> List.exists (fun (_, target) ->
                                            not (SymbolEqualityComparer.Default.Equals(target, ty)))

                                    if crossCast then
                                        None, taken
                                    else
                                        let declaration = leadingCastDeclaration model s link.Then

                                        let binder =
                                            match given, declaration with
                                            | Some name, _ -> Some name
                                            | None, Some(name, _) -> Some name
                                            | None, None when bodyCasts.IsEmpty -> None
                                            | None, None -> binderFor typeSyntax scope taken

                                        // a name is needed where a cast is read
                                        if binder.IsNone && not bodyCasts.IsEmpty then
                                            None, taken
                                        else
                                            let name = binder |> Option.defaultValue ""

                                            let removed =
                                                declaration
                                                |> Option.map (fun (_, d) ->
                                                    TextChange(Text.statementLineSpan text d, ""))
                                                |> Option.toList

                                            let replaced =
                                                bodyCasts
                                                |> List.filter (fun (n, _) ->
                                                    declaration
                                                    |> Option.forall (fun (_, d) -> not (d.Span.Contains n.Span)))
                                                |> List.map (fun (n, _) -> TextChange(n.Span, name))

                                            let label =
                                                match binder with
                                                | Some name -> "case " + typeSyntax.ToString() + " " + name + ":"
                                                | None when bareTypePattern -> "case " + typeSyntax.ToString() + ":"
                                                | None -> "case " + typeSyntax.ToString() + " _:"

                                            Some
                                                {
                                                    SwitchRewrite.Labels = [ label ]
                                                    SwitchRewrite.Body = link.Then
                                                    SwitchRewrite.Changes = removed @ replaced
                                                    SwitchRewrite.Binders = Option.toList binder
                                                },
                                            (match binder with
                                             | Some b -> taken.Add b
                                             | None -> taken)
                                | None -> None, taken)
                            |> fst

                        if sections |> List.exists Option.isNone then
                            None
                        else
                            let sections = sections |> List.choose id

                            let sections =
                                match terminal with
                                | Some body ->
                                    sections
                                    @ [
                                        {
                                            SwitchRewrite.Labels = [ "default:" ]
                                            SwitchRewrite.Body = body
                                            SwitchRewrite.Changes = []
                                            SwitchRewrite.Binders = []
                                        }
                                    ]
                                | None -> sections

                            // the binders must differ from each other
                            let binders = sections |> List.collect (fun s -> s.Binders)

                            if
                                binders.Length <> (List.distinct binders).Length
                                || SwitchRewrite.blocked head sections
                                || Guards.insideExpressionTree model head
                            then
                                None
                            else
                                let replacement =
                                    SwitchRewrite.renderStatement
                                        model
                                        text
                                        head
                                        subjectText
                                        sections
                                        (SwitchRewrite.newlineAt text head.SpanStart)

                                let edit = Suggestion.replace head.Span replacement

                                if Guards.speculativeCheck model [ edit ] then
                                    Some
                                        {
                                            Code = Code
                                            Message = "A chain of type tests with casts is a switch over type patterns"
                                            Span =
                                                TextSpan.FromBounds(
                                                    head.IfKeyword.SpanStart,
                                                    head.CloseParenToken.Span.End
                                                )
                                            Fixes = [ Suggestion.fix "Switch on the type" Code [ edit ] ]
                                        }
                                else
                                    None
                | _ -> None
            | _ -> None)
        |> List.ofSeq
