/// The shared proofs, each from both sides: what passes, and what must
/// fail because a rewrite over it would change behaviour.
module CSharp.Refactor.Tests.GuardsTests

open System.Collections.Generic
open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax
open Microsoft.CodeAnalysis.Diagnostics
open Microsoft.CodeAnalysis.Text
open Xunit
open CSharp.Refactor
open CSharp.Refactor.Tests.Harness

/// The expression of a `_ = <expr>;` statement labelled by a comment.
let private expressionsOf (source: string) =
    let compilation, tree = compileClean source
    let model = compilation.GetSemanticModel(tree, false)

    let byLabel =
        tree.GetRoot().DescendantNodes()
        |> Seq.choose (fun n ->
            match n with
            | :? ExpressionStatementSyntax as s ->
                match s.Expression with
                | :? AssignmentExpressionSyntax as a when a.Left.ToString() = "_" ->
                    let label =
                        s.GetTrailingTrivia()
                        |> Seq.tryFind (fun t -> t.IsKind SyntaxKind.SingleLineCommentTrivia)
                        |> Option.map (fun t -> t.ToString().TrimStart('/', ' '))
                        |> Option.defaultValue ""

                    Some(label, a.Right)
                | _ -> None
            | _ -> None)
        |> Map.ofSeq

    model, byLabel

let private fixture =
    """
using System;
using System.Collections.Generic;
using System.Linq;
class P { public int Get { get { Console.WriteLine("x"); return 1; } } public int this[int i] => i; }
class C
{
    static int Log(int x) { Console.WriteLine(x); return x; }
    readonly int ro = 1;
    int mutable = 2;
    const int K = 3;
    void M(int a, string s, int[] arr, List<int> xs, P p, Lazy<int> lazy, dynamic d)
    {
        _ = a + 1;                          // pure-arith
        _ = s.Length;                       // pure-length
        _ = arr[0] * K;                     // pure-index
        _ = ro;                             // pure-readonly
        _ = (a, s);                         // pure-tuple
        _ = a > 0 ? a : -a;                 // pure-cond
        _ = nameof(a);                      // pure-nameof
        _ = Log(a);                         // impure-call
        _ = mutable;                        // impure-field
        _ = p.Get;                          // impure-getter
        _ = p[1];                           // impure-indexer
        _ = a++;                            // impure-increment
        _ = s.Trim().ToUpperInvariant();    // core-string
        _ = Math.Max(a, 1);                 // core-math
        _ = xs.Select(x => x * 2).Count();  // core-linq
        _ = xs.Select(x => Log(x));         // notcore-lambda
        _ = xs.Where(x => x > 0).ToList();  // notcore-tolist
        _ = lazy.Value;                     // notcore-lazy
        _ = $"{a}";                         // notcore-hole
        _ = d + 1;                          // notcore-dynamic
        _ = DateTime.Now;                   // notcore-clock
    }
}
"""

[<Fact>]
let ``isPureExpression accepts reads and built-in operators and refuses calls and writes`` () =
    let model, e = expressionsOf fixture

    for label in
        [
            "pure-arith"
            "pure-length"
            "pure-index"
            "pure-readonly"
            "pure-tuple"
            "pure-cond"
            "pure-nameof"
        ] do
        Assert.True(Guards.isPureExpression model e.[label], label)

    for label in
        [
            "impure-call"
            "impure-field"
            "impure-getter"
            "impure-indexer"
            "impure-increment"
        ] do
        Assert.False(Guards.isPureExpression model e.[label], label)

[<Fact>]
let ``callsOnlyCore waves BCL members through and stops at user code, lazies, holes and dynamic`` () =
    let model, e = expressionsOf fixture

    for label in [ "core-string"; "core-math"; "core-linq"; "pure-arith" ] do
        Assert.True(Guards.callsOnlyCore model e.[label], label)

    for label in
        [
            "impure-call"
            "notcore-lambda"
            "notcore-tolist"
            "notcore-lazy"
            "notcore-hole"
            "notcore-dynamic"
            "notcore-clock"
            "impure-getter"
        ] do
        Assert.False(Guards.callsOnlyCore model e.[label], label)

[<Fact>]
let ``the speculative check refuses an edit that breaks binding and accepts one that keeps it`` () =
    let source = "using System;\nclass C { int M(string s) => s.Length; }\n"
    let compilation, tree = compileClean source
    let model = compilation.GetSemanticModel(tree, false)
    let at = source.IndexOf "s.Length"

    let good =
        [ Suggestion.replace (TextSpan(at, "s.Length".Length)) "s.Trim().Length" ]

    let bad = [ Suggestion.replace (TextSpan(at, "s.Length".Length)) "s.Lenght" ]
    Assert.True(Guards.speculativeCheck model good)
    Assert.False(Guards.speculativeCheck model bad)

[<Fact>]
let ``the yields-to gate stands down only when the shadowed rule is enabled`` () =
    let ctxWith (pairs: (string * string) list) =
        { RuleContext.editor with
            Options = Some(FakeOptions(dict pairs) :> AnalyzerConfigOptions)
        }

    Assert.False(RuleContext.shadowedRuleOn (ctxWith []) [ "CA2213" ])
    Assert.False(RuleContext.shadowedRuleOn (ctxWith [ "dotnet_diagnostic.CA2213.severity", "none" ]) [ "CA2213" ])
    Assert.True(RuleContext.shadowedRuleOn (ctxWith [ "dotnet_diagnostic.CA2213.severity", "warning" ]) [ "CA2213" ])

    Assert.True(
        RuleContext.shadowedRuleOn
            (ctxWith [ "dotnet_diagnostic.CA2213.severity", "suggestion" ])
            [ "CA2213"; "CA1001" ]
    )

[<Fact>]
let ``knobs read integers and booleans in the ini spellings and fail open`` () =
    let ctx =
        { RuleContext.editor with
            Options =
                Some(
                    FakeOptions(
                        dict
                            [
                                "csharp_refactor.CR0006.then_at_least", "30"
                                "csharp_refactor.CR0040.sync_swap", "on"
                                "csharp_refactor.CR0028.arrays", "nonsense"
                            ]
                    )
                    :> AnalyzerConfigOptions
                )
        }

    Assert.Equal(30, RuleContext.knobInt ctx "CR0006" "then_at_least" 20)
    Assert.Equal(3, RuleContext.knobInt ctx "CR0006" "else_at_most" 3)
    Assert.True(RuleContext.knobBool ctx "CR0040" "sync_swap" false)
    Assert.False(RuleContext.knobBool ctx "CR0028" "arrays" false)
    Assert.Equal(20, RuleContext.knobInt RuleContext.editor "CR0006" "then_at_least" 20)

[<Fact>]
let ``an indexer on a type parameter or a user type is a call, and never a crash`` () =
    let source =
        """
using System.Collections.Generic;
class Box { public int this[int i] => i; }
class C
{
    static void M<T>(T xs, Box b, List<int> ys, int[] arr) where T : IList<int>
    {
        _ = xs[0];      // typeparam
        _ = b[0];       // user
        _ = ys[0];      // list
        _ = arr[0];     // array
    }
}
"""

    let model, e = expressionsOf source
    Assert.False(Guards.isPureExpression model e.["typeparam"], "typeparam")
    Assert.False(Guards.callsOnlyCore model e.["typeparam"], "typeparam-core")
    Assert.False(Guards.isPureExpression model e.["user"], "user")
    Assert.True(Guards.isPureExpression model e.["list"], "list")
    Assert.True(Guards.isPureExpression model e.["array"], "array")
