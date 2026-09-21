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
    /// CR0172: a local and a private static readonly field holding a constant
    | ConstCandidate of string
    /// CR0173: `if (c) return 1; else return 2;`, the else-less twin, the assignment
    | HoistedReturn of BoolExpr * form: int
    /// CR0174: a Substring or a range handed to Parse, Append or Write
    | SubstringToConsumer of form: int
    /// CR0175: a guarded prefix or suffix compared with a literal word
    | PrefixCompare of form: int * word: string
    /// CR0176: `foreach (var c in s.ToCharArray())`
    | CharArrayLoop
    /// CR0177: an invariant local inside a loop, a concatenation or an arithmetic
    | LoopInvariant of word: string * form: int
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
    // ---- family A ----
    /// CR0002: an if/else-if ladder over constants of one subject
    | ConstantLadder
    /// CR0003: a type-test chain with casts
    | TypeTestChain
    /// CR0006: a long guarded block with a short exiting else (default-off)
    | GuardFlip
    /// CR0010: `case var x when x == "A":`
    | GuardIsConstant
    /// CR0012: an arm that says it is unfinished and returns null
    | UnfinishedArm
    /// CR0013, CR0014: enum switches missing a member, with and without default
    | EnumSwitch of bool
    /// CR0016: a flag-steered while loop (default-off)
    | FlagLoop
    /// CR0017: a closure over the for variable handed to a lazy query
    | ForCapture
    // ---- family B ----
    /// CR0022: `bool found = false; foreach … if (p) found = true;` (default-off)
    | FoundFlag
    /// CR0023: a literal array probed by Contains
    | LiteralSet
    /// CR0025: `s += piece` in a loop
    | AppendInLoop
    /// CR0026, CR0030: a bare IEnumerable counted per iteration and enumerated twice
    | Rewalk
    /// CR0027: a lazy statement
    | LazyStatement
    /// CR0028: a fill loop (default-off)
    | FillLoop
    /// CR0032: dictionary keys then indexer
    | KeysIndexer
    /// CR0034: a query inside a loop
    | QueryInLoop
    /// CR0035: a recursive iterator
    | RecursiveIterator
    // ---- family C ----
    /// CR0040: `t.Result` in an async body
    | BlockingDrain
    /// CR0041: a private drainer whose callers are all async
    | Taskify
    /// CR0042: a sync call with an Async twin in an async body
    | AsyncTwin
    /// CR0043: `async void` with an async caller
    | AsyncVoid
    /// CR0044: a dropped task
    | DroppedTask
    /// CR0045: a blocking xUnit test
    | BlockingTest
    /// CR0047: `lock (this)`
    | WeakLock
    /// CR0049, CR0050: check-then-store on a ConcurrentDictionary, a cached Task
    | ConcurrentCache
    /// CR0051: a using outlived by the returned task
    | UsingOutlived
    /// CR0052: a this-capturing handler on a process-wide publisher
    | ProcessExitHandler
    /// CR0053: an async lambda to a void delegate
    | AsyncLambdaVoid
    /// CR0054: a single-task combinator
    | SingleTaskCombinator
    // ---- family D ----
    /// CR0060: a disposable local never disposed
    | UndisposedLocal
    /// CR0061, CR0062, CR0063: an ownerless disposable field, a Dispose that frees nothing, a fake Dispose
    | DisposableDesign
    /// CR0064: a swallowing catch-all
    | SwallowingCatch
    /// CR0066, CR0067, CR0068: throw in finally, in ToString, of a reserved type
    | ThrowPlaces
    /// CR0069: a constant exception message on a private method with a printable parameter
    | ConstantMessage of string
    /// CR0070: an informative exception member left unread
    | UninformedCatch
    // ---- family E ----
    /// CR0081: a record of two ints
    | SmallRecord
    /// CR0083: a setter only used in constructors
    | InitCandidate
    /// CR0084: a public mutable static written from two sites
    | PublicStatic
    /// CR0085: a type test by name
    | TypeByName
    /// CR0086: a virtual call from a constructor
    | VirtualInCtor
    /// CR0087: an enum compared by text
    | EnumByText
    /// CR0089: a private type's clock slot (default-off)
    | ClockSlot
    // ---- family F ----
    /// CR0102: a ToString inside a hole
    | ToStringHole
    /// CR0104: `x == null || x == ""`
    | Emptiness
    /// CR0105: a culture-free Parse
    | CultureFreeParse
    /// CR0106: `DateTime.Now` as an instant
    | LocalNow
    /// CR0107: a pattern the engine rejects
    | InvalidPattern
    /// CR0108: a plain-text regex, in one of its eight shapes (IsMatch bare and
    /// anchored, Match.Success, Matches.Count bare, against zero and against one,
    /// Replace, Split), over a random word
    | PlainTextRegex of form: int * word: string
    /// CR0110: an HttpClient per call
    | ClientPerCall
    /// CR0111: a hand-joined path
    | HandJoinedPath
    /// CR0112: a zero-width space inside a literal
    | HiddenUnicode
    /// CR0113: arithmetic near the int ceiling
    | NearCeiling
    /// CR0114, CR0115: a log template that does not fit, a caught exception not logged
    | LogTemplate
    // ---- family G ----
    /// CR0120, CR0121: SQL text built from a value, a constant DML without a parameter
    | SqlText
    /// CR0122: a command line built from a value
    | CommandLine
    /// CR0123, CR0124: a provider-format secret, a credential in a connection string
    | Secrets
    /// CR0125, CR0126: a broken hash, an obsolete crypto constructor
    | Crypto
    // ---- family H / ladder ----
    /// CR0142: two attribute lists on one line (default-off)
    | AttributeLists
    /// CR0146: a trailing note on a public declaration
    | TrailingNote of string
    /// CR0147: a length-test chain
    | LengthChain
    /// CR0148: `Encoding.UTF8.GetBytes("literal")`
    | Utf8Literal of string
    /// CR0149: an init property every construction sets (default-off)
    | RequiredCandidate
    /// CR0150: a static readonly set filled in its initialiser and only read
    | FrozenCandidate
    /// CR0151: `params T[]` only enumerated
    | ParamsSpan
    /// CR0152: a gate object used only by lock
    | LockGate
    /// CR0153: a property whose backing field is only its own
    | FieldKeyword
    /// CR0154: `if (x != null) x.P = v;`
    | NullConditionalAssignment
    /// CR0155: a static class of extension methods on one receiver (default-off)
    | ExtensionBlock
    /// CR0157: an exhaustive switch whose discard throws
    | UnreachableDiscard
    // ---- family J ----
    /// CR0161: a mutating struct method on a readonly field
    | StructCopy
    /// CR0162: a dropped System.Threading.Timer
    | DroppedTimer
    /// CR0163: a semaphore released in the same block with no try
    | UnguardedSemaphore
    /// CR0164: check-then-assign on a static reference field
    | StaticLazyInit
    /// CR0165: a wrapping throw without the inner exception
    | LostInner
    /// CR0166: `try { v = int.Parse(s); } catch (FormatException) { v = -1; }`
    | ParseTry
    /// CR0167: a float compared with ==
    | FloatEquality
    /// CR0168: an integer division landing in a double
    | IntDivision
    /// CR0169: a local time compared with a UTC time
    | MixedKinds
    /// CR0170: a loop that awaits under an unobserved token
    | UnobservedToken

let private parameters =
    variables |> Array.map (fun v -> $"int {v}") |> String.concat ", "

let private litText (b: bool) = if b then "true" else "false"

let private lines (ls: string list) = String.concat "\n" ls

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
    | ConstCandidate w ->
        $"static readonly string Name{i} = \"{w}\";
static string M{i}() {{ var local = \"{w}\"; int n = 3; return local + n + Name{i}; }}"
    | SubstringToConsumer form ->
        match form with
        | 0 -> $"static int M{i}(string s) => int.Parse(s.Substring(6, 5));"
        | 1 ->
            $"static int M{i}(string s) {{ var sb = new System.Text.StringBuilder(); sb.Append(s.Substring(6)); return sb.Length; }}"
        | 2 -> $"static void M{i}(string s, System.IO.TextWriter w) => w.Write(s[6..]);"
        | _ -> $"static long M{i}(string s) => long.Parse(s[6..11]);"
    | PrefixCompare(form, w) ->
        let n = w.Length

        match form with
        | 0 -> $"static bool M{i}(string s) => s.Length >= {n} && s.Substring(0, {n}) == \"{w}\";"
        | 1 -> $"static bool M{i}(string s) => s.Length < {n} || s[..{n}] != \"{w}\";"
        | 2 ->
            $"static bool M{i}(string s) {{ if (s.Length >= {n}) return s.Substring(s.Length - {n}) == \"{w}\"; return false; }}"
        | _ -> $"static bool M{i}(string s) => s.Length > {n - 1} && s[^{n}..] == \"{w}\";"
    | CharArrayLoop ->
        $"static int M{i}(string s) {{ int n = 0; foreach (var c in s.ToCharArray()) if (c == 'a') n++; return n; }}"
    | LoopInvariant(w, form) ->
        match form with
        | 0 ->
            $"static int M{i}(int[] xs, string tag)
{{
    int n = 0;
    foreach (var x in xs)
    {{
        var label = tag + \"{w}\";
        n += label.Length + x;
    }}
    return n;
}}"
        | _ ->
            $"static int M{i}(int[] xs, int a)
{{
    int n = 0;
    for (int j = 0; j < xs.Length; j++)
    {{
        var c = a * {w.Length} + 1;
        n += xs[j] * c;
    }}
    return n;
}}"
    | HoistedReturn(e, form) ->
        match form with
        | 0 ->
            $"static int M{i}({parameters})
{{
    if ({printBool e}) return 1; else return 2;
}}"
        | 1 ->
            $"static int M{i}({parameters})
{{
    if ({printBool e}) return x0; return x1 + 1;
}}"
        | _ ->
            $"static int M{i}({parameters})
{{
    int r;
    if ({printBool e}) r = x0; else r = x1;
    return r;
}}"
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
    // ---- family A ----
    | ConstantLadder ->
        lines
            [
                $"static string M{i}(int k)"
                "{"
                "    if (k == 1) return \"one\";"
                "    else if (k == 2) return \"two\";"
                "    else if (k == 3) return \"three\";"
                "    else return \"many\";"
                "}"
            ]
    | TypeTestChain ->
        lines
            [
                $"abstract class Shape{i} {{ }}"
                $"sealed class Circle{i} : Shape{i} {{ public double R; }}"
                $"sealed class Rect{i} : Shape{i} {{ public double W, H; }}"
                $"static double M{i}(Shape{i} s)"
                "{"
                $"    if (s is Circle{i})"
                "    {"
                $"        var c = (Circle{i})s;"
                "        return 3.14 * c.R * c.R;"
                "    }"
                $"    else if (s is Rect{i})"
                "    {"
                $"        var r = (Rect{i})s;"
                "        return r.W * r.H;"
                "    }"
                "    return 0;"
                "}"
            ]
    | GuardFlip ->
        lines
            [
                $"static int M{i}(bool ok, int n)"
                "{"
                "    if (ok)"
                "    {"
                lines [ for k in 1..20 -> $"        n = n + {k};" ]
                "    }"
                "    else"
                "    {"
                "        return -1;"
                "    }"
                "    return n;"
                "}"
            ]
    | GuardIsConstant ->
        lines
            [
                $"static int M{i}(string s)"
                "{"
                "    switch (s)"
                "    {"
                "        case var x when x == \"A\": return 1;"
                "        default: return 0;"
                "    }"
                "}"
            ]
    | UnfinishedArm ->
        lines
            [
                $"enum Method{i} {{ Gauss, Seidel }}"
                $"static double[] M{i}(Method{i} m, double[] cf)"
                "{"
                "    switch (m)"
                "    {"
                $"        case Method{i}.Gauss: return cf.Select(x => x * 2).ToArray();"
                $"        case Method{i}.Seidel:"
                "            // not supported yet"
                "            return null;"
                "        default: throw new ArgumentOutOfRangeException(nameof(m));"
                "    }"
                "}"
            ]
    | EnumSwitch withDefault ->
        lines
            [
                $"enum Color{i} {{ Red, Green, Blue }}"
                $"static string M{i}(Color{i} c)"
                "{"
                "    switch (c)"
                "    {"
                $"        case Color{i}.Red: return \"r\";"
                (if withDefault then
                     $"        case Color{i}.Green: return \"g\";\n        default: return \"other\";"
                 else
                     $"        case Color{i}.Green: throw new InvalidOperationException();")
                "    }"
                "    return \"?\";"
                "}"
            ]
    | FlagLoop ->
        lines
            [
                $"static void M{i}(int[] xs)"
                "{"
                "    bool done = false;"
                "    int i = 0;"
                "    while (!done && i < xs.Length)"
                "    {"
                "        if (xs[i] < 0)"
                "        {"
                "            done = true;"
                "        }"
                "        Console.WriteLine(xs[i]);"
                "        i++;"
                "    }"
                "}"
            ]
    | ForCapture ->
        $"static void M{i}(List<Task> tasks)\n{{\n    for (int i = 0; i < 3; i++) tasks.Add(Task.Run(() => Console.WriteLine(i)));\n}}"
    // ---- family B ----
    | FoundFlag ->
        $"static bool M{i}(IEnumerable<int> xs) {{ bool found = false; foreach (var x in xs) if (x > 3) found = true; return found; }}"
    | LiteralSet ->
        $"static readonly string[] Allowed{i} = {{ \"a\", \"b\", \"c\" }};\nstatic IEnumerable<string> M{i}(IEnumerable<string> xs) => xs.Where(x => Allowed{i}.Contains(x));"
    | AppendInLoop ->
        // the plain `s += x` accumulation is CR0021's rewrite, so CR0025 leaves it alone
        $"static int[] M{i}(int[] arr, IEnumerable<int> xs) {{ foreach (var x in xs) arr = arr.Append(x).ToArray(); return arr; }}"
    | Rewalk ->
        lines
            [
                $"static int M{i}(IEnumerable<int> xs)"
                "{"
                "    for (int i = 0; i < xs.Count(); i++) Console.WriteLine(xs.ElementAt(i));"
                "    if (xs.Any()) { foreach (var x in xs) Console.WriteLine(x); }"
                "    return 0;"
                "}"
            ]
    | LazyStatement -> $"static void M{i}(List<int> xs)\n{{\n    xs.Where(x => x > 1);\n}}"
    | FillLoop ->
        lines
            [
                $"static List<string> M{i}(IEnumerable<int> xs)"
                "{"
                "    var names = new List<string>();"
                "    foreach (var x in xs)"
                "    {"
                "        if (x > 0) names.Add(x.ToString());"
                "    }"
                "    return names;"
                "}"
            ]
    | KeysIndexer ->
        lines
            [
                $"static void M{i}(Dictionary<string, int> d)"
                "{"
                "    foreach (var k in d.Keys)"
                "    {"
                "        Console.WriteLine(k + d[k]);"
                "        Console.WriteLine(d[k] * 2);"
                "    }"
                "}"
            ]
    | QueryInLoop ->
        lines
            [
                $"sealed class Order{i} {{ public int CustomerId; }}"
                $"sealed class Customer{i} {{ public int Id; }}"
                $"sealed class Db{i} {{ public IQueryable<Order{i}> Orders; }}"
                $"static void M{i}(Db{i} db, List<Customer{i}> customers)"
                "{"
                "    foreach (var c in customers)"
                "    {"
                "        foreach (var o in db.Orders.Where(o => o.CustomerId == c.Id)) Console.WriteLine(o);"
                "    }"
                "}"
            ]
    | RecursiveIterator ->
        lines
            [
                $"sealed class Node{i} {{ public List<Node{i}> Children = new(); }}"
                $"static IEnumerable<Node{i}> M{i}(Node{i} n) {{ yield return n; foreach (var c in n.Children) foreach (var d in M{i}(c)) yield return d; }}"
            ]
    // ---- family C ----
    | BlockingDrain -> $"static async Task<int> M{i}(Task<int> t) {{ var x = t.Result; return x + 1; }}"
    | Taskify ->
        lines
            [
                $"static Task<int> Load{i}() => Task.FromResult(1);"
                $"private static int Fetch{i}(int n) {{ var x = Load{i}().Result; return x + n; }}"
                $"static async Task<int> M{i}() {{ var r = Fetch{i}(1); return r; }}"
            ]
    | AsyncTwin ->
        $"static async Task<string> M{i}(System.IO.StreamReader reader) {{ var line = reader.ReadLine(); return line; }}"
    | AsyncVoid ->
        lines
            [
                $"static async void Work{i}() {{ await Task.Yield(); }}"
                $"static async Task M{i}() {{ Work{i}(); }}"
            ]
    | DroppedTask ->
        lines
            [
                $"static Task Save{i}() => Task.CompletedTask;"
                $"static void M{i}() {{ Save{i}(); }}"
            ]
    | BlockingTest ->
        lines
            [
                $"public class Tests{i}"
                "{"
                "    static Task<int> Load() => Task.FromResult(1);"
                "    [Xunit.Fact]"
                "    public void T() { var r = Load().Result; Xunit.Assert.Equal(1, r); }"
                "}"
            ]
    | WeakLock ->
        lines
            [
                $"sealed class Counter{i}"
                "{"
                "    int count;"
                "    public void A() { lock (this) { count++; } }"
                "}"
            ]
    | ConcurrentCache ->
        lines
            [
                $"sealed class Cache{i}"
                "{"
                "    readonly ConcurrentDictionary<string, int> cache = new();"
                "    readonly ConcurrentDictionary<string, Task<int>> tasks = new();"
                "    int Compute(string k) => k.Length;"
                "    public int A(string k)"
                "    {"
                "        if (!cache.TryGetValue(k, out var v))"
                "        {"
                "            v = Compute(k);"
                "            cache[k] = v;"
                "        }"
                "        return v;"
                "    }"
                "    public Task<int> F(string k) => tasks.GetOrAdd(k, key => Task.FromResult(key.Length));"
                "}"
            ]
    | UsingOutlived ->
        lines
            [
                $"static Task<int> Read{i}(System.IO.Stream s) => Task.FromResult(1);"
                $"static Task<int> M{i}(string path) {{ using var f = System.IO.File.OpenRead(path); return Read{i}(f); }}"
            ]
    | ProcessExitHandler ->
        lines
            [
                $"sealed class Sink{i}"
                "{"
                "    void Flush() { }"
                "    public void E() { AppDomain.CurrentDomain.ProcessExit += (s, e) => Flush(); }"
                "}"
            ]
    | AsyncLambdaVoid ->
        lines
            [
                $"static Task Save{i}() => Task.CompletedTask;"
                $"static void M{i}(List<int> xs) {{ xs.ForEach(async x => await Save{i}()); }}"
            ]
    | SingleTaskCombinator -> $"static Task M{i}(Task t) => Task.WhenAll(new[] {{ t }});"
    // ---- family D ----
    | UndisposedLocal ->
        $"static int M{i}(string p) {{ var s = new FileStream(p, FileMode.Open); return s.ReadByte(); }}"
    | DisposableDesign ->
        lines
            [
                $"sealed class Owner{i} {{ readonly FileStream stream = new FileStream(\"x\", FileMode.Open); public int Read() => stream.ReadByte(); }}"
                $"sealed class Forgetful{i} : IDisposable"
                "{"
                "    readonly FileStream stream = new FileStream(\"x\", FileMode.Open);"
                "    readonly CancellationTokenSource cts = new CancellationTokenSource();"
                "    public int Read() => stream.ReadByte();"
                "    public void Dispose() { cts.Cancel(); }"
                "}"
                $"sealed class Fake{i} {{ public void Dispose() {{ }} }}"
            ]
    | SwallowingCatch ->
        lines
            [
                $"static int Parse{i}(string s) => int.Parse(s);"
                $"static int M{i}(string s) {{ try {{ return Parse{i}(s); }} catch {{ return 0; }} }}"
            ]
    | ThrowPlaces ->
        lines
            [
                $"sealed class Thrower{i}"
                "{"
                "    public void A() { try { } finally { throw new InvalidOperationException(); } }"
                "    public override string ToString() => throw new NotSupportedException();"
                "    public void D(object o) { if (o == null) throw new NullReferenceException(); }"
                "}"
            ]
    | ConstantMessage s ->
        $"private static void M{i}(int count, string name) {{ if (count < 0) throw new ArgumentException(\"Rejected {s}\"); }}"
    | UninformedCatch ->
        $"static void M{i}() {{ try {{ }} catch (System.Reflection.ReflectionTypeLoadException e) {{ Console.WriteLine(e.Message); }} }}"
    // ---- family E ----
    | SmallRecord -> $"record Small{i}(int X, int Y);"
    | InitCandidate ->
        lines
            [
                $"sealed class Key{i}"
                "{"
                "    public int Id { get; set; }"
                $"    public Key{i}(int id) {{ Id = id; }}"
                "}"
            ]
    | PublicStatic ->
        lines
            [
                $"static class Globals{i}"
                "{"
                "    public static int Counter;"
                "    public static void A() { Counter++; }"
                "    public static void B() { Counter = 0; }"
                "}"
            ]
    | TypeByName -> $"static bool M{i}(object x) => x.GetType().Name == \"Customer\";"
    | VirtualInCtor ->
        lines
            [
                $"class Base{i}"
                "{"
                "    protected virtual void Init() { }"
                $"    public Base{i}() {{ Init(); }}"
                "}"
                $"sealed class Derived{i} : Base{i} {{ protected override void Init() {{ }} }}"
            ]
    | EnumByText ->
        lines
            [
                $"enum Status{i} {{ Active = 1, Closed = 2 }}"
                $"static bool M{i}(Status{i} s) => s.ToString() == \"Active\";"
            ]
    | ClockSlot ->
        lines
            [
                $"private sealed class Session{i}"
                "{"
                "    DateTime started = DateTime.UtcNow;"
                "    public DateTime LastSeen { get; set; }"
                "    public void Touch() { LastSeen = DateTime.UtcNow; }"
                "    public bool Stale() => DateTime.UtcNow - LastSeen > TimeSpan.FromMinutes(5) && started.Year > 2000;"
                "}"
            ]
    // ---- family F ----
    | ToStringHole -> $"static string M{i}(int x) => $\"{{x.ToString()}} items\";"
    | Emptiness -> $"static bool M{i}(string x) => x == null || x == \"\";"
    | CultureFreeParse -> $"static double M{i}(string s) => double.Parse(s);"
    | LocalNow -> $"static long M{i}() => DateTime.Now.Ticks;"
    | InvalidPattern -> $"static bool M{i}(string s) => Regex.IsMatch(s, \"(unclosed\");"
    | PlainTextRegex(form, w) ->
        match form with
        | 0 -> $"static bool M{i}(string s) => Regex.IsMatch(s, \"^{w}\");"
        | 1 -> $"static bool M{i}(string s) => Regex.IsMatch(s, \"{w}\");"
        | 2 -> $"static bool M{i}(string s) => Regex.Match(s, \"{w}\").Success;"
        | 3 -> $"static int M{i}(string s) => Regex.Matches(s, \"{w}\").Count;"
        | 4 -> $"static bool M{i}(string s) => Regex.Matches(s, \"{w}\").Count > 0;"
        | 5 -> $"static bool M{i}(string s) => Regex.Matches(s, \"{w}\").Count == 0;"
        | 6 -> $"static string M{i}(string s) => Regex.Replace(s, \"{w}\", \"x\");"
        | _ -> $"static int M{i}(string s) => Regex.Split(s, \"{w}\").Length;"
    | ClientPerCall -> $"static int M{i}() {{ var c = new System.Net.Http.HttpClient(); return c.GetHashCode(); }}"
    | HandJoinedPath -> $"static string M{i}(string dir, string file) => dir + \"\\\\\" + file;"
    | HiddenUnicode -> $"static string M{i}() => \"ab​c\";"
    | NearCeiling -> $"static int M{i}(int balance) => balance + 2_000_000_000;"
    | LogTemplate ->
        lines
            [
                $"sealed class Worker{i}"
                "{"
                "    readonly Microsoft.Extensions.Logging.ILogger log;"
                "    public void A(int id) => log.LogInformation(\"{Id} and {Name}\", id);"
                "    public void B(int id) { try { A(id); } catch (Exception ex) { log.LogError(\"sync failed {Id}\", id); } }"
                "}"
            ]
    // ---- family G ----
    | SqlText ->
        lines
            [
                $"static void M{i}(System.Data.Common.DbCommand cmd, int id) {{ cmd.CommandText = $\"SELECT * FROM t WHERE id = {{id}}\"; }}"
                $"static void N{i}(System.Data.Common.DbCommand cmd) {{ cmd.CommandText = \"DELETE FROM t WHERE id = 1\"; }}"
            ]
    | CommandLine -> $"static void M{i}(string input) {{ Process.Start(\"cmd\", $\"/c {{input}}\"); }}"
    | Secrets ->
        lines
            [
                $"const string Github{i} = \"ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ0246813579abcd\";"
                $"const string Prod{i} = \"Server=db.internal;Database=app;User Id=sa;Password=Hunter2!;\";"
            ]
    | Crypto ->
        lines
            [
                $"static byte[] M{i}(byte[] x) {{ using var md5 = MD5.Create(); return md5.ComputeHash(x); }}"
                $"static byte[] N{i}(byte[] x) {{ using var sha = new SHA256Managed(); return sha.ComputeHash(x); }}"
            ]
    // ---- family H / ladder ----
    | AttributeLists -> $"[Xunit.Fact] [Xunit.Trait(\"a\", \"b\")]\npublic void M{i}() {{ }}"
    | TrailingNote s -> $"public decimal M{i}(int n) => n * 2m; // {s} rate, non-compounding"
    | LengthChain ->
        lines
            [
                $"static string M{i}(int[] xs)"
                "{"
                "    if (xs.Length == 0)"
                "    {"
                "        return \"none\";"
                "    }"
                "    else if (xs.Length == 1)"
                "    {"
                "        var a = xs[0];"
                "        return \"one \" + a;"
                "    }"
                "    else"
                "    {"
                "        return \"many\";"
                "    }"
                "}"
            ]
    | Utf8Literal s -> $"static byte[] M{i}() => Encoding.UTF8.GetBytes(\"{s}\");"
    | RequiredCandidate ->
        lines
            [
                $"sealed class Options{i}"
                "{"
                "    public string Name { get; init; }"
                "    public int Port { get; init; }"
                "}"
                $"static Options{i} M{i}() => new Options{i} {{ Name = \"a\", Port = 1 }};"
            ]
    | FrozenCandidate ->
        lines
            [
                $"private static readonly HashSet<string> Allowed{i} = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {{ \"a\", \"b\" }};"
                $"static bool M{i}(string s) => Allowed{i}.Contains(s);"
            ]
    | ParamsSpan ->
        lines
            [
                $"private static int Sum{i}(params int[] xs) {{ var t = 0; foreach (var x in xs) t += x; return t + xs.Length; }}"
                $"static int M{i}() => Sum{i}(1, 2);"
            ]
    | LockGate ->
        lines
            [
                $"sealed class Gated{i}"
                "{"
                "    private readonly object _gate = new();"
                "    int n;"
                "    public void A() { lock (_gate) { n++; } }"
                "}"
            ]
    | FieldKeyword ->
        lines
            [
                $"sealed class Counted{i}"
                "{"
                "    private int _count = 3;"
                "    public int Count { get => _count; set => _count = value < 0 ? 0 : value; }"
                "}"
            ]
    | NullConditionalAssignment ->
        lines
            [
                $"sealed class Holder{i} {{ public int Count {{ get; set; }} }}"
                $"static void M{i}(Holder{i} other, int v) {{ if (other != null) other.Count = v; }}"
            ]
    | ExtensionBlock ->
        // the static class itself lives outside C (extension methods cannot nest)
        $"static bool M{i}(string s) => s.IsBlank{i}();"
    | UnreachableDiscard ->
        lines
            [
                $"enum Kind{i} {{ A, B }}"
                $"static int M{i}(Kind{i} k) => k switch {{ Kind{i}.A => 1, Kind{i}.B => 2, _ => throw new InvalidOperationException() }};"
            ]
    // ---- family J ----
    | StructCopy ->
        lines
            [
                $"struct Counter{i} {{ public int N; public void Bump() => N++; }}"
                $"sealed class Owner{i}"
                "{"
                $"    readonly Counter{i} counter;"
                "    public void A() { counter.Bump(); }"
                "}"
            ]
    | DroppedTimer -> $"static void M{i}() {{ new Timer(_ => Console.WriteLine(\"tick\"), null, 0, 1000); }}"
    | UnguardedSemaphore ->
        lines
            [
                $"static readonly SemaphoreSlim Gate{i} = new(1, 1);"
                $"static async Task M{i}()"
                "{"
                $"    await Gate{i}.WaitAsync();"
                "    Console.WriteLine(1);"
                $"    Gate{i}.Release();"
                "}"
            ]
    | StaticLazyInit ->
        lines
            [
                $"static StringBuilder cache{i};"
                $"static StringBuilder M{i}() {{ if (cache{i} == null) cache{i} = new StringBuilder(); return cache{i}; }}"
            ]
    | LostInner ->
        $"static void M{i}() {{ try {{ Console.WriteLine(1); }} catch (Exception ex) {{ throw new InvalidOperationException(\"failed\"); }} }}"
    | ParseTry ->
        lines
            [
                $"static int M{i}(string s)"
                "{"
                "    int v;"
                "    try { v = int.Parse(s); } catch (FormatException) { v = -1; }"
                "    return v;"
                "}"
            ]
    | FloatEquality -> $"static bool M{i}(double a, double b) => a * 2 == b;"
    | IntDivision -> $"static double M{i}(int sum, int count) {{ double avg = sum / count; return avg; }}"
    | MixedKinds ->
        lines
            [
                $"static readonly DateTime Started{i} = DateTime.UtcNow;"
                $"static bool M{i}() => DateTime.Now > Started{i};"
            ]
    | UnobservedToken ->
        lines
            [
                $"static async Task M{i}(int[] xs, CancellationToken ct)"
                "{"
                "    foreach (var x in xs) { await Task.Delay(x); }"
                "}"
            ]

/// The usings every program starts with.
[<Literal>]
let header =
    "using System;\nusing System.Collections.Concurrent;\nusing System.Collections.Generic;\nusing System.Diagnostics;\nusing System.Globalization;\nusing System.IO;\nusing System.Linq;\nusing System.Security.Cryptography;\nusing System.Text;\nusing System.Text.RegularExpressions;\nusing System.Threading;\nusing System.Threading.Tasks;\nusing Microsoft.Extensions.Logging;\n\n"

/// The logging shapes need Microsoft.Extensions.Logging's surface; the
/// harness references no such package, so the program carries the shape of it.
[<Literal>]
let loggerShim =
    "\nnamespace Microsoft.Extensions.Logging\n{\n    public interface ILogger { }\n    public struct EventId { public EventId(int id) { } }\n    public static class LoggerExtensions\n    {\n        public static void LogError(this ILogger l, string message, params object[] args) { }\n        public static void LogError(this ILogger l, System.Exception exception, string message, params object[] args) { }\n        public static void LogError(this ILogger l, EventId eventId, string message, params object[] args) { }\n        public static void LogInformation(this ILogger l, string message, params object[] args) { }\n    }\n}\n"

/// The program: every shape as one member of the class, in order.
/// What a shape declares OUTSIDE the class, at the top level: only what
/// cannot nest (a static class of extension methods).
let outside (i: int) (shape: Shape) : string option =
    match shape with
    | ExtensionBlock ->
        Some(
            lines
                [
                    $"static class StringExtensions{i}"
                    "{"
                    $"    public static bool IsBlank{i}(this string s) => string.IsNullOrWhiteSpace(s);"
                    $"    public static string Shout{i}(this string s) => s.ToUpperInvariant();"
                    "}"
                ]
        )
    | _ -> None

let program (shapes: Shape list) : string =
    let members =
        shapes
        |> List.mapi (fun i shape ->
            print i shape
            |> _.Split('\n')
            |> Array.map (fun l -> if l = "" then "" else "    " + l)
            |> String.concat "\n")
        |> String.concat "\n\n"

    let outsides =
        shapes
        |> List.mapi outside
        |> List.choose id
        |> List.map (fun t -> "\n" + t + "\n")
        |> String.concat ""

    header + $"class C\n{{\n{members}\n}}\n" + outsides + loggerShim

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
            2, Gen.map ConstCandidate genWord
            2, Gen.map HoistedReturn (Gen.zip term (Gen.choose (0, 2)))
            2, Gen.map SubstringToConsumer (Gen.choose (0, 3))
            2, Gen.map PrefixCompare (Gen.zip (Gen.choose (0, 3)) genWord)
            1, Gen.constant CharArrayLoop
            2, Gen.map LoopInvariant (Gen.zip genWord (Gen.choose (0, 1)))
            1, Gen.constant Qualified
            1, Gen.map SwitchExpressionDuplicate genWord
            1, Gen.constant ReferenceTuple
            1, Gen.map OverloadDecoy genWord
            1, Gen.constant KeysMutation
            1, Gen.map RegexPair genWord
            1, Gen.constant DataHolder
            1, Gen.constant ConstantLadder
            1, Gen.constant TypeTestChain
            1, Gen.constant GuardFlip
            1, Gen.constant GuardIsConstant
            1, Gen.constant UnfinishedArm
            1, Gen.map EnumSwitch (Gen.elements [ true; false ])
            1, Gen.constant FlagLoop
            1, Gen.constant ForCapture
            1, Gen.constant FoundFlag
            1, Gen.constant LiteralSet
            1, Gen.constant AppendInLoop
            1, Gen.constant Rewalk
            1, Gen.constant LazyStatement
            1, Gen.constant FillLoop
            1, Gen.constant KeysIndexer
            1, Gen.constant QueryInLoop
            1, Gen.constant RecursiveIterator
            1, Gen.constant BlockingDrain
            1, Gen.constant Taskify
            1, Gen.constant AsyncTwin
            1, Gen.constant AsyncVoid
            1, Gen.constant DroppedTask
            1, Gen.constant BlockingTest
            1, Gen.constant WeakLock
            1, Gen.constant ConcurrentCache
            1, Gen.constant UsingOutlived
            1, Gen.constant ProcessExitHandler
            1, Gen.constant AsyncLambdaVoid
            1, Gen.constant SingleTaskCombinator
            1, Gen.constant UndisposedLocal
            1, Gen.constant DisposableDesign
            1, Gen.constant SwallowingCatch
            1, Gen.constant ThrowPlaces
            1, Gen.map ConstantMessage genWord
            1, Gen.constant UninformedCatch
            1, Gen.constant SmallRecord
            1, Gen.constant InitCandidate
            1, Gen.constant PublicStatic
            1, Gen.constant TypeByName
            1, Gen.constant VirtualInCtor
            1, Gen.constant EnumByText
            1, Gen.constant ClockSlot
            1, Gen.constant ToStringHole
            1, Gen.constant Emptiness
            1, Gen.constant CultureFreeParse
            1, Gen.constant LocalNow
            1, Gen.constant InvalidPattern
            2, Gen.map PlainTextRegex (Gen.zip (Gen.choose (0, 7)) genWord)
            1, Gen.constant ClientPerCall
            1, Gen.constant HandJoinedPath
            1, Gen.constant HiddenUnicode
            1, Gen.constant NearCeiling
            1, Gen.constant LogTemplate
            1, Gen.constant SqlText
            1, Gen.constant CommandLine
            1, Gen.constant Secrets
            1, Gen.constant Crypto
            1, Gen.constant AttributeLists
            1, Gen.map TrailingNote genWord
            1, Gen.constant LengthChain
            1, Gen.map Utf8Literal genWord
            1, Gen.constant RequiredCandidate
            1, Gen.constant FrozenCandidate
            1, Gen.constant ParamsSpan
            1, Gen.constant LockGate
            1, Gen.constant FieldKeyword
            1, Gen.constant NullConditionalAssignment
            1, Gen.constant ExtensionBlock
            1, Gen.constant UnreachableDiscard
            1, Gen.constant StructCopy
            1, Gen.constant DroppedTimer
            1, Gen.constant UnguardedSemaphore
            1, Gen.constant StaticLazyInit
            1, Gen.constant LostInner
            1, Gen.constant ParseTry
            1, Gen.constant FloatEquality
            1, Gen.constant IntDivision
            1, Gen.constant MixedKinds
            1, Gen.constant UnobservedToken
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
        | HoistedReturn(e, form) -> for e' in shrinkBool e -> HoistedReturn(e', form)
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
        "CR0172"
        "CR0173"
        "CR0174"
        "CR0175"
        "CR0176"
        "CR0177"
        "CR0080"
        "CR0082"
        "CR0100"
        "CR0101"
        "CR0109"
        "CR0171"
        "CR0002"
        "CR0003"
        "CR0006"
        "CR0010"
        "CR0012"
        "CR0013"
        "CR0014"
        "CR0016"
        // CR0017 is not listed: CR0160 wins every shape it reads (the ForCapture
        // shape reaches CR0160), so the note never surfaces on its own
        "CR0022"
        "CR0023"
        "CR0025"
        "CR0026"
        "CR0027"
        "CR0028"
        "CR0030"
        "CR0032"
        "CR0034"
        "CR0035"
        "CR0040"
        "CR0041"
        "CR0042"
        "CR0043"
        "CR0044"
        "CR0045"
        "CR0047"
        "CR0049"
        "CR0050"
        "CR0051"
        "CR0052"
        "CR0053"
        "CR0054"
        "CR0060"
        "CR0061"
        "CR0062"
        "CR0063"
        "CR0064"
        "CR0066"
        "CR0067"
        "CR0068"
        "CR0069"
        "CR0070"
        "CR0081"
        "CR0083"
        "CR0084"
        "CR0085"
        "CR0086"
        "CR0087"
        "CR0089"
        "CR0102"
        "CR0104"
        "CR0105"
        "CR0106"
        "CR0107"
        "CR0108"
        "CR0110"
        "CR0111"
        "CR0112"
        "CR0113"
        "CR0114"
        "CR0115"
        "CR0120"
        "CR0121"
        "CR0122"
        "CR0123"
        "CR0124"
        "CR0125"
        "CR0126"
        "CR0142"
        "CR0146"
        "CR0147"
        "CR0148"
        "CR0149"
        "CR0150"
        "CR0151"
        "CR0152"
        "CR0153"
        "CR0154"
        "CR0155"
        "CR0157"
        "CR0160"
        "CR0161"
        "CR0162"
        "CR0163"
        "CR0164"
        "CR0165"
        "CR0166"
        "CR0167"
        "CR0168"
        "CR0169"
        "CR0170"
    ]

/// Instances of every shape, built by reflection over the union so a shape
/// added later is in the list without anyone remembering to add it. A field
/// takes every value of a small set — both flags, a few boolean terms of
/// the kinds the boolean rules read — and the shape appears once per
/// combination.
let exemplars: Shape list =
    let samplesFor (t: System.Type) : obj list =
        if t = typeof<BoolExpr> then
            [
                box (Cmp(Gt, IVar 0, ILit 1))
                box (Not(Cmp(Gt, IVar 0, ILit 1)))
                box (EqLit(Cmp(Lt, IVar 1, ILit 2), true))
                box (And(Lit true, Cmp(Eq, IVar 0, IVar 2)))
                box (Or(Cmp(Eq, IVar 0, IVar 2), Cmp(Eq, IVar 0, IVar 2)))
            ]
        elif t = typeof<bool> then
            [ box true; box false ]
        elif t = typeof<string> then
            [ box "abc" ]
        elif t = typeof<int> then
            // a form selector: every form the printer knows
            [ for i in 0..7 -> box i ]
        else
            failwithf "no exemplar for a shape field of type %s" t.Name

    let rec combinations (fields: obj list list) : obj list list =
        match fields with
        | [] -> [ [] ]
        | first :: rest ->
            [
                for v in first do
                    for tail in combinations rest -> v :: tail
            ]

    Microsoft.FSharp.Reflection.FSharpType.GetUnionCases typeof<Shape>
    |> Array.toList
    |> List.collect (fun case ->
        case.GetFields()
        |> Array.map (fun f -> samplesFor f.PropertyType)
        |> List.ofArray
        |> combinations
        |> List.map (fun args -> Microsoft.FSharp.Reflection.FSharpValue.MakeUnion(case, Array.ofList args) :?> Shape))
