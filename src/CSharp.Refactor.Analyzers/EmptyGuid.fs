/// CR0090 — correctness, typed. `new Guid()` reads like "a new guid" and
/// produces `00000000-…`. The fix states the value as `Guid.Empty`
/// (identical value and type, so a sweep applies it freely); the editor
/// also offers `Guid.NewGuid()` — the likely intent, but a behaviour change
/// only a human confirms. Typed-gated to `System.Guid`; `new Guid(bytes)`
/// and friends are deliberate and stay. A parameter default (`Guid g =
/// new Guid()`) stays: a default must be a constant, which `Guid.Empty` is
/// not (CS1736). The implicit `new()` form spells the type as the file
/// resolves it (`Guid` under `using System;`, else `System.Guid`), and the
/// speculative check proves the rewrite binds.
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

/// A position that demands a constant: a parameter's default value, an
/// attribute argument.
let private constantContext (node: SyntaxNode) =
    Guards.insideAttribute node
    || node.Ancestors()
       |> Seq.exists (fun a ->
           match a with
           | :? EqualsValueClauseSyntax as ev -> ev.Parent :? ParameterSyntax
           | _ -> false)

let analyze (tree: SyntaxTree) (model: SemanticModel) (_ctx: RuleContext) : Suggestion list =
    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun node ->
        let creation =
            match node with
            | _ when constantContext node -> None
            | :? ObjectCreationExpressionSyntax as c when isNull c.ArgumentList || c.ArgumentList.Arguments.Count = 0 ->
                let t = model.GetTypeInfo(c).Type

                if isSystemGuid t then
                    Some(c :> SyntaxNode, qualifier c.Type)
                else
                    None
            | :? ImplicitObjectCreationExpressionSyntax as c when c.ArgumentList.Arguments.Count = 0 ->
                // `Guid id = new();` — the target type says Guid; the file may not import System
                let t = model.GetTypeInfo(c).Type

                if isSystemGuid t then
                    Some(c :> SyntaxNode, Guards.typeText model c.SpanStart "System" "Guid")
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
            }
            |> Guards.verified model))
    |> List.ofSeq
