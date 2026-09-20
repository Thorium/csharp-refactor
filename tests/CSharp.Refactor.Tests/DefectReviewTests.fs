/// The critical review of family J and the edit sets: each shape here
/// was a wrong or half-applied rewrite before its guard.
module CSharp.Refactor.Tests.DefectReviewTests

open Xunit
open CSharp.Refactor.Roslyn
open CSharp.Refactor.Tests.Harness
open Microsoft.CodeAnalysis.CSharp

[<Fact>]
let ``CR0160 copies an outer loop's variable read from an inner loop, and leaves a cell another closure writes`` () =
    let source =
        """
using System;
using System.Collections.Generic;
class C
{
    void A(List<Action> actions, List<Action> resets)
    {
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++)
            {
                actions.Add(() => Console.WriteLine(i + j));
            }
        }
        int n = 0;
        for (int k = 0; k < 3; k++)
        {
            n = k;
            actions.Add(() => Console.WriteLine(n));
            resets.Add(() => n = 0);
        }
    }
}
"""

    let fired = suggestCode "CR0160" source
    // i and j from the first nest; n is a shared cell the reset closure writes: quiet
    Assert.Equal(2, fired.Length)
    let fixedSource = fixAll "CR0160" source

    Assert.Contains(
        "var i1 = i;\n                var j1 = j;\n                actions.Add(() => Console.WriteLine(i1 + j1));",
        fixedSource
    )

    Assert.Contains("actions.Add(() => Console.WriteLine(n));", fixedSource)

[<Fact>]
let ``CR0166 keeps a numeric parse under a FormatException catch, where an overflow used to propagate`` () =
    let source =
        """
using System;
class C
{
    long A(string s)
    {
        long v;
        try { v = long.Parse(s); } catch (FormatException) { v = 0; }
        return v;
    }
    bool B(string s)
    {
        bool v;
        try { v = bool.Parse(s); } catch (FormatException) { v = false; }
        return v;
    }
    int D(string? s)
    {
        int v;
        try { v = int.Parse(s!); } catch (OverflowException) { v = 0; }
        return v;
    }
}
"""

    let fired = suggestCode "CR0166" source
    let fixes = fired |> List.filter (fun s -> not s.Fixes.IsEmpty)
    Assert.Equal(1, fixes.Length)
    let fixedSource = fixAll "CR0166" source
    Assert.Contains("try { v = long.Parse(s); } catch (FormatException) { v = 0; }", fixedSource)
    Assert.Contains("if (!bool.TryParse(s, out v)) { v = false; }", fixedSource)
    Assert.Contains("try { v = int.Parse(s!); } catch (OverflowException) { v = 0; }", fixedSource)

[<Fact>]
let ``CR0171 leaves a CoreLib dictionary's indexer set and Remove under its own foreach`` () =
    let source =
        """
using System.Collections.Generic;
class C
{
    void A(Dictionary<string, int> map, HashSet<int> set)
    {
        foreach (var kv in map)
        {
            map[kv.Key] = kv.Value + 1;
        }
        foreach (var k in map)
        {
            if (k.Value < 0) map.Remove(k.Key);
        }
        foreach (var x in set)
        {
            if (x < 0) set.Remove(x);
        }
        foreach (var kv in map)
        {
            map.Add(kv.Key + "x", 1);
        }
        // the key collection enumerates the dictionary itself: an Add on it throws too
        foreach (var k in map.Keys)
        {
            map.Add(k + "y", 2);
        }
        foreach (var v in map.Values)
        {
            map.Remove(v.ToString());
        }
    }
}
"""

    let fired = suggestCode "CR0171" source
    Assert.Equal<string list>([ "map.Add(kv.Key + \"x\", 1)"; "map.Add(k + \"y\", 2)" ], firedText source fired)

    Assert.Contains("foreach (var k in map.Keys.ToList())", fixAll "CR0171" source)

[<Fact>]
let ``CR0015 spells the element type where the collection enumerates something else than its indexer returns`` () =
    let source =
        """
using System.Collections.Generic;
using System.Text.RegularExpressions;
class C
{
    int A(MatchCollection matches, List<int> xs)
    {
        var n = 0;
        for (var i = 0; i < matches.Count; i++)
        {
            n += matches[i].Value.Length;
        }
        for (var i = 0; i < xs.Count; i++)
        {
            n += xs[i];
        }
        return n;
    }
}
"""

    let fixedSource = fixAll "CR0015" source
    Assert.Contains("foreach (Match match in matches)", fixedSource)
    Assert.Contains("foreach (var item in xs)", fixedSource)

[<Fact>]
let ``a using lands inside the namespace where the compilation keeps its usings there`` () =
    // CR0171's snapshot needs System.Linq; the file has no usings, and every
    // other file of the compilation keeps them inside the namespace
    let source =
        """
namespace App;

public class Cleaner
{
    public void A(System.Collections.Generic.List<int> xs)
    {
        foreach (var x in xs)
        {
            if (x < 0)
            {
                System.Console.WriteLine(x);
                xs.Add(-x);
            }
        }
    }
}
"""

    let fixedSource = fixAll "CR0171" source
    // the harness compiles one file: no convention to read, the top of the file it is
    Assert.StartsWith("using System.Linq;", fixedSource.TrimStart())

[<Fact>]
let ``CR0082 retypes a tuple handed to a source parameter of its type, and holds one handed to a library, a type parameter or an unbound callee``
    ()
    =
    let source =
        """
using System;
class C
{
    private int Take(Tuple<int, string> p) => p.Item1;
    private T Wrap<T>(T x) => x;
    private int A() { var t = Tuple.Create(1, "a"); return Take(t); }
    private string B() { var t = Tuple.Create(1, 2); return string.Format("{0}", t); }
    private object D() { var t = Tuple.Create(1, true); return Wrap(t); }
}
"""

    Assert.Equal(1, (suggestCode "CR0082" source).Length)
    let fixedSource = fixAll "CR0082" source
    Assert.Contains("private int Take((int, string) p) => p.Item1;", fixedSource)
    Assert.Contains("(int, string) t = (1, \"a\"); return Take(t);", fixedSource)
    Assert.Contains("var t = Tuple.Create(1, 2); return string.Format(\"{0}\", t);", fixedSource)
    Assert.Contains("var t = Tuple.Create(1, true); return Wrap(t);", fixedSource)

    // a callee that does not bind (a reference the compilation lacks) hides
    // what it takes: the rule stands down rather than trust an empty check
    let broken =
        """
using System;
class C
{
    private int A() { var t = Tuple.Create(1, "a"); return Missing.Take(t); }
}
"""

    let compilation, tree = compile broken
    let model = compilation.GetSemanticModel(tree, false)

    let fired =
        Rules.all tree model (Context.forTree None compilation tree false)
        |> List.filter (fun s -> s.Code = "CR0082")

    Assert.Empty fired

[<Fact>]
let ``CR0080 leaves a class whose getter has a body, and takes the auto-property twin`` () =
    let source =
        """
using System;
class Lazy
{
    static string cached;
    public string Template { get { if (cached == null) cached = "x"; return cached; } }
}
class Computed
{
    public int Half => Whole / 2;
    public int Whole { get; }
    public Computed(int whole) { Whole = whole; }
}
class Plain
{
    public string Template { get; }
    public Plain(string template) { Template = template; }
}
"""

    Assert.Equal<string list>([ "Plain" ], firedText source (suggestCode "CR0080" source))

[<Fact>]
let ``CR0100 and CR0101 leave an argument whose interpolated form would pick another overload`` () =
    // EF Core 2.x's shape: `ExecuteSqlCommand(RawSqlString, params object[])` takes a
    // string through a user-defined conversion; `ExecuteSqlCommand(FormattableString)`
    // takes an interpolated string directly and parameterises its holes
    let source =
        """
using System;
struct RawSqlString
{
    public string Format;
    public RawSqlString(string s) { Format = s; }
    public static implicit operator RawSqlString(string s) => new RawSqlString(s);
}
class Db
{
    public int ExecuteSqlCommand(RawSqlString sql, params object[] parameters) => 0;
    public int ExecuteSqlCommand(FormattableString sql) => 1;
    public int Plain(string sql) => 2;
}
class C
{
    int A(Db db, string table) => db.ExecuteSqlCommand("DELETE FROM " + table + " WHERE 1 = 1");
    int B(Db db, string table) => db.ExecuteSqlCommand(string.Format("DELETE FROM {0} WHERE 1 = 1", table));
    int D(Db db, string table) => db.Plain("DELETE FROM " + table + " WHERE 1 = 1");
    int E(Db db, string table) => db.Plain(string.Format("DELETE FROM {0} WHERE 1 = 1", table));
}
"""

    Assert.Empty(
        suggestCode "CR0100" source
        |> List.filter (fun s -> s.Span.Start < source.IndexOf "int D")
    )

    Assert.Empty(
        suggestCode "CR0101" source
        |> List.filter (fun s -> s.Span.Start < source.IndexOf "int D")
    )

    let fixedSource = fixAll "CR0100" source |> fixAll "CR0101"
    Assert.Contains("db.ExecuteSqlCommand(\"DELETE FROM \" + table + \" WHERE 1 = 1\")", fixedSource)
    Assert.Contains("db.ExecuteSqlCommand(string.Format(\"DELETE FROM {0} WHERE 1 = 1\", table))", fixedSource)
    Assert.Contains("db.Plain($\"DELETE FROM {table} WHERE 1 = 1\")", fixedSource)

// ---- what the property suite's coverage pass found (2026-09-20) ----

[<Fact>]
let ``CR0084 offers readonly only where every write is a constructor's`` () =
    let source =
        """
static class Globals
{
    public static int Counter;
    public static int Fixed;
    static Globals() { Fixed = 1; Fixed = 2; }
    public static void A() { Counter++; }
    public static void B() { Counter = 0; }
}
"""

    let fired = suggestCode "CR0084" source
    Assert.Equal<string list>([ "Counter"; "Fixed" ], firedText source fired)

    let titles (name: string) =
        fired
        |> List.find (fun s -> normalize source |> fun t -> t.Substring(s.Span.Start, s.Span.Length) = name)
        |> fun s -> s.Fixes |> List.map (fun f -> f.Title)
    // a method's write is what the note is about: `readonly` would not compile there
    Assert.Equal<string list>([ "Make it private" ], titles "Counter")
    Assert.Equal<string list>([ "Make it private"; "Make it readonly" ], titles "Fixed")

[<Fact>]
let ``CR0109 leaves a pattern the engine rejects to CR0107`` () =
    // hoisted into a generated regex it would fail the build, where the call only threw when reached
    let source =
        """
using System.Text.RegularExpressions;
class C
{
    bool A(string s) => Regex.IsMatch(s, "(unclosed");
}
"""

    Assert.Empty(suggestCode "CR0109" source)
    Assert.Equal(1, (suggestCode "CR0107" source).Length)

[<Fact>]
let ``CR0047's gate and CR0009's or-pattern follow the file's language version`` () =
    let source =
        "using System;\nusing System.Threading;\nclass C\n{\n    int count;\n    void A() { lock (this) { count++; } }\n    string B(int k) => k switch\n    {\n        1 => \"a\",\n        2 => \"a\",\n        _ => \"b\",\n    };\n}\n"

    let at (version: LanguageVersion) =
        let compilation, tree = compileRaw version source
        suggestRaw compilation tree

    let gate (version: LanguageVersion) =
        at version
        |> List.find (fun s -> s.Code = "CR0047")
        |> fun s -> s.Fixes.Head.Edits.Head.Replacement.Trim()

    Assert.Equal("private readonly object _gate = new object();", gate LanguageVersion.CSharp8)

    Assert.Equal("private readonly object _gate = new();", gate LanguageVersion.CSharp12)
    Assert.Equal("private readonly Lock _gate = new();", gate LanguageVersion.CSharp13)
    // an `or` pattern is C# 9's
    Assert.Empty(at LanguageVersion.CSharp8 |> List.filter (fun s -> s.Code = "CR0009"))

    Assert.Equal(
        1,
        at LanguageVersion.CSharp9
        |> List.filter (fun s -> s.Code = "CR0009")
        |> List.length
    )
