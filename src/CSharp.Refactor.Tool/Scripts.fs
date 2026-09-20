/// C# scripts (`.csx`): the fsx twin. A script is its own compilation —
/// the file parsed as `SourceCodeKind.Script`, the running runtime's shared
/// framework as its references, `#r` directives resolved against the
/// script's directory, the runtime directory and the NuGet cache
/// (`#r "nuget: Name, Version"`), `#load` directives read by the compiler
/// for context (a loaded file is edited when it is swept as a target of
/// its own). Nothing builds a script, so a run is verified in memory: the
/// error count may not grow. Read into the same AdhocWorkspace the legacy
/// projects use.
module CSharp.Refactor.Tool.Scripts

open System
open System.Collections.Generic
open System.Collections.Immutable
open System.IO
open System.Text.RegularExpressions
open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.Text

let isScript (path: string) =
    path.EndsWith(".csx", StringComparison.OrdinalIgnoreCase)

/// What dotnet-script imports for every script, so a bare `Console` or
/// `File` binds the way it does when the script runs.
let private defaultUsings =
    [
        "System"
        "System.IO"
        "System.Collections.Generic"
        "System.Diagnostics"
        "System.Dynamic"
        "System.Linq"
        "System.Linq.Expressions"
        "System.Text"
        "System.Threading.Tasks"
    ]

let private runtimeDirectory = Path.GetDirectoryName typeof<obj>.Assembly.Location

/// Every managed assembly of the running shared framework.
let private frameworkReferences: Lazy<PortableExecutableReference list> =
    lazy
        (Directory.GetFiles(runtimeDirectory, "*.dll")
         |> Array.choose (fun f ->
             try
                 use stream = File.OpenRead f
                 use pe = new System.Reflection.PortableExecutable.PEReader(stream)

                 if pe.HasMetadata then
                     Some(MetadataReference.CreateFromFile f)
                 else
                     None
             with _ ->
                 None)
         |> List.ofArray)

let private nugetRoot =
    let env = Environment.GetEnvironmentVariable "NUGET_PACKAGES"

    if not (String.IsNullOrEmpty env) then
        env
    else
        Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile, ".nuget", "packages")

/// Prefer the newest .NET target, then netstandard, then the rest.
let private tfmRank (tfm: string) =
    let t = tfm.ToLowerInvariant()

    let m = Regex.Match(t, @"^net(\d+)\.(\d+)$")

    if m.Success then
        1000 + int m.Groups.[1].Value * 10 + int m.Groups.[2].Value
    elif t = "netstandard2.1" then
        900
    elif t = "netstandard2.0" then
        890
    elif t.StartsWith "netcoreapp" then
        800
    elif t.StartsWith "netstandard" then
        700
    elif t.StartsWith "net4" then
        100
    else
        0

/// The lib assemblies of a package from the NuGet cache — the newest
/// version when none is asked for, the best framework folder.
let private nugetAssemblies (name: string) (version: string option) : string list =
    let packageDir = Path.Combine(nugetRoot, name.ToLowerInvariant())

    if not (Directory.Exists packageDir) then
        []
    else
        let versionDir =
            match version with
            | Some v when Directory.Exists(Path.Combine(packageDir, v)) -> Some(Path.Combine(packageDir, v))
            | Some v ->
                // a version prefix (`1.2`) picks the newest match
                Directory.GetDirectories packageDir
                |> Array.filter (fun d -> Path.GetFileName(d).StartsWith v)
                |> Array.sortDescending
                |> Array.tryHead
            | None -> Directory.GetDirectories packageDir |> Array.sortDescending |> Array.tryHead

        match versionDir with
        | None -> []
        | Some vd ->
            let lib = Path.Combine(vd, "lib")

            if not (Directory.Exists lib) then
                []
            else
                Directory.GetDirectories lib
                |> Array.sortByDescending (Path.GetFileName >> tfmRank)
                |> Array.tryFind (fun d -> Directory.GetFiles(d, "*.dll").Length > 0)
                |> Option.map (fun d -> Directory.GetFiles(d, "*.dll") |> List.ofArray)
                |> Option.defaultValue []

/// `#r` resolution: a path relative to the script, a framework assembly by
/// name, or a NuGet package from the cache; what fails is remembered.
type private ScriptReferenceResolver(scriptDirectory: string, unresolved: ResizeArray<string>) =
    inherit MetadataReferenceResolver()

    override _.ResolveReference(reference: string, baseFilePath: string, _properties: MetadataReferenceProperties) =
        let found =
            let r = reference.Trim()

            if r.StartsWith("nuget:", StringComparison.OrdinalIgnoreCase) then
                let spec = r.Substring(6).Split([| ',' |], 2)
                let name = spec.[0].Trim()
                let version = if spec.Length > 1 then Some(spec.[1].Trim()) else None
                nugetAssemblies name version
            else
                let baseDir =
                    if String.IsNullOrEmpty baseFilePath then
                        scriptDirectory
                    else
                        Path.GetDirectoryName baseFilePath

                let withDll =
                    if r.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) then
                        r
                    else
                        r + ".dll"

                [
                    Path.Combine(baseDir, r)
                    Path.Combine(baseDir, withDll)
                    Path.Combine(runtimeDirectory, withDll)
                ]
                |> List.tryFind File.Exists
                |> Option.map List.singleton
                |> Option.defaultValue []

        if found.IsEmpty then
            unresolved.Add reference
            ImmutableArray<PortableExecutableReference>.Empty
        else
            found |> List.map MetadataReference.CreateFromFile |> ImmutableArray.CreateRange

    override x.Equals(other: obj) = obj.ReferenceEquals(x, other)
    override _.GetHashCode() = scriptDirectory.GetHashCode()

type LoadReport =
    {
        Script: string
        Unresolved: string list
    }

let private loaded = Dictionary<string, ProjectId>(StringComparer.OrdinalIgnoreCase)

let private reports =
    Dictionary<string, ResizeArray<string>>(StringComparer.OrdinalIgnoreCase)

/// The `#r` directives a script's compilation could not resolve, once it
/// has been compiled.
let unresolvedOf (scriptPath: string) : string list =
    match reports.TryGetValue(Path.GetFullPath scriptPath) with
    | true, xs -> xs |> Seq.distinct |> List.ofSeq
    | _ -> []

/// Load a script into the workspace as a project of one document; returns
/// the project.
let load (workspace: AdhocWorkspace) (scriptPath: string) : Project =
    let scriptPath = Path.GetFullPath scriptPath

    match loaded.TryGetValue scriptPath with
    | true, id -> workspace.CurrentSolution.GetProject id
    | _ ->
        let dir = Path.GetDirectoryName scriptPath
        let projectId = ProjectId.CreateNewId scriptPath
        let name = Path.GetFileNameWithoutExtension scriptPath
        let unresolved = ResizeArray<string>()
        reports.[scriptPath] <- unresolved

        let sourceText = Workspace.readSource scriptPath

        let document =
            DocumentInfo.Create(
                DocumentId.CreateNewId projectId,
                Path.GetFileName scriptPath,
                loader = TextLoader.From(TextAndVersion.Create(sourceText, VersionStamp.Create(), scriptPath)),
                filePath = scriptPath,
                sourceCodeKind = SourceCodeKind.Script
            )

        let compilationOptions =
            CSharpCompilationOptions(
                OutputKind.ConsoleApplication,
                usings = defaultUsings,
                sourceReferenceResolver = SourceFileResolver(ImmutableArray<string>.Empty, dir),
                metadataReferenceResolver = ScriptReferenceResolver(dir, unresolved)
            )

        let info =
            ProjectInfo.Create(
                projectId,
                VersionStamp.Create(),
                name,
                name,
                LanguageNames.CSharp,
                filePath = scriptPath,
                compilationOptions = compilationOptions,
                parseOptions = CSharpParseOptions(LanguageVersion.Latest, kind = SourceCodeKind.Script),
                documents = [ document ],
                metadataReferences = (frameworkReferences.Value |> List.map (fun r -> r :> MetadataReference))
            )

        let project = workspace.AddProject info
        loaded.[scriptPath] <- project.Id
        project
