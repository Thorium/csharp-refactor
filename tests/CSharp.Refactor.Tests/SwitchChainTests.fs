module CSharp.Refactor.Tests.SwitchChainTests

open Xunit
open CSharp.Refactor.Tests.Harness

// ---- CR0003: type-test chain → switch over type patterns ----

[<Fact>]
let ``a type-test chain with casts becomes a switch, the cast declaration the binder`` () =
    let source =
        """
using System;
abstract class Shape { }
class Circle : Shape { public double R; }
class Rect : Shape { public double W, H; }
class C
{
    double Area(Shape s)
    {
        if (s is Circle)
        {
            var c = (Circle)s;
            return Math.PI * c.R * c.R;
        }
        else if (s is Rect)
        {
            return ((Rect)s).W * (s as Rect).H;
        }
        else
        {
            throw new ArgumentException("unknown", nameof(s));
        }
    }
}
"""

    let fired = suggestCode "CR0003" source
    Assert.Equal(1, fired.Length)
    let fixedSource = fixAll "CR0003" source

    let expected =
        """        switch (s)
        {
            case Circle c:
                return Math.PI * c.R * c.R;
            case Rect rect:
                return rect.W * rect.H;
            default:
                throw new ArgumentException("unknown", nameof(s));
        }
"""

    Assert.Contains(normalize expected, fixedSource)

[<Fact>]
let ``a body that falls through gains break, a null arm and a bare type stay patterns`` () =
    let source =
        """
using System;
class C
{
    void Log(object o)
    {
        if (o is null)
        {
            Console.WriteLine("null");
        }
        else if (o is string)
        {
            Console.WriteLine("text");
        }
        else if (o is int n)
        {
            if (n > 0) Console.WriteLine(n);
        }
    }
}
"""

    let fixedSource = fixAll "CR0003" source

    let expected =
        """        switch (o)
        {
            case null:
                Console.WriteLine("null");
                break;
            case string:
                Console.WriteLine("text");
                break;
            case int n:
                if (n > 0) Console.WriteLine(n);
                break;
        }
"""

    Assert.Contains(normalize expected, fixedSource)

[<Fact>]
let ``locals shared between branches keep their braces`` () =
    let source =
        """
using System;
class A { public int V; }
class B { public int V; }
class C
{
    int Get(object o)
    {
        if (o is A)
        {
            var v = ((A)o).V;
            return v + 1;
        }
        else if (o is B)
        {
            var v = ((B)o).V;
            return v + 2;
        }
        return 0;
    }
}
"""

    let fixedSource = fixAll "CR0003" source

    let expected =
        """        switch (o)
        {
            case A a:
                {
                    var v = a.V;
                    return v + 1;
                }
            case B b:
                {
                    var v = b.V;
                    return v + 2;
                }
        }
        return 0;
"""

    Assert.Contains(normalize expected, fixedSource)

[<Fact>]
let ``a cross-cast, an assigned subject, a compound condition, a break in a loop, a comment on the else and a single test stand down``
    ()
    =
    let source =
        """
using System;
class A { public int V; }
class B : A { }
class C
{
    int Cross(object o)
    {
        if (o is B) { return ((A)o).V; }
        else if (o is A) { return ((A)o).V; }
        return 0;
    }
    int Assigned(object o)
    {
        if (o is A) { o = null; return 1; }
        else if (o is B) { return 2; }
        return 0;
    }
    int Compound(object o, bool f)
    {
        if (o is A && f) { return 1; }
        else if (o is B) { return 2; }
        return 0;
    }
    int Loop(object[] xs)
    {
        foreach (var o in xs)
        {
            if (o is A) { break; }
            else if (o is B) { return 2; }
        }
        return 0;
    }
    int Comment(object o)
    {
        if (o is A) { return 1; }
        // the else
        else if (o is B) { return 2; }
        return 0;
    }
    int Single(object o)
    {
        if (o is A) { return 1; }
        return 0;
    }
}
"""

    Assert.Empty(suggestCode "CR0003" source)

// ---- CR0002: constant-comparison chain → switch ----

[<Fact>]
let ``single-return arms become a switch expression`` () =
    let source =
        """
class C
{
    string Name(int k)
    {
        if (k == 1) return "one";
        else if (k == 2 || k == 3) return "few";
        else if (4 == k) return "four";
        else return "many";
    }
}
"""

    let fired = suggestCode "CR0002" source
    Assert.Equal(1, fired.Length)
    let fixedSource = fixAll "CR0002" source

    let expected =
        """        return k switch
        {
            1 => "one",
            2 or 3 => "few",
            4 => "four",
            _ => "many",
        };
"""

    Assert.Contains(normalize expected, fixedSource)

[<Fact>]
let ``assignment arms and a throwing else become an assigned switch expression`` () =
    let source =
        """
using System;
enum Color { Red, Green, Blue }
class C
{
    string Hex(Color c)
    {
        string s;
        if (c == Color.Red)
        {
            s = "#f00";
        }
        else if (c == Color.Green)
        {
            s = "#0f0";
        }
        else if (c == Color.Blue)
        {
            s = "#00f";
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(c));
        }
        return s;
    }
}
"""

    let fixedSource = fixAll "CR0002" source

    let expected =
        """        s = c switch
        {
            Color.Red => "#f00",
            Color.Green => "#0f0",
            Color.Blue => "#00f",
            _ => throw new ArgumentOutOfRangeException(nameof(c)),
        };
        return s;
"""

    Assert.Contains(normalize expected, fixedSource)

[<Fact>]
let ``statement bodies become a switch statement with stacked labels`` () =
    let source =
        """
using System;
class C
{
    void Run(string cmd)
    {
        if (cmd == "start" || cmd == "go")
        {
            Console.WriteLine("starting");
            Start();
        }
        else if (cmd == "stop")
        {
            Stop();
        }
        else if (cmd == "quit")
        {
            return;
        }
    }
    void Start() { }
    void Stop() { }
}
"""

    let fixedSource = fixAll "CR0002" source

    let expected =
        """        switch (cmd)
        {
            case "start":
            case "go":
                Console.WriteLine("starting");
                Start();
                break;
            case "stop":
                Stop();
                break;
            case "quit":
                return;
        }
"""

    Assert.Contains(normalize expected, fixedSource)

[<Fact>]
let ``two comparisons, a mutable field, a non-constant, a repeated constant, a mixed scrutinee and a user-defined equality stand down``
    ()
    =
    let source =
        """
class Money { public static bool operator ==(Money a, Money b) => true; public static bool operator !=(Money a, Money b) => false; public override bool Equals(object o) => true; public override int GetHashCode() => 0; }
class C
{
    int counter;
    static readonly Money One = new Money();
    static readonly Money Two = new Money();
    int Two_(int k) { if (k == 1) return 1; else if (k == 2) return 2; else return 0; }
    int Field() { if (counter == 1) return 1; else if (counter == 2) return 2; else if (counter == 3) return 3; else return 0; }
    int NonConst(int k, int n) { if (k == 1) return 1; else if (k == n) return 2; else if (k == 3) return 3; else return 0; }
    int Repeated(int k) { if (k == 1) return 1; else if (k == 2) return 2; else if (k == 1) return 3; else return 0; }
    int Mixed(int k, int j) { if (k == 1) return 1; else if (j == 2) return 2; else if (k == 3) return 3; else return 0; }
    int Money_(Money m) { if (m == One) return 1; else if (m == Two) return 2; else if (m == null) return 3; else return 0; }
}
"""

    Assert.Empty(suggestCode "CR0002" source)

[<Fact>]
let ``a lambda parameter named like the subject keeps its own casts`` () =
    let source =
        """
using System.Linq;
class Circle { public double R; }
class D
{
    double B(object s, object[] all)
    {
        if (s is Circle) { return all.Sum(s => ((Circle)s).R) + ((Circle)s).R; }
        else if (s is string) { return 1; }
        return 0;
    }
}
"""

    Assert.Contains("return all.Sum(s => ((Circle)s).R) + circle.R;", fixAll "CR0003" source)

[<Fact>]
let ``a comment in a condition keeps the chain out of the expression form`` () =
    let source =
        """
class C
{
    string Name(int k)
    {
        if (k == 1 /* one */) return "one";
        else if (k == 2) return "two";
        else if (k == 3) return "three";
        else return "many";
    }
}
"""

    // the statement form would drop the comment too, so nothing fires
    Assert.Empty(suggestCode "CR0002" source)
