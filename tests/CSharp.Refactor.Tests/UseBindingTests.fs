module CSharp.Refactor.Tests.UseBindingTests

open Xunit
open CSharp.Refactor.Tests.Harness

// ---- CR0060 ----

[<Fact>]
let ``a disposable that stays in scope becomes a using declaration`` () =
    let source =
        """
using System;
using System.IO;
using System.Security.Cryptography;
class C
{
    int A(string path)
    {
        var s = new FileStream(path, FileMode.Open);
        return s.ReadByte();
    }
    string B(byte[] bytes)
    {
        var md5 = MD5.Create();
        var hash = md5.ComputeHash(bytes);
        return Convert.ToHexString(hash);
    }
    long D(string path)
    {
        var s = File.OpenRead(path);
        var len = s.Length;
        if (s.CanSeek) len += 1;
        return len;
    }
    void E(string path)
    {
        using var s = new FileStream(path, FileMode.Open);
        s.ReadByte();
    }
}
"""

    let fired = suggestCode "CR0060" source
    Assert.Equal(3, fired.Length)
    Assert.True(fired |> List.forall (fun s -> not s.Fixes.IsEmpty))
    let fixedSource = fixAll "CR0060" source

    Assert.Contains(
        "using var s = new FileStream(path, FileMode.Open);\n        return s.ReadByte();",
        normalize fixedSource
    )

    Assert.Contains("using var md5 = MD5.Create();", fixedSource)
    Assert.Contains("using var s = File.OpenRead(path);", fixedSource)
    Assert.DoesNotContain("using using", fixedSource)

[<Fact>]
let ``an ownership transfer is silent, an unknown destination is a note naming it`` () =
    let source =
        """
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
class Holder { public Stream Kept; }
class C
{
    readonly List<Stream> streams = new List<Stream>();
    Stream field;
    Stream Returned(string p) { var s = new FileStream(p, FileMode.Open); s.ReadByte(); return s; }
    (Stream, int) Tupled(string p) { var s = new FileStream(p, FileMode.Open); return (s, 1); }
    string Wrapped(string p) { var s = new FileStream(p, FileMode.Open); var r = new StreamReader(s); return r.ReadToEnd(); }
    void Stored(string p) { var s = new FileStream(p, FileMode.Open); streams.Add(s); }
    void Fielded(string p) { var s = new FileStream(p, FileMode.Open); field = s; }
    void Manual(string p) { var s = new FileStream(p, FileMode.Open); s.ReadByte(); s.Dispose(); }
    void Closed(string p) { var s = new FileStream(p, FileMode.Open); s.ReadByte(); s.Close(); }
    void Upcast(string p) { var s = new FileStream(p, FileMode.Open); ((IDisposable)s).Dispose(); }
    void Handed(string p) { var s = new FileStream(p, FileMode.Open); Keep(s); }
    void Adopted(string p) { var s = new FileStream(p, FileMode.Open); Consume(s); }
    void Captured(string p) { var s = new FileStream(p, FileMode.Open); Func<int> f = () => s.ReadByte(); f(); }
    void Pending(string p, byte[] b) { var s = new FileStream(p, FileMode.Open); var t = s.WriteAsync(b, 0, 1); Register(t); }
    async Task Awaited(string p, byte[] b) { var s = new FileStream(p, FileMode.Open); await s.WriteAsync(b, 0, 1); }
    void Keep(Stream s) { field = s; }
    void Consume(Stream s) { using (s) { s.ReadByte(); } }
    void Register(Task t) { }
    void Token() { var cts = new CancellationTokenSource(); StartWork(cts.Token); }
    async Task TokenAwaited() { var cts = new CancellationTokenSource(); await Task.Delay(10, cts.Token); }
    void StartWork(CancellationToken ct) { }
}
"""

    let fired = suggestCode "CR0060" source
    let fixes = fired |> List.filter (fun s -> not s.Fixes.IsEmpty) |> firedText source
    let notes = fired |> List.filter (fun s -> s.Fixes.IsEmpty)
    // fixes: the reader in Wrapped (it adopted the stream and stays), Awaited, TokenAwaited;
    // Adopted is a one-hop transfer (Consume disposes its parameter) and stays silent
    Assert.Equal<string list>(
        [
            "r = new StreamReader(s)"
            "s = new FileStream(p, FileMode.Open)"
            "cts = new CancellationTokenSource()"
        ],
        fixes
    )

    let messages = notes |> List.map (fun s -> s.Message)
    Assert.Equal(4, notes.Length)
    Assert.Contains(messages, fun m -> m.Contains "handed to 'Keep'")
    Assert.Contains(messages, fun m -> m.Contains "captured by a lambda")
    Assert.Contains(messages, fun m -> m.Contains "('t') is still pending, handed to 'Register'")
    Assert.Contains(messages, fun m -> m.Contains "its token is handed on")

[<Fact>]
let ``self-active objects, foreign wrappers, no-ownership types and Main are left alone`` () =
    let source =
        """
using System;
using System.IO;
using System.Net.Http;
using System.Threading;
class C
{
    void Timer() { var t = new Timer(_ => { }, null, 0, 100); Console.WriteLine(t.GetHashCode()); }
    void Watcher(string p) { var w = new FileSystemWatcher(p); w.Created += (s, e) => { }; }
    string Wrapper(Stream given) { var r = new StreamReader(given); return r.ReadToEnd(); }
    void Client() { var c = new HttpClient(); Console.WriteLine(c.Timeout); }
    void Buffer() { var ms = new MemoryStream(); ms.WriteByte(1); }
    static void Main(string[] args)
    {
        var s = new FileStream("x", FileMode.Open);
        s.ReadByte();
        var w = new StreamWriter("y");
        w.Write("z");
    }
}
"""

    let fired = suggestCode "CR0060" source
    // Main: the stream and the writer are flush-sensitive, and both fix
    Assert.Equal<string list>(
        [ "s = new FileStream(\"x\", FileMode.Open)"; "w = new StreamWriter(\"y\")" ],
        firedText source fired
    )

[<Fact>]
let ``chains through null-coalescing, awaited aliases, wrappers, elements and assertions are followed`` () =
    let source =
        """
using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
class Key { public Key(RSA rsa) { } }
class Bag { public Bag(Stream[] items) { } }
class Svc : IDisposable { public Task StartAsync(CancellationToken ct) => Task.CompletedTask; public void Dispose() { } }
class C
{
    Key Derived(byte[] raw)
    {
        var cert = new X509Certificate2(raw);
        var rsa = cert.GetRSAPublicKey() ?? throw new InvalidOperationException("no key");
        return new Key(rsa);
    }
    async Task Awaited()
    {
        var svc = new Svc();
        var run = svc.StartAsync(CancellationToken.None);
        await run;
    }
    Bag Element(string p)
    {
        var s = new FileStream(p, FileMode.Open);
        return new Bag([s]);
    }
    Task<Stream> Wrapped(string p)
    {
        var s = new FileStream(p, FileMode.Open);
        return Task.FromResult<Stream>(s);
    }
    void Asserted(string p)
    {
        var s = new FileStream(p, FileMode.Open);
        Assert.NotNull(s);
        Assert.True(s.CanRead);
    }
}
"""

    let fired = suggestCode "CR0060" source
    let fixes = fired |> List.filter (fun s -> not s.Fixes.IsEmpty) |> firedText source

    let notes =
        fired |> List.filter (fun s -> s.Fixes.IsEmpty) |> List.map (fun s -> s.Message)
    // Awaited and Asserted fix; Element and Wrapped are transfers; Derived hands the key on
    Assert.Equal<string list>([ "svc = new Svc()"; "s = new FileStream(p, FileMode.Open)" ], fixes)
    Assert.Equal(1, notes.Length)
    Assert.Contains("through 'rsa' it is handed to the constructor of 'Key'", notes.Head)

[<Fact>]
let ``an awaited BCL call is done with its argument, a settled value read through it stays`` () =
    let source =
        """
using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
class C
{
    readonly HttpClient client = new HttpClient();
    async Task<string> Send(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "t");
        var response = await client.SendAsync(request).ConfigureAwait(false);
        return await response.Content.ReadAsStringAsync();
    }
    Task<HttpResponseMessage> Pending(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        return client.SendAsync(request);
    }
}
"""

    // a request message owns nothing unmanaged, and a test's handler mock reads
    // it back after the send: neither a fix nor a note
    Assert.Empty(suggestCode "CR0060" source)
