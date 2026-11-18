/// Wrong fixes the 2026-11-18 csharp-refactor sweep of dotnet/aspnetcore
/// (AspNetCore.slnx, tool at c046553) applied or had to bisect away. The
/// sweep is not repeated: each shape lives here, asserting the behaviour
/// the rule should have.
module CSharp.Refactor.Tests.DotnetSweep1118Tests

open System
open System.IO
open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.Text
open Xunit
open CSharp.Refactor
open CSharp.Refactor.Roslyn
open CSharp.Refactor.Tests.Harness

let private fixed' (suggestions: Suggestion list) =
    suggestions |> List.filter (fun s -> not s.Fixes.IsEmpty)

/// The netstandard2.0 reference assembly in the NuGet cache (the test
/// project downloads the package): no `IsExternalInit`, and
/// `string.IsNullOrEmpty` without `[NotNullWhen(false)]`. A cache without it
/// fails the test that asks, where skipping would pass it untested.
let private netstandard20 =
    lazy
        (let packages =
            match Environment.GetEnvironmentVariable "NUGET_PACKAGES" with
            | null
            | "" -> Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile, ".nuget", "packages")
            | root -> root

         let path =
             Path.Combine(packages, "netstandard.library", "2.0.3", "build", "netstandard2.0", "ref", "netstandard.dll")

         Assert.True(File.Exists path, $"{path} is missing: restore the test project")
         path)

let private nullableLibrary =
    CSharpCompilationOptions(
        OutputKind.DynamicallyLinkedLibrary,
        nullableContextOptions = NullableContextOptions.Enable
    )

let private suggestOn (compilation: CSharpCompilation) (tree: SyntaxTree) (code: string) =
    let errors =
        compilation.GetDiagnostics()
        |> Seq.filter (fun d -> d.Severity = DiagnosticSeverity.Error)
        |> List.ofSeq

    if not errors.IsEmpty then
        failwithf "test input does not compile:\n%s" (String.Join("\n", errors))

    let model = compilation.GetSemanticModel(tree, false)

    Rules.all tree model (Context.forTree None compilation tree false)
    |> List.filter (fun s -> s.Code = code)

/// Project A and project B both compile `C:/fake/Shared/Shared.cs` (a
/// `<Compile Include="..\Shared\…" />` link, as aspnetcore's src/Shared is):
/// one rule on A's copy, with the solution oracle the tool lends the rules.
let private linkedFile
    (code: string)
    (shared: string)
    (ownFiles: (string * string) list)
    (otherReferences: MetadataReference list)
    (otherFiles: (string * string) list)
    =
    let workspace = new AdhocWorkspace()
    let a = ProjectId.CreateNewId "A"
    let b = ProjectId.CreateNewId "B"

    let project (id: ProjectId) (name: string) (references: MetadataReference list) =
        ProjectInfo.Create(
            id,
            VersionStamp.Default,
            name,
            name,
            LanguageNames.CSharp,
            compilationOptions = nullableLibrary,
            parseOptions = parseOptions,
            metadataReferences = references
        )

    let sharedPath = "C:/fake/Shared/Shared.cs"
    let docA = DocumentId.CreateNewId a

    let solution =
        workspace.CurrentSolution
            .AddProject(project a "A" metadataReferences)
            .AddProject(project b "B" otherReferences)
            .AddDocument(docA, "Shared.cs", SourceText.From(normalize shared), filePath = sharedPath)
            .AddDocument(
                DocumentId.CreateNewId b,
                "Shared.cs",
                SourceText.From(normalize shared),
                filePath = sharedPath
            )

    let add (project: ProjectId) (folder: string) (files: (string * string) list) (solution: Solution) =
        files
        |> List.fold
            (fun (s: Solution) (name, text) ->
                s.AddDocument(
                    DocumentId.CreateNewId project,
                    name,
                    SourceText.From(normalize text),
                    filePath = "C:/fake/" + folder + "/" + name
                ))
            solution

    let solution = solution |> add a "A" ownFiles |> add b "B" otherFiles

    let document = solution.GetDocument docA

    let compilation =
        document.Project.GetCompilationAsync().Result :?> CSharpCompilation

    let tree = document.GetSyntaxTreeAsync().Result
    let model = compilation.GetSemanticModel(tree, false)
    Assert.Empty(errorsAfterFix compilation)

    let ctx =
        { Context.forTree None compilation tree false with
            References = Some(References.oracle solution tree)
        }

    solution, tree, Rules.all tree model ctx |> List.filter (fun s -> s.Code = code)

/// What the rule alone says of A's copy.
let private onLinkedFile
    (code: string)
    (shared: string)
    (ownFiles: (string * string) list)
    (otherReferences: MetadataReference list)
    (otherFiles: (string * string) list)
    =
    let _, _, suggestions = linkedFile code shared ownFiles otherReferences otherFiles
    suggestions

/// What a host with the solution keeps of it: the fixes B offers on its own
/// copy too (`Linked.everyCompilation`, as the tool and the editor apply it).
let private hostedOnLinkedFile
    (code: string)
    (shared: string)
    (ownFiles: (string * string) list)
    (otherReferences: MetadataReference list)
    (otherFiles: (string * string) list)
    =
    let solution, tree, suggestions =
        linkedFile code shared ownFiles otherReferences otherFiles

    let context (project: Project) (compilation: Compilation) (theirTree: SyntaxTree) =
        { Context.forTree None compilation theirTree false with
            References = Some(References.oracle project.Solution theirTree)
        }

    Linked.everyCompilation context Threading.CancellationToken.None solution tree suggestions

// ---- CR0012 ----

[<Fact; Trait("Category", "Sweep1118")>]
let ``CR0012 leaves a default arm that returns the method's own no-result for unknown input`` () =
    // aspnetcore RouteUsageDetector.CalculateHttpMethods: `default: // Unknown/unsupported method. return default;`
    // became `throw new NotImplementedException()` - every MapMethods call would crash the analyzer
    let source =
        csharp
            """
            using System.Collections.Immutable;
            static class Detector
            {
                public static ImmutableArray<string> HttpMethods(string name, bool known)
                {
                    if (known)
                    {
                        var builder = ImmutableArray.CreateBuilder<string>();
                        switch (name)
                        {
                            case "MapGet":
                                builder.Add("GET");
                                break;
                            case "MapPost":
                                builder.Add("POST");
                                break;
                            case "Map":
                                // No HTTP methods.
                                break;
                            default:
                                // Unknown/unsupported method.
                                return default;
                        }
                        return builder.ToImmutable();
                    }
                    return default;
                }
            }
            """

    Assert.Empty(fixed' (suggestCode "CR0012" source))

// ---- CR0171 ----

[<Fact; Trait("Category", "Sweep1118")>]
let ``CR0171 leaves a mutation whose block ends in break and still reports one that loops on`` () =
    // aspnetcore src/Shared/Metrics/MetricsExtensions.cs: `tags.Remove(tag); tags.Add(…); break;`
    // got `foreach (var tag in tags.ToList())` - the loop never moves again, and the comment says it avoids allocating
    let source =
        csharp
            """
            using System.Collections.Generic;
            static class Tags
            {
                public static void Set(ICollection<KeyValuePair<string, object?>> tags, string name, object? value)
                {
                    foreach (var tag in tags)
                    {
                        if (tag.Key == name)
                        {
                            tags.Remove(tag);
                            tags.Add(new KeyValuePair<string, object?>(name, value));
                            break;
                        }
                    }
                }
                public static void Drop(ICollection<KeyValuePair<string, object?>> others, string name)
                {
                    foreach (var other in others)
                    {
                        if (other.Key == name)
                        {
                            others.Remove(other);
                        }
                    }
                }
            }
            """

    let fired = firedText source (suggestCode "CR0171" source)
    Assert.Equal(1, fired.Length)
    Assert.Contains("others", fired.Head)

// ---- CR0083 ----

let private sharedOption =
    csharp
        """
        namespace Tools
        {
            internal sealed class CommandOption
            {
                public CommandOption(string template) { Template = template; }
                public string Template { get; }
                public string? Description { get; set; }
            }
            internal static class Options
            {
                public static CommandOption Verbose() => new CommandOption("-v") { Description = "verbose" };
            }
        }
        """

[<Fact; Trait("Category", "Sweep1118")>]
let ``CR0083 keeps a setter another project assigns through its own copy of a linked file`` () =
    // aspnetcore src/Shared/CommandLineUtils: `Description { get; init; }` broke dotnet-user-jwts,
    // dotnet-user-secrets, dotnet-sql-cache, ClientSample (CS8852), which compile the same file
    let assigns =
        csharp
            """
            namespace Tools
            {
                internal static class Use
                {
                    public static void Run()
                    {
                        var help = new CommandOption("-h");
                        help.Description = "help";
                    }
                }
            }
            """

    // the control: with no other project assigning it, the setter does go
    Assert.Single(fixed' (onLinkedFile "CR0083" sharedOption [] metadataReferences []))
    |> ignore

    Assert.Empty(fixed' (onLinkedFile "CR0083" sharedOption [] metadataReferences [ "Use.cs", assigns ]))

[<Fact; Trait("Category", "Sweep1118")>]
let ``CR0083 keeps a setter in a linked file another project compiles without IsExternalInit`` () =
    // aspnetcore GetDocument.Insider(net462), ClientSample(net462): CS0518 IsExternalInit on src/Shared/CommandLineUtils
    let old =
        [ MetadataReference.CreateFromFile netstandard20.Value :> MetadataReference ]

    Assert.Empty(fixed' (onLinkedFile "CR0083" sharedOption [] old []))

// ---- CR0152 ----

[<Fact; Trait("Category", "Sweep1118")>]
let ``CR0152 spells a Lock its file can see in every project that compiles it`` () =
    // aspnetcore src/Tools/Shared/CommandLine/ConsoleReporter.cs: `object _writeLock` became `Lock`, resolved
    // through dotnet-dev-certs' implicit `global using System.Threading;` - GetDocument.Insider and
    // dotnet-getdocument compile the same file without it (CS0246 'Lock'), net462 has no Lock at all
    let reporter =
        csharp
            """
            using System;
            namespace Tools
            {
                public class ConsoleReporter
                {
                    private readonly object _writeLock = new object();
                    public void Write(string message)
                    {
                        lock (_writeLock)
                        {
                            Console.Write(message);
                        }
                    }
                }
            }
            """

    let globalUsings = [ "GlobalUsings.cs", "global using System.Threading;\n" ]

    // standing down passes too; a fix must compile in project B: the same file, no global using
    for suggestion in fixed' (onLinkedFile "CR0152" reporter globalUsings metadataReferences []) do
        let edited = applyFix reporter suggestion.Fixes.Head
        let tree = CSharpSyntaxTree.ParseText(edited, parseOptions, path = "Shared.cs")

        let compilation =
            CSharpCompilation.Create("B", [ tree ], metadataReferences, nullableLibrary)

        Assert.Empty(errorsOf compilation)

// ---- CR0104 ----

/// aspnetcore's src/Shared/ThrowHelpers/ArgumentThrowHelper.cs, with the
/// attributes netstandard2.0 lacks declared beside it as that repository does.
let private throwHelper =
    csharp
        """
        namespace System.Diagnostics.CodeAnalysis
        {
            [AttributeUsage(AttributeTargets.Parameter)] internal sealed class NotNullAttribute : Attribute { }
            [AttributeUsage(AttributeTargets.Method)] internal sealed class DoesNotReturnAttribute : Attribute { }
        }
        namespace Shared
        {
            using System;
            using System.Diagnostics.CodeAnalysis;
            internal static class ArgumentThrowHelper
            {
                public static void ThrowIfNullOrEmpty([NotNull] string? argument, string? paramName = null)
                {
                    if (argument is null || argument == string.Empty)
                    {
                        ThrowIfNull(argument, paramName);
                        Throw(paramName);
                    }
                }
                static void ThrowIfNull([NotNull] object? argument, string? paramName)
                {
                    if (argument is null) throw new ArgumentNullException(paramName);
                }
                [DoesNotReturn]
                static void Throw(string? paramName) => throw new ArgumentException("empty", paramName);
            }
        }
        """

[<Fact; Trait("Category", "Sweep1118")>]
let ``CR0104 keeps the spelled-out null test where IsNullOrEmpty carries no NotNullWhen`` () =
    // aspnetcore src/Shared/ThrowHelpers/ArgumentThrowHelper.cs on netstandard2.0/net462: CS8777 after the fix
    let compileOld (text: string) =
        let tree =
            CSharpSyntaxTree.ParseText(normalize text, parseOptions, path = "Sample.cs")

        CSharpCompilation.Create(
            "Old",
            [ tree ],
            [ MetadataReference.CreateFromFile netstandard20.Value :> MetadataReference ],
            nullableLibrary
        ),
        tree

    let nullabilityWarnings (compilation: CSharpCompilation) =
        compilation.GetDiagnostics()
        |> Seq.filter (fun d -> d.Id = "CS8777")
        |> List.ofSeq

    let compilation, tree = compileOld throwHelper
    Assert.Empty(nullabilityWarnings compilation)

    for suggestion in fixed' (suggestOn compilation tree "CR0104") do
        let after, _ = compileOld (applyFix throwHelper suggestion.Fixes.Head)
        Assert.Empty(nullabilityWarnings after)

// ---- CR0042 ----

[<Fact; Trait("Category", "Sweep1118")>]
let ``CR0042 leaves a sync call a test makes beside its async twin on the same receiver`` () =
    // aspnetcore ViewBufferTextWriterTest.WriteLines_WritesCharBuffer: `writer.WriteLine(); await writer.WriteLineAsync();`
    // - the test covers both overloads; the fix left it testing the async one twice
    let source =
        csharp
            """
            using System.IO;
            using System.Text;
            using System.Threading.Tasks;
            using Xunit;
            public sealed class ViewWriter : TextWriter
            {
                public int Count;
                public override Encoding Encoding => Encoding.UTF8;
                public override void Write(char value) => Count++;
            }
            public class ViewWriterTest
            {
                [Fact]
                public async Task WriteLines_WritesBothWays()
                {
                    var writer = new ViewWriter();
                    writer.WriteLine();
                    await writer.WriteLineAsync();
                    Assert.True(writer.Count > 0);
                }
                [Fact]
                public async Task Other()
                {
                    var other = new ViewWriter();
                    other.WriteLine();
                    await Task.Yield();
                    Assert.True(other.Count > 0);
                }
            }
            """

    Assert.Equal<string list>([ "other.WriteLine()" ], firedText source (fixed' (suggestCode "CR0042" source)))

[<Fact; Trait("Category", "Sweep1118")>]
let ``CR0042 leaves a StreamWriter over a MemoryStream`` () =
    // aspnetcore src/Logging.AzureAppServices/src/BlobLoggerProvider.cs: a state machine per log message
    // for a write into memory (AsyncShapes.onMemoryStream knows the shape; the twin rule does not)
    let source =
        csharp
            """
            using System;
            using System.Collections.Generic;
            using System.IO;
            using System.Threading.Tasks;
            class BlobLogger
            {
                async Task WriteMessagesAsync(IEnumerable<string> messages, Func<ArraySegment<byte>, Task> append)
                {
                    using (var stream = new MemoryStream())
                    using (var writer = new StreamWriter(stream))
                    {
                        foreach (var message in messages)
                        {
                            writer.Write(message);
                        }
                        writer.Flush();
                        stream.TryGetBuffer(out var buffer);
                        await append(buffer);
                    }
                }
            }
            """

    Assert.Empty(fixed' (suggestCode "CR0042" source))

// ---- CR0060 ----

[<Fact; Trait("Category", "Sweep1118")>]
let ``CR0060 never disposes a writer whose stream the method returns`` () =
    // aspnetcore src/Mvc/test/WebSites/FilesWebSite/Controllers/DownloadFilesController.cs: `using var writer`
    // closes the MemoryStream handed to File(stream, …): ObjectDisposedException when the result runs
    let source =
        csharp
            """
            using System.IO;
            class Downloads
            {
                public Stream DownloadFromStream()
                {
                    var stream = new MemoryStream();
                    var writer = new StreamWriter(stream);
                    writer.Write("This is sample text from a stream");
                    writer.Flush();
                    stream.Seek(0, SeekOrigin.Begin);
                    return stream;
                }
            }
            """

    Assert.Empty(fixed' (suggestCode "CR0060" source))

[<Fact; Trait("Category", "Sweep1118")>]
let ``CR0060 never disposes a pooled builder whose segment is stored`` () =
    // aspnetcore src/Components/Shared/test/CapturedBatch.cs: `using var cloneBuilder = new ArrayBuilder<…>()`
    // returns the array to the pool while the stored RenderTreeDiff still points into it
    let source =
        csharp
            """
            using System;
            using System.Buffers;
            using System.Collections.Generic;
            sealed class ArrayBuilder<T> : IDisposable
            {
                T[] _items = ArrayPool<T>.Shared.Rent(16);
                int _count;
                public void Append(T[] source, int start, int length)
                {
                    Array.Copy(source, start, _items, _count, length);
                    _count += length;
                }
                public ArraySegment<T> ToSegment(int from, int to) => new ArraySegment<T>(_items, from, to - from);
                public void Dispose() => ArrayPool<T>.Shared.Return(_items);
            }
            sealed class CapturedBatch
            {
                public List<ArraySegment<int>> Diffs { get; } = new List<ArraySegment<int>>();
                public void AddDiff(int[] edits)
                {
                    var cloneBuilder = new ArrayBuilder<int>();
                    cloneBuilder.Append(edits, 0, edits.Length);
                    Diffs.Add(cloneBuilder.ToSegment(0, edits.Length));
                }
            }
            """

    Assert.Empty(fixed' (suggestCode "CR0060" source))

// ---- CR0081 ----

[<Fact; Trait("Category", "Sweep1118")>]
let ``CR0081 leaves a record passed where object is taken`` () =
    // aspnetcore ValidationStackTest.Model became a `readonly record struct`: Push(object)/Pop(object)
    // box it afresh each call and ValidationStack's ReferenceEqualityComparer set never finds it again
    let source =
        csharp
            """
            using System.Collections.Generic;
            class ValidationStack
            {
                readonly HashSet<object> _seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
                public bool Push(object model) => _seen.Add(model);
                public void Pop(object model) => _seen.Remove(model);
                public int Count => _seen.Count;
            }
            record Model(int Position);
            static class Use
            {
                public static int Run(ValidationStack stack)
                {
                    var models = new List<Model> { new Model(1), new Model(2) };
                    foreach (var model in models) stack.Push(model);
                    foreach (var model in models) stack.Pop(model);
                    return stack.Count;
                }
            }
            """

    Assert.Empty(fixed' (suggestCode "CR0081" source))

[<Fact; Trait("Category", "Sweep1118")>]
let ``CR0081 leaves a record whose Type is handed to reflection`` () =
    // aspnetcore ParameterBindingMethodCacheTests: `[InlineData(typeof(RecordClassParameterizedConstructor))]`
    // - the test is about a record CLASS; a struct changes what the binder sees
    let source =
        csharp
            """
            using System;
            record RecordClassParameterizedConstructor(int Foo);
            static class Cache
            {
                public static bool HasConstructor(Type type) => type.GetConstructors().Length > 0 && !type.IsValueType;
                public static bool Probe() => HasConstructor(typeof(RecordClassParameterizedConstructor));
            }
            """

    Assert.Empty(fixed' (suggestCode "CR0081" source))

// ---- CR0015 ----

[<Fact; Trait("Category", "Sweep1118")>]
let ``CR0015 leaves an indexed loop over an interface-typed list and still converts an array`` () =
    // aspnetcore AcceptsMatcherPolicy, DefaultHubDispatcher, ActionEndpointFactory…: `for` over IReadOnlyList<T>
    // on hot paths became foreach - a boxed enumerator and two interface calls per element
    let source =
        csharp
            """
            using System.Collections.Generic;
            static class Policy
            {
                public static int Count(IReadOnlyList<string> endpoints)
                {
                    var n = 0;
                    for (var i = 0; i < endpoints.Count; i++)
                    {
                        if (endpoints[i].Length > 0) n++;
                    }
                    return n;
                }
                public static int Sum(int[] xs)
                {
                    var n = 0;
                    for (var i = 0; i < xs.Length; i++)
                    {
                        n += xs[i];
                    }
                    return n;
                }
            }
            """

    let fired = firedText source (fixed' (suggestCode "CR0015" source))
    Assert.Equal(1, fired.Length)
    Assert.Contains("xs", fired.Head)

// ---- CR0177 ----

[<Fact; Trait("Category", "Sweep1118")>]
let ``CR0177 leaves a zeroed local whose address the loop hands out`` () =
    // aspnetcore HttpSys ClientCertLoader / AsyncAcceptContext: `uint bytesReceived = 0;` hoisted out of the
    // retry loop while `&bytesReceived` goes to the native call - a retry sees the last round's value, not 0
    let source =
        csharp
            """
            unsafe class Loader
            {
                static uint Receive(uint* bytes, bool fill)
                {
                    if (fill) *bytes = 42;
                    return fill ? 234u : 997u;
                }
                public uint Load()
                {
                    bool retry;
                    uint last = 0;
                    do
                    {
                        retry = false;
                        uint bytesReceived = 0;
                        var status = Receive(&bytesReceived, last == 0);
                        if (status == 234u)
                        {
                            retry = true;
                        }
                        last = bytesReceived;
                    } while (retry);
                    return last;
                }
            }
            """

    let tree =
        CSharpSyntaxTree.ParseText(normalize source, parseOptions, path = "Sample.cs")

    let compilation =
        CSharpCompilation.Create("Test", [ tree ], metadataReferences, nullableLibrary.WithAllowUnsafe true)

    Assert.Empty(fixed' (suggestOn compilation tree "CR0177"))

// ---- CR0045 ----

[<Fact; Trait("Category", "Sweep1118")>]
let ``CR0045 leaves a test that suppresses xUnit1031 around a completed task's Result`` () =
    // aspnetcore NavigationManagerTest.LocationChangingHandlers_CanContinueTheNavigationSynchronously_*:
    // `#pragma warning disable xUnit1031` and IsCompletedSuccessfully asserted first - the sync read is the point
    let source =
        csharp
            """
            using System.Threading.Tasks;
            using Xunit;
            public class NavigationTests
            {
                static Task<bool> Navigate() => Task.FromResult(true);
                [Fact]
                public void CanContinueTheNavigationSynchronously()
                {
                    var navigation = Navigate();
                    Assert.True(navigation.IsCompletedSuccessfully);
                    #pragma warning disable xUnit1031 // Do not use blocking task operations in test method
                    Assert.True(navigation.Result);
                    #pragma warning restore xUnit1031 // Do not use blocking task operations in test method
                }
            }
            """

    Assert.Empty(fixed' (suggestCode "CR0045" source))

// ---- a linked file: a fix holds where every project compiling it offers it ----

[<Fact; Trait("Category", "Sweep1118")>]
let ``a fix in a linked file is dropped where another project compiling it does not offer it`` () =
    // aspnetcore src/Shared/ThrowHelpers/ArgumentThrowHelper.cs: a net10.0 project offers CR0104, the
    // netstandard2.0 project compiling the same file has no [NotNullWhen] to narrow `argument` after it
    let guard =
        csharp
            """
            namespace Shared
            {
                internal static class Guard
                {
                    public static int Length(string? argument)
                    {
                        if (argument is null || argument == string.Empty)
                        {
                            return 0;
                        }
                        return argument.Length;
                    }
                }
            }
            """

    let old =
        [ MetadataReference.CreateFromFile netstandard20.Value :> MetadataReference ]

    // the rule alone answers for its own compilation
    Assert.NotEmpty(fixed' (onLinkedFile "CR0104" guard [] old []))
    // where both projects have the attribute, the fix stays
    Assert.NotEmpty(fixed' (hostedOnLinkedFile "CR0104" guard [] metadataReferences []))
    Assert.Empty(fixed' (hostedOnLinkedFile "CR0104" guard [] old []))

[<Fact; Trait("Category", "Sweep1118")>]
let ``a fix in a linked file is dropped where another project's part of the type breaks it`` () =
    // a partial type whose shared part declares a private setter another project's own part assigns:
    // private, so no reference search leaves the compilation, and CS8852 in that project after `init`
    let settings =
        csharp
            """
            namespace Tools
            {
                internal sealed partial class Settings
                {
                    private int Retries { get; set; }
                    public static Settings Create() => new Settings { Retries = 3 };
                    public int Read() => Retries;
                }
            }
            """

    let otherPart =
        csharp
            """
            namespace Tools
            {
                internal sealed partial class Settings
                {
                    public void Reset() { Retries = 0; }
                }
            }
            """

    Assert.NotEmpty(fixed' (hostedOnLinkedFile "CR0083" settings [] metadataReferences []))
    Assert.Empty(fixed' (hostedOnLinkedFile "CR0083" settings [] metadataReferences [ "Reset.cs", otherPart ]))

// ---- CR0060: the wrapped stream leaves inside what is returned ----

[<Fact; Trait("Category", "Sweep1118")>]
let ``CR0060 never disposes a writer whose stream leaves inside what the method returns`` () =
    let source (leaves: string) =
        csharp (
            """
            using System.IO;
            class Holder { public Holder(Stream s) { S = s; } public Stream S { get; } }
            class Downloads
            {
                public object Download(bool ok)
                {
                    var stream = new MemoryStream();
                    var writer = new StreamWriter(stream);
                    writer.Write("This is sample text from a stream");
                    writer.Flush();
            """
            + "        "
            + leaves
            + """

                }
            }
            """
        )

    Assert.Empty(fixed' (suggestCode "CR0060" (source "return (Stream)stream;")))
    Assert.Empty(fixed' (suggestCode "CR0060" (source "return new Holder(stream);")))
    Assert.Empty(fixed' (suggestCode "CR0060" (source "return ok ? stream : null;")))

// ---- the batch speculative check: one bad edit among many ----

[<Fact; Trait("Category", "Sweep1118")>]
let ``the batch speculative check keeps every candidate but the one that does not compile`` () =
    let methods = [ for i in 0..31 -> $"    int M{i}() => {100 + i};\n" ]
    let source = "class C\n{\n" + String.concat "" methods + "}\n"
    let compilation, tree = compile source
    let model = compilation.GetSemanticModel(tree, false)

    let candidates =
        [
            for i in 0..31 ->
                let literal = string (100 + i)
                let start = source.IndexOf($"=> {literal};", StringComparison.Ordinal) + 3
                // candidate 13 returns a string from an int method
                i,
                [
                    Suggestion.replace (TextSpan(start, literal.Length)) (if i = 13 then "\"x\"" else "7")
                ]
        ]

    let expected = [ 0..31 ] |> List.filter (fun i -> i <> 13)
    Assert.Equal<int list>(expected, Guards.speculativeCheckEach model candidates)
