/// Logging shapes, for `Microsoft.Extensions.Logging` and Serilog.
///
/// CR0114 (correctness, note): a message template that does not fit its
/// arguments — a placeholder name used twice (`"{Id} then {Id}"`: the
/// second binds the second argument, or nothing), more placeholders than
/// arguments, more arguments than placeholders, or an interpolated string
/// where a template was expected (the holes are formatted before the
/// logger sees them, so no property is captured and every call is a new
/// template). The template may be a `"…" + "…"` chain of literals; a
/// trailing array literal is the params array spelled out; one trailing
/// identifier argument may be the params array passed whole — its count
/// is invisible, so no arity claim there; a hole-free `$"…"` compiles to
/// a constant and is not the interpolation defect. Placeholder syntax:
/// `{@Name}`, `{$Name}`, `{Name:format}`, `{Name,align}`, `{{` literal.
/// Yields to CA2017 and CA2254.
///
/// CR0115 (correctness, fix): `catch (Exception ex) { _log.LogError("sync
/// failed {Id}", id); }` — the exception is caught, the log line loses it.
/// The exception-first overload takes it: `_log.LogError(ex, "sync failed
/// {Id}", id);`. Guards: typed `ILogger` (`Log*` methods) or Serilog
/// (`Error`/`Warning`/`Fatal`/… on `ILogger`/`Log`); the call sits in a
/// `catch` that binds the exception; ANY mention of the exception in the
/// arguments counts as handled, `ex.Message` included (a PII choice the
/// rule must not escalate); the template is in first position (an
/// `EventId` first wants the exception second — that shape is a note);
/// the overload with the exception first exists on the callee's type.
module CSharp.Refactor.LoggingRules

open System
open System.Text.RegularExpressions
open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax
open Microsoft.CodeAnalysis.Text

[<Literal>]
let TemplateCode = "CR0114"

[<Literal>]
let CatchLogCode = "CR0115"

let private msLoggerMethods =
    set
        [
            "LogTrace"
            "LogDebug"
            "LogInformation"
            "LogWarning"
            "LogError"
            "LogCritical"
            "Log"
        ]

let private serilogMethods =
    set [ "Verbose"; "Debug"; "Information"; "Warning"; "Error"; "Fatal" ]

/// A logging call: the method symbol, and the position of the message template.
let private loggingCall (model: SemanticModel) (inv: InvocationExpressionSyntax) : (IMethodSymbol * int) option =
    match model.GetSymbolInfo(inv).Symbol with
    | :? IMethodSymbol as m ->
        let ns = m.ContainingNamespace.ToDisplayString()

        let isMs =
            ns.StartsWith "Microsoft.Extensions.Logging" && msLoggerMethods.Contains m.Name

        let isSerilog = ns.StartsWith "Serilog" && serilogMethods.Contains m.Name

        if not (isMs || isSerilog) then
            None
        else
            // the template is the `message`/`messageTemplate` parameter; a reduced extension
            // method already drops its receiver, so the index is the argument's
            let templateIndex =
                m.Parameters
                |> Seq.tryFindIndex (fun p ->
                    p.Type.SpecialType = SpecialType.System_String
                    && (p.Name = "message" || p.Name = "messageTemplate"))

            templateIndex |> Option.map (fun i -> m, i)
    | _ -> None

// ---- CR0114 ----

let private placeholderPattern =
    Regex(@"\{([@$]?)([A-Za-z_][A-Za-z0-9_]*)(?:[,:][^}]*)?\}", RegexOptions.Compiled)

/// The literal text of a template: a literal, or a `+` chain of literals.
let rec private templateText (e: ExpressionSyntax) : string option =
    match e with
    | :? LiteralExpressionSyntax as l when l.IsKind SyntaxKind.StringLiteralExpression -> Some l.Token.ValueText
    | :? BinaryExpressionSyntax as b when b.IsKind SyntaxKind.AddExpression ->
        match templateText b.Left, templateText b.Right with
        | Some a, Some c -> Some(a + c)
        | _ -> None
    | :? ParenthesizedExpressionSyntax as p -> templateText p.Expression
    | _ -> None

let private templates (tree: SyntaxTree) (model: SemanticModel) : Suggestion list =
    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun n ->
        match n with
        | :? InvocationExpressionSyntax as inv ->
            match loggingCall model inv with
            | Some(m, templateIndex) ->
                let args = inv.ArgumentList.Arguments |> List.ofSeq

                // the argument feeding the template parameter, by position or name
                let templateArg =
                    args
                    |> List.tryFind (fun a ->
                        not (isNull a.NameColon)
                        && a.NameColon.Name.Identifier.ValueText = m.Parameters.[templateIndex].Name)
                    |> Option.orElse (List.tryItem templateIndex args)

                match templateArg with
                | None -> None
                | Some templateArg ->
                    match templateArg.Expression with
                    | :? InterpolatedStringExpressionSyntax as i when
                        i.Contents |> Seq.exists (fun c -> c :? InterpolationSyntax)
                        ->
                        Some(
                            Suggestion.note
                                TemplateCode
                                "An interpolated string where a message template was expected: the holes are formatted before the logger sees them, no property is captured, and every call is a new template — use {Name} placeholders and pass the values"
                                templateArg.Span
                        )
                    | e ->
                        match templateText e with
                        | None -> None
                        | Some template ->
                            let unescaped = template.Replace("{{", "").Replace("}}", "")

                            let holes =
                                placeholderPattern.Matches unescaped
                                |> Seq.cast<Match>
                                |> Seq.map (fun mt -> mt.Groups.[2].Value)
                                |> List.ofSeq

                            let duplicate =
                                holes |> List.countBy id |> List.tryFind (fun (_, c) -> c > 1) |> Option.map fst

                            // the arguments after the template
                            let after =
                                args
                                |> List.skipWhile (fun a -> not (obj.ReferenceEquals(a, templateArg)))
                                |> List.tail
                                |> List.filter (fun a -> isNull a.NameColon)

                            // one trailing identifier may be the params array passed whole
                            let paramsWhole =
                                after.Length = 1
                                && (match model.GetTypeInfo(after.Head.Expression).Type with
                                    | :? IArrayTypeSymbol -> true
                                    | _ -> false)

                            let count =
                                match after with
                                | [ a ] ->
                                    match a.Expression with
                                    | :? ArrayCreationExpressionSyntax as c when not (isNull c.Initializer) ->
                                        Some c.Initializer.Expressions.Count
                                    | :? ImplicitArrayCreationExpressionSyntax as c ->
                                        Some c.Initializer.Expressions.Count
                                    | :? CollectionExpressionSyntax as c -> Some c.Elements.Count
                                    | _ when paramsWhole -> None
                                    | _ -> Some 1
                                | xs -> Some xs.Length

                            match duplicate with
                            | Some name ->
                                Some(
                                    Suggestion.note
                                        TemplateCode
                                        $"The placeholder '{{{name}}}' appears twice: each occurrence binds the next argument, so the second reads the wrong value or none"
                                        templateArg.Span
                                )
                            | None ->
                                match count with
                                | Some c when c <> holes.Length ->
                                    Some(
                                        Suggestion.note
                                            TemplateCode
                                            $"The template has {holes.Length} placeholder(s) and the call passes {c} value(s): the extras are dropped or the holes print empty"
                                            templateArg.Span
                                    )
                                | _ -> None
            | None -> None
        | _ -> None)
    |> List.ofSeq

// ---- CR0115 ----

let private catchLogs (tree: SyntaxTree) (model: SemanticModel) : Suggestion list =
    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun n ->
        match n with
        | :? InvocationExpressionSyntax as inv ->
            match loggingCall model inv with
            | Some(m, templateIndex) ->
                // the enclosing catch that binds an exception
                let handler =
                    inv.Ancestors()
                    |> Seq.tryPick (fun a ->
                        match a with
                        | :? CatchClauseSyntax as c when
                            not (isNull c.Declaration)
                            && not (c.Declaration.Identifier.IsKind SyntaxKind.None)
                            ->
                            Some c
                        | :? AnonymousFunctionExpressionSyntax
                        | :? LocalFunctionStatementSyntax -> None
                        | _ -> None)

                match handler with
                | None -> None
                | Some c ->
                    let binder = c.Declaration.Identifier.ValueText

                    // any mention of the exception counts as handled
                    if Text.mentionsName binder inv.ArgumentList then
                        None
                    else
                        let args = inv.ArgumentList.Arguments |> List.ofSeq
                        let templatePosition = templateIndex

                        // does the callee's type carry the exception-first overload?
                        let hasExceptionOverload =
                            let owner = m.ContainingType

                            owner.GetMembers m.Name
                            |> Seq.exists (fun s ->
                                match s with
                                | :? IMethodSymbol as o ->
                                    let ps = o.Parameters |> List.ofSeq
                                    // an extension's unreduced form carries its receiver first
                                    let skip = if o.IsExtensionMethod then 1 else 0

                                    ps.Length > skip && ps.[skip].Type.ToDisplayString() = "System.Exception"
                                | _ -> false)

                        if
                            not hasExceptionOverload
                            || args |> List.exists (fun a -> not (isNull a.NameColon))
                        then
                            None
                        elif templatePosition = 0 && not args.IsEmpty then
                            let edit = Suggestion.insert args.Head.SpanStart (binder + ", ")

                            if Guards.speculativeCheck model [ edit ] then
                                Some
                                    {
                                        Code = CatchLogCode
                                        Message =
                                            $"The caught exception '{binder}' is not on the log line: pass it first, so its type, message and stack are kept"
                                        Span = inv.Span
                                        Fixes = [ Suggestion.fix "Log the exception" CatchLogCode [ edit ] ]
                                    }
                            else
                                None
                        else
                            Some(
                                Suggestion.note
                                    CatchLogCode
                                    $"The caught exception '{binder}' is not on the log line: pass it (after the EventId) so its type, message and stack are kept"
                                    inv.Span
                            )
            | None -> None
        | _ -> None)
    |> List.ofSeq

let analyze (tree: SyntaxTree) (model: SemanticModel) (_ctx: RuleContext) : Suggestion list =
    templates tree model @ catchLogs tree model
