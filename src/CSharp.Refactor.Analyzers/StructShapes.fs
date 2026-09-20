/// Value types where a reference type allocates for nothing.
///
/// CR0081 (performance, fix, API): a `record` (or a class CR0080 would
/// make one) of at most four small unmanaged fields, 32 bytes in all, is a
/// `readonly record struct` (C# 10): no allocation, value equality kept.
/// Guards: every instance field or auto-property an unmanaged struct or
/// enum (`int`, `long`, `double`, `decimal`, `bool`, `Guid`, `DateTime`…
/// — no string, no reference, no generic parameter), total at most 32
/// bytes; no inheritance either way (no base but `object`, nothing
/// derives from it); no interface beyond the compiler's `IEquatable<T>`;
/// not used as `T?` anywhere (that spelling would change from a nullable
/// reference to `Nullable<T>`), not compared with `null`, not boxed to
/// `object`/an interface, not `lock`ed, not the element of an expression
/// tree lambda (`IQueryable` providers translate struct members
/// differently); the scope gate. The fix inserts `readonly … struct`
/// around the `record` keyword and nothing else.
///
/// CR0082 (performance, fix, API): `Tuple<int, string>` in a
/// private/internal signature, field or local, and `Tuple.Create(a, b)`,
/// are `(int, string)` and `(a, b)`. Guards: all-or-nothing over the typed
/// uses of a declaration — every construction is `Tuple.Create(…)` or
/// `new Tuple<…>(…)`, every read is an `Item1..ItemN` member (shared with
/// `ValueTuple`) or a deconstruction; a use through `ITuple`, reflection,
/// or a null comparison vetoes; at most four elements (a struct tuple is
/// copied by value); a public signature is API and follows the scope
/// gate; the serializer heuristic (a `ValueTuple`'s elements are fields,
/// which System.Text.Json ignores by default — a shape change). The
/// speculative check re-binds the file.
module CSharp.Refactor.StructShapes

open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax
open Microsoft.CodeAnalysis.Text

[<Literal>]
let RecordStructCode = "CR0081"

[<Literal>]
let ValueTupleCode = "CR0082"

/// Does the scope gate let this declaration change shape?
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

// ---- CR0081 ----

/// The size of a small unmanaged type, or None for anything else.
let private sizeOf (t: ITypeSymbol) : int option =
    match t with
    | null -> None
    | t when t.TypeKind = TypeKind.Enum -> Some 4
    | t ->
        match t.SpecialType with
        | SpecialType.System_Boolean
        | SpecialType.System_Byte
        | SpecialType.System_SByte -> Some 1
        | SpecialType.System_Int16
        | SpecialType.System_UInt16
        | SpecialType.System_Char -> Some 2
        | SpecialType.System_Int32
        | SpecialType.System_UInt32
        | SpecialType.System_Single -> Some 4
        | SpecialType.System_Int64
        | SpecialType.System_UInt64
        | SpecialType.System_Double
        | SpecialType.System_DateTime -> Some 8
        | SpecialType.System_Decimal -> Some 16
        | _ ->
            match t.ToDisplayString() with
            | "System.Guid"
            | "System.DateTimeOffset"
            | "System.TimeSpan" -> Some 16
            | _ -> None

/// Any use in the compilation that a struct would change: `T?`, `== null`,
/// a boxing conversion, a `lock`, an expression-tree lambda over it, a
/// type deriving from it.
let private structHostile (model: SemanticModel) (t: INamedTypeSymbol) =
    let index = Index.ofCompilation model.Compilation
    Index.structHostile index t || Index.isDerivedFrom index t

let private recordStructs (tree: SyntaxTree) (model: SemanticModel) (ctx: RuleContext) : Suggestion list =
    if ctx.LanguageVersion < LanguageVersion.CSharp10 then
        []
    else
        tree.GetRoot().DescendantNodes()
        |> Seq.choose (fun n ->
            match n with
            | :? RecordDeclarationSyntax as r when
                r.ClassOrStructKeyword.IsKind SyntaxKind.None
                && r.AttributeLists.Count = 0
                && not (
                    r.Modifiers
                    |> Seq.exists (fun m -> m.IsKind SyntaxKind.AbstractKeyword || m.IsKind SyntaxKind.PartialKeyword)
                )
                && (isNull r.TypeParameterList)
                ->
                match model.GetDeclaredSymbol r with
                | null -> None
                | self when
                    (isNull self.BaseType || self.BaseType.SpecialType = SpecialType.System_Object)
                    && self.AllInterfaces
                       |> Seq.forall (fun i -> i.OriginalDefinition.ToDisplayString() = "System.IEquatable<T>")
                    && shapeOpen ctx self
                    ->
                    let fields =
                        self.GetMembers()
                        |> Seq.choose (fun m ->
                            match m with
                            | :? IFieldSymbol as f when not (f.IsStatic || f.IsConst) -> Some f.Type
                            | _ -> None)
                        |> List.ofSeq

                    // every field small and unmanaged, at most four, at most 32 bytes
                    let sizes = fields |> List.map sizeOf

                    if
                        fields.IsEmpty
                        || fields.Length > 4
                        || sizes |> List.exists Option.isNone
                        || (sizes |> List.choose id |> List.sum) > 32
                        || structHostile model self
                    then
                        None
                    else
                        let edits =
                            [
                                Suggestion.insert r.Keyword.SpanStart "readonly "
                                Suggestion.insert r.Keyword.Span.End " struct"
                            ]

                        if Guards.speculativeCheck model edits then
                            Some
                                {
                                    Code = RecordStructCode
                                    Message =
                                        $"'{self.Name}' holds {fields.Length} small value field(s): a readonly record struct keeps value equality without the allocation"
                                    Span = r.Identifier.Span
                                    Fixes =
                                        [ Suggestion.fix "Make it a readonly record struct" RecordStructCode edits ]
                                }
                        else
                            None
                | _ -> None
            | _ -> None)
        |> List.ofSeq

// ---- CR0082 ----

let private isReferenceTuple (t: ITypeSymbol) =
    match t with
    | :? INamedTypeSymbol as n ->
        let name = n.OriginalDefinition.ToDisplayString()
        name.StartsWith "System.Tuple<" && n.TypeArguments.Length <= 4
    | _ -> false

/// The `(T1, T2)` spelling of a `Tuple<T1, T2>` type at a position.
let private valueTupleText (model: SemanticModel) (position: int) (t: INamedTypeSymbol) =
    "("
    + (t.TypeArguments
       |> Seq.map (fun a -> a.ToMinimalDisplayString(model, position))
       |> String.concat ", ")
    + ")"

let private sameTuple (a: ITypeSymbol) (b: ITypeSymbol) =
    not (isNull a)
    && not (isNull b)
    && a.WithNullableAnnotation(NullableAnnotation.NotAnnotated).ToDisplayString() =
        b.WithNullableAnnotation(NullableAnnotation.NotAnnotated).ToDisplayString()

/// The tuple type a declaration carries.
let private declaredType (symbol: ISymbol) : ITypeSymbol =
    match symbol with
    | :? ILocalSymbol as l -> l.Type
    | :? IParameterSymbol as p -> p.Type
    | :? IFieldSymbol as f -> f.Type
    | :? IPropertySymbol as p -> p.Type
    | :? IMethodSymbol as m -> m.ReturnType
    | _ -> null

/// An argument use is simple only when the call binds and the parameter the
/// value lands in is a source declaration of the same tuple type in this
/// compilation — one retyped together with it. A library's `Tuple<…>`
/// parameter, a type parameter or `object` would take the value tuple as
/// something else; a callee that does not bind hides what it takes (an
/// unresolved reference made the in-memory check blind once).
let private argumentOk (m: SemanticModel) (symbol: ISymbol) (arg: ArgumentSyntax) =
    match arg.Parent with
    | :? ArgumentListSyntax as al ->
        match m.GetSymbolInfo(al.Parent).Symbol with
        | :? IMethodSymbol as callee ->
            // the definition, not the instantiation: `Wrap<T>(T x)` called on a tuple takes a T
            let parameters = callee.OriginalDefinition.Parameters

            let parameter =
                if not (isNull arg.NameColon) then
                    parameters
                    |> Seq.tryFind (fun p -> p.Name = arg.NameColon.Name.Identifier.ValueText)
                else
                    let i = al.Arguments.IndexOf arg

                    if i < parameters.Length then Some parameters.[i] else None

            match parameter with
            | Some p when not p.IsParams && sameTuple p.Type (declaredType symbol) ->
                p.Locations
                |> Seq.exists (fun l -> l.IsInSource && m.Compilation.ContainsSyntaxTree l.SourceTree)
            | _ -> false
        | _ -> false
    | _ -> false

/// Is a use of a tuple-typed symbol one the value tuple serves the same:
/// an `ItemN` read, a deconstruction, handed to a parameter retyped with
/// it — not a null test, not an argument to anything else.
let private simpleUse (m: SemanticModel) (symbol: ISymbol) (id: SyntaxNode) =
    let e: SyntaxNode =
        match id.Parent with
        | :? MemberAccessExpressionSyntax as ma when ma.Name.Span = id.Span -> ma :> SyntaxNode
        | _ -> id

    // a method's call site is the value; what happens to that is the question
    let e: SyntaxNode =
        match e.Parent with
        | :? InvocationExpressionSyntax as inv when (symbol :? IMethodSymbol) && inv.Expression.Span = e.Span ->
            inv :> SyntaxNode
        | _ -> e

    match e.Parent with
    | :? ArgumentSyntax as arg -> argumentOk m symbol arg
    | :? MemberAccessExpressionSyntax as ma when ma.Expression.Span = e.Span ->
        let name = ma.Name.Identifier.ValueText
        name.StartsWith "Item" && name.Length = 5 && System.Char.IsDigit name.[4]
    | :? BinaryExpressionSyntax as b ->
        // a null comparison vetoes
        not (
            b.Left.IsKind SyntaxKind.NullLiteralExpression
            || b.Right.IsKind SyntaxKind.NullLiteralExpression
        )
    | :? IsPatternExpressionSyntax as p -> not (p.Pattern.ToString().Contains "null")
    | :? InvocationExpressionSyntax -> symbol :? IMethodSymbol // a method's call site; a delegate call otherwise
    | :? ConditionalAccessExpressionSyntax -> false // `t?.Item1`: a null test
    | _ -> true

/// The declarations of a tree typed as a reference tuple: fields,
/// properties, parameters, return types, locals.
let private tupleDeclarations (tree: SyntaxTree) (model: SemanticModel) : (ISymbol * INamedTypeSymbol) list =
    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun n ->
        match n with
        | :? VariableDeclaratorSyntax as v ->
            match model.GetDeclaredSymbol v with
            | :? IFieldSymbol as f when isReferenceTuple f.Type -> Some(f :> ISymbol, f.Type :?> INamedTypeSymbol)
            | :? ILocalSymbol as l when isReferenceTuple l.Type -> Some(l :> ISymbol, l.Type :?> INamedTypeSymbol)
            | _ -> None
        | :? ParameterSyntax as p ->
            match model.GetDeclaredSymbol p with
            | :? IParameterSymbol as ps when isReferenceTuple ps.Type ->
                Some(ps :> ISymbol, ps.Type :?> INamedTypeSymbol)
            | _ -> None
        | :? MethodDeclarationSyntax as md ->
            match model.GetDeclaredSymbol md with
            | :? IMethodSymbol as m when isReferenceTuple m.ReturnType ->
                Some(m :> ISymbol, m.ReturnType :?> INamedTypeSymbol)
            | _ -> None
        | :? PropertyDeclarationSyntax as pd ->
            match model.GetDeclaredSymbol pd with
            | :? IPropertySymbol as p when isReferenceTuple p.Type -> Some(p :> ISymbol, p.Type :?> INamedTypeSymbol)
            | _ -> None
        | _ -> None)
    |> List.ofSeq

/// The edits that retype every spelling of one tuple type in one tree —
/// `Tuple<…>` in type positions, `Tuple.Create`/`new Tuple<…>` constructions —
/// or None where the tree holds a spelling that cannot go (a generic
/// argument, `List<Tuple<…>>`, is a different shape).
let private treeEdits (file: string option) (t: SyntaxTree) (m: SemanticModel) (tupleType: INamedTypeSymbol) =
    let replaceAt =
        match file with
        | Some f -> Suggestion.replaceIn f
        | None -> Suggestion.replace

    let nodes = t.GetRoot().DescendantNodes() |> List.ofSeq

    let nested =
        nodes
        |> List.exists (fun x ->
            match x with
            | :? TypeSyntax as ts when (ts.Parent :? TypeArgumentListSyntax) ->
                sameTuple (m.GetTypeInfo(ts).Type) tupleType
            | _ -> false)

    if nested then
        None
    else
        let constructions =
            nodes
            |> List.choose (fun x ->
                match x with
                | :? InvocationExpressionSyntax as inv when
                    inv.Expression.ToString() = "Tuple.Create"
                    && sameTuple (m.GetTypeInfo(inv).Type) tupleType
                    ->
                    Some(replaceAt inv.Span (inv.ArgumentList.ToString()))
                | :? ObjectCreationExpressionSyntax as c when
                    sameTuple (m.GetTypeInfo(c).Type) tupleType && not (isNull c.ArgumentList)
                    ->
                    Some(replaceAt c.Span (c.ArgumentList.ToString()))
                | _ -> None)

        // every spelling of the type, outside constructions and type arguments
        let spellings =
            nodes
            |> List.choose (fun x ->
                match x with
                | :? TypeSyntax as ts when
                    not (ts.Parent :? TypeSyntax)
                    && not (ts :? PredefinedTypeSyntax)
                    && not (ts.Parent :? ObjectCreationExpressionSyntax)
                    && not (ts.Parent :? TypeArgumentListSyntax)
                    // a type position: a name bound to a local is a SimpleName too
                    && (m.GetSymbolInfo(ts).Symbol :? ITypeSymbol)
                    && sameTuple (m.GetTypeInfo(ts).Type) tupleType
                    ->
                    Some(replaceAt ts.Span (valueTupleText m ts.SpanStart tupleType))
                | _ -> None)

        Some((spellings @ constructions) |> List.distinctBy (fun e -> e.Span))

let private valueTuples (tree: SyntaxTree) (model: SemanticModel) (ctx: RuleContext) : Suggestion list =
    if ctx.LanguageVersion < LanguageVersion.CSharp7 then
        []
    else
        let declarations = tupleDeclarations tree model
        let index = Index.ofCompilation model.Compilation

        // is one declaration safe to retype: its shape open, every use simple
        // (an ItemN read, a deconstruction, handed along) — in this compilation
        // through the index, in another project through the host's oracle
        // (without one, a shape another project can see stands down)
        let safe (symbol: ISymbol) =
            let owner =
                match symbol with
                | :? ILocalSymbol -> symbol.ContainingSymbol
                | :? IParameterSymbol as p -> p.ContainingSymbol
                | s -> s

            let gateOpen = (symbol :? ILocalSymbol) || shapeOpen ctx owner

            let hereSimple =
                Index.usesOf index symbol
                |> List.forall (fun u -> simpleUse (model.Compilation.GetSemanticModel u.Id.SyntaxTree) symbol u.Id)

            let exported =
                not (symbol :? ILocalSymbol)
                && (match owner.DeclaredAccessibility with
                    | Accessibility.Private -> false
                    | Accessibility.Internal -> ctx.HasFriends
                    | _ -> not ctx.IsLeaf)

            let elsewhereSimple =
                if not exported then
                    true
                else
                    match RuleContext.referencesOf ctx symbol with
                    | None -> false
                    | Some sites ->
                        sites
                        |> List.forall (fun site ->
                            site.Editable
                            && not (isNull site.Node)
                            // a site of this compilation is the index's business
                            && (model.Compilation.ContainsSyntaxTree site.Tree
                                || simpleUse site.Model symbol site.Node))

            gateOpen && hereSimple && elsewhereSimple

        // the trees of this compilation that spell a type: the edit set is one
        // per type over all of them, reported from the first declaring tree
        let treesSpelling (tupleType: INamedTypeSymbol) =
            model.Compilation.SyntaxTrees
            |> Seq.filter (fun t ->
                t = tree
                || t.GetRoot().DescendantNodes()
                   |> Seq.exists (fun x ->
                       match x with
                       | :? GenericNameSyntax as g when g.Identifier.ValueText = "Tuple" ->
                           sameTuple (model.Compilation.GetSemanticModel(t).GetTypeInfo(g).Type) tupleType
                       | _ -> false))
            |> List.ofSeq

        // per tuple type: every declaration of it, in every tree that spells it,
        // must be safe, since the type's spellings are retyped together
        declarations
        // the annotation (`Tuple<…>?`) is not a different tuple
        |> List.groupBy (fun (_, t) -> t.WithNullableAnnotation(NullableAnnotation.NotAnnotated).ToDisplayString())
        |> List.choose (fun (_, group) ->
            let tupleType = snd group.Head
            let trees = treesSpelling tupleType

            let others =
                trees
                |> List.filter (fun t -> t <> tree)
                |> List.map (fun t -> t, model.Compilation.GetSemanticModel t)

            // the first declaring tree of the compilation reports; the others hold
            let reporter =
                trees
                |> List.filter (fun t ->
                    let m =
                        if t = tree then
                            model
                        else
                            model.Compilation.GetSemanticModel t

                    tupleDeclarations t m |> List.exists (fun (_, dt) -> sameTuple dt tupleType))
                |> List.sortBy (fun t -> t.FilePath)
                |> List.tryHead

            let allSafe =
                group |> List.forall (fun (s, _) -> safe s)
                && others
                   |> List.forall (fun (t, m) ->
                       tupleDeclarations t m
                       |> List.forall (fun (s, dt) -> not (sameTuple dt tupleType) || safe s))

            if not allSafe || reporter <> Some tree then
                None
            else
                let own = treeEdits None tree model tupleType

                let elsewhere =
                    others
                    |> List.map (fun (t, m) -> t, m, treeEdits (Some t.FilePath) t m tupleType)

                match own with
                | None -> None
                | Some ownEdits when ownEdits.IsEmpty -> None
                | Some _ when elsewhere |> List.exists (fun (_, _, e) -> e.IsNone) -> None
                | Some ownEdits ->
                    let edits =
                        ownEdits
                        @ (elsewhere |> List.collect (fun (_, _, e) -> Option.defaultValue [] e))

                    let sites =
                        elsewhere
                        |> List.map (fun (t, m, _) ->
                            {
                                Tree = t
                                Model = m
                                Node = null
                                Editable = true
                            })

                    let binds =
                        if sites.IsEmpty then
                            Guards.speculativeCheck model edits
                        else
                            Guards.speculativeCheckAcross model sites edits

                    if binds then
                        let first = fst group.Head

                        let span =
                            first.DeclaringSyntaxReferences
                            |> Seq.tryFind (fun r -> r.SyntaxTree = tree)
                            |> Option.map (fun r -> r.Span)
                            |> Option.defaultValue ownEdits.Head.Span

                        let files =
                            if others.IsEmpty then
                                ""
                            else
                                $", {others.Length} other file(s)"

                        Some
                            {
                                Code = ValueTupleCode
                                Message =
                                    $"'{tupleType.ToMinimalDisplayString(model, span.Start)}' is a reference tuple: the value tuple keeps the shape without the allocation ({group.Length} declaration(s) in this file{files})"
                                Span = span
                                Fixes = [ Suggestion.fix "Use a value tuple" ValueTupleCode edits ]
                            }
                    else
                        None)

let analyze (tree: SyntaxTree) (model: SemanticModel) (ctx: RuleContext) : Suggestion list =
    recordStructs tree model ctx @ valueTuples tree model ctx
