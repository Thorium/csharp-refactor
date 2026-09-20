/// What a run surfaces, and the files it writes: SARIF 2.1.0 (with the
/// stable fingerprints --baseline keys on), CSV, a self-contained HTML
/// page, and the JSON of --format json and --mcp. Ported from
/// fsharp-refactor; the layouts are identical so one set of CI consumers
/// serves both tools.
module CSharp.Refactor.Tool.Reports

open System
open System.IO
open System.Text.Json
open System.Text.RegularExpressions
open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.Text
open CSharp.Refactor

type ReportedFinding =
    {
        File: string
        Code: string
        Message: string
        Severity: DiagnosticSeverity
        /// 1-based lines, 0-based columns, as the F# tool spells them.
        StartLine: int
        StartColumn: int
        EndLine: int
        EndColumn: int
        /// Does a quick fix exist, or is this a note?
        Fixable: bool
        /// The fix's edits — (startLine, startColumn, endLine, endColumn,
        /// original, replacement) — so a report reader can render or apply
        /// them.
        Fixes: (int * int * int * int * string * string) list
        /// Stable identity across line shifts and sessions: a hash of the
        /// rule code, the file's NAME (not path — checkouts differ), and the
        /// whitespace-normalized source lines around the finding.
        Fingerprint: string
        /// The finding's own line(s) with one line of margin.
        Snippet: string
        /// The 1-based first and last line the snippet covers.
        SnippetLines: int * int
        /// The text of the finding's range itself, as the source spells it.
        RegionText: string
    }

/// The fingerprint version key used in SARIF partialFingerprints.
[<Literal>]
let FingerprintKey = "csrefContextHash/v1"

let private whitespaceRunRegex = Regex(@"\s+", RegexOptions.Compiled)

/// The fingerprint and context of a finding at `span` in `text`.
let fingerprintAndSnippet (text: SourceText) (file: string) (code: string) (span: TextSpan) =
    let lines = text.Lines
    let lineCount = lines.Count
    let startLine = lines.GetLinePosition(span.Start).Line + 1
    let endLine = lines.GetLinePosition(span.End).Line + 1
    let clamp l = max 0 (min (lineCount - 1) l)
    let firstContext = clamp (startLine - 3)
    let lastContext = clamp (endLine + 1)

    let normalized =
        [
            for l in firstContext..lastContext -> whitespaceRunRegex.Replace(lines.[l].ToString().Trim(), " ")
        ]
        |> String.concat "\n"

    let hash =
        use sha = System.Security.Cryptography.SHA256.Create()

        let bytes =
            System.Text.Encoding.UTF8.GetBytes($"{code}|{Path.GetFileName(file).ToLowerInvariant()}|{normalized}")

        (sha.ComputeHash bytes)[..7] |> Array.map (sprintf "%02x") |> String.concat ""

    let snippetFirst = clamp (startLine - 2)
    let snippetLast = clamp (endLine - 1)

    let snippet =
        [ for l in snippetFirst..snippetLast -> lines.[l].ToString() ]
        |> String.concat "\n"

    hash, snippet, (snippetFirst + 1, snippetLast + 1), text.ToString span

/// The tool's version as Directory.Build.props set it: the informational
/// version, minus any +sha suffix a source build carries.
let toolVersion =
    lazy
        (let asm = Reflection.Assembly.GetExecutingAssembly()

         asm.GetCustomAttributes(typeof<Reflection.AssemblyInformationalVersionAttribute>, false)
         |> Array.tryHead
         |> Option.map (fun a -> (a :?> Reflection.AssemblyInformationalVersionAttribute).InformationalVersion)
         |> Option.map (fun v -> v.Split('+').[0])
         |> Option.defaultValue (string (asm.GetName().Version)))

/// When this process started, for the report's invocation record.
let private startedUtc = DateTime.UtcNow

let sourceRootOf (target: string) =
    let targetDir =
        try
            let full = Path.GetFullPath target

            if Directory.Exists full then
                full
            else
                Path.GetDirectoryName full
        with _ ->
            Directory.GetCurrentDirectory()

    let rec repoRoot (dir: string) depth =
        if depth > 24 || String.IsNullOrEmpty dir then
            None
        elif
            Directory.Exists(Path.Combine(dir, ".git"))
            || File.Exists(Path.Combine(dir, ".git"))
        then
            Some dir
        else
            repoRoot (Path.GetDirectoryName dir) (depth + 1)

    let chosen =
        repoRoot targetDir 0
        |> Option.defaultValue (
            if String.IsNullOrEmpty targetDir then
                Directory.GetCurrentDirectory()
            else
                targetDir
        )

    chosen.Replace('\\', '/').TrimEnd('/') + "/"

/// A file's path relative to the root when it sits under it, else the
/// full path.
let relativeToRoot (root: string) (file: string) =
    let full = Path.GetFullPath(file).Replace('\\', '/')

    if full.StartsWith(root, StringComparison.OrdinalIgnoreCase) then
        full.Substring root.Length
    else
        full

/// A priority rule and a correctness rule earn a warning in the reports;
/// the rest are notes. Nothing becomes an error — every finding is advice
/// about code that compiles.
let reportLevel (code: string) (severity: DiagnosticSeverity) =
    match severity with
    | DiagnosticSeverity.Error -> "error"
    | DiagnosticSeverity.Warning -> "warning"
    | _ ->
        if RuleCatalog.isPriority code then
            "warning"
        else
            match RuleCatalog.categoryOf code with
            | RuleCatalog.Category.Correctness -> "warning"
            | _ -> "note"

let reportCategory (code: string) =
    RuleCatalog.name (RuleCatalog.categoryOf code)

let plainMessage (f: ReportedFinding) =
    let tag = $" [{reportCategory f.Code}]"

    if f.Message.EndsWith tag then
        f.Message.Substring(0, f.Message.Length - tag.Length)
    else
        f.Message

/// SARIF 2.1.0, hand-built with System.Text.Json (no Sarif.Sdk dependency).
/// The layout is the standard's; what makes the file read well is the
/// optional content it allows, all of which is here: a tool.driver with a
/// version and an information link, one rule entry per surfaced code with
/// its description, help link and default level, results carrying
/// ruleIndex, level, a fingerprint, the range's text plus a context
/// region and — where a fix exists — the fix itself as artifactChanges
/// (code scanning renders those as suggested changes), locations under a
/// `%SRCROOT%` base id, an artifact table, an automation id per target,
/// and an invocation record with times and the command line.
let private writeSarifReport (path: string) (target: string) (findings: ReportedFinding seq) =
    let root = sourceRootOf target

    let fileUri (dir: string) =
        Uri(dir.Replace('/', Path.DirectorySeparatorChar)).AbsoluteUri

    let rootUri = fileUri root

    // a location: relative to %SRCROOT% when the file sits under the root,
    // absolute otherwise
    let artifactLocation (file: string) =
        let relative = relativeToRoot root file

        if relative <> Path.GetFullPath(file).Replace('\\', '/') then
            dict [ "uri", box relative; "uriBaseId", box "%SRCROOT%" ]
        else
            dict [ "uri", box (Uri(Path.GetFullPath file).AbsoluteUri) ]

    let regionEntries (startLine, startColumn, endLine, endColumn) =
        [
            "startLine", box (max 1 startLine)
            "startColumn", box (startColumn + 1)
            "endLine", box (max 1 endLine)
            "endColumn", box (endColumn + 1)
        ]

    let region bounds = dict (regionEntries bounds)

    // one entry per rule the run surfaced, in code order: code scanning
    // groups and filters by these, and without them every finding is an
    // opaque id. The description is the catalog's one line; help points at
    // the rule table
    let codes =
        findings |> Seq.map (fun f -> f.Code) |> Seq.distinct |> Seq.sort |> List.ofSeq

    let ruleIndex = codes |> List.mapi (fun i code -> code, i) |> Map.ofList

    let rulesMetadata =
        codes
        |> List.map (fun code ->
            let description = RuleCatalog.describe code
            let category = reportCategory code

            dict
                [
                    "id", box code
                    "name", box code
                    "shortDescription", box (dict [ "text", box description ])
                    "fullDescription", box (dict [ "text", box $"{description} ({category} rule of csharp-refactor)" ])
                    "helpUri", box (RuleCatalog.helpUri code)
                    "help", box (dict [ "text", box $"See {code} in Rules.md: {RuleCatalog.helpUri code}" ])
                    "defaultConfiguration", box (dict [ "level", box (reportLevel code DiagnosticSeverity.Info) ])
                    "properties",
                    box (dict [ "category", box category; "tags", box [ category; "csharp"; "refactoring" ] ])
                ])

    // every file a finding names, once, in path order: the run's artifact
    // table, which results point into by relative uri
    let artifacts =
        findings
        |> Seq.map (fun f -> Path.GetFullPath f.File)
        |> Seq.distinct
        |> Seq.sort
        |> Seq.map (fun file -> dict [ "location", box (artifactLocation file); "roles", box [ "analysisTarget" ] ])
        |> List.ofSeq

    let results =
        [
            for f in findings ->
                let snippetStart, snippetEnd = f.SnippetLines

                let entries =
                    [
                        "ruleId", box f.Code
                        "ruleIndex", box ruleIndex.[f.Code]
                        "level", box (reportLevel f.Code f.Severity)
                        "message", box (dict [ "text", box (plainMessage f) ])
                        // stable across line shifts and sessions; the baseline
                        // mechanism keys on this
                        "partialFingerprints", box (dict [ FingerprintKey, box f.Fingerprint ])
                        "properties",
                        box (dict [ "autoFixable", box f.Fixable; "category", box (reportCategory f.Code) ])
                        "locations",
                        box
                            [
                                dict
                                    [
                                        "physicalLocation",
                                        box (
                                            dict
                                                [
                                                    "artifactLocation", box (artifactLocation f.File)
                                                    // the range itself, with its text
                                                    "region",
                                                    box (
                                                        dict (
                                                            regionEntries (
                                                                f.StartLine,
                                                                f.StartColumn,
                                                                f.EndLine,
                                                                f.EndColumn
                                                            )
                                                            @ [ "snippet", box (dict [ "text", box f.RegionText ]) ]
                                                        )
                                                    )
                                                    // the surrounding lines: saves the
                                                    // reader (human or agent) one
                                                    // file-open per finding
                                                    "contextRegion",
                                                    box (
                                                        dict
                                                            [
                                                                "startLine", box snippetStart
                                                                "endLine", box snippetEnd
                                                                "snippet", box (dict [ "text", box f.Snippet ])
                                                            ]
                                                    )
                                                ]
                                        )
                                    ]
                            ]
                    ]

                // the fix as SARIF spells it: code scanning shows it as a
                // suggested change, and any consumer can apply it
                let fixes =
                    match f.Fixes with
                    | [] -> []
                    | edits ->
                        [
                            "fixes",
                            box
                                [
                                    dict
                                        [
                                            "description",
                                            box (dict [ "text", box $"{f.Code}: {RuleCatalog.describe f.Code}" ])
                                            "artifactChanges",
                                            box
                                                [
                                                    dict
                                                        [
                                                            "artifactLocation", box (artifactLocation f.File)
                                                            "replacements",
                                                            box
                                                                [
                                                                    for (sl, sc, el, ec, _, text) in edits ->
                                                                        dict
                                                                            [
                                                                                "deletedRegion",
                                                                                box (region (sl, sc, el, ec))
                                                                                "insertedContent",
                                                                                box (dict [ "text", box text ])
                                                                            ]
                                                                ]
                                                        ]
                                                ]
                                        ]
                                ]
                        ]

                dict (entries @ fixes)
        ]

    let invocation =
        dict
            [
                "executionSuccessful", box true
                "startTimeUtc", box (startedUtc.ToString "o")
                "endTimeUtc", box (DateTime.UtcNow.ToString("o"))
                "workingDirectory",
                box (
                    dict
                        [
                            "uri", box (fileUri (Directory.GetCurrentDirectory().Replace('\\', '/').TrimEnd('/') + "/"))
                        ]
                )
                "commandLine", box (Environment.CommandLine)
            ]

    // the id code scanning files this run under: one per target, so a
    // repository with several solutions keeps their reports apart
    let automationId =
        let name = Path.GetFileName(target.TrimEnd('\\', '/'))
        let name = if String.IsNullOrEmpty name then "run" else name
        $"csharp-refactor/{name}/"

    let report =
        dict
            [
                "$schema", box "https://json.schemastore.org/sarif-2.1.0.json"
                "version", box "2.1.0"
                "runs",
                box
                    [
                        dict
                            [
                                "tool",
                                box (
                                    dict
                                        [
                                            "driver",
                                            box (
                                                dict
                                                    [
                                                        "name", box "csharp-refactor"
                                                        "fullName",
                                                        box "csharp-refactor: C# refactoring analyzers and apply tool"
                                                        "version", box toolVersion.Value
                                                        "semanticVersion", box toolVersion.Value
                                                        // the URL the package and --help both publish; this
                                                        // one said FSharp.Refactorings, and it is the link
                                                        // GitHub code scanning puts in front of users
                                                        "informationUri",
                                                        box "https://github.com/Thorium/csharp-refactor"
                                                        "rules", box rulesMetadata
                                                    ]
                                            )
                                        ]
                                )
                                "automationDetails", box (dict [ "id", box automationId ])
                                "originalUriBaseIds", box (dict [ "%SRCROOT%", box (dict [ "uri", box rootUri ]) ])
                                "invocations", box [ invocation ]
                                "artifacts", box artifacts
                                "columnKind", box "utf16CodeUnits"
                                "results", box results
                            ]
                    ]
            ]

    File.WriteAllText(path, JsonSerializer.Serialize(report, JsonSerializerOptions(WriteIndented = true)))

/// CSV, one row per finding, RFC 4180 quoting, UTF-8 with a BOM so Excel
/// opens it as text rather than guessing. The columns are what a
/// spreadsheet triage needs: rule, category, level, file (relative to the
/// source root), the 1-based range, whether a fix exists, the message,
/// and the fingerprint the baseline keys on.
let private writeCsvReport (path: string) (target: string) (findings: ReportedFinding seq) =
    let root = sourceRootOf target

    let cell (text: string) =
        if text.IndexOfAny [| ','; '"'; '\n'; '\r' |] >= 0 then
            "\"" + text.Replace("\"", "\"\"") + "\""
        else
            text

    let row (cells: string list) =
        cells |> List.map cell |> String.concat ","

    let lines =
        seq {
            yield
                row
                    [
                        "Rule"
                        "Category"
                        "Level"
                        "File"
                        "StartLine"
                        "StartColumn"
                        "EndLine"
                        "EndColumn"
                        "AutoFixable"
                        "Message"
                        "Description"
                        "Fingerprint"
                    ]

            for f in findings do
                yield
                    row
                        [
                            f.Code
                            reportCategory f.Code
                            reportLevel f.Code f.Severity
                            relativeToRoot root f.File
                            string (max 1 f.StartLine)
                            string (f.StartColumn + 1)
                            string (max 1 f.EndLine)
                            string (f.EndColumn + 1)
                            (if f.Fixable then "yes" else "no")
                            plainMessage f
                            RuleCatalog.describe f.Code
                            f.Fingerprint
                        ]
        }

    File.WriteAllText(path, String.concat "\r\n" lines + "\r\n", Text.UTF8Encoding(true))

/// A self-contained HTML page — no scripts fetched, no stylesheets
/// linked, so it opens from a build artifact or an email attachment. A
/// summary strip (findings, fixes, per-category counts), then the
/// findings grouped by rule, each with its file and range, the message,
/// the source with the range highlighted, and the fix as before/after
/// text. Filter boxes at the top narrow the page by category, level, and
/// fixability without a round trip.
let private writeHtmlReport (path: string) (target: string) (findings: ReportedFinding seq) =
    let root = sourceRootOf target
    let findings = List.ofSeq findings
    let esc (s: string) = Net.WebUtility.HtmlEncode s
    let sb = Text.StringBuilder()
    let line (s: string) = sb.AppendLine s |> ignore

    let byCategory =
        findings
        |> List.countBy (fun f -> reportCategory f.Code)
        |> List.sortBy (fun (category, _) ->
            match RuleCatalog.parse category with
            | Some c -> RuleCatalog.all |> List.findIndex ((=) c)
            | None -> 99)

    let fixable = findings |> List.filter (fun f -> f.Fixable) |> List.length

    let files =
        findings
        |> List.map (fun f -> Path.GetFullPath f.File)
        |> List.distinct
        |> List.length

    let grouped =
        findings
        |> List.groupBy (fun f -> f.Code)
        |> List.sortBy (fun (code, items) ->
            not (RuleCatalog.isPriority code),
            (match RuleCatalog.categoryOf code with
             | RuleCatalog.Category.Correctness -> 0
             | RuleCatalog.Category.Performance -> 1
             | RuleCatalog.Category.Idiom -> 2
             | RuleCatalog.Category.Cosmetic -> 3),
            -items.Length,
            code)

    // the context snippet with the finding's own range wrapped in <mark>:
    // the snippet starts at SnippetLines' first line, and the range's
    // columns are 0-based offsets into its lines
    let highlighted (f: ReportedFinding) =
        let snippetStart, _ = f.SnippetLines
        let lines = f.Snippet.Split '\n'
        let firstIndex = f.StartLine - snippetStart
        let lastIndex = f.EndLine - snippetStart

        let clampCol (text: string) c = max 0 (min text.Length c)

        lines
        |> Array.mapi (fun i text ->
            if i < firstIndex || i > lastIndex || firstIndex < 0 then
                esc text
            else
                let startCol = if i = firstIndex then clampCol text f.StartColumn else 0

                let endCol =
                    if i = lastIndex then
                        clampCol text f.EndColumn
                    else
                        text.Length

                let endCol = max startCol endCol

                esc (text.Substring(0, startCol))
                + "<mark>"
                + esc (text.Substring(startCol, endCol - startCol))
                + "</mark>"
                + esc (text.Substring endCol))
        |> String.concat "\n"

    line "<!DOCTYPE html>"

    line
        "<html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">"

    line $"<title>csharp-refactor report: {esc (Path.GetFileName(target.TrimEnd('\\', '/')))}</title>"
    line "<style>"

    line
        ":root{--bg:#fff;--fg:#1f2328;--muted:#656d76;--line:#d0d7de;--code:#f6f8fa;--mark:#fff8c5;--warn:#9a6700;--note:#0969da;--del:#ffebe9;--ins:#dafbe1}"

    line
        "@media (prefers-color-scheme:dark){:root{--bg:#0d1117;--fg:#e6edf3;--muted:#8b949e;--line:#30363d;--code:#161b22;--mark:#4d3800;--warn:#d29922;--note:#58a6ff;--del:#3c1618;--ins:#12261e}}"

    line
        "body{margin:0;padding:24px 32px;font:14px/1.5 -apple-system,'Segoe UI',Roboto,Helvetica,Arial,sans-serif;color:var(--fg);background:var(--bg);max-width:1200px}"

    line
        "h1{font-size:22px;margin:0 0 4px}h2{font-size:16px;margin:32px 0 8px;padding-top:12px;border-top:1px solid var(--line)}"

    line ".meta{color:var(--muted);margin-bottom:16px}.meta code{font-size:13px}"
    line ".summary{display:flex;flex-wrap:wrap;gap:12px;margin:16px 0}"
    line ".card{border:1px solid var(--line);border-radius:6px;padding:8px 14px;min-width:96px}"
    line ".card b{display:block;font-size:20px}.card span{color:var(--muted);font-size:12px}"

    line
        ".filters{display:flex;flex-wrap:wrap;gap:16px;margin:12px 0 4px;color:var(--muted)}.filters label{margin-right:8px;cursor:pointer}"

    line ".rule .desc{color:var(--muted);font-weight:normal}"
    line ".finding{border:1px solid var(--line);border-radius:6px;margin:10px 0;overflow:hidden}"
    line ".finding.hidden{display:none}"

    line
        ".head{display:flex;flex-wrap:wrap;gap:8px 16px;align-items:baseline;padding:8px 12px;background:var(--code);border-bottom:1px solid var(--line)}"

    line ".loc{font-family:ui-monospace,SFMono-Regular,Consolas,monospace;font-size:13px}"

    line
        ".lvl{font-size:11px;text-transform:uppercase;letter-spacing:.04em;padding:1px 6px;border-radius:10px;border:1px solid}"

    line ".lvl.warning{color:var(--warn);border-color:var(--warn)}.lvl.note{color:var(--note);border-color:var(--note)}"
    line ".cat{font-size:12px;color:var(--muted)}.msg{padding:8px 12px}"

    line
        "pre{margin:0;padding:10px 12px;font:12.5px/1.45 ui-monospace,SFMono-Regular,Consolas,monospace;overflow-x:auto;white-space:pre;tab-size:4}"

    line "pre.src{border-top:1px solid var(--line)}mark{background:var(--mark);color:inherit;border-radius:2px}"

    line
        ".fix{border-top:1px solid var(--line)}.fix .lbl{padding:4px 12px;font-size:12px;color:var(--muted);background:var(--code)}"

    line "pre.del{background:var(--del)}pre.ins{background:var(--ins)}"
    line ".empty{padding:24px;border:1px dashed var(--line);border-radius:6px;color:var(--muted)}"
    line "footer{margin-top:32px;color:var(--muted);font-size:12px}"
    line "</style></head><body>"

    line "<h1>csharp-refactor report</h1>"
    let stamp = DateTime.UtcNow.ToString "yyyy-MM-dd HH:mm"

    line
        $"<div class=\"meta\"><code>{esc (Path.GetFullPath target)}</code> · csharp-refactor {esc toolVersion.Value} · {stamp} UTC · paths relative to <code>{esc root}</code></div>"

    line "<div class=\"summary\">"
    line $"<div class=\"card\"><b>{findings.Length}</b><span>findings</span></div>"
    line $"<div class=\"card\"><b>{fixable}</b><span>auto-fixable</span></div>"
    line $"<div class=\"card\"><b>{grouped.Length}</b><span>rules</span></div>"
    line $"<div class=\"card\"><b>{files}</b><span>files</span></div>"

    for category, count in byCategory do
        line $"<div class=\"card\"><b>{count}</b><span>{esc category}</span></div>"

    line "</div>"

    if findings.IsEmpty then
        line "<div class=\"empty\">No findings. The run surfaced nothing for the enabled rules.</div>"
    else
        line "<div class=\"filters\">"
        line "<span>Category:"

        for category, _ in byCategory do
            line
                $"<label><input type=\"checkbox\" data-filter=\"cat\" value=\"{esc category}\" checked> {esc category}</label>"

        line "</span><span>Level:"

        for level in [ "warning"; "note" ] do
            line $"<label><input type=\"checkbox\" data-filter=\"lvl\" value=\"{level}\" checked> {level}</label>"

        line "</span><span>Auto-fix:"
        line "<label><input type=\"checkbox\" data-filter=\"fix\" value=\"yes\" checked> auto-fixable</label>"
        line "<label><input type=\"checkbox\" data-filter=\"fix\" value=\"no\" checked> advisory only</label>"
        line "</span></div>"

        for code, items in grouped do
            line
                $"<h2 class=\"rule\" id=\"{code}\"><a href=\"{RuleCatalog.helpUri code}\">{code}</a> <span class=\"desc\">— {esc (RuleCatalog.describe code)}</span> <span class=\"cat\">({items.Length})</span></h2>"

            for f in items |> List.sortBy (fun f -> f.File, f.StartLine, f.StartColumn) do
                let level = reportLevel f.Code f.Severity
                let category = reportCategory f.Code
                let fixFlag = if f.Fixable then "yes" else "no"

                line $"<div class=\"finding\" data-cat=\"{esc category}\" data-lvl=\"{level}\" data-fix=\"{fixFlag}\">"

                line
                    $"<div class=\"head\"><span class=\"loc\">{esc (relativeToRoot root f.File)}:{max 1 f.StartLine}:{f.StartColumn + 1}</span><span class=\"lvl {level}\">{level}</span><span class=\"cat\">{esc category}</span></div>"

                line $"<div class=\"msg\">{esc (plainMessage f)}</div>"
                line $"<pre class=\"src\">{highlighted f}</pre>"

                match f.Fixes with
                | [] -> ()
                | edits ->
                    line "<div class=\"fix\">"

                    for (sl, sc, _, _, original, text) in edits do
                        let verb = if original = "" then "insert" else "fix"
                        line $"<div class=\"lbl\">{verb} at {max 1 sl}:{sc + 1}</div>"

                        if original <> "" then
                            line $"<pre class=\"del\">- {esc original}</pre>"

                        line $"<pre class=\"ins\">+ {esc text}</pre>"

                    line "</div>"

                line "</div>"

        line "<script>"

        line
            "(function(){var boxes=document.querySelectorAll('input[data-filter]');function apply(){var on={};boxes.forEach(function(b){(on[b.dataset.filter]=on[b.dataset.filter]||{})[b.value]=b.checked});document.querySelectorAll('.finding').forEach(function(f){var show=on.cat[f.dataset.cat]&&on.lvl[f.dataset.lvl]&&on.fix[f.dataset.fix];f.classList.toggle('hidden',!show)});document.querySelectorAll('h2.rule').forEach(function(h){var n=h.nextElementSibling,any=false;while(n&&n.tagName!=='H2'){if(n.classList.contains('finding')&&!n.classList.contains('hidden'))any=true;n=n.nextElementSibling}h.style.display=any?'':'none'})}boxes.forEach(function(b){b.addEventListener('change',apply)})})();"

        line "</script>"

    line
        "<footer>Rules are described in <a href=\"https://github.com/Thorium/csharp-refactor/blob/main/Rules.md\">Rules.md</a>. The same run written as <code>--report findings.sarif</code> uploads to GitHub code scanning.</footer>"

    line "</body></html>"
    File.WriteAllText(path, sb.ToString(), Text.UTF8Encoding(false))

/// --report writes the format its file name asks for: .html/.htm a page,
/// .csv a spreadsheet, anything else (.sarif, .json) SARIF 2.1.0.
let writeReport (path: string) (target: string) (findings: ReportedFinding seq) =
    match Path.GetExtension(path).ToLowerInvariant() with
    | ".html"
    | ".htm" -> writeHtmlReport path target findings
    | ".csv" -> writeCsvReport path target findings
    | _ -> writeSarifReport path target findings


let private severityName (s: DiagnosticSeverity) =
    match s with
    | DiagnosticSeverity.Error -> "error"
    | DiagnosticSeverity.Warning -> "warning"
    | DiagnosticSeverity.Info -> "info"
    | _ -> "hidden"

let findingsPayload (findings: ReportedFinding list) =
    [
        for f in findings ->
            dict
                [
                    "code", box f.Code
                    "severity", box (severityName f.Severity)
                    "autoFixable", box f.Fixable
                    "file", box f.File
                    "startLine", box f.StartLine
                    "startColumn", box f.StartColumn
                    "endLine", box f.EndLine
                    "endColumn", box f.EndColumn
                    "message", box f.Message
                    "fingerprint", box f.Fingerprint
                    "snippet", box f.Snippet
                ]
    ]

/// The run's findings as one JSON document (--format json).
let findingsAsJson (findings: ReportedFinding list) (baselined: int) (commentSuppressed: int) (overridden: int) =
    let payload =
        dict
            [
                "findings", box (findingsPayload findings)
                "baselineSuppressed", box baselined
                "commentSuppressed", box commentSuppressed
                "suppressionsOverridden", box overridden
            ]

    JsonSerializer.Serialize(payload, JsonSerializerOptions(WriteIndented = true))

/// Fingerprints an earlier run accepted (--baseline).
let loadBaseline (path: string) : Result<Set<string>, string> =
    try
        use doc = JsonDocument.Parse(File.ReadAllText path)

        let prints =
            [
                for run in doc.RootElement.GetProperty("runs").EnumerateArray() do
                    match run.TryGetProperty "results" with
                    | true, results ->
                        for result in results.EnumerateArray() do
                            match result.TryGetProperty "partialFingerprints" with
                            | true, fps ->
                                match fps.TryGetProperty FingerprintKey with
                                | true, v -> v.GetString()
                                | _ -> ()
                            | _ -> ()
                    | _ -> ()
            ]

        Ok(Set.ofList prints)
    with ex ->
        Error $"could not read baseline '{path}': {ex.Message}"
