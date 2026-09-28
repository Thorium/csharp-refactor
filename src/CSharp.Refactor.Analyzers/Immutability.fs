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
/// set properties by reflection after construction — except
/// System.Text.Json's property attributes (`[JsonPropertyName]`,
/// `[JsonIgnore]`…) on a property with no initializer that no constructor
/// sets (the source generator fills an `init` DTO by an object initializer:
/// a property missing from the JSON gets default(T)), where the compilation
/// references System.Text.Json 8+ and never names `JsonObjectCreationHandling`
/// (Populate writes into what a property holds); a public setter is
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
///
/// CR0180 (idiom, fix, API): a `static` field nothing in the compilation
/// writes - no assignment, `++`, `ref`/`out` argument, deconstruction
/// (nested too), store through parentheses, `ref` alias or `&` - is
/// `static readonly`; a literal-initialised one
/// then goes on to CR0172's `const`. Guards: one declarator, not `volatile`
/// or `const`, no attribute (`[ThreadStatic]`…); not a mutable struct (a
/// `readonly` field copies it before each call, so a mutating method would
/// change the copy - primitives, enums and `readonly struct`s are fine);
/// the name in no `nameof` and no string literal of the compilation (a
/// `GetField("Name")` with a reflective write, which a `readonly` static
/// refuses); no `FieldInfo.SetValue` in the compilation (a loader walking
/// `GetFields()` reaches any static field); the scope gate of CR0080 - a public field is API; a private
/// one is IDE0044's where that is on.
module CSharp.Refactor.Immutability

open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax
open Microsoft.CodeAnalysis.Text

[<Literal>]
let InitCode = "CR0083"

[<Literal>]
let RecordCode = "CR0080"

[<Literal>]
let StaticReadonlyCode = "CR0180"

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

/// System.Text.Json's property attributes that leave the setter to the
/// serializer as plain assignment: from 8.0 it sets an `init` property, the
/// reflection serializer and the source generator alike. A type-level one (a
/// custom converter) and `[JsonExtensionData]` stay reflective.
let private initSafeJsonAttributes =
    set
        [
            "JsonPropertyNameAttribute"
            "JsonIgnoreAttribute"
            "JsonPropertyOrderAttribute"
            "JsonIncludeAttribute"
            "JsonRequiredAttribute"
            "JsonNumberHandlingAttribute"
            "JsonConverterAttribute"
        ]

/// Does the compilation reference System.Text.Json 8 or later, and never
/// switch it to populating (`JsonObjectCreationHandling.Populate` writes
/// into the object a property already holds, an option or an attribute
/// anywhere)? Cached per compilation: a DTO file asks once per property.
let private textJsonSetsInit =
    let cache = System.Runtime.CompilerServices.ConditionalWeakTable<Compilation, obj>()

    fun (compilation: Compilation) ->
        let answer =
            cache.GetValue(
                compilation,
                fun c ->
                    let modern =
                        c.ReferencedAssemblyNames
                        |> Seq.exists (fun a -> a.Name = "System.Text.Json" && a.Version.Major >= 8)

                    let populates =
                        c.SyntaxTrees
                        |> Seq.exists (fun t -> t.GetText().ToString().Contains "JsonObjectCreationHandling")

                    box (modern && not populates)
            )

        unbox<bool> answer

let private reflectiveAttribute (compilation: Compilation) (s: ISymbol) =
    s.GetAttributes()
    |> Seq.exists (fun a ->
        let cls = a.AttributeClass
        let name = cls.Name

        reflectiveAttributeWords |> List.exists name.Contains
        && not (
            s :? IPropertySymbol
            && initSafeJsonAttributes.Contains name
            && cls.ContainingNamespace.ToDisplayString() = "System.Text.Json.Serialization"
            && textJsonSetsInit compilation
            // the source generator builds an `init` DTO with an object initializer: a
            // property missing from the JSON gets default(T), not its `= 1`. Only a
            // property with no initializer has default(T) either way
            && s.DeclaringSyntaxReferences
               |> Seq.forall (fun r ->
                   match r.GetSyntax() with
                   | :? PropertyDeclarationSyntax as pd -> isNull pd.Initializer
                   | _ -> false)
        ))

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
                | Some setter, property when
                    not (isNull property)
                    && isNull setter.Body
                    && isNull setter.ExpressionBody
                    && setter.Modifiers.Count = 0 // a private/protected setter is a different contract
                    && not property.IsStatic
                    && not property.IsOverride
                    && not property.IsVirtual
                    && not property.IsAbstract
                    && property.ExplicitInterfaceImplementations.IsEmpty
                    && not (reflectiveAttribute model.Compilation property)
                    && not (reflectiveAttribute model.Compilation property.ContainingType)
                    && (property.ContainingType.TypeKind <> TypeKind.Interface)
                    && shapeOpen ctx property
                    && not (isEntity model property.ContainingType)
                    && writesAreConstruction model property
                    // a System.Text.Json DTO the generator fills by an object initializer: a
                    // constructor's value for a property missing from the JSON would be lost
                    && not (
                        property.GetAttributes()
                        |> Seq.exists (fun at ->
                            at.AttributeClass.ContainingNamespace.ToDisplayString() = "System.Text.Json.Serialization")
                        && Index.writesOf (Index.ofCompilation model.Compilation) property
                           |> List.contains Index.OwnConstructor
                    )
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

                        Some(
                            {
                                Code = InitCode
                                Message = "The setter is only used while constructing: 'init' says so and seals it"
                                Span = setter.Keyword.Span
                                Fixes = [ Suggestion.fix "Make it init-only" InitCode [ edit ] ]
                            },
                            [ edit ]
                        )
                | _ -> None
            | _ -> None)
        |> List.ofSeq
        // a DTO file of forty setters is one re-bind, not forty (a project of
        // three hundred took half a minute of them)
        |> Guards.speculativeCheckEach model

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
                    && not (reflectiveAttribute model.Compilation self)
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

// ---- CR0180 ----

/// The symbols anything takes a reference to - `ref f`, `ref readonly`
/// aliases and returns, `&f` - across the compilation: a `ref` argument is
/// a write in the index already, these are not. Once per compilation.
let private referenced =
    let cache =
        System.Runtime.CompilerServices.ConditionalWeakTable<Compilation, System.Collections.Generic.HashSet<ISymbol>>()

    fun (compilation: Compilation) ->
        cache.GetValue(
            compilation,
            fun c ->
                let taken =
                    System.Collections.Generic.HashSet<ISymbol>(SymbolEqualityComparer.Default)

                for t in c.SyntaxTrees do
                    let operands =
                        t.GetRoot().DescendantNodes()
                        |> Seq.choose (fun n ->
                            match n with
                            | :? RefExpressionSyntax as r -> Some r.Expression
                            | :? PrefixUnaryExpressionSyntax as u when u.IsKind SyntaxKind.AddressOfExpression ->
                                Some u.Operand
                            | _ -> None)
                        |> List.ofSeq

                    if not operands.IsEmpty then
                        let m = c.GetSemanticModel(t, true)

                        for o in operands do
                            match m.GetSymbolInfo(o).Symbol with
                            | null -> ()
                            | s -> taken.Add s |> ignore

                taken
        )

/// Does the compilation write fields by reflection - `FieldInfo.SetValue`,
/// `SetValueDirect`? A loader walking `GetFields()` can reach any static
/// field, and a `readonly` static throws there (FieldAccessException) where
/// the plain one took the value. Once per compilation.
let private writesFieldsByReflection =
    let cache = System.Runtime.CompilerServices.ConditionalWeakTable<Compilation, obj>()

    fun (compilation: Compilation) ->
        cache.GetValue(
            compilation,
            fun c ->
                c.SyntaxTrees
                |> Seq.exists (fun t ->
                    t.GetText().ToString().Contains "SetValue"
                    && (let m = c.GetSemanticModel(t, true)

                        t.GetRoot().DescendantNodes()
                        |> Seq.exists (fun n ->
                            match n with
                            | :? InvocationExpressionSyntax as inv when inv.Expression.ToString().Contains "SetValue" ->
                                match m.GetSymbolInfo(inv).Symbol with
                                | :? IMethodSymbol as md ->
                                    let owner = md.ContainingType.ToDisplayString()
                                    owner = "System.Reflection.FieldInfo" || owner = "System.Reflection.MemberInfo"
                                | _ -> true
                            | _ -> false)))
                |> box
        )
        |> unbox<bool>

/// A value type a `readonly` field would copy before each call: anything
/// but a primitive, an enum or a `readonly struct` (a mutating method on
/// the field would then change a copy, silently).
let private copiedWhenReadonly (t: ITypeSymbol) =
    t.IsValueType
    && t.TypeKind <> TypeKind.Enum
    && t.SpecialType = SpecialType.None
    && not t.IsReadOnly

let private staticReadonly (tree: SyntaxTree) (model: SemanticModel) (ctx: RuleContext) : Suggestion list =
    let index = lazy (Index.ofCompilation model.Compilation)

    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun n ->
        match n with
        | :? FieldDeclarationSyntax as f when
            f.Declaration.Variables.Count = 1
            && f.Modifiers |> Seq.exists (fun m -> m.IsKind SyntaxKind.StaticKeyword)
            && not (
                f.Modifiers
                |> Seq.exists (fun m ->
                    m.IsKind SyntaxKind.ReadOnlyKeyword
                    || m.IsKind SyntaxKind.VolatileKeyword
                    || m.IsKind SyntaxKind.ConstKeyword
                    || m.IsKind SyntaxKind.FixedKeyword)
            )
            && f.AttributeLists.Count = 0
            ->
            match model.GetDeclaredSymbol f.Declaration.Variables.[0] with
            | :? IFieldSymbol as field when
                not (copiedWhenReadonly field.Type)
                // `G<int>.F = 1` writes a constructed symbol the index keys apart from `G<T>.F`
                && not (
                    let rec generic (t: INamedTypeSymbol) =
                        not (isNull t) && (t.IsGenericType || generic t.ContainingType)

                    generic field.ContainingType
                )
                && field.Type.TypeKind <> TypeKind.Pointer
                && shapeOpen ctx field
                // a private field is IDE0044's where that is on
                && not (
                    field.DeclaredAccessibility = Accessibility.Private
                    && RuleContext.shadowedRuleOn ctx [ "IDE0044" ]
                )
                && (Index.writesOf index.Value field).IsEmpty
                && not (Index.namedByNameOf index.Value field)
                // `GetField("Name")` and a reflective SetValue
                && not (Index.mentionedAsString index.Value field.Name)
                && not ((referenced model.Compilation).Contains field)
                && not (writesFieldsByReflection model.Compilation)
                ->
                let staticToken =
                    f.Modifiers |> Seq.find (fun m -> m.IsKind SyntaxKind.StaticKeyword)

                let edit = Suggestion.insert staticToken.Span.End " readonly"

                if Guards.speculativeCheck model [ edit ] then
                    Some
                        {
                            Code = StaticReadonlyCode
                            Message =
                                "Nothing writes this static field after its initializer: readonly says so, and the runtime can fold it"
                            Span = f.Declaration.Variables.[0].Identifier.Span
                            Fixes = [ Suggestion.fix "Make it static readonly" StaticReadonlyCode [ edit ] ]
                        }
                else
                    None
            | _ -> None
        | _ -> None)
    |> List.ofSeq

let analyze (tree: SyntaxTree) (model: SemanticModel) (ctx: RuleContext) : Suggestion list =
    initOnly tree model ctx @ records tree model ctx @ staticReadonly tree model ctx
