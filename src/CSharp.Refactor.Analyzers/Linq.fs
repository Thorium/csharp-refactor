/// What the collection rules share: which calls are `Enumerable`'s, which
/// operators defer and which consume, and what a receiver is typed as.
module CSharp.Refactor.Linq

open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax

/// Operators that return a lazy sequence: calling one runs nothing.
let deferredOperators =
    set
        [
            "Select"
            "SelectMany"
            "Where"
            "OfType"
            "Cast"
            "Skip"
            "SkipWhile"
            "SkipLast"
            "Take"
            "TakeWhile"
            "TakeLast"
            "Concat"
            "Append"
            "Prepend"
            "Distinct"
            "DistinctBy"
            "Reverse"
            "OrderBy"
            "OrderByDescending"
            "ThenBy"
            "ThenByDescending"
            "Order"
            "OrderDescending"
            "GroupBy"
            "Join"
            "GroupJoin"
            "Zip"
            "Union"
            "UnionBy"
            "Intersect"
            "IntersectBy"
            "Except"
            "ExceptBy"
            "DefaultIfEmpty"
            "Chunk"
            "Index"
            "AsEnumerable"
        ]

/// Operators that walk the sequence and return something else.
let consumers =
    set
        [
            "ToList"
            "ToArray"
            "ToDictionary"
            "ToHashSet"
            "ToLookup"
            "ToImmutableArray"
            "ToImmutableList"
            "ToImmutableHashSet"
            "ToFrozenSet"
            "ToFrozenDictionary"
            "Count"
            "LongCount"
            "Sum"
            "Min"
            "Max"
            "MinBy"
            "MaxBy"
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

let private ownerOf (model: SemanticModel) (inv: InvocationExpressionSyntax) =
    match model.GetSymbolInfo(inv).Symbol with
    | :? IMethodSymbol as m when not (isNull m.ContainingType) -> ValueSome(m, m.ContainingType.ToDisplayString())
    | _ -> ValueNone

/// The method, when the call resolves to `System.Linq.Enumerable`.
let enumerableCall (model: SemanticModel) (inv: InvocationExpressionSyntax) : IMethodSymbol option =
    match ownerOf model inv with
    | ValueSome(m, "System.Linq.Enumerable") -> Some m
    | _ -> None

/// The method, when the call resolves to `System.Linq.Queryable` — a query
/// a provider translates, never rewritten as if it ran in memory.
let queryableCall (model: SemanticModel) (inv: InvocationExpressionSyntax) : IMethodSymbol option =
    match ownerOf model inv with
    | ValueSome(m, "System.Linq.Queryable") -> Some m
    | _ -> None

/// The name a call spells: `xs.Where(p)` → Where, `Enumerable.Where(xs, p)` → Where.
let nameOf (inv: InvocationExpressionSyntax) =
    match inv.Expression with
    | :? MemberAccessExpressionSyntax as m -> m.Name.Identifier.ValueText
    | :? IdentifierNameSyntax as i -> i.Identifier.ValueText
    | :? MemberBindingExpressionSyntax as b -> b.Name.Identifier.ValueText
    | _ -> ""

/// The receiver of an extension-style call, or the first argument of the
/// static spelling.
let receiverOf (inv: InvocationExpressionSyntax) : ExpressionSyntax option =
    match inv.Expression with
    | :? MemberAccessExpressionSyntax as m ->
        match m.Expression with
        | :? IdentifierNameSyntax as i when
            i.Identifier.ValueText = "Enumerable" || i.Identifier.ValueText = "Queryable"
            ->
            if inv.ArgumentList.Arguments.Count > 0 then
                Some inv.ArgumentList.Arguments.[0].Expression
            else
                None
        | e -> Some e
    | _ -> None

let private implements (t: ITypeSymbol) (fullName: string) =
    let named (i: INamedTypeSymbol) =
        i.OriginalDefinition.ToDisplayString() = fullName

    (match t with
     | :? INamedTypeSymbol as n -> named n
     | _ -> false)
    || t.AllInterfaces |> Seq.exists named

/// A type with a cheap `Count`/`Length`: an array, a string, or a
/// collection interface. Everything else typed `IEnumerable<T>` may be a
/// query or a generator, walked on every consumer.
let isCollection (t: ITypeSymbol) =
    match t with
    | null -> false
    | :? IArrayTypeSymbol -> true
    | t when t.SpecialType = SpecialType.System_String -> true
    | t ->
        implements t "System.Collections.Generic.ICollection<T>"
        || implements t "System.Collections.Generic.IReadOnlyCollection<T>"
        || implements t "System.Collections.ICollection"

/// Typed as a generic `IEnumerable<T>` (a collection is one too).
let isGenericEnumerable (t: ITypeSymbol) =
    match t with
    | null -> false
    | t ->
        t.OriginalDefinition.ToDisplayString() = "System.Collections.Generic.IEnumerable<T>"
        || implements t "System.Collections.Generic.IEnumerable<T>"

let isQueryable (t: ITypeSymbol) =
    match t with
    | null -> false
    | t ->
        implements t "System.Linq.IQueryable<T>"
        || implements t "System.Linq.IQueryable"

/// Does the compilation know `System.Linq`? Then a fix may spell a LINQ
/// call, adding `using System.Linq;` where the file lacks it.
let hasLinq (model: SemanticModel) =
    not (isNull (model.Compilation.GetTypeByMetadataName "System.Linq.Enumerable"))

/// Does the type resolve by its bare name at the position? Else a `using`
/// is needed (or the name is spelled qualified).
let resolvesBare (model: SemanticModel) (position: int) (ns: string) (name: string) =
    model.LookupNamespacesAndTypes(position, name = name)
    |> Seq.exists (fun s -> s.ToDisplayString() = $"{ns}.{name}")

/// Is the node inside a loop body (`for`, `foreach`, `while`, `do`), or a
/// lambda handed to a collection operator — somewhere that runs per element?
let insideLoop (node: SyntaxNode) =
    node.Ancestors()
    |> Seq.exists (fun a ->
        a :? ForStatementSyntax
        || a :? ForEachStatementSyntax
        || a :? WhileStatementSyntax
        || a :? DoStatementSyntax)

/// The innermost loop whose per-iteration part holds the node: a body, a
/// `for`/`while` condition or incrementor — not a `foreach` source, which
/// runs once.
let enclosingLoop (node: SyntaxNode) : StatementSyntax option =
    node.Ancestors()
    |> Seq.tryPick (fun a ->
        let within (part: SyntaxNode) =
            not (isNull part) && part.Span.Contains node.Span

        match a with
        | :? ForStatementSyntax as f when
            within f.Statement
            || within f.Condition
            || (f.Incrementors |> Seq.exists (fun i -> within i))
            ->
            Some(a :?> StatementSyntax)
        | :? ForEachStatementSyntax as f when within f.Statement -> Some(a :?> StatementSyntax)
        | :? WhileStatementSyntax as w when within w.Statement || within w.Condition -> Some(a :?> StatementSyntax)
        | :? DoStatementSyntax as d when within d.Statement || within d.Condition -> Some(a :?> StatementSyntax)
        | _ -> None)

/// Where a symbol is declared, as a span in this tree, if it is.
let declarationSpan (tree: SyntaxTree) (s: ISymbol) =
    match s with
    | null -> None
    | s ->
        s.DeclaringSyntaxReferences
        |> Seq.tryFind (fun r -> r.SyntaxTree = tree)
        |> Option.map (fun r -> r.Span)

/// Is the expression's root symbol declared outside the given node — so
/// the value is the same on every iteration of a loop the node is?
let invariantOutside (model: SemanticModel) (tree: SyntaxTree) (node: SyntaxNode) (e: ExpressionSyntax) =
    let rec root (e: ExpressionSyntax) =
        match e with
        | :? MemberAccessExpressionSyntax as m -> root m.Expression
        | :? ParenthesizedExpressionSyntax as p -> root p.Expression
        | e -> e

    match root e with
    | :? IdentifierNameSyntax as id ->
        match model.GetSymbolInfo(id).Symbol with
        | :? ILocalSymbol as l ->
            match declarationSpan tree l with
            | Some span -> not (node.Span.Contains span)
            | None -> false
        | :? IParameterSymbol as p ->
            // a lambda parameter declared inside the loop varies per element
            match declarationSpan tree p with
            | Some span -> not (node.Span.Contains span)
            | None -> true
        | :? IFieldSymbol
        | :? IPropertySymbol -> true
        | _ -> false
    | :? ThisExpressionSyntax -> true
    | _ -> false
