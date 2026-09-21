// The pairs behind the performance rules after CR0033 (the M3 families and
// family J), on the same contract as Program.cs: a performance rule's
// rewrite must win on one axis, at parity on the other.
//
// Rules of the category with no runtime pair, and why:
//   CR0045 — a test blocking on a task (`.Result`, `.Wait()`) is a thread-pool
//            hazard, not a micro-cost: the claim is deadlock and starvation.
//   CR0054 — `Task.WhenAll(new[] { t })` is one array and one wrapper task
//            per call; measurable but not worth a pair to say so.
// A rule missing from this file and from that list is a claim unmeasured:
// PerfClaimsTests keeps the accounting honest.
using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using BenchmarkDotNet.Attributes;

namespace CSharp.Refactor.PerfClaims;

/// <summary>CR0025: growing an array per element against a List.</summary>
[Config(typeof(Config))]
public class CR0025_ArrayGrowth
{
    [Benchmark(Baseline = true)] public int AppendToArray() { var arr = Array.Empty<int>(); foreach (var x in Fixtures.Ints) arr = arr.Append(x).ToArray(); return arr.Length; }
    [Benchmark] public int ArrayResize() { var arr = Array.Empty<int>(); foreach (var x in Fixtures.Ints) { Array.Resize(ref arr, arr.Length + 1); arr[^1] = x; } return arr.Length; }
    [Benchmark] public int ListAdd() { var list = new List<int>(); foreach (var x in Fixtures.Ints) list.Add(x); return list.Count; }
    [Benchmark] public int ListAddThenToArray() { var list = new List<int>(); foreach (var x in Fixtures.Ints) list.Add(x); return list.ToArray().Length; }
}

/// <summary>CR0026: Count/ElementAt on a bare IEnumerable inside a loop against a materialised list.</summary>
[Config(typeof(Config))]
public class CR0026_EnumerableInLoop
{
    [Benchmark(Baseline = true)] public int CountPerIteration() { var seq = Fixtures.IntSeq; int t = 0; for (int i = 0; i < seq.Count(); i++) t += seq.ElementAt(i); return t; }
    [Benchmark] public int MaterialisedOnce() { var list = Fixtures.IntSeq.ToList(); int t = 0; for (int i = 0; i < list.Count; i++) t += list[i]; return t; }
}

/// <summary>CR0035: a recursive yield against an explicit stack, over a tree of 781 nodes, five deep.</summary>
[Config(typeof(Config))]
public class CR0035_RecursiveYield
{
    sealed class Node { public int Value; public List<Node> Children = new(); }
    static readonly Node Root = Build(0);
    static Node Build(int depth) { var n = new Node { Value = depth }; if (depth < 4) for (int i = 0; i < 5; i++) n.Children.Add(Build(depth + 1)); return n; }

    static IEnumerable<Node> WalkRecursive(Node n) { yield return n; foreach (var c in n.Children) foreach (var d in WalkRecursive(c)) yield return d; }
    static IEnumerable<Node> WalkStack(Node root) { var stack = new Stack<Node>(); stack.Push(root); while (stack.Count > 0) { var n = stack.Pop(); yield return n; for (int i = n.Children.Count - 1; i >= 0; i--) stack.Push(n.Children[i]); } }

    [Benchmark(Baseline = true)] public int Recursive() { int t = 0; foreach (var n in WalkRecursive(Root)) t += n.Value; return t; }
    [Benchmark] public int ExplicitStack() { int t = 0; foreach (var n in WalkStack(Root)) t += n.Value; return t; }
}

/// <summary>CR0046: an async method that only awaits and returns, against handing the task through.</summary>
[Config(typeof(Config))]
public class CR0046_AsyncElision
{
    static Task<int> Inner(int x) => Task.FromResult(x + 1);
    static async Task<int> Wrapped(int x) => await Inner(x);
    static Task<int> Elided(int x) => Inner(x);

    [Benchmark(Baseline = true)] public int AwaitAndReturn() { int t = 0; for (int i = 0; i < 100; i++) t += Wrapped(i).Result; return t; }
    [Benchmark] public int TaskThrough() { int t = 0; for (int i = 0; i < 100; i++) t += Elided(i).Result; return t; }
}

/// <summary>CR0081: a record of small fields against a readonly record struct: construction and equality.</summary>
[Config(typeof(Config))]
public class CR0081_RecordStruct
{
    record Point(int X, int Y);
    readonly record struct PointStruct(int X, int Y);

    [Benchmark(Baseline = true)] public int RecordClass() { int hits = 0; for (int i = 0; i < 1000; i++) { var p = new Point(i, i * 2); var q = new Point(i, i * 2); if (p == q) hits++; } return hits; }
    [Benchmark] public int RecordStruct() { int hits = 0; for (int i = 0; i < 1000; i++) { var p = new PointStruct(i, i * 2); var q = new PointStruct(i, i * 2); if (p == q) hits++; } return hits; }
}

/// <summary>CR0082: Tuple against ValueTuple: construction and element reads.</summary>
[Config(typeof(Config))]
public class CR0082_ValueTuple
{
    static Tuple<int, string> MakeRef(int i) => Tuple.Create(i, Fixtures.Words[i]);
    static (int, string) MakeValue(int i) => (i, Fixtures.Words[i]);

    [Benchmark(Baseline = true)] public int ReferenceTuple() { int t = 0; for (int i = 0; i < 1000; i++) { var p = MakeRef(i); t += p.Item1 + p.Item2.Length; } return t; }
    [Benchmark] public int ValueTuple() { int t = 0; for (int i = 0; i < 1000; i++) { var (a, b) = MakeValue(i); t += a + b.Length; } return t; }
}

/// <summary>CR0087: an enum compared through its name against the value.</summary>
[Config(typeof(Config))]
public class CR0087_EnumToString
{
    enum Status { Inactive, Active, Suspended }
    static readonly Status[] Statuses = Enumerable.Range(0, 1000).Select(i => (Status)(i % 3)).ToArray();

    [Benchmark(Baseline = true)] public int ByName() { int n = 0; foreach (var s in Statuses) if (s.ToString() == "Active") n++; return n; }
    [Benchmark] public int ByValue() { int n = 0; foreach (var s in Statuses) if (s == Status.Active) n++; return n; }
}

/// <summary>CR0102: ToString() inside an interpolation hole against the bare hole.</summary>
[Config(typeof(Config))]
public class CR0102_HoleToString
{
    readonly int count = Environment.ProcessId;
    readonly Guid id = Guid.NewGuid();

    [Benchmark(Baseline = true)] public string IntToString() => $"{count.ToString()} items";
    [Benchmark] public string IntHole() => $"{count} items";
    [Benchmark] public string GuidToString() => $"id {id.ToString()}";
    [Benchmark] public string GuidHole() => $"id {id}";
    [Benchmark] public string JoinToString() => string.Join(", ", Fixtures.Ints.Select(x => x.ToString()));
    [Benchmark] public string JoinBare() => string.Join(", ", Fixtures.Ints);
}

/// <summary>CR0108: a regex over plain text against the string operation.</summary>
[Config(typeof(Config))]
public class CR0108_PlainTextRegex
{
    static readonly string Subject = "ON CONFLICT (id) DO UPDATE SET name = excluded.name; INSERT ... ON CONFLICT DO NOTHING " + Environment.ProcessId;

    [Benchmark(Baseline = true)] public bool RegexIsMatch() => Regex.IsMatch(Subject, "excluded");
    [Benchmark] public bool Contains() => Subject.Contains("excluded");
    [Benchmark] public bool RegexStart() => Regex.IsMatch(Subject, "^ON");
    [Benchmark] public bool StartsWith() => Subject.StartsWith("ON", StringComparison.Ordinal);
    [Benchmark] public string RegexReplace() => Regex.Replace(Subject, "excluded", "new");
    [Benchmark] public string Replace() => Subject.Replace("excluded", "new");
    [Benchmark] public int RegexMatchesCount() => Regex.Matches(Subject, "ON CONFLICT").Count;
    [Benchmark] public int SpanCount() => Subject.AsSpan().Count("ON CONFLICT");
    [Benchmark] public int RegexSplit() => Regex.Split(Subject, "; ").Length;
    [Benchmark] public int Split() => Subject.Split("; ").Length;
}

/// <summary>CR0109: a regex built per call against a static instance and a generated one.</summary>
[Config(typeof(Config))]
public partial class CR0109_RegexHoist
{
    static readonly string Subject = "Error Code 4711 at step " + Environment.ProcessId;
    static readonly Regex Hoisted = new(@"Error Code (\d+)");
    [GeneratedRegex(@"Error Code (\d+)")] private static partial Regex Generated();

    [Benchmark(Baseline = true)] public bool StaticCallPerCall() => Regex.IsMatch(Subject, @"Error Code (\d+)");
    [Benchmark] public bool ConstructedPerCall() => new Regex(@"Error Code (\d+)").IsMatch(Subject);
    [Benchmark] public bool StaticField() => Hoisted.IsMatch(Subject);
    [Benchmark] public bool GeneratedRegex() => Generated().IsMatch(Subject);
}

/// <summary>CR0110: a hash algorithm or serializer options created per iteration against one instance; the client per call is a socket question, not a benchmark.</summary>
[Config(typeof(Config))]
public class CR0110_PerCall
{
    static readonly byte[] Payload = Encoding.UTF8.GetBytes("payload " + Environment.ProcessId);

    [Benchmark(Baseline = true)] public int Sha256PerIteration() { int t = 0; for (int i = 0; i < 100; i++) { using var sha = System.Security.Cryptography.SHA256.Create(); t += sha.ComputeHash(Payload)[0]; } return t; }
    [Benchmark] public int Sha256Hoisted() { using var sha = System.Security.Cryptography.SHA256.Create(); int t = 0; for (int i = 0; i < 100; i++) t += sha.ComputeHash(Payload)[0]; return t; }
    [Benchmark] public int Sha256Static() { int t = 0; for (int i = 0; i < 100; i++) t += System.Security.Cryptography.SHA256.HashData(Payload)[0]; return t; }
    [Benchmark] public int JsonOptionsPerIteration() { int t = 0; for (int i = 0; i < 100; i++) t += System.Text.Json.JsonSerializer.Serialize(i, new System.Text.Json.JsonSerializerOptions { WriteIndented = false }).Length; return t; }
    [Benchmark] public int JsonOptionsHoisted() { var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = false }; int t = 0; for (int i = 0; i < 100; i++) t += System.Text.Json.JsonSerializer.Serialize(i, options).Length; return t; }
}

/// <summary>CR0148: UTF-8 bytes of a constant through the encoder against the u8 literal.</summary>
[Config(typeof(Config))]
public class CR0148_Utf8Literal
{
    [Benchmark(Baseline = true)] public int EncodeConstant() => Encoding.UTF8.GetBytes("Content-Type: application/json").Length;
    [Benchmark] public int U8ToArray() => "Content-Type: application/json"u8.ToArray().Length;
    [Benchmark] public int U8Span() => "Content-Type: application/json"u8.Length;
}

/// <summary>CR0150: a read-only static Dictionary/HashSet against the frozen ones.</summary>
[Config(typeof(Config))]
public class CR0150_Frozen
{
    static readonly Dictionary<string, int> Dict = Fixtures.Words.Take(50).ToDictionary(w => w, w => w.Length);
    static readonly FrozenDictionary<string, int> Frozen = Dict.ToFrozenDictionary();
    static readonly HashSet<string> Set = new(Fixtures.Words.Take(50));
    static readonly FrozenSet<string> FrozenSetOf = Set.ToFrozenSet();

    [Benchmark(Baseline = true)] public int DictionaryLookups() { int t = 0; foreach (var w in Fixtures.Words) if (Dict.TryGetValue(w, out var n)) t += n; return t; }
    [Benchmark] public int FrozenDictionaryLookups() { int t = 0; foreach (var w in Fixtures.Words) if (Frozen.TryGetValue(w, out var n)) t += n; return t; }
    [Benchmark] public int HashSetProbes() => Fixtures.Words.Count(Set.Contains);
    [Benchmark] public int FrozenSetProbes() => Fixtures.Words.Count(FrozenSetOf.Contains);
    [Benchmark] public FrozenDictionary<string, int> BuildFrozen50() => Dict.ToFrozenDictionary();
}

/// <summary>CR0151: params T[] against params ReadOnlySpan&lt;T&gt; at a call of three arguments.</summary>
[Config(typeof(Config))]
public class CR0151_ParamsSpan
{
    static readonly Dictionary<string, string> Info = new() { ["c"] = "found" };
    static string? FirstArray(Dictionary<string, string> info, params string[] keys) { foreach (var k in keys) if (info.TryGetValue(k, out var v)) return v; return null; }
    static string? FirstSpan(Dictionary<string, string> info, params ReadOnlySpan<string> keys) { foreach (var k in keys) if (info.TryGetValue(k, out var v)) return v; return null; }

    [Benchmark(Baseline = true)] public int ParamsArray() { int t = 0; for (int i = 0; i < 100; i++) t += FirstArray(Info, "a", "b", "c")!.Length; return t; }
    [Benchmark] public int ParamsSpan() { int t = 0; for (int i = 0; i < 100; i++) t += FirstSpan(Info, "a", "b", "c")!.Length; return t; }
}

/// <summary>CR0152: lock on an object against the Lock type, uncontended.</summary>
[Config(typeof(Config))]
public class CR0152_LockType
{
    readonly object gate = new();
    readonly Lock typedGate = new();
    int counter;

    [Benchmark(Baseline = true)] public int ObjectLock() { for (int i = 0; i < 1000; i++) lock (gate) counter++; return counter; }
    [Benchmark] public int TypedLock() { for (int i = 0; i < 1000; i++) lock (typedGate) counter++; return counter; }
}

/// <summary>CR0166: Parse under a catch against TryParse, on the failing input the catch is for and on a passing one.</summary>
[Config(typeof(Config))]
public class CR0166_TryParse
{
    static readonly string Bad = "n" + Environment.ProcessId;
    static readonly string Good = Environment.ProcessId.ToString();

    [Benchmark(Baseline = true)] public int ParseCatchFailing() { try { return int.Parse(Bad); } catch (FormatException) { return -1; } }
    [Benchmark] public int TryParseFailing() => int.TryParse(Bad, out var v) ? v : -1;
    [Benchmark] public int ParseCatchPassing() { try { return int.Parse(Good); } catch (FormatException) { return -1; } }
    [Benchmark] public int TryParsePassing() => int.TryParse(Good, out var v) ? v : -1;
}

/// <summary>CR0174: a Substring handed to a span-reading consumer against AsSpan.</summary>
[Config(typeof(Config))]
public class CR0174_SubstringToSpan
{
    static readonly string Line = "ORDER-" + Environment.ProcessId.ToString("D6") + "-rest of the line";

    [Benchmark(Baseline = true)] public int ParseSubstring() => int.Parse(Line.Substring(6, 6));
    [Benchmark] public int ParseSpan() => int.Parse(Line.AsSpan(6, 6));
    [Benchmark] public int AppendSubstring() { var sb = new StringBuilder(); for (int i = 0; i < 100; i++) sb.Append(Line.Substring(6)); return sb.Length; }
    [Benchmark] public int AppendSpan() { var sb = new StringBuilder(); for (int i = 0; i < 100; i++) sb.Append(Line.AsSpan(6)); return sb.Length; }
}

/// <summary>CR0175: a prefix cut out to be compared against StartsWith, ordinal.</summary>
[Config(typeof(Config))]
public class CR0175_PrefixCompare
{
    static readonly string Line = "ORDER-" + Environment.ProcessId;
    static readonly string Other = "INVOICE-" + Environment.ProcessId;

    [Benchmark(Baseline = true)] public bool SubstringEquals() => Line.Length >= 6 && Line.Substring(0, 6) == "ORDER-";
    [Benchmark] public bool StartsWithOrdinal() => Line.Length >= 6 && Line.StartsWith("ORDER-", StringComparison.Ordinal);
    [Benchmark] public bool SubstringEqualsMiss() => Other.Length >= 6 && Other.Substring(0, 6) == "ORDER-";
    [Benchmark] public bool StartsWithOrdinalMiss() => Other.Length >= 6 && Other.StartsWith("ORDER-", StringComparison.Ordinal);
    [Benchmark] public bool SuffixSubstringEquals() => Line.Length >= 3 && Line.Substring(Line.Length - 3) == "123";
    [Benchmark] public bool EndsWithOrdinal() => Line.Length >= 3 && Line.EndsWith("123", StringComparison.Ordinal);
}

/// <summary>CR0176: a foreach over ToCharArray against the string; LINQ over both, to see which the copy serves.</summary>
[Config(typeof(Config))]
public class CR0176_CharArray
{
    static readonly string Text = string.Concat(Enumerable.Repeat("abc123 ", 20)) + Environment.ProcessId;

    [Benchmark(Baseline = true)] public int ForeachArray() { int n = 0; foreach (var c in Text.ToCharArray()) if (c == '1') n++; return n; }
    [Benchmark] public int ForeachString() { int n = 0; foreach (var c in Text) if (c == '1') n++; return n; }
    [Benchmark] public bool AnyArray() => Text.ToCharArray().Any(char.IsDigit);
    [Benchmark] public bool AnyString() => Text.Any(char.IsDigit);
    [Benchmark] public int CountArray() => Text.ToCharArray().Count(c => c == '1');
    [Benchmark] public int CountString() => Text.Count(c => c == '1');
}

/// <summary>CR0177: a loop-invariant local computed per iteration against the same binding hoisted above the loop.</summary>
[Config(typeof(Config))]
public class CR0177_LoopInvariant
{
    readonly int[] xs = Enumerable.Range(0, 1000).ToArray();
    readonly int a = Environment.ProcessId;
    int field = Environment.ProcessId;
    readonly string prefix = "item-" + Environment.ProcessId;
    int sink;

    [MethodImpl(MethodImplOptions.NoInlining)] void Sink(int v) => sink += v;
    [MethodImpl(MethodImplOptions.NoInlining)] void Sink(string v) => sink += v.Length;

    [Benchmark(Baseline = true)] public int LocalArithmeticInLoop() { foreach (var x in xs) { var c = a * 3 + 1; Sink(x + c); } return sink; }
    [Benchmark] public int LocalArithmeticHoisted() { var c = a * 3 + 1; foreach (var x in xs) { Sink(x + c); } return sink; }
    [Benchmark] public int FieldArithmeticInLoop() { foreach (var x in xs) { var c = field * 3 + 1; Sink(x + c); } return sink; }
    [Benchmark] public int FieldArithmeticHoisted() { var c = field * 3 + 1; foreach (var x in xs) { Sink(x + c); } return sink; }
    [Benchmark] public int StringConcatInLoop() { foreach (var x in xs) { var label = prefix + ":"; Sink(label); } return sink; }
    [Benchmark] public int StringConcatHoisted() { var label = prefix + ":"; foreach (var x in xs) { Sink(label); } return sink; }
}
