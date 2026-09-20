/// The guards the M2 review added: each shape here was a silent semantics
/// change before it.
module CSharp.Refactor.Tests.M2ReviewTests

open Xunit
open CSharp.Refactor.Tests.Harness

[<Fact>]
let ``CR0020 keeps the copy over a lazy source when the loop can leave early`` () =
    let source =
        """
using System.Collections.Generic;
using System.Linq;
class C
{
    IEnumerable<int> Gen() { yield return 1; }
    int A(IEnumerable<int> xs) { foreach (var x in xs.ToList()) { if (x > 1) return x; } return 0; }
    void B(IEnumerable<int> xs) { foreach (var x in xs.ToList()) { System.Console.WriteLine(x); } }
    int D(HashSet<int> xs) { foreach (var x in xs.ToList()) { if (x > 1) return x; } return 0; }
}
"""

    // B walks it all either way; D's source is a collection; A could stop a generator short
    Assert.Equal<string list>([ ".ToList()"; ".ToList()" ], firedText source (suggestCode "CR0020" source))

[<Fact>]
let ``CR0021 leaves a float sum alone and keeps commented statements`` () =
    let source =
        """
using System.Collections.Generic;
using System.Linq;
class C
{
    float A(List<float> xs) { float t = 0f; foreach (var x in xs) t += x; return t; }
    double B(List<double> xs) { double t = 0; foreach (var x in xs) t += x; return t; }
    double D(List<double> xs)
    {
        double t = 0; // running total
        foreach (var x in xs) t += x;
        return t;
    }
}
"""

    let fired = suggestCode "CR0021" source
    Assert.Equal(1, fired.Length)
    Assert.Contains("return xs.Sum();", fixAll "CR0021" source)
    Assert.Contains("// running total", fixAll "CR0021" source)

[<Fact>]
let ``CR0029 does not fuse a use under a nested lambda or a call with type arguments`` () =
    let source =
        """
using System.Collections.Generic;
using System.Linq;
class C
{
    static int Load(int x) => x;
    IEnumerable<IEnumerable<int>> A(IEnumerable<int> xs, int[] ids) => xs.Select(x => Load(x)).Select(y => ids.Select(i => y + i));
    IEnumerable<int> B(IEnumerable<int> xs) => xs.Select<int, int>(x => x + 1).Select(y => y * 2);
    IEnumerable<int> D(IEnumerable<int> xs) => xs.Select(x => x + 1).Select(y => y * 2);
}
"""

    Assert.Equal(1, (suggestCode "CR0029" source).Length)
    Assert.Contains("xs.Select(x => (x + 1) * 2)", fixAll "CR0029" source)

[<Fact>]
let ``CR0032 leaves a concurrent dictionary alone, CR0033 an array piece`` () =
    let source =
        """
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
class C
{
    void A(ConcurrentDictionary<string, int> d) { foreach (var k in d.Keys) System.Console.WriteLine(d[k]); }
    void B(Dictionary<string, int> d) { foreach (var k in d.Keys) System.Console.WriteLine(d[k]); }
    void D(StringBuilder sb, char[] cs) { sb.Append("x" + cs); }
    void E(StringBuilder sb, string s) { sb.Append("x" + s); }
}
"""

    Assert.Equal(1, (suggestCode "CR0032" source).Length)
    Assert.Equal(1, (suggestCode "CR0033" source).Length)

[<Fact>]
let ``CR0023 leaves a literal holding null alone`` () =
    let source =
        """
using System.Linq;
class C
{
    static readonly string[] Allowed = { "a", null };
    static readonly string[] Named = { "a", "b" };
    bool A(string[] xs) => xs.Any(x => Allowed.Contains(x));
    bool B(string[] xs) => xs.Any(x => Named.Contains(x));
}
"""

    Assert.Equal<string list>([ "string[]" ], firedText source (suggestCode "CR0023" source))

[<Fact>]
let ``CR0053 leaves an event subscription alone`` () =
    let source =
        """
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
class C
{
    event EventHandler Changed;
    void A(List<int> xs) { xs.ForEach(async x => await Task.Delay(x)); }
    void B() { Changed += async (s, e) => await Task.Delay(1); }
}
"""

    Assert.Equal(1, (suggestCode "CR0053" source).Length)

[<Fact>]
let ``CR0041 and CR0043 stand down on a method mentioned as a group`` () =
    let source =
        """
using System;
using System.Linq;
using System.Threading.Tasks;
class C
{
    private int Load(int x) => Task.FromResult(x).Result;
    async Task A() { var v = Load(1); Console.WriteLine(v); }
    int[] B(int[] xs) => xs.Select(Load).ToArray();
    async void Fire() { await Task.Delay(1); }
    async Task D() { Fire(); Action a = Fire; }
}
"""

    Assert.Empty(suggestCode "CR0041" source)
    let voids = suggestCode "CR0043" source
    Assert.Equal(1, voids.Length)
    Assert.True(voids.Head.Fixes.IsEmpty)

[<Fact>]
let ``CR0042 leaves System.Xml twins and yield sleeps alone`` () =
    let source =
        """
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
class C
{
    async Task A(XmlReader r) { r.Read(); }
    async Task B() { Thread.Sleep(0); Thread.Sleep(1); Thread.Sleep(50); }
    async Task D(StreamReader r) { var line = r.ReadLine(); }
}
"""

    Assert.Equal<string list>([ "Thread.Sleep(50)"; "r.ReadLine()" ], firedText source (suggestCode "CR0042" source))

[<Fact>]
let ``CR0048 leaves a body that exits the monitor itself, or a commented finally, alone`` () =
    let source =
        """
using System.Threading;
class C
{
    readonly object gate = new object();
    void A()
    {
        Monitor.Enter(gate);
        try { Monitor.Exit(gate); Monitor.Enter(gate); }
        finally { Monitor.Exit(gate); }
    }
    void B()
    {
        Monitor.Enter(gate);
        try { }
        finally { Monitor.Exit(gate); } // release
    }
    void D()
    {
        Monitor.Enter(gate);
        try { }
        finally { Monitor.Exit(gate); }
    }
}
"""

    let fired =
        suggestCode "CR0048" source |> List.filter (fun s -> not s.Fixes.IsEmpty)

    Assert.Equal(1, fired.Length)

[<Fact>]
let ``CR0051 binds every return of the method once it is async`` () =
    let source =
        """
using System.IO;
using System.Threading.Tasks;
class C
{
    Task<int> A(string p, bool skip)
    {
        if (skip) return Task.FromResult(0);
        using var s = new FileStream(p, FileMode.Open);
        return s.ReadAsync(new byte[1], 0, 1);
    }
    Task B(string p, bool skip)
    {
        if (skip) return Task.CompletedTask;
        using var s = new FileStream(p, FileMode.Open);
        return s.FlushAsync();
    }
}
"""

    Assert.Equal(2, (suggestCode "CR0051" source).Length)
    let fixedSource = fixAll "CR0051" source
    Assert.Contains("if (skip) return 0;", fixedSource)
    Assert.Contains("return await s.ReadAsync(new byte[1], 0, 1);", fixedSource)
    Assert.Contains("if (skip) return;", fixedSource)
    Assert.Contains("await s.FlushAsync();", fixedSource)

[<Fact>]
let ``CR0065 converts only the last catch of its try and keeps a commented guard`` () =
    let source =
        """
using System;
using System.IO;
class C
{
    void A()
    {
        try { }
        catch (IOException ex) { if (ex.HResult != 5) throw; Console.WriteLine("io"); }
        catch (Exception) { Console.WriteLine("any"); }
    }
    void B()
    {
        try { }
        catch (IOException ex)
        {
            if (ex.HResult != 5) throw; // only sharing violations
            Console.WriteLine("io");
        }
    }
    void E()
    {
        try { }
        catch (ArgumentException) { Console.WriteLine("arg"); }
        catch (IOException ex) { if (ex.HResult != 5) throw; Console.WriteLine("io"); }
    }
}
"""

    Assert.Equal(1, (suggestCode "CR0065" source).Length)

[<Fact>]
let ``CR0069 keeps escapes and leaves a parameter-name argument alone`` () =
    let source =
        """
using System;
class C
{
    private void Load(int count) { throw new InvalidOperationException("Path is C:\\temp\\x"); }
    private void Check(int count) { if (count < 0) throw new ArgumentOutOfRangeException("count is small"); }
}
"""

    Assert.Equal(1, (suggestCode "CR0069" source).Length)

    Assert.Contains("$\"Path is C:\\\\temp\\\\x, calling {nameof(Load)} with count: {count}\"", fixAll "CR0069" source)

[<Fact>]
let ``CR0060 takes a conditional dispose and a using statement as manual management`` () =
    let source =
        """
using System.IO;
class C
{
    void A(string p) { var s = new FileStream(p, FileMode.Open); s.ReadByte(); s?.Dispose(); }
    void B(string p) { var s = new FileStream(p, FileMode.Open); using (s) { s.ReadByte(); } }
    void D(string p) { var s = new FileStream(p, FileMode.Open); s.ReadByte(); }
}
"""

    Assert.Equal(1, (suggestCode "CR0060" source).Length)
