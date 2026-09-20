/// The conventions and incidental content real files carry, applied to a
/// generated program: `#region`s around members, a `#if` inside a member,
/// a comment inside a member, CRLF line endings, tab indentation, a
/// `#pragma`. None of them changes what a rule may do — a fix must still
/// compile, and what the decoration put there must still be there after
/// it: no rewrite swallows a directive or a comment, none turns a CRLF
/// file into a mixed one.
module CSharp.Refactor.PropertyTests.Decorations

open System
open FsCheck
open FsCheck.FSharp

type Decoration =
    /// `#region M` / `#endregion` around every member
    | Regions
    /// `#if !NEVER` / `#endif` around the second line of every member that has one
    | DirectiveInside
    /// `// note` on its own line after the first line of every multi-line member
    | CommentInside
    /// every line ending CRLF
    | Crlf
    /// four spaces of indentation become a tab
    | Tabs
    /// `#pragma warning disable CS0168` at the top
    | Pragma

let all = [ Regions; DirectiveInside; CommentInside; Crlf; Tabs; Pragma ]

/// The members of a program's class: the runs of lines between the class
/// braces, split on blank lines (the program prints members that way).
let private splitMembers (program: string) : string * string list * string =
    let lines = program.Split '\n'
    let openAt = lines |> Array.findIndex (fun l -> l = "{")
    // the class's own closing brace: the first at column 0 after the opening one
    let closeAt =
        openAt + 1 + (lines.[openAt + 1 ..] |> Array.findIndex (fun l -> l = "}"))

    let head = String.Join("\n", lines.[..openAt])
    let tail = String.Join("\n", lines.[closeAt..])

    let members =
        String.Join("\n", lines.[openAt + 1 .. closeAt - 1]).Split "\n\n"
        |> List.ofArray

    head, members, tail

let private joinMembers (head: string) (members: string list) (tail: string) =
    head + "\n" + String.Join("\n\n", members) + "\n" + tail

let apply (d: Decoration) (program: string) : string =
    match d with
    | Regions ->
        let head, members, tail = splitMembers program

        joinMembers head (members |> List.mapi (fun i m -> $"    #region M{i}\n{m}\n    #endregion")) tail
    | DirectiveInside ->
        let head, members, tail = splitMembers program

        let decorate (m: string) =
            let ls = m.Split '\n'

            // around the first line of the body, where a rewrite of the member would swallow it
            if ls.Length >= 4 && ls.[1].Trim() = "{" then
                String.Join("\n", Array.concat [ ls.[..1]; [| "#if !NEVER" |]; ls.[2..2]; [| "#endif" |]; ls.[3..] ])
            else
                m

        joinMembers head (members |> List.map decorate) tail
    | CommentInside ->
        let head, members, tail = splitMembers program

        let decorate (i: int) (m: string) =
            let ls = m.Split '\n'

            if ls.Length >= 3 && ls.[1].Trim() = "{" then
                String.Join("\n", Array.concat [ ls.[..1]; [| $"        // note {i} kept" |]; ls.[2..] ])
            else
                m

        joinMembers head (members |> List.mapi decorate) tail
    | Crlf -> program.Replace("\n", "\r\n")
    | Tabs -> program.Replace("    ", "\t")
    | Pragma -> "#pragma warning disable CS0168\n" + program

/// What the decoration promises to find again after any fix.
let invariant (d: Decoration) (before: string) (after: string) : string option =
    let count (needle: string) (s: string) =
        let mutable n = 0
        let mutable i = s.IndexOf(needle, StringComparison.Ordinal)

        while i >= 0 do
            n <- n + 1
            i <- s.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)

        n

    match d with
    | Regions ->
        if
            count "#region" after <> count "#region" before
            || count "#endregion" after <> count "#endregion" before
        then
            Some "a #region or #endregion went missing"
        else
            None
    | DirectiveInside ->
        if
            count "#if !NEVER" after <> count "#if !NEVER" before
            || count "#endif" after <> count "#endif" before
        then
            Some "a #if or #endif went missing"
        else
            None
    | CommentInside ->
        // every note the decoration wrote is still there, word for word
        let notes =
            before.Split '\n'
            |> Array.filter (fun l -> l.Contains "// note ")
            |> Array.map (fun l -> l.Trim())

        match notes |> Array.tryFind (fun n -> not (after.Contains n)) with
        | Some n -> Some $"the comment '{n}' was swallowed"
        | None -> None
    | Crlf ->
        // no bare LF anywhere: an insertion must use the file's own newline
        let bare =
            after
            |> Seq.indexed
            |> Seq.exists (fun (i, c) -> c = '\n' && (i = 0 || after.[i - 1] <> '\r'))

        if bare then
            Some "a fix inserted a bare LF into a CRLF file"
        else
            None
    | Tabs
    | Pragma -> None

let genDecoration: Gen<Decoration> = Gen.elements all
