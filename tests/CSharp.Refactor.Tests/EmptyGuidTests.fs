module CSharp.Refactor.Tests.EmptyGuidTests

open Xunit
open CSharp.Refactor.Tests.Harness

[<Literal>]
let private code = "CR0090"

[<Fact>]
let ``new Guid() becomes Guid.Empty, qualification kept`` () =
    let source =
        """
using System;
class C
{
    Guid A() => new Guid();
    System.Guid B() => new System.Guid();
    Guid D()
    {
        Guid g = new();
        return g;
    }
}
"""

    let fired = suggestCode code source
    Assert.Equal<string list>([ "new Guid()"; "new System.Guid()"; "new()" ], firedText source fired)

    let fixedSource = fixAll code source
    Assert.Contains("Guid A() => Guid.Empty;", fixedSource)
    Assert.Contains("System.Guid B() => System.Guid.Empty;", fixedSource)
    Assert.Contains("Guid g = Guid.Empty;", fixedSource)

[<Fact>]
let ``the editor alternative is NewGuid and never auto-applied`` () =
    let source = "using System;\nclass C { Guid A() => new Guid(); }\n"
    let s = suggestCode code source |> List.exactlyOne
    let alt = s.Fixes |> List.find (fun f -> f.EditorOnly)
    Assert.Equal("Use Guid.NewGuid()", alt.Title)
    Assert.Contains("Guid.NewGuid()", applyFix source alt)
    Assert.Contains("Guid.Empty", fixAll code source)

[<Fact>]
let ``a Guid with arguments, or another type's constructor, stays`` () =
    let source =
        """
using System;
struct Guid { }
class C
{
    System.Guid A(byte[] b) => new System.Guid(b);
    System.Guid B(string s) => new System.Guid(s);
    Guid Own() => new Guid();
    object O() => new object();
}
"""

    Assert.Empty(suggestCode code source)

[<Fact>]
let ``a parameter default stays, a target-typed new() without using System is qualified`` () =
    let source =
        """
class C
{
    void M(System.Guid g = new System.Guid()) { }
    void N(System.Guid g = new()) { }
    System.Guid A()
    {
        System.Guid g = new();
        return g;
    }
}
"""

    Assert.Equal<string list>([ "new()" ], firedText source (suggestCode code source))
    Assert.Contains("System.Guid g = System.Guid.Empty;", fixAll code source)
