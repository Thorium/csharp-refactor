/// The projects around a project, read from the files alone — no MSBuild,
/// nothing built: what a solution lists, what a project references. The
/// loaded workspace answers the same for C# projects; this module exists
/// for what it cannot load — an F# or VB project of the same solution that
/// references the C# one, a caller no pass can rewrite and one the public
/// surface must be held for. Mirrors fsharp-refactor's.
module CSharp.Refactor.Tool.Workspace

open System
open System.IO
open System.Text.RegularExpressions
open Microsoft.CodeAnalysis.Text

let private projectExtensions = [ ".csproj"; ".fsproj"; ".vbproj" ]

let private isProjectFile (path: string) =
    projectExtensions
    |> List.exists (fun ext -> path.EndsWith(ext, StringComparison.OrdinalIgnoreCase))

/// The only kind whose compilation this tool loads and rewrites.
let isCSharpProject (path: string) =
    path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)

let samePath (a: string) (b: string) =
    String.Equals(Path.GetFullPath a, Path.GetFullPath b, StringComparison.OrdinalIgnoreCase)

let private fscsvbprojRegex = Regex "\"([^\"]+\\.(?:fs|cs|vb)proj)\""
let private pathssRegex = Regex "Path\\s*=\\s*\"([^\"]+)\""

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
            pathssRegex.Matches text |> Seq.map (fun m -> m.Groups.[1].Value)
        else
            fscsvbprojRegex.Matches text |> Seq.map (fun m -> m.Groups.[1].Value)

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
/// FSharp.Core\FSharp.Core.fsproj`) and matches by that, and one with no recognisable file name at all
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
/// commented away is not one MSBuild sees. A `<!--
/// <TargetFrameworks>netstandard2.0;net48</TargetFrameworks> -->` above the
/// live element would match first and ask for a net48 pass no restore
/// produced (NETSDK1005); a commented-out ProjectReference would likewise
/// make a referencer of a project that does not link.
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

    // the levels reached, newest first: joined once at the end, where
    // appending each level to the whole would copy everything reached so far
    let mutable levels = [ [ project ] ]
    let mutable frontier = [ project ]

    while not frontier.IsEmpty do
        let next =
            references
            |> List.filter (fun (p, refs) ->
                not (levels |> List.exists (List.exists (samePath p)))
                && refs |> List.exists (fun r -> frontier |> List.exists (namesProject r)))
            |> List.map fst

        levels <- next :: levels
        frontier <- next

    levels
    |> List.rev
    |> List.concat
    |> List.filter (fun p -> not (samePath p project))

let private assemblyNameOfRegex =
    Regex "<AssemblyName>\\s*([^<]+?)\\s*</AssemblyName>"

/// The name of the assembly a project builds: its `<AssemblyName>` when the
/// project spells one out, else the project file's own name - the SDK
/// default, and the name a HintPath's dll carries. Mirrors fsharp-refactor's.
let assemblyNameOf (projectPath: string) : string =
    let text =
        try
            projectTextWithoutComments (File.ReadAllText projectPath)
        with
        | :? IOException
        | :? UnauthorizedAccessException -> ""

    let m = assemblyNameOfRegex.Match text

    if m.Success && not (m.Groups.[1].Value.Contains "$(") then
        m.Groups.[1].Value
    else
        Path.GetFileNameWithoutExtension projectPath

/// Does a project's text reference an assembly's dll directly - a
/// `<Reference Include="Name">` (or `"Name, Version=..."`, or a path ending
/// in `Name.dll`), or one whose HintPath ends in `Name.dll`? Such a project
/// compiles against the dll whatever its ProjectReferences say. Mirrors
/// fsharp-refactor's.
let private referencesAssemblyDirectly (text: string) (assemblyName: string) =
    let namesAssembly (value: string) =
        let value = value.Trim()
        let fileName = Path.GetFileName(value.Replace('\\', '/'))

        String.Equals(value.Split(',').[0].Trim(), assemblyName, StringComparison.OrdinalIgnoreCase)
        || String.Equals(fileName, assemblyName + ".dll", StringComparison.OrdinalIgnoreCase)

    Regex.Matches(
        text,
        "<Reference\\b[^>]*?(?:/>|>.*?</Reference\\s*>)",
        RegexOptions.IgnoreCase ||| RegexOptions.Singleline
    )
    |> Seq.exists (fun element ->
        let includeAttribute =
            Regex.Match(element.Value, "Include\\s*=\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase)

        let hintPath =
            Regex.Match(element.Value, "<HintPath>\\s*([^<]+?)\\s*</HintPath>", RegexOptions.IgnoreCase)

        (includeAttribute.Success && namesAssembly includeAttribute.Groups.[1].Value)
        || (hintPath.Success && namesAssembly hintPath.Groups.[1].Value))

/// The repository a path sits in: the nearest ancestor directory holding
/// `.git`, or None outside one.
let private repositoryRootOf (path: string) : string option =
    let mutable dir = Path.GetDirectoryName(Path.GetFullPath path)
    let mutable found = None

    while found.IsNone && not (String.IsNullOrEmpty dir) do
        if
            Directory.Exists(Path.Combine(dir, ".git"))
            || File.Exists(Path.Combine(dir, ".git"))
        then
            found <- Some dir
        else
            dir <- Path.GetDirectoryName dir

    found

/// Every project file of a repository with its comment-free text, read once
/// per run and root.
let private repositoryProjects =
    System.Collections.Concurrent.ConcurrentDictionary<string, (string * string) list>(StringComparer.OrdinalIgnoreCase)

/// Forgets the repositories' project files: a long-lived host (the MCP
/// server) starts each run from the files as they are now.
let resetRepositoryProjects () = repositoryProjects.Clear()

let private projectsUnder (root: string) =
    repositoryProjects.GetOrAdd(
        root,
        fun root ->
            projectExtensions
            |> List.collect (fun ext -> FileWalk.files ("*" + ext) root |> List.ofSeq)
            |> List.map Path.GetFullPath
            |> List.distinctBy (fun p -> p.ToLowerInvariant())
            |> List.map (fun p ->
                let text =
                    try
                        projectTextWithoutComments (File.ReadAllText p)
                    with
                    | :? IOException
                    | :? UnauthorizedAccessException -> ""

                p, text)
    )

/// The projects of `project`'s repository that compile against it but are
/// not in the run's workspace - a `<Reference>` or HintPath to its dll (a
/// legacy tree links its solutions through a shared bin), or a
/// ProjectReference from a solution the run does not load. Nothing the run
/// builds or rewrites can see their uses of its public declarations, so a
/// public shape must not change under them: the caller holds the surface
/// for them, as fsharp-refactor holds it for a consumer it cannot build.
let outsideConsumers (runTarget: string) (project: string) : string list =
    match repositoryRootOf project with
    | None -> []
    | Some root ->
        let workspace = workspaceOf runTarget project |> Option.defaultValue [ project ]
        let assembly = assemblyNameOf project

        projectsUnder root
        |> List.filter (fun (p, text) ->
            not (workspace |> List.exists (samePath p))
            && not (samePath p project)
            && (referencesAssemblyDirectly text assembly
                || projectReferenceShapesOf p
                   |> List.exists (function
                       | Resolved target -> samePath target project
                       | ByName _
                       | Unresolvable -> false)))
        |> List.map fst

/// A source file as MSBuildWorkspace's own loader reads it: a byte order
/// mark decides, else UTF-8 where the bytes are valid UTF-8, else the
/// system code page (Windows-1252 here). The public `SourceText.From`
/// overloads decode a file without a mark as UTF-8 and replace every
/// invalid byte with U+FFFD — a legacy file's `ä` comes back as `�` —
/// so the fallback is done here, as Roslyn's internal loader does it. The
/// text carries the encoding it was decoded with; the sweep writes it back
/// the same way.
/// The code page a file that is not UTF-8 is read in: the system's ANSI page on
/// Windows, Windows-1252 where the platform has none (Linux and macOS answer
/// UTF-8 to `GetEncoding 0`, which would put U+FFFD back in) — a legacy file
/// came from a Windows machine.
let legacyEncoding () : Text.Encoding =
    Text.Encoding.RegisterProvider Text.CodePagesEncodingProvider.Instance
    let system = Text.Encoding.GetEncoding 0

    if system.CodePage = 65001 then
        Text.Encoding.GetEncoding 1252
    else
        system

/// Is the text a loader read the text of the file, or did invalid UTF-8 come
/// back as U+FFFD? Roslyn's own loaders fall back to the system page on
/// Windows and to UTF-8 elsewhere.
let replacedInvalidBytes (path: string) (text: SourceText) =
    text.ToString().Contains(char 0xFFFD)
    && (try
            Text.UTF8Encoding(false, true).GetString(File.ReadAllBytes path) |> ignore
            false
        with :? Text.DecoderFallbackException ->
            true)

let readSource (path: string) : SourceText =
    // the code page the fallback decodes with (registering twice is harmless)
    Text.Encoding.RegisterProvider Text.CodePagesEncodingProvider.Instance
    let bytes = File.ReadAllBytes path

    let decode (encoding: Text.Encoding) =
        use stream = new MemoryStream(bytes)

        SourceText.From(stream, encoding, SourceHashAlgorithm.Sha1, false, true)

    try
        decode (Text.UTF8Encoding(false, true))
    with :? Text.DecoderFallbackException ->
        decode (legacyEncoding ())
