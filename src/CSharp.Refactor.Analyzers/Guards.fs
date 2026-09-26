/// The shared proofs every rewriting rule leans on, so that "is this shape
/// safe" is answered once and the same way everywhere:
///
///   - `isPureExpression`: evaluating it again, or not at all, changes
///     nothing — the question behind every rewrite that drops or
///     duplicates an operand
///   - `callsOnlyCore`: every call in it is to a member known effect-free,
///     so reordering or short-circuiting its evaluation is invisible — the
///     question behind map fusion, `Any` over a flag loop, and the like
///   - `speculativeCheck`: the patched file still binds, and no symbol at
///     an untouched site resolves differently — the resolution class of
///     defect the F# side's 0.8.23 audit found by hand, caught per fix
///
/// Every proof errs toward silence: what it cannot read is impure.
module CSharp.Refactor.Guards

open System
open System.Collections.Concurrent
open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax
open Microsoft.CodeAnalysis.Text

/// The types whose members are known to compute and nothing else.
/// Extension members declared on these types elsewhere are NOT covered:
/// an extension's owner is the type it extends, its body the user's.
let private pureTypes =
    set
        [
            "System.String"
            "System.Math"
            "System.MathF"
            "System.Char"
            "System.Nullable"
            "System.Tuple"
            "System.ValueTuple"
            "System.Linq.Enumerable"
            "System.IO.Path"
            "System.Uri"
            "System.Guid"
            "System.DateTime"
            "System.DateTimeOffset"
            "System.TimeSpan"
            "System.Int32"
            "System.Int64"
            "System.Double"
            "System.Decimal"
            "System.Boolean"
            "System.Enum"
            "System.Convert"
            "System.StringComparer"
            "System.Collections.Generic.KeyValuePair"
        ]

/// Members of the pure types that nonetheless act: the enumeration
/// forcers that run a source's effects, and anything that reads the clock
/// or the environment.
let private effectfulMembers =
    set
        [
            "System.Linq.Enumerable.ToList"
            "System.Linq.Enumerable.ToArray"
            "System.Linq.Enumerable.ToDictionary"
            "System.Linq.Enumerable.ToHashSet"
            "System.Linq.Enumerable.ToLookup"
            "System.DateTime.Now"
            "System.DateTime.UtcNow"
            "System.DateTime.Today"
            "System.DateTimeOffset.Now"
            "System.DateTimeOffset.UtcNow"
            "System.Guid.NewGuid"
            "System.IO.Path.GetTempFileName"
            "System.IO.Path.GetTempPath"
            "System.IO.Path.GetRandomFileName"
            "System.IO.Path.GetFullPath"
        ]

let private fullNameOf (t: INamedTypeSymbol) =
    if isNull t then
        ""
    else
        let ns = t.ContainingNamespace

        if isNull ns || ns.IsGlobalNamespace then
            t.Name
        else
            ns.ToDisplayString() + "." + t.Name

let private ownerName (s: ISymbol) =
    match s.ContainingType with
    | null -> ""
    | t -> fullNameOf t.OriginalDefinition

/// Is the member one of the effectful few (a clock, an enumeration forcer)?
let private isEffectful (s: ISymbol) =
    effectfulMembers.Contains(ownerName s + "." + s.Name)

/// Is the member one of a pure type, and not one of its effectful members?
let private isCoreMember (s: ISymbol) =
    pureTypes.Contains(ownerName s) && not (isEffectful s)

/// A property whose getter is known not to act: a pure type's, a special
/// type's (a primitive's), a tuple element — and never an effectful one.
let private isPureProperty (p: IPropertySymbol) =
    not (isEffectful p)
    && (isCoreMember p
        || p.ContainingType.SpecialType <> SpecialType.None
        || p.ContainingType.IsTupleType)

/// Is an indexer read on this receiver known not to run user code: an
/// array, or a BCL collection's indexer? A type parameter, a pointer, a
/// user type — anything else — is a call, or unknowable.
let private isBclIndexerReceiver (t: ITypeSymbol) =
    match t with
    | null -> false
    | :? IArrayTypeSymbol -> true
    | :? INamedTypeSymbol as n -> (fullNameOf n.OriginalDefinition).StartsWith "System."
    | _ -> false

/// Is the symbol the BCL's own: compiled (not declared in source) into a
/// `System.*` namespace? Its body is no code of the user's.
let isBclSymbol (s: ISymbol) =
    not (isNull s)
    && not (isNull s.ContainingNamespace)
    && s.DeclaringSyntaxReferences.IsEmpty
    && (let ns = s.ContainingNamespace.ToDisplayString()
        ns = "System" || ns.StartsWith "System.")

/// A type whose `Equals`, `GetHashCode` and `CompareTo` are the BCL's own:
/// a string, a primitive, an enum, a BCL value type over such (`Guid`,
/// `DateTime`, `KeyValuePair<string, int>`, `int?`) — never a user type or
/// `object`, whose overrides may act or throw.
let rec isBclElementType (t: ITypeSymbol) =
    not (isNull t)
    && (match t.SpecialType with
        | SpecialType.System_Object -> false
        | SpecialType.None ->
            t.TypeKind = TypeKind.Enum
            || (t.IsValueType
                && isBclSymbol t.OriginalDefinition
                && (match t with
                    | :? INamedTypeSymbol as n -> n.TypeArguments |> Seq.forall isBclElementType
                    | _ -> true))
        | _ -> true)

/// A property whose getter reads a field and nothing else: an auto-property
/// (`{ get; }`, `{ get; set; }`, `{ get; init; }`) or a record's positional
/// one, never virtual, abstract or an interface's (a derived type's getter
/// may compute), nor a partial declaration (its implementation computes).
let isAutoProperty (p: IPropertySymbol) =
    not (isNull p)
    && not (isNull p.GetMethod)
    && not p.IsIndexer
    && not (p.IsAbstract || p.IsVirtual || p.IsExtern || (p.IsOverride && not p.IsSealed))
    && p.ContainingType.TypeKind <> TypeKind.Interface
    && (match p.DeclaringSyntaxReferences |> Seq.tryHead with
        | Some r ->
            match r.GetSyntax() with
            | :? PropertyDeclarationSyntax as d ->
                isNull d.ExpressionBody
                && not (d.Modifiers |> Seq.exists (fun m -> m.IsKind SyntaxKind.PartialKeyword))
                && not (isNull d.AccessorList)
                && d.AccessorList.Accessors
                   |> Seq.forall (fun a -> isNull a.Body && isNull a.ExpressionBody)
            // a record's positional parameter
            | :? ParameterSyntax -> true
            | _ -> false
        | None ->
            p.GetMethod.GetAttributes()
            |> Seq.exists (fun a ->
                not (isNull a.AttributeClass)
                && a.AttributeClass.Name = "CompilerGeneratedAttribute"))

let private symbolOf (model: SemanticModel) (node: SyntaxNode) =
    let info = model.GetSymbolInfo node

    match info.Symbol with
    | null -> ValueNone
    | s -> ValueSome s

/// A read that runs no code of the user's: a local, a parameter, a
/// constant, a `readonly` field, a get-only auto-property of a BCL type
/// (`Length`, `Count`, `HasValue`), a tuple element.
let private isPureRead (model: SemanticModel) (node: SyntaxNode) =
    match symbolOf model node with
    | ValueSome(:? ILocalSymbol)
    | ValueSome(:? IParameterSymbol) -> true
    | ValueSome(:? IFieldSymbol as f) -> f.IsConst || f.IsReadOnly
    | ValueSome(:? IPropertySymbol as p) ->
        // a property getter is a call; only a BCL type's is known not to act
        isPureProperty p
    | ValueSome(:? INamedTypeSymbol) -> true // a type name as a qualifier
    | ValueSome(:? INamespaceSymbol) -> true
    | _ -> false

/// Is the expression typed `dynamic`, whose every operation binds at run
/// time and may call anything?
let private isDynamic (model: SemanticModel) (node: SyntaxNode) =
    match model.GetTypeInfo(node).Type with
    | null -> false
    | t -> t.TypeKind = TypeKind.Dynamic

/// Does an operator application use the BUILT-IN operator? A user-defined
/// one is a call, and a `dynamic` operand binds at run time.
let isBuiltinOperator (model: SemanticModel) (node: SyntaxNode) =
    not (isDynamic model node)
    && (match symbolOf model node with
        | ValueSome(:? IMethodSymbol as m) -> m.MethodKind = MethodKind.BuiltinOperator
        | ValueSome _ -> false
        | ValueNone -> true) // no symbol: a literal comparison the binder folded

let rec private isPurePattern (p: PatternSyntax) =
    match p with
    | :? ConstantPatternSyntax
    | :? DiscardPatternSyntax
    | :? TypePatternSyntax
    | :? DeclarationPatternSyntax
    | :? VarPatternSyntax -> true
    | :? UnaryPatternSyntax as u -> isPurePattern u.Pattern
    | :? BinaryPatternSyntax as b -> isPurePattern b.Left && isPurePattern b.Right
    | :? ParenthesizedPatternSyntax as pp -> isPurePattern pp.Pattern
    | :? RelationalPatternSyntax -> true
    | :? RecursivePatternSyntax as r ->
        (isNull r.PropertyPatternClause
         || r.PropertyPatternClause.Subpatterns
            |> Seq.forall (fun s -> isPurePattern s.Pattern))
        && (isNull r.PositionalPatternClause
            || r.PositionalPatternClause.Subpatterns
               |> Seq.forall (fun s -> isPurePattern s.Pattern))
    | _ -> false

/// Evaluating this expression again — or not at all — changes nothing
/// observable: no call the user wrote runs, nothing is assigned, nothing
/// is awaited. Literals, pure reads and built-in operators over those.
let rec isPureExpression (model: SemanticModel) (e: ExpressionSyntax) : bool =
    match e with
    | :? LiteralExpressionSyntax -> true
    | :? ParenthesizedExpressionSyntax as p -> isPureExpression model p.Expression
    | :? IdentifierNameSyntax
    | :? PredefinedTypeSyntax -> isPureRead model e
    | :? MemberAccessExpressionSyntax as m ->
        isPureRead model m
        && (m.Expression :? ThisExpressionSyntax || isPureExpression model m.Expression)
    | :? ThisExpressionSyntax -> true
    | :? TypeOfExpressionSyntax
    | :? SizeOfExpressionSyntax
    | :? DefaultExpressionSyntax -> true
    | :? InvocationExpressionSyntax as inv ->
        // nameof(x) is not a call
        match inv.Expression with
        | :? IdentifierNameSyntax as id when id.Identifier.Text = "nameof" -> true
        | _ -> false
    | :? PrefixUnaryExpressionSyntax as u ->
        not (
            u.IsKind SyntaxKind.PreIncrementExpression
            || u.IsKind SyntaxKind.PreDecrementExpression
        )
        && isBuiltinOperator model u
        && isPureExpression model u.Operand
    | :? BinaryExpressionSyntax as b ->
        isBuiltinOperator model b
        && isPureExpression model b.Left
        && isPureExpression model b.Right
    | :? ConditionalExpressionSyntax as c ->
        isPureExpression model c.Condition
        && isPureExpression model c.WhenTrue
        && isPureExpression model c.WhenFalse
    | :? TupleExpressionSyntax as t -> t.Arguments |> Seq.forall (fun a -> isPureExpression model a.Expression)
    | :? CastExpressionSyntax as c -> isBuiltinOperator model c && isPureExpression model c.Expression
    | :? IsPatternExpressionSyntax as p -> isPureExpression model p.Expression && isPurePattern p.Pattern
    | :? ElementAccessExpressionSyntax as a ->
        // an array or a BCL indexer reads; a user indexer is a call
        isBclIndexerReceiver (model.GetTypeInfo(a.Expression).Type)
        && isPureExpression model a.Expression
        && a.ArgumentList.Arguments
           |> Seq.forall (fun x -> isPureExpression model x.Expression)
    | _ -> false

/// The constructs no pure body may hold, whatever it calls.
let private isStatementLike (node: SyntaxNode) =
    match node with
    | :? AssignmentExpressionSyntax
    | :? AwaitExpressionSyntax
    | :? ThrowExpressionSyntax
    | :? YieldStatementSyntax
    | :? LockStatementSyntax
    | :? UsingStatementSyntax
    | :? ForEachStatementSyntax
    | :? ForStatementSyntax
    | :? WhileStatementSyntax
    | :? DoStatementSyntax
    | :? TryStatementSyntax
    | :? ThrowStatementSyntax
    | :? ObjectCreationExpressionSyntax
    | :? ImplicitObjectCreationExpressionSyntax
    | :? AnonymousObjectCreationExpressionSyntax
    | :? StackAllocArrayCreationExpressionSyntax -> true
    | :? PostfixUnaryExpressionSyntax as u ->
        u.IsKind SyntaxKind.PostIncrementExpression
        || u.IsKind SyntaxKind.PostDecrementExpression
    | :? PrefixUnaryExpressionSyntax as u ->
        u.IsKind SyntaxKind.PreIncrementExpression
        || u.IsKind SyntaxKind.PreDecrementExpression
    | :? InterpolatedStringExpressionSyntax as s ->
        // a hole is formatted by ITS type's ToString: a user override,
        // run once per element in a different order
        s.Contents |> Seq.exists (fun c -> c :? InterpolationSyntax)
    | _ -> false

/// Does every CALL inside this expression resolve to a member known to
/// compute and nothing else — a pure type's method or property, a
/// lambda of the same, a tuple or record construction — with no
/// statement-shaped construct anywhere in it? A user function is opaque
/// and fails the proof; so does a computed property of a user type (a
/// getter runs its body; an auto-property's only reads its field) and
/// `Lazy<T>.Value` (which forces); so does a `dynamic`
/// operand, whose call binds at run time.
let rec callsOnlyCore (model: SemanticModel) (e: SyntaxNode) : bool =
    let self = e

    let nodes = self.DescendantNodesAndSelf() |> List.ofSeq

    if nodes |> List.exists isStatementLike then
        false
    else
        nodes
        |> List.forall (fun n ->
            match n with
            | :? InvocationExpressionSyntax as inv ->
                match inv.Expression with
                | :? IdentifierNameSyntax as id when id.Identifier.Text = "nameof" -> true
                | _ ->
                    match symbolOf model inv with
                    | ValueSome(:? IMethodSymbol as m) ->
                        isCoreMember m.OriginalDefinition && (m.ReturnType.TypeKind <> TypeKind.Dynamic)
                    | _ -> false
            | :? MemberAccessExpressionSyntax as m ->
                match symbolOf model m with
                | ValueSome(:? IPropertySymbol as p) ->
                    let owner = ownerName p

                    isPureProperty p
                    // an auto-property's getter reads its field, as a field read does
                    || isAutoProperty p
                    || (owner = "System.Collections.Generic.List" && p.Name = "Count")
                    || (owner = "System.Collections.Generic.Dictionary" && p.Name = "Count")
                    || (p.ContainingType.TypeKind = TypeKind.Array)
                | ValueSome(:? IFieldSymbol) -> true // a field read runs no code of anyone's
                | ValueSome(:? IMethodSymbol as meth) ->
                    // a method group handed on: pure only if the method is
                    isCoreMember meth.OriginalDefinition
                | ValueSome(:? ILocalSymbol)
                | ValueSome(:? IParameterSymbol)
                | ValueSome(:? INamedTypeSymbol)
                | ValueSome(:? INamespaceSymbol) -> true
                | ValueSome(:? IEventSymbol) -> false
                | ValueSome _ -> false
                | ValueNone -> false
            | :? IdentifierNameSyntax as id when
                not (id.Parent :? MemberAccessExpressionSyntax)
                && not (id.Parent :? InvocationExpressionSyntax)
                ->
                match symbolOf model id with
                | ValueSome(:? IPropertySymbol as p) -> isPureProperty p || isAutoProperty p
                | ValueSome(:? IFieldSymbol) -> true
                | ValueSome(:? IDynamicTypeSymbol) -> false
                | _ -> true
            | :? BinaryExpressionSyntax as b -> isBuiltinOperator model b
            | :? PrefixUnaryExpressionSyntax as u -> isBuiltinOperator model u
            | :? CastExpressionSyntax as c -> isBuiltinOperator model c
            | :? ElementAccessExpressionSyntax as a -> isBclIndexerReceiver (model.GetTypeInfo(a.Expression).Type)
            | _ -> true)

/// Is a lambda (or method group) a function whose calls are provably
/// effect-free? The question map fusion asks of both mappers.
let isPureFunction (model: SemanticModel) (f: ExpressionSyntax) : bool =
    match f with
    | :? LambdaExpressionSyntax as l ->
        match l.Body with
        | :? ExpressionSyntax as body -> callsOnlyCore model body
        | _ -> false
    | :? IdentifierNameSyntax
    | :? MemberAccessExpressionSyntax ->
        match symbolOf model f with
        | ValueSome(:? IMethodSymbol as m) -> isCoreMember m.OriginalDefinition
        | _ -> false
    | _ -> false

/// Does the node sit inside a lambda converted to an expression tree, or
/// a query expression over an `IQueryable`? There the shape is what a
/// provider translates.
let insideExpressionTree (model: SemanticModel) (node: SyntaxNode) =
    node.Ancestors()
    |> Seq.exists (fun a ->
        match a with
        | :? LambdaExpressionSyntax as lambda ->
            let t = model.GetTypeInfo(lambda).ConvertedType

            not (isNull t)
            && t.Name = "Expression"
            && t.ContainingNamespace.ToDisplayString() = "System.Linq.Expressions"
        | :? QueryExpressionSyntax as q ->
            let t = model.GetTypeInfo(q.FromClause.Expression).Type

            not (isNull t)
            && (t.Name = "IQueryable"
                || t.AllInterfaces |> Seq.exists (fun i -> i.Name = "IQueryable"))
        | _ -> false)

/// Is the node inside an attribute argument, where an expression must
/// stay a constant?
let insideAttribute (node: SyntaxNode) =
    node.Ancestors() |> Seq.exists (fun a -> a :? AttributeArgumentSyntax)

/// Where a PRIVATE member can be named in a tree: its containing type's
/// declarations there (each partial part, with the types nested in it), in
/// document order. C# lets nothing outside those spans reach a private
/// member, so walking them finds every use a walk of the whole tree finds,
/// in the same order - at the cost of the type, not of the file.
let privateMemberScope (tree: SyntaxTree) (s: ISymbol) : SyntaxNode list =
    s.ContainingType.DeclaringSyntaxReferences
    |> Seq.filter (fun r -> r.SyntaxTree = tree)
    |> Seq.map (fun r -> r.GetSyntax())
    |> Seq.sortBy (fun n -> n.SpanStart)
    |> List.ofSeq

/// Errors the tree holds today, by their message and line, so a patched
/// tree can be judged on NEW errors only.
let private errorKeys (model: SemanticModel) =
    model.GetDiagnostics()
    |> Seq.filter (fun d -> d.Severity = DiagnosticSeverity.Error)
    |> Seq.map (fun d -> d.Id)
    |> Seq.countBy id
    |> Map.ofSeq

/// The speculative check: apply the edits to a copy of the tree, re-bind
/// it in a forked compilation, and answer whether the patched file
/// introduces NO new error. Every fix that can change name or overload
/// resolution asks this before it is offered; a semantic guard is never
/// replaced by it, since a rewrite can compile and mean something else.
/// The check with a list of error ids the patched tree may carry — an
/// error a source generator will resolve once it runs (a partial method
/// under `[GeneratedRegex]` has no body until then).
/// The whole file's error counts of an original model, once per model:
/// every check of a file compares against the same counts.
let private fileErrorKeys =
    System.Runtime.CompilerServices.ConditionalWeakTable<SemanticModel, Map<string, int>>()

/// Errors inside the given spans only, by id.
let private spanErrorKeys (model: SemanticModel) (spans: TextSpan list) =
    spans
    |> Seq.collect (fun span -> model.GetDiagnostics(Nullable span))
    |> Seq.filter (fun d -> d.Severity = DiagnosticSeverity.Error)
    |> Seq.map (fun d -> d.Id)
    |> Seq.countBy id
    |> Map.ofSeq

/// The member whose BODY strictly holds `span`: inside a method's,
/// constructor's, operator's or accessor's braces, or inside the
/// expression of an expression body. None for a signature, an initializer,
/// an attribute, a top-level statement, a type, or the braces themselves.
let private bodyMemberOf (root: SyntaxNode) (span: TextSpan) : MemberDeclarationSyntax option =
    let insideBlock (b: BlockSyntax) =
        not (isNull b)
        && span.Start > b.OpenBraceToken.SpanStart
        && span.End <= b.CloseBraceToken.SpanStart

    let insideArrow (a: ArrowExpressionClauseSyntax) =
        not (isNull a) && a.Expression.Span.Contains span

    if span.End > root.FullSpan.End then
        None
    else
        root.FindToken(span.Start).Parent.AncestorsAndSelf()
        |> Seq.tryPick (fun n ->
            match n with
            | :? BaseMethodDeclarationSyntax as m ->
                Some(
                    if insideBlock m.Body || insideArrow m.ExpressionBody then
                        Some(m :> MemberDeclarationSyntax)
                    else
                        None
                )
            | :? AccessorDeclarationSyntax as a ->
                match a.Parent with
                | :? AccessorListSyntax as list when (list.Parent :? BasePropertyDeclarationSyntax) ->
                    Some(
                        if insideBlock a.Body || insideArrow a.ExpressionBody then
                            Some(list.Parent :?> MemberDeclarationSyntax)
                        else
                            None
                    )
                | _ -> Some None
            | :? PropertyDeclarationSyntax as p ->
                Some(
                    if insideArrow p.ExpressionBody then
                        Some(p :> MemberDeclarationSyntax)
                    else
                        None
                )
            | :? IndexerDeclarationSyntax as i ->
                Some(
                    if insideArrow i.ExpressionBody then
                        Some(i :> MemberDeclarationSyntax)
                    else
                        None
                )
            | :? MemberDeclarationSyntax
            | :? GlobalStatementSyntax -> Some None
            | _ -> None)
        |> Option.flatten

let private hasErrors (diagnostics: Diagnostic seq) =
    diagnostics |> Seq.exists (fun d -> d.Severity = DiagnosticSeverity.Error)

/// The speculative check confined to the members an edit set touches -
/// None where that is not known to answer as the whole file would.
///
/// An edit inside a member's BODY cannot change a diagnostic anywhere
/// else: a body declares nothing another member binds against, so every
/// other member's errors are the same before and after, and "no error id
/// counts more in the patched file" is "no error id counts more inside the
/// touched members". Binding those members instead of the whole file is
/// what turns a check per candidate from the file's cost into the
/// member's. The conditions that make the locality hold, each checked,
/// else the whole-file check answers: every edit strictly inside a body
/// (never its braces, a signature or an initializer); no preprocessor text
/// in or around an edit; no syntax error in the original or the patched
/// tree (a recovery could re-parse what follows); and each touched member
/// found again at its span shifted by the edits before it, every
/// declaration around it too - an inserted `} void X() {` ends the member
/// early, and is caught there.
let private memberLocalCheck
    (allowed: string list)
    (model: SemanticModel)
    (tree: SyntaxTree)
    (patched: SyntaxTree)
    (own: TextEdit list)
    : bool option =
    let root = tree.GetRoot()
    let text = tree.GetText()

    let noDirective =
        own
        |> List.forall (fun e ->
            not (e.Replacement.Contains "#")
            && e.Span.End <= text.Length
            && not (text.ToString(e.Span).Contains "#"))

    if own.IsEmpty || not noDirective then
        None
    else
        let members = own |> List.map (fun e -> bodyMemberOf root e.Span)

        if members |> List.exists Option.isNone then
            None
        elif hasErrors (tree.GetDiagnostics()) || hasErrors (patched.GetDiagnostics()) then
            None
        else
            let patchedRoot = patched.GetRoot()

            (
             // where an original position lands once the edits before it apply
             let shifted (position: int) =
                 position
                 + (own
                    |> List.sumBy (fun e ->
                        if e.Span.End <= position then
                            e.Replacement.Length - e.Span.Length
                        else
                            0))

             let shiftedSpan (s: TextSpan) =
                 TextSpan.FromBounds(shifted s.Start, shifted s.End)

             let touched = members |> List.choose id |> List.distinctBy (fun m -> m.Span)

             // the member found again, and every declaration around it with
             // it: each of the same kind at its own span shifted. The text
             // outside the member is unchanged, and a parser that closes the
             // member and every enclosing construct where it did before goes
             // on exactly as it went, so the rest of the tree is the same -
             // where an inserted `} void X() {` ends the member early, and
             // fails here
             let sameShape (m: SyntaxNode) (again: SyntaxNode) =
                 let before = m.AncestorsAndSelf() |> List.ofSeq
                 let after = again.AncestorsAndSelf() |> List.ofSeq

                 before.Length = after.Length
                 && List.forall2
                     (fun (b: SyntaxNode) (a: SyntaxNode) -> b.RawKind = a.RawKind && shiftedSpan b.Span = a.Span)
                     before
                     after

             let found =
                 touched
                 |> List.map (fun m ->
                     let span = shiftedSpan m.Span

                     let again =
                         if span.End > patchedRoot.FullSpan.End then
                             None
                         else
                             patchedRoot.FindNode(span).AncestorsAndSelf()
                             |> Seq.tryFind (fun n -> n.Span = span && n.RawKind = m.RawKind)
                             |> Option.filter (sameShape m)

                     m.Span, again |> Option.map (fun n -> n.Span))

             if found |> List.exists (fun (_, again) -> again.IsNone) then
                 None
             else
                 let compilation = model.Compilation.ReplaceSyntaxTree(tree, patched)
                 let newModel = compilation.GetSemanticModel(patched, false)
                 let before = spanErrorKeys model (found |> List.map fst)
                 let after = spanErrorKeys newModel (found |> List.choose snd)

                 Some(
                     after
                     |> Map.forall (fun id n ->
                         List.contains id allowed
                         || (match Map.tryFind id before with
                             | Some m -> n <= m
                             | None -> false))
                 ))

/// Set CSR_SPEC_VERIFY to a file path and every check runs both ways: a
/// member-local answer that differs from the whole file's is written there
/// (and the whole file's answer is used), so a test run proves the two agree.
let private verifyLog =
    lazy (Environment.GetEnvironmentVariable "CSR_SPEC_VERIFY" |> Option.ofObj)

let speculativeCheckAllowing (allowed: string list) (model: SemanticModel) (edits: TextEdit list) : bool =
    try
        let tree = model.SyntaxTree
        let text = tree.GetText()

        // only this file's edits: a cross-file fix checks each site with its own model
        let own =
            edits
            |> List.filter (fun e ->
                match e.File with
                | None -> true
                | Some f -> String.Equals(f, tree.FilePath, StringComparison.OrdinalIgnoreCase))

        // in text order: the patch takes its changes sorted, and a batch of
        // candidates comes candidate by candidate
        let changes =
            own
            |> List.sortBy (fun e -> e.Span.Start)
            |> List.map (fun e -> TextChange(e.Span, e.Replacement))

        let patched = tree.WithChangedText(text.WithChanges changes)

        let wholeFile () =
            let compilation = model.Compilation.ReplaceSyntaxTree(tree, patched)
            let newModel = compilation.GetSemanticModel(patched, false)
            let before = fileErrorKeys.GetValue(model, errorKeys)
            let after = errorKeys newModel

            after
            |> Map.forall (fun id n ->
                List.contains id allowed
                || (match Map.tryFind id before with
                    | Some m -> n <= m
                    | None -> false))

        let local =
            try
                memberLocalCheck allowed model tree patched own
            with _ -> // an unexpected shape answers the whole-file way; fsharpanalyzer: ignore-line FR0055
                None

        match local, verifyLog.Value with
        | Some answer, None -> answer
        | None, _ -> wholeFile ()
        | Some answer, Some log ->
            let full = wholeFile ()

            // every comparison is a line: "same" ones prove the local path
            // ran, a "DIFF" one that it answered otherwise
            let line =
                if answer = full then
                    $"same {answer}\n"
                else
                    let edited =
                        own |> List.map (fun e -> $"{e.Span}={e.Replacement}") |> String.concat " | "

                    $"DIFF {tree.FilePath}: local {answer}, whole file {full}: {edited}\n"

            // one file per process (test projects run side by side), and a
            // failed write never turns into the check's answer
            lock verifyLog (fun () ->
                try
                    IO.File.AppendAllText($"{log}.{Diagnostics.Process.GetCurrentProcess().Id}", line)
                with _ -> // the log is a test instrument; fsharpanalyzer: ignore-line FR0055
                    ())

            full
    with _ -> // a check that cannot run offers no fix; fsharpanalyzer: ignore-line FR0055
        false

let speculativeCheck (model: SemanticModel) (edits: TextEdit list) : bool = speculativeCheckAllowing [] model edits

/// The speculative check over many candidates of one file at once: the
/// candidates whose edits pass. All are patched into one fork first — a
/// fork is a re-bind of the file, and a file of forty properties checked
/// one by one is forty re-binds — and only when that fork carries a new
/// error is each candidate tried alone. Sound for independent edits: an
/// edit that errors alone errors with the rest in place too, so a clean
/// batch clears every member; a batch that fails may hold one bad edit
/// among good ones, and the retry finds which.
let speculativeCheckEach (model: SemanticModel) (candidates: ('a * TextEdit list) list) : 'a list =
    match candidates with
    | [] -> []
    | [ (candidate, edits) ] -> if speculativeCheck model edits then [ candidate ] else []
    | _ ->
        if speculativeCheck model (candidates |> List.collect snd) then
            candidates |> List.map fst
        else
            candidates
            |> List.filter (fun (_, edits) -> speculativeCheck model edits)
            |> List.map fst

/// The speculative check for a cross-file edit set: every touched tree of
/// the SAME compilation is patched into one fork, so a renamed method and
/// its rewritten callers are judged together; the error counts of the
/// whole compilation may not rise. A site in another compilation (another
/// project) is left to the host's own verification.
let speculativeCheckAcross (model: SemanticModel) (sites: ReferenceSite list) (edits: TextEdit list) : bool =
    try
        let compilation = model.Compilation

        // the errors of the touched trees only: every reference the set rewrites
        // sits in one of them (the rules vetoed the rest), and a whole-compilation
        // check would recompile the project per fix
        let errorCounts (c: Compilation) (trees: SyntaxTree list) =
            trees
            |> Seq.collect (fun t -> c.GetSemanticModel(t, false).GetDiagnostics())
            |> Seq.filter (fun d -> d.Severity = DiagnosticSeverity.Error)
            |> Seq.map (fun d -> d.Id)
            |> Seq.countBy id
            |> Map.ofSeq

        let ownPath = model.SyntaxTree.FilePath

        let sameFile (a: string) (b: string) =
            String.Equals(a, b, StringComparison.OrdinalIgnoreCase)

        let trees =
            (ownPath, model.SyntaxTree)
            :: (sites
                // a site whose tree is one of this compilation's (its model may be
                // a re-optioned twin of it)
                |> List.filter (fun s -> not (isNull s.Tree) && compilation.ContainsSyntaxTree s.Tree)
                |> List.map (fun s -> s.Tree.FilePath, s.Tree)
                |> List.distinctBy (fun (p, _) -> p.ToLowerInvariant()))

        let patched, patchedTrees =
            trees
            |> List.fold
                (fun (c: Compilation, ts: SyntaxTree list) (path, tree: SyntaxTree) ->
                    let own =
                        edits
                        |> List.filter (fun e ->
                            match e.File with
                            | None -> sameFile path ownPath
                            | Some f -> sameFile f path)

                    if own.IsEmpty then
                        c, tree :: ts
                    else
                        let text = tree.GetText()
                        let changes = own |> List.map (fun e -> TextChange(e.Span, e.Replacement))
                        let newTree = tree.WithChangedText(text.WithChanges changes)
                        c.ReplaceSyntaxTree(tree, newTree), newTree :: ts)
                (compilation, [])

        let before = errorCounts compilation (trees |> List.map snd)
        let after = errorCounts patched patchedTrees

        after
        |> Map.forall (fun id n ->
            match Map.tryFind id before with
            | Some m -> n <= m
            | None -> false)
    with _ -> // a cross-file check that cannot run offers no fix; fsharpanalyzer: ignore-line FR0055
        false

/// Does the call an edited argument sits in still bind to the same member
/// once the edits are in? An interpolated string converts to more than a
/// `string` does — `FormattableString`, `IFormattable`, an interpolated
/// string handler — so `Execute($"…")` can pick another overload than
/// `Execute("…" + x)` did (EF's `ExecuteSqlCommand(FormattableString)`
/// over `(RawSqlString)`: the holes become parameters, a table name among
/// them breaks the statement). The edits must all lie inside the call, so
/// its start does not move.
let bindingKept (model: SemanticModel) (edits: TextEdit list) (call: SyntaxNode) : bool =
    try
        let tree = model.SyntaxTree
        let text = tree.GetText()

        let own =
            edits
            |> List.filter (fun e ->
                match e.File with
                | None -> true
                | Some f -> String.Equals(f, tree.FilePath, StringComparison.OrdinalIgnoreCase))

        if own |> List.exists (fun e -> e.Span.Start < call.SpanStart) then
            false
        else
            let changes = own |> List.map (fun e -> TextChange(e.Span, e.Replacement))
            let patched = tree.WithChangedText(text.WithChanges changes)
            let compilation = model.Compilation.ReplaceSyntaxTree(tree, patched)
            let newModel = compilation.GetSemanticModel(patched, false)

            let twin =
                patched.GetRoot().FindToken(call.SpanStart).Parent.AncestorsAndSelf()
                |> Seq.tryFind (fun n -> n.SpanStart = call.SpanStart && n.GetType() = call.GetType())

            let spelled (s: ISymbol) =
                if isNull s then
                    ""
                else
                    s.OriginalDefinition.ToDisplayString()

            match twin with
            | None -> false
            | Some t -> spelled (model.GetSymbolInfo(call).Symbol) = spelled (newModel.GetSymbolInfo(t).Symbol)
    with _ -> // a binding that cannot be compared counts as changed; fsharpanalyzer: ignore-line FR0055
        false

/// The call an expression is an argument of, through parentheses: an
/// invocation, an object creation or an element access.
[<TailCall>]
let rec enclosingCall (e: SyntaxNode) : SyntaxNode option =
    match e.Parent with
    | :? ParenthesizedExpressionSyntax as p -> enclosingCall p
    | :? ArgumentSyntax as a ->
        match a.Parent with
        | :? ArgumentListSyntax as al -> Some al.Parent
        | :? BracketedArgumentListSyntax as al -> Some al.Parent
        | _ -> None
    | _ -> None

/// The first fix's edits pass the speculative check, or the suggestion
/// loses its fixes and stays a note.
let verified (model: SemanticModel) (s: Suggestion) : Suggestion =
    let survive =
        s.Fixes |> List.filter (fun f -> f.EditorOnly || speculativeCheck model f.Edits)

    { s with Fixes = survive }

/// A type's spelling at a position: its bare name where a `using` brings
/// it in scope, else the qualified name.
let typeText (model: SemanticModel) (position: int) (ns: string) (name: string) =
    let resolves =
        model.LookupNamespacesAndTypes(position, name = name)
        |> Seq.exists (fun s -> s.ToDisplayString() = $"{ns}.{name}")

    if resolves then name else $"{ns}.{name}"

/// May `!(a < b)` become `a >= b` with this operand? Not for `float`/
/// `double`/`Half` (NaN compares false both ways), not for a `Nullable<T>`
/// (a lifted comparison is false both ways on null), not for `dynamic`,
/// and not without a type.
let orderingFlipSafe (model: SemanticModel) (e: ExpressionSyntax) =
    match model.GetTypeInfo(e).Type with
    | null -> false
    | t ->
        match t.SpecialType with
        | SpecialType.System_Single
        | SpecialType.System_Double -> false
        | _ ->
            t.Name <> "Half"
            && t.TypeKind <> TypeKind.Dynamic
            && t.OriginalDefinition.SpecialType <> SpecialType.System_Nullable_T

/// The same variable or member as another spelling of it — by symbol, not
/// by text: a lambda parameter named like the receiver is another thing,
/// and `x.Value` under it must not be rewritten with the outer `x`.
let sameReference (model: SemanticModel) (a: ExpressionSyntax) (b: ExpressionSyntax) =
    a.ToString() = b.ToString()
    && (let sa = model.GetSymbolInfo(a).Symbol
        let sb = model.GetSymbolInfo(b).Symbol
        (isNull sa && isNull sb) || SymbolEqualityComparer.Default.Equals(sa, sb))

let private candidateVerdicts =
    System.Runtime.CompilerServices.ConditionalWeakTable<SemanticModel, ConcurrentDictionary<string, bool>>()

/// Is every method of this name callable on a receiver of this type at the
/// position — its own members and the extension methods in scope — the
/// BCL's? A fix that spells `xs.Any(…)` for `Enumerable.Any` would call a
/// repository's own extension with a more specific receiver instead.
let onlyBclCandidates (model: SemanticModel) (position: int) (receiver: ITypeSymbol) (name: string) =
    not (isNull receiver)
    && (
        // the lookup walks every extension method in scope: once per receiver
        // type, name and namespace scope (what `using`s see) of a file
        let scope =
            match model.SyntaxTree.GetRoot().FindToken(position).Parent with
            | null -> -1
            | parent ->
                parent.AncestorsAndSelf()
                |> Seq.tryPick (fun n ->
                    match n with
                    | :? BaseNamespaceDeclarationSyntax -> Some n.SpanStart
                    | _ -> None)
                |> Option.defaultValue -1

        let key =
            $"{receiver.ToDisplayString SymbolDisplayFormat.FullyQualifiedFormat}|{name}|{scope}"

        candidateVerdicts
            .GetValue(model, fun _ -> ConcurrentDictionary<string, bool>())
            .GetOrAdd(key, fun _ -> model.LookupSymbols(position, receiver, name, true) |> Seq.forall isBclSymbol))

/// The bodies a symbol declares in this compilation — a method's, a local
/// function's, an accessor's, a constructor's, an operator's or a lambda's —
/// each with the semantic model of its tree. None for metadata, an
/// interface's or abstract member, a delegate's `Invoke`, a field or a
/// parameter: what cannot be seen is not detected.
let visibleBodies (model: SemanticModel) (s: ISymbol) : (SemanticModel * SyntaxNode) list =
    if isNull s then
        []
    else
        let s =
            match s with
            | :? IMethodSymbol as m when not (isNull m.ReducedFrom) -> m.ReducedFrom :> ISymbol
            | _ -> s.OriginalDefinition

        let accessors (list: AccessorListSyntax) : SyntaxNode list =
            if isNull list then
                []
            else
                list.Accessors
                |> Seq.collect (fun a -> [ a.Body :> SyntaxNode; a.ExpressionBody :> SyntaxNode ])
                |> List.ofSeq

        s.DeclaringSyntaxReferences
        |> Seq.collect (fun r ->
            let node = r.GetSyntax()
            let tree = node.SyntaxTree

            if not (model.Compilation.ContainsSyntaxTree tree) then
                []
            else
                let m =
                    if obj.ReferenceEquals(tree, model.SyntaxTree) then
                        model
                    else
                        model.Compilation.GetSemanticModel(tree, false)

                let bodies: SyntaxNode list =
                    match node with
                    | :? BaseMethodDeclarationSyntax as d -> [ d.Body :> SyntaxNode; d.ExpressionBody :> SyntaxNode ]
                    | :? LocalFunctionStatementSyntax as d -> [ d.Body :> SyntaxNode; d.ExpressionBody :> SyntaxNode ]
                    | :? PropertyDeclarationSyntax as d -> (d.ExpressionBody :> SyntaxNode) :: accessors d.AccessorList
                    | :? IndexerDeclarationSyntax as d -> (d.ExpressionBody :> SyntaxNode) :: accessors d.AccessorList
                    | :? EventDeclarationSyntax as d -> accessors d.AccessorList
                    | :? AccessorDeclarationSyntax as a -> [ a.Body :> SyntaxNode; a.ExpressionBody :> SyntaxNode ]
                    // an expression-bodied property's getter declares itself as the arrow
                    | :? ArrowExpressionClauseSyntax as a -> [ a ]
                    | :? AnonymousFunctionExpressionSyntax as l -> [ l.Body ]
                    | _ -> []

                bodies |> List.filter (isNull >> not) |> List.map (fun b -> m, b))
        |> List.ofSeq

let private handlerCache =
    System.Runtime.CompilerServices.ConditionalWeakTable<
        Compilation,
        ConcurrentDictionary<ISymbol, (SemanticModel * ExpressionSyntax) list>
     >()

/// Where a delegate, a lazy or a stored sequence came from: the value's
/// initializer, the assignments to it in the member that reads it, and for
/// an event every handler `+=`'d to it anywhere in the compilation — the
/// expressions, each with its model, so that a caller may look at what they
/// touch as well as at what they run.
let originsOf (model: SemanticModel) (e: ExpressionSyntax) : (SemanticModel * ExpressionSyntax) list =
    let modelFor (tree: SyntaxTree) =
        if obj.ReferenceEquals(tree, model.SyntaxTree) then
            model
        else
            model.Compilation.GetSemanticModel(tree, false)

    match symbolOf model e with
    | ValueSome(:? IEventSymbol as ev) ->
        let cache =
            handlerCache.GetValue(
                model.Compilation,
                fun _ ->
                    ConcurrentDictionary<ISymbol, (SemanticModel * ExpressionSyntax) list>(
                        SymbolEqualityComparer.Default
                    )
            )

        cache.GetOrAdd(
            ev,
            fun _ ->
                model.Compilation.SyntaxTrees
                |> Seq.collect (fun tree ->
                    let m = modelFor tree

                    tree.GetRoot().DescendantNodes()
                    |> Seq.choose (fun n ->
                        match n with
                        | :? AssignmentExpressionSyntax as a when
                            a.IsKind SyntaxKind.AddAssignmentExpression
                            && SymbolEqualityComparer.Default.Equals(m.GetSymbolInfo(a.Left).Symbol, ev)
                            ->
                            Some(m, a.Right)
                        | _ -> None))
                |> List.ofSeq
        )
    | ValueSome((:? ILocalSymbol | :? IFieldSymbol | :? IPropertySymbol | :? IParameterSymbol) as v) ->
        let initializers =
            v.DeclaringSyntaxReferences
            |> Seq.choose (fun r ->
                match r.GetSyntax() with
                | :? VariableDeclaratorSyntax as d when not (isNull d.Initializer) ->
                    Some(modelFor d.SyntaxTree, d.Initializer.Value)
                | :? PropertyDeclarationSyntax as p when not (isNull p.Initializer) ->
                    Some(modelFor p.SyntaxTree, p.Initializer.Value)
                | _ -> None)
            |> List.ofSeq

        let assigned =
            (Text.enclosingMember e).DescendantNodes()
            |> Seq.choose (fun n ->
                match n with
                | :? AssignmentExpressionSyntax as a when
                    a.IsKind SyntaxKind.SimpleAssignmentExpression
                    && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(a.Left).Symbol, v)
                    ->
                    Some(model, a.Right)
                | _ -> None)
            |> List.ofSeq

        initializers @ assigned
    | _ -> []

/// What an origin expression runs: a lambda's own symbol, a method group's
/// method, a call's target, a constructor — and the lambdas or methods a
/// constructor is handed (`new Lazy<T>(() => …)`, `new Thread(Run)`).
let originSymbols (m: SemanticModel) (value: ExpressionSyntax) : ISymbol list =
    match value with
    | :? BaseObjectCreationExpressionSyntax as c ->
        let handed =
            if isNull c.ArgumentList then
                []
            else
                c.ArgumentList.Arguments
                |> Seq.collect (fun a -> symbolOf m a.Expression |> ValueOption.toList)
                |> List.ofSeq

        (symbolOf m value |> ValueOption.toList) @ handed
    | _ -> symbolOf m value |> ValueOption.toList

/// The delegate a `d(x)`, `d.Invoke(x)` or `d?.Invoke(x)` invokes.
let delegateReceiver (inv: InvocationExpressionSyntax) : ExpressionSyntax =
    match inv.Expression with
    | :? MemberAccessExpressionSyntax as ma when ma.Name.Identifier.ValueText = "Invoke" -> ma.Expression
    | :? MemberBindingExpressionSyntax ->
        match inv.Parent with
        | :? ConditionalAccessExpressionSyntax as ca -> ca.Expression
        | _ -> inv.Expression
    | e -> e

/// The symbols a node itself runs (not its descendants'): the target of a
/// call or a creation, a property's accessors, an indexer, a user operator,
/// conversion or `Deconstruct`, a `foreach`'s enumerator members, a method
/// group handed on — and for a delegate invoked, or a sequence enumerated,
/// what it visibly came from.
let calleesOfNode (m: SemanticModel) (n: SyntaxNode) : ISymbol list =
    let symbols (x: SyntaxNode) = symbolOf m x |> ValueOption.toList

    let originsRun (e: ExpressionSyntax) =
        originsOf m e |> List.collect (fun (om, v) -> originSymbols om v)

    let own =
        match n with
        | :? InvocationExpressionSyntax as inv ->
            match symbolOf m inv with
            | ValueSome(:? IMethodSymbol as meth) when meth.MethodKind = MethodKind.DelegateInvoke ->
                originsRun (delegateReceiver inv)
            | ValueSome s -> [ s ]
            | ValueNone -> []
        | :? BaseObjectCreationExpressionSyntax as c -> originSymbols m c
        | :? ElementAccessExpressionSyntax
        | :? IdentifierNameSyntax
        | :? MemberAccessExpressionSyntax
        | :? MemberBindingExpressionSyntax ->
            match symbolOf m n with
            | ValueSome(:? IPropertySymbol as p) -> [ p ]
            // a method group handed on runs wherever it is called
            | ValueSome(:? IMethodSymbol as meth) when not (n.Parent :? InvocationExpressionSyntax) -> [ meth ]
            | _ -> []
        | :? BinaryExpressionSyntax
        | :? PrefixUnaryExpressionSyntax
        | :? PostfixUnaryExpressionSyntax
        | :? CastExpressionSyntax ->
            match symbolOf m n with
            | ValueSome(:? IMethodSymbol as meth) -> [ meth ]
            | _ -> []
        | :? AssignmentExpressionSyntax as a ->
            let operator =
                match symbolOf m a with
                | ValueSome(:? IMethodSymbol as meth) -> [ meth :> ISymbol ]
                | _ -> []

            let deconstruct =
                if (a.Left :? TupleExpressionSyntax) || (a.Left :? DeclarationExpressionSyntax) then
                    match m.GetDeconstructionInfo(a).Method with
                    | null -> []
                    | d -> [ d :> ISymbol ]
                else
                    []

            operator @ deconstruct
        | :? CommonForEachStatementSyntax as f ->
            let info = m.GetForEachStatementInfo f

            let deconstruct =
                match f with
                | :? ForEachVariableStatementSyntax as v ->
                    match m.GetDeconstructionInfo(v).Method with
                    | null -> []
                    | d -> [ d :> ISymbol ]
                | _ -> []

            ([
                info.GetEnumeratorMethod :> ISymbol
                info.MoveNextMethod
                info.CurrentProperty
                info.DisposeMethod
             ]
             |> List.filter (isNull >> not))
            @ deconstruct
            @ originsRun f.Expression
        | _ -> []

    // a user-defined conversion on the way to wherever the value goes
    let converted =
        match n with
        | :? ExpressionSyntax as x ->
            let c = m.GetConversion x

            if c.IsUserDefined && not (isNull c.MethodSymbol) then
                [ c.MethodSymbol :> ISymbol ]
            else
                []
        | _ -> []

    own @ converted

/// The symbols a body runs, each once.
let calleesOf (m: SemanticModel) (body: SyntaxNode) : ISymbol list =
    let seen =
        System.Collections.Generic.HashSet<ISymbol>(SymbolEqualityComparer.Default)

    body.DescendantNodesAndSelf()
    |> Seq.collect (calleesOfNode m)
    |> Seq.filter seen.Add
    |> List.ofSeq

/// Does anything `roots` run — followed through the bodies this
/// compilation holds, `depth` calls deep — have a body `test` accepts? A
/// body that cannot be seen (metadata, an interface's or abstract member,
/// a delegate of unknown origin) satisfies nothing.
let reachesThrough
    (model: SemanticModel)
    (depth: int)
    (test: SemanticModel -> SyntaxNode -> bool)
    (roots: ISymbol list)
    : bool =
    let visited =
        System.Collections.Generic.HashSet<ISymbol>(SymbolEqualityComparer.Default)

    let rec go (depth: int) (s: ISymbol) =
        not (isNull s)
        && visited.Add s
        && visibleBodies model s
           |> List.exists (fun (m, body) ->
               test m body || (depth > 0 && calleesOf m body |> List.exists (go (depth - 1))))

    roots |> List.exists (go depth)

/// A getter whose visible body answers each read differently: it assigns,
/// counts (`calls++`), constructs or awaits. One that cannot be seen: no.
let unstableGetter (model: SemanticModel) (p: IPropertySymbol) =
    let getter: ISymbol = if isNull p.GetMethod then p else p.GetMethod

    visibleBodies model getter
    |> List.exists (fun (_, body) ->
        body.DescendantNodesAndSelf()
        |> Seq.exists (fun n ->
            n :? AssignmentExpressionSyntax
            || n.IsKind SyntaxKind.PostIncrementExpression
            || n.IsKind SyntaxKind.PostDecrementExpression
            || n.IsKind SyntaxKind.PreIncrementExpression
            || n.IsKind SyntaxKind.PreDecrementExpression
            || n :? BaseObjectCreationExpressionSyntax
            || n :? ArrayCreationExpressionSyntax
            || n :? ImplicitArrayCreationExpressionSyntax
            || n :? CollectionExpressionSyntax
            || n :? AwaitExpressionSyntax))

/// Can the arithmetic overflow-check at this site: a `checked` block or
/// expression around it, or a project compiled checked?
let private checkedAt (model: SemanticModel) (node: SyntaxNode) =
    (match model.Compilation.Options with
     | :? CSharpCompilationOptions as o -> o.CheckOverflow
     | _ -> true)
    || node.Ancestors()
       |> Seq.exists (fun a -> a.IsKind SyntaxKind.CheckedExpression || a.IsKind SyntaxKind.CheckedStatement)

/// A BCL call known to throw on some input: a parse or a `Convert` (a
/// `FormatException`), and — where every failure counts — `First`,
/// `Single`, `ElementAt`, `Substring`, `Remove`, `Insert`.
let private throwingCall (formatOnly: bool) (m: IMethodSymbol) =
    let owner = ownerName m

    m.Name = "Parse"
    || m.Name = "ParseExact"
    || (owner = "System.Convert"
        && m.Name.StartsWith "To"
        && m.Name <> "ToString"
        && not (m.Name.StartsWith "ToBase64"))
    || (not formatOnly
        && isBclSymbol m
        && List.contains m.Name [ "First"; "Last"; "Single"; "ElementAt"; "Substring"; "Remove"; "Insert" ])

/// Does the expression hold a shape known to throw on some input? An
/// element access or indexer, `Nullable<T>.Value`, a `Lazy<T>` or
/// `ThreadLocal<T>` `Value` (the user's factory), an `Exception` member
/// (routinely overridden) on a receiver not sealed, a parse, a `Convert`,
/// `First`/`Single`/`ElementAt`/`Substring`/`Remove`/`Insert`, a
/// user-defined conversion, `checked` arithmetic, a division or modulo by
/// other than a constant, a `throw` or an `await`. `formatOnly` keeps to
/// what throws a `FormatException` (what a `catch (FormatException)`
/// absorbed): the parses, the converts, the user's conversions and
/// factories. Anything else — a user method or getter, `string.Format`,
/// `Trim`, `ToLower`, `x!` — is taken not to throw; the accepted residual
/// is the user getter or method that does.
let hasThrowingShape (model: SemanticModel) (formatOnly: bool) (node: SyntaxNode) : bool =
    let integral (x: ExpressionSyntax) =
        match model.GetTypeInfo(x).Type with
        | null -> false
        | t ->
            t.IsValueType
            && (match t.SpecialType with
                | SpecialType.None
                | SpecialType.System_Single
                | SpecialType.System_Double -> false
                | _ -> true)

    // a division's constant divisor, by its own runtime type: a `char`
    // (`x % 'a'`, legal through char→int) is no IConvertible decimal, and a
    // throw here would silence every hint of the file
    let safeDivisor (b: BinaryExpressionSyntax) =
        match model.GetConstantValue b.Right with
        | c when c.HasValue ->
            match c.Value with
            | :? char as ch -> ch <> '\000'
            | :? sbyte as n -> n <> 0y && n <> -1y
            | :? byte as n -> n <> 0uy
            | :? int16 as n -> n <> 0s && n <> -1s
            | :? uint16 as n -> n <> 0us
            | :? int as n -> n <> 0 && n <> -1
            | :? uint32 as n -> n <> 0u
            | :? int64 as n -> n <> 0L && n <> -1L
            | :? uint64 as n -> n <> 0UL
            | :? nativeint as n -> n <> 0n && n <> -1n
            | :? unativeint as n -> n <> 0un
            | :? decimal as n -> n <> 0m && n <> -1m
            | _ -> false
        | _ -> false

    let rec rootProperty (q: IPropertySymbol) =
        if isNull q.OverriddenProperty then
            q
        else
            rootProperty q.OverriddenProperty

    let throwingProperty (p: IPropertySymbol) (receiver: ExpressionSyntax option) =
        let owner = p.ContainingType.OriginalDefinition
        let ownerName = owner.ToDisplayString()

        if owner.SpecialType = SpecialType.System_Nullable_T then
            p.Name = "Value" && not formatOnly
        elif
            ownerName.StartsWith "System.Lazy<"
            || ownerName.StartsWith "System.Threading.ThreadLocal<"
        then
            p.Name = "Value"
        elif (rootProperty p).ContainingType.ToDisplayString() = "System.Exception" then
            match receiver |> Option.map (fun r -> model.GetTypeInfo(r).Type) with
            | Some t when not (isNull t) && (t.IsSealed || t.IsValueType) -> false
            | _ -> true
        else
            false

    let userConverted (x: ExpressionSyntax) =
        let c = model.GetConversion x
        c.IsUserDefined && not (isBclSymbol c.MethodSymbol)

    node.DescendantNodesAndSelf()
    |> Seq.exists (fun n ->
        match n with
        | :? ElementAccessExpressionSyntax
        | :? ElementBindingExpressionSyntax -> not formatOnly
        | :? ThrowExpressionSyntax
        | :? ThrowStatementSyntax
        | :? AwaitExpressionSyntax -> true
        | :? CheckedExpressionSyntax as c -> c.IsKind SyntaxKind.CheckedExpression && not formatOnly
        | :? InvocationExpressionSyntax as inv ->
            match symbolOf model inv with
            | ValueSome(:? IMethodSymbol as m) -> throwingCall formatOnly m
            | _ -> false
        | :? MemberAccessExpressionSyntax as ma ->
            match symbolOf model ma with
            | ValueSome(:? IPropertySymbol as p) -> throwingProperty p (Some ma.Expression)
            | _ -> false
        | :? MemberBindingExpressionSyntax as mb ->
            match symbolOf model mb with
            | ValueSome(:? IPropertySymbol as p) -> throwingProperty p None
            | _ -> false
        | :? IdentifierNameSyntax as id when
            not (
                match id.Parent with
                | :? MemberAccessExpressionSyntax as ma -> obj.ReferenceEquals(ma.Name, id)
                | _ -> false
            )
            ->
            match symbolOf model id with
            | ValueSome(:? IPropertySymbol as p) -> throwingProperty p None
            | _ -> false
        | :? BinaryExpressionSyntax as b when not formatOnly ->
            match b.Kind() with
            | SyntaxKind.DivideExpression
            | SyntaxKind.ModuloExpression -> isBuiltinOperator model b && integral b.Right && not (safeDivisor b)
            | SyntaxKind.AddExpression
            | SyntaxKind.SubtractExpression
            | SyntaxKind.MultiplyExpression -> isBuiltinOperator model b && integral b.Left && checkedAt model b
            | _ -> false
        | :? PrefixUnaryExpressionSyntax as u when not formatOnly && u.IsKind SyntaxKind.UnaryMinusExpression ->
            integral u.Operand && checkedAt model u
        | _ -> false)
    || node.DescendantNodesAndSelf()
       |> Seq.exists (fun n ->
           match n with
           | :? ExpressionSyntax as x -> userConverted x
           | _ -> false)

/// Does the node assign, step or await — an effect that runs once per
/// element under `Count` and stops at the first match under `Any`?
let private hasEffect (node: SyntaxNode) =
    node.DescendantNodesAndSelf()
    |> Seq.exists (fun n ->
        n :? AssignmentExpressionSyntax
        || n.IsKind SyntaxKind.PostIncrementExpression
        || n.IsKind SyntaxKind.PostDecrementExpression
        || n.IsKind SyntaxKind.PreIncrementExpression
        || n.IsKind SyntaxKind.PreDecrementExpression
        || n :? AwaitExpressionSyntax)

/// A condition evaluated on every element, or only up to the first match,
/// alike: it holds no shape known to throw (`hasThrowingShape`) and no
/// effect. A user method or getter in it is taken to be total — the
/// accepted residual, with a member read on a null element after the first
/// match, which threw under `Count` and does not under `Any`.
let isTotalCondition (model: SemanticModel) (e: SyntaxNode) : bool =
    not (hasThrowingShape model false e || hasEffect e)

/// A lambda whose body is a total condition, or a method group not known
/// to throw (`s.Count(char.IsDigit)`, `xs.Any(IsValid)`; not `int.Parse`).
let isTotalPredicate (model: SemanticModel) (f: ExpressionSyntax) : bool =
    match f with
    | :? AnonymousFunctionExpressionSyntax as l ->
        l.AsyncKeyword.IsKind SyntaxKind.None && isTotalCondition model l.Body
    | :? IdentifierNameSyntax
    | :? MemberAccessExpressionSyntax ->
        match symbolOf model f with
        | ValueSome(:? IMethodSymbol as m) -> not (throwingCall false m)
        | _ -> true
    | _ -> true

/// A source enumerated to its end (`Count`) or to its first element
/// (`Any`) alike: anything but a sequence positively known to run code
/// after its first element — a call to a visible iterator (a body with
/// `yield`), a user collection whose visible `GetEnumerator` is one, a lazy
/// reader of the file system (`File.ReadLines`, `Directory.EnumerateFiles`),
/// `Cast<T>()` (throws mid-way on a mismatch), or a LINQ operator over such
/// a source or with a lambda that is not total. An `IEnumerable<T>` of
/// unknown origin, a user method without `yield`, `Distinct` or `GroupBy`
/// over user elements: eager — the accepted residual is the user code
/// (an iterator behind an interface, a `GetHashCode`) that acts or throws
/// after the first.
let rec isEagerSource (model: SemanticModel) (e: ExpressionSyntax) : bool =
    let iterator (s: ISymbol) =
        visibleBodies model s
        |> List.exists (fun (_, body) -> body.DescendantNodes() |> Seq.exists (fun n -> n :? YieldStatementSyntax))

    let lazyReader (m: IMethodSymbol) =
        let owner = ownerName m

        (owner = "System.IO.File" && m.Name.StartsWith "ReadLines")
        || ((owner = "System.IO.Directory" || owner = "System.IO.DirectoryInfo")
            && m.Name.StartsWith "Enumerate")

    let linqOwners =
        set
            [
                "System.Linq.Enumerable"
                "System.Linq.Queryable"
                "System.Linq.ParallelEnumerable"
            ]

    match e with
    | :? ParenthesizedExpressionSyntax as p -> isEagerSource model p.Expression
    | :? InvocationExpressionSyntax as inv ->
        match symbolOf model inv with
        | ValueSome(:? IMethodSymbol as m) when linqOwners.Contains(ownerName m) ->
            m.Name <> "Cast"
            && (isNull m.ReducedFrom
                || (match inv.Expression with
                    | :? MemberAccessExpressionSyntax as ma -> isEagerSource model ma.Expression
                    | _ -> true))
            && inv.ArgumentList.Arguments
               |> Seq.forall (fun a ->
                   match a.Expression with
                   | :? AnonymousFunctionExpressionSyntax as l -> isTotalPredicate model l
                   | x ->
                       match model.GetTypeInfo(x).Type with
                       // a method group
                       | null -> isTotalPredicate model x
                       // a nested sequence (`Concat(ys)`)
                       | t when
                           t.SpecialType = SpecialType.None
                           && t.TypeKind <> TypeKind.Delegate
                           && t.AllInterfaces |> Seq.exists (fun i -> i.Name = "IEnumerable")
                           ->
                           isEagerSource model x
                       | _ -> true)
        | ValueSome(:? IMethodSymbol as m) -> not (lazyReader m || iterator m)
        | _ -> true
    | :? IdentifierNameSyntax
    | :? MemberAccessExpressionSyntax ->
        match model.GetTypeInfo(e).Type with
        | :? INamedTypeSymbol as n when
            (n.TypeKind = TypeKind.Class || n.TypeKind = TypeKind.Struct)
            && not (isBclSymbol n.OriginalDefinition)
            ->
            not (n.GetMembers "GetEnumerator" |> Seq.exists iterator)
        | _ -> true
    | _ -> true




/// The symbol an expression text would bind to at a position, as if it
/// stood there — for a rewrite that must land on one particular overload
/// (a `params ReadOnlySpan<T>` overload would take an `Array` as ONE
/// element).
let speculativeSymbol (model: SemanticModel) (position: int) (expression: string) : ISymbol option =
    let e = SyntaxFactory.ParseExpression expression

    if e.ContainsDiagnostics then
        None
    else
        model.GetSpeculativeSymbolInfo(position, e, SpeculativeBindingOption.BindAsExpression).Symbol
        |> Option.ofObj

/// Do the values a conditional or `switch` expression is folded from keep
/// their conversions? Each arm was converted to the target (the return
/// type, the assigned variable's) on its own; folded, the arms first meet
/// at the expression's natural type and that converts to the target —
/// `a ? 1 : 2.0` into `object` boxes a double where the `if` boxed an int,
/// `a ? i : f` into `double` rounds the int through `float`. Safe when
/// every arm has one and the same natural type (the conversion chain is
/// the one each arm took), or when, patched, every arm converts to the
/// very type it converted to before (a natural type equal to the target,
/// or a target-typed expression). `arms` are the original values, in the
/// order the replacement's outermost conditional or switch holds them (a
/// throw arm left out); the edit's replacement holds that expression.
let armsConvertAlike (model: SemanticModel) (edit: TextEdit) (arms: ExpressionSyntax list) : bool =
    let natural = arms |> List.map (fun a -> model.GetTypeInfo(a).Type)

    let sameNatural =
        match natural with
        | first :: rest when not (isNull first) ->
            rest |> List.forall (fun t -> SymbolEqualityComparer.Default.Equals(first, t))
        | _ -> false

    sameNatural
    || (try
            let tree = model.SyntaxTree
            let text = tree.GetText()

            let newTree =
                tree.WithChangedText(text.WithChanges(TextChange(edit.Span, edit.Replacement)))

            let newModel =
                model.Compilation.ReplaceSyntaxTree(tree, newTree).GetSemanticModel(newTree, false)

            let window = TextSpan(edit.Span.Start, edit.Replacement.Length)

            let newArms =
                newTree.GetRoot().DescendantNodes window
                |> Seq.tryPick (fun n ->
                    if not (window.Contains n.Span) then
                        None
                    else
                        match n with
                        | :? ConditionalExpressionSyntax as c -> Some [ c.WhenTrue; c.WhenFalse ]
                        | :? SwitchExpressionSyntax as s ->
                            s.Arms
                            |> Seq.map (fun a -> a.Expression)
                            |> Seq.filter (fun e -> not (e :? ThrowExpressionSyntax))
                            |> List.ofSeq
                            |> Some
                        | _ -> None)

            match newArms with
            | Some newArms when newArms.Length = arms.Length ->
                List.forall2
                    (fun (o: ExpressionSyntax) (n: ExpressionSyntax) ->
                        let before = model.GetTypeInfo(o).ConvertedType
                        let after = newModel.GetTypeInfo(n).ConvertedType

                        // two compilations: a type declared in source is a
                        // different symbol in each, so symbol equality says
                        // "differs" for every user type — compare the names
                        not (isNull before)
                        && not (isNull after)
                        && before.ToDisplayString SymbolDisplayFormat.FullyQualifiedFormat =
                            after.ToDisplayString SymbolDisplayFormat.FullyQualifiedFormat)
                    arms
                    newArms
            | _ -> false
        with _ -> // arms that cannot be compared are not alike; fsharpanalyzer: ignore-line FR0055
            false)
