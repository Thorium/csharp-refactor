// The performance claims, measured: one before/after pair per rule whose
// fix claims speed or allocation, on both axes. The contract: a performance
// rule's rewrite must win on at least one axis, an idiom rule's must hold
// parity, and a slower-but-idiomatic shape goes behind a default-off knob
// rather than into the default. Fixtures are built at runtime (no interned
// literals, no constant folding), and the in-process toolchain keeps the run
// inside this process.
using System.Collections.Frozen;
using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.Emit;

namespace CSharp.Refactor.PerfClaims;

public class Config : ManualConfig
{
    public Config()
    {
        AddJob(Job.ShortRun.WithToolchain(InProcessEmitToolchain.Instance));
        AddDiagnoser(BenchmarkDotNet.Diagnosers.MemoryDiagnoser.Default);
    }
}

static class Fixtures
{
    public static readonly int[] Ints = Enumerable.Range(0, 1000).Select(i => i * 7 % 1000).ToArray();
    public static readonly List<int> IntList = Ints.ToList();
    public static IEnumerable<int> IntSeq => Ints.Select(x => x); // a real IEnumerable<T>, not a collection
    public static readonly string[] Words = Enumerable.Range(0, 1000).Select(i => "w" + i).ToArray();
    public static readonly List<string> WordList = Words.ToList();
    public static readonly Dictionary<string, int> Dict = Words.ToDictionary(w => w, w => w.Length);
}

/// <summary>CR0020: an eager copy before a consumer or a lazy stage.</summary>
[Config(typeof(Config))]
public class CR0020_ConversionMove
{
    [Benchmark(Baseline = true)] public bool AnyAfterToList() => Fixtures.IntSeq.ToList().Any(x => x > 990);
    [Benchmark] public bool AnyDirect() => Fixtures.IntSeq.Any(x => x > 990);
    [Benchmark] public List<int> WhereAfterToList() => Fixtures.IntSeq.ToList().Where(x => x % 2 == 0).ToList();
    [Benchmark] public List<int> WhereThenToList() => Fixtures.IntSeq.Where(x => x % 2 == 0).ToList();
    [Benchmark] public List<int> SelectAfterToList() => Fixtures.IntSeq.ToList().Select(x => x + 1).ToList();
    [Benchmark] public List<int> SelectThenToList() => Fixtures.IntSeq.Select(x => x + 1).ToList();
    [Benchmark] public int ForeachOverToList() { var n = 0; foreach (var x in Fixtures.IntSeq.ToList()) n += x; return n; }
    [Benchmark] public int ForeachDirect() { var n = 0; foreach (var x in Fixtures.IntSeq) n += x; return n; }
}

/// <summary>CR0021: a running sum or count against Sum/Count (idiom: parity wanted).</summary>
[Config(typeof(Config))]
public class CR0021_Aggregate
{
    [Benchmark(Baseline = true)] public long LoopSumArray() { long t = 0; foreach (var x in Fixtures.Ints) t += x; return t; }
    [Benchmark] public long SumArray() => Fixtures.Ints.Sum(x => (long)x);
    [Benchmark] public int LoopSumIntArray() { int t = 0; foreach (var x in Fixtures.Ints) t += x; return t; }
    [Benchmark] public int SumIntArray() => Fixtures.Ints.Sum();
    [Benchmark] public int LoopSumList() { int t = 0; foreach (var x in Fixtures.IntList) t += x; return t; }
    [Benchmark] public int SumList() => Fixtures.IntList.Sum();
    [Benchmark] public int LoopSumSeq() { int t = 0; foreach (var x in Fixtures.IntSeq) t += x; return t; }
    [Benchmark] public int SumSeq() => Fixtures.IntSeq.Sum();
    [Benchmark] public int LoopCount() { int n = 0; foreach (var x in Fixtures.Ints) if (x % 3 == 0) n++; return n; }
    [Benchmark] public int CountPredicate() => Fixtures.Ints.Count(x => x % 3 == 0);
    [Benchmark] public string LoopConcat() { var s = ""; foreach (var w in Fixtures.Words) s += w; return s; }
    [Benchmark] public string StringConcat() => string.Concat(Fixtures.Words);
}

/// <summary>CR0022: a flag loop against Any/All (idiom: parity wanted; List boxing measured).</summary>
[Config(typeof(Config))]
public class CR0022_Flag
{
    [Benchmark(Baseline = true)] public bool LoopFlagArray() { var found = false; foreach (var x in Fixtures.Ints) if (x == 999 * 7 % 1000) found = true; return found; }
    [Benchmark] public bool AnyArray() => Fixtures.Ints.Any(x => x == 999 * 7 % 1000);
    [Benchmark] public bool LoopFlagList() { var found = false; foreach (var x in Fixtures.IntList) if (x == 999 * 7 % 1000) found = true; return found; }
    [Benchmark] public bool AnyList() => Fixtures.IntList.Any(x => x == 999 * 7 % 1000);
    [Benchmark] public bool LoopFlagBreak() { var found = false; foreach (var x in Fixtures.Ints) { if (x == 7) { found = true; break; } } return found; }
    [Benchmark] public bool AnyEarly() => Fixtures.Ints.Any(x => x == 7);
}

/// <summary>CR0023: Contains on a literal array against a HashSet and a FrozenSet, per probe.</summary>
[Config(typeof(Config))]
public class CR0023_ContainsSet
{
    static readonly string[] Allowed3 = { "alpha", "beta", "gamma" };
    static readonly HashSet<string> Hash3 = new(Allowed3);
    static readonly FrozenSet<string> Frozen3 = Allowed3.ToFrozenSet();
    static readonly string[] Allowed20 = Enumerable.Range(0, 20).Select(i => "k" + i).ToArray();
    static readonly HashSet<string> Hash20 = new(Allowed20);
    static readonly FrozenSet<string> Frozen20 = Allowed20.ToFrozenSet();

    [Benchmark(Baseline = true)] public int Array3() => Fixtures.Words.Count(w => Allowed3.Contains(w));
    [Benchmark] public int HashSet3() => Fixtures.Words.Count(w => Hash3.Contains(w));
    [Benchmark] public int FrozenSet3() => Fixtures.Words.Count(w => Frozen3.Contains(w));
    [Benchmark] public int Array20() => Fixtures.Words.Count(w => Allowed20.Contains(w));
    [Benchmark] public int HashSet20() => Fixtures.Words.Count(w => Hash20.Contains(w));
    [Benchmark] public int FrozenSet20() => Fixtures.Words.Count(w => Frozen20.Contains(w));
    [Benchmark] public FrozenSet<string> BuildFrozen20() => Allowed20.ToFrozenSet();
    [Benchmark] public HashSet<string> BuildHash20() => new(Allowed20);
}

/// <summary>CR0024: Add per element against AddRange.</summary>
[Config(typeof(Config))]
public class CR0024_AddRange
{
    [Benchmark(Baseline = true)] public List<int> AddEach() { var acc = new List<int>(); foreach (var x in Fixtures.Ints) acc.Add(x); return acc; }
    [Benchmark] public List<int> AddRange() { var acc = new List<int>(); acc.AddRange(Fixtures.Ints); return acc; }
    [Benchmark] public List<int> AddEachSeq() { var acc = new List<int>(); foreach (var x in Fixtures.IntSeq) acc.Add(x); return acc; }
    [Benchmark] public List<int> AddRangeSeq() { var acc = new List<int>(); acc.AddRange(Fixtures.IntSeq); return acc; }
}

/// <summary>CR0028: a fill loop against Where/Select/ToList (decides the default).</summary>
[Config(typeof(Config))]
public class CR0028_FillLoop
{
    [Benchmark(Baseline = true)] public List<int> LoopArray() { var r = new List<int>(); foreach (var x in Fixtures.Ints) if (x % 2 == 0) r.Add(x * 3); return r; }
    [Benchmark] public List<int> PipelineArray() => Fixtures.Ints.Where(x => x % 2 == 0).Select(x => x * 3).ToList();
    [Benchmark] public List<int> LoopList() { var r = new List<int>(); foreach (var x in Fixtures.IntList) if (x % 2 == 0) r.Add(x * 3); return r; }
    [Benchmark] public List<int> PipelineList() => Fixtures.IntList.Where(x => x % 2 == 0).Select(x => x * 3).ToList();
    [Benchmark] public List<int> LoopSeq() { var r = new List<int>(); foreach (var x in Fixtures.IntSeq) if (x % 2 == 0) r.Add(x * 3); return r; }
    [Benchmark] public List<int> PipelineSeq() => Fixtures.IntSeq.Where(x => x % 2 == 0).Select(x => x * 3).ToList();
    [Benchmark] public List<int> LoopSelectOnly() { var r = new List<int>(); foreach (var x in Fixtures.Ints) r.Add(x * 3); return r; }
    [Benchmark] public List<int> SelectOnly() => Fixtures.Ints.Select(x => x * 3).ToList();
}

/// <summary>CR0029: two Selects against one (idiom: parity wanted).</summary>
[Config(typeof(Config))]
public class CR0029_SelectFusion
{
    [Benchmark(Baseline = true)] public int TwoSelects() => Fixtures.Ints.Select(x => x + 1).Select(y => y * 2).Sum();
    [Benchmark] public int OneSelect() => Fixtures.Ints.Select(x => (x + 1) * 2).Sum();
    [Benchmark] public int TwoSelectsSeq() => Fixtures.IntSeq.Select(x => x + 1).Select(y => y * 2).Sum();
    [Benchmark] public int OneSelectSeq() => Fixtures.IntSeq.Select(x => (x + 1) * 2).Sum();
}

/// <summary>CR0031: a Random per call against Random.Shared.</summary>
[Config(typeof(Config))]
public class CR0031_Random
{
    [Benchmark(Baseline = true)] public int NewRandom() => new Random().Next(100);
    [Benchmark] public int Shared() => Random.Shared.Next(100);
}

/// <summary>CR0032: Keys with a lookup per key against the pairs.</summary>
[Config(typeof(Config))]
public class CR0032_DictionaryPairs
{
    [Benchmark(Baseline = true)] public int KeysThenLookup() { int t = 0; foreach (var k in Fixtures.Dict.Keys) t += Fixtures.Dict[k] + k.Length; return t; }
    [Benchmark] public int Pairs() { int t = 0; foreach (var (k, v) in Fixtures.Dict) t += v + k.Length; return t; }
}

/// <summary>CR0033: Append of a concatenation against the pieces.</summary>
[Config(typeof(Config))]
public class CR0033_AppendChain
{
    readonly string a = "left-" + Environment.ProcessId;
    readonly string b = "right-" + Environment.ProcessId;
    readonly int n = Environment.ProcessId;

    [Benchmark(Baseline = true)] public int Concatenated() { var sb = new StringBuilder(); for (int i = 0; i < 100; i++) sb.Append(a + n + b); return sb.Length; }
    [Benchmark] public int Pieces() { var sb = new StringBuilder(); for (int i = 0; i < 100; i++) sb.Append(a).Append(n).Append(b); return sb.Length; }
}

public static class Program
{
    public static int Main(string[] args)
    {
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
        return 0;
    }
}
