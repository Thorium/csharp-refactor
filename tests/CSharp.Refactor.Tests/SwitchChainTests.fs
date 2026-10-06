module CSharp.Refactor.Tests.SwitchChainTests

open Xunit
open CSharp.Refactor.Tests.Harness

// ---- CR0003: type-test chain → switch over type patterns ----

[<Fact>]
let ``a type-test chain with casts becomes a switch, the cast declaration the binder`` () =
    let source =
        csharp
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

    let fired = fires 1 "CR0003" source
    let fixedSource = fixAll "CR0003" source

    let expected =
        csharp
            """
                    switch (s)
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
        csharp
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
        csharp
            """
                    switch (o)
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
        csharp
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
        csharp
            """
                    switch (o)
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
        csharp
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
        csharp
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

    let fired = fires 1 "CR0002" source
    let fixedSource = fixAll "CR0002" source

    let expected =
        csharp
            """
                    return k switch
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
        csharp
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
        csharp
            """
                    s = c switch
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
        csharp
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
        csharp
            """
                    switch (cmd)
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
        csharp
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
        csharp
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
        csharp
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

[<Fact>]
let ``arms that meet at a wider natural type keep the statement form`` () =
    let source =
        csharp
            """
            class C
            {
                object Name(int k)
                {
                    if (k == 1) return 1;
                    else if (k == 2) return 2L;
                    else if (k == 3) return 3;
                    else return 4;
                }
            }
            """

    let fixedSource = fixAll "CR0002" source
    Assert.DoesNotContain("=> 2L", fixedSource)
    Assert.Contains("case 2:", fixedSource)

// ---- CR0181: switch statement → switch expression ----

[<Fact>]
let ``a switch statement whose every section returns, assigns one target or throws is a switch expression`` () =
    let source =
        csharp
            """
            using System;
            enum Kind { A, B, C, D }
            class C
            {
                string Name(Kind k)
                {
                    switch (k)
                    {
                        case Kind.A: return "a";
                        default: throw new ArgumentException();
                        case Kind.B:
                        case Kind.C: return "bc";
                    }
                }
                string Tail(Kind k)
                {
                    switch (k)
                    {
                        case Kind.A: return "a";
                        case Kind.B: { return "b"; }
                    }
                    return "other";
                }
                int Set(object o)
                {
                    int n;
                    switch (o)
                    {
                        case int i when i > 0: n = i; break;
                        case string s: n = s.Length; break;
                        default: n = 0; break;
                    }
                    return n;
                }
                string Open(Kind k)
                {
                    switch (k) { case Kind.A: return "a"; }
                    Console.WriteLine();
                    return "x";
                }
                string Two(Kind k)
                {
                    switch (k)
                    {
                        case Kind.A: Console.WriteLine(); return "a";
                        default: return "b";
                    }
                }
                string Bound(object o)
                {
                    switch (o)
                    {
                        case int i:
                        case long l: return "number";
                        default: return "other";
                    }
                }
                int Sum(int a, int b)
                {
                    switch (a + b) { case 0: return 1; default: return 2; }
                }
            }
            """

    // Name, Tail, Set, Sum; Open falls through to a statement, Two has two, Bound's labels designate
    Assert.Equal<string list>(
        [ "switch"; "switch"; "switch"; "switch" ],
        firedText source (suggestCode "CR0181" source)
    )

    let fixedSource = (fixAll "CR0181" source).Replace("\r\n", "\n")

    Assert.Contains(
        "        return k switch\n        {\n            Kind.A => \"a\",\n            Kind.B or Kind.C => \"bc\",\n            _ => throw new ArgumentException(),\n        };",
        fixedSource
    )

    Assert.Contains("            Kind.B => \"b\",\n            _ => \"other\",\n        };", fixedSource)
    Assert.DoesNotContain("    return \"other\";", fixedSource)

    Assert.Contains(
        "        n = o switch\n        {\n            int i when i > 0 => i,\n            string s => s.Length,\n            _ => 0,\n        };",
        fixedSource
    )

    Assert.Contains("switch (k) { case Kind.A: return \"a\"; }", fixedSource)
    Assert.Contains("case Kind.A: Console.WriteLine(); return \"a\";", fixedSource)
    Assert.Contains("case long l: return \"number\";", fixedSource)
    Assert.Contains("return (a + b) switch", fixedSource)

[<Fact>]
let ``drop_throwing_default leaves out a throwing default only beside arms naming every member of a plain enum`` () =
    let source =
        csharp
            """
            using System;
            enum Two { A, B }
            enum Three { A, B, C }
            [Flags] enum Bits { X = 1, Y = 2 }
            class C
            {
                string Full(Two t)
                {
                    switch (t)
                    {
                        case Two.A: return "a";
                        case Two.B: return "b";
                        default: throw new ArgumentOutOfRangeException(nameof(t));
                    }
                }
                string Partial(Three t)
                {
                    switch (t)
                    {
                        case Three.A: return "a";
                        case Three.B: return "b";
                        default: throw new ArgumentOutOfRangeException(nameof(t));
                    }
                }
                string Flagged(Bits b)
                {
                    switch (b)
                    {
                        case Bits.X: return "x";
                        case Bits.Y: return "y";
                        default: throw new ArgumentOutOfRangeException(nameof(b));
                    }
                }
            }
            """

    let dropping =
        Some(
            FakeOptions(dict [ "csharp_refactor.CR0181.drop_throwing_default", "true" ])
            :> Microsoft.CodeAnalysis.Diagnostics.AnalyzerConfigOptions
        )

    let fixedSource = (fixAllWith dropping "CR0181" source).Replace("\r\n", "\n")
    Assert.Contains("            Two.B => \"b\",\n        };", fixedSource)

    Assert.Contains(
        "            Three.B => \"b\",\n            _ => throw new ArgumentOutOfRangeException(nameof(t)),",
        fixedSource
    )

    Assert.Contains(
        "            Bits.Y => \"y\",\n            _ => throw new ArgumentOutOfRangeException(nameof(b)),",
        fixedSource
    )

    // off by default: the default stays as the `_` arm
    let plain = (fixAll "CR0181" source).Replace("\r\n", "\n")

    Assert.Contains(
        "            Two.B => \"b\",\n            _ => throw new ArgumentOutOfRangeException(nameof(t)),",
        plain
    )

[<Fact>]
let ``review 2026-09-28: CR0181 keeps a guard beside default, a user conversion to the governing type, a target a scrutinee re-points``
    ()
    =
    let source =
        csharp
            """
            using System;
            class Box { public int F; }
            class W { public string V; public static implicit operator string(W w) => w?.V; }
            class C
            {
                static Box cur = new Box();
                static int Next() { cur = new Box(); return 1; }
                static bool Validate(int n) => n >= 0 ? true : throw new ArgumentOutOfRangeException();
                string Guarded(int n)
                {
                    switch (n)
                    {
                        case 0: return "zero";
                        case int big when Validate(big):
                        default: return "other";
                    }
                }
                string Converted(W w)
                {
                    switch (w)
                    {
                        case null: return "null";
                        default: return "val";
                    }
                }
                void Repointed()
                {
                    switch (Next())
                    {
                        case 1: cur.F = 10; break;
                        default: cur.F = 30; break;
                    }
                }
            }
            """

    Assert.Empty(suggestCode "CR0181" source)

[<Fact>]
let ``CR0002 a break after a block stands off it by a blank line`` () =
    let source =
        csharp
            """
            class C
            {
                int Count;
                void A(int kind, bool ok)
                {
                    if (kind == 1)
                    {
                        if (ok)
                        {
                            Count++;
                        }
                    }
                    else if (kind == 2)
                    {
                        Count += 2;
                    }
                    else if (kind == 3)
                    {
                        Count += 3;
                    }
                }
            }
            """

    // StyleCop SA1513: a closing brace is followed by a blank line, so `break;`
    // does not sit right under the inner `if`'s brace
    Assert.Contains(
        csharp
            """
            case 1:
                            if (ok)
                            {
                                Count++;
                            }

                            break;
                        case 2:
                            Count += 2;
                            break;
            """,
        normalize (fixAll "CR0002" source)
    )
