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

        // a TargetFrameworks property spread over lines leaves its whitespace in the name
        if i > 0 && name.EndsWith ")" then
            name.Substring(i + 1, name.Length - i - 2).Trim()
        else
            ""

    /// The API surface of a target framework on one scale, smallest first.
    /// .NET Framework is its version (`net462` 4.62, `net47` 4.70, `net481`
    /// 4.81); `netstandard` sits where the frameworks implementing it start
    /// (1.0-1.2 just below net45, 1.3 below net46, 1.4-2.0 between net46 and
    /// net461, 2.1 just below
    /// `netcoreapp3.0`); `netcoreapp` above every .NET Framework, `net5.0`+
    /// above that. An unknown name ranks last.
    let rank (tfm: string) : float =
        let t = (tfm.Trim().ToLowerInvariant().Split '-').[0]

        let parse (s: string) =
            match Double.TryParse(s, Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture) with
            | true, v -> Some v
            | _ -> None

        if t.StartsWith "netstandard" then
            // just below the first .NET Framework implementing it, so no tie with it,
            // and in version order among themselves
            match parse (t.Substring "netstandard".Length) with
            | Some v when v < 1.3 -> 4.49 + v / 1000.0
            | Some v when v < 1.4 -> 4.59 + v / 1000.0
            | Some v when v < 2.1 -> 4.6 + v / 1000.0
            | Some v -> 5.29 + (v - 2.1) / 1000.0
            | None -> 100.0
        elif t.StartsWith "netcoreapp" then
            match parse (t.Substring "netcoreapp".Length) with
            | Some v -> 5.0 + v / 10.0
            | None -> 100.0
        // `net4.8`, `net4.7.2`: a .NET Framework version spelled with dots
        elif
            t.StartsWith "net"
            && t.Contains "."
            && not (t.Length > 4 && t.[3] >= '1' && t.[3] <= '4' && t.[4] = '.')
        then
            match parse (t.Substring 3) with
            | Some v -> 6.0 + v
            | None -> 100.0
        elif t.StartsWith "net" then
            // net20, net35, net403, net462, net481: digits are major, minor, patch
            let digits = (t.Substring 3).Replace(".", "")

            if digits.Length >= 2 && digits |> Seq.forall Char.IsDigit then
                let d (k: int) =
                    if k < digits.Length then
                        float (int digits.[k] - int '0')
                    else
                        0.0

                d 0 + d 1 / 10.0 + d 2 / 100.0
            else
                100.0
        else
            100.0

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
                && (let theirs = rank (frameworkOf p)
                    // equal ranks (`net8.0` beside `net8.0-windows`) break by name, so
                    // exactly one of the two is the narrower
                    theirs < own
                    || (theirs = own && String.CompareOrdinal(frameworkOf p, frameworkOf project) < 0)))
            |> List.ofSeq

    let private holds (project: Project) (path: string) =
        project.Solution.GetDocumentIdsWithFilePath path
        |> Seq.exists (fun pid -> pid.ProjectId = project.Id)

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
                                |> Seq.filter (fun pid -> pid.ProjectId = p.Id)
                                |> Seq.tryPick (fun pid ->
                                    match p.GetDocument(pid).TryGetSyntaxTree() with
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

/// A file several projects compile (a `<Compile Include="..\Shared\X.cs" />`
/// link) is one text under as many compilations: each has its own target
/// framework, language version, global usings and files that use the
/// file's declarations. A rule sees one of them. A fix there holds only
/// where every other project compiling the file offers the same fix for the
/// same finding on its own copy; otherwise the finding stays as a note.
module Linked =
    /// The other projects compiling the tree's file, narrowest framework
    /// first: the one likeliest to refuse answers first.
    let projectsOf (solution: Solution) (tree: SyntaxTree) : Project list =
        References.linkedTo solution tree
        |> List.sortBy (Flavors.frameworkOf >> Flavors.rank)

    /// What `project` offers for its own copy of the file at `path`, from
    /// the rules reporting `codes`; `context` is the host's rule context for
    /// a tree of that project.
    let offeredIn
        (context: Project -> Compilation -> SyntaxTree -> RuleContext)
        (cancellation: Threading.CancellationToken)
        (path: string)
        (codes: Set<string>)
        (project: Project)
        : Suggestion list =
        project.Solution.GetDocumentIdsWithFilePath path
        |> Seq.tryFind (fun pid -> pid.ProjectId = project.Id)
        |> Option.map (fun pid ->
            let tree =
                project.GetDocument(pid).GetSyntaxTreeAsync(cancellation).GetAwaiter().GetResult()

            let compilation = project.GetCompilationAsync(cancellation).GetAwaiter().GetResult()

            if isNull tree || isNull compilation then
                []
            else
                Rules.forCodes
                    cancellation
                    codes
                    tree
                    (compilation.GetSemanticModel(tree, false))
                    (context project compilation tree))
        |> Option.defaultValue []

    /// The suggestions with only the fixes every other project compiling
    /// the file offers too. A file no other project compiles, and one with
    /// no fix, cost nothing; the other compilations are asked for the codes
    /// that carry a fix, and a fix whose edits all sit in code a project
    /// leaves out (`#if`) is not that project's to refuse.
    let everyCompilation
        (context: Project -> Compilation -> SyntaxTree -> RuleContext)
        (cancellation: Threading.CancellationToken)
        (solution: Solution)
        (tree: SyntaxTree)
        (suggestions: Suggestion list)
        : Suggestion list =
        let codes =
            suggestions
            |> List.filter (fun s -> not s.Fixes.IsEmpty)
            |> List.map (fun s -> s.Code)
            |> Set.ofList

        if codes.IsEmpty then
            suggestions
        else
            match projectsOf solution tree with
            | [] -> suggestions
            | linked ->
                Flavors.narrowestOnly linked (offeredIn context cancellation tree.FilePath codes) tree suggestions
