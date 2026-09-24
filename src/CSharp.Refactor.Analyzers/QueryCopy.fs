/// CR0178 (performance, fix): a copy of a query ahead of `Where`/`Select`
/// loads every row and filters in memory; the query takes the stages.
///
///     db.Orders.ToList().Where(o => o.Total > 0 || o.State == 0).Select(o => o.Id)
///       →  db.Orders.Where(o => o.Total > 0 || o.State == 0).Select(o => o.Id).ToList()
///
/// The copy (`ToList`, `ToArray`, `AsEnumerable`) sits on a receiver typed
/// `IQueryable<T>`; the stages after it are `Enumerable.Where`/`Select`,
/// each with one expression-bodied lambda of one parameter, and move before
/// the copy only while every lambda is one a provider translates exactly —
/// a function call, arithmetic or a nested object might not translate, or
/// translate to something else, and then a moved stage is a runtime error
/// or a different answer:
/// - `Where`: `&&`, `||`, `!` over comparisons (`==`, `!=`, `<`, `<=`, `>`,
///   `>=`, built-in or the BCL's own operator) and `bool` columns; a
///   comparison has a column on one side and a column, a literal, a local, a
///   parameter, a `const` or an enum member on the other;
/// - `Select`: a column, or an anonymous object of columns;
/// - a column is an instance auto-property of the lambda's parameter, not
///   `[NotMapped]` — a computed property has no column to translate to.
/// A sweep moves only comparisons SQL answers as C# does: integers, `bool`,
/// enums and `Guid`, and `== null`/`!= null` on any column. Such a column
/// nullable counts too when `==`, `<`, `<=`, `>` or `>=` compares it with a
/// value that cannot be null (a non-nullable literal, `const`, local,
/// parameter or enum member) and no `!` is above it: the NULL row is false
/// in C# and unknown in SQL, dropped by both.
/// A string compares under the column's collation (case-insensitive on SQL
/// Server's default), a `decimal` or `DateTime` literal is rounded to the
/// column's scale, a floating value is the server's, and a nullable column
/// is NULL-unknown under `!=` or `!` where C# says true, and NULL on both
/// sides of a column-to-column or nullable-value comparison: those the editor
/// offers and a sweep leaves as a note. The captured locals and parameters
/// are written nowhere after their declaration — the in-memory stage read
/// them when the result was enumerated, the query reads them when it runs.
/// A copy right after the stages (`.ToList().Where(p).ToList()`) is the one
/// kept; the stages' lambdas become expression trees, and the speculative
/// check re-binds them there.
module CSharp.Refactor.QueryCopy

open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax
open Microsoft.CodeAnalysis.Text

[<Literal>]
let Code = "CR0178"

let private copies = set [ "ToList"; "ToArray"; "AsEnumerable" ]

/// How faithfully a provider answers a translated lambda: as C# does, or
/// only nearly (collation, rounding, NULL logic) — the editor's to offer.
type private Fidelity =
    | Exact
    | Near

let private weaker (a: Fidelity) (b: Fidelity) =
    if a = Near || b = Near then Near else Exact

let private combine (a: Fidelity option) (b: Fidelity option) =
    match a, b with
    | Some a, Some b -> Some(weaker a b)
    | _ -> None

let private autoProperty (p: IPropertySymbol) =
    not (isNull p.GetMethod)
    && (match p.DeclaringSyntaxReferences |> Seq.tryHead with
        | Some r ->
            match r.GetSyntax() with
            | :? PropertyDeclarationSyntax as d ->
                isNull d.ExpressionBody
                && not (isNull d.AccessorList)
                && d.AccessorList.Accessors
                   |> Seq.forall (fun a -> isNull a.Body && isNull a.ExpressionBody)
            // a record's positional parameter
            | :? ParameterSyntax -> true
            | _ -> false
        | None ->
            p.GetMethod.GetAttributes()
            |> Seq.exists (fun a ->
                not (isNull a.AttributeClass)
                && a.AttributeClass.Name = "CompilerGeneratedAttribute"))

let private notMapped (p: IPropertySymbol) =
    p.GetAttributes()
    |> Seq.exists (fun a -> not (isNull a.AttributeClass) && a.AttributeClass.Name = "NotMappedAttribute")

/// `x.P`: a column of the lambda's parameter.
let private column (model: SemanticModel) (param: IParameterSymbol) (e: ExpressionSyntax) : IPropertySymbol option =
    match e with
    | :? MemberAccessExpressionSyntax as m when m.IsKind SyntaxKind.SimpleMemberAccessExpression ->
        match m.Expression with
        | :? IdentifierNameSyntax as id when
            SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(id).Symbol, param)
            ->
            match model.GetSymbolInfo(m).Symbol with
            | :? IPropertySymbol as p when not (p.IsStatic || p.IsIndexer) && autoProperty p && not (notMapped p) ->
                Some p
            | _ -> None
        | _ -> None
    | _ -> None

let private nullableInner (t: ITypeSymbol) =
    match t with
    | :? INamedTypeSymbol as n when n.OriginalDefinition.SpecialType = SpecialType.System_Nullable_T ->
        ValueSome n.TypeArguments.[0]
    | _ -> ValueNone

/// The types SQL compares exactly as C# does.
let private exactType (t: ITypeSymbol) =
    t.TypeKind = TypeKind.Enum
    || t.ToDisplayString() = "System.Guid"
    || (match t.SpecialType with
        | SpecialType.System_Boolean
        | SpecialType.System_Byte
        | SpecialType.System_SByte
        | SpecialType.System_Int16
        | SpecialType.System_UInt16
        | SpecialType.System_Int32
        | SpecialType.System_UInt32
        | SpecialType.System_Int64
        | SpecialType.System_UInt64 -> true
        | _ -> false)

/// The types a provider translates, but answers under its own rules.
let private nearType (t: ITypeSymbol) =
    match t.SpecialType with
    | SpecialType.System_String
    | SpecialType.System_Char
    | SpecialType.System_Decimal
    | SpecialType.System_Double
    | SpecialType.System_Single
    | SpecialType.System_DateTime -> true
    | _ ->
        match t.ToDisplayString() with
        | "System.DateTimeOffset"
        | "System.DateOnly"
        | "System.TimeOnly"
        | "System.TimeSpan" -> true
        | _ -> false

let private columnFidelity (t: ITypeSymbol) =
    match nullableInner t with
    | ValueSome inner when exactType inner || nearType inner -> Some Near
    | ValueSome _ -> None
    | ValueNone when exactType t -> Some Exact
    | ValueNone when nearType t -> Some Near
    | ValueNone -> None

/// A column holding a value, not an entity: a navigation property
/// (`o.Customer`) is an auto-property too, but in memory it is whatever the
/// copy loaded — null without an `Include` — where the query joins it, so
/// `o.Customer == null` and `Select(o => o.Customer)` answer differently.
let private scalar (t: ITypeSymbol) =
    (columnFidelity t).IsSome
    || (match t with
        | :? IArrayTypeSymbol as a -> a.ElementType.SpecialType = SpecialType.System_Byte
        | _ -> false)

/// A value the query parameterizes or inlines: a literal, a local, a
/// parameter other than the lambda's, a `const`, an enum member.
let private value (model: SemanticModel) (param: IParameterSymbol) (e: ExpressionSyntax) =
    match e with
    | :? LiteralExpressionSyntax -> true
    | :? PrefixUnaryExpressionSyntax as u when
        u.IsKind SyntaxKind.UnaryMinusExpression
        && (u.Operand :? LiteralExpressionSyntax)
        ->
        true
    | :? IdentifierNameSyntax
    | :? MemberAccessExpressionSyntax ->
        match model.GetSymbolInfo(e).Symbol with
        | :? ILocalSymbol -> true
        | :? IParameterSymbol as p -> not (SymbolEqualityComparer.Default.Equals(p, param))
        | :? IFieldSymbol as f -> f.IsConst
        | _ -> false
    | _ -> false

let private comparisons =
    set
        [
            SyntaxKind.EqualsExpression
            SyntaxKind.NotEqualsExpression
            SyntaxKind.LessThanExpression
            SyntaxKind.LessThanOrEqualExpression
            SyntaxKind.GreaterThanExpression
            SyntaxKind.GreaterThanOrEqualExpression
        ]

/// The operator is the language's or the BCL's own, never a user-defined one.
let private builtinOperator (model: SemanticModel) (b: BinaryExpressionSyntax) =
    match model.GetSymbolInfo(b).Symbol with
    | :? IMethodSymbol as m ->
        m.MethodKind = MethodKind.BuiltinOperator
        || (not (isNull m.ContainingType)
            && (exactType m.ContainingType || nearType m.ContainingType))
    | _ -> false

let private isNullLiteral (e: ExpressionSyntax) =
    e.IsKind SyntaxKind.NullLiteralExpression

/// A nullable column of an exact type against a value that cannot be null:
/// on the NULL row C# answers false and SQL unknown, and a `Where` drops the
/// row either way — unless `!=` (C#'s true) or a `!` above (C#'s false
/// turned true, SQL's unknown kept) tells them apart.
let private columnAgainstValue
    (model: SemanticModel)
    (negated: bool)
    (b: BinaryExpressionSyntax)
    (c: IPropertySymbol)
    (v: ExpressionSyntax)
    =
    let nonNullValue =
        not (isNullLiteral v)
        && (match model.GetTypeInfo(v).Type with
            | null -> false
            | t -> t.IsValueType && (nullableInner t).IsNone)

    match nullableInner c.Type with
    | ValueSome inner when
        exactType inner
        && nonNullValue
        && not negated
        && not (b.IsKind SyntaxKind.NotEqualsExpression)
        ->
        Some Exact
    | _ -> columnFidelity c.Type

let private comparison (model: SemanticModel) (param: IParameterSymbol) (negated: bool) (b: BinaryExpressionSyntax) =
    let left = column model param b.Left
    let right = column model param b.Right

    match left, right with
    // `x.P == null`: a null test answers alike on a value column
    | Some c, None when isNullLiteral b.Right && scalar c.Type -> Some Exact
    | None, Some c when isNullLiteral b.Left && scalar c.Type -> Some Exact
    | _ when not (builtinOperator model b) -> None
    | Some l, Some r -> combine (columnFidelity l.Type) (columnFidelity r.Type)
    | Some c, None when value model param b.Right -> columnAgainstValue model negated b c b.Right
    | None, Some c when value model param b.Left -> columnAgainstValue model negated b c b.Left
    | _ -> None

/// A `Where` body: comparisons and `bool` columns under `&&`, `||`, `!`.
/// Under a `!`, a nullable column's NULL turns unknown into true in C# and
/// stays unknown in SQL: `Near` is as far as it goes, so `negated` rides
/// down from the first `!`.
let rec private predicate (model: SemanticModel) (param: IParameterSymbol) (negated: bool) (e: ExpressionSyntax) =
    match e with
    | :? ParenthesizedExpressionSyntax as p -> predicate model param negated p.Expression
    | :? PrefixUnaryExpressionSyntax as u when u.IsKind SyntaxKind.LogicalNotExpression ->
        predicate model param true u.Operand
    | :? BinaryExpressionSyntax as b when
        b.IsKind SyntaxKind.LogicalAndExpression
        || b.IsKind SyntaxKind.LogicalOrExpression
        ->
        combine (predicate model param negated b.Left) (predicate model param negated b.Right)
    | :? BinaryExpressionSyntax as b when comparisons.Contains(b.Kind()) -> comparison model param negated b
    | e ->
        match column model param e with
        | Some c when c.Type.SpecialType = SpecialType.System_Boolean -> Some Exact
        | _ -> None

/// A `Select` body: a value column, or an anonymous object of them. A
/// projected value is the row's either way; a projected entity is not.
let private projection (model: SemanticModel) (param: IParameterSymbol) (e: ExpressionSyntax) =
    let valueColumn (e: ExpressionSyntax) =
        column model param e |> Option.exists (fun c -> scalar c.Type)

    match e with
    | :? AnonymousObjectCreationExpressionSyntax as a when a.Initializers.Count > 0 ->
        if a.Initializers |> Seq.forall (fun i -> valueColumn i.Expression) then
            Some Exact
        else
            None
    | e when valueColumn e -> Some Exact
    | _ -> None

/// The lambda of a stage, with its one parameter and its expression body.
let private lambdaOf (model: SemanticModel) (stage: InvocationExpressionSyntax) =
    if stage.ArgumentList.Arguments.Count <> 1 then
        None
    else
        let parameterAndBody =
            match stage.ArgumentList.Arguments.[0].Expression with
            | :? SimpleLambdaExpressionSyntax as l when
                l.AsyncKeyword.IsKind SyntaxKind.None && not (isNull l.ExpressionBody)
                ->
                Some(l.Parameter, l.ExpressionBody)
            | :? ParenthesizedLambdaExpressionSyntax as l when
                l.AsyncKeyword.IsKind SyntaxKind.None
                && l.ParameterList.Parameters.Count = 1
                && not (isNull l.ExpressionBody)
                ->
                Some(l.ParameterList.Parameters.[0], l.ExpressionBody)
            | _ -> None

        parameterAndBody
        |> Option.bind (fun (p, body) ->
            match model.GetDeclaredSymbol p with
            | null -> None
            | symbol -> Some(symbol, body))

let private stageFidelity (model: SemanticModel) (stage: InvocationExpressionSyntax) =
    match Linq.enumerableCall model stage, lambdaOf model stage with
    | Some _, Some(param, body) ->
        match Linq.nameOf stage with
        | "Where" -> predicate model param false body
        | "Select" -> projection model param body
        | _ -> None
    | _ -> None

/// Every local and parameter the stages read is written nowhere in the
/// member but its declaration.
let private capturesSettled (model: SemanticModel) (stages: InvocationExpressionSyntax list) =
    match stages with
    | [] -> true
    | first :: _ ->
        let scope = Text.enclosingMember first

        let captured =
            stages
            |> List.collect (fun s ->
                s.ArgumentList.DescendantNodes()
                |> Seq.choose (fun n ->
                    match n with
                    | :? IdentifierNameSyntax as id ->
                        match model.GetSymbolInfo(id).Symbol with
                        | :? ILocalSymbol as l -> Some(l :> ISymbol)
                        | :? IParameterSymbol as p when
                            not (p.DeclaringSyntaxReferences |> Seq.exists (fun r -> s.Span.Contains r.Span))
                            ->
                            Some(p :> ISymbol)
                        | _ -> None
                    | _ -> None)
                |> List.ofSeq)

        let written (s: ISymbol) =
            scope.DescendantNodes()
            |> Seq.exists (fun n ->
                let target =
                    match n with
                    | :? AssignmentExpressionSyntax as a -> Some a.Left
                    | :? PrefixUnaryExpressionSyntax as u when
                        u.IsKind SyntaxKind.PreIncrementExpression
                        || u.IsKind SyntaxKind.PreDecrementExpression
                        ->
                        Some u.Operand
                    | :? PostfixUnaryExpressionSyntax as u -> Some u.Operand
                    | :? ArgumentSyntax as a when not (a.RefKindKeyword.IsKind SyntaxKind.None) -> Some a.Expression
                    | _ -> None

                match target with
                | Some t -> SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(t).Symbol, s)
                | None -> false)

        captured |> List.forall (written >> not)

/// Does the chain's value go somewhere a `List<T>`/`T[]` binds exactly as
/// the `IEnumerable<T>` it was? Moving `ToList()` to the end changes the
/// chain's static type: an overload taking `List<T>` would win over one
/// taking `IEnumerable<T>`, a generic argument would infer differently, a
/// `var` local would carry the new type on, and `.Reverse()` would bind to
/// `List<T>.Reverse()`, which reverses in place and returns nothing. So:
/// a `foreach`, an Enumerable call on it (not `Reverse`), an argument of
/// a method with one candidate whose parameter is no type parameter, a
/// `return` or expression body of a member (a lambda infers its return
/// type from it), an explicitly typed declaration or an assignment, and a
/// `var` local every use of which is one of these.
let rec private stableUse (model: SemanticModel) (depth: int) (e: ExpressionSyntax) : bool =
    let lambdaAbove (n: SyntaxNode) =
        n.Ancestors()
        |> Seq.takeWhile (fun a -> not (a :? MemberDeclarationSyntax || a :? LocalFunctionStatementSyntax))
        |> Seq.exists (fun a -> a :? AnonymousFunctionExpressionSyntax)

    match e.Parent with
    | :? ParenthesizedExpressionSyntax as p -> stableUse model depth p
    | :? ForEachStatementSyntax as f -> obj.ReferenceEquals(f.Expression, e)
    | :? MemberAccessExpressionSyntax as m when obj.ReferenceEquals(m.Expression, e) ->
        match m.Parent with
        | :? InvocationExpressionSyntax as inv when obj.ReferenceEquals(inv.Expression, m) ->
            m.Name.Identifier.ValueText <> "Reverse"
            && (Linq.enumerableCall model inv).IsSome
        | _ -> false
    | :? ArgumentSyntax as a ->
        match a.Parent with
        | :? ArgumentListSyntax as list ->
            match list.Parent with
            | :? InvocationExpressionSyntax as inv ->
                match model.GetSymbolInfo(inv).Symbol with
                | :? IMethodSymbol as m when model.GetMemberGroup(inv.Expression).Length = 1 ->
                    let index = list.Arguments.IndexOf a

                    let parameter =
                        if isNull a.NameColon then
                            if index < m.Parameters.Length then
                                Some m.Parameters.[index]
                            else
                                None
                        else
                            m.Parameters
                            |> Seq.tryFind (fun p -> p.Name = a.NameColon.Name.Identifier.ValueText)

                    parameter
                    |> Option.exists (fun p ->
                        let t = p.OriginalDefinition.Type
                        t.TypeKind <> TypeKind.TypeParameter && not p.IsParams)
                | _ -> false
            | _ -> false
        | _ -> false
    | :? ReturnStatementSyntax
    | :? ArrowExpressionClauseSyntax -> not (lambdaAbove e)
    | :? AssignmentExpressionSyntax as a -> obj.ReferenceEquals(a.Right, e)
    | :? EqualsValueClauseSyntax as v ->
        match v.Parent with
        | :? VariableDeclaratorSyntax as d ->
            match d.Parent with
            | :? VariableDeclarationSyntax as decl when not decl.Type.IsVar -> true
            | :? VariableDeclarationSyntax when depth < 3 ->
                // a `var` local: every use of it binds as before
                match model.GetDeclaredSymbol d with
                | :? ILocalSymbol as local ->
                    Text.enclosingMember d
                    |> fun scope -> scope.DescendantNodes()
                    |> Seq.choose (fun n ->
                        match n with
                        | :? IdentifierNameSyntax as id when
                            id.Identifier.ValueText = local.Name
                            && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(id).Symbol, local)
                            ->
                            Some id
                        | _ -> None)
                    |> Seq.forall (fun id -> stableUse model (depth + 1) id)
                | _ -> false
            | _ -> false
        | _ -> false
    | _ -> false

/// The invocation a call's result is the receiver of: `x.F()` → `x.F().G()`.
let private nextCall (inv: InvocationExpressionSyntax) =
    match inv.Parent with
    | :? MemberAccessExpressionSyntax as m when obj.ReferenceEquals(m.Expression, inv) ->
        match m.Parent with
        | :? InvocationExpressionSyntax as next when obj.ReferenceEquals(next.Expression, m) -> ValueSome next
        | _ -> ValueNone
    | _ -> ValueNone

let analyze (tree: SyntaxTree) (model: SemanticModel) (_ctx: RuleContext) : Suggestion list =
    let source = tree.GetText().ToString()

    if
        not (
            source.Contains ".ToList()"
            || source.Contains ".ToArray()"
            || source.Contains ".AsEnumerable()"
        )
    then
        []
    else
        tree.GetRoot().DescendantNodes()
        |> Seq.choose (fun node ->
            match node with
            | :? InvocationExpressionSyntax as copy when
                copies.Contains(Linq.nameOf copy)
                && copy.ArgumentList.Arguments.Count = 0
                && (copy.Expression :? MemberAccessExpressionSyntax)
                ->
                let receiver = (copy.Expression :?> MemberAccessExpressionSyntax).Expression

                if
                    (Linq.enumerableCall model copy).IsNone
                    || not (Linq.isQueryable (model.GetTypeInfo(receiver).Type))
                    || Guards.insideExpressionTree model copy
                then
                    None
                else
                    // the run of translatable stages after the copy
                    let rec run (at: InvocationExpressionSyntax) (acc: (InvocationExpressionSyntax * Fidelity) list) =
                        match nextCall at with
                        | ValueSome stage ->
                            match stageFidelity model stage with
                            | Some f -> run stage ((stage, f) :: acc)
                            | None -> List.rev acc
                        | ValueNone -> List.rev acc

                    match run copy [] with
                    | [] -> None
                    | stages ->
                        let last, _ = List.last stages
                        let stageNodes = stages |> List.map fst

                        let fidelity = stages |> List.map snd |> List.reduce weaker

                        let copyName = Linq.nameOf copy

                        // a copy of the same kind right after the stages is the one kept
                        let copiedAfter =
                            match nextCall last with
                            | ValueSome next ->
                                Linq.nameOf next = copyName
                                && next.ArgumentList.Arguments.Count = 0
                                && (Linq.enumerableCall model next).IsSome
                            | ValueNone -> false

                        let dropSpan = TextSpan.FromBounds(receiver.Span.End, copy.Span.End)

                        let edits =
                            if copiedAfter then
                                [ Suggestion.replace dropSpan "" ]
                            else
                                [
                                    Suggestion.replace dropSpan ""
                                    Suggestion.insert last.Span.End $".{copyName}()"
                                ]

                        let names = stageNodes |> List.map Linq.nameOf |> List.distinct |> String.concat "/"

                        if
                            Text.holdsCommentOrDirective last
                            || not (capturesSettled model stageNodes)
                            || not (Guards.speculativeCheck model edits)
                        then
                            None
                        else
                            let fix = Suggestion.fix $"Run {names} in the query" Code edits

                            // `AsEnumerable()` keeps the chain an IEnumerable, and a copy
                            // kept after the stages is the one the chain already ended in
                            let typeStable = copyName = "AsEnumerable" || copiedAfter || stableUse model 0 last

                            let caveat =
                                if typeStable then
                                    ""
                                else
                                    let copied = if copyName = "ToList" then "List<T>" else "T[]"
                                    $"; the chain becomes a {copied} where it was an IEnumerable<T>, so check what it binds to"

                            Some
                                {
                                    Code = Code
                                    Message =
                                        match fidelity with
                                        | Exact ->
                                            $"'{copyName}()' loads every row of the query before '{names}' runs in memory: run {names} in the query, copy after{caveat}"
                                        | Near ->
                                            $"'{copyName}()' loads every row of the query before '{names}' runs in memory; the query could run it, but compares strings, decimals, dates or nullable columns under its own rules (collation, scale, NULL) — check the translation, then move the copy after{caveat}"
                                    Span = dropSpan
                                    Fixes =
                                        [
                                            if fidelity = Exact && typeStable then
                                                fix
                                            else
                                                Suggestion.editorOnly fix
                                        ]
                                }
            | _ -> None)
        |> List.ofSeq
