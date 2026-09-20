/// Turn whatever the user pointed at into the compilations to run: a
/// project directly, every C# project in a solution, everything a glob
/// matches, a directory's solution or projects, or a workspace of
/// checkouts, each resolved on its own. Ported from fsharp-refactor.
module CSharp.Refactor.Tool.Targets

open System
open System.IO

/// One compilation to work on: a whole project, or — when a single source
/// file was named — that project analysed but only that one file edited.
[<RequireQualifiedAccess>]
type Target = Project of project: string * onlyFile: string option

let projectOf (t: Target) =
    match t with
    | Target.Project(p, _) -> p

/// The project a source file belongs to, searching outwards: a C# project
/// compiles every .cs beneath its own directory by default, so the nearest
/// project whose directory contains the file wins, and one that lists the
/// file by name (a linked file) wins over that.
let private owningProject (sourceFile: string) =
    let full = Path.GetFullPath sourceFile
    let name = Path.GetFileName full

    let lists (project: string) =
        try
            (File.ReadAllText project).Contains name
        with
        | :? IOException
        | :? UnauthorizedAccessException -> false

    let contains (project: string) =
        let dir =
            Path.GetDirectoryName(Path.GetFullPath project).TrimEnd Path.DirectorySeparatorChar

        full.StartsWith(dir + string Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)

    let mutable dir = Path.GetDirectoryName full
    let mutable found = None
    let mutable atEdge = false
    let mutable levels = 0

    while found.IsNone && not atEdge && levels < 6 && not (String.IsNullOrEmpty dir) do
        let here =
            try
                Directory.EnumerateFiles(dir, "*.csproj") |> List.ofSeq
            with
            | :? IOException
            | :? UnauthorizedAccessException -> []

        found <-
            match here |> List.tryFind lists with
            | Some p -> Some p
            | None -> here |> List.tryFind contains

        let parent = Path.GetDirectoryName dir

        atEdge <-
            Directory.Exists(Path.Combine(dir, ".git"))
            || String.IsNullOrEmpty parent
            || parent = Path.GetPathRoot dir

        dir <- parent
        levels <- levels + 1

    found

let private projectsInSolution (solutionPath: string) =
    Workspace.projectsInSolution solutionPath
    |> List.filter Workspace.isCSharpProject

/// A C# script is a compilation of its own (Scripts.fs reads it).
let isScript (path: string) =
    path.EndsWith(".csx", StringComparison.OrdinalIgnoreCase)

let private targetOf (path: string) =
    if Workspace.isCSharpProject path || isScript path then
        Some(Target.Project(path, None))
    else
        None

let resolveTargets (raw: string) : Result<Target list, string> =
    let expandGlob (pattern: string) =
        let normalized = pattern.Replace('\\', '/')
        let starIndex = normalized.IndexOf '*'

        let root =
            let head = normalized.Substring(0, starIndex)
            let slash = head.LastIndexOf '/'

            if slash >= 0 then head.Substring(0, slash) else "."

        let leaf = Path.GetFileName normalized

        if Directory.Exists root then
            FileWalk.files leaf root |> List.ofSeq
        else
            []

    let rec fromDirectory (dir: string) =
        let solutionsIn (d: string) =
            [
                yield! Directory.EnumerateFiles(d, "*.slnx")
                yield! Directory.EnumerateFiles(d, "*.sln")
            ]

        let solutions = solutionsIn dir

        // a workspace of checkouts — C:\git — has no solution of its own,
        // but its children do: each child is then resolved as its own
        // target set, so every checkout's solutions are honoured
        let checkouts =
            if solutions.IsEmpty then
                Directory.EnumerateDirectories dir
                |> Seq.filter (fun child ->
                    let name = Path.GetFileName child

                    not (name.StartsWith '.')
                    && not (List.contains name [ "node_modules"; "bin"; "obj"; "packages" ]))
                |> List.ofSeq
            else
                []

        let workspace =
            solutions.IsEmpty
            && checkouts |> List.exists (fun child -> not (solutionsIn child).IsEmpty)

        if workspace then
            let named =
                checkouts |> List.filter (fun c -> not (solutionsIn c).IsEmpty) |> List.length

            printfn $"({named} checkouts with solutions under {dir} — analysing each checkout on its own)"

            checkouts
            |> List.collect (fun child ->
                try
                    fromDirectory child
                with ex ->
                    eprintfn $"  ({Path.GetFileName child}: skipped — {ex.Message})"
                    [])
        else
            // loose scripts are code too: a build.csx never appears in a solution
            let scripts =
                FileWalk.files "*.csx" dir
                |> Seq.map (fun s -> Target.Project(s, None))
                |> List.ofSeq

            let projects =
                match solutions with
                | [] ->
                    FileWalk.files "*.csproj" dir
                    |> Seq.map (fun p -> Target.Project(p, None))
                    |> List.ofSeq
                | _ ->
                    if solutions.Length > 1 then
                        printfn $"({solutions.Length} solutions here — analysing the union of their projects)"

                    solutions
                    |> List.collect projectsInSolution
                    |> List.distinctBy (fun p -> Path.GetFullPath(p).ToLowerInvariant())
                    |> List.map (fun p -> Target.Project(p, None))

            projects @ scripts

    if raw.Contains '*' || raw.Contains '?' then
        match expandGlob raw |> List.choose targetOf with
        | [] -> Error $"'{raw}' matched no .csproj or .csx files."
        | targets -> Ok targets
    elif Directory.Exists raw then
        match fromDirectory raw with
        | [] -> Error $"No solution, C# project or .csx script found in '{raw}'."
        | targets -> Ok targets
    elif not (File.Exists raw) then
        Error $"No such file or directory: {raw}"
    else
        match (Path.GetExtension raw).ToLowerInvariant() with
        | ".sln"
        | ".slnx" ->
            match projectsInSolution raw |> List.map (fun p -> Target.Project(p, None)) with
            | [] -> Error $"'{Path.GetFileName raw}' lists no C# projects."
            | targets -> Ok targets
        | ".slnf" -> Error "Solution filters are not supported; pass the solution or a project."
        | ".csx" -> Ok [ Target.Project(Path.GetFullPath raw, None) ]
        | ".cs" ->
            match owningProject raw with
            | Some project -> Ok [ Target.Project(project, Some(Path.GetFullPath raw)) ]
            | None ->
                Error
                    $"No .csproj found above '{Path.GetFileName raw}'. A source file is not a compilation on its own — it needs its project for references."
        | _ ->
            match targetOf raw with
            | Some target -> Ok [ target ]
            | None ->
                Error
                    $"Don't know what to do with '{Path.GetFileName raw}' — pass a .csproj, a .csx script, a solution, a directory or a glob."
