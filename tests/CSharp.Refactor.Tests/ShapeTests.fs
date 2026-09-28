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
        csharp
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

[<Fact>]
let ``System.Text.Json's property attributes leave the setter init-able; Newtonsoft, a type converter, extension data and Populate keep it``
    ()
    =
    let source =
        csharp
            """
            using System.Collections.Generic;
            using System.Text.Json;
            using System.Text.Json.Serialization;
            namespace Newtonsoft.Json { class JsonPropertyAttribute : System.Attribute { public JsonPropertyAttribute(string n) { } } }
            class Dto
            {
                [JsonPropertyName("n")] public string Name { get; set; }
                [JsonIgnore] public int Hidden { get; set; }
                [Newtonsoft.Json.JsonProperty("o")] public string Old { get; set; }
                [JsonExtensionData] public Dictionary<string, JsonElement> Rest { get; set; }
            }
            class Uint64Converter : JsonConverter<Converted>
            {
                public override Converted Read(ref Utf8JsonReader r, System.Type t, JsonSerializerOptions o) => null;
                public override void Write(Utf8JsonWriter w, Converted v, JsonSerializerOptions o) { }
            }
            [JsonConverter(typeof(Uint64Converter))] class Converted { [JsonPropertyName("v")] public int V { get; set; } }
            static class Make
            {
                static Dto D() => new Dto { Name = "a", Hidden = 1, Old = "b", Rest = null };
                static Converted C() => new Converted { V = 1 };
            }
            """

    Assert.Equal<string list>([ "set"; "set" ], firedText source (suggestCode "CR0083" source))
    let fixedSource = fixAll "CR0083" source
    Assert.Contains("""[JsonPropertyName("n")] public string Name { get; init; }""", fixedSource)
    Assert.Contains("[JsonIgnore] public int Hidden { get; init; }", fixedSource)
    Assert.Contains("""[Newtonsoft.Json.JsonProperty("o")] public string Old { get; set; }""", fixedSource)
    Assert.Contains("public Dictionary<string, JsonElement> Rest { get; set; }", fixedSource)
    Assert.Contains("""[JsonPropertyName("v")] public int V { get; set; }""", fixedSource)

    // Populate writes into what a property already holds: anywhere in the compilation, nothing converts
    let populating =
        source
        + "\nstatic class Options { static JsonSerializerOptions O() => new() { PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate }; }\n"

    Assert.Empty(suggestCode "CR0083" populating)

// ---- CR0179 ----

[<Fact>]
let ``a local set member by member straight after its construction folds into an object initializer; the run stops at anything else``
    ()
    =
    let source =
        csharp
            """
            using System;
            class Order
            {
                public int Id { get; set; }
                public decimal Total { get; set; }
                public string Note;
                public Order Parent { get; set; }
                public Order() { }
                public Order(int id) { Id = id; }
            }
            class Special : Order { public new int Id { get; set; } }
            struct Point { public int X; public int Y; }
            class C
            {
                static int Parse(string s, out int v) { v = 0; return 0; }
                Order A(int id, decimal total)
                {
                    var o = new Order();
                    o.Id = id;
                    o.Total = total;
                    return o;
                }
                Order B(int id)
                {
                    Order o = new(id);
                    o.Note = "first";
                    Console.WriteLine();
                    o.Total = 2;
                    return o;
                }
                Order D()
                {
                    var o = new Order();
                    o.Id = 1;
                    o.Total = o.Id * 2;
                    return o;
                }
                Order E()
                {
                    var o = new Order();
                    o.Id = 1;
                    o.Id = 2;
                    return o;
                }
                Order F(Order parent)
                {
                    var o = new Order();
                    // the parent first
                    o.Parent = parent;
                    return o;
                }
                Order G()
                {
                    Order o = new Special();
                    o.Id = 1;
                    return o;
                }
                Order H()
                {
                    var o = new Order { Id = 1 };
                    o.Total = 2;
                    return o;
                }
                Order I(string s)
                {
                    var o = new Order();
                    o.Id = Parse(s, out var v);
                    return o;
                }
                Order J()
                {
                    var o = new Order();
                    o.Parent.Id = 1;
                    return o;
                }
                Point K()
                {
                    var p = new Point();
                    p.X = 1;
                    p.Y = 2;
                    return p;
                }
                Order L()
                {
                    var order = new Order();
                    order.Note = "a long enough note to push this well past the wrap column of the file";
                    order.Total = 1234567.89m;
                    return order;
                }
            }
            """

    // A, B (the run before Console), D (Id only), E (the first Id), K, L
    Assert.Equal<string list>([ "o"; "o"; "o"; "o"; "p"; "order" ], firedText source (suggestCode "CR0179" source))
    let fixedSource = fixAll "CR0179" source
    Assert.Contains("var o = new Order() { Id = id, Total = total };", fixedSource)
    Assert.Contains("""Order o = new(id) { Note = "first" };""", fixedSource)
    Assert.Contains("o.Total = 2;", fixedSource)
    Assert.Contains("var o = new Order() { Id = 1 };\n        o.Total = o.Id * 2;", fixedSource.Replace("\r\n", "\n"))
    Assert.Contains("var o = new Order() { Id = 1 };\n        o.Id = 2;", fixedSource.Replace("\r\n", "\n"))
    Assert.Contains("// the parent first", fixedSource)
    Assert.Contains("Order o = new Special();", fixedSource)
    Assert.Contains("var o = new Order { Id = 1 };", fixedSource)
    Assert.Contains("o.Id = Parse(s, out var v);", fixedSource)
    Assert.Contains("o.Parent.Id = 1;", fixedSource)
    Assert.Contains("var p = new Point() { X = 1, Y = 2 };", fixedSource)

    Assert.Contains(
        "var order = new Order()\n        {\n            Note = \"a long enough note to push this well past the wrap column of the file\",\n            Total = 1234567.89m\n        };",
        fixedSource.Replace("\r\n", "\n")
    )

// ---- CR0180 ----

[<Fact>]
let ``a static field nothing writes becomes static readonly; a write anywhere, a ref, a mutable struct, reflection or a generic owner keep it``
    ()
    =
    let source =
        csharp
            """
            using System;
            using System.Threading;
            struct Counter { public int N; public void Bump() => N++; }
            readonly struct Frozen { public readonly int N; }
            class Holder
            {
                public static string Prefix = "v";
                public static int[] Table = { 1, 2 };
                static DateTime Epoch = new DateTime(2000, 1, 1);
                static Frozen Ice;
                static int Written = 1;
                static int Bumped;
                static int Swapped;
                static int Aliased;
                static int InStatic;
                static Counter Mutable;
                static int Named;
                static int ByString;
                [ThreadStatic] static int PerThread;
                static volatile int Flag;
                static void Use()
                {
                    Written = 2;
                    Bumped++;
                    Interlocked.Increment(ref Swapped);
                    ref int r = ref Aliased;
                    Table[0] = 3;
                    Console.WriteLine(nameof(Named) + typeof(Holder).GetField("ByString"));
                }
                static Holder() { InStatic = 5; }
            }
            class Generic<T> { static int Shared; static void Set() => Generic<int>.Shared = 1; }
            """

    // Prefix, Table (its elements are not the field), Epoch, Ice; the rest are written, aliased,
    // named, copied, attributed, volatile or generic
    Assert.Equal<string list>([ "Prefix"; "Table"; "Epoch"; "Ice" ], firedText source (suggestCode "CR0180" source))

    let fixedSource = fixAll "CR0180" source
    Assert.Contains("""public static readonly string Prefix = "v";""", fixedSource)
    Assert.Contains("static readonly DateTime Epoch", fixedSource)
    Assert.Contains("static int Written = 1;", fixedSource)
    Assert.Contains("static Counter Mutable;", fixedSource)
    Assert.Contains("static int InStatic;", fixedSource)

// ---- CR0080 / CR0081 ----

[<Fact>]
let ``an immutable class becomes a record and a small record a readonly record struct; identity, inheritance and nullables hold``
    ()
    =
    let source =
        csharp
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

[<Fact>]
let ``CR0081 holds a record another file uses by null, as, coalesce, conditional access or a class constraint`` () =
    let small = "record Small(int X);"
    let plain = "class U { int M(Small s) => s.X; }"
    Assert.Equal(1, (suggestInProject false false "CR0081" [ "Small.cs", small; "U.cs", plain ]).Length)

    for other in
        [
            csharp
                """
                #nullable disable
                class U { Small M() { Small s = null; return s; } }
                """
            csharp
                """
                #nullable disable
                class U { Small M(object o) => o as Small; }
                """
            csharp
                """
                #nullable disable
                class U { Small M(Small a, Small b) => a ?? b; }
                """
            csharp
                """
                #nullable disable
                class U { int? M(Small s) => s?.X; }
                """
            "class U { static T Id<T>(T x) where T : class => x; Small M(Small s) => Id<Small>(s); }"
        ] do
        Assert.Empty(suggestInProject false false "CR0081" [ "Small.cs", small; "U.cs", other ])

// ---- CR0082 ----

[<Fact>]
let ``a reference tuple in private shapes becomes a value tuple; a null test or a nested generic holds`` () =
    let source =
        csharp
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

    let fired = fires 1 "CR0082" source
    let fixedSource = fixAll "CR0082" source
    Assert.Contains("""private (int, string) Pair() => (1, "a");""", fixedSource)
    Assert.Contains("Tuple<int, int> Checked()", fixedSource)
    Assert.Contains("Tuple<bool, bool> First()", fixedSource)

[<Fact>]
let ``CR0082 holds a reference tuple compared with == or !=`` () =
    let source =
        csharp
            """
            using System;
            class C
            {
                private Tuple<int, int> _last = Tuple.Create(0, 0);
                private bool Changed(int a, int b) { var t = Tuple.Create(a, b); var changed = t != _last; _last = t; return changed; }
                private Tuple<string, string> Pair() => Tuple.Create("a", "b");
                private bool Same() { var p = Pair(); var q = Pair(); return p == q; }
            }
            """

    Assert.Empty(suggestCode "CR0082" source)

// ---- CR0089 ----

[<Fact>]
let ``a private type's clock slot read through parity members migrates to DateTimeOffset; a Date read or a mixed clock holds``
    ()
    =
    let source =
        csharp
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

    let fired = fires 1 "CR0089" source
    let fixedSource = fixAll "CR0089" source
    Assert.Contains("DateTimeOffset started = DateTimeOffset.UtcNow;", fixedSource)
    Assert.Contains("public DateTimeOffset LastSeen { get; set; }", fixedSource)
    Assert.Contains("LastSeen = DateTimeOffset.UtcNow;", fixedSource)
    Assert.Contains("DateTime day = DateTime.Now;", fixedSource)

[<Fact>]
let ``CR0089 holds a clock slot printed through ToString()`` () =
    let source =
        csharp
            """
            using System;
            class Outer
            {
                private class Stamp
                {
                    DateTime at = DateTime.Now;
                    public string Show() => at.ToString();
                    public int Year() => at.Year;
                }
            }
            """

    Assert.Empty(suggestCode "CR0089" source)

// ---- CR0172, the field half ----

[<Fact>]
let ``a static readonly field of a constant becomes const; a public one only under the API gate, a written or attributed one never``
    ()
    =
    let source =
        csharp
            """
            using System;
            public class C
            {
                static readonly string Prefix = "v";
                private static readonly int Retries = 3, Timeout = 30;
                internal static readonly double Ratio = 1.5;
                public static readonly string PublicName = "pub";
                protected static readonly string ProtectedName = "prot";
                static readonly string Rewritten = "a";
                [ThreadStatic] static readonly int Slotted = 1;
                static readonly int[] Table = { 1, 2 };
                static readonly string Empty = string.Empty;
                static readonly DateTime When = DateTime.MinValue;
                static C() { Rewritten = "b"; }
                string M() => Prefix + Retries + Timeout + Ratio + PublicName + ProtectedName + Rewritten + Slotted + Table.Length + Empty + When;
            }
            """

    Assert.Equal<string list>(
        [
            "string Prefix = \"v\""
            "int Retries = 3, Timeout = 30"
            "double Ratio = 1.5"
        ],
        suggestCode "CR0172" source |> firedText source
    )

    let fixedSource = fixAll "CR0172" source
    Assert.Contains("""    const string Prefix = "v";""", fixedSource)
    Assert.Contains("    private const int Retries = 3, Timeout = 30;", fixedSource)
    Assert.Contains("    internal const double Ratio = 1.5;", fixedSource)
    Assert.Contains("""    public static readonly string PublicName = "pub";""", fixedSource)
    Assert.Contains("""    static readonly string Rewritten = "a";""", fixedSource)

    let opened = suggestCodeWith apiOpen "CR0172" source |> firedText source
    Assert.Contains("string PublicName = \"pub\"", opened)
    Assert.Contains("string ProtectedName = \"prot\"", opened)
    Assert.Contains("""    public const string PublicName = "pub";""", fixAllWith apiOpen "CR0172" source)

// ---- CR0183 ----

[<Fact>]
let ``a private Try-method whose callers only test it returns the value or null; a null true, a public one or a caller reading after keep it``
    ()
    =
    let source =
        csharp
            """
            #nullable enable
            using System;
            using System.Collections.Generic;
            class Order { }
            class C
            {
                Dictionary<string, Order?> _map = new();
                static void Use(object o) { }
                private bool TryFind(string k, out Order o)
                {
                    if (k.Length > 0) { o = new Order(); return true; }
                    o = null!;
                    return false;
                }
                private bool TryNum(string s, out int n)
                {
                    n = 0;
                    if (s.Length > 0) { n = s.Length; return true; }
                    return false;
                }
                private bool TryMaybe(string k, out Order o)
                {
                    if (_map.TryGetValue(k, out var found)) { o = found!; return true; }
                    o = null!;
                    return false;
                }
                public bool TryPublic(string k, out int n) { n = 1; return true; }
                private bool TryGrouped(string k, out int n) { n = 1; return true; }
                private bool TryAfter(string k, out int n) { n = 1; return true; }
                void Callers(string k)
                {
                    if (TryFind(k, out var o)) Use(o);
                    var len = TryNum(k, out var n) ? n : -1;
                    if (TryMaybe(k, out var m)) Use(m);
                    TryPublic(k, out var p);
                    Func<string, int> f = s => 0;
                    if (TryAfter(k, out var a)) { }
                    Use(a);
                }
                void Leaving(string k)
                {
                    if (!TryFind(k, out var o2)) return;
                    Use(o2);
                    if (TryNum(k, out _)) Use(1);
                }
                delegate bool Getter(string k, out int n);
                Getter G => TryGrouped;
            }
            """

    // TryFind and TryNum; TryMaybe's value may be null (the `!` only hides it), TryPublic is public,
    // TryGrouped is taken as a method group, TryAfter's caller reads `a` after the if
    Assert.Equal<string list>([ "TryFind"; "TryNum" ], firedText source (suggestCode "CR0183" source))
    let fixedSource = fixAll "CR0183" source
    Assert.Contains("private Order? TryFind(string k)", fixedSource)
    Assert.Contains("if (k.Length > 0) { return new Order(); }", fixedSource)
    Assert.Contains("return null;", fixedSource)
    Assert.Contains("if (TryFind(k) is { } o) Use(o);", fixedSource)
    Assert.Contains("if (TryFind(k) is not { } o2) return;", fixedSource)
    Assert.Contains("private int? TryNum(string s)", fixedSource)
    Assert.Contains("if (s.Length > 0) { return s.Length; }", fixedSource)
    Assert.Contains("var len = (TryNum(k) is { } n) ? n : -1;", fixedSource)
    Assert.Contains("if (TryNum(k) is { }) Use(1);", fixedSource)
    Assert.Contains("private bool TryMaybe(string k, out Order o)", fixedSource)
    Assert.Contains("private bool TryAfter(string k, out int n)", fixedSource)

// ---- CR0185 ----

[<Fact>]
let ``a method returning a List every caller only reads returns IReadOnlyList; an Add, an in-place Reverse, a method group or a List local keep it``
    ()
    =
    let source =
        csharp
            """
            using System;
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                List<int> Evens() => new List<int> { 2, 4 };
                IList<int> Odds() => new List<int> { 1 };
                List<int> Mut() => new List<int>();
                List<int> Rev() => new List<int>();
                List<int> Group() => new List<int>();
                List<int> Typed() => new List<int>();
                static int Total(IEnumerable<int> xs) => xs.Sum();
                IEnumerable<int> Passed() { return Evens(); }
                void Use()
                {
                    foreach (var e in Evens()) Console.WriteLine(e);
                    Console.WriteLine(Evens().Count + Evens()[0] + Total(Evens()) + Evens().Where(x => x > 2).Count());
                    var xs = Evens();
                    foreach (var x in xs) Console.WriteLine(x + xs.Count);
                    Console.WriteLine(Odds().Contains(1));
                    Mut().Add(1);
                    var r = Rev();
                    r.Reverse();
                    Func<List<int>> f = Group;
                    List<int> t = Typed();
                }
            }
            """

    Assert.Equal<string list>([ "Evens"; "Odds" ], firedText source (suggestCode "CR0185" source))
    let fixedSource = fixAll "CR0185" source
    Assert.Contains("IReadOnlyList<int> Evens() => new List<int> { 2, 4 };", fixedSource)
    Assert.Contains("IReadOnlyList<int> Odds() => new List<int> { 1 };", fixedSource)
    Assert.Contains("List<int> Mut() =>", fixedSource)
    Assert.Contains("List<int> Rev() =>", fixedSource)
    Assert.Contains("List<int> Group() =>", fixedSource)
    Assert.Contains("List<int> Typed() =>", fixedSource)

[<Fact>]
let ``review 2026-09-28: CR0183 keeps a Try-method whose true value may still be null, an overload the new signature would take, a finally resetting the value``
    ()
    =
    let source =
        csharp
            """
            #nullable enable
            using System.Collections.Generic;
            class C
            {
                string[] _slots = new string[3];
                Dictionary<string, string> _map = new();
                private bool TrySlot(int i, out string s) { if (i < 3) { s = _slots[i]; return true; } s = ""; return false; }
                private bool TryMap(string k, out string s) { if (k.Length > 0) { s = _map[k]; return true; } s = ""; return false; }
                private string? TryName(object k) => null;
                private bool TryName(string k, out string s) { if (k.Length > 0) { s = new string('a', 1); return true; } s = ""; return false; }
                private bool TryReset(int i, out int n) { try { n = i * 2; return true; } finally { n = 0; } }
                void Use()
                {
                    if (TrySlot(0, out var a)) System.Console.WriteLine(a);
                    if (TryMap("k", out var b)) System.Console.WriteLine(b);
                    if (TryName("k", out var c)) System.Console.WriteLine(c);
                    if (TryReset(5, out var d)) System.Console.WriteLine(d);
                }
            }
            """

    Assert.Empty(suggestCode "CR0183" source)

[<Fact>]
let ``review 2026-09-28: CR0180 keeps a static field a reflective loader, a nested deconstruction or a parenthesised store writes``
    ()
    =
    let source =
        csharp
            """
            using System.Reflection;
            static class Settings { public static int Retries = 3; }
            static class Loader
            {
                public static void Load()
                {
                    foreach (var f in typeof(Settings).GetFields(BindingFlags.Public | BindingFlags.Static)) f.SetValue(null, 7);
                }
            }
            """

    Assert.Empty(suggestCode "CR0180" source)

    let deconstructed =
        csharp
            """
            static class Counters { public static int C = 3; public static int D = 4; public static int E = 5; public static int F = 6; }
            static class Writers
            {
                public static void Write()
                {
                    ((Counters.C, Counters.D), _) = ((10, 20), 30);
                    (Counters.F) = 40;
                }
                public static int Read() => Counters.E;
            }
            """

    // only E: C and D are written through the nested deconstruction, F through the parentheses
    Assert.Equal<string list>([ "E" ], firedText deconstructed (suggestCode "CR0180" deconstructed))

[<Fact>]
let ``review 2026-09-28: CR0083 keeps a System.Text.Json setter whose property has an initializer or a constructor value``
    ()
    =
    let source =
        csharp
            """
            using System.Text.Json.Serialization;
            class OrderDto
            {
                [JsonPropertyName("qty")] public int Quantity { get; set; } = 1;
                [JsonPropertyName("id")] public string Id { get; set; }
                [JsonPropertyName("note")] public string Note { get; set; }
                [JsonPropertyName("plain")] public int Plain { get; set; }
                public OrderDto() { Note = "none"; }
                static OrderDto Make() => new OrderDto { Quantity = 2, Id = "a", Note = "n", Plain = 3 };
            }
            """

    // Id and Plain: no initializer, no constructor value - default(T) whoever builds it
    Assert.Equal<string list>([ "set"; "set" ], firedText source (suggestCode "CR0083" source))
    let fixedSource = fixAll "CR0083" source
    Assert.Contains("""[JsonPropertyName("id")] public string Id { get; init; }""", fixedSource)
    Assert.Contains("""[JsonPropertyName("plain")] public int Plain { get; init; }""", fixedSource)
    Assert.Contains("public int Quantity { get; set; } = 1;", fixedSource)
    Assert.Contains("public string Note { get; set; }", fixedSource)

[<Fact>]
let ``review 2026-09-28: CR0179 keeps a construction a goto returns over, a trailing comment, a one-line block that does not fit``
    ()
    =
    let source =
        csharp
            """
            using System;
            class Item { public string Name; public string Echo; }
            class Holder { public int A; }
            class C
            {
                void Loop()
                {
                    int n = 0;
                    Func<string> previous = null;
                again:
                    var o = new Item();
                    o.Name = "item" + n;
                    o.Echo = previous?.Invoke() ?? "none";
                    previous = () => o.Name;
                    if (++n < 3) goto again;
                }
                void Commented()
                {
                    var h = new Holder(); // keep: explains why
                    h.A = 1;
                }
                void OneLine(bool b)
                {
                    if (b) { var o2 = new Item(); o2.Name = "a long enough name to push this well past the wrap column of the file"; o2.Echo = "x"; Console.WriteLine(o2.Name); }
                }
            }
            """

    Assert.Empty(suggestCode "CR0179" source)
