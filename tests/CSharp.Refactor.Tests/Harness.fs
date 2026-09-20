/// The test harness: compile a C# string against the running framework's
/// references, run the pure rules, apply fixes as text, and recompile.
/// Test inputs are string literals so formatting tools never touch the
/// deliberately-shaped fragments.
module CSharp.Refactor.Tests.Harness

open System
open System.IO
open System.Linq
open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.Diagnostics
open Microsoft.CodeAnalysis.Text
open CSharp.Refactor
open CSharp.Refactor.Roslyn

/// Every assembly of the running runtime's shared framework: what a
/// `net10.0` project references, without a NuGet download.
let private references: MetadataReference list =
    let dir = Path.GetDirectoryName typeof<obj>.Assembly.Location

    Directory.GetFiles(dir, "*.dll")
    |> Array.filter (fun f ->
        let n = Path.GetFileName f
        n.StartsWith "System." || n = "mscorlib.dll" || n = "netstandard.dll")
    |> Array.choose (fun f ->
        // the shared framework directory also holds native dlls
        // (System.IO.Compression.Native.dll); only managed ones are references
        try
            use stream = File.OpenRead f
            use pe = new System.Reflection.PortableExecutable.PEReader(stream)

            if pe.HasMetadata then
                Some(MetadataReference.CreateFromFile f :> MetadataReference)
            else
                None
        with _ ->
            None)
    |> List.ofArray
    // xUnit, so a fixture can declare a `[Fact]` the way a test project does
    |> List.append
        [
            MetadataReference.CreateFromFile typeof<Xunit.FactAttribute>.Assembly.Location
            MetadataReference.CreateFromFile typeof<Xunit.Assert>.Assembly.Location
            MetadataReference.CreateFromFile typeof<Xunit.Abstractions.ITestOutputHelper>.Assembly.Location
        ]

/// The same references, for a test that builds its own workspace.
let metadataReferences: MetadataReference list = references

let parseOptions = CSharpParseOptions(LanguageVersion.Latest)

/// Test inputs are written as triple-quoted literals, whose line endings
/// follow the test FILE's (fantomas writes CRLF); the rules and the
/// assertions want one convention, so every input is LF here.
let normalize (source: string) = source.Replace("\r\n", "\n")

let compile (source: string) : CSharpCompilation * SyntaxTree =
    let tree =
        CSharpSyntaxTree.ParseText(normalize source, parseOptions, path = "Sample.cs")

    let compilation =
        CSharpCompilation.Create(
            "Test",
            [ tree ],
            references,
            CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions = NullableContextOptions.Enable
            )
        )

    compilation, tree

let errorsOf (compilation: Compilation) =
    compilation.GetDiagnostics()
    |> Seq.filter (fun d -> d.Severity = DiagnosticSeverity.Error)
    |> Seq.map (fun d -> d.ToString())
    |> List.ofSeq

/// The errors a fixed program may carry: a source generator's pending body
/// (CS8795 under `[GeneratedRegex]`) is the build's to supply, not the harness's.
let errorsAfterFix (compilation: Compilation) =
    compilation.GetDiagnostics()
    |> Seq.filter (fun d -> d.Severity = DiagnosticSeverity.Error && d.Id <> "CS8795")
    |> Seq.map (fun d -> d.ToString())
    |> List.ofSeq

/// The source must compile clean: a test over a broken fragment tests nothing.
let compileClean (source: string) =
    let compilation, tree = compile source
    let errors = errorsAfterFix compilation

    if not errors.IsEmpty then
        failwithf "test input does not compile:\n%s" (String.Join("\n", errors))

    compilation, tree

/// A config the tests can fill.
type FakeOptions(values: System.Collections.Generic.IDictionary<string, string>) =
    inherit AnalyzerConfigOptions()

    override _.TryGetValue(key, value) =
        match values.TryGetValue key with
        | true, v ->
            value <- v
            true
        | _ ->
            value <- null
            false

/// Run every rule over the source under a config.
let suggestWith (options: AnalyzerConfigOptions option) (source: string) : Suggestion list =
    let compilation, tree = compileClean source
    let model = compilation.GetSemanticModel(tree, false)
    Rules.all tree model (Context.forTree options compilation tree false)

/// Run every rule over the source.
let suggest (source: string) : Suggestion list = suggestWith None source

/// Run one rule's code over the source under a config.
let suggestCodeWith (options: AnalyzerConfigOptions option) (code: string) (source: string) =
    suggestWith options source |> List.filter (fun s -> s.Code = code)

/// Run one rule's code over the source.
let suggestCode (code: string) (source: string) : Suggestion list = suggestCodeWith None code source

/// Apply a fix's edits as text, bottom-up so earlier spans stay valid.
let applyFix (source: string) (fix: Fix) : string =
    let text = SourceText.From(normalize source)
    let changes = fix.Edits |> List.map (fun e -> TextChange(e.Span, e.Replacement))
    text.WithChanges(changes).ToString()

/// Apply the PRIMARY (non-editor-only) fix of every suggestion of one rule,
/// and check the result still compiles clean.
/// As `fixAllWith`, tolerating the listed error ids in the result (an error a
/// source generator resolves once it runs, which this harness never does).
let fixAllAllowing
    (allowed: string list)
    (options: AnalyzerConfigOptions option)
    (code: string)
    (source: string)
    : string =
    let suggestions = suggestCodeWith options code source

    let edits =
        suggestions
        |> List.collect (fun s ->
            s.Fixes
            |> List.filter (fun f -> not f.EditorOnly)
            |> List.truncate 1
            |> List.collect (fun f -> f.Edits))
        // two fixes asking for the same insertion need it once, as in the sweep
        |> List.distinctBy (fun e -> e.Span, e.Replacement)

    let text = SourceText.From(normalize source)
    let changes = edits |> List.map (fun e -> TextChange(e.Span, e.Replacement))
    let result = text.WithChanges(changes).ToString()
    let compilation, _ = compile result

    let errors =
        errorsOf compilation
        |> List.filter (fun e -> not (allowed |> List.exists (fun id -> e.Contains id)))

    if not errors.IsEmpty then
        failwithf "fixed source does not compile:\n%s\n---\n%s" (String.Join("\n", errors)) result

    result

let fixAllWith (options: AnalyzerConfigOptions option) (code: string) (source: string) : string =
    fixAllAllowing [] options code source

/// Apply the primary fix of every suggestion of one rule, no config.
let fixAll (code: string) (source: string) : string = fixAllWith None code source

/// The text at each suggestion's span, for asserting WHAT fired.
let firedText (source: string) (suggestions: Suggestion list) =
    let source = normalize source
    suggestions |> List.map (fun s -> source.Substring(s.Span.Start, s.Span.Length))

/// Compile the source AS IS — its own line endings, at a chosen language
/// version — for the properties that watch what a fix does to the file's
/// conventions, which `compile`'s normalisation would hide.
let compileRaw (version: LanguageVersion) (source: string) : CSharpCompilation * SyntaxTree =
    let tree =
        CSharpSyntaxTree.ParseText(source, CSharpParseOptions(version), path = "Sample.cs")

    let compilation =
        CSharpCompilation.Create(
            "Test",
            [ tree ],
            references,
            CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions = NullableContextOptions.Enable
            )
        )

    compilation, tree

/// Every rule over a raw compilation: the context reads the language
/// version off the tree, as the analyzer does.
let suggestRaw (compilation: CSharpCompilation) (tree: SyntaxTree) : Suggestion list =
    let model = compilation.GetSemanticModel(tree, false)
    Rules.all tree model (Context.forTree None compilation tree false)

/// A fix's edits applied to the text as it is, nothing normalised.
let applyFixRaw (source: string) (fix: Fix) : string =
    let text = SourceText.From source
    text.WithChanges(fix.Edits |> List.map (fun e -> TextChange(e.Span, e.Replacement))).ToString()
