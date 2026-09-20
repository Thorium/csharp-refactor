/// Type-level notes and one small fix.
///
/// CR0084 (correctness, note): `public static int Counter;` — a public
/// mutable static assigned from two or more sites, or updated from itself
/// (`Counter++`), is shared state anything can race on. Churn gate: a
/// set-once seam (one assignment site in the compilation) stays quiet;
/// `private`/`internal` fields stay quiet; a `readonly` or `const` field
/// is not mutable. The editor offers `private` and `readonly`. Yields to
/// CA2211.
///
/// CR0085 (correctness, note): `x.GetType().Name == "Customer"`,
/// `x.GetType().FullName == "…"`, `x.GetType() == typeof(T)` — a type
/// test by name breaks on rename and on a derived type, and `==` on
/// `Type` misses subtypes where `is` would not. Quiet inside the `when`
/// guard of a `case T x` (there `x.GetType() == typeof(T)` narrows to
/// exactly `T` on purpose), wherever it sits in the guard.
///
/// CR0086 (correctness, note, priority): a constructor (or a field
/// initializer, which runs before the base constructor) calling a
/// `virtual` or `abstract` member of its own type runs the override
/// before the derived constructor has run. Guards: the callee is
/// `virtual`/`abstract` on the constructed type or a base and not
/// overridden `sealed` in this type; a `sealed` class stays quiet (nothing
/// can override); every `this.<member>` reference inside constructor-time
/// code counts — assignment right-hand sides, loops, try blocks, field
/// initialisers. Yields to CA2214.
///
/// CR0087 (performance, fix): `status.ToString() == "Active"` on an enum
/// is `status == Status.Active`: no allocation, no cache lookup, and a
/// rename survives. Guards: typed enum, the literal equal to a member
/// name (case-sensitive), not `[Flags]` (a combined value prints a list),
/// `ToString()` parameterless; `!=` becomes `!=`; the enum spelled as the
/// receiver's type is spelled at the site.
module CSharp.Refactor.TypeNotes

open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax
open Microsoft.CodeAnalysis.Text

[<Literal>]
let MutableStaticCode = "CR0084"

[<Literal>]
let TypeNameCode = "CR0085"

[<Literal>]
let VirtualInCtorCode = "CR0086"

[<Literal>]
let EnumTextCode = "CR0087"

// ---- CR0084 ----

let private mutableStatics (tree: SyntaxTree) (model: SemanticModel) : Suggestion list =
    let text = tree.GetText()

    tree.GetRoot().DescendantNodes()
    |> Seq.collect (fun n ->
        match n with
        | :? FieldDeclarationSyntax as fd when
            fd.Modifiers |> Seq.exists (fun m -> m.IsKind SyntaxKind.StaticKeyword)
            && fd.Modifiers |> Seq.exists (fun m -> m.IsKind SyntaxKind.PublicKeyword)
            && not (fd.Modifiers |> Seq.exists (fun m -> m.IsKind SyntaxKind.ReadOnlyKeyword))
            && not (fd.Modifiers |> Seq.exists (fun m -> m.IsKind SyntaxKind.ConstKeyword))
            ->
            fd.Declaration.Variables
            |> Seq.choose (fun v ->
                match model.GetDeclaredSymbol v with
                | :? IFieldSymbol as field ->
                    // assignment sites across the compilation, and self-updates
                    // assignment sites across the compilation, and self-updates
                    // (`x++`, `x += n`, `x = x + 1`), from the index
                    let index = Index.ofCompilation model.Compilation
                    let writes = Index.writesOf index field

                    let selfUpdate =
                        writes |> List.exists (fun w -> w = Index.Elsewhere)
                        && (Index.usesOf index field
                            |> List.exists (fun u ->
                                match u.Id.Parent with
                                | :? PostfixUnaryExpressionSyntax
                                | :? PrefixUnaryExpressionSyntax -> true
                                | :? AssignmentExpressionSyntax as a ->
                                    not (a.IsKind SyntaxKind.SimpleAssignmentExpression)
                                    || (a.Left.Span = u.Id.Span && Text.mentionsName field.Name a.Right)
                                | _ -> false))

                    let sites = writes

                    if sites.Length >= 2 || selfUpdate then
                        let modifiers = fd.Modifiers
                        let publicToken = modifiers |> Seq.find (fun m -> m.IsKind SyntaxKind.PublicKeyword)
                        let staticToken = modifiers |> Seq.find (fun m -> m.IsKind SyntaxKind.StaticKeyword)

                        Some
                            {
                                Code = MutableStaticCode
                                Message =
                                    $"'{field.Name}' is a public mutable static written from {sites.Length} site(s): shared state anything can race on — make it private, readonly, or an instance"
                                Span = v.Identifier.Span
                                Fixes =
                                    [
                                        Suggestion.fix
                                            "Make it private"
                                            MutableStaticCode
                                            [ Suggestion.replace publicToken.Span "private" ]
                                        |> Suggestion.editorOnly
                                        Suggestion.fix
                                            "Make it readonly"
                                            MutableStaticCode
                                            [ Suggestion.insert staticToken.Span.End " readonly" ]
                                        |> Suggestion.editorOnly
                                    ]
                            }
                    else
                        None
                | _ -> None)
        | _ -> Seq.empty)
    |> List.ofSeq

// ---- CR0085 ----

let private isGetTypeCall (inv: InvocationExpressionSyntax) =
    Linq.nameOf inv = "GetType" && inv.ArgumentList.Arguments.Count = 0

/// Inside the `when` guard of a type-pattern case: an exact-type narrowing.
let private inTypeGuard (node: SyntaxNode) =
    node.Ancestors()
    |> Seq.exists (fun a ->
        match a with
        | :? WhenClauseSyntax as w ->
            match w.Parent with
            | :? CasePatternSwitchLabelSyntax as l -> l.Pattern :? DeclarationPatternSyntax
            | :? SwitchExpressionArmSyntax as arm -> arm.Pattern :? DeclarationPatternSyntax
            | _ -> false
        | _ -> false)

let private typeNames (tree: SyntaxTree) (model: SemanticModel) : Suggestion list =
    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun n ->
        match n with
        | :? BinaryExpressionSyntax as b when
            (b.IsKind SyntaxKind.EqualsExpression || b.IsKind SyntaxKind.NotEqualsExpression)
            && not (inTypeGuard b)
            ->
            let sides = [ b.Left, b.Right; b.Right, b.Left ]

            sides
            |> List.tryPick (fun (subject, other) ->
                match subject with
                // `x.GetType().Name == "…"`, `.FullName`
                | :? MemberAccessExpressionSyntax as m when
                    (m.Name.Identifier.ValueText = "Name" || m.Name.Identifier.ValueText = "FullName")
                    && (match m.Expression with
                        | :? InvocationExpressionSyntax as inv -> isGetTypeCall inv
                        | _ -> false)
                    && (other :? LiteralExpressionSyntax)
                    ->
                    Some(
                        Suggestion.note
                            TypeNameCode
                            "A type test by name breaks on rename and misses derived types: use 'is' with the type"
                            b.Span
                    )
                // `x.GetType() == typeof(T)`
                | :? InvocationExpressionSyntax as inv when isGetTypeCall inv && (other :? TypeOfExpressionSyntax) ->
                    Some(
                        Suggestion.note
                            TypeNameCode
                            "'GetType() == typeof(T)' misses derived types: 'is T' says what is meant, unless exactly T is the point"
                            b.Span
                    )
                | _ -> None)
        | _ -> None)
    |> List.ofSeq

// ---- CR0086 ----

/// Is the owner the type itself or one of its bases?
let rec private inheritsFrom (self: INamedTypeSymbol) (owner: INamedTypeSymbol) =
    not (isNull self)
    && (SymbolEqualityComparer.Default.Equals(self.OriginalDefinition, owner.OriginalDefinition)
        || inheritsFrom self.BaseType owner)

let private virtualInConstructors (tree: SyntaxTree) (model: SemanticModel) : Suggestion list =
    tree.GetRoot().DescendantNodes()
    |> Seq.collect (fun n ->
        match n with
        | :? TypeDeclarationSyntax as t when
            not (t :? InterfaceDeclarationSyntax)
            && not (t.Modifiers |> Seq.exists (fun m -> m.IsKind SyntaxKind.SealedKeyword))
            && not (t.Modifiers |> Seq.exists (fun m -> m.IsKind SyntaxKind.StaticKeyword))
            && not (t :? StructDeclarationSyntax)
            ->
            match model.GetDeclaredSymbol t with
            | null -> Seq.empty
            | self when self.IsSealed || self.TypeKind <> TypeKind.Class -> Seq.empty
            | self ->
                // constructor-time code: instance constructor bodies and field/property initializers
                let constructorTime: SyntaxNode seq =
                    t.Members
                    |> Seq.collect (fun m ->
                        match m with
                        | :? ConstructorDeclarationSyntax as c when
                            not (c.Modifiers |> Seq.exists (fun k -> k.IsKind SyntaxKind.StaticKeyword))
                            ->
                            seq {
                                if not (isNull c.Body) then
                                    yield c.Body :> SyntaxNode

                                if not (isNull c.ExpressionBody) then
                                    yield c.ExpressionBody :> SyntaxNode

                                if not (isNull c.Initializer) then
                                    yield c.Initializer :> SyntaxNode
                            }
                        | :? FieldDeclarationSyntax as f when
                            not (f.Modifiers |> Seq.exists (fun k -> k.IsKind SyntaxKind.StaticKeyword))
                            ->
                            f.Declaration.Variables
                            |> Seq.choose (fun v ->
                                if isNull v.Initializer then
                                    None
                                else
                                    Some(v.Initializer :> SyntaxNode))
                        | :? PropertyDeclarationSyntax as p when
                            not (isNull p.Initializer)
                            && not (p.Modifiers |> Seq.exists (fun k -> k.IsKind SyntaxKind.StaticKeyword))
                            ->
                            Seq.singleton (p.Initializer :> SyntaxNode)
                        | _ -> Seq.empty)

                constructorTime
                |> Seq.collect (fun body ->
                    body.DescendantNodes()
                    |> Seq.choose (fun x ->
                        let symbolOf (e: SyntaxNode) = model.GetSymbolInfo(e).Symbol

                        let candidate: (SyntaxNode * ISymbol) option =
                            match x with
                            | :? InvocationExpressionSyntax as inv ->
                                // a call on this (explicit or implicit), not inside a lambda
                                let receiverIsThis =
                                    match inv.Expression with
                                    | :? IdentifierNameSyntax -> true
                                    | :? MemberAccessExpressionSyntax as m -> m.Expression :? ThisExpressionSyntax
                                    | _ -> false

                                if receiverIsThis then
                                    Some(inv :> SyntaxNode, symbolOf inv)
                                else
                                    None
                            | :? MemberAccessExpressionSyntax as m when
                                (m.Expression :? ThisExpressionSyntax)
                                && not (
                                    m.Parent :? InvocationExpressionSyntax
                                    && (m.Parent :?> InvocationExpressionSyntax).Expression.Span = m.Span
                                )
                                ->
                                Some(m :> SyntaxNode, symbolOf m)
                            | :? IdentifierNameSyntax as id when
                                not (
                                    id.Parent :? MemberAccessExpressionSyntax
                                    && (id.Parent :?> MemberAccessExpressionSyntax).Name.Span = id.Span
                                )
                                && not (id.Parent :? InvocationExpressionSyntax)
                                ->
                                match symbolOf id with
                                | :? IPropertySymbol as p -> Some(id :> SyntaxNode, p :> ISymbol)
                                | _ -> None
                            | _ -> None

                        let inLambda =
                            x.Ancestors()
                            |> Seq.takeWhile (fun a -> not (obj.ReferenceEquals(a, body)))
                            |> Seq.exists (fun a ->
                                a :? AnonymousFunctionExpressionSyntax || a :? LocalFunctionStatementSyntax)

                        match candidate with
                        | Some(site, (:? IMethodSymbol as m)) when
                            not inLambda
                            && (m.IsVirtual || m.IsAbstract || (m.IsOverride && not m.IsSealed))
                            && not m.IsStatic
                            && inheritsFrom self m.ContainingType
                            ->
                            Some(
                                Suggestion.note
                                    VirtualInCtorCode
                                    $"'{m.Name}' is virtual and called while constructing: an override runs before the derived constructor has initialised its state — make it non-virtual, seal the type, or move the call out of the constructor"
                                    site.Span
                            )
                        | Some(site, (:? IPropertySymbol as p)) when
                            not inLambda
                            && (p.IsVirtual || p.IsAbstract || (p.IsOverride && not p.IsSealed))
                            && not p.IsStatic
                            && inheritsFrom self p.ContainingType
                            ->
                            Some(
                                Suggestion.note
                                    VirtualInCtorCode
                                    $"'{p.Name}' is virtual and read while constructing: an override runs before the derived constructor has initialised its state — make it non-virtual, seal the type, or move the call out of the constructor"
                                    site.Span
                            )
                        | _ -> None))
        | _ -> Seq.empty)
    |> List.ofSeq

// ---- CR0087 ----

let private enumTexts (tree: SyntaxTree) (model: SemanticModel) : Suggestion list =
    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun n ->
        match n with
        | :? BinaryExpressionSyntax as b when
            (b.IsKind SyntaxKind.EqualsExpression || b.IsKind SyntaxKind.NotEqualsExpression)
            && not (Text.insideExpressionTree model b)
            ->
            let sides = [ b.Left, b.Right; b.Right, b.Left ]

            sides
            |> List.tryPick (fun (subject, other) ->
                match subject, other with
                | (:? InvocationExpressionSyntax as inv), (:? LiteralExpressionSyntax as lit) when
                    Linq.nameOf inv = "ToString"
                    && inv.ArgumentList.Arguments.Count = 0
                    && lit.IsKind SyntaxKind.StringLiteralExpression
                    ->
                    match inv.Expression with
                    | :? MemberAccessExpressionSyntax as m ->
                        match model.GetTypeInfo(m.Expression).Type with
                        | :? INamedTypeSymbol as enumType when enumType.TypeKind = TypeKind.Enum ->
                            let flags =
                                enumType.GetAttributes()
                                |> Seq.exists (fun a -> a.AttributeClass.ToDisplayString() = "System.FlagsAttribute")

                            let member' =
                                enumType.GetMembers()
                                |> Seq.tryFind (fun s -> s.Kind = SymbolKind.Field && s.Name = lit.Token.ValueText)

                            // two members sharing a value print as one of them: the text says
                            // less than the value would
                            let duplicateValues =
                                let values =
                                    enumType.GetMembers()
                                    |> Seq.choose (fun s ->
                                        match s with
                                        | :? IFieldSymbol as f when f.HasConstantValue ->
                                            Some(string f.ConstantValue)
                                        | _ -> None)
                                    |> List.ofSeq

                                values.Length <> (values |> List.distinct |> List.length)

                            match member' with
                            | Some field when not (flags || duplicateValues) ->
                                let typeText = enumType.ToMinimalDisplayString(model, b.SpanStart)

                                let op =
                                    if b.IsKind SyntaxKind.EqualsExpression then
                                        " == "
                                    else
                                        " != "

                                let replacement = m.Expression.ToString() + op + typeText + "." + field.Name
                                let edit = Suggestion.replace b.Span replacement

                                if Guards.speculativeCheck model [ edit ] then
                                    Some
                                        {
                                            Code = EnumTextCode
                                            Message =
                                                "Comparing an enum's text to a literal allocates and breaks on rename: compare the values"
                                            Span = b.Span
                                            Fixes = [ Suggestion.fix "Compare the enum values" EnumTextCode [ edit ] ]
                                        }
                                else
                                    None
                            | _ -> None
                        | _ -> None
                    | _ -> None
                | _ -> None)
        | _ -> None)
    |> List.ofSeq

let analyze (tree: SyntaxTree) (model: SemanticModel) (_ctx: RuleContext) : Suggestion list =
    mutableStatics tree model
    @ typeNames tree model
    @ virtualInConstructors tree model
    @ enumTexts tree model
