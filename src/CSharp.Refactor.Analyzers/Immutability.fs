/// Immutability by declaration.
///
/// CR0083 (idiom, fix, API): `{ get; set; }` whose every assignment in
/// the compilation is an object initialiser, a `with` expression, or a
/// constructor of the declaring type is `{ get; init; }` (C# 9,
/// `IsExternalInit` resolvable or polyfilled). Guards: no
/// `PropertyInfo.SetValue`/`nameof(P)` reflection shape naming the
/// property; the setter is not `private`/`protected` (a private setter is
/// a different contract); no attribute on the property or the type from a
/// serializer or ORM (`[JsonProperty]`, `[Column]`, `[BsonElement]`…) and
/// no Entity Framework entity (`DbSet<T>` of the type anywhere) — those
/// set properties by reflection after construction; a public setter is
/// API, so public types convert only where the public shape is open,
/// internal ones where no friend sees them; private types always.
///
/// CR0080 (idiom, fix, API): a `class` whose every instance member is a
/// get-only or `init` property, a `readonly` field, or a constructor
/// assigning them — a data holder, no methods — is a `record` — value equality and `with` for free. The
/// fix changes `class` to `record` and nothing else. Guards: no mutable
/// instance state (every field `readonly`, every property get-only or
/// `init`, no method or property assigns `this` state); no base class
/// other than `object` (a record cannot derive from a class); no
/// `Equals`/`GetHashCode`/`==`/`!=`/`ToString` override; no
/// `[StructLayout]`, no `unsafe`; reference identity is not relied on
/// anywhere in the compilation — `==`/`!=` on two instances,
/// `ReferenceEquals`, `lock` on an instance, use as a dictionary key or
/// set element (a record changes equality, which is the point and the
/// risk); no instance is formatted (a hole, a concatenation, `ToString()`,
/// a conversion to `object`: a record prints its members where the class
/// printed its name); the serializer heuristic of CR0083; the same scope
/// gate.
module CSharp.Refactor.Immutability

open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax
open Microsoft.CodeAnalysis.Text

[<Literal>]
let InitCode = "CR0083"

[<Literal>]
let RecordCode = "CR0080"

/// Attributes that mean "set by reflection after construction".
let private reflectiveAttributeWords =
    [
        "Json"
        "Xml"
        "Column"
        "Table"
        "Bson"
        "DataMember"
        "Serializ"
        "Key"
        "Dapper"
        "Proto"
        "MessagePack"
    ]

let private reflectiveAttribute (s: ISymbol) =
    s.GetAttributes()
    |> Seq.exists (fun a ->
        let name = a.AttributeClass.Name
        reflectiveAttributeWords |> List.exists name.Contains)

/// Is the type an Entity Framework entity: a `DbSet<T>` of it anywhere?
let private isEntity (model: SemanticModel) (t: INamedTypeSymbol) =
    Index.isEntity (Index.ofCompilation model.Compilation) t

/// Does the scope gate let this declaration change shape?
let private shapeOpen (ctx: RuleContext) (s: ISymbol) =
    // the effective accessibility: the least accessible of the symbol and its containers
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

/// Every write to the property across the compilation is an object
/// initialiser, a `with` expression, or `this.P = …` in a constructor of
/// the declaring type; at least one such write exists (a property nobody
/// assigns is one a serializer, DI or reflection sets); no `nameof(P)`.
let private writesAreConstruction (model: SemanticModel) (property: IPropertySymbol) : bool =
    let index = Index.ofCompilation model.Compilation
    let writes = Index.writesOf index property

    not writes.IsEmpty
    && writes |> List.forall (fun w -> w <> Index.Elsewhere)
    && not (Index.namedByNameOf index property)

let private isExternalInitAvailable (model: SemanticModel) =
    not (isNull (model.Compilation.GetTypeByMetadataName "System.Runtime.CompilerServices.IsExternalInit"))

// ---- CR0083 ----

let private initOnly (tree: SyntaxTree) (model: SemanticModel) (ctx: RuleContext) : Suggestion list =
    if
        ctx.LanguageVersion < LanguageVersion.CSharp9
        || not (isExternalInitAvailable model)
    then
        []
    else
        tree.GetRoot().DescendantNodes()
        |> Seq.choose (fun n ->
            match n with
            | :? PropertyDeclarationSyntax as p when not (isNull p.AccessorList) ->
                let setter =
                    p.AccessorList.Accessors
                    |> Seq.tryFind (fun a -> a.IsKind SyntaxKind.SetAccessorDeclaration)

                match setter, model.GetDeclaredSymbol p with
                | Some setter, (:? IPropertySymbol as property) when
                    isNull setter.Body
                    && isNull setter.ExpressionBody
                    && setter.Modifiers.Count = 0 // a private/protected setter is a different contract
                    && not property.IsStatic
                    && not property.IsOverride
                    && not property.IsVirtual
                    && not property.IsAbstract
                    && property.ExplicitInterfaceImplementations.IsEmpty
                    && not (reflectiveAttribute property)
                    && not (reflectiveAttribute property.ContainingType)
                    && (property.ContainingType.TypeKind <> TypeKind.Interface)
                    && shapeOpen ctx property
                    && not (isEntity model property.ContainingType)
                    && writesAreConstruction model property
                    ->
                    // an interface member the property implements must allow init too: refuse
                    let implementsInterface =
                        property.ContainingType.AllInterfaces
                        |> Seq.exists (fun i ->
                            i.GetMembers()
                            |> Seq.exists (fun im ->
                                let impl = property.ContainingType.FindImplementationForInterfaceMember im
                                not (isNull impl) && SymbolEqualityComparer.Default.Equals(impl, property)))

                    if implementsInterface then
                        None
                    else
                        let edit = Suggestion.replace setter.Keyword.Span "init"

                        if Guards.speculativeCheck model [ edit ] then
                            Some
                                {
                                    Code = InitCode
                                    Message = "The setter is only used while constructing: 'init' says so and seals it"
                                    Span = setter.Keyword.Span
                                    Fixes = [ Suggestion.fix "Make it init-only" InitCode [ edit ] ]
                                }
                        else
                            None
                | _ -> None
            | _ -> None)
        |> List.ofSeq

// ---- CR0080 ----

let private overridesIdentity (t: TypeDeclarationSyntax) =
    t.Members
    |> Seq.exists (fun m ->
        match m with
        | :? MethodDeclarationSyntax as md ->
            List.contains md.Identifier.ValueText [ "Equals"; "GetHashCode"; "ToString" ]
        | :? OperatorDeclarationSyntax -> true
        | _ -> false)

/// Is reference identity relied on anywhere: `==`/`!=` between instances,
/// `ReferenceEquals`, `lock`, a dictionary key or set element?
let private identityUsed (model: SemanticModel) (t: INamedTypeSymbol) =
    Index.identityUsed (Index.ofCompilation model.Compilation) t

let private records (tree: SyntaxTree) (model: SemanticModel) (ctx: RuleContext) : Suggestion list =
    if ctx.LanguageVersion < LanguageVersion.CSharp9 then
        []
    else
        tree.GetRoot().DescendantNodes()
        |> Seq.choose (fun n ->
            match n with
            | :? ClassDeclarationSyntax as c when
                not (
                    c.Modifiers
                    |> Seq.exists (fun m ->
                        m.IsKind SyntaxKind.StaticKeyword
                        || m.IsKind SyntaxKind.UnsafeKeyword
                        || m.IsKind SyntaxKind.PartialKeyword
                        || m.IsKind SyntaxKind.AbstractKeyword)
                )
                && c.AttributeLists.Count = 0
                && not (overridesIdentity c)
                ->
                match model.GetDeclaredSymbol c with
                | null -> None
                | self when
                    (isNull self.BaseType || self.BaseType.SpecialType = SpecialType.System_Object)
                    && shapeOpen ctx self
                    && not (isEntity model self)
                    && not (reflectiveAttribute self)
                    ->
                    let instanceMembers =
                        self.GetMembers() |> Seq.filter (fun m -> not m.IsStatic) |> List.ofSeq

                    let immutable =
                        instanceMembers
                        |> List.forall (fun m ->
                            match m with
                            | :? IFieldSymbol as f -> f.IsReadOnly || f.IsConst
                            | :? IPropertySymbol as p ->
                                // an auto-property: a getter with a body is behaviour (a lazy
                                // initialiser writing a static, a computed value) — not data held
                                (isNull p.SetMethod || p.SetMethod.IsInitOnly)
                                && p.DeclaringSyntaxReferences
                                   |> Seq.forall (fun r ->
                                       match r.GetSyntax() with
                                       | :? PropertyDeclarationSyntax as pd ->
                                           isNull pd.ExpressionBody
                                           && not (isNull pd.AccessorList)
                                           && pd.AccessorList.Accessors
                                              |> Seq.forall (fun a -> isNull a.Body && isNull a.ExpressionBody)
                                       | _ -> false)
                            | :? IMethodSymbol as md ->
                                // constructors and accessors only: a type with behaviour is a
                                // service or a domain object, not the data holder a record is for
                                md.MethodKind = MethodKind.Constructor
                                || md.MethodKind = MethodKind.PropertyGet
                                || md.MethodKind = MethodKind.PropertySet
                            | :? IEventSymbol -> false
                            | _ -> true)

                    // no method assigns instance state (a readonly field cannot be, a get-only property cannot be)
                    let hasState =
                        instanceMembers
                        |> List.exists (fun m -> m :? IFieldSymbol || m :? IPropertySymbol)

                    // subclasses anywhere: a class cannot derive from a record
                    let derived = Index.isDerivedFrom (Index.ofCompilation model.Compilation) self

                    // a record prints its members where a class printed its name: a log line
                    // or an interpolation over an instance would change, and may leak
                    let printed = Index.formatted (Index.ofCompilation model.Compilation) self

                    if
                        immutable
                        && hasState
                        && not derived
                        && not printed
                        && not (identityUsed model self)
                    then
                        let edit = Suggestion.replace c.Keyword.Span "record"

                        if Guards.speculativeCheck model [ edit ] then
                            Some
                                {
                                    Code = RecordCode
                                    Message =
                                        "Every member is immutable and nothing relies on reference identity: a record gives value equality and 'with' for free"
                                    Span = c.Identifier.Span
                                    Fixes = [ Suggestion.fix "Make it a record" RecordCode [ edit ] ]
                                }
                        else
                            None
                    else
                        None
                | _ -> None
            | _ -> None)
        |> List.ofSeq

let analyze (tree: SyntaxTree) (model: SemanticModel) (ctx: RuleContext) : Suggestion list =
    initOnly tree model ctx @ records tree model ctx
