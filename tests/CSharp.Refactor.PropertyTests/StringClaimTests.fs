/// The premises of the string-copy rules, checked against the runtime:
/// CR0174's span hands the consumer the same characters (a parse of a
/// span answers what the parse of the cut did, a builder holds the same
/// text); CR0175's ordinal `StartsWith`/`EndsWith` answers what the cut
/// compared with a literal did, on every string long enough for the cut
/// (the guard the rule demands) — non-ASCII, ligature-like pairs, the
/// Turkish `İ`/`i` that the culture-sensitive overloads would fold;
/// CR0176's `foreach` over the string visits what the array's did, in
/// order.
module CSharp.Refactor.PropertyTests.StringClaimTests

open System
open System.Text
open FsCheck
open FsCheck.FSharp
open FsCheck.Xunit

let private alphabet =
    [
        'a'
        'b'
        'O'
        'R'
        '-'
        ' '
        '1'
        '2'
        'ä'
        'ß'
        'İ'
        'i'
        'I'
        'é'
        'ﬁ'
        'f'
    ]

let private genString (longest: int) =
    Gen.choose (0, longest)
    |> Gen.bind (fun n -> Gen.elements alphabet |> List.replicate n |> Gen.sequenceToList)
    |> Gen.map (Array.ofList >> String)

/// A literal of one to four characters, and a subject at least as long —
/// often starting or ending with the literal, so both answers occur.
let private cases =
    gen {
        let! literal = genString 4 |> Gen.filter (fun p -> p <> "")
        let! filler = genString 8
        let! filler2 = genString 8

        let! subject =
            Gen.elements
                [
                    literal
                    literal + filler
                    filler + literal
                    filler + literal + filler2
                    literal + filler + literal
                    (filler + literal + filler2).Substring 1 + literal
                ]
            |> Gen.filter (fun s -> s.Length >= literal.Length)

        return literal, subject
    }

let private arb = Arb.fromGen cases

[<Property(MaxTest = 500)>]
let ``a prefix cut and compared is the ordinal StartsWith, under the length guard`` () =
    Prop.forAll arb (fun (lit, s) ->
        let n = lit.Length

        (s.Substring(0, n) = lit) = s.StartsWith(lit, StringComparison.Ordinal)
        && (s.[.. n - 1] = lit) = s.StartsWith(lit, StringComparison.Ordinal)
        && (s.Substring(0, n) <> lit) = not (s.StartsWith(lit, StringComparison.Ordinal)))

[<Property(MaxTest = 500)>]
let ``a suffix cut and compared is the ordinal EndsWith, under the length guard`` () =
    Prop.forAll arb (fun (lit, s) ->
        let n = lit.Length

        (s.Substring(s.Length - n) = lit) = s.EndsWith(lit, StringComparison.Ordinal)
        && (s.[s.Length - n ..] = lit) = s.EndsWith(lit, StringComparison.Ordinal))

[<Property(MaxTest = 500)>]
let ``a span of the cut parses and appends as the cut did`` () =
    let digits =
        gen {
            let! prefix = genString 3
            let! n = Gen.choose (0, 999999)
            let! suffix = genString 3
            return prefix, string n, suffix
        }

    Prop.forAll (Arb.fromGen digits) (fun (prefix, number, suffix) ->
        let s = prefix + number + suffix
        let a = prefix.Length
        let b = number.Length

        Int32.Parse(s.Substring(a, b)) = Int32.Parse(s.AsSpan(a, b))
        && StringBuilder().Append(s.Substring a).ToString() = StringBuilder().Append(s.AsSpan a).ToString()
        && String.Concat(s.Substring(a, b), suffix) = String.Concat(s.AsSpan(a, b), suffix))

[<Property(MaxTest = 300)>]
let ``a foreach over the string visits what the array's did, in order`` () =
    Prop.forAll (Arb.fromGen (genString 12)) (fun s ->

        let viaArray: char list =
            [
                for c in s.ToCharArray() do
                    c
            ]


        let viaString: char list =
            [
                for c in s do
                    c
            ]

        viaArray = viaString)
