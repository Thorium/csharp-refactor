/// Where a new `using` goes in a file: among the usings in alphabetical
/// order within its family (`System` first), else after the last one,
/// else at the top; a namespace with its own usings takes it there. Under
/// a `#if` there is no one place, and the caller stands down.
module CSharp.Refactor.Usings

open System
open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax
open Microsoft.CodeAnalysis.Text

/// Is the namespace already imported at the position (a `using`, a global
/// using, an implicit using), or the type resolvable bare?
let imported (model: SemanticModel) (position: int) (ns: string) (typeName: string) =
    model.LookupNamespacesAndTypes(position, name = typeName)
    |> Seq.exists (fun s -> s :? INamedTypeSymbol && s.ContainingNamespace.ToDisplayString() = ns)

/// Does the compilation keep its usings inside the namespace declaration
/// (StyleCop's SA1200 convention)? Read off the files that have usings at
/// all, once per compilation, for the file that has none yet.
let private insideNamespaceConvention =
    System.Runtime.CompilerServices.ConditionalWeakTable<Compilation, Lazy<bool>>()

let usingsInsideNamespace (compilation: Compilation) : bool =
    insideNamespaceConvention
        .GetValue(
            compilation,
            fun c ->
                lazy
                    (let mutable inside = 0
                     let mutable outside = 0

                     for t in c.SyntaxTrees do
                         match t.GetRoot() with
                         | :? CompilationUnitSyntax as root ->
                             if root.Usings.Count > 0 then
                                 outside <- outside + 1
                             elif
                                 root.Members
                                 |> Seq.exists (fun m ->
                                     match m with
                                     | :? BaseNamespaceDeclarationSyntax as n -> n.Usings.Count > 0
                                     | _ -> false)
                             then
                                 inside <- inside + 1
                         | _ -> ()

                     inside > outside)
        )
        .Value

/// Where the `using` goes, and with what line ending. `insideNamespace` is
/// the compilation's convention for a file that has no usings yet.
let insertionIn (insideNamespace: bool) (tree: SyntaxTree) (text: SourceText) (nsName: string) : (int * string) option =
    let root = tree.GetRoot() :?> CompilationUnitSyntax
    let newUsing = $"using {nsName};"
    let newline = Text.newlineAt text 0

    let usingsOf (usings: SyntaxList<UsingDirectiveSyntax>) =
        usings
        |> Seq.filter (fun u ->
            isNull u.Alias
            && u.StaticKeyword.IsKind SyntaxKind.None
            && u.GlobalKeyword.IsKind SyntaxKind.None)
        |> List.ofSeq

    let family (name: string) =
        if name = "System" || name.StartsWith "System." then
            0
        else
            1

    // a using block under `#if` would take the new line into one
    // configuration only; the speculative check sees just the current one
    let underDirective (usings: UsingDirectiveSyntax list) =
        usings
        |> List.exists (fun u ->
            u.GetLeadingTrivia() |> Seq.exists (fun t -> t.IsDirective)
            || u.GetTrailingTrivia() |> Seq.exists (fun t -> t.IsDirective))

    let place (usings: UsingDirectiveSyntax list) (fallback: int) =
        match usings with
        | [] -> Some(fallback, newUsing + newline)
        | _ when underDirective usings -> None
        | _ ->
            let after =
                usings
                |> List.filter (fun u ->
                    let n = u.Name.ToString()

                    family n < family nsName
                    || (family n = family nsName && String.CompareOrdinal(n, nsName) < 0))
                |> List.tryLast

            match after with
            | Some u ->
                let line = text.Lines.GetLineFromPosition u.Span.End
                Some(line.EndIncludingLineBreak, Text.leadingWhitespace text u.SpanStart + newUsing + newline)
            | None ->
                let first = List.head usings
                let line = text.Lines.GetLineFromPosition first.SpanStart
                Some(line.Start, Text.leadingWhitespace text first.SpanStart + newUsing + newline)

    let topUsings = usingsOf root.Usings

    if not topUsings.IsEmpty then
        place topUsings 0
    else
        // a namespace with its own usings
        let ns =
            root.Members
            |> Seq.tryPick (fun m ->
                match m with
                | :? BaseNamespaceDeclarationSyntax as n when not (usingsOf n.Usings).IsEmpty -> Some n
                | _ -> None)

        match ns with
        | Some n -> place (usingsOf n.Usings) 0
        | None ->
            // the file's one namespace, where the compilation keeps its usings
            let soleNamespace =
                match root.Members |> List.ofSeq with
                | [ :? BaseNamespaceDeclarationSyntax as n ] when insideNamespace -> Some n
                | _ -> None

            match soleNamespace with
            | Some(:? FileScopedNamespaceDeclarationSyntax as n) ->
                let line = text.Lines.GetLineFromPosition n.SemicolonToken.Span.End
                Some(line.EndIncludingLineBreak, newline + newUsing + newline)
            | Some(:? NamespaceDeclarationSyntax as n) ->
                let line = text.Lines.GetLineFromPosition n.OpenBraceToken.Span.End

                let indent =
                    match n.Members |> Seq.tryHead with
                    | Some m -> Text.leadingWhitespace text m.SpanStart
                    | None -> "    "

                Some(line.EndIncludingLineBreak, indent + newUsing + newline + newline)
            | _ ->
                // the top of the file, after leading comments and directives
                let firstToken = root.GetFirstToken()
                let line = text.Lines.GetLineFromPosition firstToken.SpanStart
                Some(line.Start, newUsing + newline + newline)

/// Where the `using` goes for a host without a compilation: the top of the
/// file when the file has no usings.
let insertion (tree: SyntaxTree) (text: SourceText) (nsName: string) : (int * string) option =
    insertionIn false tree text nsName


/// The edit that imports a namespace, or none when it is imported already
/// (an empty list) or cannot be placed (None).
let importEdit
    (model: SemanticModel)
    (tree: SyntaxTree)
    (position: int)
    (ns: string)
    (typeName: string)
    : TextEdit list option =
    if imported model position ns typeName then
        Some []
    else
        insertionIn (usingsInsideNamespace model.Compilation) tree (tree.GetText()) ns
        |> Option.map (fun (at, text) -> [ Suggestion.insert at text ])
