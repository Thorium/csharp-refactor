/// Dates that say something other than they look like: a custom format
/// with the wrong specifier, a date put together from two instants.
///
/// CR0190 (correctness, fix): a literal custom format on `DateTime`,
/// `DateTimeOffset`, `DateOnly` or `TimeOnly` whose specifier is the wrong
/// one of a look-alike pair. Three shapes, read off the format's runs of
/// one letter:
///
///   - `hh`/`h` with no `t` run anywhere: a 12-hour clock without its AM/PM
///     designator prints 14:05 and 02:05 alike → `HH`/`H`;
///   - `mm`/`m` beside a `y` or `d` run and beside no `h`/`H`/`s` run: the
///     minutes where the month belongs → `MM`/`M`;
///   - `MM`/`M` after an `h`/`H` run or before an `s` run, beside no `y`/`d`
///     run: the month where the minutes belong → `mm`/`m` (`MMM` and longer
///     are month names).
///
/// A run of the other letter in its rightful place vouches for the one in
/// doubt: the minutes are rewritten only when every `M` run of the format is
/// itself a month among the time parts (or there is none), the month only
/// when every `m` run is itself minutes among the date parts — so
/// `yyyy-mm-dd HH:MM` has both repaired and `yyyy-MM-dd mm` is left alone.
/// The 12-hour repair is a sweep's only in a timestamp (a format with a
/// `y`, `M` or `d` run) of a file where no other text renders the designator
/// (AM/PM as a word, a format with a `t` run) and no `ParseExact` reads an
/// `h` format; elsewhere it is an editor offer — the designator may be
/// rendered beside the time, and a reader may expect the 12-hour text.
/// "Beside" is the nearest letter run on either side with nothing but
/// separator characters between: an escaped character or a quoted section
/// between two runs parts them. Read at `.ToString(format)`/
/// `.ToString(format, provider)`, in an interpolation hole's format clause
/// (`{dt:yyyymmdd}`) and at the format argument of `ParseExact`/
/// `TryParseExact` (a literal, or the literals of an array). The fix
/// rewrites the letters inside the literal and nothing else, so its quoting
/// (regular, verbatim, raw) stays; at a `ParseExact` it is an editor offer
/// only — what a parser accepts is the author's call. Guards: the receiver,
/// hole or parse owner is one of the four types by symbol (`TimeSpan` has
/// `hh` as its only hour specifier and never matches); a one-character
/// format is a standard format; a format holding `%` or an unclosed quote is
/// left alone; so is a literal whose source between the quotes is not its
/// value (a C# escape, a doubled quote), where a position in the format is
/// not a position in the source; an `h` run touching an `H` run stays (the
/// new letters would join it); an interpolated string is read only where it
/// is a `string` (a `FormattableString` or a handler may format by other
/// rules); nothing inside an expression tree; nothing in a test file (a test
/// pins the text it expects).
///
/// CR0191 (correctness, fix): `new DateTime(now.Year, now.AddMonths(-1).Month,
/// 25)` — the year of one instant and the month of the same instant
/// shifted. Across a year boundary the two disagree: in January that is
/// December of the wrong year. The fix reads the year from the shifted
/// instant too: the date changes at a year boundary only. Where it is the
/// year that reads the shifted instant, moving the month changes every
/// date, and the rewrite is an editor offer. A receiver that changes when
/// read (`xs[i++]`) is not one instant written twice and is quiet. Guards: `DateTime`, `DateTimeOffset` or `DateOnly` constructed
/// through `(year, month, day, …)` with the three passed by position; the
/// two receivers are the same expression but for one `AddMonths`/`AddDays`/
/// `AddYears` call; the fix needs a receiver that is a chain of locals,
/// parameters, fields and properties and a shift amount that is a literal
/// or a name, negated or not — anything else is a note. `AddYears` keeps
/// the month, so a year read through it beside the plain month is one
/// instant and quiet, and a month read through it beside the plain year
/// (the shift is lost) is a note. A day read from the instant shifted
/// against the one the year and month come from (`new DateTime(now.Year,
/// now.Month, now.AddDays(1).Day)`) is a note: the day can belong to
/// another month.
module CSharp.Refactor.DateShapes

open System
open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax

[<Literal>]
let FormatSpecifierCode = "CR0190"

[<Literal>]
let MixedInstantCode = "CR0191"

let private dateTypes = set [ "DateTime"; "DateTimeOffset"; "DateOnly"; "TimeOnly" ]

/// The date type's name when the type is one of the four (or a
/// `Nullable` of one), by symbol.
let private dateTypeName (t: ITypeSymbol) : string option =
    if isNull t then
        None
    else
        let t =
            if t.OriginalDefinition.SpecialType = SpecialType.System_Nullable_T then
                (t :?> INamedTypeSymbol).TypeArguments.[0]
            else
                t

        if
            dateTypes.Contains t.Name
            && not (isNull t.ContainingNamespace)
            && t.ContainingNamespace.ToDisplayString() = "System"
        then
            Some t.Name
        else
            None

// ---- CR0190 ----

/// A run of one letter in a custom format: `yyyy`, `MM`, a lone `T`. An
/// escaped character or a quoted section stands in the list as a run of no
/// letter and no length: the runs either side of it are not neighbours.
[<Struct>]
type private Run =
    {
        Letter: char
        Start: int
        Length: int
    }

[<Literal>]
let private NoLetter = '\000'

/// The letter runs of a custom format, in order, escaped characters
/// (`\x`) and quoted sections marked as the breaks they are. None for a
/// format the rule does not read: a standard (one-character) format, one
/// holding `%`, one whose quote never closes.
let private runsOf (format: string) : Run[] option =
    if format.Length < 2 || format.IndexOf '%' >= 0 then
        None
    else
        let runs = ResizeArray<Run>()
        let mutable i = 0
        let mutable readable = true

        let breakAt (position: int) =
            runs.Add
                {
                    Letter = NoLetter
                    Start = position
                    Length = 0
                }

        while readable && i < format.Length do
            let c = format.[i]

            if c = '\\' then
                breakAt i
                i <- i + 2
            elif c = '\'' || c = '"' then
                // a backslash inside the quotes escapes too
                let mutable j = i + 1

                while j < format.Length && format.[j] <> c do
                    j <- j + (if format.[j] = '\\' then 2 else 1)

                if j >= format.Length then
                    readable <- false
                else
                    breakAt i
                    i <- j + 1
            elif Char.IsLetter c then
                let start = i

                while i < format.Length && format.[i] = c do
                    i <- i + 1

                runs.Add
                    {
                        Letter = c
                        Start = start
                        Length = i - start
                    }
            else
                i <- i + 1

        if readable then Some(runs.ToArray()) else None

type private Wrong =
    | TwelveHour
    | MinutesForMonth
    | MonthForMinutes

/// The runs written with the wrong letter of a pair, each with the letter
/// that was meant.
let private wrongRuns (runs: Run[]) : (Run * char * Wrong) list =
    let has (letter: char) =
        runs |> Array.exists (fun r -> r.Letter = letter)

    let letterAt (i: int) =
        if i >= 0 && i < runs.Length then
            runs.[i].Letter
        else
            NoLetter

    let beside (i: int) (letters: string) =
        letters.IndexOf(letterAt (i - 1)) >= 0 || letters.IndexOf(letterAt (i + 1)) >= 0

    // minutes in a date position: beside the year or the day, not beside the time
    let minutesInDate (i: int) =
        runs.[i].Letter = 'm'
        && runs.[i].Length <= 2
        && beside i "yd"
        && not (beside i "hHs")

    // a month in a time position (`MMM` and longer are month names)
    let monthInTime (i: int) =
        runs.[i].Letter = 'M'
        && runs.[i].Length <= 2
        && ("hH".IndexOf(letterAt (i - 1)) >= 0 || letterAt (i + 1) = 's')
        && not (beside i "yd")

    // a run of the other letter in its rightful place vouches for this one
    let every (letter: char) (misplaced: int -> bool) =
        seq { 0 .. runs.Length - 1 }
        |> Seq.forall (fun i -> runs.[i].Letter <> letter || misplaced i)

    // the rewritten letters would join a run they touch into another specifier
    let touches (i: int) (letter: char) =
        (i > 0
         && runs.[i - 1].Letter = letter
         && runs.[i - 1].Start + runs.[i - 1].Length = runs.[i].Start)
        || (i + 1 < runs.Length
            && runs.[i + 1].Letter = letter
            && runs.[i].Start + runs.[i].Length = runs.[i + 1].Start)

    [
        for i in 0 .. runs.Length - 1 do
            let r = runs.[i]

            match r.Letter with
            | 'h' when r.Length <= 2 && not (has 't') && not (touches i 'H') -> yield r, 'H', TwelveHour
            | 'm' when minutesInDate i && every 'M' monthInTime -> yield r, 'M', MinutesForMonth
            | 'M' when monthInTime i && every 'm' minutesInDate -> yield r, 'm', MonthForMinutes
            | _ -> ()
    ]

/// Where the token's value starts in the token's text, when the source
/// between the quotes IS the value — no escape, no doubled quote — so a
/// position in the value is a position in the source. None otherwise.
let private valueStart (token: SyntaxToken) : int option =
    let text = token.Text
    let value = token.ValueText

    let between (first: int) (last: int) =
        if
            last - first = value.Length
            && String.CompareOrdinal(text, first, value, 0, value.Length) = 0
        then
            Some first
        else
            None

    match token.Kind() with
    | SyntaxKind.StringLiteralToken when text.StartsWith "@\"" && text.Length >= 3 -> between 2 (text.Length - 1)
    | SyntaxKind.StringLiteralToken when text.StartsWith "\"" && text.Length >= 2 -> between 1 (text.Length - 1)
    | SyntaxKind.SingleLineRawStringLiteralToken ->
        let quotes = text.Length - text.TrimStart('"').Length
        between quotes (text.Length - quotes)
    | SyntaxKind.InterpolatedStringTextToken -> between 0 text.Length
    | _ -> None

/// A text that renders the AM/PM designator on its own: the words, in any
/// casing and with or without the dots, or a format made of date letters
/// alone that holds a `t` run (`"tt"`, `"hh:mm tt"`).
let private designatorWords =
    System.Text.RegularExpressions.Regex(
        @"(?<![A-Za-z])[AaPp]\.?[Mm]\.?(?![A-Za-z])",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant
    )

let private rendersDesignator (value: string) =
    designatorWords.IsMatch value
    || (value.IndexOf 't' >= 0
        && value
           |> Seq.forall (fun c -> not (Char.IsLetter c) || "yMdhHmsfFtzKg".IndexOf c >= 0))

/// The finding for one format token, when its format holds a wrong
/// specifier. `parsing`: the format tells a parser what to accept, and the
/// fix is the editor's to offer, not a sweep's to apply. `hoursSweepable`:
/// may a sweep turn this file's 12-hour clock into the 24-hour one? Only in
/// a timestamp — a format with a date part — since a bare time may have its
/// designator rendered beside it.
let private formatFinding
    (parsing: bool)
    (hoursSweepable: SyntaxToken -> bool)
    (token: SyntaxToken)
    : Suggestion option =
    let format = token.ValueText

    match runsOf format, valueStart token with
    | Some runs, Some start ->
        let wrong = wrongRuns runs

        if
            wrong.IsEmpty
            || wrong
               |> List.exists (fun (r, _, _) -> start + r.Start + r.Length > token.Text.Length)
        then
            None
        else
            let rewrite (repairs: (Run * char * Wrong) list) =
                let chars = token.Text.ToCharArray()

                for r, letter, _ in repairs do
                    for k in r.Start .. r.Start + r.Length - 1 do
                        chars.[start + k] <- letter

                String(chars)

            let fixFor (key: string) (repairs: (Run * char * Wrong) list) =
                let rewritten = rewrite repairs
                Suggestion.fix $"Write the format as {rewritten}" key [ Suggestion.replace token.Span rewritten ]

            let reasons =
                wrong
                |> List.map (fun (r, letter, kind) ->
                    let was = String(r.Letter, r.Length)
                    let meant = String(letter, r.Length)

                    match kind with
                    | TwelveHour ->
                        $"'{was}' is the 12-hour clock and the format has no AM/PM designator, so 14:05 and 02:05 give the same text ('{meant}' is the 24-hour clock)"
                    | MinutesForMonth -> $"'{was}' is the minutes where the month belongs ('{meant}' is the month)"
                    | MonthForMinutes -> $"'{was}' is the month where the minutes belong ('{meant}' is the minutes)")
                |> List.distinct
                |> String.concat "; "

            let hours = wrong |> List.exists (fun (_, _, kind) -> kind = TwelveHour)

            let hoursSwept =
                hours
                && runs |> Array.exists (fun r -> "yMd".IndexOf r.Letter >= 0)
                && hoursSweepable token

            // what a sweep applies: the month and minute repairs always, the hours where proven
            let swept =
                if hoursSwept then
                    wrong
                else
                    wrong |> List.filter (fun (_, _, kind) -> kind <> TwelveHour)

            let whole = fixFor FormatSpecifierCode wrong

            let fixes =
                if parsing || swept.IsEmpty then
                    [ Suggestion.editorOnly whole ]
                elif swept.Length = wrong.Length then
                    [ whole ]
                else
                    [ fixFor $"{FormatSpecifierCode}.months" swept; Suggestion.editorOnly whole ]

            Some
                {
                    Code = FormatSpecifierCode
                    Message = $"In the date format {token.Text}, {reasons}"
                    Span = token.Span
                    Fixes = fixes
                }
    | _ -> None

/// The string literals an argument holds: itself, or the elements of an
/// array written in place.
let private literalsOf (e: ExpressionSyntax) : SyntaxToken list =
    let literal (x: ExpressionSyntax) =
        match x with
        | :? LiteralExpressionSyntax as l when l.IsKind SyntaxKind.StringLiteralExpression -> Some l.Token
        | _ -> None

    match e with
    | :? LiteralExpressionSyntax -> literal e |> Option.toList
    | :? ArrayCreationExpressionSyntax as a when not (isNull a.Initializer) ->
        a.Initializer.Expressions |> Seq.choose literal |> List.ofSeq
    | :? ImplicitArrayCreationExpressionSyntax as a -> a.Initializer.Expressions |> Seq.choose literal |> List.ofSeq
    | :? CollectionExpressionSyntax as c ->
        c.Elements
        |> Seq.choose (fun element ->
            match element with
            | :? ExpressionElementSyntax as x -> literal x.Expression
            | _ -> None)
        |> List.ofSeq
    | _ -> []

/// The argument bound to one of the named parameters: by its own name
/// where it is written with one, by its position otherwise.
let private argumentFor
    (m: IMethodSymbol)
    (arguments: SeparatedSyntaxList<ArgumentSyntax>)
    (wanted: string -> bool)
    : ExpressionSyntax option =
    arguments
    |> Seq.mapi (fun i a -> i, a)
    |> Seq.tryPick (fun (i, a) ->
        let name =
            if not (isNull a.NameColon) then
                a.NameColon.Name.Identifier.ValueText
            elif i < m.Parameters.Length then
                m.Parameters.[i].Name
            else
                ""

        if wanted name then Some a.Expression else None)

let private formatSpecifiers (tree: SyntaxTree) (model: SemanticModel) : Suggestion list =
    let root = tree.GetRoot()

    // every text the file writes: literals, and the text and format parts of interpolated strings
    let texts =
        lazy
            (root.DescendantTokens()
             |> Seq.filter (fun t ->
                 match t.Kind() with
                 | SyntaxKind.StringLiteralToken
                 | SyntaxKind.SingleLineRawStringLiteralToken
                 | SyntaxKind.MultiLineRawStringLiteralToken
                 | SyntaxKind.InterpolatedStringTextToken -> true
                 | _ -> false)
             |> List.ofSeq)

    // a parser of this file that reads a 12-hour clock: the writer's `hh` is half of a pair
    let parsesTwelveHour =
        lazy
            (root.DescendantNodes()
             |> Seq.exists (fun n ->
                 match n with
                 | :? InvocationExpressionSyntax as inv when
                     (let name = Linq.nameOf inv
                      name = "ParseExact" || name = "TryParseExact")
                     ->
                     inv.ArgumentList.DescendantTokens()
                     |> Seq.exists (fun t ->
                         t.IsKind SyntaxKind.StringLiteralToken
                         && (match runsOf t.ValueText with
                             | Some runs -> runs |> Array.exists (fun r -> r.Letter = 'h')
                             | None -> false))
                 | _ -> false))

    // the designator rendered by another text of the file makes the 12-hour clock whole
    let hoursSweepable (token: SyntaxToken) =
        not parsesTwelveHour.Value
        && not (
            texts.Value
            |> List.exists (fun t -> t.Span <> token.Span && rendersDesignator t.ValueText)
        )

    // a test pins the text it expects: its format is the fixture
    (if Text.isTestFile tree then
         Seq.empty
     else
         root.DescendantNodes())
    |> Seq.collect (fun n ->
        match n with
        | :? InvocationExpressionSyntax as inv when
            (let name = Linq.nameOf inv
             name = "ToString" || name = "ParseExact" || name = "TryParseExact")
            && inv.ArgumentList.Arguments.Count > 0
            ->
            match model.GetSymbolInfo(inv).Symbol with
            | :? IMethodSymbol as m when (dateTypeName m.ContainingType).IsSome ->
                let parsing = m.Name <> "ToString"

                // `ToString` on an instance, the parses on the type
                if parsing <> m.IsStatic || Text.insideExpressionTree model inv then
                    []
                else
                    // a parse names its format `format`, or `formats` for several
                    let isFormat (name: string) =
                        name = "format" || (parsing && name = "formats")

                    match argumentFor m inv.ArgumentList.Arguments isFormat with
                    | Some format -> literalsOf format |> List.choose (formatFinding parsing hoursSweepable)
                    | None -> []
            | _ -> []
        | :? InterpolationSyntax as hole when not (isNull hole.FormatClause) ->
            match hole.Parent with
            | :? InterpolatedStringExpressionSyntax as interpolated when
                (match model.GetTypeInfo(interpolated).ConvertedType with
                 | null -> false
                 | converted -> converted.SpecialType = SpecialType.System_String)
                && (dateTypeName (model.GetTypeInfo(hole.Expression).Type)).IsSome
                && not (Text.insideExpressionTree model hole)
                ->
                formatFinding false hoursSweepable hole.FormatClause.FormatStringToken
                |> Option.toList
            | _ -> []
        | _ -> [])
    |> List.ofSeq

// ---- CR0191 ----

let private shifts = set [ "AddMonths"; "AddDays"; "AddYears" ]

[<TailCall>]
let rec private bare (e: ExpressionSyntax) : ExpressionSyntax =
    match e with
    | :? ParenthesizedExpressionSyntax as p -> bare p.Expression
    | _ -> e

/// Two expressions written alike, whitespace and outer parentheses aside.
let private sameText (a: ExpressionSyntax) (b: ExpressionSyntax) =
    SyntaxFactory.AreEquivalent(bare a, bare b)

/// `X.Year`, `X.Month`, `X.Day` on a date: X.
let private partReceiver (model: SemanticModel) (part: string) (e: ExpressionSyntax) : ExpressionSyntax option =
    match bare e with
    | :? MemberAccessExpressionSyntax as ma when
        ma.IsKind SyntaxKind.SimpleMemberAccessExpression
        && ma.Name.Identifier.ValueText = part
        ->
        match model.GetSymbolInfo(ma).Symbol with
        | :? IPropertySymbol as p when (dateTypeName p.ContainingType).IsSome -> Some ma.Expression
        | _ -> None
    | _ -> None

/// `shifted` is `plain` with one `AddMonths`/`AddDays`/`AddYears` call
/// appended: the call's name and its amount.
let private shiftBetween
    (model: SemanticModel)
    (plain: ExpressionSyntax)
    (shifted: ExpressionSyntax)
    : (string * ExpressionSyntax) option =
    match bare shifted with
    | :? InvocationExpressionSyntax as inv when inv.ArgumentList.Arguments.Count = 1 ->
        match inv.Expression with
        | :? MemberAccessExpressionSyntax as ma when
            ma.IsKind SyntaxKind.SimpleMemberAccessExpression
            && shifts.Contains ma.Name.Identifier.ValueText
            && sameText ma.Expression plain
            ->
            match model.GetSymbolInfo(inv).Symbol with
            | :? IMethodSymbol as m when (dateTypeName m.ContainingType).IsSome ->
                Some(m.Name, inv.ArgumentList.Arguments.[0].Expression)
            | _ -> None
        | _ -> None
    | _ -> None

/// A chain of locals, parameters, fields and properties (`now`,
/// `DateTime.UtcNow`, `order.Created`): no call runs when it is read.
let rec private plainRead (model: SemanticModel) (e: ExpressionSyntax) : bool =
    match e with
    | :? ParenthesizedExpressionSyntax as p -> plainRead model p.Expression
    | :? ThisExpressionSyntax -> true
    | :? IdentifierNameSyntax
    | :? MemberAccessExpressionSyntax ->
        match model.GetSymbolInfo(e).Symbol with
        | :? ILocalSymbol
        | :? IParameterSymbol
        | :? INamedTypeSymbol
        | :? INamespaceSymbol -> true
        | :? IFieldSymbol
        | :? IPropertySymbol ->
            match e with
            | :? MemberAccessExpressionSyntax as ma ->
                ma.IsKind SyntaxKind.SimpleMemberAccessExpression
                && plainRead model ma.Expression
            | _ -> true
        | _ -> false
    | _ -> false

/// A shift amount that can be written a second time: a literal or a name,
/// negated or not.
let private plainAmount (model: SemanticModel) (amount: ExpressionSyntax) : bool =
    let atom (e: ExpressionSyntax) =
        match e with
        | :? LiteralExpressionSyntax -> true
        | :? IdentifierNameSyntax -> plainRead model e
        | _ -> false

    match amount with
    | :? PrefixUnaryExpressionSyntax as u when u.IsKind SyntaxKind.UnaryMinusExpression -> atom u.Operand
    | _ -> atom amount

let private mixedInstants (tree: SyntaxTree) (model: SemanticModel) : Suggestion list =
    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun n ->
        match n with
        | :? BaseObjectCreationExpressionSyntax as creation when
            not (isNull creation.ArgumentList)
            && creation.ArgumentList.Arguments.Count >= 3
            && creation.ArgumentList.Arguments
               |> Seq.truncate 3
               |> Seq.forall (fun a -> isNull a.NameColon)
            ->
            let arguments = creation.ArgumentList.Arguments

            let constructsDate =
                match model.GetSymbolInfo(creation).Symbol with
                | :? IMethodSymbol as ctor ->
                    (match dateTypeName ctor.ContainingType with
                     | Some name -> name <> "TimeOnly"
                     | None -> false)
                    && ctor.Parameters.Length >= 3
                    && ctor.Parameters.[0].Name = "year"
                    && ctor.Parameters.[1].Name = "month"
                    && ctor.Parameters.[2].Name = "day"
                | _ -> false

            if not constructsDate then
                None
            else
                match
                    partReceiver model "Year" arguments.[0].Expression,
                    partReceiver model "Month" arguments.[1].Expression
                with
                | Some year, Some month when sameText year month ->
                    // one instant for the year and the month: the day from it shifted, or it
                    // from the day's shifted
                    match partReceiver model "Day" arguments.[2].Expression with
                    | Some day when
                        not (sameText day year)
                        && ((shiftBetween model year day).IsSome || (shiftBetween model day year).IsSome)
                        ->
                        Some(
                            Suggestion.note
                                MixedInstantCode
                                $"The day is read from '{bare day}' and the year and month from '{bare year}': the day can belong to another month, or not exist in this one — build the date from one instant and shift the result"
                                creation.Span
                        )
                    | _ -> None
                | Some year, Some month ->
                    // (the part read from the plain instant, the shifted instant, the call)
                    let mixed =
                        match shiftBetween model year month with
                        | Some(call, amount) -> Some(year, month, call, amount, true)
                        | None ->
                            shiftBetween model month year
                            |> Option.map (fun (call, amount) -> month, year, call, amount, false)

                    // written alike is the same instant only where reading it changes nothing
                    let changesOnRead (e: ExpressionSyntax) =
                        e.DescendantNodesAndSelf()
                        |> Seq.exists (fun d ->
                            d :? AssignmentExpressionSyntax
                            || d :? AwaitExpressionSyntax
                            || d.IsKind SyntaxKind.PreIncrementExpression
                            || d.IsKind SyntaxKind.PreDecrementExpression
                            || d.IsKind SyntaxKind.PostIncrementExpression
                            || d.IsKind SyntaxKind.PostDecrementExpression)

                    match mixed with
                    | Some(plain, _, _, _, _) when changesOnRead plain -> None
                    // AddYears keeps the month: the shifted year beside the plain month is one instant
                    | Some(_, _, "AddYears", _, false) -> None
                    | Some(plain, shifted, "AddYears", _, true) ->
                        Some(
                            Suggestion.note
                                MixedInstantCode
                                $"The month of '{bare shifted}' is the month of '{bare plain}', and the year is read from '{bare plain}': the shift is lost — read the year from '{bare shifted}' if the shifted year was meant"
                                creation.Span
                        )
                    | Some(plain, shifted, _, amount, yearIsPlain) ->
                        let part = if yearIsPlain then "year" else "month"

                        let message =
                            if yearIsPlain then
                                $"The year is read from '{bare year}' and the month from '{bare month}': when the shift crosses a year boundary the date lands in the wrong year (January less a month is December of the year before) — read both from '{bare shifted}'"
                            else
                                $"The year is read from '{bare year}' and the month from '{bare month}': when the shift crosses a year boundary the date lands in the wrong year (December under next year's number) — read both from one instant"

                        if plainRead model plain && plainAmount model amount then
                            let fix =
                                Suggestion.fix
                                    $"Read the {part} from '{bare shifted}'"
                                    MixedInstantCode
                                    [ Suggestion.replace plain.Span ((bare shifted).ToString()) ]

                            Some
                                {
                                    Code = MixedInstantCode
                                    Message = message
                                    Span = creation.Span
                                    // the year read from the shifted instant changes the date at
                                    // a year boundary only, where it was wrong; the month read
                                    // from it changes every date, and which instant was meant
                                    // is the author's to say
                                    Fixes = [ (if yearIsPlain then fix else Suggestion.editorOnly fix) ]
                                }
                        else
                            Some(Suggestion.note MixedInstantCode message creation.Span)
                    | None -> None
                | _ -> None
        | _ -> None)
    |> List.ofSeq

let analyze (tree: SyntaxTree) (model: SemanticModel) (_ctx: RuleContext) : Suggestion list =
    formatSpecifiers tree model @ mixedInstants tree model
