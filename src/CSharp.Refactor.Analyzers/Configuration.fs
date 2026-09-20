/// Per-repository configuration, read from `.editorconfig` the way every
/// Roslyn analyzer reads it: the host hands each file its effective
/// `AnalyzerConfigOptions`, nearest section winning. Two families of key:
///
///     csharp_refactor.CR0006.then_at_least = 30      a rule's knob
///     csharp_refactor.public_api = false             a run-level key
///
/// plus the standard `dotnet_diagnostic.CRxxxx.severity`, which Roslyn
/// itself applies (a `none` drops the rule before any rule code runs; a
/// default-off rule wakes on any other value). A malformed value fails
/// open to the default so a typo can never break the editor.
module CSharp.Refactor.Configuration

open System
open System.Text.RegularExpressions
open Microsoft.CodeAnalysis.Diagnostics

[<Literal>]
let Prefix = "csharp_refactor."

/// A boolean the way people write them in an ini file.
let asBool (value: string) : bool option =
    match value.Trim().ToLowerInvariant() with
    | "true"
    | "on"
    | "1"
    | "yes" -> Some true
    | "false"
    | "off"
    | "0"
    | "no" -> Some false
    | _ -> None

let private tryGet (options: AnalyzerConfigOptions) (key: string) : string option =
    match options.TryGetValue key with
    | true, v when not (String.IsNullOrWhiteSpace v) -> Some v
    | _ -> None

/// A rule's integer knob, or the fallback.
let parameterInt (options: AnalyzerConfigOptions) (code: string) (knob: string) (fallback: int) : int =
    match tryGet options $"{Prefix}{code}.{knob}" with
    | Some v ->
        match Int32.TryParse(v.Trim()) with
        | true, n -> n
        | _ -> fallback
    | None -> fallback

/// A rule's boolean knob, or the fallback.
let parameterBool (options: AnalyzerConfigOptions) (code: string) (knob: string) (fallback: bool) : bool =
    match tryGet options $"{Prefix}{code}.{knob}" with
    | Some v -> asBool v |> Option.defaultValue fallback
    | None -> fallback

/// The effective severity of another rule (a Microsoft twin), as the
/// config spells it: None when nothing sets it.
let severityOf (options: AnalyzerConfigOptions) (id: string) : string option =
    tryGet options $"dotnet_diagnostic.{id}.severity"
    |> Option.map (fun s -> s.Trim().ToLowerInvariant())

/// Is a shadowed Microsoft rule ON — set to anything but `none`? Then the
/// CR rule stands down for that rule's shapes.
let shadowedRuleOn (options: AnalyzerConfigOptions) (ids: string list) : bool =
    ids
    |> List.exists (fun id ->
        match severityOf options id with
        | Some "none" -> false
        | Some _ -> true
        | None -> false)

/// `csharp_refactor.public_api`: None when unset (the compilation answers).
let publicApi (options: AnalyzerConfigOptions) : bool option =
    tryGet options $"{Prefix}public_api" |> Option.bind asBool

/// `csharp_refactor.api_changes` as a standing decision.
let apiChanges (options: AnalyzerConfigOptions) : bool =
    tryGet options $"{Prefix}api_changes"
    |> Option.bind asBool
    |> Option.defaultValue false

/// `csharp_refactor.suppressions`: all (default) | no-correctness | none.
let suppressions (options: AnalyzerConfigOptions) : string =
    match tryGet options $"{Prefix}suppressions" with
    | Some v ->
        match v.Trim().ToLowerInvariant() with
        | "no-correctness" -> "no-correctness"
        | "none" -> "none"
        | _ -> "all"
    | None -> "all"

let private defaultIgnoredSegments = [ "node_modules"; "bin"; "obj"; ".git" ]

let private defaultIgnoredGlobs =
    [ "*.g.cs"; "*.designer.cs"; "*.generated.cs"; "*.g.i.cs" ]

let private globRegex (pattern: string) =
    let escaped = Regex.Escape(pattern.Replace('\\', '/'))

    let body =
        escaped.Replace("\*\*", "<<ANY>>").Replace("\*", "[^/]*").Replace("<<ANY>>", ".*").Replace("\?", "[^/]")

    Regex($"(^|/){body}$", RegexOptions.IgnoreCase)

let private matchesEntry (normalizedPath: string) (entry: string) =
    let entry = entry.Trim().Replace('\\', '/').Trim '/'

    if entry = "" then
        false
    elif entry.IndexOf '*' >= 0 || entry.IndexOf '?' >= 0 then
        (globRegex entry).IsMatch normalizedPath
    elif entry.IndexOf '/' >= 0 then
        normalizedPath.IndexOf($"/{entry}/", StringComparison.OrdinalIgnoreCase) >= 0
        || normalizedPath.EndsWith("/" + entry, StringComparison.OrdinalIgnoreCase)
    else
        normalizedPath.Split '/'
        |> Array.exists (fun seg -> String.Equals(seg, entry, StringComparison.OrdinalIgnoreCase))

/// `csharp_refactor.ignore_paths` (semicolon-separated), additive over the
/// built-in defaults. A bare name matches a path segment, a slash matches
/// anywhere, `*`/`**` are globs.
let isIgnoredPath (options: AnalyzerConfigOptions option) (path: string) : bool =
    let normalized = "/" + path.Replace('\\', '/').TrimStart('/')

    let configured =
        match options with
        | Some o ->
            match tryGet o $"{Prefix}ignore_paths" with
            | Some v -> v.Split([| ';'; ',' |], StringSplitOptions.RemoveEmptyEntries) |> List.ofArray
            | None -> []
        | None -> []

    (defaultIgnoredSegments @ defaultIgnoredGlobs @ configured)
    |> List.exists (matchesEntry normalized)

/// `csharp_refactor.hints`: the file of custom CR0011 hints, one
/// `lhs ===> rhs` per line, as written (relative paths resolve from the
/// analysed file's directory upward).
let hintsFile (options: AnalyzerConfigOptions) : string option = tryGet options (Prefix + "hints")
