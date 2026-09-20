module CSharp.Refactor.Tests.ShapeTests

open Xunit
open CSharp.Refactor.Tests.Harness

let private apiOpen =
    Some(
        FakeOptions(dict [ "csharp_refactor.api_changes", "true" ])
        :> Microsoft.CodeAnalysis.Diagnostics.AnalyzerConfigOptions
    )

// ---- CR0083 ----

[<Fact>]
let ``a setter only used while constructing becomes init; a later write, a serializer attribute or an entity keeps it``
    ()
    =
    let source =
        """
using System;
using System.Text.Json.Serialization;
class Db { public DbSet<Row> Rows { get; set; } }
class DbSet<T> { }
class Row { public int Id { get; set; } }
class Dto { [JsonPropertyName("n")] public string Name { get; set; } }
class Point
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Z { get; set; }
    private int W { get; set; }
    public Point() { X = 1; }
    void Move() { Y = 2; }
    static Point Make() => new Point { Z = 3 };
    static void Other(Point p) { p.W = 4; }
}
class Copy { public int X { get; set; } public Copy(Copy other) { other.X = 1; } }
"""

    // Point.X (constructor via this), Point.Z (initializer); Y is written by a method, W by another
    // instance in a method, Copy.X through another instance in the constructor; the leaf compilation opens the public shape
    Assert.Equal<string list>([ "set"; "set" ], firedText source (suggestCode "CR0083" source))
    let fixedSource = fixAll "CR0083" source
    Assert.Contains("public int X { get; init; }", fixedSource)
    Assert.Contains("public int Z { get; init; }", fixedSource)
    Assert.Contains("public int Y { get; set; }", fixedSource)

// ---- CR0080 / CR0081 ----

[<Fact>]
let ``an immutable class becomes a record and a small record a readonly record struct; identity, inheritance and nullables hold``
    ()
    =
    let source =
        """
using System;
using System.Collections.Generic;
class Money
{
    public decimal Amount { get; }
    public string Currency { get; }
    public Money(decimal amount, string currency) { Amount = amount; Currency = currency; }
}
class Service
{
    public int Rate { get; }
    public Service(int rate) { Rate = rate; }
    public int Apply(int x) => x * Rate;
}
class Counter
{
    public int Value { get; private set; }
    public void Bump() => Value++;
}
class Keyed
{
    public int Id { get; }
    public Keyed(int id) { Id = id; }
    static readonly Dictionary<Keyed, int> Index = new Dictionary<Keyed, int>();
}
class Parent { public int A { get; } }
class Child : Parent { public int B { get; } }
record Point(int X, int Y);
record Named(string Name);
record Big(long A, long B, long C, long D, long E);
record Boxed(int X);
class Uses { object O() => new Boxed(1); Point? P() => null; }
record Small(int X);
"""

    Assert.Equal<string list>([ "Money" ], firedText source (suggestCode "CR0080" source))
    Assert.Contains("record Money", fixAll "CR0080" source)
    // Point is used as `Point?`, Boxed is boxed, Named holds a string, Big is too big
    Assert.Equal<string list>([ "Small" ], firedText source (suggestCode "CR0081" source))
    Assert.Contains("readonly record struct Small(int X);", fixAll "CR0081" source)

// ---- CR0082 ----

[<Fact>]
let ``a reference tuple in private shapes becomes a value tuple; a null test or a nested generic holds`` () =
    let source =
        """
using System;
using System.Collections.Generic;
class C
{
    private Tuple<int, string> Pair() => Tuple.Create(1, "a");
    private int Use() { var p = Pair(); return p.Item1 + p.Item2.Length; }
    private Tuple<int, int> Checked() { Tuple<int, int> t = new Tuple<int, int>(1, 2); return t == null ? null : t; }
    private List<Tuple<bool, bool>> Many() => new List<Tuple<bool, bool>>();
    private Tuple<bool, bool> First() => Many()[0];
}
"""

    let fired = suggestCode "CR0082" source
    Assert.Equal(1, fired.Length)
    let fixedSource = fixAll "CR0082" source
    Assert.Contains("private (int, string) Pair() => (1, \"a\");", fixedSource)
    Assert.Contains("Tuple<int, int> Checked()", fixedSource)
    Assert.Contains("Tuple<bool, bool> First()", fixedSource)

// ---- CR0089 ----

[<Fact>]
let ``a private type's clock slot read through parity members migrates to DateTimeOffset; a Date read or a mixed clock holds``
    ()
    =
    let source =
        """
using System;
class Outer
{
    private class Session
    {
        DateTime started = DateTime.UtcNow;
        public DateTime LastSeen { get; set; }
        public void Touch() { LastSeen = DateTime.UtcNow; }
        public bool Stale() => DateTime.UtcNow - LastSeen > TimeSpan.FromMinutes(5) && started.Year > 2000;
    }
    private class Calendar
    {
        DateTime day = DateTime.Now;
        public int Today() => day.Date.Day;
    }
    private class Mixed
    {
        DateTime a = DateTime.Now;
        DateTime b = DateTime.UtcNow;
        public bool Later() => a > b;
    }
    private class Passed
    {
        DateTime when = DateTime.UtcNow;
        public void Log() => Console.WriteLine(when);
    }
}
"""

    let fired = suggestCode "CR0089" source
    Assert.Equal(1, fired.Length)
    let fixedSource = fixAll "CR0089" source
    Assert.Contains("DateTimeOffset started = DateTimeOffset.UtcNow;", fixedSource)
    Assert.Contains("public DateTimeOffset LastSeen { get; set; }", fixedSource)
    Assert.Contains("LastSeen = DateTimeOffset.UtcNow;", fixedSource)
    Assert.Contains("DateTime day = DateTime.Now;", fixedSource)
