/// The async rules that change a signature.
///
/// CR0041 (correctness, fix): a sync method draining a task at its boundary
/// — `return Foo().Result;`, `t.Wait();` — whose every caller sits in an
/// `async` context becomes `async Task<T>`, its drains `await`, its
/// callers `await`ed. Guards (the F# taskify's, in the editor's strict
/// form): the method is private (internal under the friend check, public
/// never); every caller is in this file, is a bindable statement of an
/// `async` body — `var x = M(…);`, `return M(…);`, `M(…);` — and not
/// inside a lambda, a local function, a `lock`, a `catch` or a `finally`,
/// nor inside a `try` whose handler catches the `AggregateException` the
/// sync call threw (CR0040's handler test: the awaited call throws the
/// inner exception, and the handler would go dead);
/// every drain in the body is one CR0040 could await (no known-complete
/// read, no `AggregateException` handler, no no-bind zone, no
/// thread-choreographed body); no `out`/`ref` parameters, no iterator, no
/// `unsafe`, no override or interface implementation, no recursion, no
/// `ref struct` local, no mention as a method group anywhere (a delegate
/// built from it would silently carry a Task); the `Async` suffix is added when the name lacks
/// it and no member of the type already carries it
/// (`csharp_refactor.CR0041.async_suffix = false` keeps the name); one
/// unconvertible caller vetoes everything; the speculative check re-binds
/// the file.
///
/// CR0043 (correctness, fix): `async void M()` that is not an event
/// handler swallows nothing — an exception in it crashes the process, and
/// no caller can await it. It becomes `async Task M()`, and its callers
/// in `async` contexts gain `await`. Guards: not `(object, EventArgs)`-
/// shaped, not subscribed to any event in the compilation (`+=`, `-=`, a
/// delegate constructor) or mentioned as a method group, not an override,
/// virtual, abstract, partial or interface implementation, no attribute (a handler-style attribute would hand the runtime a Task it
/// does not await); at least one caller, every caller in this file and in
/// an `async` context — a call site in a sync context would become a
/// silent fire-and-forget, and a method nothing here calls is called by
/// the framework, reflection or another assembly, which would get a Task
/// nobody awaits, so either vetoes the fix and the rule notes instead;
/// the scope gate (`async void` → `async Task` is a signature change: a
/// public or protected method needs the public shape open, an internal one
/// the friend check).
/// Yields to VSTHRD100.
///
/// CR0045 (performance, fix): a test that blocks on a task —
/// `[Fact] public void T() { var r = Load().Result; … }`,
/// `Assert.Throws<E>(() => t.Wait())`, `Task.WaitAll(a, b)` — becomes
/// `public async Task T()`, the drains `await`,
/// `await Assert.ThrowsAsync<E>(() => t)`, `await Task.WhenAll(a, b)`.
/// Guards: the test attribute's declaring assembly is xUnit, NUnit,
/// MSTest or TUnit (a home-grown attribute with a reflection runner would
/// get a Task nobody awaits); the method is `void` or a non-`async`
/// `Task`; every drain is bindable (CR0040's proofs); a test whose last
/// expression is a value (NUnit's `ExpectedResult`) stays; shared state
/// vetoes — a test that writes a `static` of another type, or a
/// process-global (`Environment.CurrentDirectory`,
/// `Environment.SetEnvironmentVariable`, `CurrentCulture`), keeps its
/// blocking shape since an awaited test may overlap another; a file that
/// installs global state by reflection (`BindingFlags` in the file)
/// converts no test; `Assert.Throws<AggregateException>(() => t.Wait())`
/// asserts the wrapper and stays as written.
module CSharp.Refactor.AsyncSignatures

open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax
open Microsoft.CodeAnalysis.Text
open System.Collections.Generic

[<Literal>]
let TaskifyCode = "CR0041"

[<Literal>]
let AsyncVoidCode = "CR0043"

[<Literal>]
let TestTaskCode = "CR0045"

let private hasModifier (mods: SyntaxTokenList) (kind: SyntaxKind) =
    mods |> Seq.exists (fun t -> t.IsKind kind)

/// Per tree, once: the nodes the reference scans below bind, under every
/// identifier name each spells, in document order. A reference to a method
/// binds through an identifier spelling its name - `M`, `x.M`, `M<T>`,
/// `?.M`, `@M` - so a node spelling no identifier of that name cannot refer
/// to it: each scan binds the nodes under the method's own name only, where
/// it walked every node of every tree and bound most of them, once per
/// method (a large file paid minutes for CR0043).
type private NameIndex =
    {
        /// Invocations, by the names their callee expression spells.
        Invocations: Dictionary<string, InvocationExpressionSyntax[]>
        /// What a subscription or a delegate construction binds - the right
        /// side of `+=`/`-=`, the argument of a one-argument `new`, a name or
        /// member access handed as an argument - by the names each spells.
        Subscriptions: Dictionary<string, ExpressionSyntax[]>
        /// Every simple name, by its own name.
        Names: Dictionary<string, SimpleNameSyntax[]>
    }

let private nameIndexes =
    System.Runtime.CompilerServices.ConditionalWeakTable<SyntaxTree, NameIndex>()

let private nameIndexOf (tree: SyntaxTree) =
    nameIndexes.GetValue(
        tree,
        fun tree ->
            let invocations = Dictionary<string, ResizeArray<InvocationExpressionSyntax>>()

            let subscriptions = Dictionary<string, ResizeArray<ExpressionSyntax>>()

            let names = Dictionary<string, ResizeArray<SimpleNameSyntax>>()

            let add (d: Dictionary<string, ResizeArray<'T>>) (name: string) (item: 'T) =
                match d.TryGetValue name with
                | true, l -> l.Add item
                | false, _ -> d.[name] <- ResizeArray [ item ]

            let spelled (node: SyntaxNode) =
                node.DescendantTokens()
                |> Seq.filter (fun t -> t.IsKind SyntaxKind.IdentifierToken)
                |> Seq.map (fun t -> t.ValueText)
                |> Seq.distinct

            for n in tree.GetRoot().DescendantNodes() do
                match n with
                | :? InvocationExpressionSyntax as inv ->
                    for name in spelled inv.Expression do
                        add invocations name inv
                | _ -> ()

                let subscription: ExpressionSyntax option =
                    match n with
                    | :? AssignmentExpressionSyntax as a when
                        a.IsKind SyntaxKind.AddAssignmentExpression
                        || a.IsKind SyntaxKind.SubtractAssignmentExpression
                        ->
                        Some a.Right
                    | :? ObjectCreationExpressionSyntax as c when
                        not (isNull c.ArgumentList) && c.ArgumentList.Arguments.Count = 1
                        ->
                        Some c.ArgumentList.Arguments.[0].Expression
                    | :? ArgumentSyntax as arg ->
                        match arg.Expression with
                        | :? IdentifierNameSyntax
                        | :? MemberAccessExpressionSyntax -> Some arg.Expression
                        | _ -> None
                    | _ -> None

                match subscription with
                | Some e ->
                    for name in spelled e do
                        add subscriptions name e
                | None -> ()

                match n with
                | :? SimpleNameSyntax as id -> add names id.Identifier.ValueText id
                | _ -> ()

            let frozen (d: Dictionary<string, ResizeArray<'T>>) =
                let result = Dictionary<string, 'T[]>()

                for KeyValue(name, l) in d do
                    result.[name] <- l.ToArray()

                result

            {
                Invocations = frozen invocations
                Subscriptions = frozen subscriptions
                Names = frozen names
            }
    )

let private lookup (d: Dictionary<string, 'T[]>) (name: string) =
    match d.TryGetValue name with
    | true, found -> found
    | false, _ -> [||]

/// The call sites of a method symbol in this tree, each with its statement
/// context and the function it sits in.
let private callSites (model: SemanticModel) (tree: SyntaxTree) (target: IMethodSymbol) =
    lookup (nameIndexOf tree).Invocations target.Name
    |> Seq.choose (fun inv ->
        match model.GetSymbolInfo(inv).Symbol with
        | :? IMethodSymbol as m when
            SymbolEqualityComparer.Default.Equals(m.OriginalDefinition, target.OriginalDefinition)
            ->
            Some inv
        | _ -> None)
    |> List.ofSeq

/// Is a call a bindable statement of an async body: `var x = M();`,
/// `return M();`, `M();`, `x = M();` — directly, not under a lambda,
/// local function, lock, catch or finally?
let private bindableCall (inv: InvocationExpressionSyntax) : bool =
    let directParent =
        match inv.Parent with
        | :? ExpressionStatementSyntax -> true
        | :? ReturnStatementSyntax -> true
        | :? ArrowExpressionClauseSyntax -> true
        | :? EqualsValueClauseSyntax as e -> e.Parent :? VariableDeclaratorSyntax
        | :? AssignmentExpressionSyntax as a -> a.Right.Span = inv.Span && (a.Parent :? ExpressionStatementSyntax)
        | _ -> false

    match AsyncShapes.enclosingFunction inv with
    | Some f when f.IsAsync ->
        directParent
        && not (
            inv.Ancestors()
            |> Seq.takeWhile (fun a -> not (obj.ReferenceEquals(a, f.Node)))
            |> Seq.exists (fun a ->
                a :? LockStatementSyntax
                || a :? CatchClauseSyntax
                || a :? FinallyClauseSyntax
                || a :? AnonymousFunctionExpressionSyntax
                || a :? LocalFunctionStatementSyntax)
        )
        // the caller's own handler of the wrapper: the sync call threw the
        // AggregateException, the awaited one throws the inner exception
        && not (AsyncShapes.underAggregateCatch f inv)
    | _ -> false

/// All references to a method symbol across the compilation are in this tree.
let private allReferencesHere (model: SemanticModel) (tree: SyntaxTree) (target: IMethodSymbol) =
    model.Compilation.SyntaxTrees
    |> Seq.filter (fun t -> t <> tree)
    |> Seq.forall (fun t ->
        let m = model.Compilation.GetSemanticModel t

        t.GetRoot().DescendantNodes()
        |> Seq.forall (fun n ->
            match n with
            | :? IdentifierNameSyntax as id when id.Identifier.ValueText = target.Name ->
                not (SymbolEqualityComparer.Default.Equals(m.GetSymbolInfo(id).Symbol, target))
            | _ -> true))

let private isEventShaped (m: IMethodSymbol) =
    m.Parameters.Length = 2
    && m.Parameters.[0].Type.SpecialType = SpecialType.System_Object
    && m.Parameters.[1].Type.ToDisplayString().EndsWith "EventArgs"

/// Is the method subscribed to any event, or wrapped in a delegate, anywhere?
let private subscribed (model: SemanticModel) (target: IMethodSymbol) =
    // the right side of `+=`/`-=`, the argument of a one-argument `new`, a
    // method group handed to anything (a delegate somewhere): the index holds
    // them under the names they spell
    model.Compilation.SyntaxTrees
    |> Seq.exists (fun t ->
        match lookup (nameIndexOf t).Subscriptions target.Name with
        | [||] -> false
        | candidates ->
            let m = model.Compilation.GetSemanticModel t

            candidates
            |> Array.exists (fun e ->
                let info = m.GetSymbolInfo e

                Seq.append (Option.toList (Option.ofObj info.Symbol)) info.CandidateSymbols
                |> Seq.exists (fun s -> SymbolEqualityComparer.Default.Equals(s, target))))

/// Is the method named anywhere other than as the callee of an invocation —
/// a method group handed on, a delegate built from it, `nameof`? A changed
/// signature would silently change what such a mention means.
let private mentionedAsGroup (model: SemanticModel) (target: IMethodSymbol) =
    model.Compilation.SyntaxTrees
    |> Seq.exists (fun t ->
        let m = model.Compilation.GetSemanticModel t

        lookup (nameIndexOf t).Names target.Name
        |> Seq.exists (fun id ->
            let info = m.GetSymbolInfo id

            let refers =
                Seq.append (Option.toList (Option.ofObj info.Symbol)) info.CandidateSymbols
                |> Seq.exists (fun s ->
                    SymbolEqualityComparer.Default.Equals(s.OriginalDefinition, target.OriginalDefinition))

            refers
            && (let callee =
                    match id.Parent with
                    | :? InvocationExpressionSyntax as inv -> inv.Expression.Span = id.Span
                    | :? MemberAccessExpressionSyntax as ma when ma.Name.Span = id.Span ->
                        match ma.Parent with
                        | :? InvocationExpressionSyntax as inv -> inv.Expression.Span = ma.Span
                        | _ -> false
                    | _ -> false

                not callee)))

let private returnTypeText (m: MethodDeclarationSyntax) (model: SemanticModel) =
    let t = model.GetTypeInfo(m.ReturnType).Type

    if isNull t || t.SpecialType = SpecialType.System_Void then
        "Task"
    else
        "Task<" + m.ReturnType.ToString() + ">"

// ---- CR0041 ----

let private taskify (tree: SyntaxTree) (model: SemanticModel) (ctx: RuleContext) : Suggestion list =
    let addSuffix = RuleContext.knobBool ctx TaskifyCode "async_suffix" true

    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun n ->
        match n with
        | :? MethodDeclarationSyntax as m when
            not (hasModifier m.Modifiers SyntaxKind.AsyncKeyword)
            && not (isNull m.Body)
            && not (hasModifier m.Modifiers SyntaxKind.OverrideKeyword)
            && not (hasModifier m.Modifiers SyntaxKind.VirtualKeyword)
            && not (hasModifier m.Modifiers SyntaxKind.AbstractKeyword)
            && not (hasModifier m.Modifiers SyntaxKind.UnsafeKeyword)
            && m.ParameterList.Parameters
               |> Seq.forall (fun p ->
                   p.Modifiers
                   |> Seq.forall (fun t -> not (t.IsKind SyntaxKind.OutKeyword || t.IsKind SyntaxKind.RefKeyword)))
            && not (m.Body.DescendantNodes() |> Seq.exists (fun x -> x :? YieldStatementSyntax))
            ->
            match model.GetDeclaredSymbol m with
            | null -> None
            | self ->
                let accessible =
                    match self.DeclaredAccessibility with
                    | Accessibility.Private -> true
                    | Accessibility.Internal -> RuleContext.internalShapeOpen ctx
                    | _ -> false

                let implementsInterface =
                    self.ContainingType.AllInterfaces
                    |> Seq.exists (fun i ->
                        i.GetMembers()
                        |> Seq.exists (fun im ->
                            let impl = self.ContainingType.FindImplementationForInterfaceMember im
                            not (isNull impl) && SymbolEqualityComparer.Default.Equals(impl, self)))

                let fn =
                    {
                        AsyncShapes.Function.Node = m
                        AsyncShapes.Function.IsAsync = false
                        AsyncShapes.Function.Body = m.Body
                        AsyncShapes.Function.Parameters = List.ofSeq m.ParameterList.Parameters
                    }

                let drains = AsyncShapes.drains model m.Body

                // only drains that belong to this body, not a lambda inside it
                let own =
                    drains
                    |> List.filter (fun d ->
                        let site, _ = AsyncShapes.drainSite d

                        match AsyncShapes.enclosingFunction site with
                        | Some f -> obj.ReferenceEquals(f.Node, m)
                        | None -> false)

                if
                    not accessible
                    || implementsInterface
                    || own.IsEmpty
                    || own.Length <> drains.Length
                    || not (own |> List.forall (AsyncShapes.bindable model fn))
                then
                    None
                else
                    let recursive =
                        m.Body.DescendantNodes()
                        |> Seq.exists (fun x ->
                            match x with
                            | :? InvocationExpressionSyntax as inv ->
                                SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(inv).Symbol, self)
                            | _ -> false)

                    let refStructLocal =
                        m.Body.DescendantNodes()
                        |> Seq.exists (fun x ->
                            match x with
                            | :? VariableDeclaratorSyntax as v ->
                                match model.GetDeclaredSymbol v with
                                | :? ILocalSymbol as l -> l.Type.IsRefLikeType
                                | _ -> false
                            | _ -> false)

                    let callers = callSites model tree self

                    // callers in other files: through the host's oracle where it has
                    // one (every site an editable, bindable call), else there must
                    // be none — the fix rewrites every caller or none
                    let elsewhere: (ReferenceSite * InvocationExpressionSyntax) list option =
                        // without an oracle the compilation's other trees are still in
                        // reach: every reference there must be a call (a method group,
                        // a nameof, is not), each becoming a site of its own file
                        let compilationSites () =
                            model.Compilation.SyntaxTrees
                            |> Seq.filter (fun t -> t <> tree)
                            |> Seq.collect (fun t ->
                                let m = model.Compilation.GetSemanticModel t
                                let calls = callSites m t self

                                let mentions =
                                    lookup (nameIndexOf t).Names self.Name
                                    |> Seq.filter (fun n ->
                                        match n with
                                        | :? IdentifierNameSyntax as id ->
                                            SymbolEqualityComparer.Default.Equals(m.GetSymbolInfo(id).Symbol, self)
                                        | _ -> false)
                                    |> Seq.length

                                if mentions <> calls.Length then
                                    [
                                        {
                                            Tree = t
                                            Model = m
                                            Node = null
                                            Editable = false
                                        }
                                    ]
                                else
                                    calls
                                    |> List.map (fun inv ->
                                        let name: SyntaxNode =
                                            match inv.Expression with
                                            | :? MemberAccessExpressionSyntax as ma -> ma.Name :> SyntaxNode
                                            | e -> e :> SyntaxNode

                                        {
                                            Tree = t
                                            Model = m
                                            Node = name
                                            Editable = true
                                        }))
                            |> List.ofSeq

                        let sites =
                            match RuleContext.referencesOf ctx self with
                            | None -> compilationSites ()
                            | Some sites -> sites

                        let calls =
                            sites
                            |> List.map (fun site ->
                                if not site.Editable || isNull site.Node then
                                    None
                                else
                                    site.Node.AncestorsAndSelf()
                                    |> Seq.tryPick (fun a ->
                                        match a with
                                        | :? InvocationExpressionSyntax as inv when
                                            inv.Expression.Span.Contains site.Node.Span
                                            && SymbolEqualityComparer.Default.Equals(
                                                (match site.Model.GetSymbolInfo(inv).Symbol with
                                                 | :? IMethodSymbol as ms -> ms.OriginalDefinition
                                                 | _ -> null),
                                                self.OriginalDefinition
                                            )
                                            ->
                                            Some(site, inv)
                                        | _ -> None)
                                    |> Option.filter (fun (_, inv) -> bindableCall inv))

                        if calls |> List.forall Option.isSome then
                            Some(calls |> List.choose id)
                        else
                            None

                    if
                        recursive
                        || refStructLocal
                        || (callers.IsEmpty && (elsewhere |> Option.forall List.isEmpty))
                        || not (callers |> List.forall bindableCall)
                        || elsewhere.IsNone
                        || mentionedAsGroup model self
                    then
                        None
                    else
                        let elsewhere = Option.get elsewhere

                        let newName =
                            if addSuffix && not (m.Identifier.ValueText.EndsWith "Async") then
                                m.Identifier.ValueText + "Async"
                            else
                                m.Identifier.ValueText

                        let clash =
                            newName <> m.Identifier.ValueText
                            && not (self.ContainingType.GetMembers(newName) |> Seq.isEmpty)

                        if clash then
                            None
                        else
                            // the call gains `await` and the new name, in this file or another
                            let callEdits (file: string option) (calls: InvocationExpressionSyntax list) =
                                let insertAt =
                                    match file with
                                    | Some f -> Suggestion.insertIn f
                                    | None -> Suggestion.insert

                                let replaceAt =
                                    match file with
                                    | Some f -> Suggestion.replaceIn f
                                    | None -> Suggestion.replace

                                [
                                    for c in calls do
                                        insertAt c.SpanStart "await "

                                        if newName <> m.Identifier.ValueText then
                                            let nameToken =
                                                match c.Expression with
                                                | :? MemberAccessExpressionSyntax as ma -> ma.Name.Identifier
                                                | :? IdentifierNameSyntax as id -> id.Identifier
                                                | _ -> m.Identifier

                                            if nameToken.SpanStart <> m.Identifier.SpanStart || file.IsSome then
                                                replaceAt nameToken.Span newName
                                ]

                            let edits =
                                [
                                    Suggestion.replace m.ReturnType.Span ("async " + returnTypeText m model)
                                    if newName <> m.Identifier.ValueText then
                                        Suggestion.replace m.Identifier.Span newName
                                    yield! own |> List.map AsyncShapes.awaitEdit
                                    yield! callEdits None callers
                                    for site, inv in elsewhere do
                                        yield! callEdits (Some site.Tree.FilePath) [ inv ]
                                ]

                            // every touched file of this compilation re-binds in one fork:
                            // the renamed method and its awaited callers together
                            let binds =
                                if elsewhere.IsEmpty then
                                    Guards.speculativeCheck model edits
                                else
                                    Guards.speculativeCheckAcross model (elsewhere |> List.map fst) edits

                            if binds then
                                Some
                                    {
                                        Code = TaskifyCode
                                        Message =
                                            $"'{m.Identifier.ValueText}' blocks on a task and every caller is async: make it async and await it ({callers.Length + elsewhere.Length} caller(s))"
                                        Span = m.Identifier.Span
                                        Fixes = [ Suggestion.fix "Make it async" TaskifyCode edits ]
                                    }
                            else
                                None
        | _ -> None)
    |> List.ofSeq

// ---- CR0043 ----

let private asyncVoids (tree: SyntaxTree) (model: SemanticModel) (ctx: RuleContext) : Suggestion list =
    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun n ->
        match n with
        | :? MethodDeclarationSyntax as m when
            hasModifier m.Modifiers SyntaxKind.AsyncKeyword
            && (match m.ReturnType with
                | :? PredefinedTypeSyntax as p -> p.Keyword.IsKind SyntaxKind.VoidKeyword
                | _ -> false)
            && m.AttributeLists.Count = 0
            && not (hasModifier m.Modifiers SyntaxKind.OverrideKeyword)
            && not (hasModifier m.Modifiers SyntaxKind.VirtualKeyword)
            && not (hasModifier m.Modifiers SyntaxKind.AbstractKeyword)
            && not (hasModifier m.Modifiers SyntaxKind.PartialKeyword)
            ->
            match model.GetDeclaredSymbol m with
            | null -> None
            | self when isEventShaped self || subscribed model self -> None
            | self ->
                let implementsInterface =
                    self.ContainingType.AllInterfaces
                    |> Seq.exists (fun i ->
                        i.GetMembers()
                        |> Seq.exists (fun im ->
                            let impl = self.ContainingType.FindImplementationForInterfaceMember im
                            not (isNull impl) && SymbolEqualityComparer.Default.Equals(impl, self)))

                if implementsInterface then
                    None
                else
                    let callers = callSites model tree self

                    let rec effective (s: ISymbol) =
                        match s with
                        | null -> Accessibility.Public
                        | s ->
                            match s.DeclaredAccessibility, effective s.ContainingType with
                            | Accessibility.Private, _
                            | _, Accessibility.Private -> Accessibility.Private
                            | Accessibility.Internal, _
                            | Accessibility.ProtectedAndInternal, _
                            | _, Accessibility.Internal -> Accessibility.Internal
                            | own, _ -> own

                    let shapeOpen =
                        match effective self with
                        | Accessibility.Private -> true
                        | Accessibility.Internal -> RuleContext.internalShapeOpen ctx
                        | _ -> RuleContext.publicShapeOpen ctx

                    let convertible =
                        shapeOpen
                        && not callers.IsEmpty
                        && allReferencesHere model tree self
                        && not (mentionedAsGroup model self)
                        && callers
                           |> List.forall (fun c ->
                               match AsyncShapes.enclosingFunction c with
                               | Some f -> f.IsAsync && (c.Parent :? ExpressionStatementSyntax)
                               | None -> false)

                    if convertible then
                        let edits =
                            Suggestion.replace m.ReturnType.Span "Task"
                            :: (callers |> List.map (fun c -> Suggestion.insert c.SpanStart "await "))

                        if Guards.speculativeCheck model edits then
                            Some
                                {
                                    Code = AsyncVoidCode
                                    Message =
                                        "async void: no caller can await it and an exception in it crashes the process; return a Task"
                                    Span = m.ReturnType.Span
                                    Fixes = [ Suggestion.fix "Return a Task" AsyncVoidCode edits ]
                                }
                        else
                            None
                    else
                        Some(
                            Suggestion.note
                                AsyncVoidCode
                                "async void: no caller can await it and an exception in it crashes the process; a caller in a sync context or another file, no caller in sight (framework, reflection, another assembly) or a closed public surface keeps this a note"
                                m.ReturnType.Span
                        )
        | _ -> None)
    |> List.ofSeq

// ---- CR0045 ----

let private testFrameworks =
    [
        "xunit"
        "nunit.framework"
        "Microsoft.VisualStudio.TestPlatform.TestFramework"
        "TUnit"
    ]

let private isTestMethod (model: SemanticModel) (m: MethodDeclarationSyntax) =
    m.AttributeLists
    |> Seq.collect (fun l -> l.Attributes)
    |> Seq.exists (fun a ->
        match model.GetSymbolInfo(a).Symbol with
        | :? IMethodSymbol as ctor ->
            let asm = ctor.ContainingAssembly.Name

            testFrameworks |> List.exists asm.StartsWith
        | _ -> false)

let private globalSetters =
    [
        "Environment.CurrentDirectory"
        "Environment.SetEnvironmentVariable"
        "CurrentCulture"
        "CurrentUICulture"
        "Console.SetOut"
        "Console.SetIn"
    ]

/// Writes to state that outlives the test: a static of another type, a
/// process-global.
let private touchesSharedState (model: SemanticModel) (m: MethodDeclarationSyntax) =
    let own = (model.GetDeclaredSymbol m).ContainingType

    m.Body.DescendantNodes()
    |> Seq.exists (fun n ->
        match n with
        | :? AssignmentExpressionSyntax as a ->
            let text = a.Left.ToString()

            globalSetters |> List.exists text.Contains
            || (match model.GetSymbolInfo(a.Left).Symbol with
                | :? IFieldSymbol as f ->
                    f.IsStatic && not (SymbolEqualityComparer.Default.Equals(f.ContainingType, own))
                | :? IPropertySymbol as p ->
                    p.IsStatic && not (SymbolEqualityComparer.Default.Equals(p.ContainingType, own))
                | null -> a.Left :? MemberAccessExpressionSyntax // unresolvable target counts as shared
                | _ -> false)
        | :? InvocationExpressionSyntax as inv ->
            let text = inv.Expression.ToString()
            globalSetters |> List.exists text.Contains
        | _ -> false)

let private testTasks (tree: SyntaxTree) (model: SemanticModel) : Suggestion list =
    let text = tree.GetText()
    let reflectionInFile = tree.GetRoot().ToString().Contains "BindingFlags"

    if reflectionInFile then
        []
    else
        tree.GetRoot().DescendantNodes()
        |> Seq.choose (fun n ->
            match n with
            | :? MethodDeclarationSyntax as m when
                not (isNull m.Body)
                && not (hasModifier m.Modifiers SyntaxKind.AsyncKeyword)
                && isTestMethod model m
                ->
                let returnsVoid =
                    match m.ReturnType with
                    | :? PredefinedTypeSyntax as p -> p.Keyword.IsKind SyntaxKind.VoidKeyword
                    | _ -> false

                let returnsTask = AsyncShapes.isTaskLike (model.GetTypeInfo(m.ReturnType).Type)

                if not (returnsVoid || returnsTask) then
                    None // a value-returning test: its result has no bind shape
                else
                    let fn =
                        {
                            AsyncShapes.Function.Node = m
                            AsyncShapes.Function.IsAsync = false
                            AsyncShapes.Function.Body = m.Body
                            AsyncShapes.Function.Parameters = List.ofSeq m.ParameterList.Parameters
                        }

                    let drains = AsyncShapes.drains model m.Body

                    // `Assert.Throws<E>(() => t.Wait())` / `(() => t.Result)`: the async assert
                    let throwsAsserts =
                        m.Body.DescendantNodes()
                        |> Seq.choose (fun x ->
                            match x with
                            | :? InvocationExpressionSyntax as inv when
                                (inv.Expression.ToString().StartsWith "Assert.Throws<"
                                 || inv.Expression.ToString().StartsWith "Assert.Throws(")
                                && not (inv.Expression.ToString().Contains "AggregateException")
                                && inv.ArgumentList.Arguments.Count = 1
                                ->
                                match inv.ArgumentList.Arguments.[0].Expression with
                                | :? ParenthesizedLambdaExpressionSyntax as l when not (isNull l.ExpressionBody) ->
                                    let body = l.ExpressionBody.ToString()

                                    if body.EndsWith ".Wait()" then
                                        Some(inv, l.ExpressionBody, body.Substring(0, body.Length - ".Wait()".Length))
                                    elif body.EndsWith ".Result" then
                                        Some(inv, l.ExpressionBody, body.Substring(0, body.Length - ".Result".Length))
                                    else
                                        None
                                | _ -> None
                            | _ -> None)
                        |> List.ofSeq

                    // drains inside those asserts are handled by the assert rewrite
                    let ownDrains =
                        drains
                        |> List.filter (fun d ->
                            let site, _ = AsyncShapes.drainSite d

                            not (throwsAsserts |> List.exists (fun (inv, _, _) -> inv.Span.Contains site.Span))
                            && (match AsyncShapes.enclosingFunction site with
                                | Some f -> obj.ReferenceEquals(f.Node, m)
                                | None -> false))

                    let foreignDrains =
                        drains.Length
                        - ownDrains.Length
                        - (drains
                           |> List.filter (fun d ->
                               let site, _ = AsyncShapes.drainSite d
                               throwsAsserts |> List.exists (fun (inv, _, _) -> inv.Span.Contains site.Span))
                           |> List.length)

                    if
                        (ownDrains.IsEmpty && throwsAsserts.IsEmpty)
                        || foreignDrains > 0
                        || not (ownDrains |> List.forall (AsyncShapes.bindable model fn))
                        || touchesSharedState model m
                    then
                        None
                    else
                        let edits =
                            [
                                Suggestion.replace
                                    m.ReturnType.Span
                                    ("async " + (if returnsVoid then "Task" else m.ReturnType.ToString()))
                                yield! ownDrains |> List.map AsyncShapes.awaitEdit
                                for (inv, body, task) in throwsAsserts do
                                    let head = inv.Expression.ToString().Replace("Assert.Throws", "Assert.ThrowsAsync")
                                    Suggestion.replace inv.Span ($"await {head}(() => {task})")
                            ]

                        if Guards.speculativeCheck model edits then
                            Some
                                {
                                    Code = TestTaskCode
                                    Message =
                                        "A test blocking on a task holds a worker thread for its whole run: return a Task and await"
                                    Span = m.Identifier.Span
                                    Fixes = [ Suggestion.fix "Make the test async" TestTaskCode edits ]
                                }
                        else
                            None
            | _ -> None)
        |> List.ofSeq

let analyze (tree: SyntaxTree) (model: SemanticModel) (ctx: RuleContext) : Suggestion list =
    taskify tree model ctx @ asyncVoids tree model ctx @ testTasks tree model
