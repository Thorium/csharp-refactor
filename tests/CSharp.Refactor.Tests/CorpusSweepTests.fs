/// Shapes the 2026-11-16 sweep of ten public repositories (Newtonsoft.Json,
/// serilog, jellyfin, PowerShell, OpenTelemetry, Files, v2rayN, aws-sdk-net,
/// protobuf, shadowsocks-windows) found a rule getting wrong.
module CSharp.Refactor.Tests.CorpusSweepTests

open System
open System.IO
open Xunit
open CSharp.Refactor.Roslyn
open CSharp.Refactor.Tests.Harness

let private fixed' (suggestions: CSharp.Refactor.Suggestion list) =
    suggestions |> List.filter (fun s -> not s.Fixes.IsEmpty)

// ---- CR0015 ----

[<Fact>]
let ``CR0015 leaves a list loop that writes through the indexer, takes the array one, and drops a field prefix`` () =
    let source =
        csharp
            """
            using System.Collections.Generic;
            class C
            {
                readonly int[] _contexts = new int[3];
                static int CompactList(List<int> result)
                {
                    var next = 0;
                    for (var i = 0; i < result.Count; ++i)
                    {
                        if (result[i] != 0)
                        {
                            result[next] = result[i];
                            ++next;
                        }
                    }
                    return next;
                }
                static int CompactArray(int[] result)
                {
                    var next = 0;
                    for (var i = 0; i < result.Length; ++i)
                    {
                        if (result[i] != 0)
                        {
                            result[next] = result[i];
                            ++next;
                        }
                    }
                    return next;
                }
                int Sum()
                {
                    var total = 0;
                    for (var i = 0; i < _contexts.Length; i++)
                        total += _contexts[i];
                    return total;
                }
            }
            """

    // the list's setter bumps its version: the foreach would throw on the next MoveNext
    let fired = fires 2 "CR0015" source
    Assert.DoesNotContain("result.Count", firedText source fired |> String.concat "\n")
    let fixedSource = fixAll "CR0015" source
    Assert.Contains("for (var i = 0; i < result.Count; ++i)", fixedSource)
    Assert.Contains("foreach (var item in result)", fixedSource)
    Assert.Contains("foreach (var context in _contexts)", fixedSource)

// ---- CR0161 ----

[<Fact>]
let ``CR0161 reads a null-forgiving member access as a read`` () =
    let source =
        csharp
            """
            using System.Collections.Generic;
            struct Pos
            {
                public string? Name;
                public int Count;
                public int Length() => Name!.Length;
                public void Bump() => Count++;
            }
            class C
            {
                int Sum(List<Pos> xs)
                {
                    var n = 0;
                    for (var i = 0; i < xs.Count; i++)
                    {
                        n += xs[i].Length();
                        xs[i].Bump();
                    }
                    return n;
                }
            }
            """

    let fired = fires 1 "CR0161" source
    Assert.Equal<string list>([ "xs[i].Bump()" ], firedText source fired)

// ---- CR0114 ----

[<Fact>]
let ``CR0114 counts positional placeholders, once each`` () =
    let source =
        csharp
            """
            namespace Serilog
            {
                public interface ILogger
                {
                    void Information(string messageTemplate, params object?[]? propertyValues);
                }
            }
            class C
            {
                void A(Serilog.ILogger log, int n)
                {
                    log.Information("Processed {0} items", n);
                    log.Information("{0} and again {0}", n);
                    log.Information("{0}{1}", n);
                    log.Information("{Count} and {Count}", n, n);
                }
            }
            """

    let fired = fires 2 "CR0114" source
    Assert.Contains(fired, fun s -> s.Message.Contains "2 placeholder(s) and the call passes 1 value(s)")
    Assert.Contains(fired, fun s -> s.Message.Contains "'{Count}' appears twice")

// ---- CR0060 ----

[<Fact>]
let ``CR0060 counts await using over ConfigureAwait as the dispose, and leaves a half-built object`` () =
    let source =
        csharp
            """
            using System;
            using System.IO;
            using System.Threading.Tasks;
            sealed class Connection : IDisposable
            {
                private readonly Stream _socket;
                public Connection(Stream socket) { _socket = socket; }
                public int Peek() => 1;
                public void Dispose() => _socket.Dispose();
            }
            class C
            {
                async Task Write(string file, byte[] data)
                {
                    var fileStream = new FileStream(file, FileMode.Create);
                    await using (fileStream.ConfigureAwait(false))
                    {
                        await fileStream.WriteAsync(data).ConfigureAwait(false);
                    }
                }
                async Task Write2(string file, byte[] data)
                {
                    var fileStream = new FileStream(file, FileMode.Create);
                    await using var configured = fileStream.ConfigureAwait(false);
                    await fileStream.WriteAsync(data).ConfigureAwait(false);
                }
                int Probe()
                {
                    var con = new Connection(null!);
                    return con.Peek();
                }
                int Real(string file)
                {
                    var con = new Connection(File.OpenRead(file));
                    return con.Peek();
                }
            }
            """

    // the fully built one is the control: the rule is awake, and only for it
    match fixed' (suggestCode "CR0060" source) with
    | [ s ] -> Assert.True(s.Span.Start > (normalize source).IndexOf "int Real")
    | other -> failwithf "Expected the one fix in Real, got %A" (firedText source other)

[<Fact>]
let ``CR0060 takes a System-namespaced method outside the runtime for one that may keep the object`` () =
    let source =
        csharp
            """
            using System.IO;
            namespace System.Management.Automation
            {
                public abstract class Cmdlet
                {
                    public void WriteObject(object sendToPipeline) { }
                }
            }
            namespace App
            {
                sealed class GetThing : System.Management.Automation.Cmdlet
                {
                    void ProcessRecord(string path)
                    {
                        var provider = new FileStream(path, FileMode.Open);
                        WriteObject(provider);
                    }
                }
            }
            """

    let fired = fires 1 "CR0060" source
    Assert.Empty(fixed' fired)
    Assert.Contains("WriteObject", fired.Head.Message)

// ---- CR0042 ----

[<Fact>]
let ``CR0042 awaits with ConfigureAwait where the file does, and skips in-memory, console, EF Add and sync-named members``
    ()
    =
    let source =
        csharp
            """
            using System;
            using System.IO;
            using System.Threading;
            using System.Threading.Tasks;
            namespace Microsoft.EntityFrameworkCore
            {
                public sealed class Entry { }
                public class DbSet<T>
                {
                    public Entry Add(T entity) => new Entry();
                    public ValueTask<Entry> AddAsync(T entity, CancellationToken cancellationToken = default) => new(new Entry());
                }
            }
            namespace App
            {
                class C
                {
                    async Task<string?> Read(TextReader reader)
                    {
                        await Task.Delay(1).ConfigureAwait(false);
                        return reader.ReadLine();
                    }
                    async Task Memory(MemoryStream ms, byte[] buffer)
                    {
                        await Task.Yield();
                        ms.Write(buffer, 0, buffer.Length);
                        Console.Out.WriteLine("done");
                    }
                    async Task Store(Microsoft.EntityFrameworkCore.DbSet<string> set)
                    {
                        await Task.Yield();
                        set.Add("x");
                    }
                    async Task WriteSyncReadingAsync(TextWriter writer)
                    {
                        await Task.Yield();
                        writer.Write("x");
                    }
                }
            }
            """

    let fired = fires 1 "CR0042" source
    Assert.Equal<string list>([ "reader.ReadLine()" ], firedText source fired)
    let fixedSource = fixAll "CR0042" source
    Assert.Contains("return await reader.ReadLineAsync().ConfigureAwait(false);", fixedSource)

[<Fact>]
let ``CR0042 awaits bare where the file never configures an await`` () =
    let source =
        csharp
            """
            using System.IO;
            using System.Threading.Tasks;
            class C
            {
                async Task<string?> Read(TextReader reader)
                {
                    await Task.Yield();
                    return reader.ReadLine();
                }
            }
            """

    let fixedSource = fixAll "CR0042" source
    Assert.Contains("return await reader.ReadLineAsync();", fixedSource)

// ---- CR0165 ----

[<Fact>]
let ``CR0165 keeps a note where the try adds HTTP headers, whose exception quotes the value`` () =
    let source =
        csharp
            """
            using System;
            using System.Net.Http;
            class C
            {
                void Add(HttpRequestMessage request, string key, string value)
                {
                    try
                    {
                        request.Headers.Add(key, value);
                    }
                    catch (FormatException)
                    {
                        throw new FormatException($"The value configured for the '{key}' header is not valid.");
                    }
                }
                int Parse(string text)
                {
                    try { return int.Parse(text); }
                    catch (FormatException) { throw new InvalidOperationException("bad number"); }
                }
            }
            """

    let fired = fires 2 "CR0165" source
    Assert.Equal(1, (fixed' fired).Length)
    Assert.Contains(fired, fun s -> s.Fixes.IsEmpty && s.Message.Contains "HTTP headers")

// ---- CR0083 ----

[<Fact>]
let ``CR0083 leaves a public type's setters where a XAML framework is referenced`` () =
    let source =
        csharp
            """
            namespace System.Windows { public class DependencyObject { } }
            namespace App
            {
                public sealed class IconGalleryEntry
                {
                    public string Key { get; set; } = "";
                }
                internal sealed class Row
                {
                    public string Key { get; set; } = "";
                }
                static class Use
                {
                    static object Make() => new IconGalleryEntry { Key = "a" };
                    static object Make2() => new Row { Key = "a" };
                }
            }
            """

    let fired = fires 1 "CR0083" source
    Assert.Equal<string list>([ "set" ], firedText source fired)
    // the internal Row's setter, after its declaration
    Assert.True((normalize source).IndexOf "class Row" < fired.Head.Span.Start)

// ---- framework order ----

[<Fact>]
let ``frameworks rank by API surface: net20 below net462 below net47, netstandard2.0 between net46 and net461`` () =
    let order =
        [
            "net10.0"
            "net47"
            "netstandard2.0"
            "net462"
            "net20"
            "netcoreapp3.1"
            "net461"
            "net46"
            "net35"
            "netstandard2.1"
            "netcoreapp2.1"
        ]
        |> List.sortBy Flavors.rank

    Assert.Equal<string list>(
        [
            "net20"
            "net35"
            "net46"
            "netstandard2.0"
            "net461"
            "net462"
            "net47"
            "netcoreapp2.1"
            "netstandard2.1"
            "netcoreapp3.1"
            "net10.0"
        ],
        order
    )

    // a TargetFrameworks property spread over lines leaves whitespace in the name
    Assert.Equal(Flavors.rank "net11.0", Flavors.rank "\n      net11.0")
    Assert.True(Flavors.rank "net462\n    " < Flavors.rank "net47")

// ---- the tool ----

[<Fact>]
let ``a solution without Any CPU loads for x64, or its first platform`` () =
    let dir =
        Path.Combine(Path.GetTempPath(), "csref-tests", Guid.NewGuid().ToString "N")

    Directory.CreateDirectory dir |> ignore

    try
        let write name (text: string) =
            let path = Path.Combine(dir, name)
            File.WriteAllText(path, text)
            path

        let files =
            write
                "Files.slnx"
                """<Solution><Configurations><Platform Name="arm64" /><Platform Name="x64" /><Platform Name="x86" /></Configurations></Solution>"""

        Assert.Equal(Some "x64", CSharp.Refactor.Tool.Sweep.solutionPlatform files)

        let arm =
            write "Arm.slnx" """<Solution><Configurations><Platform Name="arm64" /></Configurations></Solution>"""

        Assert.Equal(Some "arm64", CSharp.Refactor.Tool.Sweep.solutionPlatform arm)

        let anyCpu =
            write
                "Lib.sln"
                "Global\n\tGlobalSection(SolutionConfigurationPlatforms) = preSolution\n\t\tDebug|Any CPU = Debug|Any CPU\n\t\tDebug|x86 = Debug|x86\n\tEndGlobalSection\nEndGlobal\n"

        Assert.Equal(None, CSharp.Refactor.Tool.Sweep.solutionPlatform anyCpu)

        let x86 =
            write
                "Old.sln"
                "Global\n\tGlobalSection(SolutionConfigurationPlatforms) = preSolution\n\t\tDebug|x86 = Debug|x86\n\t\tRelease|x86 = Release|x86\n\tEndGlobalSection\nEndGlobal\n"

        Assert.Equal(Some "x86", CSharp.Refactor.Tool.Sweep.solutionPlatform x86)
    finally
        Directory.Delete(dir, true)

// ---- CR0163 ----

[<Fact>]
let ``CR0163 makes the author's own block the try body instead of nesting it`` () =
    let source =
        csharp
            """
            using System;
            using System.Threading;
            class C
            {
                static readonly ReaderWriterLockSlim Gate = new ReaderWriterLockSlim();
                static TimeSpan? correction;
                static TimeSpan? Read()
                {
                    TimeSpan? value;
                    Gate.EnterReadLock();
                    {
                        value = correction;
                    }
                    Gate.ExitReadLock();
                    return value;
                }
            }
            """

    let fixedSource = fixAll "CR0163" source

    Assert.Contains(
        csharp
            """
                    Gate.EnterReadLock();
                    try
                    {
                        value = correction;
                    }
                    finally
                    {
                        Gate.ExitReadLock();
                    }
            """,
        fixedSource
    )

    Assert.DoesNotContain("{\n            {", fixedSource.Replace("\r\n", "\n"))

// ---- CR0180 ----

[<Fact>]
let ``CR0180 makes a private constant field const, and a public one static readonly`` () =
    let source =
        csharp
            """
            class Credentials
            {
                private static string Server = "169.254.169.254";
                public static string Path = "/latest";
                static int[] Table = { 1, 2 };
                static string Use() => Server + Path + Table[0];
            }
            """

    let fixedSource = fixAll "CR0180" source
    Assert.Contains("""private const string Server = "169.254.169.254";""", fixedSource)
    Assert.Contains("""public static readonly string Path = "/latest";""", fixedSource)
    Assert.Contains("static readonly int[] Table = { 1, 2 };", fixedSource)

// ---- review of 0.1.28 ----

[<Fact>]
let ``an error a fix only moves is not one it added; a second one of the same is`` () =
    let without =
        "C:\a\B.cs(10,55): error CA1822: Member 'M' does not access instance data [C:\a\A.csproj]"

    let moved =
        "C:\a\B.cs(10,54): error CA1822: Member 'M' does not access instance data [C:\a\A.csproj]"

    Assert.Empty(CSharp.Refactor.Tool.Sweep.addedErrors moved without)

    let twice =
        moved
        + "\n"
        + "C:\a\B.cs(12,9): error CA1822: Member 'M' does not access instance data [C:\a\A.csproj]"

    Assert.Equal(1, (CSharp.Refactor.Tool.Sweep.addedErrors twice without).Length)

[<Fact>]
let ``CR0042 awaits bare in a method whose own awaits are bare, though another method configures them`` () =
    let source =
        csharp
            """
            using System.IO;
            using System.Threading.Tasks;
            class Form
            {
                public string Text = "";
                async Task Helper() => await Task.Delay(1).ConfigureAwait(false);
                async void OnClick(TextReader reader)
                {
                    await Task.Yield();
                    var s = reader.ReadLine();
                    Text = s ?? "";
                }
                async Task SyncContactsAsync(TextReader reader)
                {
                    await Task.Yield();
                    reader.ReadLine();
                }
            }
            """

    let fired = fires 2 "CR0042" source
    Assert.Equal<string list>([ "reader.ReadLine()"; "reader.ReadLine()" ], firedText source fired)
    let fixedSource = fixAll "CR0042" source
    Assert.Contains("var s = await reader.ReadLineAsync();", fixedSource)
    Assert.DoesNotContain("ReadLineAsync().ConfigureAwait", fixedSource)

[<Fact>]
let ``CR0114 holds Microsoft.Extensions.Logging to one argument per hole, positional too`` () =
    let source =
        csharp
            """
            using Microsoft.Extensions.Logging;
            namespace Microsoft.Extensions.Logging
            {
                public interface ILogger { }
                public static class LoggerExtensions
                {
                    public static void LogInformation(this ILogger logger, string? message, params object?[] args) { }
                }
            }
            class C
            {
                void A(Microsoft.Extensions.Logging.ILogger log, int n)
                {
                    log.LogInformation("{0} and {0}", n);
                    log.LogInformation("{0} and {1}", n, n);
                }
            }
            """

    let fired = fires 1 "CR0114" source
    Assert.Contains("'{0}' appears twice", fired.Head.Message)

[<Fact>]
let ``CR0180 keeps static readonly where an earlier initializer runs code that may read the field`` () =
    let source =
        csharp
            """
            class Order
            {
                static int A = Compute();
                private static int B = 5;
                static int Compute() => B;
                public static int Read() => A + B;
            }
            class Plain
            {
                private static int B = 5;
                static int A = B + 1;
                public static int Read() => A + B;
            }
            """

    let fixedSource = fixAll "CR0180" source
    // Order's B is read by A's initializer through Compute: it stays a field; Plain's B comes first
    let plain = fixedSource.IndexOf "class Plain"
    Assert.Contains("private static readonly int B = 5;", fixedSource.Substring(0, plain))
    Assert.Contains("private const int B = 5;", fixedSource.Substring plain)

[<Fact>]
let ``netstandard ranks below the .NET Framework that first implements it, never level with it`` () =
    Assert.True(Flavors.rank "netstandard1.0" < Flavors.rank "net45")
    Assert.True(Flavors.rank "netstandard1.3" < Flavors.rank "net46")
    Assert.True(Flavors.rank "net46" < Flavors.rank "netstandard2.0")
    Assert.True(Flavors.rank "netstandard2.0" < Flavors.rank "net461")
    Assert.True(Flavors.rank "netstandard1.4" < Flavors.rank "netstandard2.0")
    Assert.True(Flavors.rank "netstandard2.1" < Flavors.rank "netcoreapp3.0")

[<Fact>]
let ``a solution's Mixed Platforms is never the platform a project builds for`` () =
    let dir =
        Path.Combine(Path.GetTempPath(), "csref-tests", Guid.NewGuid().ToString "N")

    Directory.CreateDirectory dir |> ignore

    try
        let path = Path.Combine(dir, "Old.sln")

        File.WriteAllText(
            path,
            "Global\n\tGlobalSection(SolutionConfigurationPlatforms) = preSolution\n\t\tDebug|Mixed Platforms = Debug|Mixed Platforms\n\t\tDebug|x86 = Debug|x86\n\tEndGlobalSection\nEndGlobal\n"
        )

        Assert.Equal(Some "x86", CSharp.Refactor.Tool.Sweep.solutionPlatform path)
    finally
        Directory.Delete(dir, true)

[<Fact>]
let ``CR0015 reads a null-forgiving element of the list as a read, not a write`` () =
    let source =
        csharp
            """
            using System.Collections.Generic;
            class C
            {
                static int Total(List<string> xs)
                {
                    var n = 0;
                    for (var i = 0; i < xs.Count; i++)
                        n += xs[i].Length + xs[0]!.Length;
                    return n;
                }
            }
            """

    let fixedSource = fixAll "CR0015" source
    Assert.Contains("foreach (var item in xs)", fixedSource)

[<Fact>]
let ``CR0172 and CR0180 leave a name an inactive #if branch writes`` () =
    let source =
        csharp
            """
            class C
            {
                private static int Limit = 3;
                static bool Ignored()
                {
                    bool hasIgnore = false;
            #if HAVE_IGNORE
                    hasIgnore = Probe();
                    Limit = 4;
            #endif
                    return hasIgnore && Limit > 0;
                }
                static bool Probe() => true;
                static int Plain()
                {
                    int width = 2;
                    return width;
                }
            }
            """

    let fired = fires 1 "CR0172" source
    Assert.Equal<string list>([ "int width = 2" ], firedText source fired)
    Assert.Empty(suggestCode "CR0180" source)

[<Fact>]
let ``CR0015 leaves a list loop that swaps elements by deconstruction`` () =
    let source =
        csharp
            """
            using System.Collections.Generic;
            class C
            {
                static int Spin(List<int> xs)
                {
                    var n = 0;
                    for (var i = 0; i < xs.Count; i++)
                    {
                        n += xs[i];
                        (xs[0], xs[^1]) = (xs[^1], xs[0]);
                    }
                    return n;
                }
            }
            """

    Assert.Empty(suggestCode "CR0015" source)

[<Fact>]
let ``CR0172 still makes a local const that an inactive #if branch only reads`` () =
    let source =
        csharp
            """
            class C
            {
                static int Show()
                {
                    string xml = "<a/>";
            #if HAVE_XLINQ
                    var d = System.Xml.Linq.XDocument.Parse(xml);
            #endif
                    return xml.Length;
                }
            }
            """

    let fixedSource = fixAll "CR0172" source
    Assert.Contains("const string xml = \"<a/>\";", fixedSource)

[<Fact>]
let ``CR0042 reads await foreach and await using as the method's awaits`` () =
    let source =
        csharp
            """
            using System.Collections.Generic;
            using System.IO;
            using System.Threading.Tasks;
            class C
            {
                async Task<int> Count(IAsyncEnumerable<int> xs, TextReader reader)
                {
                    var n = 0;
                    await foreach (var x in xs.ConfigureAwait(false))
                        n += x;
                    var line = reader.ReadLine();
                    return n + (line?.Length ?? 0);
                }
                async Task<int> Bare(IAsyncEnumerable<int> xs, TextReader reader)
                {
                    var n = 0;
                    await foreach (var x in xs)
                        n += x;
                    var line = reader.ReadLine();
                    return n + (line?.Length ?? 0);
                }
            }
            """

    let fixedSource = fixAll "CR0042" source
    Assert.Contains("var line = await reader.ReadLineAsync().ConfigureAwait(false);", fixedSource)
    Assert.Contains("var line = await reader.ReadLineAsync();", fixedSource)

[<Fact>]
let ``CR0172 ignores a disabled method above that declares a local of the same name`` () =
    let source =
        csharp
            """
            class C
            {
            #if HAVE_BIG_INTEGER
                static int Old()
                {
                    string json = "{}";
                    json = json + " ";
                    return json.Length;
                }
            #endif
                static int Now()
                {
                    string json = "{}";
                    return json.Length;
                }
            }
            """

    let fixedSource = fixAll "CR0172" source
    Assert.Contains("const string json = \"{}\";\n        return json.Length;", fixedSource.Replace("\r\n", "\n"))

[<Fact>]
let ``CR0172 reads XML in a disabled string literal as no write`` () =
    let source =
        csharp
            """
            class C
            {
                static int Now()
                {
                    string json = "{}";
            #if HAVE_XML
                    Check(@"<a xmlns:json=""urn:x"" />", json);
            #endif
                    return json.Length;
                }
            }
            """

    let fixedSource = fixAll "CR0172" source
    Assert.Contains("const string json = \"{}\";", fixedSource)

// ---- second review of 0.1.28 ----

[<Fact>]
let ``CR0180 keeps static readonly where a const would fold into a warning or an error`` () =
    let source =
        csharp
            """
            using System;
            class Flags
            {
                private static bool Trace = false;
                private static int Level = 1;
                private static int Seed = 1000003;
                private static int Mul = 16777619;
                private static int Size =
            #if NETSTANDARD2_0
                    4096;
            #else
                    8192;
            #endif
                private static int Plain = 7;
                static void Step() { if (Trace) Console.WriteLine("s"); }
                static void Deep() { if (Level > 3) Console.WriteLine("d"); }
                static int Start() => Seed * Mul;
                static int Use(int x) => x + Plain + Size;
            }
            """

    let fixedSource = fixAll "CR0180" source
    Assert.Contains("private static readonly bool Trace = false;", fixedSource)
    Assert.Contains("private static readonly int Level = 1;", fixedSource)
    Assert.Contains("private static readonly int Seed = 1000003;", fixedSource)
    Assert.Contains("private static readonly int Mul = 16777619;", fixedSource)
    Assert.Contains("private static readonly int Size =", fixedSource)
    Assert.Contains("private const int Plain = 7;", fixedSource)

[<Fact>]
let ``CR0042 takes Sync as a noun for what it is`` () =
    let source =
        csharp
            """
            using System.IO;
            using System.Threading.Tasks;
            class C
            {
                async Task RunDataSyncJobAsync(Stream s, byte[] b)
                {
                    await Task.Yield();
                    s.Write(b, 0, b.Length);
                }
                async Task WriteTokenSyncReadingAsync(Stream s, byte[] b)
                {
                    await Task.Yield();
                    s.Write(b, 0, b.Length);
                }
            }
            """

    let fired = fires 1 "CR0042" source
    Assert.True(fired.Head.Span.Start < (normalize source).IndexOf "WriteTokenSyncReadingAsync")

[<Fact>]
let ``CR0165 keeps the fix where the try only reads headers, a note where it builds a header value`` () =
    let source =
        csharp
            """
            using System;
            using System.Linq;
            using System.Net.Http;
            using System.Net.Http.Headers;
            class C
            {
                string Etag(HttpResponseMessage response)
                {
                    try { return response.Headers.GetValues("ETag").First(); }
                    catch (InvalidOperationException) { throw new FormatException("no etag"); }
                }
                void Authorize(HttpClient client, string key)
                {
                    try { client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(key); }
                    catch (FormatException) { throw new InvalidOperationException("bad key"); }
                }
            }
            """

    let fired = fires 2 "CR0165" source
    let reads = (normalize source).IndexOf "void Authorize"
    Assert.Contains(fired, fun s -> s.Span.Start < reads && not s.Fixes.IsEmpty)
    Assert.Contains(fired, fun s -> s.Span.Start > reads && s.Fixes.IsEmpty)

[<Fact>]
let ``CR0015 reads a write through this as a write to the list`` () =
    let source =
        csharp
            """
            using System.Collections.Generic;
            class C
            {
                readonly List<string> _items = new List<string>();
                int Compact()
                {
                    var n = 0;
                    for (int i = 0; i < _items.Count; i++)
                        if (_items[i] != null)
                            this._items[n++] = _items[i];
                    return n;
                }
            }
            """

    Assert.Empty(fixed' (suggestCode "CR0015" source))

[<Fact>]
let ``CR0055 leaves the explicit None of a cleanup call, and replaces another`` () =
    let source =
        csharp
            """
            using System.Threading;
            using System.Threading.Tasks;
            class C
            {
                Task StopCoreAndConfirmAsync(CancellationToken token) => Task.CompletedTask;
                Task LoadAsync(CancellationToken token) => Task.CompletedTask;
                async Task Start(CancellationToken cancellationToken)
                {
                    await LoadAsync(CancellationToken.None);
                    await StopCoreAndConfirmAsync(CancellationToken.None);
                }
            }
            """

    let fixedSource = fixAll "CR0055" source
    Assert.Contains("await LoadAsync(cancellationToken);", fixedSource)
    Assert.Contains("await StopCoreAndConfirmAsync(CancellationToken.None);", fixedSource)

[<Fact>]
let ``a dotted .NET Framework name ranks as its version`` () =
    Assert.Equal(Flavors.rank "net48", Flavors.rank "net4.8")
    Assert.Equal(Flavors.rank "net472", Flavors.rank "net4.7.2")
    Assert.True(Flavors.rank "net4.8" < Flavors.rank "netcoreapp3.1")
    Assert.True(Flavors.rank "net4.8" < Flavors.rank "net10.0")

[<Fact>]
let ``a solution that maps an old-format project to Any CPU keeps the projects' own platforms`` () =
    let dir =
        Path.Combine(Path.GetTempPath(), "csref-tests", Guid.NewGuid().ToString "N")

    Directory.CreateDirectory dir |> ignore

    try
        let write name (text: string) =
            let path = Path.Combine(dir, name)
            File.WriteAllText(path, text)
            path

        write
            "Legacy.csproj"
            """<Project ToolsVersion="15.0"><PropertyGroup Condition="'$(Configuration)|$(Platform)' == 'Debug|AnyCPU'" /></Project>"""
        |> ignore

        write "Modern.csproj" """<Project Sdk="Microsoft.NET.Sdk" />""" |> ignore

        let solution (project: string) =
            "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"P\", \""
            + project
            + "\", \"{11111111-1111-1111-1111-111111111111}\"\nEndProject\nGlobal\n"
            + "\tGlobalSection(SolutionConfigurationPlatforms) = preSolution\n\t\tDebug|x64 = Debug|x64\n\tEndGlobalSection\n"
            + "\tGlobalSection(ProjectConfigurationPlatforms) = postSolution\n"
            + "\t\t{11111111-1111-1111-1111-111111111111}.Debug|x64.ActiveCfg = Debug|Any CPU\n\tEndGlobalSection\nEndGlobal\n"

        Assert.Equal(None, CSharp.Refactor.Tool.Sweep.solutionPlatform (write "Old.sln" (solution "Legacy.csproj")))

        Assert.Equal(
            Some "x64",
            CSharp.Refactor.Tool.Sweep.solutionPlatform (write "New.sln" (solution "Modern.csproj"))
        )
    finally
        Directory.Delete(dir, true)

// ---- CR0152 ----

[<Fact>]
let ``CR0152 converts a gate declared in one part of a partial type and locked only in another`` () =
    // v2rayN: V2rayRuntime.cs declares _speedtestGate, V2rayRuntime.Profiles.cs locks it
    let declaring =
        csharp
            """
            partial class C
            {
                private readonly object _gate = new();
            }

            """

    let locking = "partial class C { void F() { lock (_gate) { } } }"

    let monitoring =
        csharp
            """
            using System.Threading;
            partial class C { void F() { Monitor.Enter(_gate); Monitor.Exit(_gate); } }
            """

    let unused = "partial class C { void F() { } }"

    Assert.Equal(1, (suggestInProject false false "CR0152" [ "A.cs", declaring; "B.cs", locking ]).Length)
    Assert.Empty(suggestInProject false false "CR0152" [ "A.cs", declaring; "B.cs", monitoring ])
    Assert.Empty(suggestInProject false false "CR0152" [ "A.cs", declaring; "B.cs", unused ])

// ---- CR0165 / CR0189: the appended argument ----

let private leadOf (marker: string) (source: string) =
    let line = source.Split('\n') |> Array.find (fun l -> l.Contains marker)
    line.Substring(0, line.Length - line.TrimStart().Length)

[<Fact>]
let ``CR0165 puts the inner exception on a line of its own when the arguments span lines`` () =
    // PowerShell's TraceListenerCommandBase: `provider.FullName), ex);` broke StyleCop SA1117
    let source =
        csharp
            """
            using System;
            class C
            {
                void A(string path, string name)
                {
                    try { Work(); }
                    catch (InvalidOperationException)
                    {
                        throw
                            new NotSupportedException(
                                string.Format("{0} {1}",
                                    path,
                                    name));
                    }
                    try { Work(); }
                    catch (ArgumentException) { throw new NotSupportedException(string.Format("{0}", path)); }
                }
                void Work() { }
            }
            """

    let fixedSource = (fixAll "CR0165" source).Replace("\r\n", "\n")
    let lead = leadOf "string.Format(\"{0} {1}\"," fixedSource
    Assert.Contains("name),\n" + lead + "ex);", fixedSource)
    Assert.Contains("""throw new NotSupportedException(string.Format("{0}", path), ex); }""", fixedSource)

[<Fact>]
let ``CR0189 puts the token on a line of its own when the arguments span lines`` () =
    let source =
        csharp
            """
            using System.Threading;
            using System.Threading.Tasks;
            class C
            {
                Task<int> FetchAsync(string key, int retries) => Task.FromResult(1);
                Task<int> FetchAsync(string key, int retries, CancellationToken token) => Task.FromResult(1);
                Task<int> RunAsync(System.Func<Task<int>> work) => work();
                Task<int> RunAsync(System.Func<Task<int>> work, CancellationToken token) => work();
                async Task<int> Lambda(CancellationToken ct)
                {
                    return await RunAsync(async () =>
                    {
                        return await Task.FromResult(8);
                    });
                }
                async Task<int> Lines(CancellationToken ct)
                {
                    return await FetchAsync(
                        "lines",
                        3);
                }
                async Task<int> Paren(CancellationToken ct)
                {
                    return await FetchAsync(
                        "paren",
                        4
                    );
                }
                async Task<int> Single(CancellationToken ct) => await FetchAsync("single", 1);
                async Task<int> Directive(CancellationToken ct)
                {
                    return await FetchAsync("directive", 7
            #if NEVER
                        , 9
            #endif
                        );
                }
                async Task<int> Below(CancellationToken ct)
                {
                    return await FetchAsync(
                        "below", 2);
                }
            }
            """

    let fixedSource = (fixAll "CR0189" source).Replace("\r\n", "\n")
    let lead = leadOf "\"lines\"," fixedSource
    Assert.Contains("3,\n" + lead + "ct);", fixedSource)
    Assert.Contains("4,\n" + leadOf "\"paren\"," fixedSource + "ct\n", fixedSource)
    Assert.Contains("""await FetchAsync("single", 1, ct);""", fixedSource)
    // the arguments together on the line below the call stay together
    Assert.Contains(""""below", 2, ct);""", fixedSource)
    // a lambda starting on the `(` line keeps the idiom (v2rayN's `_mutations.RunAsync`)
    Assert.Contains("}, ct);", fixedSource)
    // a directive before the `)`: the token goes after it, so an argument under `#if` stays in place
    Assert.DoesNotContain("7, ct", fixedSource)
    Assert.Contains("#endif\n" + leadOf ", 9" fixedSource + ", ct);", fixedSource)
