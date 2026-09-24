module CSharp.Refactor.Tests.RedundantSyntaxTests

open Xunit
open CSharp.Refactor.Tests.Harness

[<Fact>]
let ``the Attribute suffix goes, unless a same-named type of this file would win`` () =
    let source =
        """
using System;
[SerializableAttribute]
class A { [ObsoleteAttribute("x")] void M() { } }
class Fancy : Attribute { }
class FancyAttribute : Attribute { }
[FancyAttribute]
class B { }
"""

    let fired = suggestCode "CR0140" source |> firedText source
    Assert.Equal<string list>([ "SerializableAttribute"; "ObsoleteAttribute" ], fired)
    let fixedSource = fixAll "CR0140" source
    Assert.Contains("[Serializable]", fixedSource)
    Assert.Contains("[Obsolete(\"x\")]", fixedSource)
    Assert.Contains("[FancyAttribute]", fixedSource)

[<Fact>]
let ``an empty attribute argument list goes`` () =
    let source =
        "using System;\n[Serializable()]\nclass A { [Obsolete(\"x\")] void M() { } }\n"

    Assert.Equal<string list>([ "()" ], suggestCode "CR0141" source |> firedText source)
    Assert.Contains("[Serializable]\n", fixAll "CR0141" source)

[<Fact>]
let ``CR0141 keeps an attribute's parentheses when they hold the arguments of a build flavour`` () =
    // without STRICT_API the argument list is empty, but dropping it would drop the #if
    let source =
        """
using System;
class OrderService
{
    [Obsolete(
#if STRICT_API
        "Use SubmitOrderAsync", true
#endif
    )]
    public void SubmitOrder() { }
}
"""

    Assert.Empty(suggestCode "CR0141" source)

[<Fact>]
let ``a verbatim identifier keeps its at-sign on keywords, contextual keywords and the discard`` () =
    let source =
        "class C\n{\n    int @plain = 1;\n    int @class = 2;\n    int @var = 3;\n    int @_ = 4;\n    int M() => @plain + @class + @var + @_;\n}\n"

    let fired = suggestCode "CR0143" source |> firedText source
    Assert.Equal<string list>([ "@plain"; "@plain" ], fired)
    let fixedSource = fixAll "CR0143" source
    Assert.Contains("int plain = 1;", fixedSource)
    Assert.Contains("=> plain + @class + @var + @_;", fixedSource)

[<Fact>]
let ``else holding only an if flattens, and its lines move left`` () =
    let source =
        """
class C
{
    int M(int a, int b)
    {
        if (a > 0)
        {
            return 1;
        }
        else
        {
            if (b > 0)
            {
                return 2;
            }
            else
            {
                return 3;
            }
        }
    }
}
"""

    let fired = suggestCode "CR0144" source
    Assert.Single fired |> ignore
    let fixedSource = fixAll "CR0144" source

    let expected =
        """
class C
{
    int M(int a, int b)
    {
        if (a > 0)
        {
            return 1;
        }
        else if (b > 0)
        {
            return 2;
        }
        else
        {
            return 3;
        }
    }
}
"""

    Assert.Equal(normalize expected, fixedSource)

[<Fact>]
let ``a comment inside the else block, or a second statement, keeps the block`` () =
    let source =
        "class C\n{\n    int M(int a, int b)\n    {\n        if (a > 0) return 1;\n        else\n        {\n            // why\n            if (b > 0) return 2;\n        }\n        if (a > 1) return 4;\n        else\n        {\n            if (b > 1) return 5;\n            return 6;\n        }\n        return 0;\n    }\n}\n"

    Assert.Empty(suggestCode "CR0144" source)

[<Fact>]
let ``without a model the attribute suffix is a note, never a fix`` () =
    let source = "using System;\n[SerializableAttribute]\nclass A { }\n"
    let tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText source

    let fired =
        CSharp.Refactor.RedundantSyntax.analyze tree CSharp.Refactor.RuleContext.editor
        |> List.filter (fun s -> s.Code = "CR0140")

    Assert.Single fired |> ignore
    Assert.Empty fired.Head.Fixes
