/// The invariants every rule promises, over generated programs: a fix
/// never leaves the file failing to compile, and no rule throws on a tree
/// and model the compiler recovered from damaged text.
module CSharp.Refactor.PropertyTests.RobustnessTests

open System
open Xunit
open FsCheck.Xunit
open FsCheck
open FsCheck.FSharp
open Microsoft.CodeAnalysis
open CSharp.Refactor
open CSharp.Refactor.Roslyn
open CSharp.Refactor.Tests.Harness
open CSharp.Refactor.PropertyTests

/// A rule's every fix — the primary and the editor-only alternatives
/// alike — applied alone to the compiling `source`, must compile.
let private checkFixes (source: string) =
    for s in suggest source do
        for fix in s.Fixes do
            let patched = applyFix source fix
            let compilation, _ = compile patched
            let errors = errorsAfterFix compilation

            if not errors.IsEmpty then
                failwithf
                    "%s \"%s\" at %d does not compile after the fix:\n%s\n--- before\n%s\n--- after\n%s"
                    s.Code
                    fix.Title
                    s.Span.Start
                    (String.Join("\n", errors))
                    source
                    patched

[<Property(MaxTest = 150, EndSize = 40)>]
let ``a generated program compiles, and every fix of every rule keeps it compiling`` () =
    Prop.forAll Programs.arbitrary (Programs.program >> checkFixes)

[<Property(MaxTest = 300, EndSize = 40)>]
let ``no rule throws on a damaged program, and fixes on a still-compiling one keep it compiling`` () =
    Prop.forAll (Arb.zip (Programs.arbitrary, Arb.fromGen Mutation.genMutations)) (fun (shapes, mutations) ->
        let damaged = Mutation.applyAll (Programs.program shapes) mutations
        let compilation, tree = compile damaged
        let broken = not (errorsOf compilation).IsEmpty

        if broken then
            // an error tree and model: the rules must survive them,
            // nothing more
            try
                let model = compilation.GetSemanticModel(tree, false)
                Rules.all tree model (Context.forTree None compilation tree false) |> ignore
            with ex ->
                failwithf "a rule threw on a broken compilation: %s\n--- source\n%s" (string ex) damaged
        else
            checkFixes damaged)

/// The generators exist to reach the rules: if a shape stops firing its
/// rule (a rule tightened, a shape drifted), this says which. Every shape
/// is tried on its own — deterministic, where a sample could miss a rare
/// one — and once more with every other shape beside it, since a rule may
/// hold in company it fires without.
[<Fact>]
let ``every targeted rule fires from the shapes written for it`` () =
    let fired = Collections.Generic.HashSet<string>()

    for shape in Programs.exemplars do
        for s in suggest (Programs.program [ shape ]) do
            fired.Add s.Code |> ignore

    let missing = Programs.targetedCodes |> List.filter (fired.Contains >> not)
    Assert.True(missing.IsEmpty, $"rules no shape alone reaches: %A{missing}")

    let together = Collections.Generic.HashSet<string>()

    for s in suggest (Programs.program Programs.exemplars) do
        together.Add s.Code |> ignore

    let held = Programs.targetedCodes |> List.filter (together.Contains >> not)
    Assert.True(held.IsEmpty, $"rules that fire alone but not beside every other shape: %A{held}")
