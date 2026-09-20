/// CR0029 (idiom, fix): two `Select`s in a row are one, and an identity
/// `Select` is none.
///
///     xs.Select(x => x.A).Select(a => a.B)   →  xs.Select(x => x.A.B)
///     xs.Select(x => x)                      →  xs
///
/// Both forms are lazy and per element in order, so nothing runs sooner
/// or later; the runtime already composes consecutive `Select`s, so the
/// claim is idiom, measured for parity. Guards: both calls resolve to
/// `Enumerable.Select` (an `IQueryable` chain is a query the provider
/// translates); both lambdas are expression lambdas of one parameter;
/// substituting the first body for the second's parameter duplicates
/// nothing (the parameter is used once and outside any nested lambda, or
/// the first body is a pure atom); neither call spells type arguments
/// and captures nothing (the second body never spells the first
/// parameter's name, the first body never spells the second's); the
/// substituted body is bracketed where the parameter stood under a
/// tighter operator; the identity form goes only where the receiver is
/// already typed `IEnumerable<T>` (a `List<T>` would change the static
/// type and lose the defensive copy).
module CSharp.Refactor.SelectFusion

open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax
open Microsoft.CodeAnalysis.Text

[<Literal>]
let Code = "CR0029"

let private simpleLambda (arg: ArgumentSyntax) =
    match arg.Expression with
    | :? SimpleLambdaExpressionSyntax as l when not (isNull l.ExpressionBody) ->
        Some(l.Parameter.Identifier.ValueText, l.ExpressionBody)
    | :? ParenthesizedLambdaExpressionSyntax as l when
        not (isNull l.ExpressionBody) && l.ParameterList.Parameters.Count = 1
        ->
        Some(l.ParameterList.Parameters.[0].Identifier.ValueText, l.ExpressionBody)
    | _ -> None

let private isAtom (e: ExpressionSyntax) =
    match e with
    | :? IdentifierNameSyntax
    | :? MemberAccessExpressionSyntax
    | :? LiteralExpressionSyntax
    | :? InvocationExpressionSyntax
    | :? ElementAccessExpressionSyntax
    | :? ParenthesizedExpressionSyntax -> true
    | _ -> false

/// Occurrences of the parameter in a body.
let private uses (name: string) (body: ExpressionSyntax) =
    body.DescendantNodesAndSelf()
    |> Seq.choose (fun n ->
        match n with
        | :? IdentifierNameSyntax as id when
            id.Identifier.ValueText = name
            && not (
                id.Parent :? MemberAccessExpressionSyntax
                && (id.Parent :?> MemberAccessExpressionSyntax).Name.Span = id.Span
            )
            ->
            Some id
        | _ -> None)
    |> List.ofSeq

let analyze (tree: SyntaxTree) (model: SemanticModel) (_ctx: RuleContext) : Suggestion list =
    let text = tree.GetText()

    let isSelect (inv: InvocationExpressionSyntax) =
        Linq.nameOf inv = "Select"
        && inv.ArgumentList.Arguments.Count = 1
        && (Linq.enumerableCall model inv).IsSome

    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun node ->
        match node with
        // inside an expression tree the two lambdas are the provider's to translate as written
        | :? InvocationExpressionSyntax as outer when isSelect outer && not (Text.insideExpressionTree model outer) ->
            match outer.Expression with
            | :? MemberAccessExpressionSyntax as m ->
                match m.Expression, simpleLambda outer.ArgumentList.Arguments.[0] with
                // `xs.Select(x => x)`
                | receiver, Some(p, body) when
                    (match body with
                     | :? IdentifierNameSyntax as id -> id.Identifier.ValueText = p
                     | _ -> false)
                    ->
                    let t = model.GetTypeInfo(receiver).Type

                    if
                        not (isNull t)
                        && t.OriginalDefinition.ToDisplayString() = "System.Collections.Generic.IEnumerable<T>"
                    then
                        Some
                            {
                                Code = Code
                                Message = "An identity Select changes nothing"
                                Span = TextSpan.FromBounds(receiver.Span.End, outer.Span.End)
                                Fixes =
                                    [
                                        Suggestion.fix
                                            "Drop the Select"
                                            Code
                                            [
                                                Suggestion.replace
                                                    (TextSpan.FromBounds(receiver.Span.End, outer.Span.End))
                                                    ""
                                            ]
                                    ]
                            }
                    else
                        None
                // `xs.Select(x => A).Select(y => B)`
                | (:? InvocationExpressionSyntax as inner), Some(y, b) when isSelect inner ->
                    match simpleLambda inner.ArgumentList.Arguments.[0] with
                    | Some(x, a) ->
                        let yUses = uses y b

                        // a use inside a nested lambda runs once per inner element: as good as duplicated
                        let underLambda =
                            yUses
                            |> List.exists (fun u ->
                                u.Ancestors()
                                |> Seq.takeWhile (fun n -> not (obj.ReferenceEquals(n, b)))
                                |> Seq.exists (fun n -> n :? AnonymousFunctionExpressionSyntax))

                        let duplicates = yUses.Length > 1 || underLambda

                        // explicit type arguments would be lost with the call
                        let explicitTypes =
                            (m.Name :? GenericNameSyntax)
                            || ((inner.Expression :?> MemberAccessExpressionSyntax).Name :? GenericNameSyntax)

                        let pureA = isAtom a && Guards.isPureExpression model a

                        if
                            yUses.IsEmpty
                            || explicitTypes
                            || (duplicates && not pureA)
                            || Text.mentionsName x b
                            || Text.mentionsName y a
                            || Text.holdsCommentOrDirective outer
                        then
                            None
                        else
                            let aText = if isAtom a then a.ToString() else "(" + a.ToString() + ")"

                            let fused =
                                SourceText
                                    .From(b.ToString())
                                    .WithChanges(
                                        yUses
                                        |> List.map (fun u ->
                                            TextChange(TextSpan(u.SpanStart - b.SpanStart, u.Span.Length), aText))
                                    )
                                    .ToString()

                            let replacement = $"{x} => {fused}"
                            // the inner call's receiver keeps its place; the two Selects become one
                            let innerMember = inner.Expression :?> MemberAccessExpressionSyntax

                            let span = TextSpan.FromBounds(innerMember.OperatorToken.SpanStart, outer.Span.End)
                            let edit = Suggestion.replace span ($".Select({replacement})")

                            if Guards.speculativeCheck model [ edit ] then
                                Some
                                    {
                                        Code = Code
                                        Message = "Two Selects in a row are one"
                                        Span = span
                                        Fixes = [ Suggestion.fix "Fuse the Selects" Code [ edit ] ]
                                    }
                            else
                                None
                    | None -> None
                | _ -> None
            | _ -> None
        | _ -> None)
    |> List.ofSeq
