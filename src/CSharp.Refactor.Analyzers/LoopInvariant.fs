/// CR0177 (performance, fix): a local computed inside a loop from nothing
/// the loop changes is computed once, above it.
///
///     foreach (var x in xs)                   var label = prefix + ":";
///     {                                       foreach (var x in xs)
///         var label = prefix + ":";     →     {
///         Use(x, label);                          Use(x, label);
///     }                                       }
///
/// The F# twin is FR0071. Measured in PerfClaims: a string concatenation
/// hoisted is 4× faster and 48 KB → 48 B over a thousand iterations — the
/// win. Arithmetic over locals, parameters and readonly fields is parity
/// (the JIT hoists that itself) and moves for the reading; a MUTABLE field
/// under a call would gain 8%, but a call in the body can change it, so
/// the rule never reads one.
///
/// Guards: the declaration is one `var`/typed local with an initializer,
/// on one line, a statement of the loop body reached through blocks,
/// `if`/`else`, `switch` sections, `try`, `lock`, `using`, `checked` —
/// never through a lambda, a local function or a nested loop (the
/// innermost loop is the target); the initializer is literals, `nameof`,
/// reads of locals, parameters, `const` and `readonly` fields (outside a
/// constructor), built-in operators over those (`/` and `%` by a non-zero
/// literal, negated or not, only: an empty loop never divided — and never
/// by an integral `-1`, since `int.MinValue / -1` throws even unchecked),
/// `?:`, and an interpolated
/// string whose holes are such reads of primitives or strings; every
/// local or parameter it reads is assigned nowhere in the member but its
/// own declaration, and declared outside the loop; the hoisted name is
/// spelled nowhere in the member outside the loop (a sibling scope's
/// namesake would clash, a field's would be shadowed) and is written
/// nowhere in the loop; the loop statement heads its line; no comment or
/// directive rides on the declaration; the speculative check re-binds.
module CSharp.Refactor.LoopInvariant

open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax
open Microsoft.CodeAnalysis.Text

let Code = "CR0177"

let private isLoop (n: SyntaxNode) =
    n :? ForStatementSyntax
    || n :? ForEachStatementSyntax
    || n :? WhileStatementSyntax
    || n :? DoStatementSyntax

/// The body a loop runs per iteration.
let private bodyOf (loop: SyntaxNode) : StatementSyntax =
    match loop with
    | :? ForStatementSyntax as f -> f.Statement
    | :? ForEachStatementSyntax as f -> f.Statement
    | :? WhileStatementSyntax as w -> w.Statement
    | :? DoStatementSyntax as d -> d.Statement
    | _ -> null

[<return: Struct>]
let inline private (|IsLoop|_|) input =
    if isLoop input then ValueSome input else ValueNone

/// The innermost loop the declaration sits in, reached only through the
/// statement shapes that run it on the loop's own schedule.
let private enclosingLoop (decl: LocalDeclarationStatementSyntax) : SyntaxNode option =
    let rec climb (n: SyntaxNode) =
        match n.Parent with
        | null -> None
        | IsLoop p -> Some p
        | :? BlockSyntax
        | :? IfStatementSyntax
        | :? ElseClauseSyntax
        | :? SwitchSectionSyntax
        | :? SwitchStatementSyntax
        | :? TryStatementSyntax
        | :? CheckedStatementSyntax
        | :? LockStatementSyntax
        | :? UsingStatementSyntax as p -> climb p
        | _ -> None

    climb decl

/// The divisors a `/` or `%` may take: a non-zero numeric literal, negated
/// or not — but never an integral `-1`: `int.MinValue / -1` (and `% -1`)
/// throws OverflowException even unchecked, once above the loop where an
/// empty loop never divided. A floating or decimal divisor never overflows.
let private safeDivisor (e: ExpressionSyntax) =
    let literal, negated =
        match e with
        | :? LiteralExpressionSyntax as l -> Some l, false
        | :? PrefixUnaryExpressionSyntax as u when u.IsKind SyntaxKind.UnaryMinusExpression ->
            match u.Operand with
            | :? LiteralExpressionSyntax as l -> Some l, true
            | _ -> None, false
        | _ -> None, false

    // `-1u` is a long -1: the sign, not the literal's type, decides
    let integral (zero: bool) (one: bool) = not (zero || negated && one)

    match literal with
    | Some l when l.IsKind SyntaxKind.NumericLiteralExpression ->
        match l.Token.Value with
        | :? int as v -> integral (v = 0) (v = 1)
        | :? int64 as v -> integral (v = 0L) (v = 1L)
        | :? uint32 as v -> integral (v = 0u) (v = 1u)
        | :? uint64 as v -> integral (v = 0UL) (v = 1UL)
        | :? double as v -> v <> 0.0
        | :? float32 as v -> v <> 0.0f
        | :? decimal as v -> v <> 0m
        | _ -> false
    | _ -> false

let private isPrimitiveOrString (t: ITypeSymbol) =
    not (isNull t)
    && (t.SpecialType <> SpecialType.None || t.SpecialType = SpecialType.System_String)

let find (tree: SyntaxTree) (model: SemanticModel) : Suggestion list =
    let text = tree.GetText()

    // a local or parameter assigned nowhere in the member but its own
    // declaration: the same value on every iteration
    let neverReassigned (member': SyntaxNode) (s: ISymbol) =
        let name = s.Name

        member'.DescendantNodes()
        |> Seq.forall (fun n ->
            match n with
            | :? AssignmentExpressionSyntax as a ->
                match a.Left with
                | :? IdentifierNameSyntax as id ->
                    not (
                        id.Identifier.ValueText = name
                        && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(id).Symbol, s)
                    )
                // a deconstruction `(a, b) = …` writes every name it lists
                | left -> not (Text.mentionsName name left)
            // `ref var r = ref a;` aliases the local: a write to `r` is a write to `a`
            | :? RefExpressionSyntax as r -> not (Text.mentionsName name r)
            | :? PostfixUnaryExpressionSyntax as u ->
                not (
                    u.Operand.ToString() = name
                    && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(u.Operand).Symbol, s)
                )
            | :? PrefixUnaryExpressionSyntax as u when
                u.IsKind SyntaxKind.PreIncrementExpression
                || u.IsKind SyntaxKind.PreDecrementExpression
                ->
                not (
                    u.Operand.ToString() = name
                    && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(u.Operand).Symbol, s)
                )
            | :? ArgumentSyntax as a when not (a.RefKindKeyword.IsKind SyntaxKind.None) ->
                not (
                    a.Expression.ToString() = name
                    && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(a.Expression).Symbol, s)
                )
            | _ -> true)

    // the initializer: computes from nothing the loop can change, and
    // runs no code of the user's
    let rec invariant
        (member': SyntaxNode)
        (loop: SyntaxNode)
        (inConstructor: bool)
        (checkedContext: bool)
        (e: ExpressionSyntax)
        : bool =
        let again = invariant member' loop inConstructor checkedContext

        let pureRead (node: ExpressionSyntax) =
            match model.GetSymbolInfo(node).Symbol with
            | :? ILocalSymbol as l ->
                // declared outside the loop, and never reassigned
                (match l.DeclaringSyntaxReferences |> Seq.tryHead with
                 | Some r -> not (loop.Span.Contains r.Span.Start)
                 | None -> false)
                && neverReassigned member' l
            | :? IParameterSymbol as p ->
                // a lambda's parameter is per call; the member's own is the same every iteration
                (match p.DeclaringSyntaxReferences |> Seq.tryHead with
                 | Some r -> not (loop.Span.Contains r.Span.Start)
                 | None -> false)
                && neverReassigned member' p
            | :? IFieldSymbol as f -> f.IsConst || (f.IsReadOnly && not inConstructor)
            | _ -> false

        match e with
        | :? LiteralExpressionSyntax -> true
        | :? ParenthesizedExpressionSyntax as p -> again p.Expression
        | :? IdentifierNameSyntax -> pureRead e
        | :? MemberAccessExpressionSyntax as m ->
            (m.Expression :? ThisExpressionSyntax
             || (match model.GetSymbolInfo(m.Expression).Symbol with
                 | :? INamedTypeSymbol -> true
                 | _ -> false))
            && pureRead m
        | :? InvocationExpressionSyntax as inv ->
            match inv.Expression with
            | :? IdentifierNameSyntax as id when id.Identifier.Text = "nameof" -> true
            | _ -> false
        | :? PrefixUnaryExpressionSyntax as u ->
            (u.IsKind SyntaxKind.UnaryMinusExpression
             || u.IsKind SyntaxKind.UnaryPlusExpression
             || u.IsKind SyntaxKind.LogicalNotExpression
             || u.IsKind SyntaxKind.BitwiseNotExpression)
            && Guards.isBuiltinOperator model u
            && again u.Operand
        | :? BinaryExpressionSyntax as b ->
            let arithmetic =
                b.IsKind SyntaxKind.AddExpression
                || b.IsKind SyntaxKind.SubtractExpression
                || b.IsKind SyntaxKind.MultiplyExpression

            let isString =
                match model.GetTypeInfo(b).Type with
                | null -> false
                | t -> t.SpecialType = SpecialType.System_String

            Guards.isBuiltinOperator model b
            && not (b.IsKind SyntaxKind.CoalesceExpression)
            && ((not (b.IsKind SyntaxKind.DivideExpression || b.IsKind SyntaxKind.ModuloExpression))
                || safeDivisor b.Right)
            // under `checked`, an overflow that threw per iteration (or not at
            // all, for an empty loop) would throw once, above the loop
            && not (arithmetic && not isString && checkedContext)
            && again b.Left
            && again b.Right
        | :? ConditionalExpressionSyntax as c -> again c.Condition && again c.WhenTrue && again c.WhenFalse
        | :? InterpolatedStringExpressionSyntax as s ->
            s.Contents
            |> Seq.forall (fun c ->
                match c with
                | :? InterpolatedStringTextSyntax -> true
                | :? InterpolationSyntax as i ->
                    isNull i.AlignmentClause
                    && isNull i.FormatClause
                    && isPrimitiveOrString (model.GetTypeInfo(i.Expression).Type)
                    && again i.Expression
                | _ -> false)
        | _ -> false

    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun node ->
        match node with
        | :? LocalDeclarationStatementSyntax as decl when
            decl.Declaration.Variables.Count = 1
            && decl.Modifiers.Count = 0
            && decl.UsingKeyword.IsKind SyntaxKind.None
            && not (decl.Declaration.Type.IsKind SyntaxKind.RefType)
            && not (isNull decl.Declaration.Variables.[0].Initializer)
            && not (Text.multiLine text decl)
            && not (Text.holdsCommentOrDirective decl)
            ->
            match enclosingLoop decl with
            | Some loop ->
                let member' = Text.enclosingMember decl
                let inConstructor = member' :? ConstructorDeclarationSyntax

                // `checked { }`, `checked(…)` or the project option: overflow throws
                let checkedContext =
                    model.Compilation.Options.CheckOverflow
                    || decl.Ancestors()
                       |> Seq.exists (fun a ->
                           (a :? CheckedStatementSyntax && a.IsKind SyntaxKind.CheckedStatement)
                           || (a :? CheckedExpressionSyntax && a.IsKind SyntaxKind.CheckedExpression))

                let variable = decl.Declaration.Variables.[0]
                let name = variable.Identifier.ValueText
                let init = variable.Initializer.Value

                // the name lives only in this loop, and the loop never writes it
                let nameOnlyHere =
                    member'.DescendantTokens()
                    |> Seq.forall (fun t ->
                        not (t.IsKind SyntaxKind.IdentifierToken && t.ValueText = name)
                        || loop.Span.Contains t.SpanStart)

                let loopHeadsLine =
                    text.Lines
                        .GetLineFromPosition(loop.SpanStart)
                        .ToString()
                        .TrimStart()
                        .StartsWith(loop.GetFirstToken().Text)

                if
                    nameOnlyHere
                    && loopHeadsLine
                    && not (Text.assignsTo name (bodyOf loop))
                    // the loop must sit in a block: above an unbraced `if`/`else`
                    // body a declaration is no statement at all (CS1023)
                    && loop.Parent :? BlockSyntax
                    && invariant member' loop inConstructor checkedContext init
                then
                    let declText = decl.ToString()
                    let indent = Text.leadingWhitespace text loop.SpanStart
                    let newline = Text.newlineAt text loop.SpanStart

                    let edits =
                        [
                            Suggestion.replace (Text.statementLineSpan text decl) ""
                            Suggestion.insert (loop.SpanStart - indent.Length) (indent + declText + newline)
                        ]

                    if Guards.speculativeCheck model edits then
                        Some
                            {
                                Code = Code
                                Message =
                                    $"'{name}' is computed on every iteration from nothing the loop changes; the fix computes it once, above the loop"
                                Span = decl.Span
                                Fixes = [ Suggestion.fix $"Hoist '{name}' above the loop" Code edits ]
                            }
                    else
                        None
                else
                    None
            | None -> None
        | _ -> None)
    |> List.ofSeq

let analyze (tree: SyntaxTree) (model: SemanticModel) (_ctx: RuleContext) : Suggestion list = find tree model
