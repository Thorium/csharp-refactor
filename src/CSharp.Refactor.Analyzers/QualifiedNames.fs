/// CR0145 (idiom, fix): a namespace spelled out at every use is a `using`.
///
///     System.Text.Json.JsonSerializer.Serialize(x)   // six times in the file
///
///     using System.Text.Json;                        // among the usings
///     JsonSerializer.Serialize(x)                    // at every use
///
/// Guards: the prefix binds to a NAMESPACE at each spelling and the name
/// after it to a type (so a type nested in a type, or a namespace inside
/// another, is never mistaken for the prefix); the namespace is not
/// already imported — that case is IDE0001's; at least `uses` (6)
/// spellings, or `deep_uses` (4) when the namespace has three segments or
/// more; a type name the file already declares or uses unqualified would
/// clash and holds the fix; `global::`-qualified and `using`-directive
/// spellings are left alone; the speculative re-bind (with the new
/// `using` in place) settles every other resolution change, an
/// extension-method ambiguity included. The `using` goes among the file's
/// usings in alphabetical order within its family (`System` first), else
/// after the last one, else at the top; a file-scoped or block namespace
/// with its own usings takes it there.
module CSharp.Refactor.QualifiedNames

open System
open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax
open Microsoft.CodeAnalysis.Text

[<Literal>]
let Code = "CR0145"

/// A spelling `Ns.Type`: the prefix node to drop (with its dot) and the
/// namespace.
type private Spelling =
    {
        Prefix: SyntaxNode
        Dot: SyntaxToken
        Namespace: INamespaceSymbol
        TypeName: string
    }

/// The leftmost identifier of a dotted chain of plain names (`System` in
/// `System.Text.Json.JsonSerializer`); None where the chain holds anything
/// else (`this.x`, `f().y`, `a[0].z`), which no namespace spelling does.
[<TailCall>]
let rec private chainRoot (node: SyntaxNode) : IdentifierNameSyntax option =
    match node with
    | :? IdentifierNameSyntax as id -> Some id
    | :? QualifiedNameSyntax as q -> chainRoot q.Left
    | :? MemberAccessExpressionSyntax as m when m.IsKind SyntaxKind.SimpleMemberAccessExpression ->
        chainRoot m.Expression
    | _ -> None

/// The simple name of every namespace a compilation can see, its own and
/// its references' (`System`, `Text`, `Json`, `Microsoft`…): the only
/// names a chain's root identifier can bind to a namespace by. Once per
/// compilation.
let private namespaceNames =
    System.Runtime.CompilerServices.ConditionalWeakTable<Compilation, Lazy<Collections.Generic.HashSet<string>>>()

let private namespaceNamesOf (compilation: Compilation) =
    namespaceNames
        .GetValue(
            compilation,
            fun c ->
                lazy
                    (let names = Collections.Generic.HashSet<string>()

                     let rec walk (ns: INamespaceSymbol) =
                         for child in ns.GetNamespaceMembers() do
                             names.Add child.Name |> ignore
                             walk child

                     walk c.GlobalNamespace
                     names)
        )
        .Value

/// The spellings of a file. Binding every `a.b` of a file twice was the
/// rule's whole cost (a startup file of service-registration chains took
/// a quarter of a second for nothing; a symbol lookup is a third of a
/// millisecond), so a chain is bound only when its root identifier can be
/// a namespace — spelled like one the compilation knows, or like an alias
/// the file declares — and is one: a namespace holds nothing but
/// namespaces and types, so a chain rooted in a local, a member or a type
/// spells no namespace at any prefix. The root is bound once per file,
/// not once per prefix of every chain it heads.
let private spellingsOf (model: SemanticModel) (root: SyntaxNode) (nodes: SyntaxNode seq) : Spelling list =
    let namespaceRoots = System.Collections.Generic.Dictionary<SyntaxNode, bool>()
    let knownNames = namespaceNamesOf model.Compilation

    let aliases =
        root.DescendantNodes()
        |> Seq.choose (fun n ->
            match n with
            | :? UsingDirectiveSyntax as u when not (isNull u.Alias) -> Some u.Alias.Name.Identifier.ValueText
            | _ -> None)
        |> Collections.Generic.HashSet

    let rootIsNamespace (left: SyntaxNode) =
        match chainRoot left with
        | None -> false
        | Some root ->
            match namespaceRoots.TryGetValue root with
            | true, answer -> answer
            | _ ->
                let name = root.Identifier.ValueText

                let answer =
                    (knownNames.Contains name || aliases.Contains name)
                    && (match model.GetSymbolInfo(root).Symbol with
                        | :? INamespaceSymbol -> true
                        | _ -> false)

                namespaceRoots.[root] <- answer
                answer

    let ofParts (left: SyntaxNode) (dot: SyntaxToken) (right: SimpleNameSyntax) =
        if not (rootIsNamespace left) then
            None
        else
            match model.GetSymbolInfo(left).Symbol with
            | :? INamespaceSymbol as ns when not ns.IsGlobalNamespace ->
                match model.GetSymbolInfo(right).Symbol with
                | :? INamedTypeSymbol ->
                    Some
                        {
                            Prefix = left
                            Dot = dot
                            Namespace = ns
                            TypeName = right.Identifier.ValueText
                        }
                | _ -> None
            | _ -> None

    nodes
    |> Seq.choose (fun node ->
        match node with
        | :? QualifiedNameSyntax as q -> ofParts q.Left q.DotToken q.Right
        | :? MemberAccessExpressionSyntax as m when m.IsKind SyntaxKind.SimpleMemberAccessExpression ->
            ofParts m.Expression m.OperatorToken m.Name
        | _ -> None)
    |> List.ofSeq

let private insideUsingOrAlias (node: SyntaxNode) =
    node.Ancestors()
    |> Seq.exists (fun a -> a :? UsingDirectiveSyntax || a :? AliasQualifiedNameSyntax)

let analyze (tree: SyntaxTree) (model: SemanticModel) (ctx: RuleContext) : Suggestion list =
    let text = tree.GetText()
    let uses = RuleContext.knobInt ctx Code "uses" 6
    let deepUses = RuleContext.knobInt ctx Code "deep_uses" 4
    let root = tree.GetRoot()

    let spellings =
        root.DescendantNodes()
        |> Seq.filter (insideUsingOrAlias >> not)
        |> spellingsOf model root

    spellings
    |> List.groupBy (fun s -> s.Namespace.ToDisplayString())
    |> List.choose (fun (nsName, group) ->
        let segments = nsName.Split('.').Length
        let threshold = if segments >= 3 then deepUses else uses

        if group.Length < threshold then
            None
        else
            // already imported? then every type of it resolves bare
            let alreadyImported =
                group
                |> List.forall (fun s ->
                    model.LookupNamespacesAndTypes(s.Prefix.SpanStart, name = s.TypeName)
                    |> Seq.exists (fun sym ->
                        sym :? INamedTypeSymbol && sym.ContainingNamespace.ToDisplayString() = nsName))

            if alreadyImported then
                None
            else
                let typeNames = group |> List.map (fun s -> s.TypeName) |> List.distinct

                // a clash: the file already has the bare name in some OTHER sense —
                // a token that is the type itself (its declaration, a spelling) is not one
                let isTheType (t: SyntaxToken) =
                    match t.Parent with
                    | null -> false
                    | parent ->
                        let declared = model.GetDeclaredSymbol parent

                        let symbol =
                            if isNull declared then
                                model.GetSymbolInfo(parent).Symbol
                            else
                                declared

                        match symbol with
                        | :? INamedTypeSymbol as nt -> nt.ContainingNamespace.ToDisplayString() = nsName
                        | _ -> false

                let clash =
                    typeNames
                    |> List.exists (fun name ->
                        root.DescendantTokens()
                        |> Seq.exists (fun t ->
                            t.IsKind SyntaxKind.IdentifierToken && t.ValueText = name && not (isTheType t)))

                let placed =
                    Usings.insertionIn (Usings.usingsInsideNamespace model.Compilation) tree text nsName

                let edits =
                    match placed with
                    | Some(position, insertText) ->
                        Suggestion.insert position insertText
                        :: (group
                            |> List.map (fun s ->
                                Suggestion.replace (TextSpan.FromBounds(s.Prefix.SpanStart, s.Dot.Span.End)) ""))
                    | None -> []

                let message =
                    $"'{nsName}' is spelled out {group.Length} times: a using directive would carry it"

                let span = TextSpan.FromBounds(group.Head.Prefix.SpanStart, group.Head.Dot.Span.End)

                // a note stands as it is; a fix waits for the re-bind, all of a
                // file's together (Choice2Of2: the candidate with its edits)
                if clash then
                    Some(
                        Choice1Of2(
                            Suggestion.note
                                Code
                                (message + " (held: the file already uses a name it would shorten to)")
                                span
                        )
                    )
                elif placed.IsNone then
                    Some(Choice1Of2(Suggestion.note Code (message + " (held: the usings sit under a directive)") span))
                else
                    Some(
                        Choice2Of2(
                            {
                                Code = Code
                                Message = message
                                Span = span
                                Fixes = [ Suggestion.fix $"Add 'using {nsName};' and shorten every use" Code edits ]
                            },
                            edits
                        )
                    ))
    |> fun found ->
        let notes =
            found
            |> List.choose (function
                | Choice1Of2 note -> Some note
                | Choice2Of2 _ -> None)

        let candidates =
            found
            |> List.choose (function
                | Choice2Of2 candidate -> Some candidate
                | Choice1Of2 _ -> None)
        // a startup file spelling thirty namespaces is one re-bind, not thirty
        notes @ Guards.speculativeCheckEach model candidates
