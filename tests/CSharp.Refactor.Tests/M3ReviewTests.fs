/// The guards the M3 review added: each shape here was a silent semantics
/// change before it.
module CSharp.Refactor.Tests.M3ReviewTests

open Xunit
open CSharp.Refactor
open CSharp.Refactor.Tests.Harness

[<Fact>]
let ``CR0100 and CR0102 leave a user IFormattable alone: the handler formats it through ToString(format, provider)``
    ()
    =
    let source =
        """
using System;
class Money : IFormattable
{
    public override string ToString() => "money";
    public string ToString(string format, IFormatProvider provider) => "formatted";
}
class Plain { public override string ToString() => "plain"; }
class C
{
    string A(Money m, string s) => "x " + m + " " + s;
    string B(Plain p, string s) => "x " + p + " " + s;
    string D(Money m) => $"{m.ToString()}";
    string E(Plain p) => $"{p.ToString()}";
    string F(double d) => $"{d.ToString()}";
}
"""

    Assert.Equal<string list>([ "\"x \" + p + \" \" + s" ], firedText source (suggestCode "CR0100" source))
    Assert.Equal(2, (suggestCode "CR0102" source).Length)

[<Fact>]
let ``CR0087 leaves an enum with duplicate values alone`` () =
    let source =
        """
enum Alias { A = 1, B = 1 }
enum Plain { A = 1, B = 2 }
class C
{
    bool X(Alias a) => a.ToString() == "B";
    bool Y(Plain p) => p.ToString() == "B";
}
"""

    Assert.Equal(1, (suggestCode "CR0087" source).Length)

[<Fact>]
let ``CR0080 counts equality-using calls as identity, CR0083 a deconstruction as a write`` () =
    let source =
        """
using System.Collections.Generic;
using System.Linq;
class Key
{
    public int Id { get; }
    public Key(int id) { Id = id; }
}
class Free
{
    public int Id { get; }
    public Free(int id) { Id = id; }
}
class Pair
{
    public int X { get; set; }
    public int Y { get; set; }
}
class Use
{
    readonly List<Key> keys = new List<Key>();
    void A(Key k) { keys.Remove(k); }
    Free B() => new Free(1);
    void D(Pair p, int a) { (p.X, a) = (1, 2); }
    Pair E() => new Pair { X = 1, Y = 2 };
}
"""

    Assert.Equal<string list>([ "Free" ], firedText source (suggestCode "CR0080" source))
    // Y is only ever set in an initialiser; X is deconstructed into
    Assert.Equal<string list>([ "set" ], firedText source (suggestCode "CR0083" source))

[<Fact>]
let ``CR0150 keeps a dictionary that is enumerated, CR0149 is off by default`` () =
    let source =
        """
using System.Collections.Generic;
using System.Linq;
class C
{
    private static readonly Dictionary<string, int> Ordered = new Dictionary<string, int> { ["a"] = 1, ["b"] = 2 };
    private static readonly Dictionary<string, int> Looked = new Dictionary<string, int> { ["a"] = 1 };
    IEnumerable<string> A() => Ordered.Select(kv => kv.Key);
    int B(string k) => Looked.TryGetValue(k, out var v) ? v : 0;
}
class Options { public string Name { get; init; } }
class Use { Options A() => new Options { Name = "x" }; }
"""

    Assert.Equal<string list>([ "Dictionary<string, int>" ], firedText source (suggestCode "CR0150" source))
    Assert.Contains("FrozenDictionary<string, int> Looked", fixAll "CR0150" source)
    // off by default: `required` is a runtime demand on every deserializer of the type
    Assert.Equal(Some false, CSharp.Refactor.RuleCatalog.tryFind "CR0149" |> Option.map (fun r -> r.Default))

[<Fact>]
let ``CR0153 keeps a backing field of another type or with an effectful initialiser`` () =
    let source =
        """
using System;
class C
{
    private long _wide;
    public int Wide { get => (int)_wide; set => _wide = value; }
    private int _stamp = Environment.TickCount;
    public int Stamp { get => _stamp; set => _stamp = value; }
    private int _plain = 1;
    public int Plain { get => _plain; set => _plain = value; }
}
"""

    Assert.Equal<string list>([ "Plain" ], firedText source (suggestCode "CR0153" source))

[<Fact>]
let ``CR0147 needs a subject the flow proves not null and var binders`` () =
    let source =
        """
#nullable enable
class C
{
    string A(int[]? xs)
    {
        if (xs.Length == 0) return "none";
        else if (xs.Length == 1) { var a = xs[0]; return "one"; }
        else return "many";
    }
    string B(int[] xs)
    {
        if (xs.Length == 0) return "none";
        else if (xs.Length == 1) { long a = xs[0]; return "one"; }
        else return "many";
    }
    string D(int[] xs)
    {
        if (xs.Length == 0) return "none";
        else if (xs.Length == 1) { var a = xs[0]; return "one"; }
        else return "many";
    }
}
"""

    let fired = suggestCode "CR0147" source
    // A's subject may be null; B's binder is not var (the pattern would retype it); D converts
    Assert.Equal(2, fired.Length)
    Assert.Contains("case [var a]:", fixAll "CR0147" source)
    Assert.Contains("long a = xs[0];", fixAll "CR0147" source)

[<Fact>]
let ``CR0126 leaves the RSA and DSA providers alone: the factory changes the key size`` () =
    let source =
        """
using System.Security.Cryptography;
class C
{
    void A() { using var rsa = new RSACryptoServiceProvider(); }
    void B() { using var sha = new SHA256Managed(); }
}
"""

    Assert.Equal(1, (suggestCode "CR0126" source).Length)

[<Fact>]
let ``CR0109 takes the field form under a generic container`` () =
    let source =
        """
using System.Text.RegularExpressions;
class Box<T>
{
    bool A(string s) => Regex.IsMatch(s, @"\d+");
}
"""

    let fixedSource = fixAll "CR0109" source
    Assert.Contains("private static readonly Regex ARegex = new Regex(@\"\\d+\");", fixedSource)
    Assert.DoesNotContain("GeneratedRegex", fixedSource)

[<Fact>]
let ``CR0083 sees a null-conditional assignment as a write`` () =
    let source =
        """
internal class Balance
{
    public string Status { get; set; }
    public string Note { get; set; }
}
internal class Account
{
    public Balance Balance { get; set; }
}
class C
{
    void A(Account a, string s)
    {
        a.Balance?.Status = s;
        var b = new Balance { Note = "x", Status = "y" };
        System.Console.WriteLine(b.Note);
    }
}
"""

    let fired = suggestCode "CR0083" source
    // the gate is open and the rule awake: the initialiser-only sibling fires, the
    // null-conditionally assigned one does not
    let property (s: Suggestion) =
        let before = (normalize source).Substring(0, s.Span.Start)

        if before.LastIndexOf "Note" > before.LastIndexOf "Status" then
            "Note"
        else
            "Status"

    Assert.Equal<string list>([ "Note" ], fired |> List.map property)
