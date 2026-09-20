/// The pure core's vocabulary. A rule is a function from a syntax tree, a
/// semantic model and a context to suggestions; the Roslyn adapters in
/// Analyzers.fs and CodeFixes.fs turn these into diagnostics and code
/// actions, and the apply tool reads them directly.
namespace CSharp.Refactor

open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.Diagnostics
open Microsoft.CodeAnalysis.Text

/// One text change: the span to replace and what replaces it. A fix is a list
/// of these, applied together; nothing outside the spans changes. `File`
/// names another file of the solution the edit belongs to (a caller of a
/// reshaped method); `None` is the suggestion's own file.
type TextEdit =
    {
        Span: TextSpan
        Replacement: string
        File: string option
    }

/// One reference to a symbol in another file of the host's solution: the
/// tree and model it sits in, the name node that refers, and whether the
/// host may edit that file (a VB document is read, never rewritten).
type ReferenceSite =
    {
        Tree: SyntaxTree
        Model: SemanticModel
        Node: SyntaxNode
        Editable: bool
    }

/// A rewrite the rule offers. `EditorOnly` marks the alternatives an editor
/// lists beside the primary fix and a sweep never applies (the F# side's
/// "editor offers"). `Key` is Roslyn's equivalence key, so fix-all groups
/// the same kind of fix across a document.
type Fix =
    {
        Title: string
        Edits: TextEdit list
        EditorOnly: bool
        Key: string
    }

/// A finding: where, what, and the fixes offered for it (none for a note).
type Suggestion =
    {
        Code: string
        Message: string
        Span: TextSpan
        Fixes: Fix list
    }

/// What a rule may ask of its host beyond the tree: the file's effective
/// `.editorconfig` (rule knobs, the yields-to gate), the run's scope
/// decisions, and what the compilation is. An editor and the compiler see
/// the defaults; the apply tool sets `ApiChanges` for a run.
type RuleContext =
    {
        /// The file's effective analyzer config, when the host has one.
        Options: AnalyzerConfigOptions option
        /// `--api-changes`, or `csharp_refactor.api_changes = true`: the
        /// caller owns every caller, cross-file rewrites included.
        ApiChanges: bool
        /// Does the compilation produce something nothing links against —
        /// an executable? Then public is not an exported surface.
        IsLeaf: bool
        /// The compilation names `InternalsVisibleTo` friends: internal
        /// declarations count as exported.
        HasFriends: bool
        /// The C# language version the file is parsed under.
        LanguageVersion: Microsoft.CodeAnalysis.CSharp.LanguageVersion
        /// Every reference to a symbol OUTSIDE this tree, across the host's
        /// solution — the tool and the fix provider answer; the compiler and
        /// an editor without a solution have `None`, and a rule then
        /// rewrites callers in its own file only, or stands down.
        References: (ISymbol -> ReferenceSite list) option
    }

module Suggestion =
    let fix (title: string) (key: string) (edits: TextEdit list) : Fix =
        {
            Title = title
            Edits = edits
            EditorOnly = false
            Key = key
        }

    let editorOnly (f: Fix) : Fix = { f with EditorOnly = true }

    let replace (span: TextSpan) (replacement: string) : TextEdit =
        {
            Span = span
            Replacement = replacement
            File = None
        }

    let insert (position: int) (text: string) : TextEdit =
        {
            Span = TextSpan(position, 0)
            Replacement = text
            File = None
        }

    /// An edit in another file of the solution (a reference site's).
    let replaceIn (file: string) (span: TextSpan) (replacement: string) : TextEdit =
        { replace span replacement with
            File = Some file
        }

    let insertIn (file: string) (position: int) (text: string) : TextEdit =
        { insert position text with
            File = Some file
        }

    let note (code: string) (message: string) (span: TextSpan) : Suggestion =
        {
            Code = code
            Message = message
            Span = span
            Fixes = []
        }

module RuleContext =
    /// What an editor or the compiler sees: no run-level decisions.
    let editor: RuleContext =
        {
            Options = None
            ApiChanges = false
            IsLeaf = false
            HasFriends = false
            LanguageVersion =
                Microsoft.CodeAnalysis.CSharp.LanguageVersionFacts.MapSpecifiedToEffectiveVersion
                    Microsoft.CodeAnalysis.CSharp.LanguageVersion.Latest
            References = None
        }

    /// The references to a symbol outside this tree, as the host sees them:
    /// `Some []` when the host can see the whole solution and there are none,
    /// `None` when the host cannot see (a rule then keeps to its own file).
    let referencesOf (ctx: RuleContext) (symbol: ISymbol) : ReferenceSite list option =
        ctx.References |> Option.map (fun find -> find symbol)

    /// Is the file at least this C# major version? Read as a number so a
    /// level the referenced Roslyn does not name (C# 15 is 1500) still gates.
    let languageAtLeast (ctx: RuleContext) (major: int) = int ctx.LanguageVersion >= major * 100

    /// A rule's integer knob (`csharp_refactor.CRxxxx.<knob>`), or the fallback.
    let knobInt (ctx: RuleContext) (code: string) (knob: string) (fallback: int) =
        match ctx.Options with
        | Some o -> Configuration.parameterInt o code knob fallback
        | None -> fallback

    /// A rule's boolean knob, or the fallback.
    let knobBool (ctx: RuleContext) (code: string) (knob: string) (fallback: bool) =
        match ctx.Options with
        | Some o -> Configuration.parameterBool o code knob fallback
        | None -> fallback

    /// The yields-to gate: is any of the Microsoft rules this rule shadows
    /// enabled in the file's config? Then the rule stands down for those
    /// shapes.
    let shadowedRuleOn (ctx: RuleContext) (ids: string list) =
        match ctx.Options with
        | Some o -> Configuration.shadowedRuleOn o ids
        | None -> false

    /// May a rule reshape PUBLIC declarations of this compilation in
    /// place? With `--api-changes`, in a leaf compilation, or where the
    /// config says nothing links to it; a library with no setting keeps
    /// its surface.
    let publicShapeOpen (ctx: RuleContext) =
        ctx.ApiChanges
        || (match ctx.Options |> Option.bind Configuration.publicApi with
            | Some publicApi -> not publicApi
            | None -> ctx.IsLeaf)

    /// May a rule reshape INTERNAL declarations? Always, unless friends
    /// see them and the run does not own the friends.
    let internalShapeOpen (ctx: RuleContext) = ctx.ApiChanges || not ctx.HasFriends

    /// Where a rewritten line stops fitting: the rule's own `wrap_column`
    /// knob, else the file's `max_line_length` (the `.editorconfig` key
    /// formatters read), else 120 — the width C# code is commonly wrapped
    /// at, wider than the F# side's 110.
    let wrapColumn (ctx: RuleContext) (code: string) =
        match ctx.Options with
        | Some o ->
            let own = Configuration.parameterInt o code "wrap_column" 0

            if own > 0 then
                own
            else
                match o.TryGetValue "max_line_length" with
                | true, v ->
                    match System.Int32.TryParse(v.Trim()) with
                    | true, n when n > 0 -> n
                    | _ -> 120
                | _ -> 120
        | None -> 120
