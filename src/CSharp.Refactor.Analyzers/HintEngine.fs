/// CR0011 (idiom, fix): term-rewriting hints over Roslyn syntax, in the
/// FSharpLint tradition. A hint is one line `lhs ===> rhs`, both sides
/// ordinary C# expressions; a single lowercase letter is a metavariable
/// that binds any expression (`a`, `b`, `x`, `p`, `f`), a single uppercase
/// letter binds a type (`T`), and a lambda's single-letter parameter binds
/// the name of the matched lambda's parameter:
///
///     !(a == b) ===> a != b
///     xs.Where(p).Count() > 0 ===> xs.Any(p)
///     a.CompareTo(b) < 0 ===> a < b
///
/// The built-in set is curated for exactness and each hint carries the
/// typed proof it needs; custom hints come from the file named by
/// `csharp_refactor.hints`, one per line, and are their author's to aim.
///
/// Safety, for every hint:
/// - unification sees through parentheses; only the OUTERMOST match of a
///   nested pair fires; a substituted binding is bracketed where its
///   precedence needs it, and so is the whole replacement in its parent;
/// - a metavariable the right side drops or duplicates fires only on a
///   pure expression (evaluating it again, or not at all, is not a change);
/// - nothing fires inside an attribute argument or an expression tree
///   (there the shape is what a LINQ provider translates);
/// - the speculative re-bind settles the rest.
/// For the built-in hints, additionally: every operator the left side
/// spells is the built-in one at the site (a user-defined `==` is a call),
/// every method it spells resolves to the BCL (`Enumerable.Any`, not a
/// repository's own), every method the right side spells has no
/// non-BCL candidate in scope for the receiver's type (a shadowing
/// extension would win), and the per-hint proofs: `bool` (exactly, not
/// `bool?` — `x == true` on a nullable is a null-safe test) for the
/// literal identities, non-floating operands for an ordering flip or a
/// `CompareTo` collapse (`!(x < 0)` is true for NaN, `x >= 0` is not),
/// a `StringComparison` for `string.Compare` → `string.Equals`, C# 9 for
/// `is not`.
module CSharp.Refactor.HintEngine

open System
open System.IO
open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax
open Microsoft.CodeAnalysis.Text

[<Literal>]
let Code = "CR0011"

/// The typed proof a built-in hint needs beyond the shape.
type Proof =
    /// These metavariables are exactly `System.Boolean`.
    | Bool of string list
    /// These metavariables are not `float`/`double`/`Half`, not `Nullable<T>`
    /// (a lifted comparison is false both ways on null), not `dynamic`.
    | NotFloating of string list
    /// These metavariables are primitive, enum, `decimal`, `DateTime`-like
    /// or `Guid` — types whose `CompareTo` and operators agree.
    | Comparable of string list
    /// These metavariables are `System.StringComparison`.
    | StringComparison of string list
    /// The file is at least this C# major version.
    | Language of int
    /// These metavariables are atoms, not operator applications (for a
    /// custom hint whose left side is a bare `!a`).
    | Atomic of string list

type Hint =
    {
        Text: string
        Lhs: ExpressionSyntax
        Rhs: ExpressionSyntax
        Proofs: Proof list
        /// Microsoft rule ids this hint yields to.
        YieldsTo: string list
        /// A built-in hint: operators and names are checked against the BCL.
        Builtin: bool
    }

type private Binding =
    | Expr of ExpressionSyntax
    | Name of string
    | Type of TypeSyntax

let private isMetavar (name: string) =
    name.Length = 1 && Char.IsLower name.[0]

let private isTypeMetavar (name: string) =
    name.Length = 1 && Char.IsUpper name.[0]

let private parse (text: string) (proofs: Proof list) (yieldsTo: string list) (builtin: bool) : Hint option =
    match text.Split([| "===>" |], StringSplitOptions.None) with
    | [| l; r |] ->
        let lhs = SyntaxFactory.ParseExpression(l.Trim())
        let rhs = SyntaxFactory.ParseExpression(r.Trim())

        if lhs.ContainsDiagnostics || rhs.ContainsDiagnostics then
            None
        else
            Some
                {
                    Text = text.Trim()
                    Lhs = lhs
                    Rhs = rhs
                    Proofs = proofs
                    YieldsTo = yieldsTo
                    Builtin = builtin
                }
    | _ -> None

/// The curated built-in hints.
let builtinHints: Hint list =
    let h text proofs yieldsTo = parse text proofs yieldsTo true

    [
        h "!(a == b) ===> a != b" [] []
        h "!(a != b) ===> a == b" [] []
        h "!(a > b) ===> a <= b" [ NotFloating [ "a"; "b" ] ] []
        h "!(a >= b) ===> a < b" [ NotFloating [ "a"; "b" ] ] []
        h "!(a < b) ===> a >= b" [ NotFloating [ "a"; "b" ] ] []
        h "!(a <= b) ===> a > b" [ NotFloating [ "a"; "b" ] ] []
        h "!(!x) ===> x" [ Bool [ "x" ] ] []
        h "x == true ===> x" [ Bool [ "x" ] ] [ "IDE0100" ]
        h "true == x ===> x" [ Bool [ "x" ] ] [ "IDE0100" ]
        h "x != false ===> x" [ Bool [ "x" ] ] [ "IDE0100" ]
        h "false != x ===> x" [ Bool [ "x" ] ] [ "IDE0100" ]
        h "x == false ===> !x" [ Bool [ "x" ] ] [ "IDE0100" ]
        h "false == x ===> !x" [ Bool [ "x" ] ] [ "IDE0100" ]
        h "x != true ===> !x" [ Bool [ "x" ] ] [ "IDE0100" ]
        h "true != x ===> !x" [ Bool [ "x" ] ] [ "IDE0100" ]
        h "a.CompareTo(b) == 0 ===> a == b" [ Comparable [ "a"; "b" ] ] []
        h "a.CompareTo(b) != 0 ===> a != b" [ Comparable [ "a"; "b" ] ] []
        h "a.CompareTo(b) < 0 ===> a < b" [ Comparable [ "a"; "b" ] ] []
        h "a.CompareTo(b) <= 0 ===> a <= b" [ Comparable [ "a"; "b" ] ] []
        h "a.CompareTo(b) > 0 ===> a > b" [ Comparable [ "a"; "b" ] ] []
        h "a.CompareTo(b) >= 0 ===> a >= b" [ Comparable [ "a"; "b" ] ] []
        h "string.Compare(a, b, c) == 0 ===> string.Equals(a, b, c)" [ StringComparison [ "c" ] ] []
        h "string.Compare(a, b, c) != 0 ===> !string.Equals(a, b, c)" [ StringComparison [ "c" ] ] []
        h "x.Select(f).Sum() ===> x.Sum(f)" [] []
        h "x.Select(f).Average() ===> x.Average(f)" [] []
        h "x.Select(f).Min() ===> x.Min(f)" [] []
        h "x.Select(f).Max() ===> x.Max(f)" [] []
        h "x.Where(p).Any() ===> x.Any(p)" [] [ "IDE0120" ]
        h "x.Where(p).Count() ===> x.Count(p)" [] [ "IDE0120" ]
        h "x.Where(p).First() ===> x.First(p)" [] [ "IDE0120" ]
        h "x.Where(p).FirstOrDefault() ===> x.FirstOrDefault(p)" [] [ "IDE0120" ]
        h "x.Where(p).Last() ===> x.Last(p)" [] [ "IDE0120" ]
        h "x.Where(p).LastOrDefault() ===> x.LastOrDefault(p)" [] [ "IDE0120" ]
        h "x.Where(p).Single() ===> x.Single(p)" [] [ "IDE0120" ]
        h "x.Where(p).SingleOrDefault() ===> x.SingleOrDefault(p)" [] [ "IDE0120" ]
        h "x.Where(p).Count() > 0 ===> x.Any(p)" [] [ "CA1827" ]
        h "x.Where(p).Count() != 0 ===> x.Any(p)" [] [ "CA1827" ]
        h "x.Where(p).Count() == 0 ===> !x.Any(p)" [] [ "CA1827" ]
        h "x.Count() > 0 ===> x.Any()" [] [ "CA1827" ]
        h "x.Count() != 0 ===> x.Any()" [] [ "CA1827" ]
        h "x.Count() >= 1 ===> x.Any()" [] [ "CA1827" ]
        h "x.Count() == 0 ===> !x.Any()" [] [ "CA1827" ]
        h "x.Count() < 1 ===> !x.Any()" [] [ "CA1827" ]
        h "x.Count(p) > 0 ===> x.Any(p)" [] [ "CA1827" ]
        h "x.Count(p) != 0 ===> x.Any(p)" [] [ "CA1827" ]
        h "x.Count(p) == 0 ===> !x.Any(p)" [] [ "CA1827" ]
        h "!(x is T) ===> x is not T" [ Language 9 ] [ "IDE0083" ]
    ]
    |> List.choose id

/// The parsed hints of a file, by path and write time: one read per
/// change, not one per analysed file.
let private hintFileCache =
    System.Collections.Concurrent.ConcurrentDictionary<string, DateTime * Hint list>()

let private readHints (path: string) =
    let stamp =
        try
            File.GetLastWriteTimeUtc path
        with _ ->
            DateTime.MinValue

    match hintFileCache.TryGetValue path with
    | true, (s, hints) when s = stamp -> hints
    | _ ->
        let hints =
            try
                File.ReadAllLines path
                |> Array.toList
                |> List.map (fun l -> l.Trim())
                |> List.filter (fun l -> l <> "" && not (l.StartsWith "#"))
                |> List.choose (fun l -> parse l [] [] false)
            with _ ->
                []

        hintFileCache.[path] <- (stamp, hints)
        hints

/// Custom hints from the config file: one per line, `#` comments skipped;
/// no BCL checks, the author aims them.
let private customHints (ctx: RuleContext) (filePath: string) : Hint list =
    match ctx.Options |> Option.bind Configuration.hintsFile with
    | None -> []
    | Some hintsPath ->
        let resolved =
            if Path.IsPathRooted hintsPath then
                Some hintsPath
            else
                // walk up from the analysed file's directory
                let rec up (dir: string) =
                    if String.IsNullOrEmpty dir then
                        None
                    else
                        let candidate = Path.Combine(dir, hintsPath)

                        if File.Exists candidate then
                            Some candidate
                        else
                            up (Path.GetDirectoryName dir)

                if String.IsNullOrEmpty filePath then
                    None
                else
                    up (Path.GetDirectoryName filePath)

        match resolved with
        | Some path when File.Exists path -> readHints path
        | _ -> []

// ---- unification ----

[<TailCall>]
let rec private unparen (n: SyntaxNode) =
    match n with
    | :? ParenthesizedExpressionSyntax as p -> unparen p.Expression
    | n -> n

/// Match a pattern against a target, binding metavariables; records the
/// operator and invocation nodes of the target the pattern spelled.
let rec private unify
    (pattern: SyntaxNode)
    (target: SyntaxNode)
    (env: Map<string, Binding>)
    (spelled: SyntaxNode list)
    : (Map<string, Binding> * SyntaxNode list) option =
    let pattern = unparen pattern
    let target = unparen target

    match pattern with
    | :? IdentifierNameSyntax as id when isMetavar id.Identifier.ValueText ->
        let name = id.Identifier.ValueText

        match env.TryFind name, target with
        | Some(Expr bound), (:? ExpressionSyntax as t) when SyntaxFactory.AreEquivalent(bound, t, false) ->
            Some(env, spelled)
        | Some(Name n), (:? IdentifierNameSyntax as t) when t.Identifier.ValueText = n -> Some(env, spelled)
        | None, (:? ExpressionSyntax as t) -> Some(env.Add(name, Expr t), spelled)
        | _ -> None
    | :? IdentifierNameSyntax as id when isTypeMetavar id.Identifier.ValueText ->
        let name = id.Identifier.ValueText

        match env.TryFind name, target with
        | Some(Type bound), (:? TypeSyntax as t) when SyntaxFactory.AreEquivalent(bound, t, false) -> Some(env, spelled)
        | None, (:? TypeSyntax as t) -> Some(env.Add(name, Type t), spelled)
        | _ -> None
    | :? SimpleLambdaExpressionSyntax as pl when isMetavar pl.Parameter.Identifier.ValueText ->
        match target with
        | :? SimpleLambdaExpressionSyntax as tl when not (isNull pl.ExpressionBody || isNull tl.ExpressionBody) ->
            let env =
                env.Add(pl.Parameter.Identifier.ValueText, Name tl.Parameter.Identifier.ValueText)

            unify pl.ExpressionBody tl.ExpressionBody env spelled
        | _ -> None
    | _ ->
        if pattern.RawKind <> target.RawKind then
            None
        else
            let spelled =
                match target with
                | :? BinaryExpressionSyntax
                | :? PrefixUnaryExpressionSyntax
                | :? InvocationExpressionSyntax -> target :: spelled
                | _ -> spelled

            let pc = pattern.ChildNodesAndTokens() |> List.ofSeq
            let tc = target.ChildNodesAndTokens() |> List.ofSeq

            if pc.Length <> tc.Length then
                None
            else
                (Some(env, spelled), List.zip pc tc)
                ||> List.fold (fun acc (p, t) ->
                    match acc with
                    | None -> None
                    | Some(env, spelled) ->
                        if p.IsToken then
                            let pt = p.AsToken()
                            let tt = t.AsToken()

                            if t.IsToken && pt.RawKind = tt.RawKind && pt.ValueText = tt.ValueText then
                                Some(env, spelled)
                            else
                                None
                        elif t.IsNode then
                            unify (p.AsNode()) (t.AsNode()) env spelled
                        else
                            None)

// ---- rendering ----

/// C# precedence, higher binds tighter.
let private precedence (e: ExpressionSyntax) =
    match e with
    | :? AssignmentExpressionSyntax
    | :? LambdaExpressionSyntax -> 1
    | :? ConditionalExpressionSyntax -> 2
    | :? BinaryExpressionSyntax as b ->
        match b.Kind() with
        | SyntaxKind.CoalesceExpression -> 3
        | SyntaxKind.LogicalOrExpression -> 4
        | SyntaxKind.LogicalAndExpression -> 5
        | SyntaxKind.BitwiseOrExpression -> 6
        | SyntaxKind.ExclusiveOrExpression -> 7
        | SyntaxKind.BitwiseAndExpression -> 8
        | SyntaxKind.EqualsExpression
        | SyntaxKind.NotEqualsExpression -> 9
        | SyntaxKind.LessThanExpression
        | SyntaxKind.GreaterThanExpression
        | SyntaxKind.LessThanOrEqualExpression
        | SyntaxKind.GreaterThanOrEqualExpression
        | SyntaxKind.IsExpression
        | SyntaxKind.AsExpression -> 10
        | SyntaxKind.LeftShiftExpression
        | SyntaxKind.RightShiftExpression
        | SyntaxKind.UnsignedRightShiftExpression -> 11
        | SyntaxKind.AddExpression
        | SyntaxKind.SubtractExpression -> 12
        | _ -> 13
    | :? IsPatternExpressionSyntax -> 10
    | :? RangeExpressionSyntax -> 10
    | :? PrefixUnaryExpressionSyntax
    | :? CastExpressionSyntax
    | :? AwaitExpressionSyntax -> 14
    | _ -> 15

/// Does a child of this precedence need brackets under this parent, in
/// this position? Left operands of a left-associative binary keep equal
/// precedence unbracketed; right operands do not.
let private needsParens (childPrec: int) (parent: SyntaxNode) (isLeftOperand: bool) =
    match parent with
    | :? BinaryExpressionSyntax as b ->
        let p = precedence b

        if b.IsKind SyntaxKind.CoalesceExpression then
            // right-associative
            if isLeftOperand then childPrec <= p else childPrec < p
        elif isLeftOperand then
            childPrec < p
        else
            childPrec <= p
    | :? PrefixUnaryExpressionSyntax
    | :? CastExpressionSyntax
    | :? AwaitExpressionSyntax -> childPrec < 14
    | :? MemberAccessExpressionSyntax
    | :? InvocationExpressionSyntax
    | :? ElementAccessExpressionSyntax
    | :? ConditionalAccessExpressionSyntax -> childPrec < 15
    | :? ConditionalExpressionSyntax -> if isLeftOperand then childPrec <= 2 else childPrec < 2
    | :? IsPatternExpressionSyntax -> childPrec <= 10
    | :? AssignmentExpressionSyntax -> childPrec < 1
    | _ -> false

let private isLeftChild (child: SyntaxNode) =
    match child.Parent with
    | :? BinaryExpressionSyntax as b -> obj.ReferenceEquals(b.Left, child) || b.Left.Span = child.Span
    | :? ConditionalExpressionSyntax as c -> c.Condition.Span = child.Span
    | _ -> true

/// The right side with the bindings substituted, bracketed where needed.
let private render (rhs: ExpressionSyntax) (env: Map<string, Binding>) : ExpressionSyntax =
    let targets =
        rhs.DescendantNodesAndSelf()
        |> Seq.filter (fun n ->
            match n with
            | :? IdentifierNameSyntax as id ->
                (isMetavar id.Identifier.ValueText || isTypeMetavar id.Identifier.ValueText)
                && env.ContainsKey id.Identifier.ValueText
            | _ -> false)
        |> List.ofSeq

    // a chain laid out one call per line keeps its layout: where a bound
    // expression stood before a `.` that began a line (`x` in `x.Where(p)`),
    // the rhs's `.` after it begins a line too (`x.FirstOrDefault(p)`)
    let dotTrivia =
        System.Collections.Generic.Dictionary<SyntaxNode, SyntaxTriviaList>(HashIdentity.Reference)

    for t in targets do
        match t.Parent, env.[(t :?> IdentifierNameSyntax).Identifier.ValueText] with
        | (:? MemberAccessExpressionSyntax as ma), Expr e when obj.ReferenceEquals(ma.Expression, t) ->
            // the line break is the last token's trailing trivia, the indentation the dot's leading
            let broken = e.GetLastToken().TrailingTrivia

            match e.Parent with
            | :? MemberAccessExpressionSyntax as src when
                obj.ReferenceEquals(src.Expression, e)
                && broken |> Seq.exists (fun tr -> tr.IsKind SyntaxKind.EndOfLineTrivia)
                ->
                dotTrivia.[ma] <- broken.AddRange src.OperatorToken.LeadingTrivia
            | _ -> ()
        | _ -> ()

    let replaced =
        rhs.ReplaceNodes(
            targets @ List.ofSeq dotTrivia.Keys,
            fun original rewritten ->
                match dotTrivia.TryGetValue original with
                | true, trivia ->
                    let ma = rewritten :?> MemberAccessExpressionSyntax
                    ma.WithOperatorToken(ma.OperatorToken.WithLeadingTrivia trivia) :> SyntaxNode
                | _ ->
                    let id = original :?> IdentifierNameSyntax
                    let name = id.Identifier.ValueText

                    match env.[name] with
                    | Expr e ->
                        let e = e.WithoutTrivia()

                        let bracket =
                            match original.Parent with
                            | null -> false
                            | parent -> needsParens (precedence e) parent (isLeftChild original)

                        (if bracket then
                             SyntaxFactory.ParenthesizedExpression e :> SyntaxNode
                         else
                             e :> SyntaxNode)
                            .WithTriviaFrom
                            original
                    | Name n -> (SyntaxFactory.IdentifierName n).WithTriviaFrom original :> SyntaxNode
                    | Type t -> t.WithoutTrivia().WithTriviaFrom original :> SyntaxNode
        )

    // lambda parameters named by a Name binding
    let lambdaParams =
        replaced.DescendantNodesAndSelf()
        |> Seq.choose (fun n ->
            match n with
            | :? ParameterSyntax as p when
                isMetavar p.Identifier.ValueText
                && (match env.TryFind p.Identifier.ValueText with
                    | Some(Name _) -> true
                    | _ -> false)
                ->
                Some p
            | _ -> None)
        |> List.ofSeq

    replaced.ReplaceNodes(
        lambdaParams,
        fun original _ ->
            match env.[original.Identifier.ValueText] with
            | Name n -> original.WithIdentifier(SyntaxFactory.Identifier n) :> SyntaxNode
            | Expr _
            | Type _ -> original :> SyntaxNode
    )

// ---- proofs ----

let private typeOf (model: SemanticModel) (e: ExpressionSyntax) = model.GetTypeInfo(e).Type

let private isFloating (t: ITypeSymbol) =
    match t with
    | null -> true
    | t ->
        match t.SpecialType with
        | SpecialType.System_Single
        | SpecialType.System_Double -> true
        | _ -> t.Name = "Half" || t.TypeKind = TypeKind.Dynamic

[<return: Struct>]
let inline private (|IsFloating|_|) input =
    if isFloating input then ValueSome input else ValueNone

let private isComparable (t: ITypeSymbol) =
    match t with
    | null -> false
    | IsFloating _ -> false
    | t when t.TypeKind = TypeKind.Enum -> true
    | t when t.SpecialType <> SpecialType.None && t.SpecialType <> SpecialType.System_String -> t.IsValueType
    | t ->
        match t.ToDisplayString() with
        | "System.DateTime"
        | "System.DateTimeOffset"
        | "System.TimeSpan"
        | "System.Guid"
        | "System.DateOnly"
        | "System.TimeOnly" -> true
        | _ -> false

let private proven (model: SemanticModel) (ctx: RuleContext) (env: Map<string, Binding>) (proof: Proof) =
    let bound (name: string) =
        match env.TryFind name with
        | Some(Expr e) -> Some e
        | _ -> None

    let all names (test: ITypeSymbol -> bool) =
        names
        |> List.forall (fun n ->
            match bound n with
            | Some e -> test (typeOf model e)
            | None -> false)

    match proof with
    | Bool names -> all names (fun t -> not (isNull t) && t.SpecialType = SpecialType.System_Boolean)
    | NotFloating names ->
        names
        |> List.forall (fun n ->
            match bound n with
            | Some e -> Guards.orderingFlipSafe model e
            | None -> false)
    | Comparable names -> all names isComparable
    | StringComparison names -> all names (fun t -> not (isNull t) && t.ToDisplayString() = "System.StringComparison")
    | Language major -> RuleContext.languageAtLeast ctx major
    | Atomic names ->
        names
        |> List.forall (fun n ->
            match bound n with
            | Some e ->
                not (
                    e :? BinaryExpressionSyntax
                    || e :? IsPatternExpressionSyntax
                    || e :? ConditionalExpressionSyntax
                )
            | None -> false)

let private inSystem (s: ISymbol) =
    match s with
    | null -> false
    | s ->
        let ns = s.ContainingNamespace

        not (isNull ns)
        && (let full = ns.ToDisplayString()
            full = "System" || full.StartsWith "System.")

/// Every operator the pattern spelled is built-in at the site, every
/// invocation resolves to the BCL.
let private spelledAreBcl (model: SemanticModel) (spelled: SyntaxNode list) =
    spelled
    |> List.forall (fun n ->
        match n with
        | :? InvocationExpressionSyntax as inv -> inSystem (model.GetSymbolInfo(inv).Symbol)
        | _ -> Guards.isBuiltinOperator model n)

/// Every method the right side spells on a bound receiver has only BCL
/// candidates in scope for that receiver's type.
let private rhsNamesAreBcl (model: SemanticModel) (position: int) (rhs: ExpressionSyntax) (env: Map<string, Binding>) =
    rhs.DescendantNodesAndSelf()
    |> Seq.forall (fun n ->
        match n with
        | :? InvocationExpressionSyntax as inv ->
            match inv.Expression with
            | :? MemberAccessExpressionSyntax as m ->
                let receiverType =
                    match m.Expression with
                    | :? IdentifierNameSyntax as id when isMetavar id.Identifier.ValueText ->
                        match env.TryFind id.Identifier.ValueText with
                        | Some(Expr e) -> typeOf model e
                        | _ -> null
                    | :? PredefinedTypeSyntax -> null // `string.Equals`: static on the BCL type
                    | _ -> null

                match receiverType with
                | null -> true
                | t ->
                    model.LookupSymbols(position, t, m.Name.Identifier.ValueText, true)
                    |> Seq.forall inSystem
            | _ -> true
        | _ -> true)

/// Metavariables the right side drops or repeats: their bindings must be
/// pure.
let private droppedOrDuplicated (hint: Hint) =
    let count (e: ExpressionSyntax) =
        e.DescendantNodesAndSelf()
        |> Seq.choose (fun n ->
            match n with
            | :? IdentifierNameSyntax as id when isMetavar id.Identifier.ValueText -> Some id.Identifier.ValueText
            | _ -> None)
        |> Seq.countBy id
        |> Map.ofSeq

    let l = count hint.Lhs
    let r = count hint.Rhs

    l
    |> Map.toList
    |> List.filter (fun (name, n) ->
        let m = r |> Map.tryFind name |> Option.defaultValue 0
        m = 0 || m > n)
    |> List.map fst

let private insideAttributeOrTree (model: SemanticModel) (node: SyntaxNode) =
    Guards.insideAttribute node || Guards.insideExpressionTree model node

let analyze (tree: SyntaxTree) (model: SemanticModel) (ctx: RuleContext) : Suggestion list =
    let hints =
        (builtinHints
         |> List.filter (fun h -> not (RuleContext.shadowedRuleOn ctx h.YieldsTo)))
        @ customHints ctx tree.FilePath

    let text = tree.GetText()
    // outermost matches only: a matched node's descendants are skipped
    let matched = System.Collections.Generic.HashSet<TextSpan>()

    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun node ->
        match node with
        | :? ExpressionSyntax as target when
            not (target :? ParenthesizedExpressionSyntax)
            && not (target.Ancestors() |> Seq.exists (fun a -> matched.Contains a.Span))
            ->
            hints
            |> List.tryPick (fun hint ->
                match unify hint.Lhs target Map.empty [] with
                | None -> None
                | Some(env, spelled) ->
                    let pureEnough =
                        droppedOrDuplicated hint
                        |> List.forall (fun name ->
                            match env.TryFind name with
                            | Some(Expr e) -> Guards.isPureExpression model e
                            | _ -> true)

                    let bclOk =
                        not hint.Builtin
                        || (spelledAreBcl model spelled
                            && rhsNamesAreBcl model target.SpanStart hint.Rhs env)

                    if
                        not pureEnough
                        || not bclOk
                        || not (hint.Proofs |> List.forall (proven model ctx env))
                        || insideAttributeOrTree model target
                    then
                        None
                    else
                        let rendered = render hint.Rhs env

                        // the whole replacement in its parent
                        let bracketWhole =
                            match target.Parent with
                            | null -> false
                            | :? ParenthesizedExpressionSyntax -> false
                            | parent -> needsParens (precedence rendered) parent (isLeftChild target)

                        let replacement =
                            if bracketWhole then
                                "(" + rendered.ToString() + ")"
                            else
                                rendered.ToString()

                        let edit = Suggestion.replace target.Span replacement

                        if Guards.speculativeCheck model [ edit ] then
                            matched.Add target.Span |> ignore

                            Some
                                {
                                    Code = Code
                                    Message = $"'{text.ToString target.Span}' is '{replacement}' ({hint.Text})"
                                    Span = target.Span
                                    Fixes = [ Suggestion.fix ("Rewrite: " + hint.Text) Code [ edit ] ]
                                }
                        else
                            None)
        | _ -> None)
    |> List.ofSeq
