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
}
"""

    let fixedSource = fixAll code source
    Assert.Contains("bool A(int[] xs) => xs.Count() > 0;", fixedSource)
    Assert.Contains("bool B(List<int> xs) => xs.Count() != 0;", fixedSource)
    Assert.Contains("bool D(IReadOnlyCollection<int> xs) => xs.Count() == 0;", fixedSource)
    Assert.Contains("bool E(string s) => s.Count() > 0;", fixedSource)
    Assert.Contains("bool F(IEnumerable<int> xs) => xs.Any();", fixedSource)

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
