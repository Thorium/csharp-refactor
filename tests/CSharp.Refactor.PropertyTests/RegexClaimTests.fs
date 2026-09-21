/// CR0108's premise, checked against the runtime rather than the syntax:
/// for a plain-text pattern, the string operation the rule spells answers
/// exactly what the regex answered, on any subject — overlapping
/// occurrences (`aa` in `aaa`), a subject of newlines (a `^` anchor is the
/// start of the string, not of a line, without `Multiline`), characters
/// beyond ASCII (the regex compares ordinally without `IgnoreCase`, as
/// `Contains(string)`, `Replace(string, string)`, `Split(string)` and the
/// ordinal `StartsWith` do), an empty subject, a subject that is the
/// pattern itself. A shape whose two sides ever disagree is one the rule
/// must not offer.
module CSharp.Refactor.PropertyTests.RegexClaimTests

open System
open System.Text.RegularExpressions
open FsCheck
open FsCheck.FSharp
open FsCheck.Xunit

/// The characters a plain-text pattern may hold: no metacharacter, no
/// quote, no control character — the rule's own gate.
let private patternAlphabet =
    [
        'a'
        'b'
        'c'
        ' '
        '-'
        ','
        ';'
        ':'
        '='
        'ä'
        'ß'
        'İ'
        'i'
        'I'
        '0'
        '1'
        'é'
    ]

/// The subject's alphabet: the pattern's, plus what a subject may carry
/// that a pattern may not.
let private subjectAlphabet =
    patternAlphabet @ [ '\n'; '\r'; '\t'; '"'; '\\'; '.'; '*'; '$'; '^' ]

let private genString (alphabet: char list) (longest: int) =
    Gen.choose (0, longest)
    |> Gen.bind (fun n -> Gen.elements alphabet |> List.replicate n |> Gen.sequenceToList)
    |> Gen.map (Array.ofList >> String)

/// A pattern of one to four characters, and a subject that often holds it —
/// as a substring, repeated, or at the start — so the true branch is exercised.
let private cases =
    gen {
        let! pattern = genString patternAlphabet 4 |> Gen.filter (fun p -> p <> "")
        let! filler = genString subjectAlphabet 8
        let! filler2 = genString subjectAlphabet 8

        let! subject =
            Gen.elements
                [
                    filler
                    ""
                    pattern
                    pattern + pattern
                    filler + pattern + filler2
                    pattern + filler + pattern + pattern
                    filler + pattern.Substring(0, pattern.Length - 1)
                ]

        return pattern, subject
    }

let private arb = Arb.fromGen cases

[<Property(MaxTest = 500)>]
let ``IsMatch and Match.Success over plain text are Contains`` () =
    Prop.forAll arb (fun (p, s) -> Regex.IsMatch(s, p) = s.Contains p && Regex.Match(s, p).Success = s.Contains p)

[<Property(MaxTest = 500)>]
let ``an anchored IsMatch over plain text is the ordinal StartsWith`` () =
    Prop.forAll arb (fun (p, s) -> Regex.IsMatch(s, "^" + p) = s.StartsWith(p, StringComparison.Ordinal))

[<Property(MaxTest = 500)>]
let ``Matches.Count over plain text is the span count, and against zero the presence`` () =
    Prop.forAll arb (fun (p, s) ->
        let count = Regex.Matches(s, p).Count

        count = s.AsSpan().Count(p.AsSpan())
        && (count > 0) = s.Contains p
        && (count = 0) = not (s.Contains p))

[<Property(MaxTest = 500)>]
let ``Replace over plain text is string.Replace`` () =
    Prop.forAll arb (fun (p, s) -> Regex.Replace(s, p, "x") = s.Replace(p, "x"))

[<Property(MaxTest = 500)>]
let ``Split over plain text is string.Split`` () =
    Prop.forAll arb (fun (p, s) -> Regex.Split(s, p) = s.Split p)
