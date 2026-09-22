/// Non-SDK ("legacy") C# projects — `<Project ToolsVersion="4.0" …>` with
/// explicit `<Compile Include>` items — which MSBuildWorkspace hands to a
/// .NET Framework build host that does not always answer (a Visual Studio
/// MSBuild the host was not built against). This module reads the project
/// file itself and builds the compilation into an `AdhocWorkspace`: the
/// compile items, the defines of the first configuration, the references
/// by `HintPath` where the file exists, the framework assemblies from the
/// targeting pack of the project's `TargetFrameworkVersion` (or the
/// runtime's own directory), and every `ProjectReference` loaded the same
/// way. Custom `.targets` imports are not evaluated: a reference they add
/// is missing, and the rules with typed guards stand down where a type is
/// unknown — the sweep reports the unresolved count and proceeds on the
/// relative in-memory check. The verification build uses the newest Visual
/// Studio `MSBuild.exe` (found through `vswhere`) where a baseline build
/// succeeds; otherwise the run is verified in memory only, and says so.
module CSharp.Refactor.Tool.LegacyProjects

open System
open System.Collections.Generic
open System.IO
open System.Text.RegularExpressions
open System.Xml.Linq
open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.Text

let private importsbSdksRegex = Regex @"<Import\s[^>]*\bSdk\s*="
let private projectbRegex = Regex @"<Project\b[^>]*>"

/// A project file that is not SDK-style: no `Sdk` attribute on the root
/// and no `<Import Sdk=…>`.
let isLegacy (projectPath: string) =
    try
        let text = Workspace.projectTextWithoutComments (File.ReadAllText projectPath)
        let root = projectbRegex.Match text

        root.Success
        && not (root.Value.Contains "Sdk=")
        && not (importsbSdksRegex.IsMatch text)
    with
    | :? IOException
    | :? UnauthorizedAccessException -> false

let private referenceAssembliesRoot =
    Path.Combine(
        Environment.GetFolderPath Environment.SpecialFolder.ProgramFilesX86,
        "Reference Assemblies",
        "Microsoft",
        "Framework",
        ".NETFramework"
    )

/// The assembly directories of the newest Visual Studio, where a legacy test
/// project's MSTest (`Microsoft.VisualStudio.QualityTools.*`) and the IDE's
/// public assemblies live — references no HintPath names.
let private visualStudioDirectories: Lazy<string list> =
    lazy
        (let vswhere =
            Path.Combine(
                Environment.GetFolderPath Environment.SpecialFolder.ProgramFilesX86,
                "Microsoft Visual Studio",
                "Installer",
                "vswhere.exe"
            )

         if not (File.Exists vswhere) then
             []
         else
             let code, out, _ =
                 Processes.runProcessIn
                     None
                     (TimeSpan.FromSeconds 30.0)
                     vswhere
                     "-latest -products * -requires Microsoft.Component.MSBuild -property installationPath"

             if code <> 0 then
                 []
             else
                 match
                     out.Split '\n'
                     |> Array.map (fun l -> l.Trim())
                     |> Array.tryFind Directory.Exists
                 with
                 | None -> []
                 | Some root ->
                     [
                         Path.Combine(root, "Common7", "IDE", "PublicAssemblies")
                         Path.Combine(root, "Common7", "IDE", "ReferenceAssemblies", "v4.0")
                         Path.Combine(root, "Common7", "IDE", "Extensions", "TestPlatform")
                     ]
                     |> List.filter Directory.Exists)

/// The directory of the framework assemblies for a `TargetFrameworkVersion`
/// (`v4.5.1`): the targeting pack, else the runtime's own directory.
let private frameworkDirectory (version: string) =
    // a pack directory may hold only the XML docs: one with mscorlib counts
    let hasAssemblies (dir: string) =
        File.Exists(Path.Combine(dir, "mscorlib.dll"))

    let pack = Path.Combine(referenceAssembliesRoot, version)

    if Directory.Exists pack && hasAssemblies pack then
        Some pack
    else
        // the nearest pack with assemblies: older first, else the next newer (a
        // superset of the older surface)
        let packs =
            if Directory.Exists referenceAssembliesRoot then
                Directory.GetDirectories referenceAssembliesRoot
                |> Array.filter hasAssemblies
                |> Array.map Path.GetFileName
                |> Array.filter (fun d -> d.StartsWith "v4")
                |> Array.sort
            else
                [||]

        let older =
            packs
            |> Array.filter (fun d -> String.CompareOrdinal(d, version) <= 0)
            |> Array.tryLast

        let newer =
            packs
            |> Array.filter (fun d -> String.CompareOrdinal(d, version) > 0)
            |> Array.tryHead

        match older |> Option.orElse newer with
        | Some p -> Some(Path.Combine(referenceAssembliesRoot, p))
        | None ->
            let runtime =
                Path.Combine(
                    Environment.GetEnvironmentVariable "WINDIR"
                    |> Option.ofObj
                    |> Option.defaultValue @"C:\Windows",
                    "Microsoft.NET",
                    "Framework64",
                    "v4.0.30319"
                )

            if Directory.Exists runtime then Some runtime else None

let private ns =
    XNamespace.Get "http://schemas.microsoft.com/developer/msbuild/2003"

let private elements (root: XElement) (name: string) =
    root.Descendants() |> Seq.filter (fun e -> e.Name.LocalName = name)

let private attr (name: string) (e: XElement) =
    match e.Attribute(XName.Get name) with
    | null -> None
    | a -> Some a.Value

let private child (name: string) (e: XElement) =
    e.Elements()
    |> Seq.tryFind (fun c -> c.Name.LocalName = name)
    |> Option.map (fun c -> c.Value)

/// A property's value from the first PropertyGroup that defines it and whose
/// condition names Debug or nothing.
let private property (root: XElement) (name: string) =
    elements root "PropertyGroup"
    |> Seq.filter (fun g ->
        match attr "Condition" g with
        | None -> true
        | Some c -> c.Contains "Debug" || not (c.Contains "Release"))
    |> Seq.tryPick (child name)

/// Expand a compile item's path: `*` globs within the project directory.
let private expandItem (dir: string) (include': string) : string list =
    let normalized = include'.Replace('\\', Path.DirectorySeparatorChar)

    if normalized.Contains '*' then
        let pattern = Path.GetFileName normalized
        let sub = Path.GetDirectoryName normalized
        let baseDir = Path.GetFullPath(Path.Combine(dir, sub))

        if Directory.Exists baseDir then
            let option =
                if normalized.Contains "**" then
                    SearchOption.AllDirectories
                else
                    SearchOption.TopDirectoryOnly

            Directory.GetFiles(baseDir, pattern.Replace("**", "*"), option) |> List.ofArray
        else
            []
    else
        [ Path.GetFullPath(Path.Combine(dir, normalized)) ]

/// What the loader could not resolve, for the run's report.
type LoadReport =
    {
        Project: string
        Documents: int
        MissingReferences: string list
        FrameworkDirectory: string option
    }

let private loaded = Dictionary<string, ProjectId>(StringComparer.OrdinalIgnoreCase)

/// The projects being read right now: a reference back into one of them (a
/// cycle) is dropped rather than followed.
let private loading = HashSet<string>(StringComparer.OrdinalIgnoreCase)
let private reports = ResizeArray<LoadReport>()

let loadReports () = List.ofSeq reports

/// Load a legacy project (and, recursively, its project references) into
/// the workspace; returns the project.
let rec load (workspace: AdhocWorkspace) (projectPath: string) : Project =
    let projectPath = Path.GetFullPath projectPath

    match loaded.TryGetValue projectPath with
    | true, id -> workspace.CurrentSolution.GetProject id
    | _ ->
        let dir = Path.GetDirectoryName projectPath
        let text = Workspace.projectTextWithoutComments (File.ReadAllText projectPath)
        let root = XDocument.Parse(text).Root

        let assemblyName =
            property root "AssemblyName"
            |> Option.defaultValue (Path.GetFileNameWithoutExtension projectPath)

        let outputKind =
            match property root "OutputType" with
            | Some "Exe" -> OutputKind.ConsoleApplication
            | Some "WinExe" -> OutputKind.WindowsApplication
            | _ -> OutputKind.DynamicallyLinkedLibrary

        let frameworkVersion =
            property root "TargetFrameworkVersion" |> Option.defaultValue "v4.8"

        let frameworkDir = frameworkDirectory frameworkVersion

        let defines =
            property root "DefineConstants"
            |> Option.defaultValue "DEBUG;TRACE"
            |> fun s -> s.Split([| ';'; ',' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.map (fun d -> d.Trim())
            |> Array.filter (fun d -> d <> "" && not (d.Contains '$'))

        let allowUnsafe =
            property root "AllowUnsafeBlocks"
            |> Option.exists (fun v -> String.Equals(v.Trim(), "true", StringComparison.OrdinalIgnoreCase))

        // the language version the build will compile with: `<LangVersion>` where
        // the project says, else MSBuild's default for a .NET Framework target —
        // C# 7.3 — so no rule offers a `using` declaration or `field` the real
        // build then rejects
        let languageVersion =
            match property root "LangVersion" with
            | Some v when String.Equals(v.Trim(), "default", StringComparison.OrdinalIgnoreCase) ->
                LanguageVersion.CSharp7_3
            | Some v ->
                let mutable parsed = LanguageVersion.Default

                if LanguageVersionFacts.TryParse(v.Trim(), &parsed) then
                    LanguageVersionFacts.MapSpecifiedToEffectiveVersion parsed
                else
                    LanguageVersion.CSharp7_3
            | None -> LanguageVersion.CSharp7_3

        let parseOptions =
            CSharpParseOptions(languageVersion, preprocessorSymbols = defines)

        // compile items
        let documents =
            elements root "Compile"
            |> Seq.choose (attr "Include")
            |> Seq.collect (expandItem dir)
            |> Seq.filter File.Exists
            |> Seq.distinctBy (fun p -> p.ToLowerInvariant())
            |> List.ofSeq

        let projectId = ProjectId.CreateNewId assemblyName
        loading.Add projectPath |> ignore

        // project references first: their compilations are ours to reference;
        // one that cannot be read (a file missing from the checkout, a cycle)
        // is left out, and the types it would have given stay unresolved
        let projectReferences =
            elements root "ProjectReference"
            |> Seq.choose (attr "Include")
            |> Seq.map (fun i -> Path.GetFullPath(Path.Combine(dir, i.Replace('\\', Path.DirectorySeparatorChar))))
            |> Seq.filter File.Exists
            |> Seq.filter (fun p -> not (loading.Contains p))
            |> Seq.choose (fun p ->
                try
                    Some(ProjectReference((load workspace p).Id))
                with _ ->
                    None)
            |> List.ofSeq

        // assembly references: a HintPath that exists, else the framework directory
        let missing = ResizeArray<string>()

        let resolveReference (e: XElement) =
            match attr "Include" e with
            | None -> None
            | Some include' ->
                let name = include'.Split(',').[0].Trim()

                let hint =
                    child "HintPath" e
                    |> Option.map (fun h ->
                        Path.GetFullPath(Path.Combine(dir, h.Replace('\\', Path.DirectorySeparatorChar))))
                    |> Option.filter File.Exists

                match hint with
                | Some h -> Some h
                | None ->
                    let fromFramework =
                        frameworkDir
                        |> Option.bind (fun fd ->
                            [ Path.Combine(fd, name + ".dll"); Path.Combine(fd, "Facades", name + ".dll") ]
                            |> List.tryFind File.Exists)

                    match fromFramework with
                    | Some f -> Some f
                    | None ->
                        // the IDE's own assemblies (MSTest's QualityTools, the SDK's)
                        let fromVisualStudio =
                            visualStudioDirectories.Value
                            |> List.map (fun d -> Path.Combine(d, name + ".dll"))
                            |> List.tryFind File.Exists

                        match fromVisualStudio with
                        | Some f -> Some f
                        | None ->
                            missing.Add name
                            None

        let explicitReferences =
            elements root "Reference" |> Seq.choose resolveReference |> List.ofSeq

        // the implicit framework references every legacy project gets
        let implicitReferences =
            match frameworkDir with
            | Some fd ->
                [ "mscorlib"; "System"; "System.Core" ]
                |> List.map (fun n -> Path.Combine(fd, n + ".dll"))
                |> List.filter File.Exists
            | None -> []

        // the facades (System.Runtime and friends) that netstandard packages need
        let facades =
            match frameworkDir with
            | Some fd when Directory.Exists(Path.Combine(fd, "Facades")) ->
                Directory.GetFiles(Path.Combine(fd, "Facades"), "*.dll") |> List.ofArray
            | _ -> []

        let metadataReferences =
            implicitReferences @ explicitReferences @ facades
            |> List.distinctBy (fun p -> p.ToLowerInvariant())
            |> List.map (fun p -> MetadataReference.CreateFromFile p :> MetadataReference)

        let documentInfos =
            documents
            |> List.map (fun path ->
                let sourceText = Workspace.readSource path

                DocumentInfo.Create(
                    DocumentId.CreateNewId projectId,
                    Path.GetFileName path,
                    loader = TextLoader.From(TextAndVersion.Create(sourceText, VersionStamp.Create(), path)),
                    filePath = path
                ))

        let info =
            ProjectInfo.Create(
                projectId,
                VersionStamp.Create(),
                assemblyName,
                assemblyName,
                LanguageNames.CSharp,
                filePath = projectPath,
                compilationOptions = CSharpCompilationOptions(outputKind, allowUnsafe = allowUnsafe),
                parseOptions = parseOptions,
                documents = documentInfos,
                projectReferences = projectReferences,
                metadataReferences = metadataReferences
            )

        reports.Add
            {
                Project = projectPath
                Documents = documents.Length
                MissingReferences = List.ofSeq missing
                FrameworkDirectory = frameworkDir
            }

        loading.Remove projectPath |> ignore
        let project = workspace.AddProject info
        // recorded only once it is in the workspace: a failure above leaves no
        // entry that a later lookup would answer with nothing
        loaded.[projectPath] <- projectId
        project

// ---- the verification build ----

let private vswhere =
    Path.Combine(
        Environment.GetFolderPath Environment.SpecialFolder.ProgramFilesX86,
        "Microsoft Visual Studio",
        "Installer",
        "vswhere.exe"
    )

/// The newest Visual Studio MSBuild.exe, if any.
let msbuildExe: Lazy<string option> =
    lazy
        (if not (File.Exists vswhere) then
             None
         else
             let code, out, _ =
                 Processes.runProcessIn
                     None
                     (TimeSpan.FromSeconds 30.0)
                     vswhere
                     "-latest -products * -requires Microsoft.Component.MSBuild -find MSBuild\\**\\Bin\\MSBuild.exe"

             if code = 0 then
                 out.Split('\n') |> Array.map (fun l -> l.Trim()) |> Array.tryFind File.Exists
             else
                 None)

/// Build a legacy project with MSBuild.exe: Ok on success, Error with the
/// error lines, or None when no MSBuild.exe is available.
let build (projectPath: string) : Result<unit, string> option =
    msbuildExe.Value
    |> Option.map (fun exe ->
        let code, out, err =
            Processes.runProcessIn
                (Some(Path.GetDirectoryName projectPath))
                Processes.processTimeout
                exe
                $"\"{projectPath}\" -nologo -v:q -clp:NoSummary -p:Configuration=Debug"

        if code = 0 then
            Ok()
        else
            let lines =
                ($"{out}\n{err}").Split '\n'
                |> Array.map (fun l -> l.Trim())
                |> Array.filter (fun l -> l.Contains "error" || l.StartsWith Processes.TimeCapMark)
                |> Array.distinct
                |> Array.truncate 12

            Error(String.concat "\n" lines))
