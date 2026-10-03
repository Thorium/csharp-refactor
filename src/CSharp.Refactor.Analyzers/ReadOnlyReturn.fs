/// CR0185 (idiom, fix, API, off by default): a method returning `List<T>`
/// or `IList<T>` whose every caller only reads the result returns
/// `IReadOnlyList<T>` - the signature then says the caller gets a view, not
/// a collection to change:
///
///     public List<Order> Pending() { … }   →   public IReadOnlyList<Order> Pending() { … }
///
/// Only the signature changes: the body still builds and returns its list.
/// Guards: not virtual, override, abstract, async or an interface
/// implementation; every reference in the compilation (or the host's
/// solution) is a call, and each call's value is read only - a `foreach`
/// source, `.Count`, an element read, `Contains`/`ToArray`, a LINQ call
/// other than `Reverse` (on a List it reverses in place; on the view it
/// returns a reversed copy and changes nothing), an argument to a parameter
/// typed `IEnumerable<T>`, `IReadOnlyCollection<T>` or `IReadOnlyList<T>`
/// (the overload bound then stays bound: every better one takes a type the
/// view does not convert to), the value of a `return` from a method of such
/// a type, or a `var` local read only in those ways and never reassigned.
/// The re-bind of every touched file settles the rest. Off by default:
/// the rule changes a signature for the reader's sake, not the program's;
/// the scope gate of CR0080 holds a public method to `--api-changes`.
module CSharp.Refactor.ReadOnlyReturn

open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax
open Microsoft.CodeAnalysis.Text

[<Literal>]
let Code = "CR0185"

let private readOnlyShapes =
    set
        [
            "System.Collections.Generic.IEnumerable<T>"
            "System.Collections.Generic.IReadOnlyList<T>"
            "System.Collections.Generic.IReadOnlyCollection<T>"
        ]

let private listInstanceReads = set [ "Count"; "Contains"; "ToArray" ]

/// Is the value of the expression (a call, or a local holding one) only read?
let rec private readOnly (model: SemanticModel) (depth: int) (e: ExpressionSyntax) : bool =
    match e.Parent with
    | :? ParenthesizedExpressionSyntax as p -> readOnly model depth p
    | :? ForEachStatementSyntax as fe -> fe.Expression = e
    | :? ElementAccessExpressionSyntax as ea when ea.Expression = e ->
        match ea.Parent with
        | :? AssignmentExpressionSyntax as a when a.Left = (ea :> ExpressionSyntax) -> false
        | :? ArgumentSyntax as arg -> arg.RefKindKeyword.IsKind SyntaxKind.None
        | :? PostfixUnaryExpressionSyntax
        | :? RefExpressionSyntax -> false
        | _ -> true
    | :? MemberAccessExpressionSyntax as ma when ma.Expression = e ->
        match model.GetSymbolInfo(ma).Symbol with
        | :? IPropertySymbol as p -> listInstanceReads.Contains p.Name
        | :? IMethodSymbol as m when m.IsExtensionMethod ->
            m.ContainingType.ToDisplayString() = "System.Linq.Enumerable"
            && m.Name <> "Reverse"
        | :? IMethodSymbol as m -> listInstanceReads.Contains m.Name
        | _ -> false
    | :? ArgumentSyntax as arg when arg.RefKindKeyword.IsKind SyntaxKind.None && isNull arg.NameColon ->
        match arg.Parent with
        | :? ArgumentListSyntax as list ->
            match model.GetSymbolInfo(list.Parent).Symbol with
            | :? IMethodSymbol as m ->
                let i = list.Arguments.IndexOf arg

                i < m.Parameters.Length
                && not m.Parameters.[i].IsParams
                && readOnlyShapes.Contains(m.Parameters.[i].Type.OriginalDefinition.ToDisplayString())
            | _ -> false
        | _ -> false
    | :? ReturnStatementSyntax as r ->
        match model.GetEnclosingSymbol r.SpanStart with
        | :? IMethodSymbol as m when m.MethodKind = MethodKind.Ordinary && not m.IsAsync ->
            readOnlyShapes.Contains(m.ReturnType.OriginalDefinition.ToDisplayString())
        | _ -> false
    | :? EqualsValueClauseSyntax as ev when depth = 0 ->
        // `var xs = M();`, read only in the ways above and never written again
        match ev.Parent with
        | :? VariableDeclaratorSyntax as v ->
            match v.Parent with
            | :? VariableDeclarationSyntax as d when d.Type.IsVar ->
                match model.GetDeclaredSymbol v with
                | :? ILocalSymbol as local ->
                    let scope = Text.enclosingMember v

                    let uses =
                        scope.DescendantNodes()
                        |> Seq.choose (fun n ->
                            match n with
                            | :? IdentifierNameSyntax as id when id.Identifier.ValueText = local.Name ->
                                match model.GetSymbolInfo(id).Symbol with
                                | :? ILocalSymbol as s when SymbolEqualityComparer.Default.Equals(s, local) -> Some id
                                | _ -> None
                            | _ -> None)
                        |> List.ofSeq

                    uses |> List.forall (fun id -> readOnly model (depth + 1) id)
                | _ -> false
            | _ -> false
        | _ -> false
    | _ -> false

let analyze (tree: SyntaxTree) (model: SemanticModel) (ctx: RuleContext) : Suggestion list =
    let shapeOpen (s: ISymbol) =
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

    // spelled List<T> or IList<T>, before the model is asked: an alias of
    // either is not read, and stays as written
    let spelledAsList (t: TypeSyntax) =
        let rec last (t: TypeSyntax) =
            match t with
            | :? QualifiedNameSyntax as q -> last q.Right
            | :? AliasQualifiedNameSyntax as a -> last a.Name
            | t -> t

        match last t with
        | :? GenericNameSyntax as g ->
            g.TypeArgumentList.Arguments.Count = 1
            && (g.Identifier.ValueText = "List" || g.Identifier.ValueText = "IList")
        | _ -> false

    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun n ->
        match n with
        | :? MethodDeclarationSyntax as md when
            spelledAsList md.ReturnType
            && not (
                md.Modifiers
                |> Seq.exists (fun m -> m.IsKind SyntaxKind.AsyncKeyword || m.IsKind SyntaxKind.PartialKeyword)
            )
            ->
            match model.GetDeclaredSymbol md with
            | null -> None
            | m when
                not m.IsVirtual
                && not m.IsOverride
                && not m.IsAbstract
                && m.ExplicitInterfaceImplementations.IsEmpty
                && (let d = m.ReturnType.OriginalDefinition.ToDisplayString()

                    d = "System.Collections.Generic.List<T>"
                    || d = "System.Collections.Generic.IList<T>")
                && shapeOpen m
                // no interface member it implicitly implements
                && not (
                    m.ContainingType.AllInterfaces
                    |> Seq.exists (fun i ->
                        i.GetMembers()
                        |> Seq.exists (fun im ->
                            SymbolEqualityComparer.Default.Equals(
                                m.ContainingType.FindImplementationForInterfaceMember im,
                                m
                            )))
                )
                ->
                // every reference: the host's, else this compilation's trees
                let references: (SemanticModel * SyntaxTree * SyntaxNode) list option =
                    match RuleContext.referencesOf ctx m with
                    | Some sites ->
                        if sites |> List.forall (fun s -> s.Editable && not (isNull s.Node)) then
                            Some(sites |> List.map (fun s -> s.Model, s.Tree, s.Node))
                        else
                            None
                    | None ->
                        model.Compilation.SyntaxTrees
                        |> Seq.collect (fun t ->
                            let tm =
                                if t = tree then
                                    model
                                else
                                    model.Compilation.GetSemanticModel t

                            t.GetRoot().DescendantNodes()
                            |> Seq.choose (fun x ->
                                match x with
                                | :? IdentifierNameSyntax as id when id.Identifier.ValueText = m.Name ->
                                    match tm.GetSymbolInfo(id).Symbol with
                                    | :? IMethodSymbol as s when
                                        SymbolEqualityComparer.Default.Equals(
                                            s.OriginalDefinition,
                                            m.OriginalDefinition
                                        )
                                        ->
                                        Some(tm, t, (id :> SyntaxNode))
                                    | _ -> None
                                | _ -> None))
                        |> List.ofSeq
                        |> Some

                // each reference a call whose value is only read
                let callRead (tm: SemanticModel, node: SyntaxNode) =
                    let callee =
                        match node.Parent with
                        | :? MemberAccessExpressionSyntax as ma when ma.Name = (node :?> SimpleNameSyntax) ->
                            ma :> SyntaxNode
                        | _ -> node

                    match callee.Parent with
                    | :? InvocationExpressionSyntax as inv when inv.Expression = (callee :?> ExpressionSyntax) ->
                        readOnly tm 0 inv
                    | _ -> false

                match references with
                | Some refs when
                    not refs.IsEmpty
                    && refs |> List.forall (fun (tm, _, node) -> callRead (tm, node))
                    ->
                    let elementText =
                        match md.ReturnType with
                        | :? GenericNameSyntax as g when g.TypeArgumentList.Arguments.Count = 1 ->
                            Some(g.TypeArgumentList.Arguments.[0].ToString())
                        | :? QualifiedNameSyntax as q ->
                            match q.Right with
                            | :? GenericNameSyntax as g when g.TypeArgumentList.Arguments.Count = 1 ->
                                Some(g.TypeArgumentList.Arguments.[0].ToString())
                            | _ -> None
                        | _ -> None

                    match elementText with
                    | Some element ->
                        let edits = [ Suggestion.replace md.ReturnType.Span $"IReadOnlyList<{element}>" ]

                        let elsewhere =
                            refs
                            |> List.filter (fun (_, t, _) -> t <> tree)
                            |> List.distinctBy (fun (_, t, _) -> t.FilePath)
                            |> List.map (fun (tm, t, node) ->
                                {
                                    Tree = t
                                    Model = tm
                                    Node = node
                                    Editable = true
                                })

                        let binds =
                            if elsewhere.IsEmpty then
                                Guards.speculativeCheck model edits
                            else
                                Guards.speculativeCheckAcross model elsewhere edits

                        if binds then
                            Some
                                {
                                    Code = Code
                                    Message =
                                        "Every caller only reads the list: IReadOnlyList says the caller gets a view, not a collection to change"
                                    Span = md.Identifier.Span
                                    Fixes = [ Suggestion.fix "Return IReadOnlyList" Code edits ]
                                }
                        else
                            None
                    | None -> None
                | _ -> None
            | _ -> None
        | _ -> None)
    |> List.ofSeq
