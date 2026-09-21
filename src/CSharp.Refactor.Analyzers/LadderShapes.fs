/// The language ladder, part two: the structural rewrites.
///
/// CR0147 (idiom, fix, C# 11): a chain of length tests on one countable
/// with positional reads — `if (xs.Length == 0) … else if (xs.Length ==
/// 1) { var a = xs[0]; … } else …` — is a `switch` with list patterns:
/// `switch (xs) { case []: …; case [var a]: …; default: … }`. Guards: the
/// subject is a plain identifier typed as an array, `string`, `List<T>`,
/// `Span<T>`/`ReadOnlySpan<T>` or any type the compiler accepts in a list
/// pattern (the speculative check decides); every link compares that
/// length (`Length` or `Count`) with `==` against a small non-negative
/// integer constant; a branch reads `xs[i]` only for `i` below its own
/// length, and a `var a = xs[i];` declaration at the top of the branch
/// becomes the binder (`[var a]`), the remaining positions `_`; three
/// links at least, or two with a terminal `else`; the shared switch
/// guards of CR0002 (SwitchRewrite) apply. CR0002 stands down on the
/// chain (CR0147 is the better spelling and wins the overlap).
///
/// CR0151 (performance, fix, C# 13, API): `params T[]` on a method whose
/// body only enumerates, indexes or measures the array is `params
/// ReadOnlySpan<T>`: callers pass their arguments without an array.
/// Guards: the parameter is used only for `foreach`, `.Length`, indexing,
/// or passing to a `ReadOnlySpan<T>` parameter; never stored, returned,
/// captured by a lambda, passed to an array or `IEnumerable<T>` parameter,
/// used with LINQ, or in an `async`/iterator body (a span cannot cross an
/// await or a yield); the method is private/internal or the public shape
/// is open (a `params` type change is binary-breaking); the speculative
/// check re-binds the file.
///
/// CR0155 (idiom, fix, C# 14, off, v2): a static class holding only
/// `this T`-extension methods on one receiver type is an `extension(T x)
/// { … }` block. Off by default: the class name disappears for reflection
/// and for callers who invoked the methods statically.
///
/// CR0156 (idiom, fix, C# 15, API): a memberless `abstract record Base;`
/// whose only derived types are sealed records in the same assembly is
/// `union Base(Case1, Case2);`, with `: Base` dropped from each case.
/// Gated behind C# 15 (the numeric gate; silent below) and the syntax the
/// compilation's compiler accepts (the speculative check proves it); the
/// public shape is API.
///
/// CR0158 (idiom, note, C# 15): a `switch` expression over a native union
/// that lists every case and keeps a `_ =>` arm: the compiler now proves
/// exhaustiveness, and the arm hides a missing case.
module CSharp.Refactor.LadderShapes

open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax
open Microsoft.CodeAnalysis.Text

[<Literal>]
let ListPatternCode = "CR0147"

[<Literal>]
let ParamsSpanCode = "CR0151"

[<Literal>]
let ExtensionBlockCode = "CR0155"

[<Literal>]
let UnionCode = "CR0156"

[<Literal>]
let UnionDiscardCode = "CR0158"

let private shapeOpen (ctx: RuleContext) (s: ISymbol) =
    let rec effective (s: ISymbol) =
        match s with
        | null -> Accessibility.Public
        | s ->
            let own = s.DeclaredAccessibility
            let outer = effective s.ContainingType

            if own = Accessibility.Private || outer = Accessibility.Private then
                Accessibility.Private
            elif own = Accessibility.Internal || outer = Accessibility.Internal then
                Accessibility.Internal
            else
                own

    match effective s with
    | Accessibility.Private -> true
    | Accessibility.Internal
    | Accessibility.ProtectedAndInternal -> RuleContext.internalShapeOpen ctx
    | _ -> RuleContext.publicShapeOpen ctx

// ---- CR0147 ----

/// `xs.Length == n` / `xs.Count == n` / `n == xs.Length`: the subject and the length.
let private lengthTest (model: SemanticModel) (c: ExpressionSyntax) : (IdentifierNameSyntax * int) option =
    match c with
    | :? BinaryExpressionSyntax as b when b.IsKind SyntaxKind.EqualsExpression ->
        let sides = [ b.Left, b.Right; b.Right, b.Left ]

        sides
        |> List.tryPick (fun (subject, other) ->
            match subject, other with
            | (:? MemberAccessExpressionSyntax as m), (:? LiteralExpressionSyntax as lit) when
                (m.Name.Identifier.ValueText = "Length" || m.Name.Identifier.ValueText = "Count")
                && lit.IsKind SyntaxKind.NumericLiteralExpression
                ->
                match m.Expression, lit.Token.Value with
                | (:? IdentifierNameSyntax as id), (:? int as n) when n >= 0 && n <= 4 ->
                    match model.GetSymbolInfo(id).Symbol with
                    | :? ILocalSymbol
                    | :? IParameterSymbol -> Some(id, n)
                    | _ -> None
                | _ -> None
            | _ -> None)
    | _ -> None

/// The `xs[i]` reads in a body, with their constant indexes.
let private indexReads (model: SemanticModel) (subject: IdentifierNameSyntax) (body: SyntaxNode) =
    body.DescendantNodes()
    |> Seq.choose (fun n ->
        match n with
        | :? ElementAccessExpressionSyntax as ea when
            ea.ArgumentList.Arguments.Count = 1
            && Guards.sameReference model ea.Expression subject
            ->
            match ea.ArgumentList.Arguments.[0].Expression with
            | :? LiteralExpressionSyntax as lit ->
                match lit.Token.Value with
                | :? int as i -> Some(ea, Some i)
                | _ -> Some(ea, None)
            | _ -> Some(ea, None)
        | _ -> None)
    |> List.ofSeq

let private listPatterns (tree: SyntaxTree) (model: SemanticModel) (ctx: RuleContext) : Suggestion list =
    if not (RuleContext.languageAtLeast ctx 11) then
        []
    else
        let text = tree.GetText()

        tree.GetRoot().DescendantNodes()
        |> Seq.choose (fun n ->
            match n with
            | :? IfStatementSyntax as head when SwitchRewrite.isHead head ->
                let links, terminal = SwitchRewrite.chain head
                let tests = links |> List.map (fun l -> lengthTest model l.Condition)

                match tests with
                | Some(subject, _) :: _ when
                    tests |> List.forall Option.isSome
                    && tests |> List.forall (fun t -> Guards.sameReference model (fst t.Value) subject)
                    && (links.Length >= 3 || (links.Length = 2 && terminal.IsSome))
                    && (tests |> List.map (fun t -> snd t.Value) |> List.distinct |> List.length) = links.Length
                    // a null subject threw on `.Length`; a list pattern would quietly match nothing
                    && model.GetTypeInfo(subject).Nullability.FlowState = NullableFlowState.NotNull
                    ->
                    // each branch: reads only below its length; a leading `var a = xs[i];` is the binder
                    let sections =
                        List.zip links tests
                        |> List.map (fun (link, test) ->
                            let length = snd test.Value
                            let reads = indexReads model subject link.Then

                            let inRange =
                                reads
                                |> List.forall (fun (_, i) ->
                                    match i with
                                    | Some i -> i < length
                                    | None -> false)

                            // leading declarations `var a = xs[i];` become binders
                            let statements =
                                match link.Then with
                                | :? BlockSyntax as b -> List.ofSeq b.Statements
                                | s -> [ s ]

                            let binders =
                                statements
                                |> List.takeWhile (fun s ->
                                    match s with
                                    | :? LocalDeclarationStatementSyntax as d when
                                        d.Declaration.Variables.Count = 1 && d.Declaration.Type.IsVar
                                        ->
                                        let v = d.Declaration.Variables.[0]

                                        not (isNull v.Initializer)
                                        && (match v.Initializer.Value with
                                            | :? ElementAccessExpressionSyntax as ea ->
                                                reads |> List.exists (fun (r, _) -> obj.ReferenceEquals(r, ea))
                                            | _ -> false)
                                    | _ -> false)
                                |> List.map (fun s ->
                                    let d = s :?> LocalDeclarationStatementSyntax
                                    let v = d.Declaration.Variables.[0]
                                    let ea = v.Initializer.Value :?> ElementAccessExpressionSyntax

                                    let i =
                                        (ea.ArgumentList.Arguments.[0].Expression :?> LiteralExpressionSyntax)
                                            .Token.Value
                                        :?> int

                                    d, i, v.Identifier.ValueText)

                            // a binder's other reads of the same index stay as reads of the binder? no: keep it simple,
                            // the remaining reads stay `xs[i]` (the subject is still in scope)
                            let positions =
                                [ 0 .. length - 1 ]
                                |> List.map (fun i ->
                                    match binders |> List.tryFind (fun (_, bi, _) -> bi = i) with
                                    | Some(_, _, name) -> "var " + name
                                    | None -> "_")

                            let label = "case [" + String.concat ", " positions + "]:"

                            let changes =
                                binders
                                |> List.map (fun (d, _, _) -> TextChange(Text.statementLineSpan text d, ""))

                            inRange,
                            {
                                SwitchRewrite.Labels = [ label ]
                                SwitchRewrite.Body = link.Then
                                SwitchRewrite.Changes = changes
                                SwitchRewrite.Binders = binders |> List.map (fun (_, _, name) -> name)
                            })

                    let allInRange = sections |> List.forall fst
                    let sections = sections |> List.map snd

                    let sections =
                        match terminal with
                        | Some t ->
                            sections
                            @ [
                                {
                                    SwitchRewrite.Labels = [ "default:" ]
                                    SwitchRewrite.Body = t
                                    SwitchRewrite.Changes = []
                                    SwitchRewrite.Binders = []
                                }
                            ]
                        | None -> sections

                    if not allInRange || SwitchRewrite.blocked head sections then
                        None
                    else
                        let newline = Text.newlineAt text head.SpanStart

                        let rendered =
                            SwitchRewrite.renderStatement
                                model
                                text
                                head
                                (subject.Identifier.ValueText)
                                sections
                                newline

                        let edit = Suggestion.replace head.Span rendered

                        if Guards.speculativeCheck model [ edit ] then
                            Some
                                {
                                    Code = ListPatternCode
                                    Message =
                                        "A chain of length tests with positional reads is a switch over list patterns"
                                    Span = head.Condition.Span
                                    Fixes = [ Suggestion.fix "Use list patterns" ListPatternCode [ edit ] ]
                                }
                        else
                            None
                | _ -> None
            | _ -> None)
        |> List.ofSeq

// ---- CR0151 ----

let private paramsSpans (tree: SyntaxTree) (model: SemanticModel) (ctx: RuleContext) : Suggestion list =
    if not (RuleContext.languageAtLeast ctx 13) then
        []
    else
        tree.GetRoot().DescendantNodes()
        |> Seq.choose (fun n ->
            match n with
            | :? ParameterSyntax as p when
                p.Modifiers |> Seq.exists (fun m -> m.IsKind SyntaxKind.ParamsKeyword)
                && (p.Type :? ArrayTypeSyntax)
                ->
                let method' =
                    match p.Parent.Parent with
                    | :? MethodDeclarationSyntax as m -> Some m
                    | _ -> None

                match method', model.GetDeclaredSymbol p with
                | Some m, (:? IParameterSymbol as ps) when
                    not (
                        m.Modifiers
                        |> Seq.exists (fun k ->
                            k.IsKind SyntaxKind.AsyncKeyword
                            || k.IsKind SyntaxKind.OverrideKeyword
                            || k.IsKind SyntaxKind.VirtualKeyword
                            || k.IsKind SyntaxKind.AbstractKeyword)
                    )
                    && (not (isNull m.Body && isNull m.ExpressionBody))
                    && not (m.DescendantNodes() |> Seq.exists (fun x -> x :? YieldStatementSyntax))
                    && shapeOpen ctx (model.GetDeclaredSymbol m)
                    ->
                    let bodyNode: SyntaxNode =
                        if isNull m.Body then
                            m.ExpressionBody :> SyntaxNode
                        else
                            m.Body :> SyntaxNode

                    let uses =
                        bodyNode.DescendantNodes()
                        |> Seq.choose (fun x ->
                            match x with
                            | :? IdentifierNameSyntax as id when
                                id.Identifier.ValueText = ps.Name
                                && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(id).Symbol, ps)
                                ->
                                Some id
                            | _ -> None)
                        |> List.ofSeq

                    let simple =
                        uses
                        |> List.forall (fun id ->
                            let inLambda =
                                id.Ancestors()
                                |> Seq.takeWhile (fun a -> not (obj.ReferenceEquals(a, m)))
                                |> Seq.exists (fun a ->
                                    a :? AnonymousFunctionExpressionSyntax || a :? LocalFunctionStatementSyntax)

                            not inLambda
                            && (match id.Parent with
                                | :? MemberAccessExpressionSyntax as ma when ma.Expression.Span = id.Span ->
                                    ma.Name.Identifier.ValueText = "Length"
                                | :? ElementAccessExpressionSyntax as ea when ea.Expression.Span = id.Span ->
                                    not (
                                        match ea.Parent with
                                        | :? AssignmentExpressionSyntax as a -> a.Left.Span = ea.Span
                                        | _ -> false
                                    )
                                | :? ForEachStatementSyntax as f -> f.Expression.Span = id.Span
                                | :? ArgumentSyntax as arg ->
                                    // to a ReadOnlySpan<T> parameter only
                                    match arg.Parent.Parent with
                                    | :? InvocationExpressionSyntax as inv ->
                                        match model.GetSymbolInfo(inv).Symbol with
                                        | :? IMethodSymbol as callee ->
                                            let i = inv.ArgumentList.Arguments.IndexOf arg

                                            i < callee.Parameters.Length
                                            && callee.Parameters.[i].Type.OriginalDefinition.ToDisplayString() =
                                                "System.ReadOnlySpan<T>"
                                        | _ -> false
                                    | _ -> false
                                | _ -> false))

                    if uses.IsEmpty || not simple then
                        None
                    else
                        let elementType = (p.Type :?> ArrayTypeSyntax).ElementType.ToString()
                        let edit = Suggestion.replace p.Type.Span ($"ReadOnlySpan<{elementType}>")

                        // every call site in the file must still bind
                        if Guards.speculativeCheck model [ edit ] then
                            Some
                                {
                                    Code = ParamsSpanCode
                                    Message =
                                        "A params array the body only reads is a params ReadOnlySpan: callers pass their arguments without an array"
                                    Span = p.Span
                                    Fixes = [ Suggestion.fix "Use params ReadOnlySpan" ParamsSpanCode [ edit ] ]
                                }
                        else
                            None
                | _ -> None
            | _ -> None)
        |> List.ofSeq

// ---- CR0155 ----

let private extensionBlocks (tree: SyntaxTree) (model: SemanticModel) (ctx: RuleContext) : Suggestion list =
    if not (RuleContext.languageAtLeast ctx 14) then
        []
    else
        let text = tree.GetText()

        tree.GetRoot().DescendantNodes()
        |> Seq.choose (fun n ->
            match n with
            | :? ClassDeclarationSyntax as c when
                c.Modifiers |> Seq.exists (fun m -> m.IsKind SyntaxKind.StaticKeyword)
                && c.Members.Count > 0
                && c.AttributeLists.Count = 0
                && not (Text.holdsCommentOrDirective c)
                ->
                let methods =
                    c.Members
                    |> Seq.choose (fun m ->
                        match m with
                        | :? MethodDeclarationSyntax as md when
                            md.ParameterList.Parameters.Count > 0
                            && md.ParameterList.Parameters.[0].Modifiers
                               |> Seq.exists (fun k -> k.IsKind SyntaxKind.ThisKeyword)
                            && isNull md.TypeParameterList
                            ->
                            Some md
                        | _ -> None)
                    |> List.ofSeq

                let receiverTypes =
                    methods
                    |> List.map (fun md -> md.ParameterList.Parameters.[0].Type.ToString())
                    |> List.distinct

                let receiverNames =
                    methods
                    |> List.map (fun md -> md.ParameterList.Parameters.[0].Identifier.ValueText)
                    |> List.distinct

                match model.GetDeclaredSymbol c with
                | self when
                    methods.Length = c.Members.Count
                    && receiverTypes.Length = 1
                    && receiverNames.Length = 1
                    && shapeOpen ctx self
                    ->
                    let indent = Text.leadingWhitespace text c.SpanStart
                    let newline = Text.newlineAt text c.SpanStart
                    let unit = "    "

                    let body =
                        methods
                        |> List.map (fun md ->
                            // the receiver parameter goes; the rest of the method stays verbatim
                            let p0 = md.ParameterList.Parameters.[0]

                            let paramsText =
                                md.ParameterList.Parameters
                                |> Seq.skip 1
                                |> Seq.map (fun p -> p.ToString())
                                |> String.concat ", "

                            let modifiers =
                                md.Modifiers
                                |> Seq.filter (fun k -> not (k.IsKind SyntaxKind.StaticKeyword))
                                |> Seq.map (fun k -> k.ValueText)
                                |> String.concat " "

                            let rest = md.ToString().Substring(md.ParameterList.Span.End - md.SpanStart)

                            indent
                            + unit
                            + unit
                            + (if modifiers = "" then "" else modifiers + " ")
                            + md.ReturnType.ToString()
                            + " "
                            + md.Identifier.ValueText
                            + "("
                            + paramsText
                            + ")"
                            + rest)
                        |> String.concat (newline + newline)

                    let rendered =
                        (c.Modifiers |> Seq.map (fun k -> k.ValueText) |> String.concat " ")
                        + " class "
                        + c.Identifier.ValueText
                        + newline
                        + indent
                        + "{"
                        + newline
                        + indent
                        + unit
                        + "extension("
                        + receiverTypes.Head
                        + " "
                        + receiverNames.Head
                        + ")"
                        + newline
                        + indent
                        + unit
                        + "{"
                        + newline
                        + body
                        + newline
                        + indent
                        + unit
                        + "}"
                        + newline
                        + indent
                        + "}"

                    let edit = Suggestion.replace c.Span rendered

                    if Guards.speculativeCheck model [ edit ] then
                        Some
                            {
                                Code = ExtensionBlockCode
                                Message = "A static class of extension methods on one receiver is an extension block"
                                Span = c.Identifier.Span
                                Fixes = [ Suggestion.fix "Use an extension block" ExtensionBlockCode [ edit ] ]
                            }
                    else
                        None
                | _ -> None
            | _ -> None)
        |> List.ofSeq

// ---- CR0156 / CR0158 ----

let private unions (tree: SyntaxTree) (model: SemanticModel) (ctx: RuleContext) : Suggestion list =
    if not (RuleContext.languageAtLeast ctx 15) then
        []
    else
        let text = tree.GetText()
        let index = lazy (Index.ofCompilation model.Compilation)

        tree.GetRoot().DescendantNodes()
        |> Seq.choose (fun n ->
            match n with
            | :? RecordDeclarationSyntax as r when
                r.Modifiers |> Seq.exists (fun m -> m.IsKind SyntaxKind.AbstractKeyword)
                && r.Members.Count = 0
                && isNull r.ParameterList
                && isNull r.BaseList
                && r.AttributeLists.Count = 0
                && isNull r.TypeParameterList
                ->
                match model.GetDeclaredSymbol r with
                | null -> None
                | self when shapeOpen ctx self ->
                    let cases = Index.derivedTypesOf index.Value self |> List.distinct

                    let allSealedRecordsHere =
                        not cases.IsEmpty
                        && cases
                           |> List.forall (fun d ->
                               d.IsSealed
                               && d.IsRecord
                               && d.ContainingAssembly = model.Compilation.Assembly
                               && d.DeclaringSyntaxReferences |> Seq.forall (fun sr -> sr.SyntaxTree = tree))

                    if not allSealedRecordsHere then
                        None
                    else
                        // `: Base` dropped from each case
                        let caseEdits =
                            cases
                            |> List.collect (fun d ->
                                d.DeclaringSyntaxReferences
                                |> Seq.choose (fun sr ->
                                    match sr.GetSyntax() with
                                    | :? RecordDeclarationSyntax as rd when
                                        not (isNull rd.BaseList) && rd.BaseList.Types.Count = 1
                                        ->
                                        // from the end of the parameter list (or identifier) to the end of the base list
                                        let before =
                                            if isNull rd.ParameterList then
                                                rd.Identifier.Span.End
                                            else
                                                rd.ParameterList.Span.End

                                        Some(
                                            Suggestion.replace (TextSpan.FromBounds(before, rd.BaseList.Span.End)) ""
                                        )
                                    | _ -> None)
                                |> List.ofSeq)

                        let names = cases |> List.map (fun d -> d.Name) |> String.concat ", "

                        let modifiers =
                            r.Modifiers
                            |> Seq.filter (fun m -> not (m.IsKind SyntaxKind.AbstractKeyword))
                            |> Seq.map (fun m -> m.ValueText)
                            |> String.concat " "

                        let declaration =
                            (if modifiers = "" then "" else modifiers + " ")
                            + "union "
                            + r.Identifier.ValueText
                            + "("
                            + names
                            + ");"

                        let edits = Suggestion.replace r.Span declaration :: caseEdits

                        if Guards.speculativeCheck model edits then
                            Some
                                {
                                    Code = UnionCode
                                    Message =
                                        "A memberless abstract record with only sealed record cases is a union: the compiler proves every switch exhaustive"
                                    Span = r.Identifier.Span
                                    Fixes = [ Suggestion.fix "Make it a union" UnionCode edits ]
                                }
                        else
                            None
                | _ -> None
            | _ -> None)
        |> List.ofSeq

let private unionDiscards (tree: SyntaxTree) (model: SemanticModel) (ctx: RuleContext) : Suggestion list =
    if not (RuleContext.languageAtLeast ctx 15) then
        []
    else
        tree.GetRoot().DescendantNodes()
        |> Seq.choose (fun n ->
            match n with
            | :? SwitchExpressionSyntax as sw ->
                let t = model.GetTypeInfo(sw.GoverningExpression).Type

                // a native union: the compiler's own kind, read by name for the pinned Roslyn
                let isUnion =
                    not (isNull t)
                    && (t.GetType().Name.Contains "Union" || string t.TypeKind = "Union")

                let discard =
                    sw.Arms
                    |> Seq.tryFind (fun a -> a.Pattern :? DiscardPatternSyntax && isNull a.WhenClause)

                match discard with
                | Some arm when isUnion && sw.Arms.Count > 1 ->
                    Some(
                        Suggestion.note
                            UnionDiscardCode
                            "The compiler proves a switch over a union exhaustive: the discard arm hides a missing case"
                            arm.Span
                    )
                | _ -> None
            | _ -> None)
        |> List.ofSeq

let analyze (tree: SyntaxTree) (model: SemanticModel) (ctx: RuleContext) : Suggestion list =
    listPatterns tree model ctx
    @ paramsSpans tree model ctx
    @ extensionBlocks tree model ctx
    @ unions tree model ctx
    @ unionDiscards tree model ctx
