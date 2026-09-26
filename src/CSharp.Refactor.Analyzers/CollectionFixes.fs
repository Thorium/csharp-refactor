/// Four local collection rewrites.
///
/// CR0024 (performance, fix): `foreach (var x in xs) acc.Add(x);` is
/// `acc.AddRange(xs);` — one grow instead of one per element. Guards:
/// `Add` resolves to `List<T>.Add` (typed); the body is that one
/// statement, its argument the loop variable as-is (a projected body
/// measured no faster and reads worse); the receiver is the same list on
/// every iteration (a name or dotted read not mentioning the loop
/// variable); the source is not the list itself; the speculative check
/// settles the element conversion.
///
/// CR0031 (performance, fix): `new Random()` per call is
/// `Random.Shared` (.NET 6+): no allocation, no seeding, thread-safe.
/// Guards: parameterless (a seed is a decision), the type exactly
/// `System.Random`; either called on directly (`new Random().Next(…)`) or
/// bound to a local whose every use is a call receiver in its own block —
/// an instance stored, returned or passed is the author's; `Random.Shared`
/// resolves in the compilation.
///
/// CR0032 (performance, fix): `foreach (var k in d.Keys) use(k, d[k])`
/// looks every key up again; `foreach (var (k, v) in d)` reads the pair.
/// Guards: `d` typed `Dictionary<K,V>`/`IDictionary<K,V>`/
/// `IReadOnlyDictionary<K,V>` (never a concurrent one: its `Keys` is a
/// snapshot where its enumerator is live) and a pure read (name or dotted); the body
/// reads `d[k]` at least once and never writes `d[k]` or assigns `k`; the
/// value name is `value`, else `v`, unused in the enclosing member;
/// deconstruction needs C# 7 and `KeyValuePair<,>.Deconstruct` (.NET Core
/// 2.0+). The pair's value is the one the iteration began with, `d[k]` the
/// one the read finds, and since .NET Core 3.0 an overwrite or a `Remove`
/// no longer breaks the enumeration: the fix stands down only where a write
/// is positively detected — a receiver read through a computed property
/// whose visible getter constructs or counts, a read deferred into a
/// lambda, local function or query, a lambda of the member writing the
/// receiver, or something visibly writing `d` before a read in the same
/// iteration: the receiver named other than by a read or a read-only
/// member, an alias's or a key's state stored into, an `await`, or a call,
/// getter, setter, constructor or operator of the user's whose body —
/// visible in this compilation, followed three calls deep — touches a
/// dictionary (arguments run before their call: `Use(k, d[k])` converts;
/// in a loop nested around a read, all of that loop counts). A call whose
/// body cannot be seen (an interface's, a delegate's of unknown origin,
/// metadata) is taken not to write it: the accepted residual. A dictionary
/// no other code can reach (a local built by `new`/`ToDictionary` that
/// never escapes, a private readonly field so built and only ever read)
/// lets only its own mentions count. Otherwise a note.
///
/// CR0033 (performance, fix): `sb.Append(a + b + c)` builds the string
/// then copies it; `sb.Append(a).Append(b).Append(c)` copies once.
/// Guards: `Append` on `System.Text.StringBuilder` with one argument that
/// is a `+` chain typed `string` through the built-in concatenation; the
/// chain is split only where the node is a string concatenation (`1 + 2 +
/// "x"` keeps `1 + 2` as one operand, as C# evaluates it); an interpolated
/// string argument is left alone (.NET 6+ handles it without an
/// intermediate); each piece appends the same characters `+` produced
/// (`Append(int)`, `Append(char)`, `Append(object)` format as concatenation
/// does; an array piece stands down, `Append(char[])` appending the characters);
/// no piece reads a `StringBuilder` (`sb.Append("Len:" + sb.Length)` read
/// the length before the append, the chain after the first piece).
module CSharp.Refactor.CollectionFixes

open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax
open Microsoft.CodeAnalysis.Operations
open Microsoft.CodeAnalysis.Text
open System.Collections.Concurrent

[<Literal>]
let AddRangeCode = "CR0024"

[<Literal>]
let SharedRandomCode = "CR0031"

[<Literal>]
let DictionaryPairCode = "CR0032"

[<Literal>]
let AppendChainCode = "CR0033"

[<TailCall>]
let rec private isPureReceiver (e: ExpressionSyntax) =
    match e with
    | :? IdentifierNameSyntax
    | :? ThisExpressionSyntax -> true
    | :? MemberAccessExpressionSyntax as m -> isPureReceiver m.Expression
    | _ -> false

let private singleStatement (body: StatementSyntax) =
    match body with
    | :? BlockSyntax as b when b.Statements.Count = 1 -> ValueSome b.Statements.[0]
    | :? BlockSyntax -> ValueNone
    | s -> ValueSome s

// ---- CR0024 ----

let private addRange (tree: SyntaxTree) (model: SemanticModel) : Suggestion list =
    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun node ->
        match node with
        | :? ForEachStatementSyntax as f ->
            match singleStatement f.Statement with
            | ValueSome(:? ExpressionStatementSyntax as s) ->
                match s.Expression with
                | :? InvocationExpressionSyntax as inv when
                    Linq.nameOf inv = "Add"
                    && inv.ArgumentList.Arguments.Count = 1
                    && inv.ArgumentList.Arguments.[0].Expression.ToString() = f.Identifier.ValueText
                    ->
                    match inv.Expression, model.GetSymbolInfo(inv).Symbol with
                    | (:? MemberAccessExpressionSyntax as m), (:? IMethodSymbol as add) when
                        add.ContainingType.OriginalDefinition.ToDisplayString() = "System.Collections.Generic.List<T>"
                        && isPureReceiver m.Expression
                        && not (Text.mentionsName f.Identifier.ValueText m.Expression)
                        && m.Expression.ToString() <> f.Expression.ToString()
                        && not (Text.holdsCommentOrDirective f)
                        // AddRange over a lazy sequence measured 2.9× slower than the loop (PerfClaims)
                        && Linq.isCollection (model.GetTypeInfo(f.Expression).Type)
                        ->
                        let call = m.Expression.ToString() + ".AddRange(" + f.Expression.ToString() + ")"
                        let edit = Suggestion.replace f.Span (call + ";")

                        // the call must land on `AddRange(IEnumerable<T>)`: the .NET 10
                        // `params ReadOnlySpan<T>` overload would take an `Array` or a
                        // non-generic source as ONE element
                        let landsOnEnumerable =
                            match Guards.speculativeSymbol model f.SpanStart call with
                            | Some(:? IMethodSymbol as ar) ->
                                ar.Parameters.Length = 1
                                && not ar.Parameters.[0].IsParams
                                && ar.Parameters.[0].Type.OriginalDefinition.ToDisplayString() =
                                    "System.Collections.Generic.IEnumerable<T>"
                            | _ -> false

                        if landsOnEnumerable && Guards.speculativeCheck model [ edit ] then
                            Some
                                {
                                    Code = AddRangeCode
                                    Message = "Adding every element one by one is AddRange"
                                    Span = TextSpan.FromBounds(f.ForEachKeyword.SpanStart, f.CloseParenToken.Span.End)
                                    Fixes = [ Suggestion.fix "Use AddRange" AddRangeCode [ edit ] ]
                                }
                        else
                            None
                    | _ -> None
                | _ -> None
            | _ -> None
        | _ -> None)
    |> List.ofSeq

// ---- CR0031 ----

let private sharedRandom (tree: SyntaxTree) (model: SemanticModel) : Suggestion list =
    let randomType = model.Compilation.GetTypeByMetadataName "System.Random"

    let sharedResolves =
        not (isNull randomType)
        && randomType.GetMembers("Shared") |> Seq.exists (fun m -> m :? IPropertySymbol)

    if not sharedResolves then
        []
    else
        let isRandomCreation (e: ExpressionSyntax) =
            match e with
            | :? BaseObjectCreationExpressionSyntax as c when
                (isNull c.ArgumentList || c.ArgumentList.Arguments.Count = 0)
                && (isNull c.Initializer)
                ->
                match model.GetTypeInfo(c).Type with
                | null -> false
                | t -> SymbolEqualityComparer.Default.Equals(t, randomType)
            | _ -> false

        let spelling (position: int) =
            if Linq.resolvesBare model position "System" "Random" then
                "Random.Shared"
            else
                "System.Random.Shared"

        tree.GetRoot().DescendantNodes()
        |> Seq.choose (fun node ->
            match node with
            // `new Random().Next(…)`
            | :? MemberAccessExpressionSyntax as m when isRandomCreation m.Expression ->
                Some
                    {
                        Code = SharedRandomCode
                        Message = "A Random per call is Random.Shared: no allocation, no seeding, thread-safe"
                        Span = m.Expression.Span
                        Fixes =
                            [
                                Suggestion.fix
                                    "Use Random.Shared"
                                    SharedRandomCode
                                    [ Suggestion.replace m.Expression.Span (spelling m.SpanStart) ]
                            ]
                    }
            // `var r = new Random();` used only as a call receiver
            | :? LocalDeclarationStatementSyntax as d when
                d.Declaration.Variables.Count = 1
                && not (isNull d.Declaration.Variables.[0].Initializer)
                && isRandomCreation d.Declaration.Variables.[0].Initializer.Value
                ->
                let v = d.Declaration.Variables.[0]
                let symbol = model.GetDeclaredSymbol v

                let uses =
                    (Text.enclosingMember d).DescendantNodes()
                    |> Seq.choose (fun n ->
                        match n with
                        | :? IdentifierNameSyntax as id when
                            id.Identifier.ValueText = v.Identifier.ValueText
                            && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(id).Symbol, symbol)
                            ->
                            Some id
                        | _ -> None)
                    |> List.ofSeq

                let onlyCalls =
                    uses
                    |> List.forall (fun id ->
                        match id.Parent with
                        | :? MemberAccessExpressionSyntax as m when obj.ReferenceEquals(m.Expression, id) ->
                            m.Parent :? InvocationExpressionSyntax
                        | _ -> false)

                if onlyCalls && not uses.IsEmpty then
                    let init = v.Initializer.Value

                    Some
                        {
                            Code = SharedRandomCode
                            Message = "A Random per call is Random.Shared: no allocation, no seeding, thread-safe"
                            Span = init.Span
                            Fixes =
                                [
                                    Suggestion.fix
                                        "Use Random.Shared"
                                        SharedRandomCode
                                        [ Suggestion.replace init.Span (spelling init.SpanStart) ]
                                ]
                        }
                else
                    None
            | _ -> None)
        |> List.ofSeq

// ---- CR0032 ----

/// The dictionary members that only read it.
let private readOnlyMembers =
    set
        [
            "Count"
            "ContainsKey"
            "ContainsValue"
            "TryGetValue"
            "GetValueOrDefault"
            "Keys"
            "Values"
            "Comparer"
        ]

/// A member whose call cannot reach a dictionary of the user's: the BCL's
/// own, but never a delegate's `Invoke` (the user's body), `Lazy<T>.Value`
/// (the user's factory) or an interface's (`ICollection<T>.Add` dispatches
/// to whatever implements it) — those are looked into, where their bodies
/// can be seen. Accepted residual, as on the F# side: a BCL member that
/// calls back into its arguments' or its own callbacks' user code
/// (`ToString` through `Write(object)`, `CompareTo` through `Sort`,
/// `GetHashCode` through `HashSet.Add`, `ObservableCollection`'s
/// `CollectionChanged`, `CancellationTokenSource.Cancel`) — the escape
/// proof closes these for a dictionary no other code can reach.
let private harmlessMember (s: ISymbol) =
    Guards.isBclSymbol s
    && not (isNull s.ContainingType)
    && s.ContainingType.TypeKind <> TypeKind.Interface
    && (match s with
        | :? IMethodSymbol as m -> m.MethodKind <> MethodKind.DelegateInvoke
        | :? IPropertySymbol as p -> p.ContainingType.Name <> "Lazy"
        | _ -> true)

/// A type whose enumerator or `Dispose` is the BCL's own: a BCL class or
/// struct, an array, a string — never an interface (a user implementation
/// may stand behind it) or a type of the user's.
let private bclConcrete (t: ITypeSymbol) =
    not (isNull t)
    && (t :? IArrayTypeSymbol
        || ((t.TypeKind = TypeKind.Class || t.TypeKind = TypeKind.Struct)
            && Guards.isBclSymbol t.OriginalDefinition))

/// A value whose `ToString` is the BCL's: a primitive, a string, an enum, a
/// BCL type — formatting anything else runs the user's override.
let private bclFormatted (t: ITypeSymbol) =
    not (isNull t)
    && ((t.SpecialType <> SpecialType.None && t.SpecialType <> SpecialType.System_Object)
        || t.TypeKind = TypeKind.Enum
        || Guards.isBclSymbol t.OriginalDefinition
           && (match t with
               | :? INamedTypeSymbol as n -> n.TypeArguments |> Seq.forall (fun a -> a.SpecialType <> SpecialType.None)
               | _ -> true))

/// Everything in a `Keys` loop's body that visibly may write the dictionary,
/// each with the position from which it has taken effect: the receiver (or
/// a name it is read through) named other than by a read or a read-only
/// member (`d.Remove(k)`, `d["a"] = 1`, `var m = d`, `this.d = …`); a store
/// into another dictionary-typed value (an alias) or into a user key's or a
/// comparer's state; an `await` or a `yield`; and a call, getter, setter,
/// constructor, operator, `Deconstruct`, enumerator or `Dispose` of the
/// user's whose body — visible in this compilation, followed three calls
/// deep, a delegate's or a lazy's through what it visibly came from —
/// touches a dictionary. One whose body cannot be seen (an interface's or
/// abstract member, a delegate of unknown origin, metadata) is taken not
/// to: the accepted residual is such user code writing the very dictionary.
/// A call takes effect when it returns, after its arguments ran: `Use(k,
/// d[k])` reads first. For a `confined` dictionary (no other code holds it)
/// only the receiver's own mentions count: nothing else can reach it.
let private touchVerdicts =
    System.Runtime.CompilerServices.ConditionalWeakTable<
        Compilation,
        ConcurrentDictionary<ISymbol, ConcurrentDictionary<string, bool>>
     >()

let private dictionaryHazards
    (model: SemanticModel)
    (isDictionary: ITypeSymbol -> bool)
    (confined: bool)
    (body: StatementSyntax)
    (chain: ISymbol list)
    (reads: TextSpan list)
    : (TextSpan * int) list =
    match model.GetOperation body with
    | null -> [ body.Span, body.SpanStart ]
    | root ->
        let dictionary = List.last chain

        let inChain (s: ISymbol) =
            chain |> List.exists (fun c -> SymbolEqualityComparer.Default.Equals(c, s))

        let insideRead (op: IOperation) =
            reads |> List.exists (fun r -> r.Contains op.Syntax.Span)

        let atEnd (op: IOperation) =
            Some(op.Syntax.Span, op.Syntax.Span.End)

        // a store takes effect when the assignment it is the target of runs
        let written (op: IOperation) =
            match op.Parent with
            | :? IAssignmentOperation as a when obj.ReferenceEquals(a.Target, op) -> Some op.Parent
            | :? IIncrementOrDecrementOperation -> Some op.Parent
            | :? IArgumentOperation as a when not (isNull a.Parameter) && a.Parameter.RefKind <> RefKind.None ->
                Some op.Parent
            | _ -> None

        // the receiver's own name: read by a read-only member or another
        // key's lookup, or the qualifier of the next name in its spelling
        let harmlessMention (op: IOperation) (s: ISymbol) =
            (written op).IsNone
            && (match op.Parent with
                | :? IMemberReferenceOperation as m when obj.ReferenceEquals(m.Instance, op) ->
                    if SymbolEqualityComparer.Default.Equals(s, dictionary) then
                        match m with
                        // the dictionary's own reads: through its interface too,
                        // where its lookups are that implementation's already
                        | :? IPropertyReferenceOperation as p ->
                            Guards.isBclSymbol p.Property
                            && (p.Property.IsIndexer || readOnlyMembers.Contains p.Property.Name)
                            && (written p).IsNone
                        | _ -> false
                    else
                        true
                | :? IInvocationOperation as inv when obj.ReferenceEquals(inv.Instance, op) ->
                    SymbolEqualityComparer.Default.Equals(s, dictionary)
                    && Guards.isBclSymbol inv.TargetMethod
                    && readOnlyMembers.Contains inv.TargetMethod.Name
                // `d.GetValueOrDefault(k)`: the BCL's extension read, the receiver
                // its first argument (converted to the interface it extends)
                | parent ->
                    let argument =
                        match parent with
                        | :? IArgumentOperation as a -> Some a
                        | :? IConversionOperation as c when c.IsImplicit ->
                            match c.Parent with
                            | :? IArgumentOperation as a -> Some a
                            | _ -> None
                        | _ -> None

                    match argument |> Option.map (fun a -> a.Parent) with
                    | Some(:? IInvocationOperation as inv) ->
                        SymbolEqualityComparer.Default.Equals(s, dictionary)
                        && inv.TargetMethod.IsExtensionMethod
                        && inv.Arguments.Length > 0
                        && obj.ReferenceEquals(inv.Arguments.[0], argument.Value)
                        && Guards.isBclSymbol inv.TargetMethod
                        && readOnlyMembers.Contains inv.TargetMethod.Name
                    | _ -> false)

        let mutatesDictionary (instance: IOperation) (name: string) =
            not (isNull instance)
            && isDictionary instance.Type
            && not (readOnlyMembers.Contains name)

        // the lookup hashes the key through the dictionary's comparer: a store
        // into a user key's or a comparer's state moves it (`cmp.Upper = true`)
        let keyType =
            match dictionary with
            | :? ILocalSymbol as l -> l.Type
            | :? IParameterSymbol as p -> p.Type
            | :? IFieldSymbol as f -> f.Type
            | :? IPropertySymbol as p -> p.Type
            | _ -> null
            |> function
                | :? INamedTypeSymbol as n ->
                    Seq.append [ n ] n.AllInterfaces
                    |> Seq.tryPick (fun t ->
                        if t.TypeArguments.Length = 2 && t.Name.EndsWith "Dictionary" then
                            Some t.TypeArguments.[0]
                        else
                            None)
                    |> Option.toObj
                | _ -> null

        let hashingState (owner: ITypeSymbol) =
            not (isNull owner)
            && ((not (isNull keyType)
                 && not (Guards.isBclElementType keyType)
                 && SymbolEqualityComparer.Default.Equals(owner.OriginalDefinition, keyType.OriginalDefinition))
                || owner.AllInterfaces
                   |> Seq.exists (fun i -> i.Name = "IEqualityComparer" || i.Name = "IComparer"))

        let storesHashingState (op: IOperation) =
            match op with
            | :? IMemberReferenceOperation as m when (written op).IsSome -> hashingState m.Member.ContainingType
            | _ -> false

        // does a body visibly touch a dictionary — read or write a
        // dictionary-typed value, call its members, build or hand one on — or
        // store into a user key's or a comparer's state?
        let touches (m: SemanticModel) (body: SyntaxNode) =
            body.DescendantNodesAndSelf()
            |> Seq.exists (fun n ->
                match n with
                | :? IdentifierNameSyntax
                | :? MemberAccessExpressionSyntax
                | :? MemberBindingExpressionSyntax
                | :? ElementAccessExpressionSyntax
                | :? InvocationExpressionSyntax
                | :? CastExpressionSyntax
                | :? BaseObjectCreationExpressionSyntax ->
                    let x = n :?> ExpressionSyntax
                    let symbol = m.GetSymbolInfo(x).Symbol

                    (not (symbol :? INamespaceOrTypeSymbol) && isDictionary (m.GetTypeInfo(x).Type))
                    || (match n with
                        | :? IdentifierNameSyntax
                        | :? MemberAccessExpressionSyntax ->
                            let stored =
                                match n.Parent with
                                | :? AssignmentExpressionSyntax as a -> obj.ReferenceEquals(a.Left, n)
                                | p ->
                                    p.IsKind SyntaxKind.PostIncrementExpression
                                    || p.IsKind SyntaxKind.PostDecrementExpression
                                    || p.IsKind SyntaxKind.PreIncrementExpression
                                    || p.IsKind SyntaxKind.PreDecrementExpression

                            stored
                            && (match symbol with
                                | (:? IFieldSymbol | :? IPropertySymbol) as s -> hashingState s.ContainingType
                                | _ -> false)
                        | _ -> false)
                | _ -> false)

        // once per callee (and key type) of the compilation: a type's every
        // loop asks the same of the same helpers
        let keyTypeKey = if isNull keyType then "" else keyType.ToDisplayString()

        let userTouches (roots: ISymbol list) =
            let perSymbol =
                touchVerdicts.GetValue(
                    model.Compilation,
                    fun _ ->
                        ConcurrentDictionary<ISymbol, ConcurrentDictionary<string, bool>>(
                            SymbolEqualityComparer.Default
                        )
                )

            roots
            |> List.exists (fun r ->
                not (isNull r)
                && perSymbol
                    .GetOrAdd(r, fun _ -> ConcurrentDictionary<string, bool>())
                    .GetOrAdd(keyTypeKey, fun _ -> Guards.reachesThrough model 3 touches [ r ]))

        let hazardIf (roots: ISymbol list) (op: IOperation) =
            if userTouches roots then atEnd op else None

        // what a delegate, a lazy or a sequence visibly came from: a hazard
        // when its making touched a dictionary (`Func<string, bool> rm =
        // d.Remove`) or what it runs does (`bump = key => d[key] = 0`)
        let originsHazardous (e: ExpressionSyntax) =
            Guards.originsOf model e
            |> List.exists (fun (om, value) -> touches om value || userTouches (Guards.originSymbols om value))

        // one small function per operation kind, chosen by `op.Kind`: a single
        // match over every interface with guards compiled to a decision tree of
        // a megabyte of IL, whose JIT alone took seconds per process
        let byOperator (m: IMethodSymbol) (op: IOperation) =
            if isNull m || harmlessMember m then
                None
            else
                hazardIf [ m ] op

        let propertyHazard (op: IOperation) =
            let r = op :?> IPropertyReferenceOperation
            let store = written op

            let effect =
                match store with
                | Some a -> atEnd a
                | None -> atEnd op

            // a store into another dictionary-typed value: an alias
            if store.IsSome && mutatesDictionary r.Instance "set" then
                effect
            elif harmlessMember r.Property || Guards.isAutoProperty r.Property then
                None
            elif r.Property.Name = "Value" && r.Property.ContainingType.Name = "Lazy" then
                // the lazy's factory, where it was made
                match (if isNull r.Instance then null else r.Instance.Syntax) with
                | :? ExpressionSyntax as x when originsHazardous x -> effect
                | _ -> None
            elif userTouches [ r.Property ] then
                effect
            else
                None

        let invocationHazard (op: IOperation) =
            let inv = op :?> IInvocationOperation
            let m = inv.TargetMethod

            // a writer of some dictionary-typed value: an alias
            let aliased =
                mutatesDictionary inv.Instance m.Name
                || (m.IsExtensionMethod
                    && inv.Arguments.Length > 0
                    && mutatesDictionary inv.Arguments.[0].Value m.Name)

            if aliased then
                atEnd op
            elif harmlessMember m then
                None
            elif m.MethodKind = MethodKind.DelegateInvoke then
                match op.Syntax with
                | :? InvocationExpressionSyntax as call when originsHazardous (Guards.delegateReceiver call) -> atEnd op
                | _ -> None
            else
                hazardIf [ m ] op

        // a delegate value handed to a call, which may invoke it
        let argumentHazard (op: IOperation) =
            let a = op :?> IArgumentOperation

            if
                not (isNull a.Value.Type)
                && a.Value.Type.TypeKind = TypeKind.Delegate
                && not (a.Value :? IDelegateCreationOperation)
                && (match a.Value.Syntax with
                    | :? ExpressionSyntax as x -> originsHazardous x
                    | _ -> false)
            then
                atEnd op.Parent
            else
                None

        // the user's `ToString` a formatting runs: its visible override
        let formatterHazard (t: ITypeSymbol) (op: IOperation) =
            if isNull t || bclFormatted t then
                None
            else
                t.GetMembers "ToString"
                |> Seq.filter (fun s ->
                    match s with
                    | :? IMethodSymbol as meth -> meth.Parameters.Length = 0
                    | _ -> false)
                |> List.ofSeq
                |> fun overrides -> hazardIf overrides op

        let binaryHazard (op: IOperation) =
            let b = op :?> IBinaryOperation

            if not (isNull b.OperatorMethod) then
                byOperator b.OperatorMethod op
            elif
                b.OperatorKind = BinaryOperatorKind.Add
                && not (isNull b.Type)
                && b.Type.SpecialType = SpecialType.System_String
            then
                match formatterHazard b.LeftOperand.Type op with
                | Some h -> Some h
                | None -> formatterHazard b.RightOperand.Type op
            else
                None

        // a `Deconstruct` of the user's, looked into; a tuple's needs none
        let deconstructionHazard (op: IOperation) =
            let deconstructor =
                match op.Syntax with
                | :? AssignmentExpressionSyntax as a -> model.GetDeconstructionInfo(a).Method
                | :? ForEachVariableStatementSyntax as f -> model.GetDeconstructionInfo(f).Method
                | _ -> null

            if isNull deconstructor || harmlessMember deconstructor then
                None
            else
                hazardIf [ deconstructor ] op

        let foreachHazard (op: IOperation) =
            match op.Syntax with
            | :? CommonForEachStatementSyntax as f ->
                let info = model.GetForEachStatementInfo f

                let members =
                    [
                        info.GetEnumeratorMethod :> ISymbol
                        info.MoveNextMethod
                        info.CurrentProperty
                        info.DisposeMethod
                    ]
                    |> List.filter (isNull >> not)

                // a BCL collection's own enumerator (`List<T>.Enumerator`) is the
                // BCL's even where its `Dispose` is reported as `IDisposable`'s; a
                // user's, or one behind an interface, is looked into — its visible
                // members, and what the sequence visibly came from (`var xs =
                // Touch(); foreach (var x in xs)`)
                let enumerator =
                    if isNull info.GetEnumeratorMethod then
                        null
                    else
                        info.GetEnumeratorMethod.ReturnType

                if
                    bclConcrete (model.GetTypeInfo(f.Expression).Type)
                    && bclConcrete enumerator
                    && members |> List.forall Guards.isBclSymbol
                then
                    None
                elif userTouches members || originsHazardous f.Expression then
                    Some(op.Syntax.Span, f.Expression.Span.End)
                else
                    None
            | _ -> None

        // `Dispose` runs at the end of the `using`: a user one is looked into
        let disposeHazard (types: ITypeSymbol list) (effect: (TextSpan * int) option) =
            let disposers =
                types
                |> List.filter (fun t -> not (isNull t || bclConcrete t))
                |> List.collect (fun t -> t.GetMembers "Dispose" |> List.ofSeq)

            if userTouches disposers then effect else None

        let usingHazard (op: IOperation) =
            let disposed =
                match (op :?> IUsingOperation).Resources with
                | :? IVariableDeclarationGroupOperation as g ->
                    g.Declarations
                    |> Seq.collect (fun d -> d.Declarators)
                    |> Seq.map (fun d -> d.Symbol.Type)
                    |> List.ofSeq
                | null -> []
                // `using (r)`: the value's own type, not the IDisposable it converts to
                | :? IConversionOperation as c when c.IsImplicit -> [ c.Operand.Type ]
                | r -> [ r.Type ]

            disposeHazard disposed (atEnd op)

        let usingDeclarationHazard (op: IOperation) =
            let disposed =
                (op :?> IUsingDeclarationOperation).DeclarationGroup.Declarations
                |> Seq.collect (fun d -> d.Declarators)
                |> Seq.map (fun d -> d.Symbol.Type)
                |> List.ofSeq

            disposeHazard disposed (Some(op.Syntax.Span, op.Syntax.Parent.Span.End))

        // the receiver's names, by kind
        let chainMember (op: IOperation) : ISymbol =
            match op.Kind with
            | OperationKind.LocalReference -> (op :?> ILocalReferenceOperation).Local
            | OperationKind.ParameterReference -> (op :?> IParameterReferenceOperation).Parameter
            | OperationKind.FieldReference -> (op :?> IFieldReferenceOperation).Field
            | OperationKind.PropertyReference -> (op :?> IPropertyReferenceOperation).Property
            | _ -> null

        let hazardOf (op: IOperation) =
            match chainMember op with
            | s when not (isNull s) && inChain s ->
                if harmlessMention op s then
                    None
                else
                    Some(op.Syntax.Span, op.Syntax.SpanStart)
            | _ when confined -> None
            | _ when storesHashingState op -> atEnd op.Parent
            | _ ->
                match op.Kind with
                | OperationKind.PropertyReference -> propertyHazard op
                | OperationKind.Invocation -> invocationHazard op
                | OperationKind.ObjectCreation -> byOperator (op :?> IObjectCreationOperation).Constructor op
                | OperationKind.MethodReference ->
                    // a method group handed on runs wherever it is called
                    let r = op :?> IMethodReferenceOperation

                    if not (harmlessMember r.Method) && userTouches [ r.Method ] then
                        Some(op.Syntax.Span, op.Syntax.SpanStart)
                    else
                        None
                | OperationKind.Argument -> argumentHazard op
                | OperationKind.Binary -> binaryHazard op
                | OperationKind.Unary -> byOperator (op :?> IUnaryOperation).OperatorMethod op
                | OperationKind.Conversion -> byOperator (op :?> IConversionOperation).OperatorMethod op
                | OperationKind.CompoundAssignment -> byOperator (op :?> ICompoundAssignmentOperation).OperatorMethod op
                | OperationKind.Increment
                | OperationKind.Decrement -> byOperator (op :?> IIncrementOrDecrementOperation).OperatorMethod op
                | OperationKind.Interpolation -> formatterHazard (op :?> IInterpolationOperation).Expression.Type op
                | OperationKind.DeconstructionAssignment -> deconstructionHazard op
                | OperationKind.Loop -> foreachHazard op
                | OperationKind.Using -> usingHazard op
                | OperationKind.UsingDeclaration -> usingDeclarationHazard op
                // the caller, or another task, runs while this one waits
                | OperationKind.Await
                | OperationKind.YieldReturn -> atEnd op
                | _ -> None

        root.DescendantsAndSelf()
        |> Seq.choose (fun op -> if insideRead op then None else hazardOf op)
        |> List.ofSeq

/// The names a dictionary receiver is read through, when every read of it
/// hands back the SAME dictionary: a local, a parameter, a field or an
/// auto-property at every segment. A computed property may hand each read
/// another one than the header walked.
let rec private stableReceiver (model: SemanticModel) (e: ExpressionSyntax) : ISymbol list option =
    let segment (s: ISymbol) =
        match s with
        | :? ILocalSymbol
        | :? IParameterSymbol
        | :? IFieldSymbol -> Some [ s ]
        | :? IPropertySymbol as p when Guards.isAutoProperty p -> Some [ s ]
        // a computed getter hands back the same dictionary each read unless
        // its visible body constructs or counts (`=> new Dictionary<…>()`)
        | :? IPropertySymbol as p when not (Guards.unstableGetter model p) -> Some [ s ]
        | :? INamespaceOrTypeSymbol -> Some []
        | _ -> None

    match e with
    | :? ThisExpressionSyntax -> Some []
    | :? IdentifierNameSyntax -> model.GetSymbolInfo(e).Symbol |> Option.ofObj |> Option.bind segment
    | :? MemberAccessExpressionSyntax as m ->
        stableReceiver model m.Expression
        |> Option.bind (fun qualifier ->
            model.GetSymbolInfo(m).Symbol
            |> Option.ofObj
            |> Option.bind segment
            |> Option.map (fun last -> qualifier @ last))
    | _ -> None

/// Is the dictionary one no other code can reach — so no call, whoever's,
/// can write it? A local initialized with a BCL dictionary it builds (`new
/// Dictionary<…>()`, `new()`, `[]`, `ToDictionary(…)`) over BCL keys with
/// no comparer or the BCL's (`StringComparer.Ordinal`: a user key's or
/// comparer's state may move a lookup) and never reassigned, or a `private
/// readonly` field so initialized in a type declared in this file alone,
/// whose every mention (in the whole type) only reads it — and in either
/// case every mention keeps it: its own members and indexer, a `foreach`
/// over it, a LINQ operator building a new sequence over it; never
/// `AsEnumerable`/`Cast`/`OfType` or a member handing out a writer
/// (`GetAlternateLookup`), an argument, a returned or stored value, an
/// alias, a `ref`, a `using`/`lock`, and for a local never a capture by a
/// lambda, local function or query.
let private confinedVerdicts =
    System.Runtime.CompilerServices.ConditionalWeakTable<Compilation, ConcurrentDictionary<ISymbol, bool>>()

/// The dictionary's own members a confined one may be used through:
/// `readOnlyMembers` plus its writers — never one handing out a writer
/// (`GetAlternateLookup`) or the instance itself.
let private ownWriters =
    set [ "Add"; "Remove"; "Clear"; "TryAdd"; "EnsureCapacity"; "TrimExcess" ]

/// LINQ operators that hand the dictionary on as itself or as a live view.
let private handsItOn = set [ "AsEnumerable"; "Cast"; "OfType" ]

let private confinedReceiverUncached (model: SemanticModel) (chain: ISymbol list) : bool =
    // a comparer argument, if any, is the BCL's own (`StringComparer.Ordinal`,
    // `EqualityComparer<T>.Default`): a user one may hash by mutable state
    let bclComparers (args: ArgumentListSyntax) =
        isNull args
        || args.Arguments
           |> Seq.forall (fun a ->
               match model.GetTypeInfo(a.Expression).Type with
               | null -> a.Expression :? AnonymousFunctionExpressionSyntax // a selector
               | t when
                   t.Name = "IEqualityComparer"
                   || t.Name = "IComparer"
                   || t.Name = "StringComparer"
                   ->
                   match model.GetSymbolInfo(a.Expression).Symbol with
                   | :? IPropertySymbol
                   | :? IFieldSymbol as s -> s.IsStatic && Guards.isBclSymbol s
                   | _ -> false
               | t ->
                   t.AllInterfaces
                   |> Seq.forall (fun i -> i.Name <> "IEqualityComparer" && i.Name <> "IComparer"))

    // keys of a BCL type: a user key's hash may move with its state
    let bclKeys (t: ITypeSymbol) =
        match t with
        | :? INamedTypeSymbol as n when n.TypeArguments.Length = 2 -> Guards.isBclElementType n.TypeArguments.[0]
        | _ -> false

    let built (init: EqualsValueClauseSyntax) =
        not (isNull init)
        && (match init.Value with
            | :? BaseObjectCreationExpressionSyntax
            | :? CollectionExpressionSyntax as v ->
                // a `Dictionary`/`SortedDictionary` copies what it is built
                // from; a wrapper (`ReadOnlyDictionary(inner)`) is `inner`
                match model.GetTypeInfo(v).ConvertedType with
                | null -> false
                | t ->
                    Guards.isBclSymbol t.OriginalDefinition
                    && bclKeys t
                    && (match v with
                        | :? BaseObjectCreationExpressionSyntax as c -> bclComparers c.ArgumentList
                        | _ -> true)
                    && (let name = t.OriginalDefinition.ToDisplayString()

                        name = "System.Collections.Generic.Dictionary<TKey, TValue>"
                        || name = "System.Collections.Generic.SortedDictionary<TKey, TValue>")
            | :? InvocationExpressionSyntax as inv ->
                match model.GetSymbolInfo(inv).Symbol with
                | :? IMethodSymbol as m ->
                    m.Name = "ToDictionary"
                    && m.ContainingType.ToDisplayString() = "System.Linq.Enumerable"
                    && bclKeys m.ReturnType
                    && bclComparers inv.ArgumentList
                | _ -> false
            | _ -> false)

    let declarator (s: ISymbol) =
        match s.DeclaringSyntaxReferences |> Seq.tryExactlyOne with
        | Some r ->
            match r.GetSyntax() with
            | :? VariableDeclaratorSyntax as v -> Some v
            | _ -> None
        | None -> None

    // every mention of `s` in `scopes`: kept, and (for `readOnly`) only read
    let mentionsKeep (s: ISymbol) (scopes: SyntaxNode list) (readOnly: bool) (captureEscapes: bool) =
        scopes
        |> Seq.collect (fun scope -> scope.DescendantNodes())
        |> Seq.forall (fun n ->
            match n with
            | :? IdentifierNameSyntax as id when
                id.Identifier.ValueText = s.Name
                && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(id).Symbol, s)
                ->
                // `this.d` stands for `d`
                let e: ExpressionSyntax =
                    match id.Parent with
                    | :? MemberAccessExpressionSyntax as m when
                        obj.ReferenceEquals(m.Name, id) && (m.Expression :? ThisExpressionSyntax)
                        ->
                        m
                    | _ -> id

                let captured =
                    captureEscapes
                    && e.Ancestors()
                       |> Seq.exists (fun a ->
                           a :? AnonymousFunctionExpressionSyntax
                           || a :? LocalFunctionStatementSyntax
                           || a :? QueryExpressionSyntax)

                let writes (x: SyntaxNode) =
                    match x.Parent with
                    | :? AssignmentExpressionSyntax as a -> obj.ReferenceEquals(a.Left, x)
                    | :? PostfixUnaryExpressionSyntax
                    | :? PrefixUnaryExpressionSyntax -> true
                    | :? ArgumentSyntax as a -> not (a.RefKindKeyword.IsKind SyntaxKind.None)
                    | _ -> false

                let kept =
                    match e.Parent with
                    // a method named but not called (`Func<string, bool> rm = d.Remove;`)
                    // hands the writer on
                    | :? MemberAccessExpressionSyntax as m when
                        obj.ReferenceEquals(m.Expression, e)
                        && (model.GetSymbolInfo(m).Symbol :? IMethodSymbol)
                        && not (m.Parent :? InvocationExpressionSyntax)
                        ->
                        false
                    | :? MemberAccessExpressionSyntax as m when obj.ReferenceEquals(m.Expression, e) ->
                        match model.GetSymbolInfo(m).Symbol with
                        | :? IMethodSymbol as meth when meth.IsExtensionMethod ->
                            let owner = meth.ContainingType.ToDisplayString()

                            // LINQ building a new sequence over it reads it;
                            // `AsEnumerable`/`Cast`/`OfType` hand on the instance
                            (owner = "System.Linq.Enumerable" && not (handsItOn.Contains meth.Name))
                            || (owner = "System.Collections.Generic.CollectionExtensions"
                                && (meth.Name = "GetValueOrDefault"
                                    || (not readOnly && (meth.Name = "TryAdd" || meth.Name = "Remove"))))
                        | null -> false
                        | meth ->
                            Guards.isBclSymbol meth
                            && (readOnlyMembers.Contains meth.Name
                                || (not readOnly && ownWriters.Contains meth.Name))
                    | :? ElementAccessExpressionSyntax as ea when obj.ReferenceEquals(ea.Expression, e) ->
                        not (readOnly && writes ea)
                    | :? ForEachStatementSyntax as f -> obj.ReferenceEquals(f.Expression, e)
                    | _ -> false

                kept && not captured
            | _ -> true)

    match chain with
    | [ :? ILocalSymbol as l ] when not l.IsRef ->
        match declarator l with
        | Some v when built v.Initializer ->
            // never reassigned: `d = other` would make it an alias
            let scope = Text.enclosingMember v
            mentionsKeep l [ scope ] false true
        | _ -> false
    | [ :? IFieldSymbol as f ] when
        f.DeclaredAccessibility = Accessibility.Private
        && f.IsReadOnly
        && f.ContainingType.DeclaringSyntaxReferences.Length = 1
        ->
        match declarator f with
        | Some v when built v.Initializer -> mentionsKeep f (Guards.privateMemberScope v.SyntaxTree f) true false
        | _ -> false
    | _ -> false

/// `confinedReceiverUncached`, once per local or field of a compilation: a
/// type's every loop over one field asks the same question of the whole type.
let private confinedReceiver (model: SemanticModel) (chain: ISymbol list) : bool =
    match chain with
    | [ (:? ILocalSymbol | :? IFieldSymbol) as s ] ->
        let verdicts =
            confinedVerdicts.GetValue(
                model.Compilation,
                fun _ -> ConcurrentDictionary<ISymbol, bool>(SymbolEqualityComparer.Default)
            )

        verdicts.GetOrAdd(s, fun _ -> confinedReceiverUncached model chain)
    | _ -> false

let private dictionaryPairs (tree: SyntaxTree) (model: SemanticModel) (ctx: RuleContext) : Suggestion list =
    if ctx.LanguageVersion < LanguageVersion.CSharp7 then
        []
    else
        let kvp =
            model.Compilation.GetTypeByMetadataName "System.Collections.Generic.KeyValuePair`2"

        let canDeconstruct =
            not (isNull kvp || kvp.GetMembers("Deconstruct") |> Seq.isEmpty)

        if not canDeconstruct then
            []
        else
            let isDictionary (t: ITypeSymbol) =
                match t with
                | null -> false
                | t ->
                    let names =
                        [
                            "System.Collections.Generic.Dictionary<TKey, TValue>"
                            "System.Collections.Generic.IDictionary<TKey, TValue>"
                            "System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>"
                            "System.Collections.Generic.SortedDictionary<TKey, TValue>"
                        ]

                    let full = t.OriginalDefinition.ToDisplayString()

                    // a concurrent dictionary's `Keys` is a snapshot where its enumerator is live
                    not (full.StartsWith "System.Collections.Concurrent")
                    && (List.contains full names
                        || t.AllInterfaces
                           |> Seq.exists (fun i -> List.contains (i.OriginalDefinition.ToDisplayString()) names))

            tree.GetRoot().DescendantNodes()
            |> Seq.choose (fun node ->
                match node with
                | :? ForEachStatementSyntax as f ->
                    match f.Expression with
                    | :? MemberAccessExpressionSyntax as keys when
                        keys.Name.Identifier.ValueText = "Keys"
                        && isPureReceiver keys.Expression
                        && isDictionary (model.GetTypeInfo(keys.Expression).Type)
                        ->
                        let d = keys.Expression
                        let k = f.Identifier.ValueText

                        // `d[k]` reads, `d[k] = …` writes
                        let lookups =
                            f.Statement.DescendantNodesAndSelf()
                            |> Seq.choose (fun n ->
                                match n with
                                | :? ElementAccessExpressionSyntax as e when
                                    Guards.sameReference model e.Expression d
                                    && e.ArgumentList.Arguments.Count = 1
                                    && e.ArgumentList.Arguments.[0].Expression.ToString() = k
                                    ->
                                    Some e
                                | _ -> None)
                            |> List.ofSeq

                        let written =
                            lookups
                            |> List.exists (fun e ->
                                match e.Parent with
                                | :? AssignmentExpressionSyntax as a -> a.Left.Span = e.Span
                                | :? PostfixUnaryExpressionSyntax
                                | :? PrefixUnaryExpressionSyntax -> true
                                | :? ArgumentSyntax as a -> not (a.RefKindKeyword.IsKind SyntaxKind.None)
                                | _ -> false)

                        if
                            lookups.IsEmpty
                            || written
                            || Text.assignsTo k f.Statement
                            || Text.assignsTo (d.ToString()) f.Statement
                        then
                            None
                        else
                            let scope = Text.enclosingMember f

                            // the pair's value is the one the iteration began
                            // with; `d[k]` is the one the read finds. Since .NET
                            // Core 3.0 an overwrite or a Remove no longer breaks
                            // the enumeration, so the two agree only when nothing
                            // that may write `d` runs before a read
                            let exact =
                                match stableReceiver model d with
                                | Some chain when not chain.IsEmpty ->
                                    // a read under a lambda, a local function or
                                    // a query runs later, against `d` as it is then
                                    let deferred =
                                        lookups
                                        |> List.exists (fun e ->
                                            e.Ancestors()
                                            |> Seq.takeWhile (fun a -> not (obj.ReferenceEquals(a, f)))
                                            |> Seq.exists (fun a ->
                                                a :? AnonymousFunctionExpressionSyntax
                                                || a :? LocalFunctionStatementSyntax
                                                || a :? QueryExpressionSyntax))

                                    let confined = confinedReceiver model chain

                                    let hazards =
                                        match chain with
                                        // a confined field is only ever read: nothing writes it
                                        | [ :? IFieldSymbol ] when confined -> []
                                        | _ ->
                                            dictionaryHazards
                                                model
                                                isDictionary
                                                confined
                                                f.Statement
                                                chain
                                                (lookups |> List.map (fun e -> e.Span))

                                    // a hazard runs before a read when it has taken
                                    // effect by the read's start — or anywhere in a
                                    // loop nested around the read, whose next round
                                    // runs it first
                                    let hazardBeforeRead =
                                        lookups
                                        |> List.exists (fun e ->
                                            let innerLoop =
                                                e.Ancestors()
                                                |> Seq.takeWhile (fun a -> not (obj.ReferenceEquals(a, f)))
                                                |> Seq.tryFind (fun a ->
                                                    a :? CommonForEachStatementSyntax
                                                    || a :? ForStatementSyntax
                                                    || a :? WhileStatementSyntax
                                                    || a :? DoStatementSyntax)

                                            hazards
                                            |> List.exists (fun (span, effect) ->
                                                effect <= e.SpanStart
                                                || (match innerLoop with
                                                    | Some loop -> loop.Span.Contains span
                                                    | None -> false)))

                                    // a local the member's lambdas or local functions
                                    // write: any call — a BCL one raising an event or a
                                    // cancellation callback included — may run them
                                    let writtenByClosure =
                                        match List.last chain with
                                        | :? ILocalSymbol
                                        | :? IParameterSymbol as s ->
                                            scope.DescendantNodes()
                                            |> Seq.exists (fun n ->
                                                match n with
                                                | :? IdentifierNameSyntax as id when
                                                    id.Identifier.ValueText = s.Name
                                                    && SymbolEqualityComparer.Default.Equals(
                                                        model.GetSymbolInfo(id).Symbol,
                                                        s
                                                    )
                                                    ->
                                                    let inClosure =
                                                        id.Ancestors()
                                                        |> Seq.exists (fun a ->
                                                            a :? AnonymousFunctionExpressionSyntax
                                                            || a :? LocalFunctionStatementSyntax)

                                                    let onlyReads =
                                                        match id.Parent with
                                                        | :? ElementAccessExpressionSyntax as ea ->
                                                            match ea.Parent with
                                                            | :? AssignmentExpressionSyntax as a ->
                                                                not (obj.ReferenceEquals(a.Left, ea))
                                                            | :? PostfixUnaryExpressionSyntax
                                                            | :? PrefixUnaryExpressionSyntax -> false
                                                            | _ -> true
                                                        | :? MemberAccessExpressionSyntax as m ->
                                                            obj.ReferenceEquals(m.Expression, id)
                                                            && readOnlyMembers.Contains m.Name.Identifier.ValueText
                                                        | _ -> false

                                                    inClosure && not onlyReads
                                                | _ -> false)
                                        | _ -> false

                                    not (deferred || hazardBeforeRead || writtenByClosure)
                                | _ -> false

                            let valueName =
                                [ "value"; "v"; k + "Value" ]
                                |> List.tryFind (fun n ->
                                    SyntaxFacts.GetKeywordKind n = SyntaxKind.None
                                    && not (Text.mentionsName n scope))

                            valueName
                            |> Option.map (fun valueName ->
                                let header = TextSpan.FromBounds(f.Type.SpanStart, f.Expression.Span.End)

                                let edits =
                                    Suggestion.replace
                                        header
                                        ("var (" + k + ", " + valueName + ") in " + d.ToString())
                                    :: (lookups |> List.map (fun e -> Suggestion.replace e.Span valueName))

                                if exact then
                                    {
                                        Code = DictionaryPairCode
                                        Message = $"Every key is looked up again as '{d}[{k}]': enumerate the pairs"
                                        Span = f.Expression.Span
                                        Fixes = [ Suggestion.fix "Enumerate the pairs" DictionaryPairCode edits ]
                                    }
                                else
                                    Suggestion.note
                                        DictionaryPairCode
                                        $"Every key is looked up again as '{d}[{k}]': enumerate the pairs — no fix: the pair holds the value the iteration began with, and something before a lookup may change '{d}' (or '{d}' is computed per read)"
                                        f.Expression.Span)
                    | _ -> None
                | _ -> None)
            |> List.ofSeq
            |> List.map (Guards.verified model)

// ---- CR0033 ----

let private appendChains (tree: SyntaxTree) (model: SemanticModel) : Suggestion list =
    let isString (e: ExpressionSyntax) =
        match model.GetTypeInfo(e).Type with
        | null -> false
        | t -> t.SpecialType = SpecialType.System_String

    // the operands of the string concatenation, left to right; a non-string
    // `+` (`1 + 2`) stays one operand
    let rec pieces (e: ExpressionSyntax) : ExpressionSyntax list =
        match e with
        | :? BinaryExpressionSyntax as b when
            b.IsKind SyntaxKind.AddExpression
            && isString b
            && Guards.isBuiltinOperator model b
            ->
            pieces b.Left @ [ b.Right ]
        | e -> [ e ]

    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun node ->
        match node with
        | :? InvocationExpressionSyntax as inv when
            Linq.nameOf inv = "Append"
            && inv.ArgumentList.Arguments.Count = 1
            && (inv.ArgumentList.Arguments.[0].Expression :? BinaryExpressionSyntax)
            ->
            match inv.Expression, model.GetSymbolInfo(inv).Symbol with
            | (:? MemberAccessExpressionSyntax as m), (:? IMethodSymbol as append) when
                append.ContainingType.ToDisplayString() = "System.Text.StringBuilder"
                ->
                let arg = inv.ArgumentList.Arguments.[0].Expression
                let parts = pieces arg

                if
                    parts.Length < 2
                    || Text.holdsCommentOrDirective inv.ArgumentList
                    || parts |> List.exists (fun p -> p :? InterpolatedStringExpressionSyntax)
                    // `sb.Append("Len:" + sb.Length)`: the concatenation read the builder
                    // before the append, the chain reads it after the first piece
                    || parts
                       |> List.exists (fun p ->
                           p.DescendantNodesAndSelf()
                           |> Seq.exists (fun d ->
                               match d with
                               | :? ExpressionSyntax as e ->
                                   match model.GetTypeInfo(e).Type with
                                   | null -> false
                                   | t -> t.ToDisplayString() = "System.Text.StringBuilder"
                               | _ -> false))
                    // `Append(char[])` appends the characters where `+` printed the type name
                    || parts
                       |> List.exists (fun p ->
                           match model.GetTypeInfo(p).Type with
                           | :? IArrayTypeSymbol -> true
                           | _ -> false)
                then
                    None
                else
                    let chain =
                        parts |> List.map (fun p -> ".Append(" + p.ToString() + ")") |> String.concat ""

                    let span = TextSpan.FromBounds(m.OperatorToken.SpanStart, inv.Span.End)
                    let edit = Suggestion.replace span chain

                    if Guards.speculativeCheck model [ edit ] then
                        Some
                            {
                                Code = AppendChainCode
                                Message = "Appending a concatenation builds the string first: append the pieces"
                                Span = arg.Span
                                Fixes = [ Suggestion.fix "Append each piece" AppendChainCode [ edit ] ]
                            }
                    else
                        None
            | _ -> None
        | _ -> None)
    |> List.ofSeq

let analyze (tree: SyntaxTree) (model: SemanticModel) (ctx: RuleContext) : Suggestion list =
    addRange tree model
    @ sharedRandom tree model
    @ dictionaryPairs tree model ctx
    @ appendChains tree model
