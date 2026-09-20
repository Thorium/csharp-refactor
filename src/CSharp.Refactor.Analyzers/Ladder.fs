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
/// of the shape rules applies.
///
/// CR0150 (performance, fix, .NET 8, API): a `static readonly
/// Dictionary<K,V>`/`HashSet<T>` filled in its initialiser and only ever
/// read is a `FrozenDictionary<K,V>`/`FrozenSet<T>` via
/// `.ToFrozenDictionary()`/`.ToFrozenSet()`. Guards: the field is private
/// (internal under the friend check); every reference is a read
/// (`TryGetValue`, indexer get, `ContainsKey`, `Contains`, `Count`,
/// enumeration, `Keys`/`Values`, `GetValueOrDefault`); the comparer
/// argument travels; `System.Collections.Frozen` resolves; the `using`
/// is added.
///
/// CR0152 (performance, fix, C# 13 + .NET 9): `private readonly object
/// _gate = new();` whose every reference is a `lock` operand is `private
/// readonly Lock _gate = new();` — the dedicated type skips the
/// object-header path. `Monitor.*`, passing or comparing it vetoes;
/// `System.Threading.Lock` resolves.
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
/// of a pure read `x`, the body exactly one assignment (compound
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
let UnreachableCode = "CR0157"

let private resolves (model: SemanticModel) (metadataName: string) =
    not (isNull (model.Compilation.GetTypeByMetadataName metadataName))

/// The scope gate of the shape rules.
let private shapeOpen (ctx: RuleContext) (s: ISymbol) =
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

let private requiredMembers (tree: SyntaxTree) (model: SemanticModel) (ctx: RuleContext) : Suggestion list =
    if not (RuleContext.languageAtLeast ctx 11) then
        []
    else
        let index = Index.ofCompilation model.Compilation

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
                | :? IPropertySymbol as property when
                    (property.ContainingType.TypeKind = TypeKind.Class
                     || property.ContainingType.TypeKind = TypeKind.Struct)
                    && not (serialized property)
                    && not (serialized property.ContainingType)
                    && not (Index.isEntity index property.ContainingType)
                    && shapeOpen ctx property
                    && property.ExplicitInterfaceImplementations.IsEmpty
                    ->
                    let writes = Index.writesOf index property

                    // every write an initialiser, and every construction of the type an initialiser setting it
                    let constructions = Index.constructionsOf index property.ContainingType

                    let everyConstructionSets =
                        not constructions.IsEmpty
                        && constructions
                           |> List.forall (fun (hasInitializer, names) ->
                               hasInitializer && names.Contains property.Name)

                    if
                        not writes.IsEmpty
                        && writes |> List.forall (fun w -> w = Index.Initializer)
                        && everyConstructionSets
                    then
                        // `required` goes after the accessibility
                        let edit = Suggestion.insert p.Type.SpanStart "required "

                        if Guards.speculativeCheck model [ edit ] then
                            Some
                                {
                                    Code = RequiredCode
                                    Message =
                                        "Every construction sets the property and nothing else does: 'required' makes the compiler keep it so"
                                    Span = p.Identifier.Span
                                    Fixes = [ Suggestion.fix "Make it required" RequiredCode [ edit ] ]
                                }
                        else
                            None
                    else
                        None
                | _ -> None
            | _ -> None)
        |> List.ofSeq

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
                                tree.GetRoot().DescendantNodes()
                                |> Seq.choose (fun x ->
                                    match x with
                                    | :? IdentifierNameSyntax as id when
                                        id.Identifier.ValueText = field.Name
                                        && SymbolEqualityComparer.Default.Equals(
                                            model.GetSymbolInfo(id).Symbol,
                                            field
                                        )
                                        ->
                                        Some id
                                    | _ -> None)
                                |> List.ofSeq

                        let allReads =
                            reads
                            |> List.forall (fun id ->
                                let e: ExpressionSyntax =
                                    match id.Parent with
                                    | :? MemberAccessExpressionSyntax as ma when ma.Name.Span = id.Span ->
                                        ma :> ExpressionSyntax
                                    | _ -> id :> ExpressionSyntax

                                match e.Parent with
                                | :? MemberAccessExpressionSyntax as ma when ma.Expression.Span = e.Span ->
                                    readOnlyMembers.Contains ma.Name.Identifier.ValueText
                                | :? ElementAccessExpressionSyntax as ea when ea.Expression.Span = e.Span ->
                                    // an indexer read, not a set
                                    not (
                                        match ea.Parent with
                                        | :? AssignmentExpressionSyntax as a -> a.Left.Span = ea.Span
                                        | _ -> false
                                    )
                                | :? EqualsValueClauseSyntax when (e.Parent.Parent :? VariableDeclaratorSyntax) ->
                                    // the declaration itself
                                    (e.Parent.Parent :?> VariableDeclaratorSyntax).Identifier.Span = v.Identifier.Span
                                | _ -> false)

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
                    let uses =
                        tree.GetRoot().DescendantNodes()
                        |> Seq.choose (fun x ->
                            match x with
                            | :? IdentifierNameSyntax as id when
                                id.Identifier.ValueText = field.Name
                                && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(id).Symbol, field)
                                ->
                                Some id
                            | _ -> None)
                        |> List.ofSeq

                    let onlyLocked =
                        not uses.IsEmpty
                        && uses
                           |> List.forall (fun id ->
                               let e: SyntaxNode =
                                   match id.Parent with
                                   | :? MemberAccessExpressionSyntax as ma when
                                       ma.Name.Span = id.Span && (ma.Expression :? ThisExpressionSyntax)
                                       ->
                                       ma :> SyntaxNode
                                   | _ -> id :> SyntaxNode

                               match e.Parent with
                               | :? LockStatementSyntax as l -> l.Expression.Span = e.Span
                               | _ -> false)

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

// ---- CR0153 ----

let private fieldKeyword (tree: SyntaxTree) (model: SemanticModel) (ctx: RuleContext) : Suggestion list =
    if not (RuleContext.languageAtLeast ctx 14) then
        []
    else
        let text = tree.GetText()

        tree.GetRoot().DescendantNodes()
        |> Seq.choose (fun n ->
            match n with
            | :? PropertyDeclarationSyntax as p when not (isNull p.AccessorList) ->
                // the backing field: the one private field the accessors reference
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

                            partNode.DescendantNodes()
                            |> Seq.exists (fun x ->
                                match x with
                                | :? IdentifierNameSyntax as id when
                                    id.Identifier.ValueText = backing.Name
                                    && SymbolEqualityComparer.Default.Equals(
                                        partModel.GetSymbolInfo(id).Symbol,
                                        backing
                                    )
                                    ->
                                    not (
                                        obj.ReferenceEquals(partNode, typeDecl)
                                        && p.AccessorList.Span.Contains id.Span
                                    )
                                | _ -> false))
                        || Index.namedByNameOf index backing
                        || Index.mentionedAsString index backing.Name

                    let fieldNameTaken =
                        typeDecl.DescendantTokens()
                        |> Seq.exists (fun t -> t.IsKind SyntaxKind.IdentifierToken && t.ValueText = "field")

                    if
                        outside
                        || fieldNameTaken
                        || fieldDecl.Declaration.Variables.Count <> 1
                        || Text.holdsCommentOrDirective fieldDecl
                        // `field` takes the property's type: a backing field of another type
                        // would change what the accessors compute
                        || not (SymbolEqualityComparer.Default.Equals(backing.Type, (model.GetDeclaredSymbol p).Type))
                        // the initialiser moves in textual order among the initialisers: one
                        // with effects, or reading other state, would run at another time
                        || (not (isNull declarator.Initializer)
                            && not (Guards.isPureExpression model declarator.Initializer.Value))
                    then
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
            | :? BinaryExpressionSyntax as b when b.IsKind SyntaxKind.NotEqualsExpression ->
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
    @ nullConditionalAssignments tree model ctx
    @ exhaustiveThrows tree model ctx
