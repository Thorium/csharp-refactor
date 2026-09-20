module CSharp.Refactor.Tests.EnumCoverageTests

open Xunit
open CSharp.Refactor.Tests.Harness

let private editorFix (source: string) (s: CSharp.Refactor.Suggestion) =
    let fix = s.Fixes.Head
    Assert.True(fix.EditorOnly, "the expansion is an editor offer, never applied by the sweep")
    let result = applyFix source fix
    let compilation, _ = compile result
    Assert.Empty(errorsOf compilation)
    result

[<Fact>]
let ``a default hiding two members is named in the editor, with a throwing default`` () =
    let source =
        """
using System;
enum Color { Red, Green, Blue, Alpha }
class C
{
    string Name(Color c)
    {
        switch (c)
        {
            case Color.Red:
                return "r";
            case Color.Green:
                return "g";
            default:
                return "other";
        }
    }
    int Code(Color c) => c switch
    {
        Color.Red or Color.Green => 1,
        Color.Blue => 2,
        _ => 0,
    };
}
"""

    let fired = suggestCode "CR0013" source
    Assert.Equal(2, fired.Length)
    Assert.Empty(suggestCode "CR0014" source)

    let statement = editorFix source fired.[0]

    Assert.Contains(
        normalize
            """            case Color.Blue:
            case Color.Alpha:
                return "other";
            default:
                throw new ArgumentOutOfRangeException(nameof(c));
        }""",
        statement
    )

    let expression = editorFix source fired.[1]

    Assert.Contains(
        normalize
            """        Color.Blue => 2,
        Color.Alpha => 0,
        _ => throw new ArgumentOutOfRangeException(nameof(c)),
    };""",
        expression
    )

[<Fact>]
let ``an exiting switch with no default and missing members is noted, the editor adding throwing arms`` () =
    let source =
        """
using System;
enum Color { Red, Green, Blue }
class C
{
    string Name(Color c)
    {
        switch (c)
        {
            case Color.Red:
                return "r";
            case Color.Green:
                throw new InvalidOperationException();
        }
        return "?";
    }
}
"""

    let fired = suggestCode "CR0014" source
    Assert.Equal(1, fired.Length)
    Assert.Empty(suggestCode "CR0013" source)
    let result = editorFix source fired.[0]

    Assert.Contains(
        normalize
            """            case Color.Green:
                throw new InvalidOperationException();
            case Color.Blue:
                throw new NotImplementedException();
        }""",
        result
    )

[<Fact>]
let ``flags, guards, a nullable scrutinee, a break arm, a covered alias and too many hidden members stay quiet`` () =
    let source =
        """
using System;
[Flags] enum F { A = 1, B = 2, C = 4 }
enum Color { Red, Green, Blue, Crimson = Red }
enum Big { A, B, C, D, E }
class C
{
    int Flags(F f) => f switch { F.A => 1, _ => 0 };
    int Guarded(Color c, int n) => c switch { Color.Red when n > 0 => 1, Color.Green => 2, _ => 0 };
    int Nullable(Color? c) => c switch { Color.Red => 1, Color.Green => 2, Color.Blue => 3, _ => 0 };
    void Breaks(Color c)
    {
        switch (c)
        {
            case Color.Red:
                Console.WriteLine("r");
                break;
            case Color.Green:
                return;
        }
    }
    int Alias(Color c) => c switch { Color.Red => 1, Color.Green => 2, Color.Blue => 3, _ => 0 };
    int Many(Big b) => b switch { Big.A => 1, _ => 0 };
}
"""

    Assert.Equal<string list>([], firedText source (suggestCode "CR0013" source))
    Assert.Equal<string list>([], firedText source (suggestCode "CR0014" source))
