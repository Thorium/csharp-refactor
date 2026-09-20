/// Drive the real Roslyn entry points — the DiagnosticAnalyzer through
/// CompilationWithAnalyzers, the CodeFixProvider through a workspace
/// document — the way csc and an editor do.
module CSharp.Refactor.Tests.HostTests

open System.Collections.Immutable
open System.Threading
open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CodeActions
open Microsoft.CodeAnalysis.CodeFixes
open Microsoft.CodeAnalysis.Diagnostics
open Xunit
open CSharp.Refactor.Roslyn
open CSharp.Refactor.Tests.Harness

let private source =
    """
using System;
class C
{
    Guid A() => new Guid();
    string B() => $"plain";
}
"""

let private analyze (compilation: Compilation) =
    let analyzers = ImmutableArray.Create<DiagnosticAnalyzer>(CSharpRefactorAnalyzer())
    let withAnalyzers = compilation.WithAnalyzers analyzers

    withAnalyzers.GetAnalyzerDiagnosticsAsync(CancellationToken.None).Result
    |> Seq.sortBy (fun d -> d.Location.SourceSpan.Start)
    |> List.ofSeq

[<Fact>]
let ``the analyzer reports through CompilationWithAnalyzers`` () =
    let compilation, _ = compileClean source
    let diagnostics = analyze compilation
    Assert.Equal<string list>([ "CR0090"; "CR0103" ], diagnostics |> List.map (fun d -> d.Id))
    Assert.All(diagnostics, fun d -> Assert.EndsWith("]", d.GetMessage()))
    Assert.Equal(DiagnosticSeverity.Info, diagnostics.Head.Severity)

[<Fact>]
let ``the fix provider offers every fix of a diagnostic through a workspace`` () =
    task {
        use workspace = new AdhocWorkspace()

        let project =
            workspace
                .AddProject("Test", LanguageNames.CSharp)
                .WithCompilationOptions(
                    Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                )
                .AddMetadataReference(MetadataReference.CreateFromFile typeof<obj>.Assembly.Location)

        let document = project.AddDocument("Test.cs", source)
        let! compilation = document.Project.GetCompilationAsync()
        let diagnostics = analyze compilation
        let guid = diagnostics |> List.find (fun d -> d.Id = "CR0090")

        let actions = ResizeArray<CodeAction>()

        let context =
            CodeFixContext(document, guid, (fun action _ -> actions.Add action), CancellationToken.None)

        do! CSharpRefactorCodeFixProvider().RegisterCodeFixesAsync context

        Assert.Equal<string list>(
            [ "Use Guid.Empty"; "Use Guid.NewGuid()" ],
            actions |> Seq.map (fun a -> a.Title) |> List.ofSeq
        )

        let apply (action: CodeAction) =
            let operations = action.GetOperationsAsync(CancellationToken.None).Result

            let applied =
                operations
                |> Seq.pick (fun o ->
                    match o with
                    | :? ApplyChangesOperation as a -> Some a
                    | _ -> None)

            applied.ChangedSolution.GetDocument(document.Id).GetTextAsync().Result.ToString()

        Assert.Contains("Guid A() => Guid.Empty;", apply actions.[0])
        Assert.Contains("Guid A() => Guid.NewGuid();", apply actions.[1])
    }
    :> System.Threading.Tasks.Task

// ---- cross-file edit sets ----

[<Fact>]
let ``the fix provider rewrites a method's callers in another document through the reference oracle`` () =
    task {
        use workspace = new AdhocWorkspace()

        let project =
            workspace
                .AddProject("Test", LanguageNames.CSharp)
                .WithCompilationOptions(
                    Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                )
                .AddMetadataReferences
                metadataReferences

        let declaring =
            project.AddDocument(
                "Service.cs",
                "using System.Threading.Tasks;\nnamespace App;\ninternal static class Service\n{\n    static Task<int> Source() => Task.FromResult(1);\n    internal static int Load() { var x = Source().Result; return x; }\n}\n",
                filePath = "C:/fake/Service.cs"
            )

        let calling =
            declaring.Project.AddDocument(
                "Caller.cs",
                "using System.Threading.Tasks;\nnamespace App;\nclass Caller\n{\n    async Task<int> Run()\n    {\n        var x = Service.Load();\n        var y = Service.Load();\n        return x + y;\n    }\n}\n",
                filePath = "C:/fake/Caller.cs"
            )

        let declaring = calling.Project.GetDocument declaring.Id
        let! compilation = declaring.Project.GetCompilationAsync()
        Assert.Empty(errorsOf compilation)
        let diagnostics = analyze compilation
        // the compiler sees the caller in the other tree of its own compilation
        Assert.Contains(diagnostics, fun d -> d.Id = "CR0041")

        let! tree = declaring.GetSyntaxTreeAsync()
        let! model = declaring.GetSemanticModelAsync()

        let ctx =
            { Context.forTree None compilation tree false with
                References = Some(References.oracle declaring.Project.Solution tree)
            }

        let suggestions =
            Rules.all tree model ctx |> List.filter (fun s -> s.Code = "CR0041")

        Assert.Equal(1, suggestions.Length)
        let fix = suggestions.Head.Fixes.Head

        Assert.Equal(
            4,
            fix.Edits
            |> List.filter (fun e -> e.File = Some "C:/fake/Caller.cs")
            |> List.length
        )

        // through the provider: a diagnostic at the span, the action changes both documents
        let diagnostic =
            Diagnostic.Create(Descriptors.byCode.["CR0041"], Location.Create(tree, suggestions.Head.Span), "x")

        let actions = ResizeArray<CodeAction>()

        let context =
            CodeFixContext(declaring, diagnostic, (fun action _ -> actions.Add action), CancellationToken.None)

        do! CSharpRefactorCodeFixProvider().RegisterCodeFixesAsync context
        Assert.Equal(1, actions.Count)
        let! operations = actions.[0].GetOperationsAsync CancellationToken.None

        let changed =
            operations
            |> Seq.pick (fun o ->
                match o with
                | :? ApplyChangesOperation as a -> Some a.ChangedSolution
                | _ -> None)

        let service = changed.GetDocument(declaring.Id).GetTextAsync().Result.ToString()
        let caller = changed.GetDocument(calling.Id).GetTextAsync().Result.ToString()
        Assert.Contains("internal static async Task<int> LoadAsync() { var x = await Source(); return x; }", service)
        Assert.Contains("var x = await Service.LoadAsync();", caller)
        Assert.Contains("var y = await Service.LoadAsync();", caller)
        let! after = changed.GetProject(declaring.Project.Id).GetCompilationAsync()
        Assert.Empty(errorsOf after)
    }
    :> System.Threading.Tasks.Task

[<Fact>]
let ``a reference tuple spelled in two files of one compilation is retyped as one edit set from the first declaring file``
    ()
    =
    task {
        use workspace = new AdhocWorkspace()

        let project =
            workspace
                .AddProject("Test", LanguageNames.CSharp)
                .WithCompilationOptions(
                    Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                )
                .AddMetadataReferences
                metadataReferences

        let a =
            project.AddDocument(
                "A.cs",
                "using System;\nnamespace App;\ninternal static class Maker\n{\n    internal static Tuple<int, string> Make() => Tuple.Create(1, \"a\");\n}\n",
                filePath = "C:/fake/A.cs"
            )

        let b =
            a.Project.AddDocument(
                "B.cs",
                "using System;\nnamespace App;\nclass User\n{\n    int Use()\n    {\n        Tuple<int, string> t = Maker.Make();\n        return t.Item1;\n    }\n}\n",
                filePath = "C:/fake/B.cs"
            )

        let a = b.Project.GetDocument a.Id
        let! compilation = a.Project.GetCompilationAsync()
        Assert.Empty(errorsOf compilation)

        let suggestionsIn (document: Document) =
            let tree = document.GetSyntaxTreeAsync().Result
            let model = document.GetSemanticModelAsync().Result

            Rules.all tree model (Context.forTree None compilation tree false)
            |> List.filter (fun s -> s.Code = "CR0082")

        // B holds no declaration of its own that reports; A reports the set for both
        Assert.Empty(suggestionsIn b)
        let fromA = suggestionsIn a
        Assert.Equal(1, fromA.Length)
        let fix = fromA.Head.Fixes.Head
        Assert.Contains(fix.Edits, fun e -> e.File = Some "C:/fake/B.cs")

        let diagnostic =
            Diagnostic.Create(
                Descriptors.byCode.["CR0082"],
                Location.Create(a.GetSyntaxTreeAsync().Result, fromA.Head.Span),
                "x"
            )

        let actions = ResizeArray<CodeAction>()

        let context =
            CodeFixContext(a, diagnostic, (fun action _ -> actions.Add action), CancellationToken.None)

        do! CSharpRefactorCodeFixProvider().RegisterCodeFixesAsync context
        let! operations = actions.[0].GetOperationsAsync CancellationToken.None

        let changed =
            operations
            |> Seq.pick (fun o ->
                match o with
                | :? ApplyChangesOperation as op -> Some op.ChangedSolution
                | _ -> None)

        let textA = changed.GetDocument(a.Id).GetTextAsync().Result.ToString()
        let textB = changed.GetDocument(b.Id).GetTextAsync().Result.ToString()
        Assert.Contains("internal static (int, string) Make() => (1, \"a\");", textA)
        Assert.Contains("(int, string) t = Maker.Make();", textB)
        let! after = changed.GetProject(a.Project.Id).GetCompilationAsync()
        Assert.Empty(errorsOf after)
    }
    :> System.Threading.Tasks.Task
