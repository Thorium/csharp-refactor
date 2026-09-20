/// CR0090 — correctness, typed. `new Guid()` reads like "a new guid" and
/// produces `00000000-…`. The fix states the value as `Guid.Empty`
/// (identical value and type, so a sweep applies it freely); the editor
/// also offers `Guid.NewGuid()` — the likely intent, but a behaviour change
/// only a human confirms. Typed-gated to `System.Guid`; `new Guid(bytes)`
/// and friends are deliberate and stay.
module CSharp.Refactor.EmptyGuid

open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax

[<Literal>]
let Code = "CR0090"

let private isSystemGuid (t: ITypeSymbol) =
    not (isNull t)
    && t.Name = "Guid"
    && t.ContainingNamespace.ToDisplayString() = "System"

/// The spelling of the receiver: `Guid` where the source wrote `Guid`,
/// `System.Guid` where it qualified.
let private qualifier (typeSyntax: TypeSyntax) = typeSyntax.ToString()

let analyze (tree: SyntaxTree) (model: SemanticModel) (_ctx: RuleContext) : Suggestion list =
    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun node ->
        let creation =
            match node with
            | :? ObjectCreationExpressionSyntax as c when c.ArgumentList = null || c.ArgumentList.Arguments.Count = 0 ->
                let t = model.GetTypeInfo(c).Type

                if isSystemGuid t then
                    Some(c :> SyntaxNode, qualifier c.Type)
                else
                    None
            | :? ImplicitObjectCreationExpressionSyntax as c when c.ArgumentList.Arguments.Count = 0 ->
                // `Guid id = new();` — the target type says Guid
                let t = model.GetTypeInfo(c).Type

                if isSystemGuid t then
                    Some(c :> SyntaxNode, "Guid")
                else
                    None
            | _ -> None

        creation
        |> Option.map (fun (site, spelled) ->
            {
                Code = Code
                Message =
                    "new Guid() is the empty guid 00000000-…; Guid.Empty says so, Guid.NewGuid() makes a new one"
                Span = site.Span
                Fixes =
                    [
                        Suggestion.fix
                            "Use Guid.Empty"
                            (Code + ".Empty")
                            [ Suggestion.replace site.Span $"{spelled}.Empty" ]
                        Suggestion.fix
                            "Use Guid.NewGuid()"
                            (Code + ".New")
                            [ Suggestion.replace site.Span $"{spelled}.NewGuid()" ]
                        |> Suggestion.editorOnly
                    ]
            }))
    |> List.ofSeq
