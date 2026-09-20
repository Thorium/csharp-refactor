/// CR0028 (idiom, fix, default decided by PerfClaims): a list filled by
/// one loop and then only read is the pipeline.
///
///     var r = new List<string>();
///     foreach (var x in xs)                  →  var r = xs.Where(x => x.Ok).Select(x => x.Name).ToList();
///         if (x.Ok) r.Add(x.Name);
///
/// Guards: the list is declared empty (`new List<T>()`, `new()`, `[]`)
/// immediately before the loop, with `T` the declared element type; the
/// loop is a `foreach` over a real generic `IEnumerable<T>` whose body is
/// the one `Add`, optionally under one `if` without `else`; the condition
/// and the projection are pure through `callsOnlyCore` and mention
/// neither the list nor an assignment; the projected expression's type is
/// exactly `T` (a `List<IShape>` filled with `Circle`s would become a
/// `List<Circle>`, and an `int` into a `List<long>` would not infer);
/// after the loop the list is only read — enumerated, indexed, counted,
/// passed to a parameter typed `IEnumerable<T>`/`IReadOnlyList<T>`/
/// `List<T>` — never added to, cleared, sorted, or passed by reference; no
/// `await` or `yield`; the lambda captures no `ref struct`; no comment or
/// directive in the loop. `Where` is spelled only where there is a
/// condition, `Select` only where the projection is not the element.
/// The design expects the pipeline to lose on time (delegate calls) and
/// this rule to ship default-off (`dotnet_diagnostic.CR0028.severity` wakes it).
module CSharp.Refactor.FillLoop

open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax
open Microsoft.CodeAnalysis.Text

[<Literal>]
let Code = "CR0028"

[<Literal>]
let private listType = "System.Collections.Generic.List<T>"

let private mutators =
    set
        [
            "Add"
            "AddRange"
            "Insert"
            "InsertRange"
            "Remove"
            "RemoveAt"
            "RemoveAll"
            "RemoveRange"
            "Clear"
            "Sort"
            "Reverse"
            "TrimExcess"
            "EnsureCapacity"
        ]

/// Every use of the list after the loop is a read.
let private onlyReadAfter (model: SemanticModel) (symbol: ISymbol) (loop: StatementSyntax) =
    let scope = Text.enclosingMember loop

    scope.DescendantNodes()
    |> Seq.filter (fun n -> n.SpanStart >= loop.Span.End)
    |> Seq.forall (fun n ->
        match n with
        | :? IdentifierNameSyntax as id when
            SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(id).Symbol, symbol)
            ->
            match id.Parent with
            | :? MemberAccessExpressionSyntax as m when m.Expression.Span = id.Span ->
                match m.Parent with
                | :? InvocationExpressionSyntax -> not (mutators.Contains m.Name.Identifier.ValueText)
                | _ -> true
            | :? ElementAccessExpressionSyntax as e ->
                match e.Parent with
                | :? AssignmentExpressionSyntax as a -> a.Left.Span <> e.Span
                | _ -> true
            | :? ArgumentSyntax as a -> a.RefKindKeyword.IsKind SyntaxKind.None
            | :? AssignmentExpressionSyntax as a -> a.Left.Span <> id.Span
            | _ -> true
        | _ -> true)

let analyze (tree: SyntaxTree) (model: SemanticModel) (ctx: RuleContext) : Suggestion list =
    ignore ctx

    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun node ->
        match node with
        | :? ForEachStatementSyntax as f when
            f.Identifier.ValueText <> ""
            && not (f.Type :? TupleTypeSyntax)
            && Linq.isGenericEnumerable (model.GetTypeInfo(f.Expression).Type)
            ->
            // the declaration just before
            let declaration =
                match f.Parent with
                | :? BlockSyntax as b ->
                    let i = b.Statements.IndexOf f

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

            match declaration with
            | None -> None
            | Some(_, v) ->
                let symbol = model.GetDeclaredSymbol v :?> ILocalSymbol

                let elementType =
                    match symbol.Type with
                    | :? INamedTypeSymbol as n when n.OriginalDefinition.ToDisplayString() = listType ->
                        Some n.TypeArguments.[0]
                    | _ -> None

                let emptyConstruction =
                    match v.Initializer.Value with
                    | :? BaseObjectCreationExpressionSyntax as c ->
                        (isNull c.ArgumentList || c.ArgumentList.Arguments.Count = 0)
                        && isNull c.Initializer
                    | :? CollectionExpressionSyntax as c -> c.Elements.Count = 0
                    | _ -> false

                // the body: one Add, optionally under one if
                let single (s: StatementSyntax) =
                    match s with
                    | :? BlockSyntax as b when b.Statements.Count = 1 -> Some b.Statements.[0]
                    | :? BlockSyntax -> None
                    | s -> Some s

                let condition, addStatement =
                    match single f.Statement with
                    | Some(:? IfStatementSyntax as ifs) when isNull ifs.Else ->
                        Some ifs.Condition, single ifs.Statement
                    | other -> None, other

                let added =
                    match addStatement with
                    | Some(:? ExpressionStatementSyntax as s) ->
                        match s.Expression with
                        | :? InvocationExpressionSyntax as inv when
                            Linq.nameOf inv = "Add"
                            && inv.ArgumentList.Arguments.Count = 1
                            && (match inv.Expression with
                                | :? MemberAccessExpressionSyntax as m ->
                                    m.Expression.ToString() = v.Identifier.ValueText
                                | _ -> false)
                            ->
                            Some inv.ArgumentList.Arguments.[0].Expression
                        | _ -> None
                    | _ -> None

                match elementType, added with
                | Some t, Some projection when emptyConstruction ->
                    let loopVar = f.Identifier.ValueText
                    let listName = v.Identifier.ValueText

                    let projectedType = model.GetTypeInfo(projection).Type

                    let pureParts =
                        Guards.callsOnlyCore model projection
                        && (condition |> Option.forall (Guards.callsOnlyCore model))

                    let mentionsList (n: SyntaxNode) = Text.mentionsName listName n

                    let capturesRefStruct (n: SyntaxNode) =
                        n.DescendantNodesAndSelf()
                        |> Seq.exists (fun x ->
                            match x with
                            | :? IdentifierNameSyntax as id ->
                                match model.GetTypeInfo(id).Type with
                                | null -> false
                                | t -> t.IsRefLikeType
                            | _ -> false)

                    if
                        not pureParts
                        || isNull projectedType
                        || not (SymbolEqualityComparer.Default.Equals(projectedType, t))
                        || mentionsList projection
                        || (condition |> Option.exists mentionsList)
                        || capturesRefStruct f.Statement
                        || not (onlyReadAfter model symbol f)
                        || Text.holdsCommentOrDirective f
                        || f.Statement.DescendantNodes()
                           |> Seq.exists (fun n -> n :? AwaitExpressionSyntax || n :? YieldStatementSyntax)
                    then
                        None
                    else
                        let source = f.Expression.ToString()

                        let whereText =
                            match condition with
                            | Some c -> ".Where(" + loopVar + " => " + c.ToString() + ")"
                            | None -> ""

                        let selectText =
                            match projection with
                            | :? IdentifierNameSyntax as id when id.Identifier.ValueText = loopVar -> ""
                            | p -> ".Select(" + loopVar + " => " + p.ToString() + ")"

                        let expression = source + whereText + selectText + ".ToList()"

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
                                        Code = Code
                                        Message = "A list filled by one loop and then only read is the pipeline"
                                        Span =
                                            TextSpan.FromBounds(f.ForEachKeyword.SpanStart, f.CloseParenToken.Span.End)
                                        Fixes = [ Suggestion.fix "Build it as a pipeline" Code edits ]
                                    }
                            else
                                None
                | _ -> None
        | _ -> None)
    |> List.ofSeq
