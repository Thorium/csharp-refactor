/// The conditions any rule may meet in a real file, tried on the rules a
/// wrong answer would hurt: a name shadowing what a fix spells, a member
/// reached by reflection or `nameof`, a lambda that is an expression tree,
/// a `dynamic` operand, `unsafe` code, a type split across partial files,
/// an interface the rewritten member implements, a generated file, a
/// framework that lacks the type a fix would introduce. The F# suite
/// tries its rules against the same list; each case here is one a C#
/// rule has to answer.
module CSharp.Refactor.Tests.CrossCuttingTests

open System
open Xunit
open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open CSharp.Refactor
open CSharp.Refactor.Roslyn
open CSharp.Refactor.Tests.Harness

// ---- shadowing ----

[<Fact>]
let ``a fix never spells a name a nested type or local shadows`` () =
    // `Guid.Empty` would bind to the nested Guid; `Regex` to the nested class
    let source =
        """
using System;
class C
{
    class Guid { public static int Empty = 1; }
    System.Guid A() => new System.Guid();
    class Regex { }
    bool B(string s) => System.Text.RegularExpressions.Regex.IsMatch(s, "a.b");
}
"""

    let fixedSource = fixAllAllowing [ "CS8795" ] None "CR0090" source
    Assert.Contains("System.Guid.Empty", fixedSource)
    // the regex hoist must spell the type it can reach, or stand down
    let hoisted = fixAllAllowing [ "CS8795" ] None "CR0109" source
    Assert.DoesNotContain("private static readonly Regex ", hoisted)

[<Fact>]
let ``a local named like the replacement's helper keeps the rewrite honest`` () =
    // CR0104 spells `string.IsNullOrEmpty`; a local `string` cannot exist, but a
    // method named IsNullOrEmpty on the type can: the fix must qualify or hold
    let source =
        """
class C
{
    static bool IsNullOrEmpty(string s) => false;
    bool A(string x) => x == null || x == "";
}
"""

    let fixedSource = fixAll "CR0104" source
    Assert.Contains("string.IsNullOrEmpty(x)", fixedSource)

// ---- reflection and nameof ----

[<Fact>]
let ``a backing field reached by reflection or nameof keeps the field keyword away`` () =
    let source =
        """
using System.Reflection;
class C
{
    private int _count = 3;
    public int Count { get => _count; set => _count = value < 0 ? 0 : value; }
    FieldInfo F() => typeof(C).GetField("_count", BindingFlags.NonPublic | BindingFlags.Instance);
}
class D
{
    private int _count = 3;
    public int Count { get => _count; set => _count = value < 0 ? 0 : value; }
    string N() => nameof(_count);
}
"""

    Assert.Empty(suggestCode "CR0153" source)

[<Fact>]
let ``a property set through reflection still takes init, which reflection sets like any setter; a nameof holds it``
    ()
    =
    let source =
        """
class Key
{
    public int Id { get; set; }
    public Key(int id) { Id = id; }
}
class Binder
{
    void Fill(Key k) => typeof(Key).GetProperty("Id").SetValue(k, 2);
}
class Named
{
    public int Id { get; set; }
    public Named(int id) { Id = id; }
    string N() => nameof(Id);
}
"""

    // one suggestion, on Key's setter; Named's holds
    Assert.Equal(1, (suggestCode "CR0083" source).Length)
    Assert.Contains("public int Id { get; init; }\n    public Key(int id)", fixAll "CR0083" source)

[<Fact>]
let ``an enum compared by text whose names are read by reflection still rewrites, since values are the contract`` () =
    let source =
        """
using System;
enum Status { Active = 1, Closed = 2 }
class C
{
    bool A(Status s) => s.ToString() == "Active";
    string[] Names() => Enum.GetNames(typeof(Status));
}
"""

    Assert.Contains("s == Status.Active", fixAll "CR0087" source)

// ---- expression trees and dynamic ----

[<Fact>]
let ``inside an expression tree the boolean, string and LINQ rewrites hold`` () =
    let source =
        """
using System;
using System.Linq;
using System.Linq.Expressions;
class C
{
    Expression<Func<int, bool>> A() => x => !(x > 1);
    Expression<Func<string, string>> B() => s => "a " + s + " b";
    Expression<Func<int[], int>> D() => xs => xs.Select(x => x + 1).Select(y => y * 2).Count();
    Expression<Func<string, bool>> E() => s => s == null || s == "";
}
"""

    for code in [ "CR0011"; "CR0100"; "CR0029"; "CR0104" ] do
        Assert.Empty(suggestCode code source)

[<Fact>]
let ``a dynamic operand holds the rewrites that assume static binding`` () =
    let source =
        """
using System;
class C
{
    bool A(dynamic x) => !(x > 1);
    string B(dynamic x) => "a " + x + " b";
    bool D(dynamic x) => x == null || x == "";
}
"""

    Assert.Empty(suggestCode "CR0011" source)
    Assert.Empty(suggestCode "CR0104" source)
    // a dynamic hole is fine in an interpolation: the rewrite may go
    let _ = fixAll "CR0100" source
    ()

// ---- unsafe ----

[<Fact>]
let ``pointer loops and arithmetic are left to the author`` () =
    let unsafeRules (source: string) =
        let tree =
            CSharpSyntaxTree.ParseText(normalize source, parseOptions, path = "Sample.cs")

        let compilation =
            CSharpCompilation.Create(
                "Test",
                [ tree ],
                metadataReferences,
                CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe = true)
            )

        let errors = errorsOf compilation

        if not errors.IsEmpty then
            failwithf "test input does not compile:\n%s" (String.Join("\n", errors))

        let model = compilation.GetSemanticModel(tree, false)
        Rules.all tree model (Context.forTree None compilation tree false)

    let source =
        """
using System;
class C
{
    unsafe int A(int* xs, int n)
    {
        int total = 0;
        for (int i = 0; i < n; i++) total += xs[i];
        return total;
    }
    unsafe void B(byte* p, int n) { for (int i = 0; i < n; i++) Console.WriteLine(p[i]); }
}
"""

    let fired = unsafeRules source
    Assert.Empty(fired |> List.filter (fun s -> s.Code = "CR0015"))
    Assert.Empty(fired |> List.filter (fun s -> s.Code = "CR0021"))

// ---- partial types across files ----

let private partialPair (first: string) (second: string) =
    let tree1 =
        CSharpSyntaxTree.ParseText(normalize first, parseOptions, path = "First.cs")

    let tree2 =
        CSharpSyntaxTree.ParseText(normalize second, parseOptions, path = "Second.cs")

    let compilation =
        CSharpCompilation.Create(
            "Test",
            [ tree1; tree2 ],
            metadataReferences,
            CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        )

    let errors = errorsOf compilation

    if not errors.IsEmpty then
        failwithf "test input does not compile:\n%s" (String.Join("\n", errors))

    let model = compilation.GetSemanticModel(tree1, false)
    Rules.all tree1 model (Context.forTree None compilation tree1 false)

[<Fact>]
let ``a partial type's other file counts: a method there keeps the record away, a field use there keeps the backing field``
    ()
    =
    let first =
        """
partial class Money
{
    public int Amount { get; }
    public Money(int amount) { Amount = amount; }
}
partial class Counted
{
    private int _count = 3;
    public int Count { get => _count; set => _count = value; }
}
"""

    let second =
        """
partial class Money
{
    public int Apply(int x) => x * Amount;
}
partial class Counted
{
    void Reset() { _count = 0; }
}
"""

    let fired = partialPair first second
    Assert.Empty(fired |> List.filter (fun s -> s.Code = "CR0080"))
    Assert.Empty(fired |> List.filter (fun s -> s.Code = "CR0153"))

// ---- interfaces and overrides ----

[<Fact>]
let ``a member implementing an interface or overriding keeps its signature`` () =
    let source =
        """
using System.Threading.Tasks;
interface ILoader { int Fetch(int n); void Fire(); int Sum(params int[] xs); }
class C : ILoader
{
    Task<int> Load() => Task.FromResult(1);
    public int Fetch(int n) { var x = Load().Result; return x + n; }
    async Task<int> A() { var r = Fetch(1); return r; }
    public void Fire() { }
    public int Sum(params int[] xs) { var t = 0; foreach (var x in xs) t += x; return t; }
    int Use() => Sum(1, 2);
}
abstract class Base { public abstract int Count(params int[] xs); }
class Derived : Base
{
    public override int Count(params int[] xs) => xs.Length;
}
"""

    // CR0041 would rename and re-type Fetch; CR0151 would change the params type
    Assert.Empty(suggestCode "CR0041" source |> List.filter (fun s -> not s.Fixes.IsEmpty))
    Assert.Empty(suggestCode "CR0151" source)

// ---- generated files ----

[<Fact>]
let ``a file with an auto-generated header gets no suggestion from any rule`` () =
    let body =
        """
using System;
class C
{
    Guid A() => new Guid();
    string B() => $"x";
    bool D(int x) => !(x > 1);
}
"""

    Assert.NotEmpty(suggest body)

    Assert.Empty(
        suggest (
            "//------------------------------------------------------------------------------\n// <auto-generated>\n//     This code was generated by a tool.\n// </auto-generated>\n//------------------------------------------------------------------------------\n"
            + body
        )
    )

    Assert.Empty(suggest ("/* <autogenerated /> */\n" + body))

// ---- framework availability ----

/// The runtime's references without one assembly: a target framework that
/// lacks the type a fix would introduce.
let private withoutAssembly (name: string) (source: string) =
    let references =
        metadataReferences
        |> List.filter (fun r ->
            match r with
            | :? PortableExecutableReference as pe ->
                not (pe.FilePath.EndsWith(name, StringComparison.OrdinalIgnoreCase))
            | _ -> true)

    let tree =
        CSharpSyntaxTree.ParseText(normalize source, parseOptions, path = "Sample.cs")

    let compilation =
        CSharpCompilation.Create(
            "Test",
            [ tree ],
            references,
            CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        )

    let errors = errorsOf compilation

    if not errors.IsEmpty then
        failwithf "test input does not compile:\n%s" (String.Join("\n", errors))

    let model = compilation.GetSemanticModel(tree, false)
    Rules.all tree model (Context.forTree None compilation tree false)

[<Fact>]
let ``a fix that needs a type the framework lacks is not offered`` () =
    let frozen =
        """
using System;
using System.Collections.Generic;
class C
{
    private static readonly HashSet<string> Allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "a", "b" };
    static bool M(string s) => Allowed.Contains(s);
}
"""

    // FrozenSet lives in System.Collections.Immutable: without it, CR0150 has nothing to offer
    Assert.NotEmpty(suggestCode "CR0150" frozen)

    Assert.Empty(
        withoutAssembly "System.Collections.Immutable.dll" frozen
        |> List.filter (fun s -> s.Code = "CR0150")
    )
