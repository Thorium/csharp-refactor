module CSharp.Refactor.Tests.ExceptionTests

open Xunit
open CSharp.Refactor.Tests.Harness

// ---- CR0064 ----

[<Fact>]
let ``a catch-all that never reads the exception is noted, the idioms and acknowledged ones are not`` () =
    let source =
        """
using System;
using System.IO;
class C
{
    int Parse(string s) => int.Parse(s);
    int A(string s) { try { return Parse(s); } catch { return 0; } }
    int B(string s) { try { return Parse(s); } catch (Exception) { return -1; } }
    int D(string s) { try { return Parse(s); } catch (Exception e) { Console.WriteLine(e); return -1; } }
    int E(string s) { try { return Parse(s); } catch (Exception) { throw; } }
    bool F(string s) { try { Parse(s); return true; } catch { return false; } }
    bool TryGet(string s, out int v) { try { v = Parse(s); return true; } catch { v = 0; return false; } }
    int G(string s) { try { return Parse(s); } catch { return 0; } // best effort: the default is fine
    }
    void H(IDisposable d) { try { d.Dispose(); } catch { } }
    bool I(string p) { try { return File.Exists(p); } catch { return false; } }
    int J(string s) { try { return Parse(s); } catch (FormatException) { return 0; } }
    int K(string s) { try { return Parse(s); } catch (OperationCanceledException) { throw; } catch { return 0; } }
    int L(string s) { try { return Parse(s); } catch (Exception e) when (s.Length > 3) { return 0; } }
}
"""

    let fired = suggestCode "CR0064" source
    let texts = firedText source fired
    // A, B, H (teardown), I (probe), L (a filter that never reads the exception)
    Assert.Equal(5, fired.Length)
    Assert.True(fired |> List.exists (fun s -> s.Message.Contains "teardown"))
    Assert.True(fired |> List.exists (fun s -> s.Message.Contains "probe"))

    Assert.Equal(
        3,
        fired
        |> List.filter (fun s -> s.Message.Contains "swallows every failure")
        |> List.length
    )

    Assert.Equal<string list>([ "catch"; "catch"; "catch"; "catch"; "catch" ], texts)

// ---- CR0065 ----

[<Fact>]
let ``a rethrowing guard becomes an exception filter, an effectful one stays`` () =
    let source =
        """
using System;
using System.IO;
class C
{
    void A()
    {
        try { }
        catch (IOException ex)
        {
            if (ex.HResult != 5) throw;
            Console.WriteLine("access");
        }
    }
    void B() { try { } catch (IOException ex) { if (Console.Read() > 0) throw; Console.WriteLine(ex); } }
    void D() { try { } catch (IOException ex) { if (ex.HResult == 5) throw; } }
}
"""

    let fired = suggestCode "CR0065" source
    Assert.Equal(2, fired.Length)
    let fixedSource = fixAll "CR0065" source

    Assert.Contains(
        normalize
            """        catch (IOException ex) when (ex.HResult == 5)
        {
            Console.WriteLine("access");
        }""",
        fixedSource
    )

    Assert.Contains("catch (IOException ex) when (ex.HResult != 5) {  }", fixedSource)
    Assert.Contains("if (Console.Read() > 0) throw;", fixedSource)

[<Fact>]
let ``CR0065 keeps a guard whose condition can throw as a note`` () =
    let source =
        """
using System;
using System.IO;
class C
{
    void A() { try { } catch (IOException ex) { if (!(ex.InnerException.HResult == 5)) throw; Console.WriteLine("a"); } }
    void B() { try { } catch (IOException ex) { if ((int)ex.Data["code"] != 5) throw; Console.WriteLine("b"); } }
    void D() { try { } catch (IOException ex) { if (!ex.Message.Contains("disk")) throw; Console.WriteLine("d"); } }
}
"""

    let fired = suggestCode "CR0065" source
    Assert.Equal(3, fired.Length)
    // A reads through a possibly-null inner exception, B indexes and casts: notes
    Assert.Equal(2, fired |> List.filter (fun s -> s.Fixes.IsEmpty) |> List.length)
    let fixedSource = fixAll "CR0065" source
    Assert.Contains("if (!(ex.InnerException.HResult == 5)) throw;", fixedSource)
    Assert.Contains("if ((int)ex.Data[\"code\"] != 5) throw;", fixedSource)
    Assert.Contains("catch (IOException ex) when (ex.Message.Contains(\"disk\"))", fixedSource)

// ---- CR0066 / CR0067 / CR0068 ----

[<Fact>]
let ``throws in finally, in special members and of reserved types are noted`` () =
    let source =
        """
using System;
class C : IDisposable
{
    void A() { try { } finally { throw new InvalidOperationException(); } }
    void B() { try { } finally { try { throw new InvalidOperationException(); } catch { } } }
    public override string ToString() => throw new NotSupportedException();
    public override int GetHashCode() { try { throw new Exception(); } catch { return 0; } }
    public void Dispose() { if (Console.Read() > 0) throw new ObjectDisposedException("C"); }
    void D(object o) { if (o == null) throw new NullReferenceException(); }
    void E(int i) { if (i < 0) throw new IndexOutOfRangeException(); }
    void F(int kind)
    {
        switch (kind)
        {
            case 1: throw new NullReferenceException();
            case 2: throw new OutOfMemoryException();
            case 3: throw new StackOverflowException();
        }
    }
    void G() { throw new Exception("plain"); }
}
"""

    Assert.Equal<string list>(
        [ "throw new InvalidOperationException();" ],
        firedText source (suggestCode "CR0066" source)
    )

    Assert.Equal(2, (suggestCode "CR0067" source).Length)

    Assert.Equal<string list>(
        [ "new NullReferenceException()"; "new IndexOutOfRangeException()" ],
        firedText source (suggestCode "CR0068" source)
    )

[<Fact>]
let ``CR0066 leaves a throw the finally's own cleanup catch handles alone`` () =
    let source =
        """
using System;
using System.IO;
class ReportExport
{
    public void Export(string tempPath, string target)
    {
        try
        {
            File.WriteAllText(tempPath, "report");
            File.Copy(tempPath, target, true);
        }
        finally
        {
            try
            {
                File.Delete(tempPath);
                if (File.Exists(tempPath)) throw new IOException($"temp file {tempPath} is still locked");
            }
            catch (IOException ex) { Console.Error.WriteLine(ex.Message); }
        }
    }
}
"""

    // the throw never leaves the finally: the export's own failure still propagates
    Assert.Empty(suggestCode "CR0066" source)

[<Fact>]
let ``CR0067 lets an explicit conversion reject an out-of-range percentage`` () =
    let source =
        """
using System;
readonly struct Percentage
{
    public decimal Value { get; }
    Percentage(decimal value) => Value = value;
    public static explicit operator Percentage(decimal value) =>
        value is >= 0m and <= 100m
            ? new Percentage(value)
            : throw new ArgumentOutOfRangeException(nameof(value), value, "a percentage is 0..100");
    public static implicit operator decimal(Percentage p) => p.Value;
}
"""

    // an explicit cast is the caller asking for a checked conversion; only implicit ones must not throw
    Assert.Empty(suggestCode "CR0067" source)

[<Fact>]
let ``CR0068 leaves a chaos-testing switch that injects runtime faults alone`` () =
    let source =
        """
using System;
enum Fault { None, NullReference, OutOfMemory, IndexRange }
class ChaosInjector
{
    public void Inject(Fault fault) =>
        _ = fault switch
        {
            Fault.NullReference => throw new NullReferenceException("chaos: injected"),
            Fault.OutOfMemory => throw new OutOfMemoryException("chaos: injected"),
            Fault.IndexRange => throw new IndexOutOfRangeException("chaos: injected"),
            _ => 0,
        };
}
"""

    // three distinct runtime exceptions from one switch: a fault-injection table
    Assert.Empty(suggestCode "CR0068" source)

// ---- CR0069 ----

[<Fact>]
let ``a constant message on a private method with printable parameters quotes them; invariants, secrets and mentions stay``
    ()
    =
    let source =
        """
using System;
class Order { }
record Req(string Account, string Name);
class C
{
    private void A(int count, string name) { if (count < 0) throw new ArgumentException("Rejected"); }
    private void B(int count) { throw new InvalidOperationException("count is off"); }
    private void D(int count) { throw new InvalidOperationException("unreachable"); }
    private void E(string password) { throw new InvalidOperationException("Login failed"); }
    private void E2(string connectionString) { throw new InvalidOperationException("Connect failed"); }
    private void F(Order order) { throw new InvalidOperationException("Order rejected"); }
    private void F2(Req req) { throw new InvalidOperationException("Request rejected"); }
    private void G(int n) { throw new InvalidOperationException($"n is {n}"); }
    private void H(int n) { throw new InvalidOperationException("G failed"); }
    public void Pub(int n) { throw new InvalidOperationException("Public failure"); }
}
"""

    let fired = suggestCode "CR0069" source
    Assert.Equal<string list>([ "\"Rejected\""; "\"G failed\"" ], firedText source fired)
    let fixedSource = fixAll "CR0069" source

    Assert.Contains(
        "throw new ArgumentException($\"Rejected, calling {nameof(A)} with count: {count}, name: {name}\");",
        fixedSource
    )

    Assert.Contains("throw new InvalidOperationException(\"count is off\");", fixedSource)

// ---- CR0070 ----

[<Fact>]
let ``an exception whose informative member goes unread is noted`` () =
    let source =
        """
using System;
using System.Reflection;
class C
{
    void A() { try { } catch (ReflectionTypeLoadException e) { Console.WriteLine(e.Message); } }
    void B() { try { } catch (ReflectionTypeLoadException e) { Console.WriteLine(e.Message); foreach (var x in e.LoaderExceptions) Console.WriteLine(x); } }
    void D() { try { } catch (AggregateException e) { Console.WriteLine(e.Message); } }
    void E() { try { } catch (AggregateException e) when (e.InnerExceptions.Count > 1) { Console.WriteLine(e.Message); } }
}
"""

    Assert.Equal<string list>(
        [ "(ReflectionTypeLoadException e)"; "(AggregateException e)" ],
        firedText source (suggestCode "CR0070" source)
    )

    // the loader failure's editor offer (FR0151's): the messages joined in
    // place of the one `.Message` read, `using System.Linq` added; the
    // aggregate has no fix, and a sweep applies nothing
    match suggestCode "CR0070" source with
    | [ loader; aggregate ] ->
        let join = loader.Fixes |> List.exactlyOne
        Assert.True join.EditorOnly
        let joined = applyFix source join

        Assert.Contains(
            "Console.WriteLine(string.Join(\"; \", e.LoaderExceptions.Where(x => x != null).Select(x => x.Message)));",
            joined
        )

        Assert.Contains("using System.Linq;", joined)
        Assert.Empty aggregate.Fixes
        Assert.Equal(normalize source, fixAll "CR0070" source)
    | other -> failwithf "Expected two notes, got %A" other

// ---- CR0061 / CR0062 / CR0063 ----

[<Fact>]
let ``ownerless disposables, unreleased fields and a fake Dispose are noted`` () =
    let source =
        """
using System;
using System.IO;
using System.Threading;
class Owner { readonly FileStream stream = new FileStream("x", FileMode.Open); }
class Injected { readonly Stream stream; public Injected(Stream s) { stream = s; } }
class Managed { readonly FileStream stream = new FileStream("x", FileMode.Open); public void Close() { stream.Dispose(); } }
class Proper : IDisposable { readonly FileStream stream = new FileStream("x", FileMode.Open); public void Dispose() { stream.Dispose(); } }
class Forgetful : IDisposable
{
    readonly FileStream stream = new FileStream("x", FileMode.Open);
    readonly CancellationTokenSource cts = new CancellationTokenSource();
    public void Dispose() { cts.Cancel(); }
}
class Fake { public void Dispose() { } }
class Buffer { readonly MemoryStream ms = new MemoryStream(); }
"""

    Assert.Equal<string list>(
        [ "stream = new FileStream(\"x\", FileMode.Open)" ],
        firedText source (suggestCode "CR0061" source)
    )

    let unreleased = suggestCode "CR0062" source
    Assert.Equal(2, unreleased.Length)
    Assert.True(unreleased |> List.exists (fun s -> s.Message.Contains "Cancel frees nothing"))
    Assert.Equal<string list>([ "Dispose" ], firedText source (suggestCode "CR0063" source))

[<Fact>]
let ``CR0063 leaves a Dispose override on a type disposable through its base class alone`` () =
    let source =
        """
using System;
abstract class ConnectionScope : IDisposable
{
    public virtual void Dispose() { }
}
sealed class TransactionGuard : ConnectionScope
{
    bool _committed;
    public void Complete() => _committed = true;
    public override void Dispose()
    {
        if (!_committed) Rollback();
        base.Dispose();
    }
    void Rollback() { }
}
"""

    // `using (var tx = new TransactionGuard())` finds IDisposable on the base
    Assert.Empty(suggestCode "CR0063" source)

[<Fact>]
let ``CR0063 leaves a ref struct's pooled buffer: using takes its public Dispose without IDisposable`` () =
    let source =
        """
using System;
using System.Buffers;
ref struct PooledBuffer
{
    private byte[] _array;
    public PooledBuffer(int size) { _array = ArrayPool<byte>.Shared.Rent(size); }
    public Span<byte> Span => _array;
    public void Dispose() { ArrayPool<byte>.Shared.Return(_array); _array = null; }
}
class Reader
{
    int Read() { using var buffer = new PooledBuffer(64); return buffer.Span.Length; }
}
"""

    Assert.Empty(suggestCode "CR0063" source)

[<Fact>]
let ``CR0061 and CR0062 offer the interface and the release in the editor, never in a sweep`` () =
    let source =
        """
using System;
using System.IO;
using System.Threading;
class Owner
{
    readonly FileStream stream = new FileStream("x", FileMode.Open);
    readonly SemaphoreSlim gate = new SemaphoreSlim(1);
    int Read() => stream.ReadByte();
}
class Forgetful : IDisposable
{
    readonly FileStream stream = new FileStream("x", FileMode.Open);
    readonly CancellationTokenSource cts = new CancellationTokenSource();
    public void Dispose()
    {
        cts.Cancel();
    }
}
"""

    let ownerless = suggestCode "CR0061" source |> List.exactlyOne
    let implement = ownerless.Fixes |> List.exactlyOne
    Assert.True(implement.EditorOnly, "implementing an interface is the editor's offer")
    let implemented = applyFix source implement
    Assert.Contains("class Owner : IDisposable", implemented)
    Assert.Contains("    public void Dispose()\n    {\n        stream?.Dispose();\n    }\n}", implemented)
    Assert.DoesNotContain("gate?.Dispose()", implemented)
    Assert.Equal(normalize source, fixAll "CR0061" source)

    let unreleased = suggestCode "CR0062" source
    Assert.Equal(2, unreleased.Length)

    for s in unreleased do
        let release = s.Fixes |> List.exactlyOne
        Assert.True release.EditorOnly
        let released = applyFix source release

        Assert.Contains(
            "    public void Dispose()\n    {\n        "
            + (if s.Message.Contains "'cts'" then
                   "cts?.Dispose();"
               else
                   "stream?.Dispose();")
            + "\n        cts.Cancel();",
            released
        )

[<Fact>]
let ``CR0064 offers the guard, the narrower IO catch and a log line in the editor`` () =
    let source =
        """
using System;
using System.IO;
using Microsoft.Extensions.Logging;
namespace Microsoft.Extensions.Logging
{
    public interface ILogger { }
    public static class LoggerExtensions
    {
        public static void LogError(this ILogger l, System.Exception exception, string message, params object[] args) { }
        public static void LogInformation(this ILogger l, string message, params object[] args) { }
    }
}
class C
{
    readonly ILogger _log;
    public C(ILogger log) { _log = log; _log.LogInformation("ready"); }
    int Ratio(int a, int b)
    {
        try { return a / b; } catch { return 0; }
    }
    string Read(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception)
        {
            return "";
        }
    }
}
"""

    match suggestCode "CR0064" source with
    | [ ratio; read ] ->
        Assert.True(ratio.Fixes |> List.forall (fun f -> f.EditorOnly))
        let guard = ratio.Fixes |> List.find (fun f -> f.Title.StartsWith "Guard")
        Assert.Contains("if (b == 0) return 0;\n        return a / b;", applyFix source guard)

        let narrower = read.Fixes |> List.find (fun f -> f.Title.StartsWith "Catch the IO")

        Assert.Contains(
            "catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)",
            applyFix source narrower
        )

        let logged = read.Fixes |> List.find (fun f -> f.Title.StartsWith "Log")
        let loggedSource = applyFix source logged

        Assert.Contains(
            "catch (Exception ex)\n        {\n            _log.LogError(ex, \"Read failed\");\n            return \"\";",
            loggedSource
        )

        // the division body does no IO; the IO body is no division
        Assert.DoesNotContain(ratio.Fixes, fun f -> f.Title.StartsWith "Catch the IO")
        Assert.DoesNotContain(read.Fixes, fun f -> f.Title.StartsWith "Guard")
        Assert.Equal(normalize source, fixAll "CR0064" source)
    | other -> failwithf "Expected two swallow notes, got %A" other

[<Fact>]
let ``CR0064 keeps the IO narrowing off a body that reads and then parses`` () =
    // the parser's exception would escape a catch narrowed to IO
    let source =
        """
using System;
using System.IO;
using System.Text.Json;
class C
{
    int Read(string path)
    {
        try
        {
            var text = File.ReadAllText(path);
            return JsonSerializer.Deserialize<int>(text);
        }
        catch (Exception)
        {
            return 0;
        }
    }
    int Read2(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<int>(File.ReadAllText(path));
        }
        catch (Exception)
        {
            return 0;
        }
    }
}
"""

    for s in suggestCode "CR0064" source do
        Assert.DoesNotContain(s.Fixes, fun f -> f.Title.StartsWith "Catch the IO")
