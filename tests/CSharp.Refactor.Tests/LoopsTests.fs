module CSharp.Refactor.Tests.LoopsTests

open Xunit
open CSharp.Refactor.Tests.Harness

// ---- CR0015 ----

[<Fact>]
let ``an index that only reads becomes a foreach, the alias line naming the element`` () =
    let source =
        """
using System;
using System.Collections.Generic;
class C
{
    readonly int[] fixedOnes = { 1, 2 };
    int Sum(int[] xs)
    {
        int total = 0;
        for (int i = 0; i < xs.Length; i++) total += xs[i];
        return total;
    }
    int Pages(string[] pages)
    {
        int total = 0;
        for (int i = 0; i < pages.Length; i++) total += pages[i].Length;
        return total;
    }
    void Print(List<string> names)
    {
        for (int i = 0; i < names.Count; i++)
        {
            var name = names[i];
            Console.WriteLine(name + names[i].Length);
        }
    }
    void Reassigned(int[] xs) { for (int i = 0; i < xs.Length; i++) { var e = xs[i]; e += 1; Console.WriteLine(e); } }
    int Field()
    {
        int total = 0;
        for (var i = 0; i <= fixedOnes.Length - 1; i += 1)
        {
            if (fixedOnes[i] > 1) continue;
            total += fixedOnes[i];
        }
        return total;
    }
}
"""

    let fired = suggestCode "CR0015" source
    Assert.Equal(5, fired.Length)
    let fixedSource = fixAll "CR0015" source
    Assert.Contains("foreach (var item in xs) total += item;", fixedSource)
    Assert.Contains("foreach (var page in pages) total += page.Length;", fixedSource)
    // a reassigned alias stays a local: the element is copied into it
    Assert.Contains("foreach (var item in xs) { var e = item; e += 1; Console.WriteLine(e); }", fixedSource)

    Assert.Contains(
        normalize
            """        foreach (var name in names)
        {
            Console.WriteLine(name + name.Length);
        }""",
        fixedSource
    )

    Assert.Contains(
        normalize
            """        foreach (var fixedOne in fixedOnes)
        {
            if (fixedOne > 1) continue;
            total += fixedOne;
        }""",
        fixedSource
    )

[<Fact>]
let ``a written element, an index used as a value, a mutated list, a property source, a ref use and a struct member call stand down``
    ()
    =
    let source =
        """
using System;
using System.Collections.Generic;
struct P { public int X; public void Bump() { X++; } }
class C
{
    List<int> Items { get; } = new List<int>();
    void A(int[] xs) { for (int i = 0; i < xs.Length; i++) xs[i] = 0; }
    void B(int[] xs) { for (int i = 0; i < xs.Length; i++) Console.WriteLine(i + xs[i]); }
    void D(List<int> xs) { for (int i = 0; i < xs.Count; i++) { if (xs[i] > 3) xs.RemoveAt(i); } }
    void E() { for (int i = 0; i < Items.Count; i++) Console.WriteLine(Items[i]); }
    void F(int[] xs) { for (int i = 0; i < xs.Length; i++) { ref var e = ref xs[i]; e++; } }
    void H(P[] ps) { for (int i = 0; i < ps.Length; i++) ps[i].Bump(); }
    void I(List<int> xs) { for (int i = 0; i < xs.Count; i++) Use(xs, xs[i]); }
    void Use(List<int> xs, int x) { xs.Add(x); }
}
"""

    Assert.Equal<string list>([], firedText source (suggestCode "CR0015" source))

// ---- CR0017 ----

[<Fact>]
let ``a stored, returned, threaded or deferred closure over the for variable is noted`` () =
    let source =
        """
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
class C
{
    Action pending;
    event Action Fired;
    IEnumerable<Func<int>> A()
    {
        for (int i = 0; i < 3; i++) yield return () => i;
        for (int i = 0; i < 3; i++) Task.Run(() => Console.WriteLine(i));
        for (int i = 0; i < 3; i++) pending = () => Console.WriteLine(i);
        for (int i = 0; i < 3; i++) Fired += () => Console.WriteLine(i);
    }
    List<Func<int>> A2()
    {
        var fs = new List<Func<int>>();
        for (int i = 0; i < 3; i++) fs.Add(() => i);
        return fs;
    }
    IEnumerable<int> B(int[] xs)
    {
        IEnumerable<int> q = xs;
        for (int i = 0; i < 3; i++) q = q.Where(x => x > i);
        return q;
    }
    void D()
    {
        var fs = new List<Func<int>>();
        for (int i = 0; i < 3; i++) { var f = () => i; fs.Add(f); }
    }
}
"""

    // CR0160 (the fix) wins wherever both read the shape; the seven escapes are
    // still seven findings, and none of them is left to the note
    let fired =
        suggest source |> List.filter (fun s -> s.Code = "CR0017" || s.Code = "CR0160")

    Assert.Equal(7, fired.Length)
    Assert.Empty(fired |> List.filter (fun s -> s.Code = "CR0017"))

[<Fact>]
let ``a closure that completes inside the iteration, a copied variable and a foreach stay quiet`` () =
    let source =
        """
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
class C
{
    async Task A(int[] xs)
    {
        var fs = new List<Func<int>>();
        for (int i = 0; i < 3; i++) { var copy = i; fs.Add(() => copy); }
        for (int i = 0; i < 3; i++) Console.WriteLine(xs.Where(x => x > i).Count());
        for (int i = 0; i < 3; i++) await Task.Run(() => Console.WriteLine(i));
        for (int i = 0; i < 3; i++) xs.ToList().ForEach(x => Console.WriteLine(x + i));
        for (int i = 0; i < 3; i++) { var total = xs.Sum(x => x * i); Console.WriteLine(total); }
        for (int i = 0; i < 3; i++) foreach (var x in xs.Select(x => x + i)) Console.WriteLine(x);
        foreach (var i in xs) fs.Add(() => i);
    }
}
"""

    Assert.Equal<string list>([], firedText source (suggestCode "CR0017" source))
