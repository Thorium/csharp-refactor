/// The boolean and control-flow rewrites preserve meaning. A generated
/// body is printed as a method of three integers; the rules' primary fixes
/// are applied one suggestion at a time until none is left; after every
/// fix the program must still compile and must still compute what the
/// original body computes, at every point of the environment grid. And
/// the process must stop: a rewrite that undoes another would loop here.
module CSharp.Refactor.PropertyTests.BooleanSemanticsTests

open System
open FsCheck.Xunit
open FsCheck
open FsCheck.FSharp
open CSharp.Refactor
open CSharp.Refactor.Tests.Harness
open CSharp.Refactor.PropertyTests
open CSharp.Refactor.PropertyTests.BoolExpr
open CSharp.Refactor.PropertyTests.Bodies

/// The method evaluated over the whole grid.
let private truthTable (source: string) : bool list =
    let _, tree = compileClean source
    envs |> List.map (Interpreter.run tree)

/// The first suggestion by position that carries a primary fix, with it.
let private nextFix (source: string) : (Suggestion * Fix) option =
    suggest source
    |> List.choose (fun s ->
        s.Fixes
        |> List.tryFind (fun f -> not f.EditorOnly)
        |> Option.map (fun f -> s, f))
    |> List.sortBy (fun (s, _) -> s.Span.Start)
    |> List.tryHead

/// Apply the first fix, recompile, compare; repeat to a fixed point.
/// Returns how many fixes it took.
let private rewriteToFixedPoint (body: Body) : int =
    let expected = envs |> List.map (eval body)
    let bound = 4 * sizeOf body + 8

    let rec loop (source: string) (steps: int) (trail: string list) =
        match nextFix source with
        | None -> steps
        | Some(s, fix) ->
            if steps >= bound then
                failwithf
                    "no fixed point after %d fixes (body size %d); the trail:\n%s"
                    steps
                    (sizeOf body)
                    (String.concat "\n---\n" (List.rev trail))

            let patched = applyFix source fix
            let compilation, _ = compile patched
            let errors = errorsAfterFix compilation

            if not errors.IsEmpty then
                failwithf
                    "%s \"%s\" at %d breaks the compile:\n%s\n--- before\n%s\n--- after\n%s"
                    s.Code
                    fix.Title
                    s.Span.Start
                    (String.Join("\n", errors))
                    source
                    patched

            let actual = truthTable patched

            if actual <> expected then
                let differing =
                    List.zip3 envs expected actual
                    |> List.filter (fun (_, e, a) -> e <> a)
                    |> List.map (fun (env, e, a) -> $"x0={env.[0]} x1={env.[1]} x2={env.[2]}: expected {e}, got {a}")

                failwithf
                    "%s \"%s\" at %d changes the value:\n--- before\n%s\n--- after\n%s\n--- at\n%s"
                    s.Code
                    fix.Title
                    s.Span.Start
                    source
                    patched
                    (String.concat "\n" differing)

            loop patched (steps + 1) ($"{s.Code} {fix.Title}" :: trail)

    loop (program body) 0 []

[<Property(MaxTest = 300, EndSize = 60)>]
let ``the boolean and control-flow rewrites keep the method's truth table and reach a fixed point`` () =
    Prop.forAll Bodies.arbitrary (fun body ->
        let steps = rewriteToFixedPoint body

        // the distribution says whether the generator still reaches the
        // rules: a run where nothing ever fires proves nothing
        Prop.classify (steps = 0) "no rewrite" (Prop.classify (steps >= 3) "3+ rewrites" true))

[<Property(MaxTest = 100, EndSize = 60)>]
let ``the printed body computes what the body computes`` () =
    // the oracle and the interpreter agree BEFORE any rule runs: a
    // disagreement here is a test bug, not a rule bug
    Prop.forAll Bodies.arbitrary (fun body ->
        let expected = envs |> List.map (eval body)
        let actual = truthTable (program body)
        expected = actual)
