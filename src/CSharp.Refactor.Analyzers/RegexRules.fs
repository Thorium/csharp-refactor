/// Regular expressions.
///
/// CR0107 (correctness, note, priority): `new Regex("(unclosed")`,
/// `Regex.IsMatch(s, "[")` — a literal pattern the engine rejects is a
/// guaranteed `ArgumentException` at the first call. The check constructs
/// the pattern with the call's own constant `RegexOptions` (a pattern
/// legal under `IgnorePatternWhitespace` may be illegal without it); only
/// the engine's `ArgumentException` is proof — any other exception (a
/// `NotSupportedException` for a backreference under `NonBacktracking`)
/// leaves the pattern unproven, and CR0109 keeps such a pattern unhoisted.
///
/// CR0108 (performance, fix): `Regex.IsMatch(s, "^abc")` is
/// `s.StartsWith("abc", StringComparison.Ordinal)`, `Regex.IsMatch(s,
/// "abc")` is `Contains`, `Regex.Replace(s, "abcd", "x")` is
/// `s.Replace("abcd", "x")`, `Regex.Matches(s, "ab").Count` is
/// `s.AsSpan().Count("ab")` (.NET 8) and `Regex.Split(s, ", ")` is
/// `s.Split(", ")` (.NET Core 2.0) — for a pattern that is plain text; the
/// span count and the split see every non-overlapping occurrence, empties
/// kept, as the regex does. Guards: the pattern (any literal spelling)
/// holds no metacharacter and no backslash, quote or control character
/// (it is re-emitted verbatim, and the decoded text would need
/// re-escaping); a `Replace` with a `$` anywhere in the replacement, an
/// empty pattern, or an anchor (`Replace` cannot carry one) is refused,
/// and so is a `$` anchor anywhere (it also matches before a final
/// newline, which `EndsWith` cannot say); no `RegexOptions`,
/// `MatchEvaluator` or timeout argument; the `StartsWith` overload is the
/// ordinal one (the single-argument form is current-culture and differs
/// on ligatures, ignorable characters and Turkish i); `Contains(string)`
/// and `Replace(string, string)` are ordinal already.
///
/// CR0109 (performance, fix): a `Regex` built from a literal inside a
/// method, accessor, local function or lambda body: a construction is
/// parsed on every call (12x and 2.6 KB), a static call is served from the
/// runtime's cache of fifteen patterns until it turns over, and either
/// runs the interpreter where the generated regex runs its own code, near
/// three times faster (a field initializer or a constructor builds once
/// per object). With `[GeneratedRegex]` resolvable and the containing
/// type chain declared in this file, the pattern becomes a
/// `[GeneratedRegex("lit")] private static partial Regex LitRegex();`
/// (every type in the chain gains `partial`); otherwise a `private static
/// readonly Regex LitRegex = new Regex("lit");` field above the member's
/// doc comment — or above the first static field or property initializer
/// preceding the member, which type initialization runs first. Guards: a literal pattern and constant options, on one
/// line; the hoist lands above the enclosing member's leading comment
/// block and under no `#if`; the name comes from the local the result is
/// bound to, else the pattern's words, else the enclosing member's name,
/// numbered where taken; a bare `Regex` resolves only under a `using`
/// that precedes the insertion point. The string-operation rewrite
/// (CR0108) subsumes the hoist on the same site, and a plain-text pattern
/// under no options is never hoisted, rewritten or not: the regex over
/// plain text is the thing to lose, not to cement into a generated one.
///
/// CR0110 (performance, note): `new HttpClient()` per call or in a loop
/// exhausts sockets — the note names the lifetime question
/// (`IHttpClientFactory`, a static instance) rather than a fix;
/// `MD5.Create()`/`SHA256.Create()`/`Aes.Create()`/`new
/// JsonSerializerOptions()`/`SearchValues.Create(…)` inside a loop are
/// allocated per pass. A probe of the loop variable itself never fires;
/// a lambda handed to a collection operator is a loop.
module CSharp.Refactor.RegexRules

open System
open System.Text.RegularExpressions
open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax
open Microsoft.CodeAnalysis.Text

[<Literal>]
let InvalidCode = "CR0107"

[<Literal>]
let PlainTextCode = "CR0108"

[<Literal>]
let HoistCode = "CR0109"

[<Literal>]
let PerCallCode = "CR0110"

let private regexType (t: ITypeSymbol) =
    not (isNull t) && t.ToDisplayString() = "System.Text.RegularExpressions.Regex"

/// A literal string argument's decoded value and its own spelling.
let private literalOf (e: ExpressionSyntax) : (string * SyntaxToken) option =
    match e with
    | :? LiteralExpressionSyntax as l when l.IsKind SyntaxKind.StringLiteralExpression ->
        Some(l.Token.ValueText, l.Token)
    | _ -> None

/// The constant `RegexOptions` among the arguments, if any and constant.
let private optionsOf (model: SemanticModel) (args: SeparatedSyntaxList<ArgumentSyntax>) : RegexOptions option option =
    let optionArgs =
        args
        |> Seq.filter (fun a ->
            match model.GetTypeInfo(a.Expression).Type with
            | null -> false
            | t -> t.ToDisplayString() = "System.Text.RegularExpressions.RegexOptions")
        |> List.ofSeq

    match optionArgs with
    | [] -> Some None
    | [ a ] ->
        let c = model.GetConstantValue a.Expression

        if c.HasValue then
            Some(Some(enum<RegexOptions>(Convert.ToInt32 c.Value)))
        else
            None
    | _ -> None

/// The regex sites: a construction or a static call, with the pattern
/// argument's position.
let private patternArgument (model: SemanticModel) (node: SyntaxNode) : (ArgumentListSyntax * ExpressionSyntax) option =
    match node with
    | :? ObjectCreationExpressionSyntax as c when
        regexType (model.GetTypeInfo(c).Type)
        && not (isNull c.ArgumentList)
        && c.ArgumentList.Arguments.Count >= 1
        ->
        Some(c.ArgumentList, c.ArgumentList.Arguments.[0].Expression)
    | :? InvocationExpressionSyntax as inv when inv.ArgumentList.Arguments.Count >= 2 ->
        match model.GetSymbolInfo(inv).Symbol with
        | :? IMethodSymbol as m when m.IsStatic && regexType m.ContainingType ->
            Some(inv.ArgumentList, inv.ArgumentList.Arguments.[1].Expression)
        | _ -> None
    | _ -> None

// ---- CR0107 ----

let private invalidPatterns (tree: SyntaxTree) (model: SemanticModel) : Suggestion list =
    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun n ->
        match patternArgument model n with
        | Some(args, pattern) ->
            match literalOf pattern, optionsOf model args.Arguments with
            | Some(text, _), Some options ->
                try
                    Regex(text, defaultArg options RegexOptions.None) |> ignore
                    None
                with
                | :? ArgumentException as e ->
                    Some(
                        Suggestion.note
                            InvalidCode
                            ("The pattern is rejected by the regex engine, so this call always throws: "
                             + e.Message)
                            pattern.Span
                    )
                // anything else (`NotSupportedException`: a backreference under
                // `NonBacktracking`) proves nothing about the pattern
                | _ -> None
            | _ -> None
        | None -> None)
    |> List.ofSeq

// ---- CR0108 ----

let private metacharacters =
    set [ '\\'; '^'; '$'; '.'; '|'; '?'; '*'; '+'; '('; ')'; '['; ']'; '{'; '}'; '#' ]

/// Plain text: no metacharacter, quote or control character, so the
/// literal's own spelling can be re-emitted as a string argument.
let private plainText (text: string) =
    text <> ""
    && text
       |> Seq.forall (fun c -> not (metacharacters.Contains c) && c <> '"' && not (Char.IsControl c))

/// Plain text behind a `^` anchor, or bare.
let private plainCore (text: string) =
    plainText (if text.StartsWith "^" then text.Substring 1 else text)

/// A plain-text pattern under no options: the string operation says it, and
/// the hoist stands down for it, rewritten or not — a regex over plain text
/// is not one to cement into a generated one.
let private plainSite (text: string) (options: RegexOptions option) = plainCore text && options.IsNone

let private hasMethod (t: INamedTypeSymbol) (name: string) (arity: int) (parameterType: string) =
    not (isNull t)
    && t.GetMembers name
       |> Seq.exists (fun m ->
           match m with
           | :? IMethodSymbol as method ->
               method.Parameters.Length = arity
               && method.Parameters.[arity - 1].Type.ToDisplayString() = parameterType
           | _ -> false)

let private plainTextSites (tree: SyntaxTree) (model: SemanticModel) : Suggestion list =
    let compilation = model.Compilation

    // `MemoryExtensions.Count(ReadOnlySpan<T>, ReadOnlySpan<T>)` arrived with .NET 8
    let countOnSpan =
        hasMethod (compilation.GetTypeByMetadataName "System.MemoryExtensions") "Count" 2 "System.ReadOnlySpan<T>"

    // `string.Split(string)` with .NET Core 2.0; .NET Framework has only the array form
    let splitOnString =
        (compilation.GetSpecialType SpecialType.System_String).GetMembers "Split"
        |> Seq.exists (fun m ->
            match m with
            | :? IMethodSymbol as method ->
                method.Parameters.Length = 2
                && method.Parameters.[0].Type.SpecialType = SpecialType.System_String
                && method.Parameters.[1].IsOptional
            | _ -> false)

    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun n ->
        match n with
        | :? InvocationExpressionSyntax as inv when
            not (Text.insideExpressionTree model inv)
            && not (Text.holdsCommentOrDirective inv)
            ->
            match model.GetSymbolInfo(inv).Symbol with
            | :? IMethodSymbol as m when m.IsStatic && regexType m.ContainingType ->
                let args = inv.ArgumentList.Arguments

                let argsOk =
                    args
                    |> Seq.forall (fun a -> isNull a.NameColon && a.RefKindKeyword.IsKind SyntaxKind.None)

                // `IsMatch`, `Match(…).Success` and `Matches(…).Count` against zero are
                // one question: is the plain text there? `Contains`, or `StartsWith`
                // behind a `^` anchor; `negated` spells the `Count == 0` side
                let presence (text: string, token: SyntaxToken) (site: TextSpan) (negated: bool) =
                    let subject = Text.asReceiver args.[0].Expression
                    let spelled = token.Text

                    // a `$` anchor also matches before a final newline, which EndsWith
                    // cannot say: only the `^` anchor rewrites
                    let anchoredStart = text.StartsWith "^"
                    let core = if anchoredStart then text.Substring 1 else text

                    if not (plainText core) then
                        None
                    else
                        // the literal's spelling without its anchor; a verbatim literal keeps its `@`
                        let inner =
                            let quoteStart = spelled.IndexOf '"'
                            let body = spelled.Substring(quoteStart + 1, spelled.Length - quoteStart - 2)
                            let body = if anchoredStart then body.Substring 1 else body
                            spelled.Substring(0, quoteStart) + "\"" + body + "\""

                        let call =
                            if anchoredStart then
                                $"{subject}.StartsWith({inner}, StringComparison.Ordinal)"
                            else
                                $"{subject}.Contains({inner})"

                        let replacement = if negated then "!" + call else call

                        let edits =
                            if anchoredStart then
                                Usings.importEdit model tree inv.SpanStart "System" "StringComparison"
                                |> Option.map (fun usingEdits -> usingEdits @ [ Suggestion.replace site replacement ])
                            else
                                Some [ Suggestion.replace site replacement ]

                        match edits with
                        | Some edits when Guards.speculativeCheck model edits ->
                            Some
                                {
                                    Code = PlainTextCode
                                    Message = "A regex over plain text is a string operation"
                                    Span = site
                                    Fixes = [ Suggestion.fix "Use the string operation" PlainTextCode edits ]
                                }
                        | _ -> None

                // `.Count` of a match collection compared with zero, or with one from
                // below: the comparison is the presence test, `negated` its empty side
                let countAgainstZero (counted: MemberAccessExpressionSyntax) =
                    match counted.Parent with
                    | :? BinaryExpressionSyntax as b ->
                        let other =
                            if obj.ReferenceEquals(b.Left, counted) then
                                b.Right
                            else
                                b.Left

                        let countOnLeft = obj.ReferenceEquals(b.Left, counted)

                        match other with
                        | :? LiteralExpressionSyntax as l when l.IsKind SyntaxKind.NumericLiteralExpression ->
                            match l.Token.Value, b.Kind(), countOnLeft with
                            | (:? int as 0), SyntaxKind.GreaterThanExpression, true
                            | (:? int as 0), SyntaxKind.LessThanExpression, false
                            | (:? int as 0), SyntaxKind.NotEqualsExpression, _
                            | (:? int as 1), SyntaxKind.GreaterThanOrEqualExpression, true
                            | (:? int as 1), SyntaxKind.LessThanOrEqualExpression, false -> Some(b, false)
                            | (:? int as 0), SyntaxKind.EqualsExpression, _
                            | (:? int as 0), SyntaxKind.LessThanOrEqualExpression, true
                            | (:? int as 0), SyntaxKind.GreaterThanOrEqualExpression, false
                            | (:? int as 1), SyntaxKind.LessThanExpression, true
                            | (:? int as 1), SyntaxKind.GreaterThanExpression, false -> Some(b, true)
                            | _ -> None
                        | _ -> None
                    | _ -> None

                match m.Name, args.Count with
                | "IsMatch", 2 when argsOk ->
                    match literalOf args.[1].Expression with
                    | Some literal -> presence literal inv.Span false
                    | None -> None
                // `Regex.Match(s, "lit").Success` asks the same
                | "Match", 2 when argsOk ->
                    match literalOf args.[1].Expression, inv.Parent with
                    | Some literal, (:? MemberAccessExpressionSyntax as success) when
                        success.Name.Identifier.ValueText = "Success"
                        ->
                        presence literal success.Span false
                    | _ -> None
                // `Regex.Matches(s, "lit").Count` against zero is the presence test; on its
                // own it counts the non-overlapping occurrences of plain text, as
                // `s.AsSpan().Count("lit")` does (.NET 8)
                | "Matches", 2 when argsOk ->
                    match literalOf args.[1].Expression, inv.Parent with
                    | Some(text, token), (:? MemberAccessExpressionSyntax as counted) when
                        counted.Name.Identifier.ValueText = "Count"
                        ->
                        match countAgainstZero counted with
                        | Some(comparison, negated) -> presence (text, token) comparison.Span negated
                        // `Regex.Matches(null, …)` throws where `null.AsSpan().Count(…)` is 0: only a
                        // subject the flow analysis knows not null (a nullable-enabled file)
                        | None when
                            countOnSpan
                            && plainText text
                            && model.GetTypeInfo(args.[0].Expression).Nullability.FlowState = NullableFlowState.NotNull
                            ->
                            let replacement =
                                $"{Text.asReceiver args.[0].Expression}.AsSpan().Count({token.Text})"

                            Usings.importEdit model tree inv.SpanStart "System" "MemoryExtensions"
                            |> Option.map (fun usingEdits ->
                                usingEdits @ [ Suggestion.replace counted.Span replacement ])
                            |> Option.filter (Guards.speculativeCheck model)
                            |> Option.map (fun edits ->
                                {
                                    Code = PlainTextCode
                                    Message = "A regex count of plain text is a span count"
                                    Span = counted.Span
                                    Fixes = [ Suggestion.fix "Count the occurrences on the span" PlainTextCode edits ]
                                })
                        | None -> None
                    | _ -> None
                // `Regex.Split(s, "lit")` splits on every occurrence, empties kept, as
                // `s.Split("lit")` does (.NET Core 2.0)
                | "Split", 2 when argsOk && splitOnString ->
                    match literalOf args.[1].Expression with
                    | Some(text, token) when plainText text ->
                        let edit =
                            Suggestion.replace inv.Span $"{Text.asReceiver args.[0].Expression}.Split({token.Text})"

                        if Guards.speculativeCheck model [ edit ] then
                            Some
                                {
                                    Code = PlainTextCode
                                    Message = "A regex split on plain text is string.Split"
                                    Span = inv.Span
                                    Fixes = [ Suggestion.fix "Use string.Split" PlainTextCode [ edit ] ]
                                }
                        else
                            None
                    | _ -> None
                | "Replace", 3 when argsOk ->
                    match literalOf args.[1].Expression, literalOf args.[2].Expression with
                    | Some(pattern, patternToken), Some(replacementText, replacementToken) when
                        plainText pattern
                        && not (replacementText.Contains "$")
                        && not (replacementText.Contains "\"")
                        ->
                        let replacement =
                            Text.asReceiver args.[0].Expression
                            + ".Replace("
                            + patternToken.Text
                            + ", "
                            + replacementToken.Text
                            + ")"

                        let edit = Suggestion.replace inv.Span replacement

                        if Guards.speculativeCheck model [ edit ] then
                            Some
                                {
                                    Code = PlainTextCode
                                    Message = "A regex replace of plain text is string.Replace"
                                    Span = inv.Span
                                    Fixes = [ Suggestion.fix "Use string.Replace" PlainTextCode [ edit ] ]
                                }
                        else
                            None
                    | _ -> None
                | _ -> None
            | _ -> None
        | _ -> None)
    |> List.ofSeq

let private aZaz3Regex = Regex "[A-Za-z]{3,}"
// ---- CR0109 ----

/// A name for the hoisted regex: the local it is bound to (`var emitted =
/// Regex.Matches(…)` → `EmittedRegex`), else the pattern's words of three
/// letters or more (`ErrorCodeRegex`), else the enclosing member's name.
let private regexName (pattern: string) (site: SyntaxNode) (member': MemberDeclarationSyntax) =
    let pascal (s: string) =
        if s = "" then
            ""
        else
            string (Char.ToUpperInvariant s.[0]) + s.Substring 1

    let bound =
        site.Ancestors()
        |> Seq.takeWhile (fun a -> not (a :? StatementSyntax))
        |> Seq.tryPick (fun a ->
            match a with
            | :? EqualsValueClauseSyntax as e ->
                match e.Parent with
                | :? VariableDeclaratorSyntax as v -> Some v.Identifier.ValueText
                | _ -> None
            | _ -> None)

    // a binder named for the type (`regex`, `rx`) names nothing: the pattern's words do
    let generic = set [ "regex"; "rx"; "re"; "r"; "pattern"; "matcher"; "expression" ]

    match bound with
    | Some local when local.Length > 1 && not (generic.Contains(local.ToLowerInvariant())) -> pascal local + "Regex"
    | _ ->
        let words =
            aZaz3Regex.Matches(pattern.Replace("\\", " "))
            |> Seq.cast<Match>
            |> Seq.map (fun m -> pascal (m.Value.ToLowerInvariant()))
            |> Seq.distinct
            |> Seq.truncate 3
            |> List.ofSeq

        if not words.IsEmpty then
            String.concat "" words + "Regex"
        else
            let owner =
                match member' with
                | :? MethodDeclarationSyntax as m -> m.Identifier.ValueText
                | :? PropertyDeclarationSyntax as p -> p.Identifier.ValueText
                | _ -> "Pattern"

            pascal owner + "Regex"

/// Built per call: inside a method, accessor, local function or lambda
/// body (a field initializer or a constructor builds once per object).
let private perCallBody (node: SyntaxNode) =
    node.Ancestors()
    |> Seq.tryPick (fun a ->
        match a with
        | :? MethodDeclarationSyntax
        | :? AccessorDeclarationSyntax
        | :? LocalFunctionStatementSyntax
        | :? AnonymousFunctionExpressionSyntax -> Some true
        | :? ConstructorDeclarationSyntax
        | :? FieldDeclarationSyntax
        | :? PropertyDeclarationSyntax -> Some false
        | _ -> None)
    |> Option.defaultValue false

/// The member declaration enclosing a node and where a hoist lands: above
/// the member's leading comment block, and never under a directive.
let private hoistPoint (text: SourceText) (node: SyntaxNode) : (MemberDeclarationSyntax * int) option =
    node.Ancestors()
    |> Seq.tryPick (fun a ->
        match a with
        | :? MemberDeclarationSyntax as m when
            not (m :? BaseTypeDeclarationSyntax)
            && not (m :? BaseNamespaceDeclarationSyntax)
            ->
            Some m
        | _ -> None)
    |> Option.bind (fun m ->
        let leading = m.GetLeadingTrivia()

        if leading |> Seq.exists (fun t -> t.IsDirective) then
            None
        else
            let firstComment =
                leading
                |> Seq.tryFind (fun t ->
                    t.IsKind SyntaxKind.SingleLineCommentTrivia
                    || t.IsKind SyntaxKind.SingleLineDocumentationCommentTrivia
                    || t.IsKind SyntaxKind.MultiLineCommentTrivia
                    || t.IsKind SyntaxKind.MultiLineDocumentationCommentTrivia)

            let anchor =
                match firstComment with
                | Some t -> t.SpanStart
                | None -> m.SpanStart

            Some(m, text.Lines.GetLineFromPosition(anchor).Start))

let private isOptions (model: SemanticModel) (a: ArgumentSyntax) =
    match model.GetTypeInfo(a.Expression).Type with
    | null -> false
    | t -> t.ToDisplayString() = "System.Text.RegularExpressions.RegexOptions"

let private isTimeout (model: SemanticModel) (a: ArgumentSyntax) =
    match model.GetTypeInfo(a.Expression).Type with
    | null -> false
    | t -> t.ToDisplayString() = "System.TimeSpan"

let private hoists (tree: SyntaxTree) (model: SemanticModel) : Suggestion list =
    let text = tree.GetText()

    let generated =
        not (isNull (model.Compilation.GetTypeByMetadataName "System.Text.RegularExpressions.GeneratedRegexAttribute"))

    // names claimed by hoists this pass, per type
    let taken = System.Collections.Generic.HashSet<string>()

    // one field per pattern: a second site of the same pattern and options in a
    // type refers to the first site's field (its fix repeats the same insertion,
    // which the host applies once), and a field the type already declares is used
    let shared = System.Collections.Generic.Dictionary<string, string * TextEdit list>()

    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun n ->
        match patternArgument model n with
        | Some(args, pattern) when
            perCallBody n
            && not (Text.spansLines n)
            && not (Text.holdsCommentOrDirective n)
            && not (Text.insideExpressionTree model n)
            ->
            match literalOf pattern, optionsOf model args.Arguments with
            // a pattern the engine rejects is CR0107's: hoisted into a generated regex
            // it would fail the build, where the call only threw when reached; a
            // plain-text pattern is CR0108's — a string operation, never a regex
            // to cement into a generated one
            | Some(patternText, patternToken), Some options when
                not (plainSite patternText options)
                && (try
                        Regex(patternText, defaultArg options RegexOptions.None) |> ignore
                        true
                    with _ ->
                        false)
                ->
                let staticCall =
                    match n with
                    | :? InvocationExpressionSyntax -> true
                    | _ -> false

                // for a static call the input and pattern come first; the rest
                // (options, a replacement, an evaluator, a count) follow
                let rest =
                    if staticCall then
                        args.Arguments |> Seq.skip 2 |> List.ofSeq
                    else
                        args.Arguments |> Seq.skip 1 |> List.ofSeq

                let options = rest |> List.tryFind (isOptions model)
                let others = rest |> List.filter (isOptions model >> not)

                // a timeout stays with its call; a construction with one hoists whole as a field
                let timeoutOnCall = staticCall && (others |> List.exists (isTimeout model))

                match hoistPoint text n with
                | Some(member', insertAt) when not timeoutOnCall ->
                    let typeDecl =
                        member'.Ancestors()
                        |> Seq.tryPick (fun a ->
                            match a with
                            | :? TypeDeclarationSyntax as t -> Some t
                            | _ -> None)

                    match typeDecl with
                    | None -> None
                    | Some typeDecl ->
                        let typeSymbol = model.GetDeclaredSymbol typeDecl
                        let indent = Text.leadingWhitespace text member'.SpanStart
                        let newline = Text.newlineAt text member'.SpanStart

                        let regexSpelling =
                            if Linq.resolvesBare model n.SpanStart "System.Text.RegularExpressions" "Regex" then
                                "Regex"
                            else
                                "System.Text.RegularExpressions.Regex"

                        let optionsSuffix =
                            match options with
                            | Some o -> ", " + o.Expression.ToString()
                            | None -> ""

                        // the chain of types must be partial for a generated regex: all declared here
                        let chain =
                            member'.Ancestors()
                            |> Seq.choose (fun a ->
                                match a with
                                | :? TypeDeclarationSyntax as t -> Some t
                                | _ -> None)
                            |> List.ofSeq

                        let constructionHoistsWhole = not (staticCall || others.IsEmpty)

                        // the generated form needs a partial class, struct or record chain declared
                        // here; an interface or a generic container takes the field form
                        let useGenerated =
                            generated
                            && not constructionHoistsWhole
                            && chain
                               |> List.forall (fun t ->
                                   let s = model.GetDeclaredSymbol t

                                   not (t :? InterfaceDeclarationSyntax)
                                   && isNull t.TypeParameterList
                                   && s.DeclaringSyntaxReferences |> Seq.forall (fun r -> r.SyntaxTree = tree))

                        // the construction the field would hold, as argument text
                        let constructionArgs = patternToken.Text + optionsSuffix

                        let key =
                            String.concat
                                "|"
                                [
                                    typeDecl.Identifier.ValueText
                                    constructionArgs
                                    string useGenerated
                                    (if constructionHoistsWhole then n.ToString() else "")
                                ]

                        let argsText (al: ArgumentListSyntax) =
                            al.Arguments |> Seq.map (fun a -> a.ToString()) |> String.concat ", "

                        // a static regex field or generated method the type already declares
                        // for this construction is the one to use
                        let existing =
                            typeDecl.Members
                            |> Seq.tryPick (fun m ->
                                match m with
                                | :? FieldDeclarationSyntax as f when
                                    f.Modifiers |> Seq.exists (fun t -> t.IsKind SyntaxKind.StaticKeyword)
                                    && f.Declaration.Variables.Count = 1
                                    && not (isNull f.Declaration.Variables.[0].Initializer)
                                    ->
                                    match f.Declaration.Variables.[0].Initializer.Value with
                                    | :? ObjectCreationExpressionSyntax as c when
                                        c.Type.ToString().EndsWith "Regex"
                                        && not (isNull c.ArgumentList)
                                        && argsText c.ArgumentList = constructionArgs
                                        && not constructionHoistsWhole
                                        ->
                                        Some(f.Declaration.Variables.[0].Identifier.ValueText, [])
                                    | _ -> None
                                | :? MethodDeclarationSyntax as md when
                                    md.ParameterList.Parameters.Count = 0
                                    && md.AttributeLists
                                       |> Seq.collect (fun al -> al.Attributes)
                                       |> Seq.exists (fun a ->
                                           a.Name.ToString().EndsWith "GeneratedRegex"
                                           && not (isNull a.ArgumentList)
                                           && (a.ArgumentList.Arguments
                                               |> Seq.map (fun x -> x.ToString())
                                               |> String.concat ", ")
                                               =
                                               constructionArgs)
                                    ->
                                    Some(md.Identifier.ValueText + "()", [])
                                | _ -> None)

                        let reuse =
                            match existing with
                            | Some e -> Some e
                            | None ->
                                match shared.TryGetValue key with
                                | true, e -> Some e
                                | _ -> None

                        // the site: a construction becomes the reference; a static call an instance call
                        let siteEdit (reference: string) =
                            match n with
                            | :? InvocationExpressionSyntax as inv ->
                                let m = model.GetSymbolInfo(inv).Symbol :?> IMethodSymbol

                                let callArgs =
                                    args.Arguments.[0] :: others
                                    |> List.map (fun a -> a.ToString())
                                    |> String.concat ", "

                                Suggestion.replace inv.Span (reference + "." + m.Name + "(" + callArgs + ")")
                            | _ -> Suggestion.replace n.Span reference

                        let suggestion (reference: string) (declarationEdits: TextEdit list) =
                            let edits = declarationEdits @ [ siteEdit reference ]

                            // the generator supplies the partial method's body (CS8795) once it runs
                            let allowed = if useGenerated then [ "CS8795" ] else []

                            if Guards.speculativeCheckAllowing allowed model edits then
                                Some
                                    {
                                        Code = HoistCode
                                        Message =
                                            (if not declarationEdits.IsEmpty && useGenerated then
                                                 "The regex is built on every call: a [GeneratedRegex] compiles it once, at build time"
                                             elif not declarationEdits.IsEmpty then
                                                 "The regex is built on every call: a static field builds it once"
                                             else
                                                 $"The regex is built on every call: '{reference}' already holds it")
                                        Span = n.Span
                                        Fixes = [ Suggestion.fix "Hoist the regex" HoistCode edits ]
                                    }
                            else
                                None

                        match reuse with
                        | Some(reference, declarationEdits) -> suggestion reference declarationEdits
                        | None ->
                            let baseName = regexName patternText n member'

                            let free (candidate: string) =
                                (typeSymbol.GetMembers candidate |> Seq.isEmpty)
                                && not (taken.Contains $"{typeDecl.Identifier.ValueText}.{candidate}")

                            // the derived name, or the first numbered one that is free
                            let name =
                                baseName :: [ for i in 2..9 -> baseName + string i ]
                                |> List.tryFind free
                                |> Option.defaultValue baseName

                            let clash = not (free name)
                            taken.Add($"{typeDecl.Identifier.ValueText}.{name}") |> ignore

                            if clash then
                                Some(
                                    Suggestion.note
                                        HoistCode
                                        $"The regex is built on every call: hoist it to a static field (a member named '{name}' already exists or is claimed by another hoist)"
                                        n.Span
                                )
                            else
                                let declaration, reference =
                                    if useGenerated then
                                        indent
                                        + "[GeneratedRegex("
                                        + constructionArgs
                                        + ")]"
                                        + newline
                                        + indent
                                        + "private static partial "
                                        + regexSpelling
                                        + " "
                                        + name
                                        + "();"
                                        + newline
                                        + newline,
                                        name + "()"
                                    else
                                        let initializer =
                                            if constructionHoistsWhole then
                                                n.ToString()
                                            else
                                                $"new {regexSpelling}({constructionArgs})"

                                        indent
                                        + "private static readonly "
                                        + regexSpelling
                                        + " "
                                        + name
                                        + " = "
                                        + initializer
                                        + ";"
                                        + newline
                                        + newline,
                                        name

                                let partialEdits =
                                    if useGenerated then
                                        chain
                                        |> List.filter (fun t ->
                                            not (
                                                t.Modifiers
                                                |> Seq.exists (fun tok -> tok.IsKind SyntaxKind.PartialKeyword)
                                            ))
                                        |> List.map (fun t -> Suggestion.insert t.Keyword.SpanStart "partial ")
                                    else
                                        []

                                // static initializers run in text order: one above the member
                                // may reach it during type initialization, before a field
                                // placed below it is set — the field goes above the first
                                let fieldAt =
                                    if useGenerated then
                                        Some insertAt
                                    else
                                        typeDecl.Members
                                        |> Seq.tryFind (fun m ->
                                            m.SpanStart < member'.SpanStart
                                            && m.Modifiers |> Seq.exists (fun k -> k.IsKind SyntaxKind.StaticKeyword)
                                            && (match m with
                                                | :? FieldDeclarationSyntax as f ->
                                                    f.Declaration.Variables
                                                    |> Seq.exists (fun v -> not (isNull v.Initializer))
                                                | :? PropertyDeclarationSyntax as p -> not (isNull p.Initializer)
                                                | _ -> false))
                                        |> function
                                            | None -> Some insertAt
                                            | Some first ->
                                                first.DescendantNodes()
                                                |> Seq.tryHead
                                                |> Option.bind (hoistPoint text)
                                                |> Option.map snd

                                let declarationEdits =
                                    partialEdits
                                    @ (fieldAt
                                       |> Option.map (fun at -> Suggestion.insert at declaration)
                                       |> Option.toList)

                                match
                                    (if fieldAt.IsNone then
                                         None
                                     else
                                         suggestion reference declarationEdits)
                                with
                                | Some s ->
                                    shared.[key] <- (reference, declarationEdits)
                                    Some s
                                | None -> None
                | _ -> None
            | _ -> None
        | _ -> None)
    |> List.ofSeq

// ---- CR0110 ----

let private perCallTypes =
    set [ "System.Net.Http.HttpClient"; "System.Text.Json.JsonSerializerOptions" ]

let private perLoopFactories =
    set
        [
            "MD5.Create"
            "SHA1.Create"
            "SHA256.Create"
            "SHA384.Create"
            "SHA512.Create"
            "Aes.Create"
            "SearchValues.Create"
        ]

let private perCall (tree: SyntaxTree) (model: SemanticModel) : Suggestion list =
    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun n ->
        match n with
        | :? ObjectCreationExpressionSyntax as c ->
            match model.GetTypeInfo(c).Type with
            | null -> None
            | t when perCallTypes.Contains(t.ToDisplayString()) ->
                // a field or property initializer is once per object, not per call
                let inMember =
                    c.Ancestors()
                    |> Seq.exists (fun a ->
                        a :? MethodDeclarationSyntax
                        || a :? AccessorDeclarationSyntax
                        || a :? LocalFunctionStatementSyntax
                        || a :? AnonymousFunctionExpressionSyntax)

                // a DI factory registered once, or a Lazy<T>'s factory, runs once
                let onceFactory =
                    c.Ancestors()
                    |> Seq.exists (fun a ->
                        match a with
                        | :? InvocationExpressionSyntax as inv ->
                            let name = Linq.nameOf inv
                            name.StartsWith "AddSingleton" || name.StartsWith "TryAddSingleton"
                        | :? ObjectCreationExpressionSyntax as o -> o.Type.ToString().StartsWith "Lazy<"
                        | _ -> false)

                if not inMember || onceFactory then
                    None
                elif t.Name = "HttpClient" then
                    // a test building a client per case is not the lifetime question
                    if Text.isTestFile tree then
                        None
                    else
                        Some(
                            Suggestion.note
                                PerCallCode
                                (if Linq.insideLoop c then
                                     "An HttpClient per iteration exhausts sockets: one instance per application, or IHttpClientFactory"
                                 else
                                     "An HttpClient per call exhausts sockets and ignores DNS changes: one instance per application, or IHttpClientFactory")
                                c.Span
                        )
                elif Linq.insideLoop c then
                    Some(
                        Suggestion.note
                            PerCallCode
                            $"A {t.Name} built per iteration is rebuilt every pass: build it once outside the loop"
                            c.Span
                    )
                else
                    None
            | _ -> None
        | :? InvocationExpressionSyntax as inv when
            perLoopFactories.Contains(inv.Expression.ToString()) && Linq.insideLoop inv
            ->
            Some(
                Suggestion.note
                    PerCallCode
                    $"'{inv.Expression}' inside a loop allocates per pass: create it once outside the loop"
                    inv.Span
            )
        | _ -> None)
    |> List.ofSeq

let analyze (tree: SyntaxTree) (model: SemanticModel) (_ctx: RuleContext) : Suggestion list =
    let plain = plainTextSites tree model
    let plainSpans = plain |> List.map (fun s -> s.Span)

    invalidPatterns tree model
    @ plain
    // the string-operation rewrite subsumes the hoist on the same site
    @ (hoists tree model
       |> List.filter (fun h -> not (plainSpans |> List.exists (fun s -> s.Contains h.Span))))
    @ perCall tree model
