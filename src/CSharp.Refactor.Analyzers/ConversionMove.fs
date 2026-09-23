/// CR0020 (performance, fix): an eager copy that a lazy stage or a
/// consumer follows moves or goes.
///
///     foreach (var x in xs.ToList()) Use(x);        →  foreach (var x in xs) Use(x);
///     xs.ToList().Any(p)  /  xs.ToArray().First()   →  xs.Any(p)  /  xs.First()
///     xs.ToList().Where(p)                          →  xs.Where(p).ToList()
///
/// The copy exists to keep enumeration and mutation apart — the snapshot
/// idiom `foreach (var x in list.ToList()) list.Remove(x)` — so the rule
/// refuses outright where anything could mutate: the source is a local or
/// parameter (a field can be written by anything the body calls), the
/// body or lambda never names the source, never calls a mutating method
/// on ANY receiver (`Add`, `Remove`, `Clear`, `Insert`, `Push`, `Enqueue`,
/// `Set…`, `RemoveAt`…), and holds no `await`; the source is not captured
/// by a lambda or handed to a call anywhere in the member (owned). The
/// copy must be `Enumerable.ToList`/`ToArray` (never `Queryable`'s, which
/// runs the query); a source already a list or array gains nothing and is
/// CA1829/CA1860's. A consumer that short-circuits (`Any`, `First`,
/// `Contains`) sees fewer elements after the move, so the source must be
/// typed as a materialised collection, or be a local whose initializer is
/// a core-only view of one (`var seq = Numbers(); seq.ToList().Any(p)` is
/// a generator whose remaining effects the copy ran); likewise a `foreach` body that can leave early (`break`,
/// `return`, `throw`) keeps its copy over a source that is not a collection. A lazy stage that moves before the copy runs its
/// lambda at copy time instead of at enumeration time: the lambda must be
/// pure. Each moved pair is measured in PerfClaims.
module CSharp.Refactor.ConversionMove

open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax
open Microsoft.CodeAnalysis.Text

[<Literal>]
let Code = "CR0020"

let private copies = set [ "ToList"; "ToArray" ]

/// Stages that shrink or consume, and read the same elements in the same
/// order whether the copy sits before or after them.
let private movableStages =
    set
        [
            "Where"
            // `Select` is absent: `seq.Select(f).ToList()` measured 3.4× slower than
            // `seq.ToList().Select(f)` (PerfClaims), a list-backed Select being the fast path
            "OfType"
            "Take"
            "TakeWhile"
            "Skip"
            "SkipWhile"
            "Distinct"
            "Any"
            "All"
            "Count"
            "LongCount"
            "First"
            "FirstOrDefault"
            "Last"
            "LastOrDefault"
            "Single"
            "SingleOrDefault"
            "Contains"
            "Sum"
            "Min"
            "Max"
            "Average"
            "Aggregate"
            "ToDictionary"
            "ToHashSet"
            "ToLookup"
        ]

let private mutators =
    set
        [
            "Add"
            "AddRange"
            "Insert"
            "InsertRange"
            "Remove"
            "RemoveAt"
            "RemoveAll"
            "RemoveRange"
            "Clear"
            "Sort"
            "Reverse"
            "Push"
            "Pop"
            "Enqueue"
            "Dequeue"
            "TryAdd"
            "TryRemove"
            "SetValue"
            "CopyTo"
            "Resize"
        ]

let private callsMutator (node: SyntaxNode) =
    node.DescendantNodesAndSelf()
    |> Seq.exists (fun n ->
        match n with
        | :? InvocationExpressionSyntax as inv ->
            let name = Linq.nameOf inv
            mutators.Contains name || name.StartsWith "Set"
        | :? AssignmentExpressionSyntax as a ->
            // an indexer or member set on anything
            a.Left :? ElementAccessExpressionSyntax
            || a.Left :? MemberAccessExpressionSyntax
        | :? AwaitExpressionSyntax -> true
        | _ -> false)

/// The source is a local or parameter that nothing else in the member
/// captures, hands over, or reassigns: what the body cannot reach, the
/// body cannot mutate.
let private owned (model: SemanticModel) (source: ExpressionSyntax) =
    match source with
    | :? IdentifierNameSyntax as id ->
        match model.GetSymbolInfo(id).Symbol with
        | :? ILocalSymbol
        | :? IParameterSymbol as s ->
            let scope = Text.enclosingMember id

            scope.DescendantNodes()
            |> Seq.forall (fun n ->
                match n with
                | :? IdentifierNameSyntax as other when
                    other.Identifier.ValueText = id.Identifier.ValueText
                    && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(other).Symbol, s)
                    ->
                    match other.Parent with
                    | :? MemberAccessExpressionSyntax -> true // a call or member read on it
                    | :? ElementAccessExpressionSyntax as e -> e.Expression.Span = other.Span
                    | :? ForEachStatementSyntax -> true
                    | :? EqualsValueClauseSyntax when
                        (other.Parent.Parent :? VariableDeclaratorSyntax)
                        && (other.Parent.Parent.Parent.Parent :? LocalDeclarationStatementSyntax)
                        ->
                        // `var alias = xs;` hands it over
                        false
                    | :? ArgumentSyntax -> false
                    | :? AssignmentExpressionSyntax
                    | :? ReturnStatementSyntax
                    | :? ArrowExpressionClauseSyntax
                    | :? LambdaExpressionSyntax -> false
                    | _ ->
                        not (
                            other.Ancestors()
                            |> Seq.exists (fun a -> a :? AnonymousFunctionExpressionSyntax)
                        )
                | _ -> true)
        | _ -> false
    | _ -> false

/// A body that can leave the loop before the source is walked through: over
/// a lazy source the copy walked it all, and a generator with effects
/// would run fewer of them.
let private exitsEarly (body: StatementSyntax) =
    body.DescendantNodesAndSelf()
    |> Seq.exists (fun n ->
        match n with
        | :? BreakStatementSyntax as b ->
            // a break of this loop, not of a nested one or a switch
            b.Ancestors()
            |> Seq.takeWhile (fun a -> not (obj.ReferenceEquals(a, body.Parent)))
            |> Seq.forall (fun a ->
                not (
                    a :? ForEachStatementSyntax
                    || a :? ForStatementSyntax
                    || a :? WhileStatementSyntax
                    || a :? DoStatementSyntax
                    || a :? SwitchStatementSyntax
                ))
        | :? ReturnStatementSyntax
        | :? ThrowStatementSyntax
        | :? GotoStatementSyntax -> true
        | _ -> false)

/// Does enumerating the expression run nothing but core code over
/// materialised data: it calls core members only, and every local or
/// parameter it reads is typed as a collection, or is a local whose
/// initializer is itself such an expression (a local is never reassigned
/// here: the source is `owned`).
let rec private materialised (model: SemanticModel) (depth: int) (e: ExpressionSyntax) =
    depth > 0
    && Guards.callsOnlyCore model e
    && e.DescendantNodesAndSelf()
       |> Seq.forall (fun n ->
           match n with
           | :? IdentifierNameSyntax as id ->
               match model.GetSymbolInfo(id).Symbol with
               | :? IParameterSymbol as p when
                   // a lambda's own parameter is an element, not a source
                   (match p.ContainingSymbol with
                    | :? IMethodSymbol as m -> m.MethodKind <> MethodKind.AnonymousFunction
                    | _ -> true)
                   ->
                   Linq.isCollection p.Type
               | :? ILocalSymbol as l ->
                   Linq.isCollection l.Type
                   || (match l.DeclaringSyntaxReferences |> Seq.tryHead with
                       | Some r ->
                           match r.GetSyntax() with
                           | :? VariableDeclaratorSyntax as d when not (isNull d.Initializer) ->
                               materialised model (depth - 1) d.Initializer.Value
                           | _ -> false
                       | None -> false)
               | _ -> true
           | _ -> true)

let private isCopy (model: SemanticModel) (inv: InvocationExpressionSyntax) =
    copies.Contains(Linq.nameOf inv)
    && inv.ArgumentList.Arguments.Count = 0
    && (Linq.enumerableCall model inv).IsSome

/// A copy whose source is not already of the copied kind.
let private movableCopy (model: SemanticModel) (inv: InvocationExpressionSyntax) =
    if not (isCopy model inv) then
        ValueNone
    else
        match Linq.receiverOf inv with
        | Some source ->
            let t = model.GetTypeInfo(source).Type

            let alreadyThatKind =
                match t with
                | null -> true
                | :? IArrayTypeSymbol -> true
                | t -> t.OriginalDefinition.ToDisplayString() = "System.Collections.Generic.List<T>"

            // a query's copy runs the query and closes its reader: without it the
            // loop body runs over an open reader (a second command on the same
            // connection throws without MARS) and a moved stage changes what the
            // provider translates
            let query =
                match t with
                | null -> false
                | t ->
                    let isQueryable (i: ITypeSymbol) =
                        i.OriginalDefinition.ToDisplayString() = "System.Linq.IQueryable"

                    isQueryable t || t.AllInterfaces |> Seq.exists isQueryable

            if alreadyThatKind || query || not (owned model source) then
                ValueNone
            else
                ValueSome source
        | None -> ValueNone

let analyze (tree: SyntaxTree) (model: SemanticModel) (_ctx: RuleContext) : Suggestion list =
    let text = tree.GetText()

    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun node ->
        match node with
        // foreach over a copy: the copy goes
        | :? ForEachStatementSyntax as f ->
            match f.Expression with
            | :? InvocationExpressionSyntax as inv ->
                match movableCopy model inv with
                | ValueSome source when
                    not (Text.mentionsName (source.ToString()) f.Statement)
                    && not (callsMutator f.Statement)
                    && not (Text.holdsCommentOrDirective inv)
                    && (Linq.isCollection (model.GetTypeInfo(source).Type)
                        || not (exitsEarly f.Statement))
                    ->
                    let edit = Suggestion.replace inv.Span (source.ToString())

                    Some
                        {
                            Code = Code
                            Message =
                                "The copy is enumerated once and nothing can change the source: enumerate it directly"
                            Span = TextSpan.FromBounds(source.Span.End, inv.Span.End)
                            Fixes = [ Suggestion.fix "Drop the copy" Code [ edit ] ]
                        }
                | _ -> None
            | _ -> None
        // a copy followed by a stage
        | :? InvocationExpressionSyntax as stage when movableStages.Contains(Linq.nameOf stage) ->
            match stage.Expression with
            | :? MemberAccessExpressionSyntax as m ->
                match m.Expression with
                | :? InvocationExpressionSyntax as copy when (Linq.enumerableCall model stage).IsSome ->
                    match movableCopy model copy with
                    | ValueSome source ->
                        let stageName = Linq.nameOf stage
                        let lambdaOk = Guards.callsOnlyCore model stage.ArgumentList
                        let consumes = Linq.consumers.Contains stageName

                        let sourcePure =
                            // a short-circuiting consumer sees fewer elements after the move:
                            // the source must be a materialised collection, or a local whose
                            // initializer is (a pure view of) one — `var seq = Numbers();`
                            // is a generator whose remaining effects the copy used to run
                            (not consumes) || materialised model 3 source

                        if
                            not lambdaOk
                            || not sourcePure
                            || Text.mentionsName (source.ToString()) stage.ArgumentList
                            || callsMutator stage.ArgumentList
                            || Text.holdsCommentOrDirective stage
                        then
                            None
                        else
                            // drop the copy; a lazy stage keeps the copy after it
                            let copySpan = TextSpan.FromBounds(source.Span.End, copy.Span.End)

                            let edits =
                                if consumes then
                                    [ Suggestion.replace copySpan "" ]
                                else
                                    [
                                        Suggestion.replace copySpan ""
                                        Suggestion.insert stage.Span.End ("." + Linq.nameOf copy + "()")
                                    ]

                            if Guards.speculativeCheck model edits then
                                Some
                                    {
                                        Code = Code
                                        Message =
                                            (if consumes then
                                                 $"'{Linq.nameOf copy}()' copies everything for '{stageName}' to walk once: walk the source"
                                             else
                                                 $"'{Linq.nameOf copy}()' copies everything before '{stageName}' shrinks it: copy after")
                                        Span = copySpan
                                        Fixes = [ Suggestion.fix "Move the copy" Code edits ]
                                    }
                            else
                                None
                    | ValueNone -> None
                | _ -> None
            | _ -> None
        | _ -> None)
    |> List.ofSeq
