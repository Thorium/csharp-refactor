/// CR0111 (idiom, note): `dir + "\\" + file`, `dir + "/" + file` — a path
/// joined by hand. `Path.Combine` (or `Path.Join`) spells the join, and
/// spells it for the platform. Note only: `Path.Combine` treats a rooted
/// second argument as absolute and discards the first, which the
/// concatenation does not.
///
/// Guards (FR0081's, after its false positives): BOTH separators need
/// positive path evidence — a path-flavoured name in the chain (`dir`,
/// `path`, `file`, `folder`, `directory`, `root`, `home`, `temp`), a
/// rooted literal (`C:\…`, `/usr/…`), an extension-bearing literal
/// (`.txt`, `.json`), or a literal naming a path that exists on this
/// machine — a lone backslash used to fire alone until escape-sequence
/// building (`result + "\\" + c`) showed where that goes wrong. A
/// separator only joins with text on BOTH sides (`dir + "/"` appends a
/// marker, `"/" + name` prefixes a root); dot-segments (`"./" + p`, `p +
/// "../"`) are relative-path notation `Path.Combine` cannot spell. A chain
/// opening with a forward-slash literal (`"/img/" + id`) is as likely a
/// web route and needs the stronger evidence (a rooted or
/// extension-bearing literal, or one that exists on disk) — a name is too
/// weak there (`fileId` matches "file"). URL-shaped chains (`http`,
/// `://`, `www.`) never fire, nor a chain a name binds one hop to
/// something URL-shaped. A chain only compared or searched for is a key.
/// Contexts that must stay compile-time constants — `const` initialisers,
/// attribute arguments — never get the note.
module CSharp.Refactor.PathSeparator

open System
open System.IO
open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax
open Microsoft.CodeAnalysis.Text

[<Literal>]
let Code = "CR0111"

let private pathWords =
    [ "dir"; "path"; "file"; "folder"; "directory"; "root"; "home"; "temp"; "tmp" ]

let private isStringPlus (model: SemanticModel) (b: BinaryExpressionSyntax) =
    b.IsKind SyntaxKind.AddExpression
    && (match model.GetTypeInfo(b).Type with
        | null -> false
        | t -> t.SpecialType = SpecialType.System_String)

let rec private operands (model: SemanticModel) (e: ExpressionSyntax) : ExpressionSyntax list =
    match e with
    | :? BinaryExpressionSyntax as b when isStringPlus model b -> operands model b.Left @ [ b.Right ]
    | e -> [ e ]

let private literalText (e: ExpressionSyntax) =
    match e with
    | :? LiteralExpressionSyntax as l when l.IsKind SyntaxKind.StringLiteralExpression -> Some l.Token.ValueText
    | _ -> None

let private isSeparator (s: string) = s = "\\" || s = "/"

let private urlShaped (s: string) =
    let l = s.ToLowerInvariant()
    l.Contains "://" || l.StartsWith "http" || l.Contains "www."

let private rooted (s: string) =
    (s.Length >= 3
     && Char.IsLetter s.[0]
     && s.[1] = ':'
     && (s.[2] = '\\' || s.[2] = '/'))
    || s.StartsWith "\\\\"
    || (s.StartsWith "/" && s.Length > 1 && not (s.StartsWith "//"))

let private hasExtension (s: string) =
    let name = s.Trim('/', '\\')
    let dot = name.LastIndexOf '.'

    dot > 0
    && dot < name.Length - 1
    && name.Substring(dot + 1) |> Seq.forall Char.IsLetterOrDigit
    && name.Length - dot <= 6

let private existsOnDisk (s: string) =
    try
        s.Length > 1 && (File.Exists s || Directory.Exists s)
    with _ ->
        false

let private pathNamed (e: ExpressionSyntax) =
    let name = e.ToString().ToLowerInvariant()
    pathWords |> List.exists (fun w -> name.Contains w)

/// A name bound one hop to something URL-shaped in the enclosing member.
let private boundToUrl (model: SemanticModel) (e: ExpressionSyntax) =
    match e with
    | :? IdentifierNameSyntax as id ->
        match model.GetSymbolInfo(id).Symbol with
        | :? ILocalSymbol as l ->
            l.DeclaringSyntaxReferences
            |> Seq.exists (fun r ->
                match r.GetSyntax() with
                | :? VariableDeclaratorSyntax as v when not (isNull v.Initializer) ->
                    urlShaped (v.Initializer.Value.ToString())
                | _ -> false)
        | _ -> false
    | _ -> false

let analyze (tree: SyntaxTree) (model: SemanticModel) (_ctx: RuleContext) : Suggestion list =
    let fileName = Path.GetFileNameWithoutExtension(tree.FilePath).ToLowerInvariant()

    let fileIsWeb =
        fileName.Contains "url" || fileName.Contains "route" || fileName.Contains "json"

    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun n ->
        match n with
        | :? BinaryExpressionSyntax as b when
            isStringPlus model b
            && not (
                match b.Parent with
                | :? BinaryExpressionSyntax as p -> isStringPlus model p
                | _ -> false
            )
            && not fileIsWeb
            && not (Guards.insideAttribute b)
            && not (
                b.Ancestors()
                |> Seq.exists (fun a ->
                    match a with
                    | :? LocalDeclarationStatementSyntax as d -> d.IsConst
                    | :? FieldDeclarationSyntax as f ->
                        f.Modifiers |> Seq.exists (fun m -> m.IsKind SyntaxKind.ConstKeyword)
                    | _ -> false)
            )
            ->
            let parts = operands model b
            let texts = parts |> List.map literalText

            // a separator literal with text on both sides
            let joinIndexes =
                [ 1 .. parts.Length - 2 ]
                |> List.filter (fun i ->
                    match texts.[i] with
                    | Some s when isSeparator s -> true
                    | _ -> false)

            if joinIndexes.IsEmpty then
                None
            else
                let literals = texts |> List.choose id

                let dotSegment =
                    literals
                    |> List.exists (fun s -> s.StartsWith "./" || s.EndsWith "../" || s = "..")

                let url =
                    literals |> List.exists urlShaped || parts |> List.exists (boundToUrl model)

                let opensWithSlash =
                    match texts.Head with
                    | Some s -> s.StartsWith "/"
                    | None -> false

                let strongEvidence =
                    literals |> List.exists (fun s -> rooted s || hasExtension s || existsOnDisk s)

                let nameEvidence = parts |> List.exists pathNamed

                // compared or searched for: a key, not a path
                let asKey =
                    match b.Parent with
                    | :? BinaryExpressionSyntax as p ->
                        p.IsKind SyntaxKind.EqualsExpression || p.IsKind SyntaxKind.NotEqualsExpression
                    | :? ArgumentSyntax as a ->
                        match a.Parent.Parent with
                        | :? InvocationExpressionSyntax as inv ->
                            let name = Linq.nameOf inv

                            name = "Contains"
                            || name = "ContainsKey"
                            || name = "TryGetValue"
                            || name = "Equals"
                        | _ -> false
                    | _ -> false

                let evidence = strongEvidence || (nameEvidence && not opensWithSlash)

                if dotSegment || url || asKey || not evidence then
                    None
                else
                    Some(
                        Suggestion.note
                            Code
                            "A path joined by hand: Path.Combine (or Path.Join) spells the join for the platform — mind that a rooted second argument makes Combine discard the first"
                            b.Span
                    )
        | _ -> None)
    |> List.ofSeq
