/// CR0023 (performance, fix): a startup-built list probed by `Contains`
/// inside a loop or a collection callback is a linear scan per probe; a
/// set is a hash lookup.
///
///     static readonly string[] Allowed = { "a", "b", "c" };
///     … xs.Where(x => Allowed.Contains(x))
///
///     static readonly FrozenSet<string> Allowed = new[] { "a", "b", "c" }.ToFrozenSet();   // .NET 8+
///     static readonly HashSet<string> Allowed = new HashSet<string> { "a", "b", "c" };       // else
///
/// Guards: the collection is a `static readonly` field (or a field
/// initialised once) of array or `List<T>` type, initialised from a
/// literal, never reassigned or mutated anywhere in the compilation
/// (typed: no `Add`/`Remove`/indexer set on it, never passed by
/// reference); the probe is a `Contains` call inside a loop or a lambda
/// handed to a collection operator, and the probed value is not the
/// collection's own loop variable; the element type has value equality
/// (`string`, an enum, a primitive, a record, a struct, or a class
/// implementing `IEquatable<T>`); every use in the compilation is a probe
/// — then the declaration converts in place (private or internal; a
/// public field is API and gets a note) — otherwise a note names the
/// companion set. `FrozenSet<T>` where `System.Collections.Frozen`
/// resolves, else `HashSet<T>`; the `using` is added. Measured with the
/// build cost charged against the probes.
module CSharp.Refactor.ContainsSet

open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax
open Microsoft.CodeAnalysis.Text

[<Literal>]
let Code = "CR0023"

let private hasValueEquality (t: ITypeSymbol) =
    match t with
    | null -> false
    | t when t.SpecialType = SpecialType.System_String -> true
    | t when t.TypeKind = TypeKind.Enum -> true
    | t when t.IsValueType -> true
    | :? INamedTypeSymbol as n when n.IsRecord -> true
    | t ->
        t.AllInterfaces
        |> Seq.exists (fun i -> i.OriginalDefinition.ToDisplayString() = "System.IEquatable<T>")

/// The elements of a literal initialiser: `{ a, b }`, `new[] { a, b }`,
/// `new T[] { … }`, `new List<T> { … }`, `[a, b]`.
let private literalElements (init: ExpressionSyntax) : ExpressionSyntax list option =
    match init with
    | :? InitializerExpressionSyntax as i -> Some(List.ofSeq i.Expressions)
    | :? ImplicitArrayCreationExpressionSyntax as a -> Some(List.ofSeq a.Initializer.Expressions)
    | :? ArrayCreationExpressionSyntax as a when not (isNull a.Initializer) ->
        Some(List.ofSeq a.Initializer.Expressions)
    | :? ObjectCreationExpressionSyntax as o when not (isNull o.Initializer) ->
        Some(List.ofSeq o.Initializer.Expressions)
    | :? CollectionExpressionSyntax as c ->
        let elements =
            c.Elements
            |> Seq.choose (fun e ->
                match e with
                | :? ExpressionElementSyntax as x -> Some x.Expression
                | _ -> None)
            |> List.ofSeq

        if elements.Length = c.Elements.Count then
            Some elements
        else
            None
    | _ -> None

let private mutators =
    set
        [
            "Add"
            "AddRange"
            "Insert"
            "Remove"
            "RemoveAt"
            "RemoveAll"
            "Clear"
            "Sort"
            "Reverse"
            "SetValue"
            "CopyTo"
            "Resize"
        ]

type private Use =
    | Probe of InvocationExpressionSyntax
    | Other

let private classify (model: SemanticModel) (id: IdentifierNameSyntax) : Use =
    match id.Parent with
    | :? MemberAccessExpressionSyntax as m when m.Expression.Span = id.Span ->
        match m.Parent with
        | :? InvocationExpressionSyntax as inv when
            m.Name.Identifier.ValueText = "Contains" && inv.ArgumentList.Arguments.Count = 1
            ->
            Probe inv
        | _ -> Other
    | _ -> Other

let analyze (tree: SyntaxTree) (model: SemanticModel) (ctx: RuleContext) : Suggestion list =
    let compilation = model.Compilation

    let frozen =
        not (isNull (compilation.GetTypeByMetadataName "System.Collections.Frozen.FrozenSet`1"))

    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun node ->
        match node with
        | :? FieldDeclarationSyntax as fd when
            fd.Declaration.Variables.Count = 1
            && not (isNull fd.Declaration.Variables.[0].Initializer)
            && fd.Modifiers |> Seq.exists (fun t -> t.IsKind SyntaxKind.ReadOnlyKeyword)
            ->
            let v = fd.Declaration.Variables.[0]

            match model.GetDeclaredSymbol v with
            | :? IFieldSymbol as field ->
                let elementType =
                    match field.Type with
                    | :? IArrayTypeSymbol as a when a.Rank = 1 -> Some a.ElementType
                    | :? INamedTypeSymbol as n when
                        n.OriginalDefinition.ToDisplayString() = "System.Collections.Generic.List<T>"
                        ->
                        Some n.TypeArguments.[0]
                    | _ -> None

                match elementType, literalElements v.Initializer.Value with
                | Some elementType, Some elements when
                    hasValueEquality elementType
                    && not elements.IsEmpty
                    // a null element: the set may refuse it
                    && not (elements |> List.exists (fun e -> e.IsKind SyntaxKind.NullLiteralExpression))
                    ->
                    // every use, in every tree of the compilation that can see the
                    // field: a private one's are the trees declaring its type (this
                    // one, or the parts of a partial type); the rest only where the
                    // text spells the name at all — walking every tree's nodes per
                    // candidate was a fifth of a second per file of literal tables
                    let candidateTrees =
                        if field.DeclaredAccessibility = Accessibility.Private then
                            field.ContainingType.DeclaringSyntaxReferences
                            |> Seq.map (fun r -> r.SyntaxTree)
                            |> Seq.distinct
                        else
                            compilation.SyntaxTrees
                            |> Seq.filter (fun t -> t = tree || t.GetText().ToString().Contains field.Name)

                    let uses =
                        candidateTrees
                        |> Seq.collect (fun t ->
                            let m = if t = tree then model else compilation.GetSemanticModel t

                            t.GetRoot().DescendantNodes()
                            |> Seq.choose (fun n ->
                                match n with
                                | :? IdentifierNameSyntax as id when
                                    id.Identifier.ValueText = field.Name
                                    && SymbolEqualityComparer.Default.Equals(m.GetSymbolInfo(id).Symbol, field)
                                    ->
                                    Some(m, id)
                                | _ -> None))
                        |> List.ofSeq

                    let probes =
                        uses
                        |> List.choose (fun (m, id) ->
                            match classify m id with
                            | Probe inv ->
                                // in a loop or a collection callback, not probing the collection's own element
                                let perElement =
                                    Linq.insideLoop inv
                                    || (inv.Ancestors()
                                        |> Seq.exists (fun a -> a :? AnonymousFunctionExpressionSyntax))

                                if perElement then Some inv else None
                            | Other -> None)

                    let mutated =
                        uses
                        |> List.exists (fun (_, id) ->
                            match id.Parent with
                            | :? MemberAccessExpressionSyntax as m when m.Expression.Span = id.Span ->
                                mutators.Contains m.Name.Identifier.ValueText
                            | :? ElementAccessExpressionSyntax as e ->
                                match e.Parent with
                                | :? AssignmentExpressionSyntax as a -> a.Left.Span = e.Span
                                | _ -> false
                            | :? AssignmentExpressionSyntax as a -> a.Left.Span = id.Span
                            | :? ArgumentSyntax as a -> not (a.RefKindKeyword.IsKind SyntaxKind.None)
                            | _ -> false)

                    if probes.IsEmpty || mutated then
                        None
                    else
                        let allProbes =
                            uses
                            |> List.forall (fun (m, id) ->
                                match classify m id with
                                | Probe _ -> true
                                | Other -> false)

                        let isPublic =
                            field.DeclaredAccessibility = Accessibility.Public
                            || field.DeclaredAccessibility = Accessibility.Protected

                        let setName = if frozen then "FrozenSet" else "HashSet"

                        let ns =
                            if frozen then
                                "System.Collections.Frozen"
                            else
                                "System.Collections.Generic"

                        let elementText = elementType.ToMinimalDisplayString(model, fd.SpanStart)

                        let elementsText =
                            elements |> List.map (fun e -> e.ToString()) |> String.concat ", "

                        let message =
                            $"'{field.Name}' is scanned end to end by every 'Contains' ({probes.Length} probe site(s)): a {setName}<{elementText}> looks up in constant time"

                        if not allProbes || isPublic || not (RuleContext.internalShapeOpen ctx) then
                            Some(
                                Suggestion.note
                                    Code
                                    (message
                                     + (if isPublic then
                                            " (held: the field is public API; a companion set beside it would do)"
                                        else
                                            " (held: other uses pin the list type; a companion set beside it would do)"))
                                    probes.Head.Span
                            )
                        else
                            let newType = $"{setName}<{elementText}>"

                            let newInit =
                                if frozen then
                                    "new[] { " + elementsText + " }.ToFrozenSet()"
                                else
                                    "new HashSet<" + elementText + "> { " + elementsText + " }"

                            match Usings.importEdit model tree fd.SpanStart ns setName with
                            | None -> None
                            | Some usingEdits ->
                                let edits =
                                    usingEdits
                                    @ [
                                        Suggestion.replace fd.Declaration.Type.Span newType
                                        Suggestion.replace v.Initializer.Value.Span newInit
                                    ]

                                if Guards.speculativeCheck model edits then
                                    Some
                                        {
                                            Code = Code
                                            Message = message
                                            Span = fd.Declaration.Type.Span
                                            Fixes = [ Suggestion.fix ("Make it a " + setName) Code edits ]
                                        }
                                else
                                    None
                | _ -> None
            | _ -> None
        | _ -> None)
    |> List.ofSeq
