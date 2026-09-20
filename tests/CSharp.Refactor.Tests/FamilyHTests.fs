module CSharp.Refactor.Tests.FamilyHTests

open Xunit
open CSharp.Refactor.Tests.Harness

// ---- CR0142 ----

[<Fact>]
let ``attribute lists on one line merge, targets and comments hold`` () =
    let source =
        """
using System;
class TraitAttribute : Attribute { public TraitAttribute(string a, string b) { } }
class FactAttribute : Attribute { }
class C
{
    [Fact] [Trait("a", "b")]
    public void A() { }
    [Fact]
    [Trait("a", "b")]
    public void B() { }
    [Fact] /* why */ [Trait("a", "b")]
    public void D() { }
    [return: Trait("a", "b")] [Fact]
    public void E() { }
}
"""

    let fired = suggestCode "CR0142" source
    Assert.Equal(1, fired.Length)
    Assert.Contains("    [Fact, Trait(\"a\", \"b\")]\n    public void A() { }", fixAll "CR0142" source)

// ---- CR0146 ----

[<Fact>]
let ``a trailing note on a public declaration becomes its summary, weak notes and documented members stay`` () =
    let source =
        """
using System;
class C
{
    public decimal Rate(int n) => n * 2m; // monthly, non-compounding rate
    [Obsolete]
    public int Count; // number of items seen so far
    /// <summary>Documented already.</summary>
    public int Documented; // the summary above wins
    public int Short; // unused
    public int Todo; // TODO: revisit this later on
    public int Angle; // less than <b> more
    private int Hidden; // private members are not the API surface
    public int Code; // x => x.Count() + 1
    public int
        Split; // the comment is not on the header line? it is: the identifier line
}
"""

    let fired = suggestCode "CR0146" source
    Assert.Equal(3, fired.Length)
    let fixedSource = fixAll "CR0146" source

    Assert.Contains(
        "    /// <summary>monthly, non-compounding rate</summary>\n    public decimal Rate(int n) => n * 2m;\n",
        fixedSource
    )

    Assert.Contains(
        "    /// <summary>number of items seen so far</summary>\n    [Obsolete]\n    public int Count;\n",
        fixedSource
    )

    Assert.Contains("    public int Short; // unused", fixedSource)
    Assert.Contains("    public int Todo; // TODO: revisit this later on", fixedSource)
    Assert.Contains("    public int Angle; // less than <b> more", fixedSource)
    Assert.Contains("    public int Code; // x => x.Count() + 1", fixedSource)

// ---- CR0145 ----

[<Fact>]
let ``a namespace spelled out often enough becomes a using, sorted into its family`` () =
    let source =
        """
using System;
using System.Linq;
using Zed;
namespace Zed { public class Thing { } }
namespace Deep.Down.Here { public class Helper { public static int Go() => 1; } public class Other { } }
class C
{
    int A() => Deep.Down.Here.Helper.Go() + Deep.Down.Here.Helper.Go();
    int B() => Deep.Down.Here.Helper.Go();
    Deep.Down.Here.Other D() => new Deep.Down.Here.Other();
    string E() => System.Text.Json.JsonSerializer.Serialize(1) + System.Text.Json.JsonSerializer.Serialize(2);
}
"""

    let fired = suggestCode "CR0145" source
    Assert.Equal(1, fired.Length)
    let fixedSource = fixAll "CR0145" source
    Assert.Contains("using System;\nusing System.Linq;\nusing Deep.Down.Here;\nusing Zed;\n", fixedSource)
    Assert.Contains("int A() => Helper.Go() + Helper.Go();", fixedSource)
    Assert.Contains("Other D() => new Other();", fixedSource)
    // two spellings of a three-segment namespace are under the threshold
    Assert.Contains("System.Text.Json.JsonSerializer.Serialize(1)", fixedSource)

[<Fact>]
let ``an imported namespace, a clashing name and a global alias stand down`` () =
    let source =
        """
using Deep.Down.Here;
namespace Deep.Down.Here { public class Helper { public static int Go() => 1; } }
namespace Deep.Down.There { public class Helper { public static int Go() => 2; } }
class Helper2 { }
class C
{
    int A() => Deep.Down.Here.Helper.Go() + Deep.Down.Here.Helper.Go() + Deep.Down.Here.Helper.Go() + Deep.Down.Here.Helper.Go();
    int B() => Deep.Down.There.Helper.Go() + Deep.Down.There.Helper.Go() + Deep.Down.There.Helper.Go() + Deep.Down.There.Helper.Go();
    int D() => global::Deep.Down.There.Helper.Go();
}
"""

    // `Here` is imported (IDE0001's); `There` would clash with the imported `Helper`
    let fired = suggestCode "CR0145" source
    Assert.Equal(1, fired.Length)
    Assert.Empty(fired.Head.Fixes)
    Assert.Contains("held", fired.Head.Message)
