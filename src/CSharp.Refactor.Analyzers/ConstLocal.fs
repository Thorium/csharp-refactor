/// CR0172 (idiom, fix, API): a local or a `static readonly` field
/// initialised with a constant and never written is a `const` — the value
/// the compiler folds, and the declaration that says the name names a
/// value, not a slot:
///
///     var schema = "app";                       →  const string schema = "app";
///     int retries = 3;                          →  const int retries = 3;
///     static readonly string Prefix = "v";      →  const string Prefix = "v";
///
/// The F# twin of FR0130.s `[<Literal>]` (and, for the local, the nearest
/// C# has to FR0007.s `let mutable` strip). A field is API: a consumer
/// compiled against the field loads a slot the `const` no longer is, so a
/// public or protected field waits for `--api-changes` (or a leaf), an
/// internal one for the absence of friends. What follows from the local: a hole filled from the
/// constant (`$"SET search_path = {schema}"`) reads as text to CR0120,
/// where the `var` read as a value that might have been reassigned.
///
/// Guards: every declarator of the statement has an initialiser the
/// compiler folds to a constant (`GetConstantValue`: a literal, a `const`,
/// an arithmetic or concatenation of those, an enum member, a C# 10
/// constant interpolation) of a type a `const` may have (a primitive, a
/// string, an enum — never `null`, `default`, a `decimal` is fine); no
/// write to the local anywhere in the member — an assignment, a compound
/// assignment, `++`/`--`, a `ref`/`out`/`in` argument, a `ref` local or
/// `ref` return, a deconstruction target — a `var` spells the constant's
/// type in its place; `using`, `ref`, `scoped` and already-`const`
/// declarations are left alone, and so is a declaration under a comment
/// or directive inside it; the speculative re-bind settles the rest. The
/// field: `static readonly`, no attribute (`[ThreadStatic]` means the
/// slot), the same constant initialiser and type, no write anywhere in the
/// compilation (a static constructor could), the scope gate open.
module CSharp.Refactor.ConstLocal

open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax
open Microsoft.CodeAnalysis.Text

[<Literal>]
let Code = "CR0172"

/// A type a `const` may be declared as: the primitives, `string`, an enum.
let private constable (t: ITypeSymbol) =
    not (isNull t)
    && (t.SpecialType = SpecialType.System_String
        || t.TypeKind = TypeKind.Enum
        || (match t.SpecialType with
            | SpecialType.System_Boolean
            | SpecialType.System_Char
            | SpecialType.System_SByte
            | SpecialType.System_Byte
            | SpecialType.System_Int16
            | SpecialType.System_UInt16
            | SpecialType.System_Int32
            | SpecialType.System_UInt32
            | SpecialType.System_Int64
            | SpecialType.System_UInt64
            | SpecialType.System_Single
            | SpecialType.System_Double
            | SpecialType.System_Decimal -> true
            | _ -> false))

/// Is the local written anywhere after its declaration: assigned, stepped,
/// passed by reference, deconstructed into, taken by `ref`?
let private written (model: SemanticModel) (local: ILocalSymbol) (scope: SyntaxNode) =
    let isLocal (e: ExpressionSyntax) =
        match e with
        | :? IdentifierNameSyntax as id when id.Identifier.ValueText = local.Name ->
            SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(id).Symbol, local)
        | _ -> false

    scope.DescendantNodes()
    |> Seq.exists (fun n ->
        match n with
        | :? AssignmentExpressionSyntax as a ->
            isLocal a.Left
            || (match a.Left with
                | :? TupleExpressionSyntax as t -> t.Arguments |> Seq.exists (fun arg -> isLocal arg.Expression)
                | _ -> false)
        | :? PostfixUnaryExpressionSyntax as u -> isLocal u.Operand
        | :? PrefixUnaryExpressionSyntax as u when
            u.IsKind SyntaxKind.PreIncrementExpression
            || u.IsKind SyntaxKind.PreDecrementExpression
            || u.IsKind SyntaxKind.AddressOfExpression
            ->
            isLocal u.Operand
        | :? ArgumentSyntax as arg when not (arg.RefKindKeyword.IsKind SyntaxKind.None) -> isLocal arg.Expression
        | :? RefExpressionSyntax as r -> isLocal r.Expression
        | _ -> false)

let private locals (tree: SyntaxTree) (model: SemanticModel) : Suggestion list =
    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun node ->
        match node with
        | :? LocalDeclarationStatementSyntax as decl when
            not decl.IsConst
            && decl.UsingKeyword.IsKind SyntaxKind.None
            && decl.Modifiers.Count = 0
            && not (decl.Declaration.Type.IsKind SyntaxKind.RefType)
            && not (Text.holdsCommentOrDirective decl)
            && decl.Declaration.Variables.Count >= 1
            && decl.Declaration.Variables
               |> Seq.forall (fun v -> not (isNull v.Initializer || v.Initializer.Value :? RefExpressionSyntax))
            ->
            // the member (or top-level program) the local lives in: where a write could sit
            let scope =
                decl.Ancestors()
                |> Seq.tryFind (fun a ->
                    // a top-level statement is a member of its own: the write may sit in the next
                    (a :? MemberDeclarationSyntax && not (a :? GlobalStatementSyntax))
                    || a :? AnonymousFunctionExpressionSyntax
                    || a :? LocalFunctionStatementSyntax
                    || a :? CompilationUnitSyntax)

            let locals =
                decl.Declaration.Variables
                |> Seq.map (fun v ->
                    match model.GetDeclaredSymbol v with
                    | :? ILocalSymbol as l when
                        constable l.Type
                        && (let c = model.GetConstantValue v.Initializer.Value
                            c.HasValue && not (isNull c.Value))
                        && (match scope with
                            | Some s -> not (written model l s)
                            | None -> false)
                        ->
                        Some l
                    | _ -> None)
                |> List.ofSeq

            if locals |> List.exists Option.isNone then
                None
            else
                let first = (List.head locals).Value

                // `var` spells the constant's type; an explicit type stays as written
                let typeEdit =
                    if decl.Declaration.Type.IsVar then
                        // a `var` local is inferred `string?` under nullable: the constant is not null
                        let spelled =
                            first.Type
                                .WithNullableAnnotation(NullableAnnotation.NotAnnotated)
                                .ToMinimalDisplayString(model, decl.Declaration.Type.SpanStart)

                        [ Suggestion.replace decl.Declaration.Type.Span spelled ]
                    else
                        []

                let edits = Suggestion.insert decl.Declaration.SpanStart "const " :: typeEdit

                if Guards.speculativeCheck model edits then
                    Some
                        {
                            Code = Code
                            Message =
                                "The local is initialised with a constant and never written: 'const' says so, and the compiler folds it"
                            Span = decl.Declaration.Span
                            Fixes = [ Suggestion.fix "Make it const" Code edits ]
                        }
                else
                    None
        | _ -> None)
    |> List.ofSeq

/// Does the scope gate let this field change shape? A `const` is a
/// different member to a consumer already compiled — a literal it reads
/// at its own compile time, not a field it loads — so a public or
/// protected one needs `--api-changes` (or a leaf), an internal one the
/// absence of friends; a private one is the file's own business.
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

/// The field half, FR0130's `[<Literal>]`: a `static readonly` field of a
/// constant, never written (a static constructor could), no attribute
/// (`[ThreadStatic]` and the like mean the slot), becomes a `const`.
let private fields (tree: SyntaxTree) (model: SemanticModel) (ctx: RuleContext) : Suggestion list =
    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun node ->
        match node with
        | :? FieldDeclarationSyntax as fd when
            fd.AttributeLists.Count = 0
            && fd.Modifiers |> Seq.exists (fun m -> m.IsKind SyntaxKind.StaticKeyword)
            && fd.Modifiers |> Seq.exists (fun m -> m.IsKind SyntaxKind.ReadOnlyKeyword)
            && not (fd.Modifiers |> Seq.exists (fun m -> m.IsKind SyntaxKind.ConstKeyword))
            && not (Text.holdsCommentOrDirective fd)
            && fd.Declaration.Variables.Count >= 1
            && fd.Declaration.Variables |> Seq.forall (fun v -> not (isNull v.Initializer))
            ->
            let fieldSymbols =
                fd.Declaration.Variables
                |> Seq.map (fun v ->
                    match model.GetDeclaredSymbol v with
                    | :? IFieldSymbol as f when
                        constable f.Type
                        && (let c = model.GetConstantValue v.Initializer.Value
                            c.HasValue && not (isNull c.Value))
                        && shapeOpen ctx f
                        && (Index.writesOf (Index.ofCompilation model.Compilation) f).IsEmpty
                        ->
                        Some f
                    | _ -> None)
                |> List.ofSeq

            if fieldSymbols |> List.exists Option.isNone then
                None
            else
                // `static readonly` out (each with the space after it), `const` in
                let dropped =
                    fd.Modifiers
                    |> Seq.filter (fun m -> m.IsKind SyntaxKind.StaticKeyword || m.IsKind SyntaxKind.ReadOnlyKeyword)
                    |> Seq.map (fun m -> Suggestion.replace (TextSpan.FromBounds(m.SpanStart, m.FullSpan.End)) "")
                    |> List.ofSeq

                let edits = dropped @ [ Suggestion.insert fd.Declaration.SpanStart "const " ]

                if Guards.speculativeCheck model edits then
                    Some
                        {
                            Code = Code
                            Message =
                                "The field holds a constant and nothing writes it: 'const' says so, and every use folds it"
                            Span = fd.Declaration.Span
                            Fixes = [ Suggestion.fix "Make it const" Code edits ]
                        }
                else
                    None
        | _ -> None)
    |> List.ofSeq

let analyze (tree: SyntaxTree) (model: SemanticModel) (ctx: RuleContext) : Suggestion list =
    locals tree model @ fields tree model ctx
