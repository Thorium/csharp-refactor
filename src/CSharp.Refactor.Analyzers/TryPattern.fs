/// CR0183 (idiom, fix): a private `bool TryX(…, out T value)` whose callers
/// only test it returns the value or null, and the callers match it:
///
///     private bool TryFind(string k, out Order o)   private Order? TryFind(string k)
///     {                                             {
///         if (_d.Count > 0) { o = _d[k]; return true; }  if (_d.Count > 0) { return _d[k]; }
///         o = null;                                      return null;
///         return false;                               }
///     }
///     if (TryFind(k, out var o)) Use(o);            if (TryFind(k) is { } o) Use(o);
///     if (!TryFind(k, out var o)) return;           if (TryFind(k) is not { } o) return;
///
/// One value in, one out, and no out-parameter plumbing - the F# shape of a
/// `'T option` return. Guards: a private method of a type declared in one
/// file (every caller is in it), not virtual, override, abstract, partial,
/// extern, async or an interface implementation, with a block body; one
/// `out` parameter, the last; `T` not already nullable (`string?`, `int?`)
/// and not a type parameter; every `return` is `return true;` or `return
/// false;`, each `return true;` right after `value = e;` in its block, with
/// `e` provably not null (a value type, a `new`, a literal, `this`,
/// `x ?? throw …`; not a flow state, which calls an array element, a
/// dictionary value or a nullable-oblivious call not null, nor a `!`) - a
/// true with a null value would read as false; the out parameter is
/// otherwise only set, at the top of the body, to a value with no effect -
/// `null`, `default`, a constant - which only the false path saw and no
/// caller reads, never read or handed on; no other member of the name in
/// the type or its bases, whose calls the new signature could take; every reference to the method
/// is a call whose last argument is `out var x`, `out T x` or `out _`, used
/// as a condition - of an `if` or `while`, an operand of `&&`/`||`, a
/// conditional's test, or under `!` (C# 9 for `is not`); no call inside the
/// method itself. The speculative re-bind catches a caller reading `x` where
/// the pattern leaves it unassigned (after the `if`, on the false path).
module CSharp.Refactor.TryPattern

open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax
open Microsoft.CodeAnalysis.Text

[<Literal>]
let Code = "CR0183"

/// The statements of the method body proper: not inside a lambda or a local function.
let private ownNodes (body: BlockSyntax) =
    body.DescendantNodes(fun n -> not (n :? AnonymousFunctionExpressionSyntax || n :? LocalFunctionStatementSyntax))

/// A value with no effect to lose: `null`, `default`, a constant (with or
/// without `!`) - what a Try-method sets its out parameter to for the false
/// path, which no caller reads.
let private isDefaultValue (model: SemanticModel) (e: ExpressionSyntax) =
    let e =
        match e with
        | :? PostfixUnaryExpressionSyntax as u when u.IsKind SyntaxKind.SuppressNullableWarningExpression -> u.Operand
        | e -> e

    e.IsKind SyntaxKind.NullLiteralExpression
    || e.IsKind SyntaxKind.DefaultLiteralExpression
    || e :? DefaultExpressionSyntax
    || model.GetConstantValue(e).HasValue

/// `name = e;` - the assigned value.
let private setsOut (name: string) (s: StatementSyntax) =
    match s with
    | :? ExpressionStatementSyntax as es ->
        match es.Expression with
        | :? AssignmentExpressionSyntax as a when
            a.IsKind SyntaxKind.SimpleAssignmentExpression
            && (match a.Left with
                | :? IdentifierNameSyntax as id -> id.Identifier.ValueText = name
                | _ -> false)
            ->
            Some a.Right
        | _ -> None
    | _ -> None

let private previous (s: StatementSyntax) =
    match s.Parent with
    | :? BlockSyntax as b ->
        let i = b.Statements.IndexOf s
        if i > 0 then Some b.Statements.[i - 1] else None
    | _ -> None

/// A value that is not null, where nullable analysis cannot say.
let rec private obviouslyNotNull (e: ExpressionSyntax) =
    e :? BaseObjectCreationExpressionSyntax
    || (match e with
        | :? BinaryExpressionSyntax as b when b.IsKind SyntaxKind.CoalesceExpression ->
            b.Right :? ThrowExpressionSyntax || obviouslyNotNull b.Right
        | :? ParenthesizedExpressionSyntax as p -> obviouslyNotNull p.Expression
        | _ -> false)
    || e :? ArrayCreationExpressionSyntax
    || e :? ImplicitArrayCreationExpressionSyntax
    || e :? InterpolatedStringExpressionSyntax
    || e :? ThisExpressionSyntax
    || e :? TypeOfExpressionSyntax
    || e.IsKind SyntaxKind.StringLiteralExpression

let analyze (tree: SyntaxTree) (model: SemanticModel) (ctx: RuleContext) : Suggestion list =
    let text = tree.GetText()
    let isNot = RuleContext.languageAtLeast ctx 9

    if not (RuleContext.languageAtLeast ctx 8) then
        []
    else
        tree.GetRoot().DescendantNodes()
        |> Seq.choose (fun node ->
            match node with
            | :? MethodDeclarationSyntax as md when
                not (isNull md.Body)
                && md.ParameterList.Parameters.Count >= 1
                && md.Modifiers |> Seq.exists (fun m -> m.IsKind SyntaxKind.PrivateKeyword)
                && not (
                    md.Modifiers
                    |> Seq.exists (fun m ->
                        m.IsKind SyntaxKind.PartialKeyword
                        || m.IsKind SyntaxKind.ExternKeyword
                        || m.IsKind SyntaxKind.AsyncKeyword)
                )
                ->
                match model.GetDeclaredSymbol md with
                | m when
                    not (isNull m)
                    && m.ReturnType.SpecialType = SpecialType.System_Boolean
                    && not m.IsVirtual
                    && not m.IsOverride
                    && not m.IsAbstract
                    && m.ExplicitInterfaceImplementations.IsEmpty
                    && m.ContainingType.DeclaringSyntaxReferences.Length = 1
                    // another `TryX` in the type or a base: the new signature could take its calls
                    && (let rec others (t: INamedTypeSymbol) =
                            not (isNull t)
                            && ((t.GetMembers m.Name
                                 |> Seq.exists (fun o -> not (SymbolEqualityComparer.Default.Equals(o, m))))
                                || others t.BaseType)

                        not (others m.ContainingType))
                    && (m.Parameters |> Seq.filter (fun p -> p.RefKind <> RefKind.None) |> Seq.length) = 1
                    && m.Parameters.[m.Parameters.Length - 1].RefKind = RefKind.Out
                    ->
                    let outParam = m.Parameters.[m.Parameters.Length - 1]
                    let t = outParam.Type
                    let name = outParam.Name

                    let tOk =
                        t.TypeKind <> TypeKind.TypeParameter
                        && t.TypeKind <> TypeKind.Error
                        && not (
                            t.IsValueType
                            && t.OriginalDefinition.SpecialType = SpecialType.System_Nullable_T
                        )
                        && not (not t.IsValueType && t.NullableAnnotation = NullableAnnotation.Annotated)

                    let annotations =
                        model.GetNullableContext(md.SpanStart).HasFlag NullableContext.AnnotationsEnabled

                    let body = md.Body

                    let returns =
                        ownNodes body
                        |> Seq.choose (fun n ->
                            match n with
                            | :? ReturnStatementSyntax as r -> Some r
                            | _ -> None)
                        |> List.ofSeq

                    // each return with the statement it takes along: (return, value set right before it)
                    let shaped =
                        returns
                        |> List.map (fun r ->
                            match r.Expression with
                            | null -> None
                            | e when e.IsKind SyntaxKind.TrueLiteralExpression ->
                                match
                                    previous r
                                    |> Option.bind (fun p -> setsOut name p |> Option.map (fun v -> p, v))
                                with
                                | Some(p, v) -> Some(r, true, Some(p, v))
                                | None -> None
                            | e when e.IsKind SyntaxKind.FalseLiteralExpression ->
                                match
                                    previous r
                                    |> Option.bind (fun p -> setsOut name p |> Option.map (fun v -> p, v))
                                with
                                | Some(p, v) when isDefaultValue model v -> Some(r, false, Some(p, v))
                                | _ -> Some(r, false, None)
                            | _ -> None)

                    // a `!` asserts, it does not prove: behind one only an obvious value counts
                    let notNull (v: ExpressionSyntax) =
                        match v with
                        | :? PostfixUnaryExpressionSyntax as u when
                            u.IsKind SyntaxKind.SuppressNullableWarningExpression
                            ->
                            t.IsValueType || obviouslyNotNull u.Operand
                        // the flow state is no proof: an array element, a dictionary value or
                        // a nullable-oblivious call is "not null" to the analysis and null at run time
                        | v -> t.IsValueType || obviouslyNotNull v

                    let taken =
                        shaped
                        |> List.choose id
                        |> List.choose (fun (_, _, p) -> p |> Option.map fst)
                        |> System.Collections.Generic.HashSet<StatementSyntax>

                    // every other mention of the out parameter: a `= null`/`= default` statement, dropped
                    let initialisers, strayMention =
                        let mentions =
                            ownNodes body
                            |> Seq.choose (fun n ->
                                match n with
                                | :? IdentifierNameSyntax as id when id.Identifier.ValueText = name -> Some id
                                | _ -> None)
                            |> List.ofSeq

                        let owners =
                            mentions
                            |> List.map (fun id ->
                                match id.Parent with
                                | :? AssignmentExpressionSyntax as a when a.Left = (id :> ExpressionSyntax) ->
                                    match a.Parent with
                                    | :? ExpressionStatementSyntax as es when
                                        a.IsKind SyntaxKind.SimpleAssignmentExpression
                                        && (taken.Contains(es :> StatementSyntax) || isDefaultValue model a.Right)
                                        ->
                                        Some(es :> StatementSyntax)
                                    | _ -> None
                                | _ -> None)

                        (owners
                         |> List.choose id
                         |> List.filter (fun (s: StatementSyntax) -> not (taken.Contains s))
                         |> List.distinct),
                        owners |> List.exists Option.isNone

                    let bodyOk =
                        tOk
                        && not returns.IsEmpty
                        && shaped |> List.forall Option.isSome
                        && shaped
                           |> List.forall (fun s ->
                               match s with
                               | Some(_, true, Some(_, v)) -> notNull v
                               | _ -> true)
                        && not strayMention
                        // an initialiser at the top of the body only: one in a `finally` or a branch
                        // runs after, or instead of, the value handed back
                        && initialisers |> List.forall (fun s -> s.Parent = (body :> SyntaxNode))

                    if not bodyOk then
                        None
                    else
                        // the callers, all in this type's one declaration
                        let scope = Guards.privateMemberScope tree m

                        let references =
                            scope
                            |> List.collect (fun root ->
                                root.DescendantNodes()
                                |> Seq.choose (fun n ->
                                    match n with
                                    | :? IdentifierNameSyntax as id when
                                        id.Identifier.ValueText = md.Identifier.ValueText
                                        ->
                                        match model.GetSymbolInfo(id).Symbol with
                                        | :? IMethodSymbol as s when
                                            SymbolEqualityComparer.Default.Equals(s.OriginalDefinition, m)
                                            ->
                                            Some id
                                        | _ -> None
                                    | _ -> None)
                                |> List.ofSeq)

                        let callSite (id: IdentifierNameSyntax) =
                            let call =
                                match id.Parent with
                                | :? InvocationExpressionSyntax as inv when inv.Expression = (id :> ExpressionSyntax) ->
                                    Some inv
                                | :? MemberAccessExpressionSyntax as ma when ma.Name = (id :> SimpleNameSyntax) ->
                                    match ma.Parent with
                                    | :? InvocationExpressionSyntax as inv when
                                        inv.Expression = (ma :> ExpressionSyntax)
                                        ->
                                        Some inv
                                    | _ -> None
                                | _ -> None

                            match call with
                            | Some inv when
                                not (md.Span.Contains inv.Span)
                                && inv.ArgumentList.Arguments |> Seq.forall (fun a -> isNull a.NameColon)
                                && inv.ArgumentList.Arguments.Count = m.Parameters.Length
                                ->
                                let args = List.ofSeq inv.ArgumentList.Arguments
                                let last = List.last args

                                let binding =
                                    match last.Expression with
                                    | :? DeclarationExpressionSyntax as d when
                                        last.RefKindKeyword.IsKind SyntaxKind.OutKeyword
                                        ->
                                        match d.Designation with
                                        | :? SingleVariableDesignationSyntax as v -> Some(Some v.Identifier.ValueText)
                                        | :? DiscardDesignationSyntax -> Some None
                                        | _ -> None
                                    | :? IdentifierNameSyntax as i when
                                        i.Identifier.ValueText = "_"
                                        && last.RefKindKeyword.IsKind SyntaxKind.OutKeyword
                                        ->
                                        Some None
                                    | _ -> None

                                let callText =
                                    inv.Expression.ToString()
                                    + "("
                                    + (args
                                       |> List.take (args.Length - 1)
                                       |> List.map (fun a -> a.ToString())
                                       |> String.concat ", ")
                                    + ")"

                                // the condition the call stands in, and whether it is negated
                                let site, negated =
                                    match inv.Parent with
                                    | :? PrefixUnaryExpressionSyntax as u when u.IsKind SyntaxKind.LogicalNotExpression ->
                                        (u :> ExpressionSyntax), true
                                    | _ -> (inv :> ExpressionSyntax), false

                                let conditional, allowed =
                                    match site.Parent with
                                    | :? IfStatementSyntax as i -> false, i.Condition = site
                                    | :? WhileStatementSyntax as w -> false, w.Condition = site
                                    | :? BinaryExpressionSyntax as b ->
                                        false,
                                        (b.IsKind SyntaxKind.LogicalAndExpression
                                         || b.IsKind SyntaxKind.LogicalOrExpression)
                                    | :? ConditionalExpressionSyntax as c -> true, c.Condition = site
                                    | _ -> false, false

                                match binding with
                                | Some b when allowed && (not negated || isNot || b.IsNone) ->
                                    let pattern =
                                        match b, negated with
                                        | Some v, false -> $"{callText} is {{ }} {v}"
                                        | Some v, true -> $"{callText} is not {{ }} {v}"
                                        | None, false -> $"{callText} is {{ }}"
                                        | None, true -> $"{callText} is null"

                                    let pattern = if conditional then $"({pattern})" else pattern
                                    Some(Suggestion.replace site.Span pattern)
                                | _ -> None
                            | _ -> None

                        let sites = references |> List.map callSite

                        if sites.IsEmpty || sites |> List.exists Option.isNone then
                            None
                        else
                            let returnType =
                                if t.IsValueType || annotations then
                                    md.ParameterList.Parameters.[md.ParameterList.Parameters.Count - 1].Type.ToString()
                                    + "?"
                                else
                                    md.ParameterList.Parameters.[md.ParameterList.Parameters.Count - 1].Type.ToString()

                            let parameters =
                                md.ParameterList.Parameters
                                |> Seq.take (md.ParameterList.Parameters.Count - 1)
                                |> Seq.map (fun p -> p.ToString())
                                |> String.concat ", "

                            let signature =
                                [
                                    Suggestion.replace md.ReturnType.Span returnType
                                    Suggestion.replace md.ParameterList.Span ($"({parameters})")
                                ]

                            let bodyEdits =
                                (shaped
                                 |> List.choose id
                                 |> List.map (fun (r, isTrue, p) ->
                                     match isTrue, p with
                                     | true, Some(p, v) ->
                                         Suggestion.replace
                                             (TextSpan.FromBounds(p.SpanStart, r.Span.End))
                                             $"return {v};"
                                     | false, Some(p, _) ->
                                         Suggestion.replace
                                             (TextSpan.FromBounds(p.SpanStart, r.Span.End))
                                             "return null;"
                                     | _, _ -> Suggestion.replace r.Span "return null;"))
                                @ (initialisers
                                   |> List.map (fun s -> Suggestion.replace (Text.statementLineSpan text s) ""))

                            let edits = signature @ bodyEdits @ (sites |> List.choose id)

                            if Guards.speculativeCheck model edits then
                                Some
                                    {
                                        Code = Code
                                        Message =
                                            "A Try-method whose callers only test it: return the value or null, and match it with 'is { } x'"
                                        Span = md.Identifier.Span
                                        Fixes = [ Suggestion.fix "Return the value or null" Code edits ]
                                    }
                            else
                                None
                | _ -> None
            | _ -> None)
        |> List.ofSeq
