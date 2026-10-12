/// Family J and the edit sets: each shape here is one a rewrite gets wrong,
/// or applies by half, without its guard.
module CSharp.Refactor.Tests.DefectReviewTests

open Xunit
open CSharp.Refactor.Roslyn
open CSharp.Refactor.Tests.Harness
open Microsoft.CodeAnalysis.CSharp

[<Fact>]
let ``CR0160 copies an outer loop's variable read from an inner loop, and leaves a cell another closure writes`` () =
    let source =
        csharp
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
    assertFired 2 source fired
    let fixedSource = fixAll "CR0160" source

    Assert.Contains(
        csharp
            """
            var i1 = i;
                            var j1 = j;
                            actions.Add(() => Console.WriteLine(i1 + j1));
            """,
        fixedSource
    )

    Assert.Contains("actions.Add(() => Console.WriteLine(n));", fixedSource)

[<Fact>]
let ``CR0166 keeps a numeric parse under a FormatException catch, where an overflow used to propagate`` () =
    let source =
        csharp
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
    assertFired 1 source fixes
    let fixedSource = fixAll "CR0166" source
    Assert.Contains("try { v = long.Parse(s); } catch (FormatException) { v = 0; }", fixedSource)
    Assert.Contains("if (!bool.TryParse(s, out v)) { v = false; }", fixedSource)
    Assert.Contains("try { v = int.Parse(s!); } catch (OverflowException) { v = 0; }", fixedSource)

[<Fact>]
let ``CR0171 leaves a CoreLib dictionary's indexer set and Remove under its own foreach`` () =
    let source =
        csharp
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
    Assert.Equal<string list>([ """map.Add(kv.Key + "x", 1)"""; """map.Add(k + "y", 2)""" ], firedText source fired)

    Assert.Contains("foreach (var k in map.Keys.ToList())", fixAll "CR0171" source)

[<Fact>]
let ``CR0171 is a note where a repository ToList would take the snapshot call`` () =
    let source =
        csharp
            """
            using System.Collections.Generic;
            using System.Linq;
            static class MyExt { public static List<T> ToList<T>(this List<T> xs) => xs; }
            class C
            {
                void A(List<int> xs) { foreach (var x in xs) { xs.Add(0); if (xs.Count > 5) break; } }
                void B(HashSet<int> set) { foreach (var x in set) { set.Add(x + 1); if (set.Count > 5) break; } }
            }
            """

    let fired = fires 2 "CR0171" source
    Assert.Equal(1, fired |> List.filter (fun s -> not s.Fixes.IsEmpty) |> List.length)
    Assert.Contains("foreach (var x in set.ToList())", fixAll "CR0171" source)

/// A compilation against an SDK reference pack (`packs/<pack>/<version>/ref/<tfm>`),
/// as MSBuild builds a project: the core library is the pack's, not the
/// running runtime's System.Private.CoreLib the harness uses.
let private compileAgainstPack (pack: string) (version: string -> bool) (source: string) =
    let dotnetRoot =
        System.IO.Path.GetFullPath(
            System.IO.Path.Combine(System.IO.Path.GetDirectoryName typeof<obj>.Assembly.Location, "..", "..", "..")
        )

    let versionDir =
        System.IO.Directory.GetDirectories(System.IO.Path.Combine(dotnetRoot, "packs", pack))
        |> Array.filter (System.IO.Path.GetFileName >> version)
        |> Array.max

    let refDir =
        System.IO.Directory.GetDirectories(System.IO.Path.Combine(versionDir, "ref"))
        |> Array.exactlyOne

    let references =
        System.IO.Directory.GetFiles(refDir, "*.dll")
        |> Array.map (fun f ->
            Microsoft.CodeAnalysis.MetadataReference.CreateFromFile f :> Microsoft.CodeAnalysis.MetadataReference)

    let tree =
        CSharpSyntaxTree.ParseText(normalize source, parseOptions, path = "Sample.cs")

    let compilation =
        CSharpCompilation.Create(
            "Test",
            [ tree ],
            references,
            CSharpCompilationOptions(Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary)
        )

    Assert.Empty(errorsOf compilation)
    let model = compilation.GetSemanticModel(tree, false)
    Rules.all tree model (Context.forTree None compilation tree false)

[<Fact>]
let ``CR0171 leaves a dictionary's indexer set and Remove on a .NET reference pack, and reports them on netstandard``
    ()
    =
    let source =
        csharp
            """
            using System.Collections.Generic;
            class C
            {
                void A(Dictionary<string, int> map, HashSet<int> set)
                {
                    foreach (var k in map.Keys)
                    {
                        map[k] = map[k] + 1;
                    }
                    foreach (var x in set)
                    {
                        if (x < 0) set.Remove(x);
                    }
                    foreach (var kv in map)
                    {
                        if (kv.Value > 9) map.Clear();
                    }
                    foreach (var k in map.Keys)
                    {
                        map.Add(k + "y", 2);
                    }
                }
            }
            """

    let fired suggestions =
        suggestions
        |> List.filter (fun (s: CSharp.Refactor.Suggestion) -> s.Code = "CR0171")
        |> firedText source

    let runtimeMajor = string System.Environment.Version.Major + "."

    Assert.Equal<string list>(
        [ """map.Add(k + "y", 2)""" ],
        fired (compileAgainstPack "Microsoft.NETCore.App.Ref" (fun v -> v.StartsWith runtimeMajor) source)
    )

    // netstandard may run on .NET Framework, whose dictionary throws on both
    Assert.Equal<string list>(
        [
            "map[k] = map[k] + 1"
            "set.Remove(x)"
            "map.Clear()"
            """map.Add(k + "y", 2)"""
        ],
        fired (compileAgainstPack "NETStandard.Library.Ref" (fun v -> v.StartsWith "2.1") source)
    )

[<Fact>]
let ``CR0171 reads the runtime generation off the core library: 3.x for Dictionary.Remove, .NET 5 for the rest`` () =
    // a core library of a chosen name and version, as a reference pack's
    // `System.Runtime` (or a runtime's CoreLib) presents itself
    let compilationOver (name: string) (version: string) =
        let core =
            CSharpCompilation.Create(
                name,
                [
                    CSharpSyntaxTree.ParseText(
                        $"""
                        [assembly: System.Reflection.AssemblyVersion("{version}")]
                        namespace System {{ public class Object {{ }} public abstract class ValueType {{ }} public struct Void {{ }} public sealed class String {{ }} public abstract class Attribute {{ }} }}
                        namespace System.Reflection {{ public sealed class AssemblyVersionAttribute : System.Attribute {{ public AssemblyVersionAttribute(string version) {{ }} }} }}
                        """
                    )
                ],
                [],
                CSharpCompilationOptions(Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary)
            )

        CSharpCompilation.Create("App", [], [ core.ToMetadataReference() ])

    let atLeast (major, minor, build) compilation =
        CSharp.Refactor.EnumerationMutation.coreRuntimeAtLeast (System.Version(major, minor, build)) compilation

    let core31 = compilationOver "System.Runtime" "4.2.2.0"
    Assert.True(atLeast (4, 2, 2) core31)
    Assert.False(atLeast (5, 0, 0) core31)

    let core21 = compilationOver "System.Runtime" "4.2.1.0"
    Assert.False(atLeast (4, 2, 2) core21)

    let net5 = compilationOver "System.Runtime" "5.0.0.0"
    Assert.True(atLeast (5, 0, 0) net5)

    // CoreLib says 4.0.0 through 3.x: it vouches for neither
    Assert.False(atLeast (4, 2, 2) (compilationOver "System.Private.CoreLib" "4.0.0.0"))
    Assert.True(atLeast (5, 0, 0) (compilationOver "System.Private.CoreLib" "6.0.0.0"))
    Assert.False(atLeast (4, 2, 2) (compilationOver "netstandard" "2.1.0.0"))
    Assert.False(atLeast (4, 2, 2) (compilationOver "mscorlib" "4.0.0.0"))

[<Fact>]
let ``CR0015 gives nested loops over the same collection different element names`` () =
    let source =
        csharp
            """
            class C
            {
                int A(int[] values, int[] offsets)
                {
                    var sum = 0;
                    for (var i = 0; i < offsets.Length; i++)
                    {
                        for (var j = 0; j < offsets.Length; j++)
                        {
                            sum += values[offsets[i] + offsets[j] + 1];
                        }
                    }
                    return sum;
                }
            }
            """

    let fixedSource = fixAll "CR0015" source
    Assert.Contains("foreach (var offset in offsets)", fixedSource)
    Assert.Contains("foreach (var item in offsets)", fixedSource)
    Assert.Contains("sum += values[offset + item + 1];", fixedSource)

[<Fact>]
let ``Symbols declared in a referenced project's source are followed no further, and no rule throws`` () =
    let library =
        CSharpCompilation.Create(
            "Library",
            [
                CSharpSyntaxTree.ParseText(
                    "using System.Collections.Generic; public static class Lib { public static readonly List<int> Items = new List<int> { 1, 2 }; } public struct Counter { int n; public void Bump() { n++; } }",
                    parseOptions
                )
            ],
            metadataReferences,
            CSharpCompilationOptions(Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary)
        )

    let tree =
        CSharpSyntaxTree.ParseText(
            normalize (
                csharp
                    """
                    class C
                    {
                        bool A()
                        {
                            bool found = false;
                            foreach (var x in Lib.Items)
                                if (x > 1) found = true;
                            return found;
                        }
                        // CR0161 reads a struct method's body to see whether it mutates
                        readonly Counter counter;
                        void B() { counter.Bump(); }
                    }
                    """
            ),
            parseOptions,
            path = "Sample.cs"
        )

    // a project reference: the library's symbols carry its syntax, from a tree
    // this compilation does not hold
    let compilation =
        CSharpCompilation.Create(
            "App",
            [ tree ],
            library.ToMetadataReference() :: metadataReferences,
            CSharpCompilationOptions(Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary)
        )

    Assert.Empty(errorsOf compilation)
    let model = compilation.GetSemanticModel(tree, false)

    let _, failures =
        Rules.allWithFailures tree model (Context.forTree None compilation tree false)

    Assert.Equal<string list>([], failures |> List.map (fun (name, ex) -> $"{name}: {ex.Message}"))

[<Fact>]
let ``CR0015 spells the element type where the collection enumerates something else than its indexer returns`` () =
    let source =
        csharp
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
        csharp
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
        csharp
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
    Assert.Contains("""(int, string) t = (1, "a"); return Take(t);""", fixedSource)
    Assert.Contains("""var t = Tuple.Create(1, 2); return string.Format("{0}", t);""", fixedSource)
    Assert.Contains("var t = Tuple.Create(1, true); return Wrap(t);", fixedSource)

    // a callee that does not bind (a reference the compilation lacks) hides
    // what it takes: the rule stands down rather than trust an empty check
    let broken =
        csharp
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
        csharp
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
        csharp
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
    Assert.Contains("""db.ExecuteSqlCommand("DELETE FROM " + table + " WHERE 1 = 1")""", fixedSource)
    Assert.Contains("""db.ExecuteSqlCommand(string.Format("DELETE FROM {0} WHERE 1 = 1", table))""", fixedSource)
    Assert.Contains("""db.Plain($"DELETE FROM {table} WHERE 1 = 1")""", fixedSource)

// ---- offers, hand-offs between rules, language-version gates ----

[<Fact>]
let ``CR0084 offers readonly only where every write is a constructor's`` () =
    let source =
        csharp
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
        csharp
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
        csharp
            """
            using System;
            using System.Threading;
            class C
            {
                int count;
                void A() { lock (this) { count++; } }
                string B(int k) => k switch
                {
                    1 => "a",
                    2 => "a",
                    _ => "b",
                };
            }

            """

    let at (version: LanguageVersion) =
        let compilation, tree = compileRaw version source
        suggestRawClean compilation tree

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
