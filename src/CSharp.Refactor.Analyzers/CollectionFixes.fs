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
/// 2.0+).
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
open Microsoft.CodeAnalysis.Text

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

                                {
                                    Code = DictionaryPairCode
                                    Message = $"Every key is looked up again as '{d}[{k}]': enumerate the pairs"
                                    Span = f.Expression.Span
                                    Fixes = [ Suggestion.fix "Enumerate the pairs" DictionaryPairCode edits ]
                                })
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
