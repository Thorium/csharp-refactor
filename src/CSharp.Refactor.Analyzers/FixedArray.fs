/// CR0184 (idiom, fix): a private `static readonly T[]` table the code only
/// reads is an `ImmutableArray<T>` - `readonly` fixes the field, not the
/// elements, and anything holding the array can write them:
///
///     private static readonly string[] Allowed = { "a", "b" };
///     private static readonly ImmutableArray<string> Allowed = ["a", "b"];
///
/// (`ImmutableArray.Create("a", "b")` before C# 12). Reads cost the same:
/// the struct wraps the array. Guards: private, `static readonly`, one
/// declarator, initialised by an array initializer (`{ … }`, `new[] { … }`,
/// `new T[] { … }`); System.Collections.Immutable resolvable; every use in
/// the type is a plain read - an element read (not a store, `ref` or
/// `++`, not a slice), `.Length`, a `foreach` source, a LINQ call but
/// `Aggregate`/`ElementAt` (ImmutableArray's own differ, measured), or an
/// argument to a parameter typed `IEnumerable<T>`, `IReadOnlyList<T>` or
/// `IReadOnlyCollection<T>` of a method with no other overload of that
/// arity whose body in sight tests no type of it; anything else - a
/// `params object[]` or a format, which would see one object where it saw
/// the elements, `Array.*`, a copy into a local - keeps the array.
/// `Contains` is CR0023's (a set may be the better table). The `using` is
/// added where it is missing.
module CSharp.Refactor.FixedArray

open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax
open Microsoft.CodeAnalysis.Text

[<Literal>]
let Code = "CR0184"

let private readOnlyShapes =
    set
        [
            "System.Collections.Generic.IEnumerable<T>"
            "System.Collections.Generic.IReadOnlyList<T>"
            "System.Collections.Generic.IReadOnlyCollection<T>"
        ]

/// LINQ calls `ImmutableArrayExtensions` answers differently (measured):
/// `Aggregate(func)` returns default on an empty table where Enumerable
/// throws, `ElementAt` throws IndexOutOfRange for ArgumentOutOfRange.
let private rebound = set [ "Aggregate"; "ElementAt"; "Contains" ]

/// Is the reference (the field's full expression) used as a plain read?
let private plainUse (model: SemanticModel) (e: ExpressionSyntax) =
    match e.Parent with
    | :? ElementAccessExpressionSyntax as ea when ea.Expression = e ->
        // a slice `arr[1..3]` is an array before and an ImmutableArray after
        let range =
            ea.ArgumentList.Arguments
            |> Seq.exists (fun a -> a.Expression :? RangeExpressionSyntax)

        if range then
            false
        else

            match ea.Parent with
            | :? AssignmentExpressionSyntax as a when a.Left = (ea :> ExpressionSyntax) -> false
            | :? PostfixUnaryExpressionSyntax
            | :? RefExpressionSyntax -> false
            | :? PrefixUnaryExpressionSyntax as u ->
                not (
                    u.IsKind SyntaxKind.PreIncrementExpression
                    || u.IsKind SyntaxKind.PreDecrementExpression
                    || u.IsKind SyntaxKind.AddressOfExpression
                )
            | :? ArgumentSyntax as arg -> arg.RefKindKeyword.IsKind SyntaxKind.None
            | _ -> true
    | :? MemberAccessExpressionSyntax as ma when ma.Expression = e ->
        match model.GetSymbolInfo(ma).Symbol with
        | :? IPropertySymbol as p -> p.Name = "Length"
        | :? IMethodSymbol as m ->
            not (rebound.Contains m.Name)
            && m.IsExtensionMethod
            && m.ContainingType.ToDisplayString() = "System.Linq.Enumerable"
        | _ -> false
    | :? ForEachStatementSyntax as fe -> fe.Expression = e
    | :? ArgumentSyntax as arg when arg.RefKindKeyword.IsKind SyntaxKind.None && isNull arg.NameColon ->
        match arg.Parent with
        | :? ArgumentListSyntax as list ->
            match list.Parent with
            | :? InvocationExpressionSyntax
            | :? ObjectCreationExpressionSyntax ->
                match model.GetSymbolInfo(list.Parent).Symbol with
                | :? IMethodSymbol as m ->
                    let i = list.Arguments.IndexOf arg

                    // one method of that name and arity: no overload the ImmutableArray could bind instead
                    let overloads =
                        m.ContainingType.GetMembers m.Name
                        |> Seq.filter (fun o ->
                            match o with
                            | :? IMethodSymbol as om -> om.Parameters.Length = m.Parameters.Length
                            | _ -> false)
                        |> Seq.length

                    // a callee in sight that tests its parameter's type (`xs is int[]`,
                    // `xs as IList<T>`, a cast, a switch on it) sees another type after
                    let typeTested () =
                        let p = m.Parameters.[i]

                        m.DeclaringSyntaxReferences
                        |> Seq.exists (fun r ->
                            r.GetSyntax().DescendantNodes()
                            |> Seq.exists (fun n ->
                                let mentions (x: SyntaxNode) = Text.mentionsName p.Name x

                                match n with
                                | :? IsPatternExpressionSyntax as ip -> mentions ip.Expression
                                | :? BinaryExpressionSyntax as b when
                                    b.IsKind SyntaxKind.IsExpression || b.IsKind SyntaxKind.AsExpression
                                    ->
                                    mentions b.Left
                                | :? CastExpressionSyntax as c -> mentions c.Expression
                                | :? SwitchExpressionSyntax as s -> mentions s.GoverningExpression
                                | :? SwitchStatementSyntax as s -> mentions s.Expression
                                | _ -> false))

                    i < m.Parameters.Length
                    && not m.Parameters.[i].IsParams
                    && overloads = 1
                    && not (typeTested ())
                    && readOnlyShapes.Contains(m.Parameters.[i].Type.OriginalDefinition.ToDisplayString())
                | _ -> false
            | _ -> false
        | _ -> false
    | _ -> false

let analyze (tree: SyntaxTree) (model: SemanticModel) (ctx: RuleContext) : Suggestion list =
    let immutableArray =
        model.Compilation.GetTypeByMetadataName "System.Collections.Immutable.ImmutableArray`1"

    if isNull immutableArray then
        []
    else
        let collectionExpressions =
            RuleContext.languageAtLeast ctx 12
            && not (
                isNull (
                    model.Compilation.GetTypeByMetadataName "System.Runtime.CompilerServices.CollectionBuilderAttribute"
                )
            )

        tree.GetRoot().DescendantNodes()
        |> Seq.choose (fun n ->
            match n with
            | :? FieldDeclarationSyntax as f when
                f.Declaration.Variables.Count = 1
                && f.Declaration.Type :? ArrayTypeSyntax
                && (f.Declaration.Type :?> ArrayTypeSyntax).RankSpecifiers.Count = 1
                && (f.Declaration.Type :?> ArrayTypeSyntax).RankSpecifiers.[0].Rank = 1
                && f.Modifiers |> Seq.exists (fun m -> m.IsKind SyntaxKind.PrivateKeyword)
                && f.Modifiers |> Seq.exists (fun m -> m.IsKind SyntaxKind.StaticKeyword)
                && f.Modifiers |> Seq.exists (fun m -> m.IsKind SyntaxKind.ReadOnlyKeyword)
                && not (isNull f.Declaration.Variables.[0].Initializer)
                && not (Text.holdsCommentOrDirective f.Declaration)
                ->
                let arrayType = f.Declaration.Type :?> ArrayTypeSyntax

                // the braces of the initializer, and the span before them to drop
                let braces =
                    match f.Declaration.Variables.[0].Initializer.Value with
                    | :? InitializerExpressionSyntax as i -> Some(i, TextSpan(i.SpanStart, 0))
                    | :? ImplicitArrayCreationExpressionSyntax as c ->
                        Some(c.Initializer, TextSpan.FromBounds(c.SpanStart, c.Initializer.SpanStart))
                    | :? ArrayCreationExpressionSyntax as c when not (isNull c.Initializer) ->
                        Some(c.Initializer, TextSpan.FromBounds(c.SpanStart, c.Initializer.SpanStart))
                    | _ -> None

                match braces, model.GetDeclaredSymbol f.Declaration.Variables.[0] with
                | Some(init, before), (:? IFieldSymbol as field) ->
                    let uses =
                        Guards.privateMemberScope tree field
                        |> List.collect (fun root ->
                            root.DescendantNodes()
                            |> Seq.choose (fun x ->
                                match x with
                                | :? IdentifierNameSyntax as id when id.Identifier.ValueText = field.Name ->
                                    match model.GetSymbolInfo(id).Symbol with
                                    | :? IFieldSymbol as s when SymbolEqualityComparer.Default.Equals(s, field) ->
                                        // `C.Field` / `this.Field` is the reference; `Field` alone otherwise
                                        match id.Parent with
                                        | :? MemberAccessExpressionSyntax as ma when
                                            ma.Name = (id :> SimpleNameSyntax)
                                            ->
                                            Some(ma :> ExpressionSyntax)
                                        | _ -> Some(id :> ExpressionSyntax)
                                    | _ -> None
                                | _ -> None)
                            |> List.ofSeq)

                    // the field lives in one file's type: a partial part elsewhere could use it unseen
                    let oneFile = field.ContainingType.DeclaringSyntaxReferences.Length = 1

                    let trailingComma =
                        init.Expressions.SeparatorCount = init.Expressions.Count
                        && init.Expressions.Count > 0

                    // a struct element: `arr[0].X = 1` and a mutating call write the array's own
                    // element, and an ImmutableArray hands out a copy
                    let elementCopied =
                        match field.Type with
                        | :? IArrayTypeSymbol as at ->
                            let e = at.ElementType

                            e.IsValueType
                            && e.TypeKind <> TypeKind.Enum
                            && e.SpecialType = SpecialType.None
                            && not e.IsReadOnly
                        | _ -> true

                    if
                        not oneFile
                        || elementCopied
                        || uses.IsEmpty
                        || not (uses |> List.forall (plainUse model))
                        || (not collectionExpressions && trailingComma)
                    then
                        None
                    else
                        let elementType = arrayType.ElementType.ToString()

                        let opening, closing =
                            if collectionExpressions then
                                "[", "]"
                            else
                                $"ImmutableArray.Create<{elementType}>(", ")"

                        // one line: the elements rebuilt, `["a", "b"]`; several: the braces swapped, the layout kept
                        let initEdits =
                            if Text.multiLine (tree.GetText()) init then
                                [
                                    Suggestion.replace
                                        (TextSpan.FromBounds(before.Start, init.OpenBraceToken.Span.End))
                                        opening
                                    Suggestion.replace init.CloseBraceToken.Span closing
                                ]
                            else
                                let elements =
                                    init.Expressions |> Seq.map (fun e -> e.ToString()) |> String.concat ", "

                                [
                                    Suggestion.replace
                                        (TextSpan.FromBounds(before.Start, init.Span.End))
                                        (opening + elements + closing)
                                ]

                        match
                            Usings.importEdit model tree f.SpanStart "System.Collections.Immutable" "ImmutableArray"
                        with
                        | Some usingEdits ->
                            let edits =
                                usingEdits
                                @ [ Suggestion.replace arrayType.Span $"ImmutableArray<{elementType}>" ]
                                @ initEdits

                            if Guards.speculativeCheck model edits then
                                Some
                                    {
                                        Code = Code
                                        Message =
                                            "A static readonly array the code only reads: readonly fixes the field, not the elements; an ImmutableArray fixes both"
                                        Span = f.Declaration.Variables.[0].Identifier.Span
                                        Fixes = [ Suggestion.fix "Make it an ImmutableArray" Code edits ]
                                    }
                            else
                                None
                        | None -> None
                | _ -> None
            | _ -> None)
        |> List.ofSeq
