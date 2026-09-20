module CSharp.Refactor.Tests.TypeTests

open Xunit
open CSharp.Refactor.Tests.Harness

// ---- CR0084 ----

[<Fact>]
let ``a public mutable static written from several sites or from itself is noted; a set-once seam is not`` () =
    let source =
        """
class C
{
    public static int Counter;
    public static string Name;
    public static int Seam;
    public static readonly int Fixed = 1;
    private static int Hidden;
    void A() { Counter++; Name = "a"; Seam = 1; Hidden = 2; }
    void B() { Name = "b"; Hidden = 3; }
}
"""

    Assert.Equal<string list>([ "Counter"; "Name" ], firedText source (suggestCode "CR0084" source))

// ---- CR0085 ----

[<Fact>]
let ``type tests by name or exact Type are noted, except inside a type-pattern guard`` () =
    let source =
        """
using System;
class C
{
    bool A(object x) => x.GetType().Name == "Customer";
    bool B(object x) => x.GetType() == typeof(string);
    bool D(object x) => x is string;
    int E(object x) => x switch { string s when s.GetType() == typeof(string) => 1, _ => 0 };
    bool F(object x) => x.GetType().FullName != "N.T";
}
"""

    Assert.Equal(3, (suggestCode "CR0085" source).Length)

// ---- CR0086 ----

[<Fact>]
let ``a virtual member called or read while constructing is noted; sealed types, lambdas and non-virtuals are not`` () =
    let source =
        """
using System;
class Base
{
    protected virtual void Init() { }
    protected virtual int Size => 1;
    protected void Plain() { }
    public Base() { Init(); }
}
class Derived : Base
{
    readonly int n = 0;
    readonly Func<int> f;
    public Derived() : base() { Plain(); n = Size; f = () => Size; }
}
sealed class Leaf : Base
{
    public Leaf() { Init(); }
}
abstract class Abstract
{
    protected abstract int Compute();
    readonly int cached;
    public Abstract() { cached = this.Compute(); }
}
"""

    Assert.Equal<string list>([ "Init()"; "Size"; "this.Compute()" ], firedText source (suggestCode "CR0086" source))

// ---- CR0087 ----

[<Fact>]
let ``an enum compared by its text compares the values; flags and unknown names stay`` () =
    let source =
        """
using System;
enum Status { Active, Closed }
[Flags] enum Bits { A = 1, B = 2 }
class C
{
    bool A(Status s) => s.ToString() == "Active";
    bool B(Status s) => "Closed" != s.ToString();
    bool D(Status s) => s.ToString() == "Missing";
    bool E(Bits b) => b.ToString() == "A";
    bool F(Status s) => s.ToString("G") == "Active";
}
"""

    Assert.Equal(2, (suggestCode "CR0087" source).Length)
    let fixedSource = fixAll "CR0087" source
    Assert.Contains("bool A(Status s) => s == Status.Active;", fixedSource)
    Assert.Contains("bool B(Status s) => s != Status.Closed;", fixedSource)
