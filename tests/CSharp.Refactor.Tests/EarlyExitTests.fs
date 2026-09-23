module CSharp.Refactor.Tests.EarlyExitTests

open Xunit
open CSharp.Refactor.Tests.Harness

let private twentyLines =
    [ for i in 1..20 -> $"            Console.WriteLine({i});" ]
    |> String.concat "\n"

// ---- CR0006 ----

[<Fact>]
let ``a long then under a short exiting else flips to a guard clause`` () =
    let source =
        $"""
using System;
class C
{{
    void Run(bool ok, int n)
    {{
        if (ok && n > 0)
        {{
{twentyLines}
        }}
        else
        {{
            Console.WriteLine("no");
            return;
        }}
        Console.WriteLine("after");
    }}
}}
"""

    let fired = suggestCode "CR0006" source
    Assert.Equal(1, fired.Length)
    let fixedSource = fixAll "CR0006" source

    Assert.Contains(
        normalize
            """        if (!(ok && n > 0))
        {
            Console.WriteLine("no");
            return;
        }

        Console.WriteLine(1);
        Console.WriteLine(2);""",
        fixedSource
    )

    Assert.Contains(normalize "        Console.WriteLine(20);\n        Console.WriteLine(\"after\");", fixedSource)

[<Fact>]
let ``a short then, a non-exiting else, a chain, a comment on the else line and a clashing local stand down`` () =
    let source =
        $"""
using System;
class C
{{
    void Short(bool ok)
    {{
        if (ok) {{ Console.WriteLine(1); }} else {{ return; }}
    }}
    void NoExit(bool ok)
    {{
        if (ok)
        {{
{twentyLines}
        }}
        else
        {{
            Console.WriteLine("no");
        }}
    }}
    void Chain(bool ok, bool other)
    {{
        if (ok)
        {{
{twentyLines}
        }}
        else if (other)
        {{
            return;
        }}
        else
        {{
            return;
        }}
    }}
    void Comment(bool ok)
    {{
        if (ok)
        {{
{twentyLines}
        }}
        else // nothing to do
        {{
            return;
        }}
    }}
    void Clash(bool ok)
    {{
        if (ok)
        {{
            var x = 1;
{twentyLines}
        }}
        else
        {{
            return;
        }}
        {{ var x = 2; Console.WriteLine(x); }}
    }}
}}
"""

    Assert.Equal<string list>([], firedText source (suggestCode "CR0006" source))

[<Fact>]
let ``CR0006 keeps a then block that declares a using var`` () =
    let source =
        $"""
using System;
using System.IO;
class C
{{
    void Run(bool ok, string path)
    {{
        if (ok)
        {{
            using var stream = File.OpenRead(path);
{twentyLines}
        }}
        else
        {{
            return;
        }}
        File.Delete(path);
    }}
}}
"""

    Assert.Empty(suggestCode "CR0006" source)

// ---- CR0016 ----

[<Fact>]
let ``a flag raised with statements still to run is noted, a raise at the end is not`` () =
    let source =
        """
using System;
class C
{
    void A(int[] xs)
    {
        bool done = false;
        int i = 0;
        while (!done && i < xs.Length)
        {
            if (xs[i] < 0)
            {
                done = true;
            }
            Console.WriteLine(xs[i]);
            i++;
        }
    }
    void B(int[] xs)
    {
        bool done = false;
        for (int i = 0; !(done || xs.Length == 0); i++)
        {
            Console.WriteLine(xs[i]);
            if (xs[i] < 0) done = true;
        }
    }
    void D(int[] xs)
    {
        bool stop = false;
        int i = 0;
        while (i < xs.Length && !stop)
        {
            if (xs[i] < 0) { stop = true; Console.WriteLine("last"); }
            i++;
        }
    }
}
"""

    let fired = suggestCode "CR0016" source
    Assert.Equal<string list>([ "done = true;"; "stop = true;" ], firedText source fired)
    Assert.Contains("2 more statement", fired.[0].Message)
