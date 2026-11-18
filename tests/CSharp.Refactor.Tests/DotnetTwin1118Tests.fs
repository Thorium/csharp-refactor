/// C# twins of the wrong fixes the 2026-11-18 fsharp-refactor sweep of
/// dotnet/fsharp found (FSharp.Refactor.Tests.DotnetSweep1118Tests). Each
/// test asserts the behaviour the rule should have: code an inactive `#if`
/// holds is code another framework or configuration compiles, so a mention
/// there counts as a caller or a write.
module CSharp.Refactor.Tests.DotnetTwin1118Tests

open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Xunit
open CSharp.Refactor
open CSharp.Refactor.Roslyn
open CSharp.Refactor.Tests.Harness

let private fixed' (suggestions: Suggestion list) =
    suggestions |> List.filter (fun s -> not s.Fixes.IsEmpty)

// ---- signature changes: a caller inside an inactive #if ----

[<Fact; Trait("Category", "Sweep1118")>]
let ``CR0041 keeps a sync method a caller under an inactive #if still calls`` () =
    // twin of `a call site inside an #if NETCOREAPP branch holds the api migration back`
    let source (inactive: string) =
        csharp (
            """
            using System.Threading.Tasks;
            class C
            {
                Task<int> Load() => Task.FromResult(1);
                private int Fetch(int n) { var x = Load().Result; return x + n; }
                async Task<int> A() { var r = Fetch(1); return r; }
            """
            + inactive
            + """
            }
            """
        )

    // control: with no other caller the method becomes async
    Assert.NotEmpty(fixed' (suggestCode "CR0041" (source "")))

    let withSyncCaller =
        source
            """
            #if NETFRAMEWORK
                public int RunSync() => Fetch(2);
            #endif
            """

    Assert.Empty(fixed' (suggestCode "CR0041" withSyncCaller))

[<Fact; Trait("Category", "Sweep1118")>]
let ``CR0043 keeps an async void method a caller under an inactive #if calls from sync code`` () =
    // twin of `a call site inside an #if NETCOREAPP branch holds the api migration back`
    let source (inactive: string) =
        csharp (
            """
            using System.Threading.Tasks;
            class C
            {
                private async void Refresh() { await Task.Yield(); }
                public async Task RunAsync()
                {
                    await Task.Yield();
                    Refresh();
                }
            """
            + inactive
            + """
            }
            """
        )

    Assert.NotEmpty(fixed' (suggestCode "CR0043" (source "")))

    let withSyncCaller =
        source
            """
            #if NETFRAMEWORK
                public void RunSync() => Refresh();
            #endif
            """

    Assert.Empty(fixed' (suggestCode "CR0043" withSyncCaller))

[<Fact; Trait("Category", "Sweep1118")>]
let ``CR0083 keeps a setter an inactive #if branch assigns after construction`` () =
    // twin of `a call site inside an #if NETCOREAPP branch holds the api migration back`
    let source (inactive: string) =
        csharp (
            """
            class Options { public int Port { get; set; } }
            static class Make
            {
                public static Options Create() => new Options { Port = 80 };
                public static void Tweak(Options o)
                {
            """
            + inactive
            + """
                }
            }
            """
        )

    Assert.NotEmpty(fixed' (suggestCode "CR0083" (source "")))

    let withLateWrite =
        source
            """
            #if NETCOREAPP
                    o.Port = 443;
            #endif
            """

    Assert.Empty(fixed' (suggestCode "CR0083" withLateWrite))

/// Several files of one compilation, and the rule's suggestions for the first.
let private suggestAcross (code: string) (files: (string * string) list) =
    let trees =
        files
        |> List.map (fun (path, text) -> CSharpSyntaxTree.ParseText(normalize text, parseOptions, path = path))

    let compilation =
        CSharpCompilation.Create(
            "Across",
            trees,
            metadataReferences,
            CSharpCompilationOptions OutputKind.DynamicallyLinkedLibrary
        )

    Assert.Empty(errorsOf compilation)
    let tree = trees.Head

    Rules.all tree (compilation.GetSemanticModel(tree, false)) (Context.forTree None compilation tree false)
    |> List.filter (fun s -> s.Code = code)

[<Fact; Trait("Category", "Sweep1118")>]
let ``CR0083 reads a bare assignment under an inactive #if as a write only where it can be the property`` () =
    let options =
        csharp
            """
            class Options { public int Port { get; set; } }
            static class Make { public static Options Create() => new Options { Port = 80 }; }
            """

    // another file's inactive code: an initializer of the type (construction, which `init`
    // allows), an attribute argument and another type's own `Port` - none writes this property
    let unrelated =
        csharp
            """
            using System;
            [AttributeUsage(AttributeTargets.Class)]
            sealed class NoteAttribute : Attribute { public int Port { get; set; } }
            class Other
            {
                int Port;
                public int Read() => Port;
            #if NETFRAMEWORK
                public static Options Old() => new Options { Port = 8080 };
                [Note(Port = 1)] class Marked { }
                public void Reset() { Port = 0; }
            #endif
            }
            """

    Assert.NotEmpty(fixed' (suggestAcross "CR0083" [ "Options.cs", options; "Other.cs", unrelated ]))

    // a type derived from it writes the inherited property bare
    let derived (inactive: string) =
        csharp (
            """
            class Derived : Options
            {
            """
            + inactive
            + """
            }
            """
        )

    Assert.NotEmpty(fixed' (suggestAcross "CR0083" [ "Options.cs", options; "Derived.cs", derived "" ]))

    let resets =
        derived
            """
            #if NETFRAMEWORK
                public void Reset() { Port = 0; }
            #endif
            """

    Assert.Empty(fixed' (suggestAcross "CR0083" [ "Options.cs", options; "Derived.cs", resets ]))

// ---- writes an inactive #if holds, read off its text ----

[<Fact; Trait("Category", "Sweep1118")>]
let ``CR0180 leaves static fields an inactive #if writes through address-of, deconstruction or a verbatim ref`` () =
    // twin of `FR0007 keeps mutable on a primed name whose address is taken`
    let source =
        csharp
            """
            using System.Threading;
            class C
            {
                private static int Limit = 3;
                private static int Depth = 4;
                private static int Width = 5;
                static void Probe()
                {
            #if HAVE_NATIVE
                    fixed (int* p = &Limit) { *p = 7; }
                    (Depth, _) = (1, 2);
                    Interlocked.Exchange(ref @Width, 6);
            #endif
                }
                static int Sum() => Limit + Depth + Width;
            }
            """

    Assert.Equal<string list>([], firedText source (suggestCode "CR0180" source))

[<Fact; Trait("Category", "Sweep1118")>]
let ``CR0172 leaves locals an inactive #if writes through deconstruction or a verbatim ref`` () =
    // twin of `FR0007 keeps mutable on a primed name whose address is taken`
    let source =
        csharp
            """
            using System.Threading;
            class C
            {
                static int Show()
                {
                    int depth = 4;
                    int width = 5;
            #if HAVE_TUNING
                    (depth, _) = (1, 2);
                    Interlocked.Exchange(ref @width, 6);
            #endif
                    return depth + width;
                }
            }
            """

    Assert.Equal<string list>([], firedText source (suggestCode "CR0172" source))
