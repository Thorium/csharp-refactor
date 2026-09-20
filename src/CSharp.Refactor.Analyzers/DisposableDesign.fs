/// Types and the disposables they own.
///
/// CR0061 (correctness, note, priority): a type that constructs a
/// disposable into a field and does not implement `IDisposable` cannot
/// be released by its owner. Guards: the field is assigned a `new` of a
/// disposable type (or a BCL factory's result) in its initializer or a
/// constructor — an injected constructor parameter is the injector's; an
/// interface inheriting `IDisposable` counts as implementing; a
/// disposable base class makes the note about overriding its
/// `Dispose(bool)`; a member that itself calls `Dispose`/`Close` on the
/// field is manual management, not an ownerless resource; a disposable
/// built with the object itself (`new X(this)`) registers with it and is
/// its owner's; the no-ownership types (`HttpClient` — a shared
/// lifetime; `MemoryStream`/`StringReader`/`StringWriter` over a
/// caller's buffer) are not noted. Yields to CA1001.
///
/// CR0062 (correctness, note, priority): a `Dispose` that never releases
/// one of the type's own constructed disposable fields. Release means
/// `Dispose`/`DisposeAsync`/`Close` on the field, directly or through an
/// upcast, or the field passed as an argument (whatever received it may
/// release it); `Cancel()` touches and frees nothing and gets its own
/// wording; the interface `Dispose` is followed one hop into
/// `Dispose(bool)`; a `base.Dispose()` hand-off and a `System.Reactive`
/// file (unsubscribe, not release) stay quiet. Yields to CA2213.
///
/// CR0063 (correctness, note): `public void Dispose()` on a type not
/// implementing `IDisposable` (through a base type either): nothing can
/// `using` it, and the runtime's `using` never finds it.
module CSharp.Refactor.DisposableDesign

open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax
open Microsoft.CodeAnalysis.Text

[<Literal>]
let OwnerlessCode = "CR0061"

[<Literal>]
let UnreleasedCode = "CR0062"

[<Literal>]
let FakeDisposeCode = "CR0063"

let private isDisposableType (t: ITypeSymbol) =
    match t with
    | null -> false
    | t ->
        let name (i: INamedTypeSymbol) = i.ToDisplayString()

        (match t with
         | :? INamedTypeSymbol as n -> name n = "System.IDisposable" || name n = "System.IAsyncDisposable"
         | _ -> false)
        || t.AllInterfaces
           |> Seq.exists (fun i -> name i = "System.IDisposable" || name i = "System.IAsyncDisposable")

let private noOwnership =
    set
        [
            "System.Net.Http.HttpClient"
            "System.IO.MemoryStream"
            "System.IO.StringReader"
            "System.IO.StringWriter"
            "System.Threading.Tasks.Task"
            // a lazily created wait handle: disposal is optional by the BCL's own guidance
            "System.Threading.SemaphoreSlim"
            "System.Threading.ManualResetEventSlim"
        ]

let private factories =
    set
        [
            "MD5.Create"
            "SHA1.Create"
            "SHA256.Create"
            "SHA384.Create"
            "SHA512.Create"
            "Aes.Create"
            "RandomNumberGenerator.Create"
            "File.OpenRead"
            "File.OpenWrite"
            "File.Create"
            "File.Open"
            "File.OpenText"
            "File.CreateText"
            "File.AppendText"
        ]

/// The disposable fields (and auto-properties) a type constructs itself.
let private ownedFields (model: SemanticModel) (t: TypeDeclarationSyntax) : ISymbol list =
    let candidates: ISymbol list =
        t.Members
        |> Seq.collect (fun m ->
            match m with
            | :? FieldDeclarationSyntax as f ->
                f.Declaration.Variables
                |> Seq.choose (fun v ->
                    match model.GetDeclaredSymbol v with
                    | :? IFieldSymbol as fs when isDisposableType fs.Type -> Some(fs :> ISymbol)
                    | _ -> None)
            | :? PropertyDeclarationSyntax as p ->
                match model.GetDeclaredSymbol p with
                | :? IPropertySymbol as ps when isDisposableType ps.Type && not (isNull p.AccessorList) ->
                    Seq.singleton (ps :> ISymbol)
                | _ -> Seq.empty
            | _ -> Seq.empty)
        |> List.ofSeq

    let constructions (e: ExpressionSyntax) =
        match e with
        | :? BaseObjectCreationExpressionSyntax as c ->
            let ty = model.GetTypeInfo(c).Type

            isDisposableType ty
            && not (noOwnership.Contains(ty.OriginalDefinition.ToDisplayString()))
            // built with the object itself: registers with it
            && not (
                not (isNull c.ArgumentList)
                && c.ArgumentList.Arguments
                   |> Seq.exists (fun a -> a.Expression :? ThisExpressionSyntax)
            )
        | :? InvocationExpressionSyntax as inv -> factories.Contains(inv.Expression.ToString())
        | _ -> false

    candidates
    |> List.filter (fun symbol ->
        t.DescendantNodes()
        |> Seq.exists (fun n ->
            match n with
            | :? VariableDeclaratorSyntax as v when
                not (isNull v.Initializer)
                && SymbolEqualityComparer.Default.Equals(model.GetDeclaredSymbol v, symbol)
                ->
                constructions v.Initializer.Value
            | :? PropertyDeclarationSyntax as p when
                not (isNull p.Initializer)
                && SymbolEqualityComparer.Default.Equals(model.GetDeclaredSymbol p, symbol)
                ->
                constructions p.Initializer.Value
            | :? AssignmentExpressionSyntax as a when
                a.IsKind SyntaxKind.SimpleAssignmentExpression
                && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(a.Left).Symbol, symbol)
                && (a.Ancestors() |> Seq.exists (fun x -> x :? ConstructorDeclarationSyntax))
                ->
                constructions a.Right
            | _ -> false))

let private releasesField (model: SemanticModel) (body: SyntaxNode) (field: ISymbol) =
    body.DescendantNodes()
    |> Seq.exists (fun n ->
        let refersTo (e: ExpressionSyntax) =
            let e =
                match e with
                | :? ParenthesizedExpressionSyntax as p -> p.Expression
                | :? CastExpressionSyntax as c -> c.Expression
                | e -> e

            SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(e).Symbol, field)

        match n with
        | :? InvocationExpressionSyntax as inv ->
            let name = Linq.nameOf inv

            ((name = "Dispose" || name = "DisposeAsync" || name = "Close")
             && (Linq.receiverOf inv |> Option.exists refersTo))
            // handed to anything: whatever received it may release it
            || inv.ArgumentList.Arguments |> Seq.exists (fun a -> refersTo a.Expression)
        | _ -> false)

/// The `Dispose` body, followed one hop into `Dispose(bool)`.
let private disposeBodies (t: TypeDeclarationSyntax) =
    t.Members
    |> Seq.choose (fun m ->
        match m with
        | :? MethodDeclarationSyntax as md when
            md.Identifier.ValueText = "Dispose" || md.Identifier.ValueText = "DisposeAsync"
            ->
            if not (isNull md.Body) then
                Some(md.Body :> SyntaxNode)
            elif not (isNull md.ExpressionBody) then
                Some(md.ExpressionBody :> SyntaxNode)
            else
                None
        | _ -> None)
    |> List.ofSeq

let analyze (tree: SyntaxTree) (model: SemanticModel) (_ctx: RuleContext) : Suggestion list =
    let isRx =
        match tree.GetRoot() with
        | :? CompilationUnitSyntax as root ->
            root.Usings
            |> Seq.exists (fun u -> u.Name.ToString().StartsWith "System.Reactive")
        | _ -> false

    tree.GetRoot().DescendantNodes()
    |> Seq.collect (fun n ->
        match n with
        | :? TypeDeclarationSyntax as t when
            not (t :? InterfaceDeclarationSyntax)
            && not (t.Modifiers |> Seq.exists (fun m -> m.IsKind SyntaxKind.StaticKeyword))
            ->
            match model.GetDeclaredSymbol t with
            | null -> []
            | self ->
                let implementsDisposable = isDisposableType self

                let baseDisposable =
                    not (isNull self.BaseType)
                    && self.BaseType.SpecialType = SpecialType.None
                    && isDisposableType self.BaseType

                let owned = ownedFields model t
                let bodies = disposeBodies t

                let disposeMethods =
                    t.Members
                    |> Seq.choose (fun m ->
                        match m with
                        | :? MethodDeclarationSyntax as md when md.Identifier.ValueText = "Dispose" -> Some md
                        | _ -> None)
                    |> List.ofSeq

                [
                    // CR0063: a Dispose on a type that is not disposable
                    for md in disposeMethods do
                        if
                            not implementsDisposable
                            && md.ParameterList.Parameters.Count = 0
                            && md.Modifiers |> Seq.exists (fun m -> m.IsKind SyntaxKind.PublicKeyword)
                        then
                            yield
                                Suggestion.note
                                    FakeDisposeCode
                                    "A public Dispose on a type that does not implement IDisposable: nothing can 'using' it"
                                    md.Identifier.Span

                    // CR0061: owned disposables, no IDisposable
                    if not (implementsDisposable || owned.IsEmpty) then
                        let managed = owned |> List.filter (releasesField model t >> not)

                        match managed with
                        | first :: _ ->
                            let names = managed |> List.map (fun f -> f.Name) |> String.concat ", "

                            let where =
                                first.DeclaringSyntaxReferences
                                |> Seq.tryFind (fun r -> r.SyntaxTree = tree)
                                |> Option.map (fun r -> r.Span)
                                |> Option.defaultValue t.Identifier.Span

                            yield
                                Suggestion.note
                                    OwnerlessCode
                                    (if baseDisposable then
                                         $"'{t.Identifier.ValueText}' constructs {names} and its disposable base never releases them: override Dispose(bool) and dispose them"
                                     else
                                         $"'{t.Identifier.ValueText}' constructs {names} and does not implement IDisposable: nothing can release them — implement IDisposable and dispose them there, or take them from the caller")
                                    where
                        | [] -> ()

                    // CR0062: a Dispose that never releases an owned field
                    if implementsDisposable && not isRx && not bodies.IsEmpty then
                        let handsToBase =
                            bodies |> List.exists (fun b -> b.ToString().Contains "base.Dispose(")

                        if not handsToBase then
                            for f in owned do
                                let released = bodies |> List.exists (fun b -> releasesField model b f)

                                if not released then
                                    let cancelled =
                                        bodies
                                        |> List.exists (fun b ->
                                            b.DescendantNodes()
                                            |> Seq.exists (fun x ->
                                                match x with
                                                | :? InvocationExpressionSyntax as inv ->
                                                    Linq.nameOf inv = "Cancel"
                                                    && (Linq.receiverOf inv
                                                        |> Option.exists (fun r ->
                                                            SymbolEqualityComparer.Default.Equals(
                                                                model.GetSymbolInfo(r).Symbol,
                                                                f
                                                            )))
                                                | _ -> false))

                                    let where =
                                        f.DeclaringSyntaxReferences
                                        |> Seq.tryFind (fun r -> r.SyntaxTree = tree)
                                        |> Option.map (fun r -> r.Span)
                                        |> Option.defaultValue t.Identifier.Span

                                    yield
                                        Suggestion.note
                                            UnreleasedCode
                                            (if cancelled then
                                                 $"Dispose cancels '{f.Name}' but never disposes it: Cancel frees nothing"
                                             else
                                                 $"Dispose never releases '{f.Name}', which this type constructs: dispose it")
                                            where
                ]
        | _ -> [])
    |> List.ofSeq
