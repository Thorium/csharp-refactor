/// The projects around a project, read from the files alone — no MSBuild,
/// nothing built: what a solution lists, what a project references. The
/// loaded workspace answers the same for C# projects; this module exists
/// for what it cannot load — an F# or VB project of the same solution that
/// references the C# one, a caller no pass can rewrite and one the public
/// surface must be held for. Ported from fsharp-refactor.
module CSharp.Refactor.Tool.Workspace

open System
open System.IO
open System.Text.RegularExpressions

let private projectExtensions = [ ".csproj"; ".fsproj"; ".vbproj" ]

let private isProjectFile (path: string) =
    projectExtensions
    |> List.exists (fun ext -> path.EndsWith(ext, StringComparison.OrdinalIgnoreCase))

/// The only kind whose compilation this tool loads and rewrites.
let isCSharpProject (path: string) =
    path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)

let samePath (a: string) (b: string) =
    String.Equals(Path.GetFullPath a, Path.GetFullPath b, StringComparison.OrdinalIgnoreCase)

/// The project paths a solution lists — every language — resolved against
/// the solution's own directory and filtered to files that exist. `.slnx`
/// is XML with one `Path="..."` per project; the classic `.sln` has one
/// `Project(...) = "Name", "path", "{guid}"` line per entry, and solution
/// folders in the same shape without an extension, which the extension
/// filter drops.
let projectsInSolution (solutionPath: string) : string list =
    let dir = Path.GetDirectoryName(Path.GetFullPath solutionPath)

    let text =
        try
            File.ReadAllText solutionPath
        with
        | :? IOException
        | :? UnauthorizedAccessException -> ""

    let paths =
        if solutionPath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) then
            Regex.Matches(text, "Path\\s*=\\s*\"([^\"]+)\"")
            |> Seq.map (fun m -> m.Groups.[1].Value)
        else
            Regex.Matches(text, "\"([^\"]+\\.(?:fs|cs|vb)proj)\"")
            |> Seq.map (fun m -> m.Groups.[1].Value)

    paths
    |> Seq.filter isProjectFile
    |> Seq.map (fun p -> Path.GetFullPath(Path.Combine(dir, p.Replace('\\', Path.DirectorySeparatorChar))))
    |> Seq.filter File.Exists
    |> Seq.distinctBy (fun p -> p.ToLowerInvariant())
    |> List.ofSeq

/// How a `<ProjectReference Include="...">` names its target. An Include
/// can carry several paths separated by semicolons, and the two MSBuild
/// properties that mean "this directory" resolve; a path built from any
/// other property cannot be resolved without MSBuild - but it still names
/// a project, and a referencer passed over is a call site missed. So such
/// a reference keeps the file name it ends in (`$(FSharpSourcesRoot)\
/// FSharp.Core\FSharp.Core.fsproj`, twenty-eight times in dotnet/fsharp)
/// and matches by that, and one with no recognisable file name at all
/// (`$(Ref)`) is taken to reference ANY project of the workspace: the
/// cost of reading a sibling that turns out not to call is a typecheck,
/// the cost of missing one is a broken build.
type ProjectReference =
    /// A path on disk.
    | Resolved of string
    /// Only the project file's own name is known.
    | ByName of string
    /// Nothing recognisable: may be any project.
    | Unresolvable

/// A project file's text with its XML comments taken out, for every read
/// that matches the text rather than evaluating it: an element an author
/// commented away is not one MSBuild sees. welendus's WelendusLogic.fsproj
/// carries `<!-- <TargetFrameworks>netstandard2.0;net48</TargetFrameworks>
/// -->` above the live element; the text match took the commented one
/// first and the run asked for a net48 pass no restore had produced
/// (NETSDK1005). A commented-out ProjectReference would likewise have made
/// a referencer of a project that no longer links.
let projectTextWithoutComments (text: string) =
    Regex.Replace(text, @"<!--.*?-->", "", RegexOptions.Singleline)

/// Every reference of a project file, in the shapes above.
let projectReferenceShapesOf (projectPath: string) : ProjectReference list =
    let text =
        try
            projectTextWithoutComments (File.ReadAllText projectPath)
        with
        | :? IOException
        | :? UnauthorizedAccessException -> ""

    let dir = Path.GetDirectoryName(Path.GetFullPath projectPath)

    Regex.Matches(text, "<ProjectReference\\s[^>]*?Include\\s*=\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase)
    |> Seq.collect (fun m -> m.Groups.[1].Value.Split(';', StringSplitOptions.RemoveEmptyEntries))
    |> Seq.map (fun raw ->
        let reference =
            raw
                .Trim()
                .Replace("$(MSBuildThisFileDirectory)", dir + string Path.DirectorySeparatorChar)
                .Replace("$(MSBuildProjectDirectory)", dir)

        if reference.Contains "$(" then
            let name = reference.Substring(reference.LastIndexOfAny [| '\\'; '/' |] + 1)

            if isProjectFile name && not (name.Contains "$(") then
                ByName name
            else
                Unresolvable
        else
            try
                Resolved(Path.GetFullPath(Path.Combine(dir, reference.Replace('\\', Path.DirectorySeparatorChar))))
            with
            | :? ArgumentException
            | :? PathTooLongException
            | :? NotSupportedException -> Unresolvable)
    |> Seq.distinct
    |> List.ofSeq

/// The references that resolve to a path on disk.
let projectReferencesOf (projectPath: string) : string list =
    projectReferenceShapesOf projectPath
    |> List.choose (function
        | Resolved p -> Some p
        | ByName _
        | Unresolvable -> None)
    |> List.distinctBy (fun p -> p.ToLowerInvariant())

/// Does a reference name this project?
let private namesProject (reference: ProjectReference) (project: string) =
    match reference with
    | Resolved p -> samePath p project
    | ByName name -> String.Equals(name, Path.GetFileName project, StringComparison.OrdinalIgnoreCase)
    | Unresolvable -> true

/// The solutions in `dir` that list `project`, nearest first as the caller
/// walks up.
let private solutionsListing (project: string) (dir: string) =
    try
        [
            yield! Directory.EnumerateFiles(dir, "*.slnx")
            yield! Directory.EnumerateFiles(dir, "*.sln")
        ]
        |> List.filter (fun sln -> projectsInSolution sln |> List.exists (samePath project))
    with
    | :? IOException
    | :? UnauthorizedAccessException -> []

/// The projects that share a workspace with `project`, the project itself
/// included, or None when there is no workspace to enumerate: the solution
/// the run was pointed at, else the nearest ancestor directory's solutions
/// that list the project (stopping at the repository root), else the
/// directory the run was pointed at when the project sits under it.
let workspaceOf (runTarget: string) (project: string) : string list option =
    let target =
        try
            Some(Path.GetFullPath runTarget)
        with
        | :? ArgumentException
        | :? PathTooLongException
        | :? NotSupportedException -> None

    let solutionRun =
        target
        |> Option.filter (fun t ->
            File.Exists t
            && (t.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                || t.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)))
        |> Option.map projectsInSolution
        |> Option.filter (List.exists (samePath project))

    match solutionRun with
    | Some projects -> Some projects
    | None ->
        let mutable dir = Path.GetDirectoryName(Path.GetFullPath project)
        let mutable found = None
        let mutable atEdge = false

        while found.IsNone && not atEdge && not (String.IsNullOrEmpty dir) do
            match solutionsListing project dir with
            | [] -> ()
            | solutions ->
                found <-
                    solutions
                    |> List.collect projectsInSolution
                    |> List.distinctBy (fun p -> p.ToLowerInvariant())
                    |> Some

            let parent = Path.GetDirectoryName dir

            atEdge <-
                Directory.Exists(Path.Combine(dir, ".git"))
                || String.IsNullOrEmpty parent
                || parent = Path.GetPathRoot dir

            dir <- parent

        match found with
        | Some projects -> Some projects
        | None ->
            let under (root: string) (path: string) =
                let root = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)

                (Path.GetFullPath path)
                    .StartsWith(root + string Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)

            match target with
            | Some t when Directory.Exists t && under t project ->
                projectExtensions
                |> List.collect (fun ext -> FileWalk.files ("*" + ext) t |> List.ofSeq)
                |> List.map Path.GetFullPath
                |> List.distinctBy (fun p -> p.ToLowerInvariant())
                |> Some
            | _ -> None

/// The projects of `workspace` that can see `project`'s declarations:
/// those referencing it directly, and those referencing one of THOSE — an
/// SDK project reference is transitive. Order is stable for output.
let referencersOf (workspace: string list) (project: string) : string list =
    let references = workspace |> List.map (fun p -> p, projectReferenceShapesOf p)

    let mutable reached = [ project ]
    let mutable frontier = [ project ]

    while not frontier.IsEmpty do
        let next =
            references
            |> List.filter (fun (p, refs) ->
                not (reached |> List.exists (samePath p))
                && refs |> List.exists (fun r -> frontier |> List.exists (namesProject r)))
            |> List.map fst

        reached <- reached @ next
        frontier <- next

    reached |> List.filter (fun p -> not (samePath p project))

/// A source file as MSBuildWorkspace's own loader reads it: a byte order
/// mark decides, else UTF-8 where the bytes are valid UTF-8, else the
/// system code page (Windows-1252 here). The public `SourceText.From`
/// overloads decode a file without a mark as UTF-8 and replace every
/// invalid byte with U+FFFD — how a legacy file's `ä` came back as `�` —
/// so the fallback is done here, as Roslyn's internal loader does it. The
/// text carries the encoding it was decoded with; the sweep writes it back
/// the same way.
let readSource (path: string) : Microsoft.CodeAnalysis.Text.SourceText =
    // the code page the fallback decodes with (registering twice is harmless)
    Text.Encoding.RegisterProvider Text.CodePagesEncodingProvider.Instance
    let bytes = File.ReadAllBytes path

    let decode (encoding: Text.Encoding) =
        use stream = new MemoryStream(bytes)

        Microsoft.CodeAnalysis.Text.SourceText.From(
            stream,
            encoding,
            Microsoft.CodeAnalysis.Text.SourceHashAlgorithm.Sha1,
            false,
            true
        )

    try
        decode (Text.UTF8Encoding(false, true))
    with :? Text.DecoderFallbackException ->
        decode (Text.Encoding.GetEncoding 0)
