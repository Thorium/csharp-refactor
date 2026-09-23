/// CR0089 (idiom, fix, off by default): a private type's `DateTime` field
/// or auto-property written from `DateTime.Now`/`UtcNow` and read only
/// through parity members becomes `DateTimeOffset` in one edit set — the
/// clock keeps its offset, comparisons and arithmetic keep their meaning,
/// and nothing downstream re-interprets a `Kind`.
///
/// The strict envelope of FR0134: the type is private (a nested private
/// class, or a file-local one); every write to the slot is `DateTime.Now`,
/// `DateTime.UtcNow`, or another slot of the same migration (the prefix
/// must REALLY be `System.DateTime` — a shadowing fake-clock type would
/// take the rewrite, compile, and switch to the real clock); at least one
/// real write pins the clock; `Now` and `UtcNow` never mix across the
/// migration; every read is a parity member — a comparison, a
/// subtraction, `.Ticks`, `.Year`…`.Second`, `.AddDays`…`.AddMinutes` —
/// never `.Date` (returns `DateTime`), `.Kind`, `.ToLocalTime()`,
/// `.ToUniversalTime()`, `.ToString()` in any form (a `DateTimeOffset`
/// appends its offset), or the value handed to a call; the slot is
/// confirmed by symbol, never by name. Off by default: a serialization-
/// shape change (`csharp_refactor.CR0089 = true` or `--codes CR0089`).
module CSharp.Refactor.ClockMigration

open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax
open Microsoft.CodeAnalysis.Text

[<Literal>]
let Code = "CR0089"

let private parityMembers =
    set
        [
            "Ticks"
            "Year"
            "Month"
            "Day"
            "Hour"
            "Minute"
            "Second"
            "Millisecond"
            "DayOfWeek"
            "DayOfYear"
            "AddDays"
            "AddHours"
            "AddMinutes"
            "AddSeconds"
            "AddMilliseconds"
            "AddMonths"
            "AddYears"
            "Subtract"
            "CompareTo"
            "Equals"
        ]

let private isSystemDateTime (t: ITypeSymbol) =
    not (isNull t) && t.SpecialType = SpecialType.System_DateTime

/// Is the expression a real clock read: `System.DateTime.Now` or `UtcNow`?
let private clockRead (model: SemanticModel) (e: ExpressionSyntax) : string option =
    match e with
    | :? MemberAccessExpressionSyntax as m when
        m.Name.Identifier.ValueText = "Now" || m.Name.Identifier.ValueText = "UtcNow"
        ->
        match model.GetSymbolInfo(m).Symbol with
        | :? IPropertySymbol as p when p.ContainingType.SpecialType = SpecialType.System_DateTime ->
            Some m.Name.Identifier.ValueText
        | _ -> None
    | _ -> None

let private isPrivateType (t: INamedTypeSymbol) =
    t.DeclaredAccessibility = Accessibility.Private

let analyze (tree: SyntaxTree) (model: SemanticModel) (_ctx: RuleContext) : Suggestion list =
    tree.GetRoot().DescendantNodes()
    |> Seq.collect (fun n ->
        match n with
        | :? TypeDeclarationSyntax as t ->
            match model.GetDeclaredSymbol t with
            | null -> Seq.empty
            | self when not (isPrivateType self) -> Seq.empty
            | self ->
                // the DateTime slots: fields and auto-properties declared in this file
                let slots =
                    t.Members
                    |> Seq.collect (fun m ->
                        match m with
                        | :? FieldDeclarationSyntax as f when
                            isSystemDateTime (model.GetTypeInfo(f.Declaration.Type).Type)
                            ->
                            f.Declaration.Variables
                            |> Seq.choose (fun v ->
                                match model.GetDeclaredSymbol v with
                                | :? IFieldSymbol as fs -> Some(fs :> ISymbol, f.Declaration.Type :> SyntaxNode)
                                | _ -> None)
                        | :? PropertyDeclarationSyntax as p when
                            isSystemDateTime (model.GetTypeInfo(p.Type).Type)
                            && not (isNull p.AccessorList)
                            && p.AccessorList.Accessors
                               |> Seq.forall (fun a -> isNull a.Body && isNull a.ExpressionBody)
                            ->
                            match model.GetDeclaredSymbol p with
                            | null -> Seq.empty
                            | ps -> Seq.singleton (ps :> ISymbol, p.Type :> SyntaxNode)
                        | _ -> Seq.empty)
                    |> List.ofSeq

                if slots.IsEmpty then
                    Seq.empty
                else
                    let slotSymbols = slots |> List.map fst

                    let isSlot (e: ExpressionSyntax) =
                        let s = model.GetSymbolInfo(e).Symbol

                        slotSymbols
                        |> List.exists (fun k -> SymbolEqualityComparer.Default.Equals(s, k))

                    // every mention of a slot in the compilation, classified
                    let mentions =
                        let index = Index.ofCompilation model.Compilation

                        slotSymbols
                        |> List.collect (fun k -> Index.usesOf index k |> List.map (fun u -> u.Tree, u.Model, u.Id))

                    let sameTree = mentions |> List.forall (fun (tr, _, _) -> tr = tree)

                    // the expression a mention stands in: `this.x` and `other.x` count as the access
                    let stand (id: IdentifierNameSyntax) : ExpressionSyntax =
                        match id.Parent with
                        | :? MemberAccessExpressionSyntax as m when m.Name.Span = id.Span -> m :> ExpressionSyntax
                        | _ -> id :> ExpressionSyntax

                    let mutable clocks: string list = []
                    let mutable realWrites = 0

                    // a slot's own initializer: `DateTime stamp = DateTime.Now;`
                    let initializerEdits =
                        slots
                        |> List.choose (fun (s, _) ->
                            s.DeclaringSyntaxReferences
                            |> Seq.tryPick (fun r ->
                                match r.GetSyntax() with
                                | :? VariableDeclaratorSyntax as v when not (isNull v.Initializer) ->
                                    Some v.Initializer.Value
                                | :? PropertyDeclarationSyntax as p when not (isNull p.Initializer) ->
                                    Some p.Initializer.Value
                                | _ -> None))
                        |> List.choose (fun init ->
                            match clockRead model init with
                            | Some which ->
                                clocks <- which :: clocks
                                realWrites <- realWrites + 1

                                Some(
                                    Suggestion.replace
                                        (init :?> MemberAccessExpressionSyntax).Expression.Span
                                        "DateTimeOffset"
                                )
                            | None -> None)

                    let ok =
                        mentions
                        |> List.forall (fun (_, m, id) ->
                            let e = stand id

                            match e.Parent with
                            // a write: from the clock, or from another slot
                            | :? AssignmentExpressionSyntax as a when
                                a.Left.Span = e.Span && a.IsKind SyntaxKind.SimpleAssignmentExpression
                                ->
                                match clockRead m a.Right with
                                | Some which ->
                                    clocks <- which :: clocks
                                    realWrites <- realWrites + 1
                                    true
                                | None -> isSlot a.Right
                            | :? AssignmentExpressionSyntax as a when a.Right.Span = e.Span -> isSlot a.Left
                            // a parity read
                            | :? MemberAccessExpressionSyntax as ma when ma.Expression.Span = e.Span ->
                                // `ToString()` is not parity: a DateTimeOffset appends its
                                // offset (`… +02:00`) to the same text
                                let name = ma.Name.Identifier.ValueText
                                name <> "ToString" && parityMembers.Contains name
                            | :? BinaryExpressionSyntax as b ->
                                // compared with or subtracted from another slot or a clock read
                                let other = if b.Left.Span = e.Span then b.Right else b.Left

                                (b.IsKind SyntaxKind.SubtractExpression
                                 || b.IsKind SyntaxKind.LessThanExpression
                                 || b.IsKind SyntaxKind.LessThanOrEqualExpression
                                 || b.IsKind SyntaxKind.GreaterThanExpression
                                 || b.IsKind SyntaxKind.GreaterThanOrEqualExpression
                                 || b.IsKind SyntaxKind.EqualsExpression
                                 || b.IsKind SyntaxKind.NotEqualsExpression)
                                && (isSlot other || (clockRead m other).IsSome)
                            | :? EqualsValueClauseSyntax -> false // bound to a DateTime local: escapes
                            | _ -> false)

                    let mixed = (clocks |> List.distinct |> List.length) > 1

                    if ok && sameTree && realWrites > 0 && not mixed then
                        // the initializer writes of the slots themselves (`DateTime x = DateTime.Now;`) count too
                        let edits =
                            initializerEdits
                            @ (slots
                               |> List.map (fun (_, typeNode) -> Suggestion.replace typeNode.Span "DateTimeOffset"))
                            @ (mentions
                               |> List.choose (fun (_, m, id) ->
                                   match (stand id).Parent with
                                   | :? AssignmentExpressionSyntax as a when a.Left.Span = (stand id).Span ->
                                       match a.Right with
                                       | :? MemberAccessExpressionSyntax as clock when (clockRead m clock).IsSome ->
                                           Some(Suggestion.replace clock.Expression.Span "DateTimeOffset")
                                       | _ -> None
                                   | _ -> None))
                            |> List.distinctBy (fun e -> e.Span)

                        if Guards.speculativeCheck model edits then
                            Seq.singleton
                                {
                                    Code = Code
                                    Message =
                                        $"'{self.Name}' keeps a DateTime written from the clock and read through parity members: DateTimeOffset carries the offset the reads assume"
                                    Span = (snd slots.Head).Span
                                    Fixes = [ Suggestion.fix "Migrate to DateTimeOffset" Code edits ]
                                }
                        else
                            Seq.empty
                    else
                        Seq.empty
        | _ -> Seq.empty)
    |> List.ofSeq
