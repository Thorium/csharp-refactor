/// Culture and clock.
///
/// CR0105 (correctness): `double.Parse(s)`, `DateTime.Parse(s)` without a
/// provider read the machine's culture — a decimal comma here, a
/// day-first date there. The editor offers `CultureInfo.InvariantCulture`
/// (primary) and `CultureInfo.CurrentCulture` (the same behaviour, spelled
/// out); the CLI applies the invariant one only under
/// `csharp_refactor.CR0105.invariant = true`, culture being a decision.
/// Guards: `Parse`/`TryParse` on `double`/`float`/`decimal`/`DateTime`/
/// `DateTimeOffset`/`TimeSpan` (integer parses stay quiet), bound to an
/// overload without an `IFormatProvider`; only the one-argument `Parse`
/// gains the provider (a `TryParse` needs a styles argument the rule will
/// not guess and stays a note); `CultureInfo` is spelled short under an
/// existing `using System.Globalization` and fully qualified otherwise;
/// inside an expression tree the whole suggestion stands down (a LINQ
/// provider resolves `Parse` by signature and the two-argument overload
/// can turn a translatable call into a runtime `NotSupportedException`).
///
/// CR0106 (correctness): `DateTime.Now` is a local clock that jumps at
/// DST and differs per machine; `DateTime.UtcNow` is the instant. Fix
/// under `csharp_refactor.CR0106.utc_now = true` (editor always): `Now`
/// read as an instant (compared, stored, subtracted, `.Ticks`,
/// `.ToBinary()`, `.ToFileTime()`, handed to a call). Notes only: `Now`
/// read as a calendar (`.Date`, `.Day`, `.Month`, `.Year`, `.DayOfWeek`,
/// `.Hour`, `.ToString(…)`, `.ToShortDateString()` — swapping the clock
/// underneath manufactures the first bug), `DateTime.Today` and
/// `DateTime.UtcNow.Date` (a calendar cut at midnight of one zone). Quiet:
/// `DateTimeOffset.Now` (it carries its offset); `dt.Date != DateTime.Today`
/// where the other side is a `.Date` this machine produced (the same clock
/// on both sides); `Now` handed to a setter that wants local time
/// (`File.SetLastWriteTime`, `SetCreationTime`, `SetLastAccessTime`); an
/// expression-tree translator's arm — the nearest enclosing `switch` or
/// `if` naming `Now`/`Today` in its condition is a translation table, not
/// a clock read.
module CSharp.Refactor.CultureTime

open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax
open Microsoft.CodeAnalysis.Text

[<Literal>]
let ParseCultureCode = "CR0105"

[<Literal>]
let ClockCode = "CR0106"

// ---- CR0105 ----

let private cultureSensitive =
    set [ "Double"; "Single"; "Decimal"; "DateTime"; "DateTimeOffset"; "TimeSpan" ]

let private parses (tree: SyntaxTree) (model: SemanticModel) (ctx: RuleContext) : Suggestion list =
    let invariantApplies = RuleContext.knobBool ctx ParseCultureCode "invariant" false

    let spelling (position: int) (culture: string) =
        if Usings.imported model position "System.Globalization" "CultureInfo" then
            "CultureInfo." + culture
        else
            "System.Globalization.CultureInfo." + culture

    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun n ->
        match n with
        | :? InvocationExpressionSyntax as inv when
            (let name = Linq.nameOf inv
             name = "Parse" || name = "TryParse")
            && not (Text.insideExpressionTree model inv)
            ->
            match model.GetSymbolInfo(inv).Symbol with
            | :? IMethodSymbol as m when
                m.IsStatic
                && cultureSensitive.Contains m.ContainingType.Name
                && m.ContainingType.ContainingNamespace.ToDisplayString() = "System"
                && not (m.Parameters |> Seq.exists (fun p -> p.Type.Name = "IFormatProvider"))
                ->
                let typeName = m.ContainingType.Name

                if m.Name = "Parse" && inv.ArgumentList.Arguments.Count = 1 then
                    let position = inv.ArgumentList.CloseParenToken.SpanStart

                    let offer (culture: string) (title: string) =
                        Suggestion.fix
                            title
                            ($"{ParseCultureCode}.{culture}")
                            [ Suggestion.insert position (", " + spelling inv.SpanStart culture) ]

                    let invariant = offer "InvariantCulture" "Parse with the invariant culture"

                    Some
                        {
                            Code = ParseCultureCode
                            Message =
                                $"{typeName}.Parse without a provider reads the machine's culture: say which culture the text is in"
                            Span = inv.Span
                            Fixes =
                                [
                                    (if invariantApplies then
                                         invariant
                                     else
                                         Suggestion.editorOnly invariant)
                                    offer "CurrentCulture" "Parse with the current culture (spelled out)"
                                    |> Suggestion.editorOnly
                                ]
                        }
                else
                    Some(
                        Suggestion.note
                            ParseCultureCode
                            $"{typeName}.{m.Name} without a provider reads the machine's culture: pass the culture the text is in"
                            inv.Span
                    )
            | _ -> None
        | _ -> None)
    |> List.ofSeq

// ---- CR0106 ----

let private calendarMembers =
    set
        [
            "Date"
            "Day"
            "Month"
            "Year"
            "DayOfWeek"
            "DayOfYear"
            "Hour"
            "Minute"
            "Second"
            "TimeOfDay"
            "ToString"
            "ToShortDateString"
            "ToLongDateString"
            "ToShortTimeString"
            "ToLongTimeString"
            "AddDays"
            "AddMonths"
            "AddYears"
        ]

let private localSetters =
    [ "SetLastWriteTime"; "SetCreationTime"; "SetLastAccessTime" ] |> Set.ofList

let private isDateTime (model: SemanticModel) (e: ExpressionSyntax) =
    match model.GetTypeInfo(e).Type with
    | null -> false
    | t -> t.ToDisplayString() = "System.DateTime"

/// The nearest enclosing `switch` or `if` names the member in its
/// condition: an expression-tree translator's table, not a clock read.
let private inTranslatorArm (node: SyntaxNode) =
    node.Ancestors()
    |> Seq.tryPick (fun a ->
        match a with
        | :? SwitchSectionSyntax as s -> Some(s.Labels.ToString())
        | :? SwitchExpressionArmSyntax as a -> Some(a.Pattern.ToString())
        | :? IfStatementSyntax as i -> Some(i.Condition.ToString())
        | _ -> None)
    |> Option.exists (fun c ->
        c.Contains "\"Now\""
        || c.Contains "\"Today\""
        || c.Contains "nameof(DateTime.Now)")

let private clocks (tree: SyntaxTree) (model: SemanticModel) (ctx: RuleContext) : Suggestion list =
    let utcApplies = RuleContext.knobBool ctx ClockCode "utc_now" false

    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun n ->
        match n with
        | :? MemberAccessExpressionSyntax as m when
            (m.Name.Identifier.ValueText = "Now" || m.Name.Identifier.ValueText = "Today")
            && isDateTime model m
            && (match model.GetSymbolInfo(m).Symbol with
                | :? IPropertySymbol as p -> p.ContainingType.ToDisplayString() = "System.DateTime"
                | _ -> false)
            && not (inTranslatorArm m)
            ->
            let name = m.Name.Identifier.ValueText

            // what reads the clock
            let read =
                match m.Parent with
                | :? MemberAccessExpressionSyntax as outer when outer.Expression.Span = m.Span ->
                    Some outer.Name.Identifier.ValueText
                | _ -> None

            let calendar = read |> Option.exists calendarMembers.Contains

            // `dt.Date != DateTime.Today`: the same clock on both sides
            let sameDayTest =
                match m.Parent with
                | :? BinaryExpressionSyntax as b ->
                    let other = if b.Left.Span = m.Span then b.Right else b.Left

                    match other with
                    | :? MemberAccessExpressionSyntax as o -> o.Name.Identifier.ValueText = "Date"
                    | _ -> false
                | _ -> false

            let localSetter =
                match m.Parent with
                | :? ArgumentSyntax as a ->
                    match a.Parent.Parent with
                    | :? InvocationExpressionSyntax as inv -> localSetters.Contains((Linq.nameOf inv))
                    | _ -> false
                | _ -> false

            if sameDayTest || localSetter then
                None
            elif name = "Today" then
                Some(
                    Suggestion.note
                        ClockCode
                        "DateTime.Today is a calendar day cut at this machine's midnight: decide the zone (DateTime.UtcNow.Date, or a TimeZoneInfo conversion)"
                        m.Span
                )
            elif calendar then
                Some(
                    Suggestion.note
                        ClockCode
                        $"DateTime.Now.{read.Value} reads the local calendar: decide the zone before swapping the clock (UtcNow would move the cut)"
                        m.Span
                )
            else
                let edit = Suggestion.replace m.Name.Span "UtcNow"

                let fix = Suggestion.fix "Use DateTime.UtcNow" ClockCode [ edit ]

                Some
                    {
                        Code = ClockCode
                        Message =
                            "DateTime.Now is a local clock that jumps at DST and differs per machine: DateTime.UtcNow is the instant"
                        Span = m.Span
                        Fixes = [ (if utcApplies then fix else Suggestion.editorOnly fix) ]
                    }
        | :? MemberAccessExpressionSyntax as m when
            m.Name.Identifier.ValueText = "Date"
            && (match m.Expression with
                | :? MemberAccessExpressionSyntax as inner -> inner.ToString() = "DateTime.UtcNow"
                | _ -> false)
            && not (inTranslatorArm m)
            ->
            Some(
                Suggestion.note
                    ClockCode
                    "DateTime.UtcNow.Date is a calendar day cut at UTC midnight: fine for a UTC key, wrong for a user's day"
                    m.Span
            )
        | _ -> None)
    |> List.ofSeq

let analyze (tree: SyntaxTree) (model: SemanticModel) (ctx: RuleContext) : Suggestion list =
    parses tree model ctx @ clocks tree model ctx
