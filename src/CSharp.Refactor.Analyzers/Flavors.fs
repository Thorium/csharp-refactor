/// A multi-targeted project is one compilation per target framework, and a
/// file outside any `#if` is compiled by each of them. A rule sees one
/// compilation: what it offers there - `init`, an overload, a type only the
/// newer framework has - holds for that framework. The narrowest framework's
/// fixes hold for the wider ones, so a host with a `Solution` analyses
/// narrowest first and takes from a wider flavor only the fixes whose every
/// edit sits in code no narrower flavor compiles: behind an `#if` they do
/// not take, or in a file they do not have.
namespace CSharp.Refactor.Roslyn

open System
open System.Collections.Generic
open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax
open Microsoft.CodeAnalysis.Text
open CSharp.Refactor

module Flavors =
    /// The framework a workspace project's name carries: a multi-targeted
    /// project's flavors are named `Name(net8.0)`.
    let frameworkOf (project: Project) =
        let name =
            if isNull project || isNull project.Name then
                ""
            else
                project.Name

        let i = name.LastIndexOf '('

        if i > 0 && name.EndsWith ")" then
            name.Substring(i + 1, name.Length - i - 2)
        else
            ""

    /// Narrowest surface first: netstandard, .NET Framework, .NET Core, .NET.
    let rank (tfm: string) =
        let t = (tfm.ToLowerInvariant().Split '-').[0]
        let digits = String(t |> Seq.filter Char.IsDigit |> Seq.toArray)

        let version =
            match Int32.TryParse digits with
            | true, v -> v
            | false, _ -> 0

        if t.StartsWith "netstandard" then 0, version
        elif t.StartsWith "netcoreapp" then 2, version
        elif t.StartsWith "net" && t.Contains "." then 3, version
        else 1, version

    /// The other flavors of the project's file with a narrower framework.
    let narrowerOf (project: Project) : Project list =
        let own = rank (frameworkOf project)

        if isNull project.FilePath || frameworkOf project = "" then
            []
        else
            project.Solution.Projects
            |> Seq.filter (fun p ->
                p.Id <> project.Id
                && String.Equals(p.FilePath, project.FilePath, StringComparison.OrdinalIgnoreCase)
                && frameworkOf p <> ""
                && rank (frameworkOf p) < own)
            |> List.ofSeq

    let private holds (project: Project) (path: string) =
        project.Solution.GetDocumentIdsWithFilePath path
        |> Seq.exists (fun id -> id.ProjectId = project.Id)

    /// Does the tree branch on a compilation symbol anywhere?
    let branches (tree: SyntaxTree) =
        let root = tree.GetRoot()

        root.ContainsDirectives
        && not (isNull (root.GetFirstDirective(fun d -> d :? IfDirectiveTriviaSyntax)))

    /// A tree a narrower flavor compiles exactly as this one does: no `#if`
    /// in it. That flavor's sweep is this one's.
    let sweptBy (narrower: Project list) (tree: SyntaxTree) =
        not (String.IsNullOrEmpty tree.FilePath)
        && not (branches tree)
        && narrower |> List.exists (fun p -> holds p tree.FilePath)

    /// The suggestions with only the fixes that hold for every framework. A
    /// fix with an edit in code a narrower flavor compiles too stays where
    /// that flavor offers the same fix for the same finding, and is dropped
    /// otherwise, the finding staying as a note. `narrower` are the flavors
    /// the host vouches for, `offered` what each of them suggests for this
    /// file: the tool sweeps them first and has applied what they offer, so
    /// it answers nothing; an editor runs the rules on each.
    let narrowestOnly
        (narrower: Project list)
        (offered: Project -> Suggestion list)
        (tree: SyntaxTree)
        (suggestions: Suggestion list)
        : Suggestion list =
        if narrower.IsEmpty || suggestions |> List.forall (fun s -> s.Fixes.IsEmpty) then
            suggestions
        else
            // the file as a narrower flavor parses it: the same text under
            // that flavor's symbols. Another file's text is the solution's;
            // one not parsed yet counts as compiled there
            let parsed = Dictionary<ProjectId * string, SyntaxTree option option>()

            let treeIn (p: Project) (path: string) : SyntaxTree option option =
                let key = p.Id, path.ToLowerInvariant()

                match parsed.TryGetValue key with
                | true, found -> found
                | _ ->
                    let own = String.Equals(path, tree.FilePath, StringComparison.OrdinalIgnoreCase)

                    let found =
                        if not (holds p path) then
                            None
                        else
                            match p.ParseOptions with
                            | :? CSharpParseOptions as options when own ->
                                Some(Some(CSharpSyntaxTree.ParseText(tree.GetText(), options, path)))
                            | _ ->
                                p.Solution.GetDocumentIdsWithFilePath path
                                |> Seq.filter (fun id -> id.ProjectId = p.Id)
                                |> Seq.tryPick (fun id ->
                                    match p.GetDocument(id).TryGetSyntaxTree() with
                                    | true, t -> Some t
                                    | _ -> None)
                                |> Some

                    parsed.[key] <- found
                    found

            let disabledIn (other: SyntaxTree) (span: TextSpan) =
                let trivia = other.GetRoot().FindTrivia span.Start
                trivia.IsKind SyntaxKind.DisabledTextTrivia && trivia.FullSpan.Contains span

            let compiledIn (p: Project) (edit: TextEdit) =
                match treeIn p (edit.File |> Option.defaultValue tree.FilePath) with
                | None -> false
                | Some None -> true
                | Some(Some other) -> not (disabledIn other edit.Span)

            let theirs = Dictionary<ProjectId, Suggestion list>()

            let offeredBy (p: Project) (s: Suggestion) (fix: Fix) =
                let suggestions =
                    match theirs.TryGetValue p.Id with
                    | true, found -> found
                    | _ ->
                        let found = offered p
                        theirs.[p.Id] <- found
                        found

                suggestions
                |> List.exists (fun o ->
                    o.Code = s.Code
                    && o.Span = s.Span
                    && o.Fixes |> List.exists (fun f -> f.Key = fix.Key && f.Edits = fix.Edits))

            suggestions
            |> List.map (fun s ->
                { s with
                    Fixes =
                        s.Fixes
                        |> List.filter (fun fix ->
                            narrower
                            |> List.forall (fun p ->
                                not (fix.Edits |> List.exists (compiledIn p)) || offeredBy p s fix))
                })
