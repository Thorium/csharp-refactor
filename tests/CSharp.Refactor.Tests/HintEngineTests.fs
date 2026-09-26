module CSharp.Refactor.Tests.HintEngineTests

open Xunit
open CSharp.Refactor.Tests.Harness

[<Literal>]
let private code = "CR0011"

[<Fact>]
let ``negations, bool literals, CompareTo and LINQ shapes rewrite, bracketed where precedence needs it`` () =
    let source =
        """
using System;
using System.Collections.Generic;
using System.Linq;
class C
{
    bool A(int a, int b) => !(a == b);
    bool B(int a, int b) => !(a < b) && !(b >= 3);
    bool D(bool x) => x == true;
    bool E(bool x, bool y) => (x == false) == y;
    bool F(int a, int b) => a.CompareTo(b) < 0;
    bool G(List<int> xs) => xs.Where(v => v > 1).Count() > 0;
    int H(List<int> xs) => xs.Select(v => v * 2).Sum();
    int I(List<int> xs) => xs.Where(v => v > 1).First();
    bool J(string a, string b) => string.Compare(a, b, StringComparison.Ordinal) == 0;
    bool K(object o) => !(o is string);
    bool L(int a, int b, int c) => !(a + b == c);
    int N(int a, int b) => (!(a == b) ? 1 : 0);
    int O(List<int> xs) => xs
        .Select(v => v + 1)
        .Where(v => v > 1)
        .First();
}
"""

    let fired = suggestCode code source
    Assert.Equal(14, fired.Length)
    let fixedSource = fixAll code source
    Assert.Contains("bool A(int a, int b) => a != b;", fixedSource)
    Assert.Contains("bool B(int a, int b) => a >= b && b < 3;", fixedSource)
    Assert.Contains("bool D(bool x) => x;", fixedSource)
    Assert.Contains("bool E(bool x, bool y) => (!x) == y;", fixedSource)
    Assert.Contains("bool F(int a, int b) => a < b;", fixedSource)
    Assert.Contains("bool G(List<int> xs) => xs.Any(v => v > 1);", fixedSource)
    Assert.Contains("int H(List<int> xs) => xs.Sum(v => v * 2);", fixedSource)
    Assert.Contains("int I(List<int> xs) => xs.First(v => v > 1);", fixedSource)
    Assert.Contains("bool J(string a, string b) => string.Equals(a, b, StringComparison.Ordinal);", fixedSource)
    Assert.Contains("bool K(object o) => o is not string;", fixedSource)
    Assert.Contains("bool L(int a, int b, int c) => a + b != c;", fixedSource)
    Assert.Contains("int N(int a, int b) => (a != b ? 1 : 0);", fixedSource)
    // a chain laid out one call per line keeps its line breaks
    Assert.Contains(
        "int O(List<int> xs) => xs\n        .Select(v => v + 1)\n        .First(v => v > 1);",
        normalize fixedSource
    )

[<Fact>]
let ``floating orderings, nullable bools, user-defined operators, own extension methods, strings under CompareTo, attributes and expression trees stand down``
    ()
    =
    let source =
        """
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
struct Money { public static bool operator ==(Money a, Money b) => true; public static bool operator !=(Money a, Money b) => false; public override bool Equals(object o) => true; public override int GetHashCode() => 0; }
static class Own { public static bool Any(this List<int> xs) => false; public static int Count(this IEnumerable<int> xs, int dummy = 0) => 0; }
class MyAttr : Attribute { public MyAttr(bool b) { } }
class C
{
    bool A(double a, double b) => !(a < b);
    bool A2(decimal? a) => !(a > 0);
    bool B(bool? x) => x == true;
    bool D(Money a, Money b) => !(a == b);
    bool E(List<int> xs) => xs.Count() > 0;
    bool F(string a, string b) => a.CompareTo(b) == 0;
    [MyAttr(!(1 == 2))]
    bool G() => true;
    Expression<Func<int, bool>> H(int b) => a => !(a == b);
    bool I(dynamic d) => !(d == 1);
}
"""

    Assert.Equal<string list>([], firedText source (suggestCode code source))

[<Fact>]
let ``Count() > 0 becomes Any() only where the receiver has no size of its own`` () =
    // on a sized receiver CA1829 asks for `.Length > 0`, and the `Any()`
    // would be CA1860's error under TreatWarningsAsErrors
    let source =
        """
using System.Collections.Generic;
using System.Linq;
class C
{
    bool A(int[] xs) => xs.Count() > 0;
    bool B(List<int> xs) => xs.Count() != 0;
    bool D(IReadOnlyCollection<int> xs) => xs.Count() == 0;
    bool E(string s) => s.Count() > 0;
    bool F(IEnumerable<int> xs) => xs.Count() > 0;
    bool G(List<int> xs) => xs.Distinct().Count() > 0;
}
"""

    let fixedSource = fixAll code source
    Assert.Contains("bool A(int[] xs) => xs.Count() > 0;", fixedSource)
    Assert.Contains("bool B(List<int> xs) => xs.Count() != 0;", fixedSource)
    Assert.Contains("bool D(IReadOnlyCollection<int> xs) => xs.Count() == 0;", fixedSource)
    Assert.Contains("bool E(string s) => s.Count() > 0;", fixedSource)
    Assert.Contains("bool F(IEnumerable<int> xs) => xs.Any();", fixedSource)
    Assert.Contains("bool G(List<int> xs) => xs.Distinct().Any();", fixedSource)

[<Fact>]
let ``Count to Any needs a total predicate and an eager source, else it is a note`` () =
    let source =
        """
using System;
using System.Collections.Generic;
using System.Linq;
class Person { public int Age { get; set; } public int Id; public string Name { get; set; } = ""; }
record Item(int Id);
class C
{
    static IEnumerable<int> Gen() { yield return 1; throw new InvalidOperationException(); }
    bool A(List<int> list) => list.Count(x => x > 0) > 0;
    bool B(Item[] arr, int id) => arr.Where(x => x.Id == id).Count() > 0;
    bool D(Person[] arr, int id) => arr.Where(x => x.Id == id).Count() > 0;
    bool E(List<Person> people) => people.Count(p => p.Age > 18) > 0;
    bool F(List<string> names) => names.Count(n => n.Length > 3 && n != "x") != 0;
    bool G(List<Person> people) => people.Select(p => p.Age).Count() > 0;
    bool H(List<int> xs) => xs.Count(x => x % 2 == 0) == 0;
    bool E1(int?[] xs) => xs.Count(x => x.Value > 0) > 0;
    bool E2(int?[] xs) => xs.Where(x => x.Value > 0).Count() > 0;
    bool E3() => Gen().Count() > 0;
    bool E4(List<int> xs) { int seen = 0; return xs.Count(x => { seen++; return x > 0; }) > 0; }
    bool E5(string?[] xs) => xs.Count(x => x!.Length > 0) > 0;
    bool E6(IEnumerable<int> xs) => xs.Count(x => x > 0) > 0;
    bool E7(List<int> xs) => xs.Count(x => 10 / x > 1) > 0;
}
"""

    let fired = suggestCode code source
    Assert.Equal(14, fired.Length)
    let fixes = fired |> List.filter (fun s -> not s.Fixes.IsEmpty)
    Assert.Equal(9, fixes.Length)
    let fixedSource = fixAll code source
    Assert.Contains("bool A(List<int> list) => list.Any(x => x > 0);", fixedSource)
    Assert.Contains("bool B(Item[] arr, int id) => arr.Any(x => x.Id == id);", fixedSource)
    Assert.Contains("bool D(Person[] arr, int id) => arr.Any(x => x.Id == id);", fixedSource)
    Assert.Contains("bool E(List<Person> people) => people.Any(p => p.Age > 18);", fixedSource)
    Assert.Contains("""bool F(List<string> names) => names.Any(n => n.Length > 3 && n != "x");""", fixedSource)
    Assert.Contains("bool G(List<Person> people) => people.Select(p => p.Age).Any();", fixedSource)
    Assert.Contains("bool H(List<int> xs) => !xs.Any(x => x % 2 == 0);", fixedSource)
    Assert.Contains("bool E1(int?[] xs) => xs.Count(x => x.Value > 0) > 0;", fixedSource)
    Assert.Contains("bool E2(int?[] xs) => xs.Where(x => x.Value > 0).Count() > 0;", fixedSource)
    Assert.Contains("bool E3() => Gen().Count() > 0;", fixedSource)
    Assert.Contains("return xs.Count(x => { seen++; return x > 0; }) > 0;", fixedSource)
    // a null element after the first match threw under Count: the accepted residual
    Assert.Contains("bool E5(string?[] xs) => xs.Any(x => x!.Length > 0);", fixedSource)
    Assert.Contains("bool E6(IEnumerable<int> xs) => xs.Any(x => x > 0);", fixedSource)
    Assert.Contains("bool E7(List<int> xs) => xs.Count(x => 10 / x > 1) > 0;", fixedSource)

[<Fact>]
let ``Count to Any takes string comparisons, Contains, a comparer and an interface Count; not a visible iterator, Cast, a user conversion or ReadLines``
    ()
    =
    let source =
        """
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
public sealed class Item { public List<string> Tags { get; } = new(); public Money Price { get; set; } }
public readonly struct Money { public readonly int V; public static implicit operator int(Money m) => m.V; }
public sealed class Bag : IEnumerable<int>
{
    public IEnumerator<int> GetEnumerator() { yield return 1; }
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
public sealed class Holder { public ICollection<int> C { get; set; } = new List<int>(); }
class C
{
    static IEnumerable<int> Gen() { yield return 1; }
    bool C06(List<string> names) => names.Count(n => n.StartsWith("A")) > 0;
    bool C09(List<Item> list) => list.Count(x => x.Tags.Contains("a")) > 0;
    bool C18(List<string> names) => names.Count(n => string.Equals(n, "ann", StringComparison.OrdinalIgnoreCase)) > 0;
    bool K01() => Gen().AsQueryable().Count() > 0;
    bool K03() { var bag = new Bag(); return bag.Count(x => x > 0) > 0; }
    bool K04(object[] objs) => objs.Cast<string>().Count() > 0;
    bool K06(int[] xs, IEqualityComparer<int> cmp) => xs.Distinct(cmp).Count() > 0;
    bool K07(List<Item> items) => items.Count(i => i.Price > 0) > 0;
    bool K11(List<Holder> hs) => hs.Count(h => h.C.Count > 0) > 0;
    bool K12(string path) => File.ReadLines(path).Count() > 0;
    bool K13(StringComparison how, List<string> names) => names.Count(n => n.Equals("a", how)) > 0;
}
"""

    let fired = suggestCode code source
    Assert.Equal(11, fired.Length)
    let fixes = fired |> List.filter (fun s -> not s.Fixes.IsEmpty)
    // K01 (a visible iterator behind AsQueryable), K03 (a user collection whose
    // GetEnumerator yields), K04 (Cast), K07 (a user conversion) and K12
    // (File.ReadLines) are positively detected; the rest take the fix
    Assert.Equal(6, fixes.Length)
    let fixedSource = fixAll code source
    Assert.Contains("""names.Any(n => n.StartsWith("A"));""", fixedSource)
    Assert.Contains("""list.Any(x => x.Tags.Contains("a"));""", fixedSource)
    Assert.Contains("""names.Any(n => string.Equals(n, "ann", StringComparison.OrdinalIgnoreCase));""", fixedSource)
    Assert.Contains("xs.Distinct(cmp).Any();", fixedSource)
    Assert.Contains("hs.Any(h => h.C.Count > 0);", fixedSource)
    Assert.Contains("""names.Any(n => n.Equals("a", how));""", fixedSource)
    Assert.Contains("bool K01() => Gen().AsQueryable().Count() > 0;", fixedSource)
    Assert.Contains("bool K04(object[] objs) => objs.Cast<string>().Count() > 0;", fixedSource)
    Assert.Contains("bool K12(string path) => File.ReadLines(path).Count() > 0;", fixedSource)

[<Fact>]
let ``Count to Any takes clock reads, method groups, case folds, conditional access, array Contains and a stored queryable``
    ()
    =
    let source =
        """
using System;
using System.Collections.Generic;
using System.Linq;
public sealed class P { public DateTime When { get; set; } public string[] Tags { get; set; } = { "a" }; public string? Name { get; set; } }
public sealed class Db { public IQueryable<P> Users { get; set; } = new List<P>().AsQueryable(); }
class C
{
    bool T04(List<P> xs) => xs.Count(p => p.When < DateTime.UtcNow) > 0;
    bool T05(string s) => s.Count(char.IsDigit) > 0;
    bool T06(List<string> names) => names.Count(n => n.ToLower() == "ann") > 0;
    bool T07(Db db) => db.Users.Count(u => u.When > DateTime.MinValue) > 0;
    bool T11(List<P> xs) => xs.Count(p => p.Tags.Contains("a")) > 0;
    bool Y01(List<P> xs) => xs.Count(p => p.Name?.Length > 0) > 0;
}
"""

    let fired = suggestCode code source
    Assert.Equal(6, fired.Length)
    Assert.All(fired, fun s -> Assert.NotEmpty s.Fixes)
    let fixedSource = fixAll code source
    Assert.Contains("xs.Any(p => p.When < DateTime.UtcNow);", fixedSource)
    Assert.Contains("s.Any(char.IsDigit);", fixedSource)
    Assert.Contains("""names.Any(n => n.ToLower() == "ann");""", fixedSource)
    Assert.Contains("db.Users.Any(u => u.When > DateTime.MinValue);", fixedSource)
    Assert.Contains("""xs.Any(p => p.Tags.Contains("a"));""", fixedSource)
    Assert.Contains("xs.Any(p => p.Name?.Length > 0);", fixedSource)

[<Fact>]
let ``Count to Any takes a variable string argument, a user element's Equals or hash and a user key: the accepted residual``
    ()
    =
    let source =
        """
using System;
using System.Collections.Generic;
using System.Linq;
public sealed class K { public int V; }
public sealed class P { public string Name { get; set; } = ""; public string? Sub { get; set; } public K Key { get; set; } = new K(); }
class C
{
    bool T01(List<P> xs) => xs.Count(p => p.Name.Contains(p.Sub!)) > 0;
    bool T03(P[] xs) => xs.Where(p => p.Name.IndexOf(p.Sub!) >= 0).Count() > 0;
    bool T18(List<K> ks, K k) => ks.Count(x => new List<K> { k }.Contains(x)) > 0;
    bool T18b(List<List<K>> xs, K k) => xs.Count(l => l.Contains(k)) > 0;
    bool U01(List<K> xs) => xs.Distinct().Count() > 0;
    bool U02(List<P> xs) => xs.GroupBy(p => p.Key).Count() > 0;
    bool Kept(List<P> xs) => xs.GroupBy(p => p.Name).Count() > 0;
}
"""

    // nothing here is positively known to throw after the first match: a null
    // argument to Contains, a user Equals or GetHashCode are the residual
    let fired = suggestCode code source
    Assert.Equal(7, fired.Length)
    Assert.All(fired, fun s -> Assert.NotEmpty s.Fixes)
    let fixedSource = fixAll code source
    Assert.Contains("bool T01(List<P> xs) => xs.Any(p => p.Name.Contains(p.Sub!));", fixedSource)
    Assert.Contains("bool U01(List<K> xs) => xs.Distinct().Any();", fixedSource)
    Assert.Contains("bool Kept(List<P> xs) => xs.GroupBy(p => p.Name).Any();", fixedSource)

/// Every rule over the source, with the rules that threw: a proof that has
/// a throwing path on odd syntax silences a whole file's hints.
let private allWithFailures (source: string) =
    let compilation, tree = compile source
    let model = compilation.GetSemanticModel(tree, false)

    CSharp.Refactor.Roslyn.Rules.allWithFailures
        tree
        model
        (CSharp.Refactor.Roslyn.Context.forTree None compilation tree false)

[<Fact>]
let ``a char divisor, dynamic, default, sizeof, checked, tuple and with operands do not silence the file's hints`` () =
    let source =
        """
using System;
using System.Collections.Generic;
using System.Linq;
record R(int A);
class C
{
    bool CharDiv(List<int> xs) => xs.Count(x => x % 'a' == 0) > 0;
    bool CharDiv2(List<int> xs) => xs.Count(x => x / 'A' > 1) > 0;
    bool Dyn(List<dynamic> xs) => xs.Count(x => x > 1) > 0;
    bool Def(List<int> xs) => xs.Count(x => x == default) > 0;
    bool Size(List<int> xs) => xs.Count(x => x > sizeof(long)) > 0;
    bool Name(List<string> xs) => xs.Count(x => x == nameof(xs)) > 0;
    bool Chk(List<int> xs) => xs.Count(x => checked(x + 1) > 0) > 0;
    bool Tup(List<(int, int)> xs) => xs.Count(x => x == (1, 2)) > 0;
    bool With(List<R> xs) => xs.Count(x => (x with { A = 1 }).A > 0) > 0;
    bool Alloc(List<int> xs) { Span<int> s = stackalloc int[1]; int n = s.Length; return xs.Count(x => x > n) > 0; }
    bool Plain(List<int> xs) => xs.Count(x => x > 0) > 0;
    bool Flag(int[] xs) { bool found = false; foreach (var x in xs) if (x % 'a' == 0) found = true; return found; }
}
"""

    let suggestions, failures = allWithFailures source
    Assert.Empty failures
    let hints = suggestions |> List.filter (fun s -> s.Code = code)
    // every Count shape is at least a note; the divisors and the plain ones take the fix
    Assert.Equal(11, hints.Length)
    let fixedSource = fixAll code source
    Assert.Contains("xs.Any(x => x % 'a' == 0);", fixedSource)
    Assert.Contains("xs.Any(x => x / 'A' > 1);", fixedSource)
    Assert.Contains("xs.Any(x => x == default);", fixedSource)
    Assert.Contains("xs.Any(x => x > sizeof(long));", fixedSource)
    Assert.Contains("xs.Any(x => x == nameof(xs));", fixedSource)
    Assert.Contains("bool Plain(List<int> xs) => xs.Any(x => x > 0);", fixedSource)
    Assert.Contains("bool found = xs.Any(x => x % 'a' == 0); return found;", fixAll "CR0022" source)

[<Fact>]
let ``error types in a predicate do not silence the file's hints`` () =
    let source =
        "using System.Collections.Generic;\nusing System.Linq;\nclass C\n{\n    bool Broken(List<Missing> xs) => xs.Count(x => x.Value > 0) > 0;\n    bool Plain(List<int> xs) => xs.Count(x => x > 0) > 0;\n}\n"

    let compilation, tree =
        compileRaw Microsoft.CodeAnalysis.CSharp.LanguageVersion.Latest source

    let model = compilation.GetSemanticModel(tree, false)

    let suggestions, failures =
        CSharp.Refactor.Roslyn.Rules.allWithFailures
            tree
            model
            (CSharp.Refactor.Roslyn.Context.forTree None compilation tree false)

    Assert.Empty failures
    let hints = suggestions |> List.filter (fun s -> s.Code = code)
    Assert.Equal(2, hints.Length)
    // an unresolved `.Value` is nothing known to throw: both keep their fix
    Assert.All(hints, fun s -> Assert.NotEmpty s.Fixes)

[<Fact>]
let ``Count to Any takes CountBy with or without a comparer`` () =
    let source =
        """
using System.Collections.Generic;
using System.Linq;
class C
{
    bool A(List<string> xs) => xs.CountBy(x => x.Length).Count() > 0;
    bool B(List<string> xs, IEqualityComparer<int> cmp) => xs.CountBy(x => x.Length, cmp).Count() > 0;
}
"""

    let fired = suggestCode code source
    Assert.Equal(2, fired.Length)
    Assert.All(fired, fun s -> Assert.NotEmpty s.Fixes)
    let fixedSource = fixAll code source
    Assert.Contains("bool A(List<string> xs) => xs.CountBy(x => x.Length).Any();", fixedSource)
    Assert.Contains("xs.CountBy(x => x.Length, cmp).Any();", fixedSource)

[<Fact>]
let ``Count to Any keeps the fix in a nullable-disabled file and on stored sequences`` () =
    let source =
        """
#nullable disable
using System.Collections.Generic;
using System.Linq;
class Person { public int Age { get; set; } }
class C
{
    IEnumerable<Person> people = new List<Person>();
    IEnumerable<int> Numbers { get; } = new List<int>();
    static List<int> Load() => new List<int>();
    bool A() => people.Count(p => p.Age > 18) > 0;
    bool B(List<string> names) => names.Count(s => s.Length > 3) > 0;
    bool D() => this.Numbers.Count() > 0;
    bool E() => Load().Count(x => x > 0) > 0;
}
"""

    let fired = suggestCode code source
    Assert.Equal(4, fired.Length)
    Assert.All(fired, fun s -> Assert.NotEmpty s.Fixes)
    let fixedSource = fixAll code source
    Assert.Contains("bool A() => people.Any(p => p.Age > 18);", fixedSource)
    Assert.Contains("bool B(List<string> names) => names.Any(s => s.Length > 3);", fixedSource)
    Assert.Contains("bool D() => this.Numbers.Any();", fixedSource)
    Assert.Contains("bool E() => Load().Any(x => x > 0);", fixedSource)

[<Fact>]
let ``only the outermost of nested matches fires and effectful bindings are not duplicated`` () =
    let source =
        """
using System;
class C
{
    int calls;
    int Next() => ++calls;
    bool A(int a) => !(!(a == 1) == false);
    bool B() => !(Next() == 1);
}
"""

    let fired = suggestCode code source
    // A: one suggestion for the whole expression, not one per nested match
    // (the next pass takes the rest); B: no metavariable is dropped or
    // repeated, so the call is fine
    Assert.Equal(2, fired.Length)
    let fixedSource = fixAll code source
    Assert.Contains("bool A(int a) => !(a == 1) != false;", fixedSource)
    Assert.Contains("bool B() => Next() != 1;", fixedSource)

[<Fact>]
let ``a custom hints file adds rules without BCL checks`` () =
    let dir =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "csref-hints-" + string (System.Guid.NewGuid()))

    System.IO.Directory.CreateDirectory dir |> ignore
    let hints = System.IO.Path.Combine(dir, "hints.txt")
    System.IO.File.WriteAllText(hints, "# own helpers\nOwn.IsNull(x) ===> x is null\nOwn.Twice(a) ===> a * 2\n")

    let source =
        """
static class Own { public static bool IsNull(object o) => o == null; public static int Twice(int a) => a * 2; }
class C
{
    bool A(object o) => Own.IsNull(o);
    int B(int a) => Own.Twice(a + 1);
}
"""

    let options =
        Some(
            FakeOptions(dict [ "csharp_refactor.hints", hints ])
            :> Microsoft.CodeAnalysis.Diagnostics.AnalyzerConfigOptions
        )

    let fired = suggestCodeWith options code source
    Assert.Equal(2, fired.Length)
    let fixedSource = fixAllWith options code source
    Assert.Contains("bool A(object o) => o is null;", fixedSource)
    Assert.Contains("int B(int a) => (a + 1) * 2;", fixedSource)
