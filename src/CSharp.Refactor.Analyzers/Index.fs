/// A compilation-wide index the shape rules read instead of walking every
/// tree per declaration. Built once per compilation, lazily, and kept in a
/// weak table so a long-lived host (an IDE) drops it with the compilation.
///
/// What it answers: where a field or property is written and how (an
/// object initialiser, `this.P = …` in its own constructor, or anything
/// else), which symbols `nameof` names, which types a `DbSet<T>` holds,
/// which types are compared by reference (`==`, `ReferenceEquals`,
/// `lock`, a dictionary or set key), which types are derived from, which
/// types a struct would disturb (`T?`, `== null`, a boxing conversion, an
/// expression tree), and every identifier use of the symbols the rules
/// care about.
module CSharp.Refactor.Index

open System.Collections.Generic
open System.Runtime.CompilerServices
open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax

type WriteKind =
    /// An object initialiser or a `with` expression.
    | Initializer
    /// `this.P = …` (or `P = …`) inside a constructor of the declaring type.
    | OwnConstructor
    /// Any other write: a method, another instance, `++`, `ref`/`out`.
    | Elsewhere

type Use =
    {
        Tree: SyntaxTree
        Model: SemanticModel
        Id: IdentifierNameSyntax
    }

type CompilationIndex =
    {
        Writes: Dictionary<ISymbol, WriteKind list>
        NameOfTargets: HashSet<ISymbol>
        EntityTypes: HashSet<ISymbol>
        IdentityTypes: HashSet<ISymbol>
        DerivedFrom: HashSet<ISymbol>
        StructHostile: HashSet<ISymbol>
        /// Types whose instances are formatted: an interpolation hole, a string
        /// concatenation operand, an explicit `ToString()`, a conversion to
        /// `object` (a log or console argument).
        FormattedTypes: HashSet<ISymbol>
        /// Every construction of a type: whether it carried an initializer, and
        /// the member names that initializer assigned.
        Constructions: Dictionary<ISymbol, (bool * Set<string>) list>
        /// The types declared as deriving from (or implementing) a type.
        DerivedTypes: Dictionary<ISymbol, INamedTypeSymbol list>
        /// Identifier uses of fields, properties, locals, parameters and
        /// methods, by symbol (the name part of a member access included).
        Uses: Dictionary<ISymbol, Use list>
        /// Every string literal's value in the compilation: a member named by one
        /// (`GetField("_count")`, `GetProperty("Id")`) is reached by reflection.
        MentionedStrings: HashSet<string>
    }

let private comparer = SymbolEqualityComparer.Default

let private build (compilation: Compilation) : CompilationIndex =

    // the names a shape rule may ask uses of, read off the declarations by
    // syntax first: a tuple-typed or DateTime-typed slot, a public mutable
    // static; only identifiers spelling one of them are resolved
    let candidateNames = HashSet<string>()

    let candidateType (t: TypeSyntax) =
        let text = if isNull t then "" else t.ToString()

        text.Contains "Tuple<"
        || text = "DateTime"
        || text.EndsWith ".DateTime"
        || text = "var"

    for tree in compilation.SyntaxTrees do
        for n in tree.GetRoot().DescendantNodes() do
            match n with
            | :? VariableDeclarationSyntax as d ->
                let isPublicStatic =
                    match d.Parent with
                    | :? FieldDeclarationSyntax as f ->
                        f.Modifiers |> Seq.exists (fun k -> k.IsKind SyntaxKind.StaticKeyword)
                        && f.Modifiers |> Seq.exists (fun k -> k.IsKind SyntaxKind.PublicKeyword)
                    | _ -> false

                if candidateType d.Type || isPublicStatic then
                    for v in d.Variables do
                        candidateNames.Add v.Identifier.ValueText |> ignore
            | :? ParameterSyntax as p when candidateType p.Type -> candidateNames.Add p.Identifier.ValueText |> ignore
            | :? PropertyDeclarationSyntax as p when candidateType p.Type ->
                candidateNames.Add p.Identifier.ValueText |> ignore
            | :? MethodDeclarationSyntax as md when candidateType md.ReturnType ->
                candidateNames.Add md.Identifier.ValueText |> ignore
            | _ -> ()

    // the methods that compare their arguments or elements for equality
    let equalityMethods =
        set
            [
                "Remove"
                "Contains"
                "IndexOf"
                "LastIndexOf"
                "Distinct"
                "DistinctBy"
                "Union"
                "Except"
                "Intersect"
                "GroupBy"
                "ToLookup"
                "ToDictionary"
                "ToHashSet"
                "SequenceEqual"
                "Equals"
                "GetHashCode"
            ]

    /// One tree's findings, in its own collectors: the trees are scanned in
    /// parallel (each with its own semantic model, which is where the time
    /// goes) and merged in compilation order, so the lists come out exactly
    /// as one sequential pass over the trees would have left them.
    let scanTree (tree: SyntaxTree) =
        let writes = Dictionary<ISymbol, WriteKind list>(comparer)
        let nameOf = HashSet<ISymbol>(comparer)
        let entities = HashSet<ISymbol>(comparer)
        let identity = HashSet<ISymbol>(comparer)
        let derived = HashSet<ISymbol>(comparer)
        let hostile = HashSet<ISymbol>(comparer)
        let mentionedStrings = HashSet<string>()
        let formatted = HashSet<ISymbol>(comparer)
        let constructions = Dictionary<ISymbol, (bool * Set<string>) list>(comparer)
        let derivedTypes = Dictionary<ISymbol, INamedTypeSymbol list>(comparer)

        let addTo (d: Dictionary<ISymbol, 'a list>) (k: ISymbol) (v: 'a) =
            if not (isNull k) then
                let k = k.OriginalDefinition

                match d.TryGetValue k with
                | true, vs -> d.[k] <- v :: vs
                | _ -> d.[k] <- [ v ]

        let uses = Dictionary<ISymbol, Use list>(comparer)

        let addWrite (s: ISymbol) (k: WriteKind) =
            if not (isNull s) then
                match writes.TryGetValue s with
                | true, ks -> writes.[s] <- k :: ks
                | _ -> writes.[s] <- [ k ]

        let addUse (s: ISymbol) (u: Use) =
            match uses.TryGetValue s with
            | true, us -> uses.[s] <- u :: us
            | _ -> uses.[s] <- [ u ]

        let typeOf (m: SemanticModel) (e: SyntaxNode) = m.GetTypeInfo(e).Type

        // types are keyed by their definition: `Base<int>` in a base list derives from `Base<T>`
        let addType (set: HashSet<ISymbol>) (t: ITypeSymbol) =
            if not (isNull t) then
                set.Add(t.OriginalDefinition.WithNullableAnnotation NullableAnnotation.NotAnnotated)
                |> ignore

        let m = compilation.GetSemanticModel tree

        for n in tree.GetRoot().DescendantNodes() do
            match n with
            | :? LiteralExpressionSyntax as lit when lit.IsKind SyntaxKind.StringLiteralExpression ->
                mentionedStrings.Add(string lit.Token.Value) |> ignore
            | :? IdentifierNameSyntax as id when candidateNames.Contains id.Identifier.ValueText ->
                // only the symbols a shape rule asks about: tuple-typed slots, DateTime
                // slots, public mutable statics
                let interesting (t: ITypeSymbol) =
                    not (isNull t)
                    && (t.SpecialType = SpecialType.System_DateTime
                        || t.OriginalDefinition.ToDisplayString().StartsWith "System.Tuple<")

                match m.GetSymbolInfo(id).Symbol with
                | :? IFieldSymbol as f when
                    interesting f.Type
                    || (f.IsStatic
                        && f.DeclaredAccessibility = Accessibility.Public
                        && not f.IsReadOnly
                        && not f.IsConst)
                    ->
                    addUse f { Tree = tree; Model = m; Id = id }
                | :? IPropertySymbol as p when interesting p.Type -> addUse p { Tree = tree; Model = m; Id = id }
                | :? ILocalSymbol as l when interesting l.Type -> addUse l { Tree = tree; Model = m; Id = id }
                | :? IParameterSymbol as p when interesting p.Type -> addUse p { Tree = tree; Model = m; Id = id }
                | :? IMethodSymbol as md when interesting md.ReturnType -> addUse md { Tree = tree; Model = m; Id = id }
                | _ -> ()
            | :? AssignmentExpressionSyntax as a when (a.Left :? TupleExpressionSyntax) ->
                // a deconstruction writes each target
                for arg in (a.Left :?> TupleExpressionSyntax).Arguments do
                    addWrite (m.GetSymbolInfo(arg.Expression).Symbol) Elsewhere
            | :? AssignmentExpressionSyntax as a ->
                let target =
                    match a.Left with
                    | :? IdentifierNameSyntax
                    | :? MemberAccessExpressionSyntax -> m.GetSymbolInfo(a.Left).Symbol
                    // `x?.P = v` (C# 14) parses as `x?.(P = v)`: the assignment sits inside
                    // the conditional access, its target a member binding
                    | :? MemberBindingExpressionSyntax -> m.GetSymbolInfo(a.Left).Symbol
                    | :? ConditionalAccessExpressionSyntax as c ->
                        let rec last (e: ExpressionSyntax) =
                            match e with
                            | :? ConditionalAccessExpressionSyntax as inner -> last inner.WhenNotNull
                            | other -> other

                        match last c.WhenNotNull with
                        | :? MemberBindingExpressionSyntax as mb -> m.GetSymbolInfo(mb).Symbol
                        | :? MemberAccessExpressionSyntax as ma -> m.GetSymbolInfo(ma).Symbol
                        | _ -> null
                    | _ -> null

                if not (isNull target) then
                    let kind =
                        if not (a.IsKind SyntaxKind.SimpleAssignmentExpression) then
                            Elsewhere
                        else
                            match a.Parent with
                            | :? InitializerExpressionSyntax as i when
                                i.IsKind SyntaxKind.ObjectInitializerExpression
                                || i.IsKind SyntaxKind.WithInitializerExpression
                                ->
                                Initializer
                            | _ ->
                                let throughThis =
                                    match a.Left with
                                    | :? IdentifierNameSyntax -> true
                                    | :? MemberAccessExpressionSyntax as ma -> ma.Expression :? ThisExpressionSyntax
                                    | _ -> false

                                let ownConstructor =
                                    a.Ancestors()
                                    |> Seq.tryPick (fun x ->
                                        match x with
                                        | :? ConstructorDeclarationSyntax as c ->
                                            Some(m.GetDeclaredSymbol c :> ISymbol)
                                        | :? AnonymousFunctionExpressionSyntax
                                        | :? LocalFunctionStatementSyntax -> Some null
                                        | _ -> None)
                                    |> Option.exists (fun c ->
                                        not (isNull c) && comparer.Equals(c.ContainingType, target.ContainingType))

                                if throughThis && ownConstructor then
                                    OwnConstructor
                                else
                                    Elsewhere

                    addWrite target kind
            | :? PostfixUnaryExpressionSyntax as u -> addWrite (m.GetSymbolInfo(u.Operand).Symbol) Elsewhere
            | :? PrefixUnaryExpressionSyntax as u when
                u.IsKind SyntaxKind.PreIncrementExpression
                || u.IsKind SyntaxKind.PreDecrementExpression
                ->
                addWrite (m.GetSymbolInfo(u.Operand).Symbol) Elsewhere
            | :? ArgumentSyntax as arg when not (arg.RefKindKeyword.IsKind SyntaxKind.None) ->
                addWrite (m.GetSymbolInfo(arg.Expression).Symbol) Elsewhere
            | :? InvocationExpressionSyntax as inv when
                inv.Expression.ToString() = "nameof" && inv.ArgumentList.Arguments.Count = 1
                ->
                let s = m.GetSymbolInfo(inv.ArgumentList.Arguments.[0].Expression).Symbol

                if not (isNull s) then
                    nameOf.Add s |> ignore
            | :? InvocationExpressionSyntax as inv when inv.Expression.ToString().EndsWith "ReferenceEquals" ->
                for a in inv.ArgumentList.Arguments do
                    addType identity (typeOf m a.Expression)
            | :? InvocationExpressionSyntax as inv when
                (match inv.Expression with
                 | :? MemberAccessExpressionSyntax as ma -> equalityMethods.Contains ma.Name.Identifier.ValueText
                 | _ -> false)
                ->
                // the arguments, and the element types of the receiver
                for a in inv.ArgumentList.Arguments do
                    addType identity (typeOf m a.Expression)

                match (inv.Expression :?> MemberAccessExpressionSyntax).Expression |> typeOf m with
                | :? INamedTypeSymbol as r ->
                    addType identity r

                    for ta in r.TypeArguments do
                        addType identity ta

                    for i in r.AllInterfaces do
                        for ta in i.TypeArguments do
                            addType identity ta
                | :? IArrayTypeSymbol as arr -> addType identity arr.ElementType
                | _ -> ()
            | :? GenericNameSyntax as g ->
                let name = g.Identifier.ValueText

                if name = "DbSet" && g.TypeArgumentList.Arguments.Count = 1 then
                    addType entities (typeOf m g.TypeArgumentList.Arguments.[0])

                if
                    (name.Contains "Dictionary" || name.Contains "Set")
                    && g.TypeArgumentList.Arguments.Count >= 1
                then
                    addType identity (typeOf m g.TypeArgumentList.Arguments.[0])

                // a type argument for a `class`-constrained parameter cannot be a struct
                let constrained (ps: System.Collections.Immutable.ImmutableArray<ITypeParameterSymbol>) =
                    g.TypeArgumentList.Arguments
                    |> Seq.iteri (fun i a ->
                        if i < ps.Length && ps.[i].HasReferenceTypeConstraint then
                            addType hostile (typeOf m a))

                match m.GetSymbolInfo(g).Symbol with
                | :? IMethodSymbol as ms -> constrained ms.TypeParameters
                | :? INamedTypeSymbol as nt -> constrained nt.TypeParameters
                | _ -> ()
            | :? BinaryExpressionSyntax as b when b.IsKind SyntaxKind.AsExpression -> addType hostile (typeOf m b.Right)
            | :? BinaryExpressionSyntax as b when b.IsKind SyntaxKind.CoalesceExpression ->
                addType hostile (typeOf m b.Left)
            | :? ConditionalAccessExpressionSyntax as ca -> addType hostile (typeOf m ca.Expression)
            | :? LiteralExpressionSyntax as lit when lit.IsKind SyntaxKind.NullLiteralExpression ->
                // `Small s = null;`, `return null;`: a null converted to the type
                addType hostile (m.GetTypeInfo(lit).ConvertedType)
            | :? BinaryExpressionSyntax as b when
                b.IsKind SyntaxKind.EqualsExpression || b.IsKind SyntaxKind.NotEqualsExpression
                ->
                let l = typeOf m b.Left
                let r = typeOf m b.Right

                if b.Left.IsKind SyntaxKind.NullLiteralExpression then
                    addType hostile r
                elif b.Right.IsKind SyntaxKind.NullLiteralExpression then
                    addType hostile l
                elif (not (isNull l || isNull r)) && comparer.Equals(l, r) then
                    addType identity l
            | :? IsPatternExpressionSyntax as p when p.Pattern.ToString().Contains "null" ->
                addType hostile (typeOf m p.Expression)
            | :? LockStatementSyntax as l ->
                let t = typeOf m l.Expression
                addType identity t
                addType hostile t
            | :? NullableTypeSyntax as nt -> addType hostile (typeOf m nt.ElementType)
            | :? InterpolationSyntax as hole -> addType formatted (typeOf m hole.Expression)
            | :? BinaryExpressionSyntax as b when b.IsKind SyntaxKind.AddExpression ->
                let t = typeOf m b

                if not (isNull t) && t.SpecialType = SpecialType.System_String then
                    addType formatted (typeOf m b.Left)
                    addType formatted (typeOf m b.Right)
            | :? InvocationExpressionSyntax as inv when inv.Expression.ToString().EndsWith ".ToString" ->
                match inv.Expression with
                | :? MemberAccessExpressionSyntax as ma -> addType formatted (typeOf m ma.Expression)
                | _ -> ()
            | :? BaseListSyntax as bl ->
                let self = m.GetDeclaredSymbol bl.Parent :?> INamedTypeSymbol

                for b in bl.Types do
                    let baseType = typeOf m b.Type
                    addType derived baseType

                    if not (isNull self) then
                        addTo derivedTypes baseType self
            | :? BaseObjectCreationExpressionSyntax as c ->
                let names =
                    if isNull c.Initializer then
                        Set.empty
                    else
                        c.Initializer.Expressions
                        |> Seq.choose (fun e ->
                            match e with
                            | :? AssignmentExpressionSyntax as a -> Some(a.Left.ToString())
                            | _ -> None)
                        |> Set.ofSeq

                addTo constructions (typeOf m c) (not (isNull c.Initializer), names)
            | :? LambdaExpressionSyntax as l ->
                // an expression tree: every type read inside is translated by a provider
                let converted = m.GetTypeInfo(l).ConvertedType

                if
                    not (isNull converted)
                    && converted.OriginalDefinition.ToDisplayString().StartsWith "System.Linq.Expressions.Expression<"
                then
                    for d in l.DescendantNodes() do
                        match d with
                        | :? ExpressionSyntax as e when not (e :? TypeSyntax) -> addType hostile (typeOf m e)
                        | _ -> ()
            | _ -> ()

            // a boxing conversion or an expression-tree position of any expression
            match n with
            | :? ExpressionSyntax as e when not (e :? TypeSyntax) ->
                let info = m.GetTypeInfo e

                if not (isNull info.Type) && info.Type.TypeKind <> TypeKind.Error then
                    let converted = info.ConvertedType

                    if
                        not (isNull converted)
                        && not (comparer.Equals(converted, info.Type))
                        && (converted.SpecialType = SpecialType.System_Object
                            || converted.TypeKind = TypeKind.Interface)
                    then
                        addType hostile info.Type

                        if converted.SpecialType = SpecialType.System_Object then
                            addType formatted info.Type
            | _ -> ()

        writes,
        nameOf,
        entities,
        identity,
        derived,
        hostile,
        mentionedStrings,
        formatted,
        constructions,
        derivedTypes,
        uses

    let scanned = compilation.SyntaxTrees |> Array.ofSeq |> Array.Parallel.map scanTree

    let writes = Dictionary<ISymbol, WriteKind list>(comparer)
    let nameOf = HashSet<ISymbol>(comparer)
    let entities = HashSet<ISymbol>(comparer)
    let identity = HashSet<ISymbol>(comparer)
    let derived = HashSet<ISymbol>(comparer)
    let hostile = HashSet<ISymbol>(comparer)
    let mentionedStrings = HashSet<string>()
    let formatted = HashSet<ISymbol>(comparer)
    let constructions = Dictionary<ISymbol, (bool * Set<string>) list>(comparer)
    let derivedTypes = Dictionary<ISymbol, INamedTypeSymbol list>(comparer)
    let uses = Dictionary<ISymbol, Use list>(comparer)

    // a tree's list holds its entries newest first, as the whole does: the
    // tree's entries go in front of the earlier trees', as one pass would
    let mergeLists (into: Dictionary<ISymbol, 'a list>) (from: Dictionary<ISymbol, 'a list>) =
        for kv in from do
            let existing =
                match into.TryGetValue kv.Key with
                | true, xs -> xs
                | _ -> []

            into.[kv.Key] <- kv.Value @ existing

    for w, n, e, i, d, h, s, f, c, dt, u in scanned do
        mergeLists writes w
        nameOf.UnionWith n
        entities.UnionWith e
        identity.UnionWith i
        derived.UnionWith d
        hostile.UnionWith h
        mentionedStrings.UnionWith s
        formatted.UnionWith f
        mergeLists constructions c
        mergeLists derivedTypes dt
        mergeLists uses u

    {
        Writes = writes
        NameOfTargets = nameOf
        EntityTypes = entities
        IdentityTypes = identity
        DerivedFrom = derived
        StructHostile = hostile
        FormattedTypes = formatted
        Constructions = constructions
        DerivedTypes = derivedTypes
        Uses = uses
        MentionedStrings = mentionedStrings
    }

let private table = ConditionalWeakTable<Compilation, Lazy<CompilationIndex>>()

/// The time a thread has spent in `ofCompilation` — building an index, or
/// waiting for the thread that is — so a rule's timer can leave it out: the
/// build is one cost per compilation, not the bill of whichever rule asked
/// first, nor of every rule that stood behind it.
type private Clock private () =
    [<System.ThreadStatic; DefaultValue>]
    static val mutable private inside: int64

    static member Inside
        with get () = Clock.inside
        and set (v: int64) = Clock.inside <- v

let private buildTicks = ref 0L

/// Stopwatch ticks this thread has spent in `ofCompilation` so far.
let threadInsideTicks () = Clock.Inside

/// Stopwatch ticks spent building indexes since the last reset, all threads.
let buildTicksSoFar () =
    System.Threading.Interlocked.Read &buildTicks.contents

let resetBuildTicks () =
    System.Threading.Interlocked.Exchange(&buildTicks.contents, 0L) |> ignore

/// The index of a compilation, built on first use.
let ofCompilation (compilation: Compilation) : CompilationIndex =
    let sw = System.Diagnostics.Stopwatch.StartNew()

    let lazyIndex =
        table.GetValue(
            compilation,
            fun c ->
                lazy
                    (let building = System.Diagnostics.Stopwatch.StartNew()
                     let index = build c

                     System.Threading.Interlocked.Add(&buildTicks.contents, building.ElapsedTicks)
                     |> ignore

                     index)
        )

    let index = lazyIndex.Value
    Clock.Inside <- Clock.Inside + sw.ElapsedTicks
    index

let private key (t: ITypeSymbol) =
    t.WithNullableAnnotation NullableAnnotation.NotAnnotated

let writesOf (index: CompilationIndex) (s: ISymbol) : WriteKind list =
    match index.Writes.TryGetValue s with
    | true, ks -> ks
    | _ -> []

let usesOf (index: CompilationIndex) (s: ISymbol) : Use list =
    match index.Uses.TryGetValue s with
    | true, us -> us
    | _ -> []

let isEntity (index: CompilationIndex) (t: ITypeSymbol) = index.EntityTypes.Contains(key t)
let identityUsed (index: CompilationIndex) (t: ITypeSymbol) = index.IdentityTypes.Contains(key t)
let isDerivedFrom (index: CompilationIndex) (t: ITypeSymbol) = index.DerivedFrom.Contains(key t)
let structHostile (index: CompilationIndex) (t: ITypeSymbol) = index.StructHostile.Contains(key t)
let formatted (index: CompilationIndex) (t: ITypeSymbol) = index.FormattedTypes.Contains(key t)

let constructionsOf (index: CompilationIndex) (t: ITypeSymbol) =
    match index.Constructions.TryGetValue(key t) with
    | true, cs -> cs
    | _ -> []

let derivedTypesOf (index: CompilationIndex) (t: ITypeSymbol) =
    match index.DerivedTypes.TryGetValue(key t) with
    | true, ds -> ds
    | _ -> []

let namedByNameOf (index: CompilationIndex) (s: ISymbol) = index.NameOfTargets.Contains s

/// Is the name spelled as a string anywhere in the compilation — the
/// shape of `GetField("name")`, `GetProperty("name")`, a binder's key?
let mentionedAsString (index: CompilationIndex) (name: string) = index.MentionedStrings.Contains name
