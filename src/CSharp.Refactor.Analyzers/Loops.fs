/// Two `for` loop rules.
///
/// CR0015 (idiom, fix): an index that only ever reads `xs[i]` is a
/// `foreach`.
///
///     for (int i = 0; i < xs.Length; i++) Use(xs[i]);   →  foreach (var item in xs) Use(item);
///     for (int i = 0; i < xs.Count; i++)                →  foreach (var x in xs)
///     {                                                     {
///         var x = xs[i];                                        Use(x);
///         Use(x);                                           }
///     }
///
/// Guards: `i` starts at literal `0`, steps by one (`i++`, `++i`, `i += 1`),
/// is bounded by `i < xs.Length` / `i < xs.Count` / `i <= xs.Length - 1`
/// on the very path the body indexes; `xs` is a local, parameter or
/// `readonly` field (a property may hand out a fresh list per read, and
/// the `for` reads it per iteration) typed as an array, `string`, a span,
/// or a type implementing `IList<T>`/`IReadOnlyList<T>`; every use of `i`
/// in the body is `xs[i]` (a use as a value wants `Select((x, i) => …)`
/// and stays the author's call); no `xs[i]` is written, taken by `ref`,
/// or passed `ref`/`out`; `xs` is not assigned, mutated through a known
/// mutator, or — for a list, which `foreach` guards against modification —
/// passed to any method in the body; a list in a field is walked by a body
/// that calls core members only (a method the body calls may append to the
/// field — a worklist); the element name is the alias line's
/// (`var x = xs[i];` first, `x` never reassigned) or `item`, `item2`…,
/// unused in the enclosing member. A `break`/`continue` stays as it is.
///
/// CR0017 (correctness, note): a closure that captures the `for` variable
/// and outlives the iteration sees the final value — the loop variable is
/// one storage location for the whole loop (unlike `foreach`, per
/// iteration since C# 5).
///
///     for (int i = 0; i < 3; i++) actions.Add(() => Console.Write(i));   // 3 3 3
///
/// Fires only where the closure demonstrably escapes: returned or yielded,
/// stored (`Add`, `Push`, `Enqueue`, `Register`, `Subscribe`, `+=`, an
/// assignment to a field or an outer local), handed to `Task.Run`,
/// `Task.Factory.StartNew`, `ThreadPool.QueueUserWorkItem`, `new Thread`,
/// or a deferred LINQ chain (`Where`, `Select`, …) that is not materialised
/// in the same statement. An awaited call, an immediately invoked delegate,
/// `List<T>.ForEach`, `Parallel.*` and a chain ending in `ToList()`/`Sum()`/
/// a `foreach` complete inside the iteration and stay quiet.
module CSharp.Refactor.Loops

open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax
open Microsoft.CodeAnalysis.Text

[<Literal>]
let IndexedLoopCode = "CR0015"

[<Literal>]
let CaptureCode = "CR0017"

// ---- CR0015 ----

let private isZero (e: ExpressionSyntax) =
    match e with
    | :? LiteralExpressionSyntax as l -> l.Token.ValueText = "0"
    | _ -> false

let private isOne (e: ExpressionSyntax) =
    match e with
    | :? LiteralExpressionSyntax as l -> l.Token.ValueText = "1"
    | _ -> false

/// `i++` / `++i` / `i += 1` on the loop variable.
let private stepsByOne (name: string) (e: ExpressionSyntax) =
    match e with
    | :? PostfixUnaryExpressionSyntax as u -> u.IsKind SyntaxKind.PostIncrementExpression && u.Operand.ToString() = name
    | :? PrefixUnaryExpressionSyntax as u -> u.IsKind SyntaxKind.PreIncrementExpression && u.Operand.ToString() = name
    | :? AssignmentExpressionSyntax as a ->
        a.IsKind SyntaxKind.AddAssignmentExpression
        && a.Left.ToString() = name
        && isOne a.Right
    | _ -> false

/// `i < xs.Length` / `i < xs.Count` / `i <= xs.Length - 1`: the collection.
let private boundedBy (name: string) (cond: ExpressionSyntax) : ExpressionSyntax option =
    let lengthOf (e: ExpressionSyntax) =
        match e with
        | :? MemberAccessExpressionSyntax as m when
            m.Name.Identifier.ValueText = "Length" || m.Name.Identifier.ValueText = "Count"
            ->
            Some m.Expression
        | _ -> None

    match cond with
    | :? BinaryExpressionSyntax as b when b.IsKind SyntaxKind.LessThanExpression && b.Left.ToString() = name ->
        lengthOf b.Right
    | :? BinaryExpressionSyntax as b when b.IsKind SyntaxKind.LessThanOrEqualExpression && b.Left.ToString() = name ->
        match b.Right with
        | :? BinaryExpressionSyntax as minus when minus.IsKind SyntaxKind.SubtractExpression && isOne minus.Right ->
            lengthOf minus.Left
        | _ -> None
    | _ -> None

/// A collection `foreach` reads exactly as the index did.
let private enumerable (model: SemanticModel) (xs: ExpressionSyntax) =
    let stable =
        match xs with
        | :? IdentifierNameSyntax
        | :? MemberAccessExpressionSyntax ->
            match model.GetSymbolInfo(xs).Symbol with
            | :? ILocalSymbol -> true
            | :? IParameterSymbol as p -> p.RefKind = RefKind.None || p.RefKind = RefKind.In
            | :? IFieldSymbol as f -> f.IsReadOnly
            | _ -> false
        | _ -> false

    if not stable then
        ValueNone
    else
        match model.GetTypeInfo(xs).Type with
        | null -> ValueNone
        | :? IArrayTypeSymbol as a when a.Rank = 1 -> ValueSome(a :> ITypeSymbol, false)
        | t when t.SpecialType = SpecialType.System_String -> ValueSome(t, false)
        | t when t.Name = "Span" || t.Name = "ReadOnlySpan" || t.Name = "ImmutableArray" -> ValueSome(t, false)
        | t ->
            let isList (i: INamedTypeSymbol) =
                i.OriginalDefinition.ToDisplayString() = "System.Collections.Generic.IList<T>"
                || i.OriginalDefinition.ToDisplayString() = "System.Collections.Generic.IReadOnlyList<T>"

            let itself =
                match t with
                | :? INamedTypeSymbol as n -> isList n
                | _ -> false

            if itself || t.AllInterfaces |> Seq.exists isList then
                // a list guards its enumerator against modification
                ValueSome(t, true)
            else
                ValueNone

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
            "SetValue"
            "CopyTo"
        ]

/// Is an `xs[i]` read as a value — not written, not addressed?
let private isReadOnlyUse (access: ElementAccessExpressionSyntax) =
    let rec outer (n: SyntaxNode) =
        match n.Parent with
        | :? ParenthesizedExpressionSyntax as p -> outer p
        | p -> p

    match outer access with
    | :? AssignmentExpressionSyntax as a -> a.Left.Span <> access.Span
    | :? PrefixUnaryExpressionSyntax as u ->
        not (
            u.IsKind SyntaxKind.PreIncrementExpression
            || u.IsKind SyntaxKind.PreDecrementExpression
        )
        && not (u.IsKind SyntaxKind.AddressOfExpression)
    | :? PostfixUnaryExpressionSyntax -> false
    | :? RefExpressionSyntax -> false
    | :? ArgumentSyntax as a -> a.RefKindKeyword.IsKind SyntaxKind.None
    | _ -> true

let private indexedLoop (tree: SyntaxTree) (model: SemanticModel) : Suggestion list =
    let text = tree.GetText()

    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun node ->
        match node with
        | :? ForStatementSyntax as f when
            not (isNull f.Declaration)
            && f.Declaration.Variables.Count = 1
            && f.Incrementors.Count = 1
            && not (isNull f.Condition)
            ->
            let v = f.Declaration.Variables.[0]
            let name = v.Identifier.ValueText

            let startsAtZero = not (isNull v.Initializer) && isZero v.Initializer.Value

            match boundedBy name f.Condition with
            | Some xs when startsAtZero && stepsByOne name f.Incrementors.[0] ->
                match enumerable model xs with
                | ValueNone -> None
                | ValueSome(_, isList) ->
                    let xsText = xs.ToString()
                    let body = f.Statement
                    let loopVar = model.GetDeclaredSymbol v

                    let refersToLoopVar (id: IdentifierNameSyntax) =
                        id.Identifier.ValueText = name
                        && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(id).Symbol, loopVar)

                    // every use of `i` in the body is `xs[i]`
                    let uses =
                        body.DescendantNodes()
                        |> Seq.choose (fun n ->
                            match n with
                            | :? IdentifierNameSyntax as id when refersToLoopVar id -> Some id
                            | _ -> None)
                        |> List.ofSeq

                    let reads =
                        uses
                        |> List.choose (fun id ->
                            match id.Parent with
                            | :? ArgumentSyntax as a when a.RefKindKeyword.IsKind SyntaxKind.None ->
                                match a.Parent with
                                | :? BracketedArgumentListSyntax as l when l.Arguments.Count = 1 ->
                                    match l.Parent with
                                    | :? ElementAccessExpressionSyntax as e when
                                        Guards.sameReference model e.Expression xs
                                        ->
                                        Some e
                                    | _ -> None
                                | _ -> None
                            | _ -> None)

                    // a mutable struct element: `xs[i].Mutate()` changes the
                    // array's element, `item.Mutate()` a copy
                    let structMemberUse =
                        reads
                        |> List.exists (fun r ->
                            match model.GetTypeInfo(r).Type with
                            | null -> false
                            | t when t.IsValueType && t.SpecialType = SpecialType.None ->
                                let readonlyStruct =
                                    match t with
                                    | :? INamedTypeSymbol as n -> n.IsReadOnly
                                    | _ -> false

                                not readonlyStruct && (r.Parent :? MemberAccessExpressionSyntax)
                            | _ -> false)

                    let xsTouched =
                        body.DescendantNodes()
                        |> Seq.exists (fun n ->
                            match n with
                            | :? InvocationExpressionSyntax as inv ->
                                match inv.Expression with
                                | :? MemberAccessExpressionSyntax as m when m.Expression.ToString() = xsText ->
                                    mutators.Contains m.Name.Identifier.ValueText
                                | _ -> false
                            | :? ArgumentSyntax as a when a.Expression.ToString() = xsText ->
                                // a list handed to a method may be modified there
                                isList || not (a.RefKindKeyword.IsKind SyntaxKind.None)
                            | _ -> false)

                    // a list in a field may be reached from any method the body calls —
                    // `Visit(_pending[i])` appending to `_pending` is a growing worklist
                    // the `for` walks to its end and a `foreach` throws on: every call in
                    // the body must be a core member's (a local keeps the syntactic rule)
                    let reachable =
                        isList
                        && (model.GetSymbolInfo(xs).Symbol :? IFieldSymbol)
                        && body.DescendantNodes()
                           |> Seq.exists (fun n ->
                               match n with
                               | :? InvocationExpressionSyntax as inv -> not (Guards.callsOnlyCore model inv)
                               | :? BaseObjectCreationExpressionSyntax -> true
                               | _ -> false)

                    if
                        uses.IsEmpty
                        || reachable
                        || reads.Length <> uses.Length
                        || not (reads |> List.forall isReadOnlyUse)
                        || Text.assignsTo xsText body
                        || xsTouched
                        || structMemberUse
                        || Text.holdsCommentOrDirective f.Declaration
                    then
                        None
                    else
                        let scope = Text.enclosingMember f

                        // the alias line: `var x = xs[i];` first in the body
                        let alias =
                            match body with
                            | :? BlockSyntax as b when b.Statements.Count > 0 ->
                                match b.Statements.[0] with
                                | :? LocalDeclarationStatementSyntax as d when
                                    d.Declaration.Variables.Count = 1
                                    && d.Declaration.Type.IsVar
                                    && not (isNull d.Declaration.Variables.[0].Initializer)
                                    && (match d.Declaration.Variables.[0].Initializer.Value with
                                        | :? ElementAccessExpressionSyntax as e ->
                                            reads |> List.exists (fun r -> r.Span = e.Span)
                                        | _ -> false)
                                    ->
                                    let aliasName = d.Declaration.Variables.[0].Identifier.ValueText
                                    // a reassigned alias needs a mutable local
                                    if Text.assignsTo aliasName body then
                                        None
                                    else
                                        Some(aliasName, d)
                                | _ -> None
                            | _ -> None

                        let elementName =
                            match alias with
                            | Some(n, _) -> Some n
                            | None ->
                                // `pages` → `page`, `entries` → `entry`; else `item`
                                let collectionName =
                                    match xs with
                                    | :? IdentifierNameSyntax as i -> i.Identifier.ValueText
                                    | :? MemberAccessExpressionSyntax as m -> m.Name.Identifier.ValueText
                                    | _ -> ""

                                let singular =
                                    if collectionName.EndsWith "ies" && collectionName.Length > 4 then
                                        [ collectionName.Substring(0, collectionName.Length - 3) + "y" ]
                                    elif
                                        // `matches` → `match`, `boxes` → `box`, `classes` → `class`
                                        [ "ches"; "shes"; "xes"; "sses"; "zes" ] |> List.exists collectionName.EndsWith
                                        && collectionName.Length > 5
                                    then
                                        [ collectionName.Substring(0, collectionName.Length - 2) ]
                                    elif
                                        collectionName.EndsWith "s"
                                        && not (collectionName.EndsWith "ss")
                                        && collectionName.Length > 3
                                    then
                                        [ collectionName.Substring(0, collectionName.Length - 1) ]
                                    else
                                        []

                                singular @ [ "item"; "item2"; "item3" ]
                                |> List.filter (fun n ->
                                    SyntaxFacts.GetKeywordKind n = SyntaxKind.None
                                    && SyntaxFacts.GetContextualKeywordKind n = SyntaxKind.None)
                                |> List.tryFind (fun n -> not (Text.mentionsName n scope))

                        // `var` only where the enumeration yields what the indexer does:
                        // a collection enumerating `object` (MatchCollection before
                        // .NET Core) or a different type spells the indexer's type
                        let indexedType = model.GetTypeInfo(reads.Head).Type

                        // what `foreach` binds: the public `GetEnumerator()` pattern first
                        // (MatchCollection's yields `object`, its `IEnumerable<Match>` being
                        // explicit), then `IEnumerable<T>`
                        let rec memberNamed (name: string) (t: ITypeSymbol) : ISymbol option =
                            match t with
                            | null -> None
                            | t ->
                                match
                                    t.GetMembers name
                                    |> Seq.tryFind (fun m ->
                                        m.DeclaredAccessibility = Accessibility.Public && not m.IsStatic)
                                with
                                | Some m -> Some m
                                | None -> memberNamed name t.BaseType

                        let enumeratedType =
                            match model.GetTypeInfo(xs).Type with
                            | :? IArrayTypeSymbol as a -> Some a.ElementType
                            | null -> None
                            | t ->
                                let pattern =
                                    match memberNamed "GetEnumerator" t with
                                    | Some(:? IMethodSymbol as g) when g.Parameters.IsEmpty ->
                                        match memberNamed "Current" g.ReturnType with
                                        | Some(:? IPropertySymbol as c) -> Some c.Type
                                        | _ -> None
                                    | _ -> None

                                match pattern with
                                | Some e -> Some e
                                | None ->
                                    Seq.append [ t ] (t.AllInterfaces |> Seq.map (fun i -> i :> ITypeSymbol))
                                    |> Seq.tryPick (fun i ->
                                        match i with
                                        | :? INamedTypeSymbol as n when
                                            n.OriginalDefinition.SpecialType =
                                                SpecialType.System_Collections_Generic_IEnumerable_T
                                            ->
                                            Some n.TypeArguments.[0]
                                        | _ -> None)

                        let elementType =
                            match enumeratedType with
                            | Some e when
                                not (isNull indexedType)
                                && SymbolEqualityComparer.Default.Equals(
                                    e.WithNullableAnnotation NullableAnnotation.NotAnnotated,
                                    indexedType.WithNullableAnnotation NullableAnnotation.NotAnnotated
                                )
                                ->
                                "var"
                            | _ when isNull indexedType -> "var"
                            | _ -> indexedType.ToMinimalDisplayString(model, f.SpanStart)

                        elementName
                        |> Option.map (fun elementName ->
                            let header = TextSpan.FromBounds(f.ForKeyword.SpanStart, f.CloseParenToken.Span.End)

                            let headerEdit =
                                Suggestion.replace
                                    header
                                    ("foreach (" + elementType + " " + elementName + " in " + xsText + ")")

                            let aliasEdit =
                                alias
                                |> Option.map (fun (_, d) -> Suggestion.replace (Text.statementLineSpan text d) "")
                                |> Option.toList

                            let readEdits =
                                reads
                                |> List.filter (fun r ->
                                    alias |> Option.forall (fun (_, d) -> not (d.Span.Contains r.Span)))
                                |> List.map (fun r -> Suggestion.replace r.Span elementName)

                            {
                                Code = IndexedLoopCode
                                Message = $"The index only reads '{xsText}[{name}]': this is a foreach"
                                Span = header
                                Fixes =
                                    [
                                        Suggestion.fix
                                            "Enumerate with foreach"
                                            IndexedLoopCode
                                            (headerEdit :: aliasEdit @ readEdits)
                                    ]
                            }
                            |> Guards.verified model)
            | _ -> None
        | _ -> None)
    |> List.ofSeq

// ---- CR0017 ----

let private storeMethods =
    set
        [
            "Add"
            "AddRange"
            "Insert"
            "Push"
            "Enqueue"
            "TryAdd"
            "Register"
            "Subscribe"
            "Append"
            "Prepend"
        ]

let private deferredStarters =
    set
        [
            "Run"
            "StartNew"
            "QueueUserWorkItem"
            "UnsafeQueueUserWorkItem"
            "ContinueWith"
        ]

let private materialisers =
    set
        [
            "ToList"
            "ToArray"
            "ToDictionary"
            "ToHashSet"
            "ToLookup"
            "ToImmutableArray"
            "ToImmutableList"
            "Count"
            "LongCount"
            "Sum"
            "Min"
            "Max"
            "Average"
            "Aggregate"
            "Any"
            "All"
            "Contains"
            "First"
            "FirstOrDefault"
            "Last"
            "LastOrDefault"
            "Single"
            "SingleOrDefault"
            "ElementAt"
            "ElementAtOrDefault"
            "SequenceEqual"
            "ForEach"
        ]

let private isLinq (model: SemanticModel) (inv: InvocationExpressionSyntax) =
    match model.GetSymbolInfo(inv).Symbol with
    | :? IMethodSymbol as m ->
        let owner = m.ContainingType.ToDisplayString()

        owner = "System.Linq.Enumerable"
        || owner = "System.Linq.Queryable"
        || owner = "System.Linq.AsyncEnumerable"
    | _ -> false

let private methodName (inv: InvocationExpressionSyntax) =
    match inv.Expression with
    | :? MemberAccessExpressionSyntax as m -> m.Name.Identifier.ValueText
    | :? IdentifierNameSyntax as i -> i.Identifier.ValueText
    | _ -> ""

/// Is a local declared inside this node?
let private declaredWithin (node: SyntaxNode) (s: ISymbol) =
    match s with
    | :? ILocalSymbol as l -> l.DeclaringSyntaxReferences |> Seq.exists (fun r -> node.Span.Contains r.Span)
    | _ -> false

/// Where a closure-carrying expression ends up: does it outlive the
/// iteration? Walks from the expression up to its statement.
let rec private escapes (model: SemanticModel) (loopBody: SyntaxNode) (carrier: SyntaxNode) : bool =
    match carrier.Parent with
    | null -> false
    | :? ParenthesizedExpressionSyntax as p -> escapes model loopBody p
    | :? CastExpressionSyntax as c -> escapes model loopBody c
    | :? AwaitExpressionSyntax -> false
    | :? ReturnStatementSyntax
    | :? YieldStatementSyntax -> true
    | :? ArrowExpressionClauseSyntax -> true
    | :? AssignmentExpressionSyntax as a when a.Right.Span = carrier.Span ->
        // `+=` on an event, or an assignment to anything living past the loop
        a.IsKind SyntaxKind.AddAssignmentExpression
        || not (declaredWithin loopBody (model.GetSymbolInfo(a.Left).Symbol))
    | :? EqualsValueClauseSyntax as e ->
        // `var f = () => i;` inside the loop: follow the local's uses
        match e.Parent with
        | :? VariableDeclaratorSyntax as v ->
            match model.GetDeclaredSymbol v with
            | :? ILocalSymbol as l when declaredWithin loopBody l ->
                loopBody.DescendantNodes()
                |> Seq.exists (fun n ->
                    match n with
                    | :? IdentifierNameSyntax as id when
                        id.Identifier.ValueText = l.Name
                        && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(id).Symbol, l)
                        ->
                        escapes model loopBody id
                    | _ -> false)
            | _ -> true
        | _ -> true
    | :? ArgumentSyntax as arg ->
        match arg.Parent.Parent with
        | :? InvocationExpressionSyntax as inv ->
            let name = methodName inv

            if storeMethods.Contains name then
                true
            elif deferredStarters.Contains name then
                // `await Task.Run(…)` completes before the loop moves on
                not (inv.Parent :? AwaitExpressionSyntax)
            elif isLinq model inv then
                if materialisers.Contains name then
                    false
                else
                    // deferred: what happens to the chain decides
                    escapes model loopBody inv
            else
                // an unknown callee: nothing proven
                false
        | :? ObjectCreationExpressionSyntax as oc ->
            let typeName = oc.Type.ToString()

            typeName = "Thread"
            || typeName = "Task"
            || typeName = "System.Threading.Thread"
            || typeName = "System.Threading.Tasks.Task"
        | _ -> false
    | :? MemberAccessExpressionSyntax as m when m.Expression.Span = carrier.Span ->
        // `xs.Where(…).ToList()`: the chain continues on the carrier
        match m.Parent with
        | :? InvocationExpressionSyntax as inv ->
            let name = methodName inv

            if materialisers.Contains name then false
            elif isLinq model inv then escapes model loopBody inv
            else false
        | _ -> false
    | :? ConditionalExpressionSyntax as c -> escapes model loopBody c
    | :? InvocationExpressionSyntax as inv when inv.Expression.Span = carrier.Span ->
        // immediately invoked
        false
    | :? ForEachStatementSyntax -> false
    | :? ExpressionStatementSyntax -> false
    | _ -> false

let private captures (tree: SyntaxTree) (model: SemanticModel) : Suggestion list =
    tree.GetRoot().DescendantNodes()
    |> Seq.collect (fun node ->
        match node with
        | :? ForStatementSyntax as f when not (isNull f.Declaration) ->
            let loopVars = [ for v in f.Declaration.Variables -> model.GetDeclaredSymbol v ]

            let body = f.Statement

            body.DescendantNodes()
            |> Seq.choose (fun n ->
                match n with
                | :? AnonymousFunctionExpressionSyntax as lambda ->
                    let captured =
                        lambda.DescendantNodes()
                        |> Seq.choose (fun x ->
                            match x with
                            | :? IdentifierNameSyntax as id ->
                                let s = model.GetSymbolInfo(id).Symbol

                                if
                                    loopVars |> List.exists (fun v -> SymbolEqualityComparer.Default.Equals(v, s))
                                then
                                    Some id
                                else
                                    None
                            | _ -> None)
                        |> Seq.tryHead

                    match captured with
                    | Some id when escapes model body lambda ->
                        Some(
                            Suggestion.note
                                CaptureCode
                                $"This closure captures the loop variable '{id.Identifier.ValueText}' and outlives the iteration: every call sees the final value. Copy it into a loop-local first."
                                id.Span
                        )
                    | _ -> None
                | _ -> None)
            |> List.ofSeq
        | _ -> [])
    |> List.ofSeq

let analyze (tree: SyntaxTree) (model: SemanticModel) (_ctx: RuleContext) : Suggestion list =
    indexedLoop tree model @ captures tree model
