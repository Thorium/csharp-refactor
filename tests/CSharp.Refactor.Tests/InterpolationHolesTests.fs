module CSharp.Refactor.Tests.InterpolationHolesTests

open Xunit
open CSharp.Refactor.Tests.Harness

[<Literal>]
let private code = "CR0103"

[<Fact>]
let ``hole-free interpolation loses its dollar`` () =
    let source =
        """
class C
{
    string A() => $"no holes";
    string B() => $@"verbatim \ no holes";
    string C2() => @$"verbatim \ no holes";
}
"""

    let fired = suggestCode code source
    Assert.Equal(3, fired.Length)

    let fixedSource = fixAll code source
    Assert.Contains("=> \"no holes\";", fixedSource)
    Assert.Contains("=> @\"verbatim \\ no holes\";", fixedSource)
    Assert.DoesNotContain("$", fixedSource)

[<Fact>]
let ``a hole, an escaped brace, or a raw string keeps the dollar`` () =
    let source =
        "class C\n{\n    string A(int x) => $\"value {x}\";\n    string B() => $\"braces {{kept}}\";\n    string C2() => $\"\"\"\n        raw text\n        \"\"\";\n}\n"

    Assert.Empty(suggestCode code source)

[<Fact>]
let ``a FormattableString, IFormattable or handler target keeps the dollar`` () =
    // the F# twin lost this the hard way: `let s: FormattableString = $"…"`
    // stopped compiling once its `$` went, and ILogger's handler parameters
    // are not string parameters at all
    let source =
        """
using System;
using System.Runtime.CompilerServices;
[InterpolatedStringHandler]
struct Handler
{
    public Handler(int literalLength, int formattedCount) { }
    public void AppendLiteral(string s) { }
}
class C
{
    FormattableString A() => $"typed";
    IFormattable B() { IFormattable f = $"formattable"; return f; }
    void Only(Handler h) { }
    void D() => Only($"handler parameter");
    string E() { var s = $"plain"; return s; }
    string F() { string s = $"declared string"; return s; }
    object G() => $"boxed";
}
"""

    let fired = suggestCode code source |> firedText source
    Assert.Equal<string list>([ "$\"plain\""; "$\"declared string\""; "$\"boxed\"" ], fired)
    let fixedSource = fixAll code source
    Assert.Contains("=> $\"typed\";", fixedSource)
    Assert.Contains("Only($\"handler parameter\")", fixedSource)

[<Fact>]
let ``without a semantic model an argument or typed declaration keeps the dollar`` () =
    let source =
        "class C\n{\n    void M(object o) { M($\"argument\"); string s = $\"declared\"; var v = $\"var\"; _ = v; }\n}\n"

    let tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText source

    let fired =
        CSharp.Refactor.InterpolationHoles.analyze tree CSharp.Refactor.RuleContext.editor
        |> firedText source

    Assert.Equal<string list>([ "$\"var\"" ], fired)
