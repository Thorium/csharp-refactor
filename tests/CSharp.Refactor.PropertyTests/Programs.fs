/// Generated C# programs: one class of independent members, each one a
/// shape some rule is written for, with the free parts (literals, the
/// boolean term) drawn at random. A member never refers to another, so a
/// shrunk program is any sub-list of the original and still compiles.
module CSharp.Refactor.PropertyTests.Programs

open FsCheck
open FsCheck.FSharp
open CSharp.Refactor.PropertyTests.BoolExpr

/// One member. The comment names the rule the shape is for; the
/// generator's coverage test checks that every one of them still fires.
type Shape =
    /// CR0001: `if (c) return true; return false;`
    | BoolReturn of e: BoolExpr * lit: bool
    /// CR0001: `bool r; if (c) r = true; else r = false;`
    | AssignReturn of e: BoolExpr * lit: bool
    /// CR0005: `if (a) { if (b) { .. } }`
    | NestedIf of a: BoolExpr * b: BoolExpr
    /// CR0007, CR0008, CR0011: a boolean term as it comes
    | BoolFn of BoolExpr
    /// CR0004: `x.HasValue ? x.Value + 1 : 0`
    | NullableValue
    /// CR0009: two `case`s with one body
    | SwitchDuplicate of string
    /// CR0015: `for (int i = 0; i < xs.Length; i++) .. xs[i]`
    | IndexLoop
    /// CR0020: `foreach (var x in xs.ToList())`
    | ToListForeach
    /// CR0021: `total += x` in a loop, a decimal
    | SumLoop
    /// CR0024: `acc.Add(x)` in a loop
    | AddLoop
    /// CR0029: `xs.Select(f).Select(g)`
    | SelectSelect
    /// CR0031: `new Random().Next(..)`
    | NewRandom
    /// CR0033: `sb.Append(a + b)`
    | AppendConcat
    /// CR0046: `async` with nothing but `return await`
    | ReturnAwait
    /// CR0048: `Monitor.Enter` / `Monitor.Exit` in `finally`
    | MonitorLock
    /// CR0055: `CancellationToken.None` while `ct` is in scope
    | TokenNone
    /// CR0065: `if (!Cond(ex)) throw;` first in a catch
    | CatchWhen
    /// CR0090: `new Guid()`
    | NewGuid
    /// CR0103: `$"no holes"`
    | HoleFree of string
    /// CR0140, CR0141: `[ObsoleteAttribute]`, `[Obsolete()]`
    | AttributeSyntax of bool
    /// CR0143: `@plain`
    | Verbatim
    /// CR0144: `else { if (c) { .. } }`
    | ElseIf of BoolExpr
    /// CR0145: `System.Text.Json.JsonSerializer` spelled six times
    | Qualified
    /// CR0009: switch-expression arms sharing a body, the last beside the discard
    | SwitchExpressionDuplicate of string
    /// CR0082: a `Tuple<int, string>` local handed to a source parameter of its
    /// type — and one handed to `object`, which a value tuple would box
    | ReferenceTuple
    /// CR0100, CR0101: a concatenation and a `string.Format` as arguments of a
    /// call with a `FormattableString` overload marked `[Obsolete(error)]` — a
    /// rewrite that re-binds the call fails to compile
    | OverloadDecoy of string
    /// CR0171: `foreach (var k in d.Keys) d.Add(..)`
    | KeysMutation
    /// CR0109: the same pattern twice in a generic container (the field form)
    | RegexPair of string
    /// CR0080: an immutable data holder; and one whose getter has a body
    | DataHolder

let private parameters =
    variables |> Array.map (fun v -> $"int {v}") |> String.concat ", "

let private litText (b: bool) = if b then "true" else "false"

/// The member for a shape, at position `i` in the class, unindented.
let print (i: int) (shape: Shape) : string =
    match shape with
    | BoolReturn(e, lit) ->
        $"static bool M{i}({parameters})\n{{\n    if ({printBool e}) return {litText lit};\n    return {litText (not lit)};\n}}"
    | AssignReturn(e, lit) ->
        $"static bool M{i}({parameters})\n{{\n    bool r;\n    if ({printBool e}) r = {litText lit}; else r = {litText (not lit)};\n    return r;\n}}"
    | NestedIf(a, b) ->
        $"static int M{i}({parameters})\n{{\n    if ({printBool a})\n    {{\n        if ({printBool b})\n        {{\n            return 1;\n        }}\n    }}\n    return 0;\n}}"
    | BoolFn e -> $"static bool M{i}({parameters}) => {printBool e};"
    | NullableValue -> $"static int M{i}(int? x) => x.HasValue ? x.Value + 1 : 0;"
    | SwitchDuplicate s ->
        $"static string M{i}(int k)\n{{\n    switch (k)\n    {{\n        case 1: return \"{s}\";\n        case 2: return \"{s}\";\n        default: return \"\";\n    }}\n}}"
    | IndexLoop ->
        $"static void M{i}(int[] xs)\n{{\n    for (int i = 0; i < xs.Length; i++) Console.WriteLine(xs[i]);\n}}"
    | ToListForeach ->
        $"static void M{i}(IEnumerable<int> xs)\n{{\n    foreach (var x in xs.ToList()) Console.WriteLine(x);\n}}"
    | SumLoop ->
        // a decimal over a List: an integral `Sum` throws where `+=`
        // wraps, so that shape rewrites only under `checked`, and a lazy
        // source measured slower than the loop
        $"static decimal M{i}(List<decimal> xs)\n{{\n    var total = 0m;\n    foreach (var x in xs) total += x;\n    return total;\n}}"
    | AddLoop -> $"static void M{i}(List<int> xs, List<int> acc)\n{{\n    foreach (var x in xs) acc.Add(x);\n}}"
    | SelectSelect -> $"static IEnumerable<int> M{i}(List<int> xs) => xs.Select(x => x + 1).Select(y => y * 2);"
    | NewRandom -> $"static int M{i}() => new Random().Next(10);"
    | AppendConcat -> $"static void M{i}(StringBuilder sb, string a, string b) => sb.Append(a + b);"
    | ReturnAwait ->
        $"static async Task<int> I{i}(int x)\n{{\n    await Task.Yield();\n    return x;\n}}\n\nstatic async Task<int> M{i}(int x)\n{{\n    return await I{i}(x);\n}}"
    | MonitorLock ->
        $"static readonly object G{i} = new object();\nstatic void M{i}()\n{{\n    Monitor.Enter(G{i});\n    try\n    {{\n        Console.WriteLine(1);\n    }}\n    finally\n    {{\n        Monitor.Exit(G{i});\n    }}\n}}"
    | TokenNone -> $"static Task M{i}(CancellationToken ct) => Task.Delay(1, CancellationToken.None);"
    | CatchWhen ->
        $"static int M{i}()\n{{\n    try\n    {{\n        return 1;\n    }}\n    catch (Exception ex)\n    {{\n        if (!ex.Message.Contains(\"x\")) throw;\n        return 0;\n    }}\n}}"
    | NewGuid -> $"static Guid M{i}() => new Guid();"
    | HoleFree s -> $"static string M{i}() => $\"{s}\";"
    | AttributeSyntax suffix ->
        let attribute = if suffix then "[ObsoleteAttribute]" else "[Obsolete()]"
        $"{attribute}\nstatic void M{i}() {{ }}"
    | Verbatim -> $"static int M{i}(int @plain) => @plain;"
    | ElseIf e ->
        $"static int M{i}({parameters})\n{{\n    if (x0 > 0)\n    {{\n        return 1;\n    }}\n    else\n    {{\n        if ({printBool e})\n        {{\n            return 2;\n        }}\n    }}\n    return 3;\n}}"
    | Qualified ->
        let call = "System.Text.Json.JsonSerializer.Serialize(o)"
        $"static string M{i}(object o) => {call} + {call} + {call} + {call} + {call} + {call};"
    | SwitchExpressionDuplicate s ->
        // 1 and 2 fold; 3 must not fold into the discard (`3 or _` reads as a mistake)
        $"static string M{i}(int k) => k switch\n{{\n    1 => \"{s}\",\n    2 => \"{s}\",\n    3 => \"o\",\n    _ => \"o\",\n}};"
    | ReferenceTuple ->
        // `t`'s type is retyped with `T{i}`'s parameter; `u` goes to `object`, where a
        // value tuple would box, so its type stays — a retype there would not compile
        $"static int T{i}(Tuple<int, string> p) => p.Item1;\nstatic int M{i}()\n{{\n    var t = Tuple.Create(1, \"a\");\n    var u = Tuple.Create(2, 3);\n    return T{i}(t) + string.Format(\"{{0}}\", u).Length;\n}}"
    | OverloadDecoy s ->
        // an interpolated string converts to FormattableString; the decoy overload is an
        // error to call, so a rewrite that re-binds the call fails to compile
        $"struct Raw{i}\n{{\n    public string Text;\n    public static implicit operator Raw{i}(string s) => new Raw{i} {{ Text = s }};\n}}\nstatic int Sink{i}(Raw{i} sql) => sql.Text.Length;\n[Obsolete(\"\", true)]\nstatic int Sink{i}(FormattableString sql) => 0;\nstatic int M{i}(string a) => Sink{i}(\"{s} \" + a + \" x\") + Sink{i}(string.Format(\"{s} {{0}}\", a));\nstatic string P{i}(string a) => \"{s} \" + a + \" x\";"
    | KeysMutation ->
        $"static void M{i}(Dictionary<string, int> d)\n{{\n    foreach (var k in d.Keys) d.Add(k + \"x\", 1);\n}}"
    | RegexPair s ->
        // a generic container takes the field form (no source generator in the harness);
        // one pattern, one field, the second site referring to it
        $"static class R{i}<T>\n{{\n    public static bool M(string s) => Regex.IsMatch(s, \"{s}.*\") || Regex.IsMatch(s, \"{s}.*\");\n}}"
    | DataHolder ->
        $"sealed class D{i}\n{{\n    public int A {{ get; }}\n    public string B {{ get; }}\n    public D{i}(int a, string b) {{ A = a; B = b; }}\n}}\nsealed class L{i}\n{{\n    public int N {{ get; }}\n    public int Twice {{ get {{ return N * 2; }} }}\n    public L{i}(int n) {{ N = n; }}\n}}"

/// The program: every shape as one member of the class, in order.
let program (shapes: Shape list) : string =
    let members =
        shapes
        |> List.mapi (fun i shape ->
            print i shape
            |> _.Split('\n')
            |> Array.map (fun l -> if l = "" then "" else "    " + l)
            |> String.concat "\n")
        |> String.concat "\n\n"

    "using System;\nusing System.Collections.Generic;\nusing System.Linq;\nusing System.Text;\nusing System.Text.RegularExpressions;\nusing System.Threading;\nusing System.Threading.Tasks;\n\n"
    + $"class C\n{{\n{members}\n}}\n"

// ---- generation ----

/// Letters only: safe inside a string literal and an interpolated one.
let private genWord =
    Gen.choose (1, 8)
    |> Gen.bind (fun n -> Gen.elements [ 'a' .. 'z' ] |> List.replicate n |> Gen.sequenceToList)
    |> Gen.map (Array.ofList >> System.String)

let genShape (size: int) : Gen<Shape> =
    let term = genBool (size / 2)

    let withLit make =
        gen {
            let! e = term
            let! lit = Gen.elements [ true; false ]
            return make (e, lit)
        }

    Gen.frequency
        [
            3, withLit BoolReturn
            2, withLit AssignReturn
            2,
            gen {
                let! a = term
                let! b = term
                return NestedIf(a, b)
            }
            5, Gen.map BoolFn term
            1, Gen.constant NullableValue
            1, Gen.map SwitchDuplicate genWord
            1, Gen.constant IndexLoop
            1, Gen.constant ToListForeach
            1, Gen.constant SumLoop
            1, Gen.constant AddLoop
            1, Gen.constant SelectSelect
            1, Gen.constant NewRandom
            1, Gen.constant AppendConcat
            1, Gen.constant ReturnAwait
            1, Gen.constant MonitorLock
            1, Gen.constant TokenNone
            1, Gen.constant CatchWhen
            1, Gen.constant NewGuid
            2, Gen.map HoleFree genWord
            2, Gen.map AttributeSyntax (Gen.elements [ true; false ])
            1, Gen.constant Verbatim
            2, Gen.map ElseIf term
            1, Gen.constant Qualified
            1, Gen.map SwitchExpressionDuplicate genWord
            1, Gen.constant ReferenceTuple
            1, Gen.map OverloadDecoy genWord
            1, Gen.constant KeysMutation
            1, Gen.map RegexPair genWord
            1, Gen.constant DataHolder
        ]

let genProgram: Gen<Shape list> =
    Gen.sized (fun size ->
        gen {
            let! count = Gen.choose (1, max 1 (size / 4))
            return! List.replicate count (genShape size) |> Gen.sequenceToList
        })

/// A shape's own smaller versions: its boolean terms shrunk.
let shrinkShape (shape: Shape) : seq<Shape> =
    seq {
        match shape with
        | BoolReturn(e, lit) -> for e' in shrinkBool e -> BoolReturn(e', lit)
        | AssignReturn(e, lit) -> for e' in shrinkBool e -> AssignReturn(e', lit)
        | NestedIf(a, b) ->
            for a' in shrinkBool a -> NestedIf(a', b)
            for b' in shrinkBool b -> NestedIf(a, b')
        | BoolFn e -> for e' in shrinkBool e -> BoolFn e'
        | ElseIf e -> for e' in shrinkBool e -> ElseIf e'
        | _ -> ()
    }

/// Drop one member, or shrink one in place.
let shrinkProgram (shapes: Shape list) : seq<Shape list> =
    seq {
        for i in 0 .. shapes.Length - 1 do
            yield List.removeAt i shapes

        for i in 0 .. shapes.Length - 1 do
            for smaller in shrinkShape shapes.[i] -> List.updateAt i smaller shapes
    }

let arbitrary: Arbitrary<Shape list> = Arb.fromGenShrink (genProgram, shrinkProgram)

/// The codes the generated programs are meant to reach.
let targetedCodes =
    [
        "CR0001"
        "CR0004"
        "CR0005"
        "CR0007"
        "CR0008"
        "CR0009"
        "CR0011"
        "CR0015"
        "CR0020"
        "CR0021"
        "CR0024"
        "CR0029"
        "CR0031"
        "CR0033"
        "CR0046"
        "CR0048"
        "CR0055"
        "CR0065"
        "CR0090"
        "CR0103"
        "CR0140"
        "CR0141"
        "CR0143"
        "CR0144"
        "CR0145"
        "CR0080"
        "CR0082"
        "CR0100"
        "CR0101"
        "CR0109"
        "CR0171"
    ]
