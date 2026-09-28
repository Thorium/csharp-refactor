/// The language ladder, part one: the rewrites C# 11 – C# 14 and .NET 7+
/// make possible, each gated on the file's effective language version as
/// a number (`RuleContext.languageAtLeast`) and, where a type is needed,
/// on the compilation.
///
/// CR0148 (performance, fix, C# 11): `Encoding.UTF8.GetBytes("literal")`
/// on a constant whose every character is below U+0080 is `"literal"u8`
/// — bare where a `ReadOnlySpan<byte>` is expected, `.ToArray()` where a
/// `byte[]` is (the allocation stays, the transcoding goes). `UTF8` and
/// `ASCII` bound to the BCL properties.
///
/// CR0149 (idiom, fix, C# 11, API): an `init`/`set` property with no
/// initialiser that every object initialiser in the compilation sets and
/// no constructor assigns is `required`. Guards: at least one
/// initialiser; no constructor of the type assigns it; the type is not
/// deserialized by the shape heuristic (a serializer constructs without
/// initialisers); `required` is a demand on callers, so the scope gate
/// of the shape rules applies; every construction of a DERIVED type in the
/// compilation sets it too (`new Derived()` would be CS9035); no type of
/// the family is a `new()`-constrained type argument (CS9040); a type seen
/// beyond the compilation (public in a library, internal with friends)
/// needs the host's oracle, every site there a construction setting it —
/// without the oracle it stands down.
///
/// CR0150 (performance, fix, .NET 8, API): a `static readonly
/// Dictionary<K,V>`/`HashSet<T>` filled in its initialiser and only ever
/// read is a `FrozenDictionary<K,V>`/`FrozenSet<T>` via
/// `.ToFrozenDictionary()`/`.ToFrozenSet()`. Guards: the scope gate;
/// every reference — in this file, in the other files of the compilation,
/// and, for a field seen beyond it, at every site the host's oracle finds
/// (no oracle: it stands down) — is an order-independent read
/// (`TryGetValue`, an indexer get, `ContainsKey`, `Contains`, `Count`,
/// `GetValueOrDefault`, `Any`/`All`/`Sum`/`Min`/`Max`), never a write, an
/// increment, a by-ref pass or an enumeration (a frozen collection
/// enumerates in its own order); the comparer argument travels;
/// `System.Collections.Frozen` resolves; the `using` is added.
///
/// CR0152 (performance, fix, C# 13 + .NET 9): `private readonly object
/// _gate = new();` whose every reference is a `lock` operand is `private
/// readonly Lock _gate = new();` — the dedicated type skips the
/// object-header path. `Monitor.*`, passing or comparing it vetoes, in
/// every part of a partial type; `System.Threading.Lock` resolves.
///
/// CR0153 (idiom, fix, C# 14): a property whose private backing field is
/// referenced only inside that property's own accessors uses the `field`
/// keyword, the backing field removed and its initialiser moved to the
/// property. Guards: the field is private, unattributed, not `volatile`
/// or `[ThreadStatic]`; a constructor writing it vetoes; the name `field`
/// is not already an identifier in the type.
///
/// CR0154 (idiom, fix, C# 14): `if (x != null) x.P = v;`, `if (x is not
/// null) x[i] = v;` is `x?.P = v;`. Guards: the condition is a null test
/// of a pure read `x` (a `!=` the built-in reference test, never a
/// user-defined operator), the body exactly one assignment (compound
/// included) whose target is `x.P`/`x[i]` with `x` the same reference, no
/// `else`; `x` not assigned inside the body. Yields to IDE0031.
///
/// CR0157 (idiom, fix, C# 8 + .NET 7): a `switch` expression over an enum
/// or a sealed hierarchy every case of which the switch lists, whose
/// discard arm throws a parameterless `InvalidOperationException`/
/// `SwitchExpressionException`/`ArgumentOutOfRangeException`, throws
/// `UnreachableException` instead. A message argument keeps the fix down
/// to a note (the message may be a contract);
/// `System.Diagnostics.UnreachableException` resolves.
module CSharp.Refactor.Ladder

open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax
open Microsoft.CodeAnalysis.Text

[<Literal>]
let Utf8Code = "CR0148"

[<Literal>]
let RequiredCode = "CR0149"

[<Literal>]
let FrozenCode = "CR0150"

[<Literal>]
let LockCode = "CR0152"

[<Literal>]
let FieldKeywordCode = "CR0153"

[<Literal>]
let NullConditionalAssignCode = "CR0154"

[<Literal>]
let AutoPropertyCode = "CR0186"

[<Literal>]
let UnreachableCode = "CR0157"

let private resolves (model: SemanticModel) (metadataName: string) =
    not (isNull (model.Compilation.GetTypeByMetadataName metadataName))

/// A symbol's accessibility as its containers narrow it; a file-local type
/// is as private as a private one.
let rec private effective (s: ISymbol) =
    match s with
    | null -> Accessibility.Public
    | s ->
        let own =
            match s with
            | :? INamedTypeSymbol as t when t.IsFileLocal -> Accessibility.Private
            | _ -> s.DeclaredAccessibility

        let outer = effective s.ContainingType

        if own = Accessibility.Private || outer = Accessibility.Private then
            Accessibility.Private
        elif own = Accessibility.Internal || outer = Accessibility.Internal then
            Accessibility.Internal
        else
            own

/// The scope gate of the shape rules.
let private shapeOpen (ctx: RuleContext) (s: ISymbol) =
    match effective s with
    | Accessibility.Private -> true
    | Accessibility.Internal
    | Accessibility.ProtectedAndInternal -> RuleContext.internalShapeOpen ctx
    | _ -> RuleContext.publicShapeOpen ctx

/// Is the symbol seen beyond this compilation — public in a library,
/// internal with friends? Then only the host's oracle knows every use.
let private exported (ctx: RuleContext) (s: ISymbol) =
    match effective s with
    | Accessibility.Private -> false
    | Accessibility.Internal
    | Accessibility.ProtectedAndInternal -> ctx.HasFriends
    | _ -> not ctx.IsLeaf

/// Every name bound to the symbol in the OTHER trees of this compilation
/// (a member's name part, a type's name, a constructor call of the type),
/// then — when the symbol is exported — every reference site the host's
/// oracle finds beyond the compilation. `None` when a use may be unseen:
/// an exported symbol and no oracle, or a site the oracle could not read.
let private usesElsewhere (tree: SyntaxTree) (model: SemanticModel) (ctx: RuleContext) (s: ISymbol) =
    let compilation = model.Compilation

    let same (x: ISymbol) =
        not (isNull x)
        && (SymbolEqualityComparer.Default.Equals(x, s)
            || SymbolEqualityComparer.Default.Equals(x.OriginalDefinition, s))

    let inCompilation =
        compilation.SyntaxTrees
        |> Seq.filter (fun t -> t <> tree)
        |> Seq.collect (fun t ->
            let root = t.GetRoot()

            if not (root.ToFullString().Contains s.Name) then
                Seq.empty
            else
                let m = compilation.GetSemanticModel t

                root.DescendantNodes()
                |> Seq.choose (fun n ->
                    match n with
                    | :? SimpleNameSyntax as id when id.Identifier.ValueText = s.Name ->
                        match m.GetSymbolInfo(id).Symbol with
                        | bound when same bound -> Some(id :> SyntaxNode, m)
                        | :? IMethodSymbol as ctor when
                            ctor.MethodKind = MethodKind.Constructor && same ctor.ContainingType
                            ->
                            Some(id :> SyntaxNode, m)
                        | _ -> None
                    | _ -> None))
        |> List.ofSeq

    if not (exported ctx s) then
        ValueSome inCompilation
    else
        match RuleContext.referencesOf ctx s with
        | None -> ValueNone
        | Some sites when sites |> List.exists (fun site -> isNull site.Node || isNull site.Model) -> ValueNone
        | Some sites ->
            let beyond =
                sites
                |> List.filter (fun site -> not (compilation.ContainsSyntaxTree site.Tree))
                |> List.map (fun site -> site.Node, site.Model)

            ValueSome(inCompilation @ beyond)

// ---- CR0148 ----

let private utf8Literals (tree: SyntaxTree) (model: SemanticModel) (ctx: RuleContext) : Suggestion list =
    if not (RuleContext.languageAtLeast ctx 11) then
        []
    else
        tree.GetRoot().DescendantNodes()
        |> Seq.choose (fun n ->
            match n with
            | :? InvocationExpressionSyntax as inv when
                Linq.nameOf inv = "GetBytes"
                && inv.ArgumentList.Arguments.Count = 1
                && not (Text.insideExpressionTree model inv)
                ->
                match inv.Expression, model.GetSymbolInfo(inv).Symbol with
                | (:? MemberAccessExpressionSyntax as m), (:? IMethodSymbol as gb) when
                    gb.ContainingType.ToDisplayString() = "System.Text.Encoding"
                    && (match model.GetSymbolInfo(m.Expression).Symbol with
                        | :? IPropertySymbol as p ->
                            (p.Name = "UTF8" || p.Name = "ASCII")
                            && p.ContainingType.ToDisplayString() = "System.Text.Encoding"
                        | _ -> false)
                    ->
                    let arg = inv.ArgumentList.Arguments.[0].Expression

                    match arg, model.GetConstantValue arg with
                    | (:? LiteralExpressionSyntax as lit), c when
                        c.HasValue
                        && (c.Value :? string)
                        && (c.Value :?> string) |> Seq.forall (fun ch -> int ch < 0x80)
                        && lit.Token.Text.StartsWith "\""
                        ->
                        // a span target drops the array; anything else keeps it
                        let converted = model.GetTypeInfo(inv).ConvertedType

                        let spanTarget =
                            not (isNull converted)
                            && converted.OriginalDefinition.ToDisplayString() = "System.ReadOnlySpan<T>"

                        let replacement = lit.Token.Text + "u8" + (if spanTarget then "" else ".ToArray()")

                        let edit = Suggestion.replace inv.Span replacement

                        if Guards.speculativeCheck model [ edit ] then
                            Some
                                {
                                    Code = Utf8Code
                                    Message =
                                        (if spanTarget then
                                             "The bytes of a constant are a u8 literal: no transcoding, no allocation"
                                         else
                                             "The bytes of a constant are a u8 literal: the transcoding goes (the array stays)")
                                    Span = inv.Span
                                    Fixes = [ Suggestion.fix "Use a u8 literal" Utf8Code [ edit ] ]
                                }
                        else
                            None
                    | _ -> None
                | _ -> None
            | _ -> None)
        |> List.ofSeq

// ---- CR0149 ----

let private serializerWords =
    [
        "Json"
        "Xml"
        "Bson"
        "DataMember"
        "Serializ"
        "Proto"
        "MessagePack"
        "Column"
        "Table"
    ]

let private serialized (s: ISymbol) =
    s.GetAttributes()
    |> Seq.exists (fun a -> serializerWords |> List.exists a.AttributeClass.Name.Contains)

/// The type and every type deriving from it in this compilation.
let private withDerived (index: Index.CompilationIndex) (t: INamedTypeSymbol) =
    let seen =
        System.Collections.Generic.HashSet<ISymbol>(SymbolEqualityComparer.Default)

    let rec walk (t: INamedTypeSymbol) =
        if seen.Add t then
            for d in Index.derivedTypesOf index t do
                walk d

    walk t
    seen |> List.ofSeq

/// Is one of the types a type argument for a `new()`-constrained type
/// parameter anywhere in the compilation (`Make<Options>()`, `List<T>` with
/// the constraint, an inferred generic call)? A required member makes that
/// argument CS9040. The compilation's `new()`-constrained arguments are
/// gathered once: every candidate property asks, and a walk of the whole
/// compilation per property costs seconds a file.
let private newConstrainedArguments =
    System.Runtime.CompilerServices.ConditionalWeakTable<Compilation, Lazy<System.Collections.Generic.HashSet<ISymbol>>>()

let private newConstrainedArgument (compilation: Compilation) (types: ISymbol list) =
    let arguments =
        newConstrainedArguments
            .GetValue(
                compilation,
                fun compilation ->
                    lazy
                        (let found =
                            System.Collections.Generic.HashSet<ISymbol>(SymbolEqualityComparer.Default)

                         let add
                             (parameters: System.Collections.Immutable.ImmutableArray<ITypeParameterSymbol>)
                             (arguments: System.Collections.Immutable.ImmutableArray<ITypeSymbol>)
                             =
                             for p, a in Seq.zip parameters arguments do
                                 if p.HasConstructorConstraint && not (isNull a) then
                                     lock found (fun () -> found.Add a.OriginalDefinition |> ignore)

                         compilation.SyntaxTrees
                         |> Array.ofSeq
                         |> Array.Parallel.iter (fun t ->
                             let m = compilation.GetSemanticModel t

                             for n in t.GetRoot().DescendantNodes() do
                                 match n with
                                 | :? GenericNameSyntax
                                 | :? InvocationExpressionSyntax ->
                                     match m.GetSymbolInfo(n).Symbol with
                                     | :? IMethodSymbol as ms when ms.IsGenericMethod ->
                                         add ms.TypeParameters ms.TypeArguments
                                     | :? INamedTypeSymbol as nt when nt.IsGenericType ->
                                         add nt.TypeParameters nt.TypeArguments
                                     | _ -> ()
                                 | _ -> ())

                         found)
            )
            .Value

    types |> List.exists arguments.Contains

/// Does `required` leave every construction compiling: each construction of
/// a DERIVED type sets the member too (`new Derived()` is CS9035), no type of
/// the family is a `new()` argument (CS9040), and — for a type seen beyond
/// the compilation — every site the host's oracle finds there is a
/// construction setting it (without an oracle such a type stands down).
let private requiredStaysSatisfied
    (model: SemanticModel)
    (ctx: RuleContext)
    (index: Index.CompilationIndex)
    (property: IPropertySymbol)
    (setsIt: (bool * Set<string>) list -> bool)
    =
    let family = withDerived index property.ContainingType

    let derivedSet =
        family
        |> List.forall (fun t ->
            match t with
            | :? INamedTypeSymbol as nt -> setsIt (Index.constructionsOf index nt)
            | _ -> false)

    // a site in another project: a construction setting the member, or a mention
    // that constructs nothing (a declared type, a cast) — never a base type or a
    // type argument there
    let siteSafe (node: SyntaxNode) =
        let rec name (n: SyntaxNode) =
            match n.Parent with
            | :? QualifiedNameSyntax as q when q.Right.Span = n.Span -> name q
            | :? AliasQualifiedNameSyntax as q when q.Name.Span = n.Span -> name q
            | _ -> n

        let spelled = name node

        let creation =
            match spelled, spelled.Parent with
            | (:? BaseObjectCreationExpressionSyntax as c), _ -> Some c
            | _, (:? ObjectCreationExpressionSyntax as c) when c.Type.Span = spelled.Span ->
                Some(c :> BaseObjectCreationExpressionSyntax)
            | _ -> None

        match creation with
        | Some c ->
            not (isNull c.Initializer)
            && c.Initializer.Expressions
               |> Seq.exists (fun e ->
                   match e with
                   | :? AssignmentExpressionSyntax as a -> a.Left.ToString() = property.Name
                   | _ -> false)
        | None ->
            not (
                spelled.AncestorsAndSelf()
                |> Seq.exists (fun a -> a :? BaseListSyntax || a :? TypeArgumentListSyntax)
            )

    let beyondSafe () =
        family
        |> List.forall (fun t ->
            not (exported ctx t)
            || (match RuleContext.referencesOf ctx t with
                | None -> false
                | Some sites ->
                    sites
                    |> List.forall (fun site ->
                        not (isNull site.Node)
                        && (model.Compilation.ContainsSyntaxTree site.Tree || siteSafe site.Node))))

    derivedSet
    && beyondSafe ()
    && not (newConstrainedArgument model.Compilation family)

let private requiredMembers (tree: SyntaxTree) (model: SemanticModel) (ctx: RuleContext) : Suggestion list =
    if not (RuleContext.languageAtLeast ctx 11) then
        []
    else
        let index = lazy (Index.ofCompilation model.Compilation)

        tree.GetRoot().DescendantNodes()
        |> Seq.choose (fun n ->
            match n with
            | :? PropertyDeclarationSyntax as p when
                isNull p.Initializer
                && not (isNull p.AccessorList)
                && not (
                    p.Modifiers
                    |> Seq.exists (fun m ->
                        m.IsKind SyntaxKind.RequiredKeyword
                        || m.IsKind SyntaxKind.StaticKeyword
                        || m.IsKind SyntaxKind.OverrideKeyword
                        || m.IsKind SyntaxKind.VirtualKeyword
                        || m.IsKind SyntaxKind.AbstractKeyword)
                )
                && p.AccessorList.Accessors
                   |> Seq.exists (fun a ->
                       a.IsKind SyntaxKind.SetAccessorDeclaration
                       || a.IsKind SyntaxKind.InitAccessorDeclaration)
                && p.AccessorList.Accessors
                   |> Seq.forall (fun a -> isNull a.Body && isNull a.ExpressionBody && a.Modifiers.Count = 0)
                ->
                match model.GetDeclaredSymbol p with
                | property when
                    not (isNull property)
                    && (property.ContainingType.TypeKind = TypeKind.Class
                        || property.ContainingType.TypeKind = TypeKind.Struct)
                    && not (serialized property)
                    && not (serialized property.ContainingType)
                    && not (Index.isEntity index.Value property.ContainingType)
                    && shapeOpen ctx property
                    && property.ExplicitInterfaceImplementations.IsEmpty
                    ->
                    let writes = Index.writesOf index.Value property

                    // every write an initialiser, and every construction of the type an initialiser setting it
                    let constructions = Index.constructionsOf index.Value property.ContainingType

                    let setsIt (constructions: (bool * Set<string>) list) =
                        constructions
                        |> List.forall (fun (hasInitializer, names) -> hasInitializer && names.Contains property.Name)

                    let everyConstructionSets = not constructions.IsEmpty && setsIt constructions

                    if
                        not writes.IsEmpty
                        && writes |> List.forall (fun w -> w = Index.Initializer)
                        && everyConstructionSets
                        && requiredStaysSatisfied model ctx index.Value property setsIt
                    then
                        // `required` goes after the accessibility
                        let edit = Suggestion.insert p.Type.SpanStart "required "

                        Some(
                            {
                                Code = RequiredCode
                                Message =
                                    "Every construction sets the property and nothing else does: 'required' makes the compiler keep it so"
                                Span = p.Identifier.Span
                                Fixes = [ Suggestion.fix "Make it required" RequiredCode [ edit ] ]
                            },
                            [ edit ]
                        )
                    else
                        None
                | _ -> None
            | _ -> None)
        |> List.ofSeq
        // a model file of forty such properties is one re-bind, not forty
        |> Guards.speculativeCheckEach model

// ---- CR0150 ----

/// The reads a frozen collection answers the same way: lookups and
/// order-independent aggregates. Enumeration is absent — a frozen
/// collection enumerates in its own order, a Dictionary in insertion order.
let private readOnlyMembers =
    set
        [
            "TryGetValue"
            "ContainsKey"
            "Contains"
            "Count"
            "GetValueOrDefault"
            "Any"
            "All"
            "Sum"
            "Min"
            "Max"
            "Count"
        ]

let private frozenCollections (tree: SyntaxTree) (model: SemanticModel) (ctx: RuleContext) : Suggestion list =
    if not (resolves model "System.Collections.Frozen.FrozenDictionary`2") then
        []
    else
        tree.GetRoot().DescendantNodes()
        |> Seq.choose (fun n ->
            match n with
            | :? FieldDeclarationSyntax as fd when
                fd.Declaration.Variables.Count = 1
                && fd.Modifiers |> Seq.exists (fun m -> m.IsKind SyntaxKind.StaticKeyword)
                && fd.Modifiers |> Seq.exists (fun m -> m.IsKind SyntaxKind.ReadOnlyKeyword)
                && not (isNull fd.Declaration.Variables.[0].Initializer)
                ->
                let v = fd.Declaration.Variables.[0]

                match model.GetDeclaredSymbol v with
                | :? IFieldSymbol as field when shapeOpen ctx field ->
                    let typeName = field.Type.OriginalDefinition.ToDisplayString()

                    let target =
                        match typeName with
                        | "System.Collections.Generic.Dictionary<TKey, TValue>" ->
                            Some("FrozenDictionary", "ToFrozenDictionary")
                        | "System.Collections.Generic.HashSet<T>" -> Some("FrozenSet", "ToFrozenSet")
                        | _ -> None

                    match target, v.Initializer.Value with
                    | Some(frozenType, converter), (:? ObjectCreationExpressionSyntax as init) when
                        not (isNull init.Initializer) && init.Initializer.Expressions.Count > 0
                        ->
                        // every reference is a read; a private field lives in this file (a
                        // partial type spread over files stands down)
                        let spread = field.ContainingType.DeclaringSyntaxReferences.Length > 1

                        let reads =
                            if spread then
                                []
                            else
                                // a private field is named inside its type only
                                (if field.DeclaredAccessibility = Accessibility.Private then
                                     Guards.privateMemberScope tree field
                                 else
                                     [ tree.GetRoot() ])
                                |> Seq.collect (fun root -> root.DescendantNodes())
                                |> Seq.choose (fun x ->
                                    match x with
                                    | :? IdentifierNameSyntax as id when
                                        id.Identifier.ValueText = field.Name
                                        && SymbolEqualityComparer.Default.Equals(
                                            model.GetSymbolInfo(id).Symbol,
                                            field
                                        )
                                        ->
                                        Some(id :> SyntaxNode)
                                    | _ -> None)
                                |> List.ofSeq

                        let isRead (id: SyntaxNode) =
                            let e: SyntaxNode =
                                match id.Parent with
                                | :? MemberAccessExpressionSyntax as ma when ma.Name.Span = id.Span -> ma
                                | _ -> id

                            match e.Parent with
                            | :? MemberAccessExpressionSyntax as ma when ma.Expression.Span = e.Span ->
                                readOnlyMembers.Contains ma.Name.Identifier.ValueText
                            | :? ElementAccessExpressionSyntax as ea when ea.Expression.Span = e.Span ->
                                // an indexer read, not a set, an increment or a by-ref pass
                                match ea.Parent with
                                | :? AssignmentExpressionSyntax as a -> a.Left.Span <> ea.Span
                                | :? PostfixUnaryExpressionSyntax as u ->
                                    u.IsKind SyntaxKind.SuppressNullableWarningExpression
                                | :? PrefixUnaryExpressionSyntax as u ->
                                    not (
                                        u.IsKind SyntaxKind.PreIncrementExpression
                                        || u.IsKind SyntaxKind.PreDecrementExpression
                                    )
                                | :? ArgumentSyntax as a -> a.RefKindKeyword.IsKind SyntaxKind.None
                                | _ -> true
                            | :? EqualsValueClauseSyntax when (e.Parent.Parent :? VariableDeclaratorSyntax) ->
                                // the declaration itself
                                (e.Parent.Parent :?> VariableDeclaratorSyntax).Identifier.Span = v.Identifier.Span
                            | _ -> false

                        // the uses in other files (other parts of the class, callers of
                        // an internal or public field): a write there breaks the build,
                        // a `foreach` there changes its order silently
                        let elsewhereReads () =
                            match usesElsewhere tree model ctx field with
                            | ValueNone -> false
                            | ValueSome uses -> uses |> List.forall (fun (id, _) -> isRead id)

                        let allReads =
                            reads |> List.forall isRead
                            && (effective field = Accessibility.Private || elsewhereReads ())

                        if not allReads || reads.IsEmpty then
                            None
                        else
                            let typeArgs =
                                match field.Type with
                                | :? INamedTypeSymbol as nt ->
                                    nt.TypeArguments
                                    |> Seq.map (fun a -> a.ToMinimalDisplayString(model, fd.SpanStart))
                                    |> String.concat ", "
                                | _ -> ""

                            // the comparer travels: the constructor's arguments become the converter's
                            let comparerArgs =
                                if isNull init.ArgumentList || init.ArgumentList.Arguments.Count = 0 then
                                    ""
                                else
                                    init.ArgumentList.Arguments
                                    |> Seq.filter (fun a ->
                                        match model.GetTypeInfo(a.Expression).Type with
                                        | null -> false
                                        | t -> t.Name.Contains "Comparer")
                                    |> Seq.map (fun a -> a.ToString())
                                    |> String.concat ", "

                            match Usings.importEdit model tree fd.SpanStart "System.Collections.Frozen" frozenType with
                            | None -> None
                            | Some usingEdits ->
                                let edits =
                                    usingEdits
                                    @ [
                                        Suggestion.replace fd.Declaration.Type.Span ($"{frozenType}<{typeArgs}>")
                                        Suggestion.insert init.Span.End ($".{converter}({comparerArgs})")
                                    ]

                                if Guards.speculativeCheck model edits then
                                    Some
                                        {
                                            Code = FrozenCode
                                            Message =
                                                $"'{field.Name}' is filled once and only read: a {frozenType} is built for lookups"
                                            Span = fd.Declaration.Type.Span
                                            Fixes = [ Suggestion.fix ("Make it a " + frozenType) FrozenCode edits ]
                                        }
                                else
                                    None
                    | _ -> None
                | _ -> None
            | _ -> None)
        |> List.ofSeq

// ---- CR0152 ----

let private lockObjects (tree: SyntaxTree) (model: SemanticModel) (ctx: RuleContext) : Suggestion list =
    if
        not (RuleContext.languageAtLeast ctx 13)
        || not (resolves model "System.Threading.Lock")
    then
        []
    else
        tree.GetRoot().DescendantNodes()
        |> Seq.choose (fun n ->
            match n with
            | :? FieldDeclarationSyntax as fd when
                fd.Declaration.Variables.Count = 1
                && fd.Modifiers |> Seq.exists (fun m -> m.IsKind SyntaxKind.PrivateKeyword)
                && fd.Modifiers |> Seq.exists (fun m -> m.IsKind SyntaxKind.ReadOnlyKeyword)
                && (match fd.Declaration.Type with
                    | :? PredefinedTypeSyntax as p -> p.Keyword.IsKind SyntaxKind.ObjectKeyword
                    | _ -> false)
                ->
                let v = fd.Declaration.Variables.[0]

                let initOk =
                    not (isNull v.Initializer)
                    && (match v.Initializer.Value with
                        | :? ImplicitObjectCreationExpressionSyntax as c -> c.ArgumentList.Arguments.Count = 0
                        | :? ObjectCreationExpressionSyntax as c ->
                            c.Type.ToString() = "object"
                            && (isNull c.ArgumentList || c.ArgumentList.Arguments.Count = 0)
                        | _ -> false)

                match model.GetDeclaredSymbol v with
                | :? IFieldSymbol as field when initOk ->
                    // the field is private: named inside its type only
                    let uses =
                        Guards.privateMemberScope tree field
                        |> Seq.collect (fun root -> root.DescendantNodes())
                        |> Seq.choose (fun x ->
                            match x with
                            | :? IdentifierNameSyntax as id when
                                id.Identifier.ValueText = field.Name
                                && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(id).Symbol, field)
                                ->
                                Some(id :> SyntaxNode)
                            | _ -> None)
                        |> List.ofSeq

                    let lockOperand (id: SyntaxNode) =
                        let e: SyntaxNode =
                            match id.Parent with
                            | :? MemberAccessExpressionSyntax as ma when
                                ma.Name.Span = id.Span && (ma.Expression :? ThisExpressionSyntax)
                                ->
                                ma :> SyntaxNode
                            | _ -> id

                        match e.Parent with
                        | :? LockStatementSyntax as l -> l.Expression.Span = e.Span
                        | _ -> false

                    // the other parts of a partial type see the field too
                    let onlyLocked =
                        not uses.IsEmpty
                        && uses |> List.forall lockOperand
                        && (field.ContainingType.DeclaringSyntaxReferences.Length <= 1
                            || (match usesElsewhere tree model ctx field with
                                | ValueSome elsewhere -> elsewhere |> List.forall (fun (id, _) -> lockOperand id)
                                | ValueNone -> false))

                    if onlyLocked then
                        let edits =
                            (Usings.importEdit model tree fd.SpanStart "System.Threading" "Lock"
                             |> Option.defaultValue [])
                            @ [
                                Suggestion.replace fd.Declaration.Type.Span "Lock"
                                Suggestion.replace v.Initializer.Value.Span "new()"
                            ]

                        if Guards.speculativeCheck model edits then
                            Some
                                {
                                    Code = LockCode
                                    Message =
                                        "A gate object used only by lock is a System.Threading.Lock: the dedicated type skips the object-header path"
                                    Span = fd.Declaration.Type.Span
                                    Fixes = [ Suggestion.fix "Use System.Threading.Lock" LockCode edits ]
                                }
                        else
                            None
                    else
                        None
                | _ -> None
            | _ -> None)
        |> List.ofSeq

// ---- CR0153 / CR0186 ----

/// The one private field a property's accessors reference, when nothing else
/// in the type reaches it: the backing field CR0153 names `field` and CR0186
/// makes an auto-property's. Guards shared by both: unattributed, not
/// `volatile`, declared alone, of the property's own type, referenced only
/// inside this property's accessors in every part of the type, named by no
/// `nameof` and no string (reflection by name), its initialiser pure (it
/// moves among the initialisers), no comment on its declaration.
/// A type part's identifiers by name, walked once: every property of the
/// type asks for its field's, and a walk per property is quadratic in a DTO
/// of hundreds of them.
let private identifiersByName =
    System.Runtime.CompilerServices.ConditionalWeakTable<SyntaxNode, System.Linq.ILookup<string, IdentifierNameSyntax>>()

let private identifiersNamed (part: SyntaxNode) (name: string) =
    identifiersByName
        .GetValue(
            part,
            fun part ->
                System.Linq.Enumerable.ToLookup(
                    part.DescendantNodes()
                    |> Seq.choose (fun n ->
                        match n with
                        | :? IdentifierNameSyntax as id -> Some id
                        | _ -> None),
                    fun (id: IdentifierNameSyntax) -> id.Identifier.ValueText
                )
        )
        .[name]

let private soleBackingField (tree: SyntaxTree) (model: SemanticModel) (p: PropertyDeclarationSyntax) =
    let accessorNodes = p.AccessorList.Accessors |> List.ofSeq

    let referenced =
        accessorNodes
        |> List.collect (fun a ->
            a.DescendantNodes()
            |> Seq.choose (fun x ->
                match x with
                | :? IdentifierNameSyntax as id ->
                    match model.GetSymbolInfo(id).Symbol with
                    | :? IFieldSymbol as f when
                        f.DeclaredAccessibility = Accessibility.Private
                        && not f.IsStatic
                        && not f.IsConst
                        && SymbolEqualityComparer.Default.Equals(
                            f.ContainingType,
                            model.GetDeclaredSymbol(p).ContainingType
                        )
                        ->
                        Some f
                    | _ -> None
                | _ -> None)
            |> List.ofSeq)
        |> List.distinct

    match referenced with
    | [ backing ] when
        backing.GetAttributes().IsEmpty
        && not backing.IsVolatile
        && backing.DeclaringSyntaxReferences.Length = 1
        ->
        let declarator =
            backing.DeclaringSyntaxReferences.[0].GetSyntax() :?> VariableDeclaratorSyntax

        let fieldDecl = declarator.Parent.Parent :?> FieldDeclarationSyntax

        // every reference to the field in the type — every PART of the type, a
        // partial one being declared across files — is inside this property's
        // accessors; a `nameof` or a string spelling the name (reflection by
        // name) reaches the field too
        let typeDecl = p.Parent :?> TypeDeclarationSyntax
        let index = Index.ofCompilation model.Compilation

        let outside =
            backing.ContainingType.DeclaringSyntaxReferences
            |> Seq.exists (fun part ->
                let partNode = part.GetSyntax()

                let partModel =
                    if partNode.SyntaxTree = tree then
                        model
                    else
                        model.Compilation.GetSemanticModel partNode.SyntaxTree

                identifiersNamed partNode backing.Name
                |> Seq.exists (fun id ->
                    SymbolEqualityComparer.Default.Equals(partModel.GetSymbolInfo(id).Symbol, backing)
                    && not (obj.ReferenceEquals(partNode, typeDecl) && p.AccessorList.Span.Contains id.Span)))
            || Index.namedByNameOf index backing
            || Index.mentionedAsString index backing.Name

        if
            outside
            // the field goes with the fix: declared in another part of a partial
            // type, it is in another file, which this file's edits cannot reach
            || fieldDecl.SyntaxTree <> tree
            || fieldDecl.Declaration.Variables.Count <> 1
            || Text.holdsCommentOrDirective fieldDecl
            // the property's type: a backing field of another type would change what
            // the accessors compute
            || not (SymbolEqualityComparer.Default.Equals(backing.Type, (model.GetDeclaredSymbol p).Type))
            // the initialiser moves in textual order among the initialisers: one
            // with effects, or reading other state, would run at another time
            || (not (isNull declarator.Initializer)
                && not (Guards.isPureExpression model declarator.Initializer.Value))
        then
            ValueNone
        else
            ValueSome(backing, declarator, fieldDecl, typeDecl)
    | _ -> ValueNone

/// Is every accessor trivial over the field - `get { return f; }`, `get =>
/// f;`, `set { f = value; }`, `set => f = value;` (`init` too), `this.f`
/// alike - with no attribute on an accessor?
let private trivialAccessors (model: SemanticModel) (backing: IFieldSymbol) (p: PropertyDeclarationSyntax) =
    let isField (e: ExpressionSyntax) =
        let e =
            match e with
            | :? MemberAccessExpressionSyntax as ma when (ma.Expression :? ThisExpressionSyntax) ->
                ma.Name :> ExpressionSyntax
            | e -> e

        e :? IdentifierNameSyntax
        && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(e).Symbol, backing)

    let body (a: AccessorDeclarationSyntax) : ExpressionSyntax option =
        match a.ExpressionBody, a.Body with
        | null, null -> None
        | eb, null when not (isNull eb) -> Some eb.Expression
        | null, b when b.Statements.Count = 1 ->
            match b.Statements.[0] with
            | :? ReturnStatementSyntax as r when not (isNull r.Expression) -> Some r.Expression
            | :? ExpressionStatementSyntax as es -> Some es.Expression
            | _ -> None
        | _ -> None

    p.AccessorList.Accessors.Count > 0
    && p.AccessorList.Accessors
       |> Seq.forall (fun a ->
           a.AttributeLists.Count = 0
           && (match a.Kind(), body a with
               | SyntaxKind.GetAccessorDeclaration, Some e ->
                   (match a.Body with
                    | null -> true
                    | b -> b.Statements.[0] :? ReturnStatementSyntax)
                   && isField e
               | (SyntaxKind.SetAccessorDeclaration | SyntaxKind.InitAccessorDeclaration),
                 Some(:? AssignmentExpressionSyntax as asg) ->
                   asg.IsKind SyntaxKind.SimpleAssignmentExpression
                   && isField asg.Left
                   && (match asg.Right with
                       | :? IdentifierNameSyntax as v -> v.Identifier.ValueText = "value"
                       | _ -> false)
               | _ -> false))

let private fieldKeyword (tree: SyntaxTree) (model: SemanticModel) (ctx: RuleContext) : Suggestion list =
    if not (RuleContext.languageAtLeast ctx 14) then
        []
    else
        let text = tree.GetText()

        tree.GetRoot().DescendantNodes()
        |> Seq.choose (fun n ->
            match n with
            | :? PropertyDeclarationSyntax as p when not (isNull p.AccessorList) ->
                match soleBackingField tree model p with
                // trivial accessors are an auto-property's: CR0186's, while it runs
                | ValueSome(backing, _, _, _) when
                    trivialAccessors model backing p
                    && not (RuleContext.ruleConfiguredOff ctx AutoPropertyCode)
                    && not (RuleContext.shadowedRuleOn ctx [ "IDE0032" ])
                    ->
                    None
                | ValueSome(backing, declarator, fieldDecl, typeDecl) ->
                    let fieldNameTaken =
                        typeDecl.DescendantTokens()
                        |> Seq.exists (fun t -> t.IsKind SyntaxKind.IdentifierToken && t.ValueText = "field")

                    if fieldNameTaken then
                        None
                    else
                        // the field's mentions in the accessors become `field`; the field goes,
                        // its initialiser moves to the property
                        let mentions =
                            p.AccessorList.DescendantNodes()
                            |> Seq.choose (fun x ->
                                match x with
                                | :? IdentifierNameSyntax as id when
                                    id.Identifier.ValueText = backing.Name
                                    && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(id).Symbol, backing)
                                    ->
                                    // `this.x` → `field`
                                    match id.Parent with
                                    | :? MemberAccessExpressionSyntax as ma when
                                        ma.Name.Span = id.Span && (ma.Expression :? ThisExpressionSyntax)
                                        ->
                                        Some(Suggestion.replace ma.Span "field")
                                    | _ -> Some(Suggestion.replace id.Span "field")
                                | _ -> None)
                            |> List.ofSeq

                        let removeField = Suggestion.replace (Text.statementLineSpan text fieldDecl) ""

                        let initializer =
                            if isNull declarator.Initializer || not (isNull p.Initializer) then
                                []
                            else
                                [
                                    Suggestion.insert
                                        p.AccessorList.Span.End
                                        (" = " + declarator.Initializer.Value.ToString() + ";")
                                ]

                        let edits = mentions @ [ removeField ] @ initializer

                        if not mentions.IsEmpty && Guards.speculativeCheck model edits then
                            Some
                                {
                                    Code = FieldKeywordCode
                                    Message =
                                        $"'{backing.Name}' backs only this property: the 'field' keyword names it without the declaration"
                                    Span = p.Identifier.Span
                                    Fixes = [ Suggestion.fix "Use the field keyword" FieldKeywordCode edits ]
                                }
                        else
                            None
                | ValueNone -> None
            | _ -> None)
        |> List.ofSeq

/// Type-level attributes known to read no private field by name: a type
/// carrying any other one keeps its fields (CR0186).
let private fieldBlindTypeAttributes =
    set
        [
            "DataContractAttribute"
            "CollectionDataContractAttribute"
            "KnownTypeAttribute"
            "ObsoleteAttribute"
            "DebuggerDisplayAttribute"
            "DebuggerTypeProxyAttribute"
            "DebuggerStepThroughAttribute"
            "DebuggerNonUserCodeAttribute"
            "ExcludeFromCodeCoverageAttribute"
            "CompilerGeneratedAttribute"
            "NullableAttribute"
            "NullableContextAttribute"
            "DescriptionAttribute"
            "DisplayNameAttribute"
            "BrowsableAttribute"
            "EditorBrowsableAttribute"
            "CategoryAttribute"
            "DefaultMemberAttribute"
            "ComVisibleAttribute"
            "GuidAttribute"
            "XmlRootAttribute"
            "XmlTypeAttribute"
            "XmlIncludeAttribute"
            "TableAttribute"
        ]

/// Does the compilation reach private fields without naming them - asking
/// reflection for non-public members (`BindingFlags.NonPublic`,
/// `GetRuntimeFields`, `DeclaredFields`), or a serializer for its private
/// mode (Newtonsoft's `DefaultMembersSearchFlags = NonPublic`, MessagePack's
/// `…AllowPrivate` resolvers)? A field walked that way turns into
/// `<Name>k__BackingField` under CR0186 (CR0180's `SetValue` scan, for reads).
let private enumeratesPrivateFields =
    let cache = System.Runtime.CompilerServices.ConditionalWeakTable<Compilation, obj>()

    let names =
        set
            [
                "NonPublic"
                "GetRuntimeFields"
                "DeclaredFields"
                "StandardResolverAllowPrivate"
                "ContractlessStandardResolverAllowPrivate"
                "DynamicObjectResolverAllowPrivate"
                "DynamicContractlessObjectResolverAllowPrivate"
            ]

    fun (compilation: Compilation) ->
        cache.GetValue(
            compilation,
            fun c ->
                c.SyntaxTrees
                |> Seq.exists (fun t ->
                    t.GetRoot().DescendantNodes()
                    |> Seq.exists (fun n ->
                        match n with
                        | :? IdentifierNameSyntax as id -> names.Contains id.Identifier.ValueText
                        | _ -> false))
                |> box
        )
        |> unbox<bool>

/// CR0186: a property whose accessors only return and store its private
/// backing field is an auto-property - the F# side's FR0026 (`member val`).
let private autoProperty (tree: SyntaxTree) (model: SemanticModel) (ctx: RuleContext) : Suggestion list =
    let text = tree.GetText()

    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun n ->
        match n with
        | :? PropertyDeclarationSyntax as p when not (isNull p.AccessorList) && isNull p.ExpressionBody ->
            match soleBackingField tree model p with
            | ValueSome(backing, declarator, fieldDecl, _) when trivialAccessors model backing p ->
                let owner = backing.ContainingType

                // the field's name is its identity to a binary serializer, and its
                // place in the declaration order to a sequential layout - a struct's by
                // default. A type-level attribute may say the same to a library -
                // Newtonsoft's `[JsonObject(MemberSerialization.Fields)]` writes `_port`
                // and then `<Port>k__BackingField` - so any but the few known to read
                // no field stands the rule down, on the type and its bases. A private
                // field without [DataMember] was never serialized by a data contract,
                // and the auto-property's is not either
                let fieldBound =
                    let rec selfAndBases (t: INamedTypeSymbol) =
                        seq {
                            if not (isNull t) && t.SpecialType <> SpecialType.System_Object then
                                yield t
                                yield! selfAndBases t.BaseType
                        }

                    // a derived type's attribute serializes the inherited field too:
                    // `[JsonObject(MemberSerialization.Fields)] class Derived : Base`
                    let family =
                        Seq.append
                            (selfAndBases owner |> Seq.cast<ISymbol>)
                            (withDerived (Index.ofCompilation model.Compilation) owner)

                    owner.TypeKind = TypeKind.Struct
                    || family
                       |> Seq.exists (fun t ->
                           t.GetAttributes()
                           |> Seq.exists (fun a -> not (fieldBlindTypeAttributes.Contains a.AttributeClass.Name)))
                    || owner.AllInterfaces |> Seq.exists (fun i -> i.Name = "ISerializable")
                    || enumeratesPrivateFields model.Compilation

                let initializerText =
                    if isNull declarator.Initializer || not (isNull p.Initializer) then
                        ""
                    else
                        " = " + declarator.Initializer.Value.ToString() + ";"

                // the initialiser moves to the property's place among the initialisers:
                // with another between them, it runs on the other side of it - which may
                // change a primary constructor parameter it reads, or run a static
                // constructor it triggers in another order. A constant runs nowhere
                let noInitializerBetween () =
                    let lo = min fieldDecl.SpanStart p.SpanStart
                    let hi = max fieldDecl.Span.End p.Span.End

                    (p.Parent :?> TypeDeclarationSyntax).Members
                    |> Seq.forall (fun other ->
                        other.SpanStart <= lo
                        || other.SpanStart >= hi
                        || obj.ReferenceEquals(other, fieldDecl)
                        || obj.ReferenceEquals(other, p)
                        || (match other with
                            | :? BaseFieldDeclarationSyntax as f ->
                                f.Declaration.Variables |> Seq.forall (fun v -> isNull v.Initializer)
                            | :? PropertyDeclarationSyntax as q -> isNull q.Initializer
                            | _ -> true))

                let initializerStays =
                    initializerText = ""
                    || model.GetConstantValue(declarator.Initializer.Value).HasValue
                    // the field in another part of a partial type: its part's
                    // initializers run in an order the move would change unseen
                    || (obj.ReferenceEquals(fieldDecl.Parent, p.Parent) && noInitializerBetween ())

                // an auto-property initialiser is C# 6
                let initializerAllowed =
                    initializerText = "" || (RuleContext.languageAtLeast ctx 6 && initializerStays)

                // the accessor list is replaced whole: a comment in it would go, and an
                // `#if` around a setter would decide every configuration by the one
                // analysed (`{ get; }` drops the setter another build has)
                let accessorsHoldTrivia =
                    p.AccessorList.DescendantTrivia(descendIntoTrivia = true)
                    |> Seq.exists (fun t ->
                        p.AccessorList.Span.Contains t.Span
                        && (t.IsDirective
                            || t.IsKind SyntaxKind.DisabledTextTrivia
                            || t.IsKind SyntaxKind.SingleLineCommentTrivia
                            || t.IsKind SyntaxKind.MultiLineCommentTrivia))

                if fieldBound || not initializerAllowed || accessorsHoldTrivia then
                    None
                else
                    let accessors =
                        p.AccessorList.Accessors
                        |> Seq.map (fun a ->
                            let modifiers = a.Modifiers |> Seq.map (fun m -> m.Text + " ") |> String.concat ""

                            $"{modifiers}{a.Keyword.Text};")
                        |> String.concat " "

                    let edits =
                        [
                            Suggestion.replace p.AccessorList.Span ("{ " + accessors + " }" + initializerText)
                            Suggestion.replace (Text.statementLineSpan text fieldDecl) ""
                        ]

                    if Guards.speculativeCheck model edits then
                        Some
                            {
                                Code = AutoPropertyCode
                                Message =
                                    $"'{backing.Name}' only backs this property's plain get and set: an auto-property says the same without the field"
                                Span = p.Identifier.Span
                                Fixes = [ Suggestion.fix "Make it an auto-property" AutoPropertyCode edits ]
                            }
                    else
                        None
            | _ -> None
        | _ -> None)
    |> List.ofSeq

// ---- CR0154 ----

let private nullConditionalAssignments (tree: SyntaxTree) (model: SemanticModel) (ctx: RuleContext) : Suggestion list =
    if not (RuleContext.languageAtLeast ctx 14) then
        []
    else
        let text = tree.GetText()

        // `x != null`, `x is not null`, `null != x`: the tested reference
        let nullTested (c: ExpressionSyntax) : ExpressionSyntax option =
            match c with
            | :? BinaryExpressionSyntax as b when
                b.IsKind SyntaxKind.NotEqualsExpression
                // the built-in reference test only: a user-defined `!=` may call a live
                // object null (a destroyed Unity object), which `?.` never asks
                && (match model.GetSymbolInfo(b).Symbol with
                    | :? IMethodSymbol as op -> op.MethodKind <> MethodKind.UserDefinedOperator
                    | _ -> true)
                ->
                if b.Right.IsKind SyntaxKind.NullLiteralExpression then
                    Some b.Left
                elif b.Left.IsKind SyntaxKind.NullLiteralExpression then
                    Some b.Right
                else
                    None
            | :? IsPatternExpressionSyntax as p ->
                match p.Pattern with
                | :? UnaryPatternSyntax as u when u.IsKind SyntaxKind.NotPattern ->
                    match u.Pattern with
                    | :? ConstantPatternSyntax as k when k.Expression.IsKind SyntaxKind.NullLiteralExpression ->
                        Some p.Expression
                    | _ -> None
                | _ -> None
            | _ -> None

        let rec pureRead (e: ExpressionSyntax) =
            match e with
            | :? IdentifierNameSyntax
            | :? ThisExpressionSyntax -> true
            | :? MemberAccessExpressionSyntax as m -> pureRead m.Expression
            | _ -> false

        tree.GetRoot().DescendantNodes()
        |> Seq.choose (fun n ->
            match n with
            | :? IfStatementSyntax as ifs when isNull ifs.Else && not (Text.holdsCommentOrDirective ifs) ->
                match nullTested ifs.Condition with
                | Some x when pureRead x ->
                    let body =
                        match ifs.Statement with
                        | :? BlockSyntax as b when b.Statements.Count = 1 -> Some b.Statements.[0]
                        | :? BlockSyntax -> None
                        | s -> Some s

                    match body with
                    | Some(:? ExpressionStatementSyntax as s) ->
                        match s.Expression with
                        | :? AssignmentExpressionSyntax as a ->
                            let receiver =
                                match a.Left with
                                | :? MemberAccessExpressionSyntax as m -> Some m.Expression
                                | :? ElementAccessExpressionSyntax as ea -> Some ea.Expression
                                | _ -> None

                            match receiver with
                            | Some r when Guards.sameReference model r x && not (Text.assignsTo (x.ToString()) a.Right) ->
                                // `x.P = v` → `x?.P = v`; `x[i] = v` → `x?[i] = v`
                                let replacement =
                                    match a.Left with
                                    | :? MemberAccessExpressionSyntax as m ->
                                        r.ToString()
                                        + "?."
                                        + m.Name.ToString()
                                        + a.ToString().Substring(m.Span.End - a.SpanStart)
                                    | :? ElementAccessExpressionSyntax as ea ->
                                        r.ToString()
                                        + "?"
                                        + ea.ArgumentList.ToString()
                                        + a.ToString().Substring(ea.Span.End - a.SpanStart)
                                    | _ -> a.ToString()

                                let edit = Suggestion.replace ifs.Span (replacement + ";")

                                if Guards.speculativeCheck model [ edit ] then
                                    Some
                                        {
                                            Code = NullConditionalAssignCode
                                            Message = "A null-guarded assignment is a null-conditional assignment"
                                            Span = ifs.Condition.Span
                                            Fixes =
                                                [
                                                    Suggestion.fix
                                                        "Use ?. assignment"
                                                        NullConditionalAssignCode
                                                        [ edit ]
                                                ]
                                        }
                                else
                                    None
                            | _ -> None
                        | _ -> None
                    | _ -> None
                | _ -> None
            | _ -> None)
        |> List.ofSeq

// ---- CR0157 ----

let private exhaustiveThrows (tree: SyntaxTree) (model: SemanticModel) (ctx: RuleContext) : Suggestion list =
    if
        not (RuleContext.languageAtLeast ctx 8)
        || not (resolves model "System.Diagnostics.UnreachableException")
    then
        []
    else
        let thrownTypes =
            set
                [
                    "InvalidOperationException"
                    "SwitchExpressionException"
                    "ArgumentOutOfRangeException"
                    "NotSupportedException"
                    "NotImplementedException"
                ]

        tree.GetRoot().DescendantNodes()
        |> Seq.choose (fun n ->
            match n with
            | :? SwitchExpressionSyntax as sw ->
                let arms = sw.Arms |> List.ofSeq

                let discard =
                    arms
                    |> List.tryFind (fun a -> a.Pattern :? DiscardPatternSyntax && isNull a.WhenClause)

                match discard with
                | Some arm ->
                    match arm.Expression with
                    | :? ThrowExpressionSyntax as t ->
                        match t.Expression with
                        | :? ObjectCreationExpressionSyntax as c when
                            thrownTypes.Contains(c.Type.ToString().Split('.') |> Array.last)
                            ->
                            let hasMessage = not (isNull c.ArgumentList) && c.ArgumentList.Arguments.Count > 0

                            // the scrutinee: an enum every member of which is listed, or a sealed hierarchy
                            let scrutineeType = model.GetTypeInfo(sw.GoverningExpression).Type

                            let listed =
                                arms
                                |> List.filter (fun a -> not (obj.ReferenceEquals(a, arm)))
                                |> List.map (fun a -> a.Pattern)

                            let exhaustive =
                                match scrutineeType with
                                | null -> false
                                | t when t.TypeKind = TypeKind.Enum ->
                                    let members =
                                        t.GetMembers()
                                        |> Seq.filter (fun m -> m.Kind = SymbolKind.Field)
                                        |> Seq.map (fun m -> m.Name)
                                        |> Set.ofSeq

                                    let named =
                                        listed
                                        |> List.choose (fun p ->
                                            match p with
                                            | :? ConstantPatternSyntax as k ->
                                                match model.GetSymbolInfo(k.Expression).Symbol with
                                                | :? IFieldSymbol as f when
                                                    SymbolEqualityComparer.Default.Equals(f.ContainingType, t)
                                                    ->
                                                    Some f.Name
                                                | _ -> None
                                            | _ -> None)
                                        |> Set.ofList

                                    listed.Length = named.Count && named = members
                                | :? INamedTypeSymbol as t when t.IsAbstract || t.TypeKind = TypeKind.Interface ->
                                    // a sealed hierarchy: every derived type in the compilation is sealed and listed
                                    let derived =
                                        Index.derivedTypesOf (Index.ofCompilation model.Compilation) t |> List.distinct

                                    let patternTypes =
                                        listed
                                        |> List.choose (fun p ->
                                            match p with
                                            | :? DeclarationPatternSyntax as d -> Some(model.GetTypeInfo(d.Type).Type)
                                            | :? TypePatternSyntax as tp -> Some(model.GetTypeInfo(tp.Type).Type)
                                            | :? RecursivePatternSyntax as r when not (isNull r.Type) ->
                                                Some(model.GetTypeInfo(r.Type).Type)
                                            | _ -> None)

                                    not derived.IsEmpty
                                    && listed.Length = patternTypes.Length
                                    && derived
                                       |> List.forall (fun d ->
                                           d.IsSealed
                                           && patternTypes
                                              |> List.exists (fun pt -> SymbolEqualityComparer.Default.Equals(pt, d)))
                                    && (t.ContainingAssembly = model.Compilation.Assembly)
                                | _ -> false

                            if not exhaustive then
                                None
                            elif hasMessage then
                                Some(
                                    Suggestion.note
                                        UnreachableCode
                                        "Every case is listed, so the discard arm is unreachable by construction: UnreachableException says so (the message here may be a contract, so the rule does not rewrite)"
                                        arm.Span
                                )
                            else
                                match
                                    Usings.importEdit
                                        model
                                        tree
                                        c.SpanStart
                                        "System.Diagnostics"
                                        "UnreachableException"
                                with
                                | None -> None
                                | Some usingEdits ->
                                    let edits = usingEdits @ [ Suggestion.replace c.Span "new UnreachableException()" ]

                                    if Guards.speculativeCheck model edits then
                                        Some
                                            {
                                                Code = UnreachableCode
                                                Message =
                                                    "Every case is listed, so the discard arm is unreachable by construction: UnreachableException says so"
                                                Span = arm.Span
                                                Fixes =
                                                    [
                                                        Suggestion.fix
                                                            "Throw UnreachableException"
                                                            UnreachableCode
                                                            edits
                                                    ]
                                            }
                                    else
                                        None
                        | _ -> None
                    | _ -> None
                | None -> None
            | _ -> None)
        |> List.ofSeq

let analyze (tree: SyntaxTree) (model: SemanticModel) (ctx: RuleContext) : Suggestion list =
    utf8Literals tree model ctx
    @ requiredMembers tree model ctx
    @ frozenCollections tree model ctx
    @ lockObjects tree model ctx
    @ fieldKeyword tree model ctx
    @ autoProperty tree model ctx
    @ nullConditionalAssignments tree model ctx
    @ exhaustiveThrows tree model ctx
