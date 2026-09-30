/// Preprocessor symbols this run defines on top of what each compilation
/// defines itself: `--define` and the config's `csharp_refactor.defines`.
/// The twin of fsharp-refactor's RunDefines.
///
/// Code under `#if LOCAL_BUILD` is not in the parse tree unless something
/// defines LOCAL_BUILD, so without a way to say so the tool can neither
/// analyse it nor keep it compiling: a fix elsewhere can break it unseen.
///
/// Process-wide, set once per run before any MSBuild call (Sweep.executeRun):
/// every child process (Processes.runProcessIn), the MSBuildWorkspace build
/// host, every script's and legacy project's compilation read it.
module CSharp.Refactor.Tool.RunDefines

open System
open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp

/// A symbol `#if` can test, as `csc -define:` takes one: a letter or `_`,
/// then letters, digits and `_`.
let isSymbol (symbol: string) =
    not (String.IsNullOrEmpty symbol)
    && (Char.IsLetter symbol.[0] || symbol.[0] = '_')
    && symbol |> Seq.forall (fun c -> Char.IsLetterOrDigit c || c = '_')

/// The pieces of one `;`- or `,`-separated value, trimmed, empties dropped.
let pieces (value: string) : string list =
    value.Split([| ';'; ',' |], StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)
    |> List.ofArray

/// Symbols from one `--define` value: `;`- or `,`-separated, each one a
/// symbol `#if` can test.
let parse (value: string) : Result<string list, string> =
    let symbols = pieces value

    match symbols |> List.tryFind (isSymbol >> not) with
    | Some bad -> Error $"--define: '{bad}' is not a preprocessor symbol (a letter or _, then letters, digits, _)"
    | None when symbols.IsEmpty -> Error "'--define' needs a value after it"
    | None -> Ok symbols

let mutable private symbols: string list = []

/// Replace the run's symbols (duplicates dropped, order kept).
let set (defined: string list) = symbols <- List.distinct defined

/// The run's symbols.
let current () = symbols

/// `extra` after `existing`, leaving out what `existing` already names.
let private appendMissing (existing: string list) (extra: string list) =
    existing @ (extra |> List.filter (fun s -> not (List.contains s existing)))

/// The value of the `DefineConstants` environment variable a child MSBuild
/// (and the MSBuildWorkspace build host) gets, or None when the run
/// defines nothing.
///
/// An environment variable rather than `-p:DefineConstants=...` or a
/// workspace global property, and deliberately: a global property
/// overrides every assignment in the project, so the SDK's DEBUG and TRACE
/// and the project's own `$(DefineConstants);FOO` would all be dropped and
/// the run would build different code than `dotnet build` does. MSBuild
/// reads an environment variable as the property's initial value instead,
/// and the project's `$(DefineConstants);...` assignments append to it.
/// Whatever the environment already carries is kept in front; a symbol it
/// already names is not repeated.
let environmentValue (inherited: string) : string option =
    match symbols with
    | [] -> None
    | defined ->
        let kept =
            if String.IsNullOrWhiteSpace inherited then
                []
            else
                pieces inherited

        Some(String.Join(";", appendMissing kept defined))

/// C# parse options with the run's symbols added after their own, none
/// repeated; the same instance when nothing is missing, so a caller can
/// tell nothing changed.
let addTo (options: ParseOptions) : ParseOptions =
    match options with
    | :? CSharpParseOptions as o when not symbols.IsEmpty ->
        let own = List.ofSeq o.PreprocessorSymbolNames

        if symbols |> List.forall (fun s -> List.contains s own) then
            options
        else
            o.WithPreprocessorSymbols(appendMissing own symbols) :> ParseOptions
    | _ -> options

/// The cached project `id` of `workspace` with `own` — its parse options
/// before any run's symbols — plus this run's symbols, and no other run's.
/// A resident host (--mcp) serves several runs from one workspace: a script
/// or legacy project read under `--define LOCAL_BUILD` must not keep the
/// symbol for a later run that does not define it.
let refresh (workspace: Workspace) (id: ProjectId) (own: ParseOptions) : Project =
    let project = workspace.CurrentSolution.GetProject id
    let wanted = addTo own

    if isNull project || wanted.Equals project.ParseOptions then
        project
    else
        workspace.TryApplyChanges(workspace.CurrentSolution.WithProjectParseOptions(id, wanted))
        |> ignore

        workspace.CurrentSolution.GetProject id

/// Every C# project of a solution with the run's symbols in its parse
/// options: the analysis sees `#if LOCAL_BUILD` code even where the
/// environment variable did not reach the design-time build (a project
/// that assigns DefineConstants without `$(DefineConstants)`). The same
/// solution when nothing is missing.
let addToSolution (solution: Solution) : Solution =
    if symbols.IsEmpty then
        solution
    else
        solution.Projects
        |> Seq.filter (fun p -> p.Language = LanguageNames.CSharp && not (isNull p.ParseOptions))
        |> Seq.fold
            (fun (s: Solution) p ->
                let patched = addTo p.ParseOptions

                if obj.ReferenceEquals(patched, p.ParseOptions) then
                    s
                else
                    s.WithProjectParseOptions(p.Id, patched))
            solution
