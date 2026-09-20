/// The conventions of a real file, and the language version it is
/// compiled at, must not change what a fix may do: a generated program
/// dressed in `#region`s, a `#if` inside a member, comments, CRLF, tabs, a
/// `#pragma` — every fix still compiles and leaves what was there; and at
/// every language version from C# 7.3 up, a fix uses no syntax newer than
/// the file's own.
module CSharp.Refactor.PropertyTests.ConventionTests

open System
open FsCheck.Xunit
open FsCheck
open FsCheck.FSharp
open Microsoft.CodeAnalysis.CSharp
open CSharp.Refactor
open CSharp.Refactor.Tests.Harness
open CSharp.Refactor.PropertyTests

/// Every fix of every rule, applied alone to the decorated program: it
/// compiles, and the decoration's promise holds.
let private checkDecorated (d: Decorations.Decoration) (program: string) =
    let decorated = Decorations.apply d program
    let compilation, tree = compileRaw LanguageVersion.Latest decorated
    let errors = errorsAfterFix compilation

    if not errors.IsEmpty then
        failwithf "the decorated program does not compile:\n%s\n--- source\n%s" (String.Join("\n", errors)) decorated

    for s in suggestRaw compilation tree do
        for fix in s.Fixes do
            let patched = applyFixRaw decorated fix
            let after, _ = compileRaw LanguageVersion.Latest patched
            let errors = errorsAfterFix after

            if not errors.IsEmpty then
                failwithf
                    "%s \"%s\" under %A does not compile after the fix:\n%s\n--- before\n%s\n--- after\n%s"
                    s.Code
                    fix.Title
                    d
                    (String.Join("\n", errors))
                    decorated
                    patched

            match Decorations.invariant d decorated patched with
            | Some broken ->
                failwithf
                    "%s \"%s\" under %A: %s\n--- before\n%s\n--- after\n%s"
                    s.Code
                    fix.Title
                    d
                    broken
                    decorated
                    patched
            | None -> ()

[<Property(MaxTest = 120, EndSize = 40)>]
let ``regions, directives, comments, CRLF, tabs and pragmas neither break a fix nor get lost to one`` () =
    Prop.forAll (Arb.zip (Programs.arbitrary, Arb.fromGen Decorations.genDecoration)) (fun (shapes, d) ->
        checkDecorated d (Programs.program shapes))

/// The language versions a project may pin: the fix at each must parse
/// and bind at that version.
let private versions =
    [
        LanguageVersion.CSharp7_3
        LanguageVersion.CSharp8
        LanguageVersion.CSharp9
        LanguageVersion.CSharp10
        LanguageVersion.CSharp11
        LanguageVersion.CSharp12
        LanguageVersion.CSharp13
    ]

/// The shapes a version can express: a shape whose own program does not
/// compile at that version (a record below C# 9, `new()` below C# 9) is
/// not a file that project has.
let private expressible (version: LanguageVersion) (shapes: Programs.Shape list) =
    shapes
    |> List.filter (fun shape ->
        let compilation, _ = compileRaw version (Programs.program [ shape ])
        (errorsAfterFix compilation).IsEmpty)

[<Property(MaxTest = 120, EndSize = 40)>]
let ``at every language version from C# 7.3 up, a fix uses no syntax newer than the file's own`` () =
    Prop.forAll (Arb.zip (Programs.arbitrary, Arb.fromGen (Gen.elements versions))) (fun (shapes, version) ->
        match expressible version shapes with
        | [] -> ()
        | shapes ->
            let program = Programs.program shapes
            let compilation, tree = compileRaw version program

            for s in suggestRaw compilation tree do
                for fix in s.Fixes do
                    let patched = applyFixRaw program fix
                    let after, _ = compileRaw version patched
                    let errors = errorsAfterFix after

                    if not errors.IsEmpty then
                        failwithf
                            "%s \"%s\" at %A does not compile after the fix:\n%s\n--- before\n%s\n--- after\n%s"
                            s.Code
                            fix.Title
                            version
                            (String.Join("\n", errors))
                            program
                            patched)

/// The same, shape by shape and version by version, so no rare pairing
/// waits on a sample: every shape a version can express, fixed at it.
[<Xunit.Fact>]
let ``every shape's fixes compile at every language version that can express it`` () =
    let failures = ResizeArray<string>()

    for version in versions do
        for shape in Programs.exemplars do
            let program = Programs.program [ shape ]
            let compilation, tree = compileRaw version program

            if (errorsAfterFix compilation).IsEmpty then
                for s in suggestRaw compilation tree do
                    for fix in s.Fixes do
                        let after, _ = compileRaw version (applyFixRaw program fix)
                        let errors = errorsAfterFix after

                        if not errors.IsEmpty then
                            failures.Add $"{s.Code} \"{fix.Title}\" on {shape} at {version}: {errors.Head}"

    Xunit.Assert.True(failures.Count = 0, String.Join("\n", failures))

/// And every decoration on every shape alone, deterministically.
[<Xunit.Fact>]
let ``every shape survives every decoration`` () =
    for d in Decorations.all do
        for shape in Programs.exemplars do
            checkDecorated d (Programs.program [ shape ])
