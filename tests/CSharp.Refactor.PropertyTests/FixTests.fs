/// Fixes compose: applying the rules' primary fixes one suggestion at a
/// time to a generated program terminates, with the program compiling at
/// every step. A rule whose fix re-creates another rule's trigger (or its
/// own) would cycle here — the sweep's idempotency, checked in the small.
module CSharp.Refactor.PropertyTests.FixTests

open System
open FsCheck.Xunit
open FsCheck
open FsCheck.FSharp
open CSharp.Refactor
open CSharp.Refactor.Tests.Harness
open CSharp.Refactor.PropertyTests

/// The first suggestion by position that carries a primary fix, with it.
let private nextFix (source: string) : (Suggestion * Fix) option =
    suggest source
    |> List.choose (fun s ->
        s.Fixes
        |> List.tryFind (fun f -> not f.EditorOnly)
        |> Option.map (fun f -> s, f))
    |> List.sortBy (fun (s, _) -> s.Span.Start)
    |> List.tryHead

[<Property(MaxTest = 100, EndSize = 40)>]
let ``applying fixes one at a time reaches a fixed point, compiling throughout`` () =
    Prop.forAll Programs.arbitrary (fun shapes ->
        let original = Programs.program shapes
        // every member fires a handful of rules at most; a term's
        // rewrites are bounded by its size
        let bound = 12 * shapes.Length + 8

        let rec loop (source: string) (steps: int) (trail: string list) =
            match nextFix source with
            | None -> steps
            | Some(s, fix) ->
                if steps >= bound then
                    failwithf
                        "no fixed point after %d fixes; the trail:\n%s\n--- original\n%s\n--- current\n%s"
                        steps
                        (String.concat "\n" (List.rev trail))
                        original
                        source

                let patched = applyFix source fix
                let compilation, _ = compile patched
                let errors = errorsOf compilation

                if not errors.IsEmpty then
                    failwithf
                        "%s \"%s\" breaks the compile:\n%s\n--- before\n%s\n--- after\n%s"
                        s.Code
                        fix.Title
                        (String.Join("\n", errors))
                        source
                        patched

                loop patched (steps + 1) ($"{s.Code} {fix.Title}" :: trail)

        let steps = loop original 0 []
        Prop.classify (steps = 0) "no rewrite" (Prop.classify (steps >= 5) "5+ rewrites" true))
