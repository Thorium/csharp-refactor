/// CR0142 (cosmetic, fix, off by default): attribute lists sharing one
/// line merge into one bracket pair.
///
///     [Fact] [Trait("a", "b")]   →  [Fact, Trait("a", "b")]
///
/// Off by default: one attribute per line is C#'s dominant style, and two
/// bracket pairs on one line are the author's choice as often as not.
///
/// Guards: the lists are adjacent on ONE line of the same declaration;
/// none has a target (`[assembly: …]`, `[return: …]`, `[field: …]` keep
/// their own brackets); no comment or directive between them; at most
/// `max_attributes` (4) in the merged list; the merged line fits the wrap
/// column, else the rule stands down rather than inventing a layout.
module CSharp.Refactor.AttributeMerge

open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax
open Microsoft.CodeAnalysis.Text

[<Literal>]
let Code = "CR0142"

let analyze (tree: SyntaxTree) (ctx: RuleContext) : Suggestion list =
    let text = tree.GetText()
    let maxAttributes = RuleContext.knobInt ctx Code "max_attributes" 4
    let wrapColumn = RuleContext.wrapColumn ctx Code

    let lists (node: SyntaxNode) =
        match node with
        | :? MemberDeclarationSyntax as m -> Some m.AttributeLists
        | :? ParameterSyntax as p -> Some p.AttributeLists
        | :? LambdaExpressionSyntax as l -> Some l.AttributeLists
        | _ -> None

    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun node ->
        match lists node with
        | Some ls when ls.Count >= 2 ->
            let ls = List.ofSeq ls
            // runs of lists on one line, targetless
            let lineOf (l: AttributeListSyntax) =
                text.Lines.GetLineFromPosition(l.SpanStart).LineNumber

            let runs =
                ls
                |> List.fold
                    (fun (acc: AttributeListSyntax list list) l ->
                        match acc with
                        | (last :: _ as run) :: rest when
                            lineOf last = lineOf l
                            && text.Lines.GetLineFromPosition(last.Span.End).LineNumber = lineOf l
                            ->
                            (l :: run) :: rest
                        | _ -> [ l ] :: acc)
                    []
                |> List.map List.rev
                |> List.rev
                |> List.filter (fun run -> run.Length >= 2)

            runs
            |> List.tryPick (fun run ->
                let attributes = run |> List.collect (fun l -> List.ofSeq l.Attributes)
                let first = List.head run
                let last = List.last run
                let span = TextSpan.FromBounds(first.SpanStart, last.Span.End)

                let between = TextSpan.FromBounds(first.SpanStart, last.Span.End)

                let holdsComment =
                    node.DescendantTrivia(descendIntoTrivia = true)
                    |> Seq.exists (fun t ->
                        between.Contains t.Span
                        && (t.IsDirective
                            || t.IsKind SyntaxKind.SingleLineCommentTrivia
                            || t.IsKind SyntaxKind.MultiLineCommentTrivia))

                if
                    run |> List.exists (fun l -> not (isNull l.Target))
                    || attributes.Length > maxAttributes
                    || holdsComment
                then
                    None
                else
                    let merged =
                        "["
                        + (attributes |> List.map (fun a -> a.ToString()) |> String.concat ", ")
                        + "]"

                    let column = Text.columnOf text first.SpanStart

                    if column + merged.Length > wrapColumn then
                        None
                    else
                        Some
                            {
                                Code = Code
                                Message = "Attribute lists on one line merge into one bracket pair"
                                Span = span
                                Fixes =
                                    [
                                        Suggestion.fix
                                            "Merge the attribute lists"
                                            Code
                                            [ Suggestion.replace span merged ]
                                    ]
                            })
        | _ -> None)
    |> List.ofSeq
