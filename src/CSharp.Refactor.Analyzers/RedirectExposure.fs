/// Requests that go wherever a redirect points. .NET follows redirects by
/// default, on every runtime. It drops `Authorization` on the way, keeps every
/// other header, and on a 307 or 308 sends the body again; a host that
/// answers with a redirect therefore gets them. No fix: turning redirects off
/// turns a followed one into a failure the caller has to handle, and code that
/// fetches pages needs them.
///
/// CR0198 (correctness, note): a header that may hold a secret is added in a
/// file that never turns redirects off:
///
///     request.Headers.Add("PLAID-SECRET", secret);
///     client.DefaultRequestHeaders.Add("X-Auth-Token", token);
///     foreach (var (h, v) in headers) req.Headers.Add(h, v);    // any name at all
///
/// A constant name holding secret, token, password, api key, signature or
/// credential counts, or the word key on its own; so does a name not known
/// here, which a helper is passed. `Authorization` is dropped, an idempotency
/// key isn't a secret, and a response's headers go nowhere. A library handed its `HttpClient` can't
/// turn redirects off, so it still fires there: its docs have to ask callers to.
///
/// CR0199 (correctness, note, off by default): a request maker with redirects
/// on, in a file that never turns them off: `new HttpClient()`, a
/// `HttpClientHandler` or `SocketsHttpHandler`, `WebRequest.Create`,
/// `WebClient`, `AddHttpClient`. Downloads and public pages are fine as they
/// are, so this is for a code base that sends secrets or personal data.
///
/// `AllowAutoRedirect = false` anywhere in the file's code, not in a comment or
/// string, quiets both: the question is whether the author has considered
/// redirects here. Test files are quiet.
module CSharp.Refactor.RedirectExposure

open System
open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax

[<Literal>]
let SecretHeaderCode = "CR0198"

[<Literal>]
let FollowingClientCode = "CR0199"

let private redirectsOff (tree: SyntaxTree) =
    tree.GetText().ToString().Contains "AllowAutoRedirect"
    && tree.GetRoot().DescendantNodes()
       |> Seq.exists (fun node ->
           match node with
           | :? AssignmentExpressionSyntax as assignment ->
               assignment.Right.IsKind SyntaxKind.FalseLiteralExpression
               && assignment.Left.ToString().EndsWith "AllowAutoRedirect"
           | _ -> false)

let private secretWords =
    [
        "secret"
        "token"
        "password"
        "passwd"
        "pwd"
        "apikey"
        "api-key"
        "api_key"
        "signature"
        "credential"
    ]

/// Does a header of this name hold a secret that a redirect would carry?
let holdsSecret (name: string) =
    let lower = name.ToLowerInvariant()

    if lower = "authorization" || lower.Contains "idempotency" then
        false
    else
        secretWords |> List.exists (fun (word: string) -> lower.Contains word)
        || lower.Split('-', '_') |> Array.contains "key"

/// Are these the headers of something that sends a request? A response's go nowhere.
let private isRequestHeaders (model: SemanticModel) (inv: InvocationExpressionSyntax) =
    match inv.Expression with
    | :? MemberAccessExpressionSyntax as access ->
        match model.GetTypeInfo(access.Expression).Type with
        | null -> false
        | t ->
            match t.ToDisplayString() with
            | "System.Net.Http.Headers.HttpRequestHeaders" -> true
            | "System.Net.WebHeaderCollection" ->
                match model.GetSymbolInfo(access.Expression).Symbol with
                | :? IPropertySymbol as p ->
                    match p.ContainingType.ToDisplayString() with
                    | "System.Net.WebRequest"
                    | "System.Net.HttpWebRequest"
                    | "System.Net.WebClient" -> true
                    | _ -> false
                | _ -> false
            | _ -> false
    | _ -> false

let private secretHeader
    (model: SemanticModel)
    (inv: InvocationExpressionSyntax)
    (m: IMethodSymbol)
    : Suggestion option =
    let isAdd = m.Name = "Add" || m.Name = "TryAddWithoutValidation"

    if isAdd && inv.ArgumentList.Arguments.Count >= 2 && isRequestHeaders model inv then
        let expression = inv.ArgumentList.Arguments.[0].Expression
        let name = model.GetConstantValue expression

        // a local bound once to a literal; `HeaderNames` holds standard headers
        let known =
            match model.GetSymbolInfo(expression).Symbol with
            | :? IFieldSymbol as f when f.ContainingType.Name = "HeaderNames" -> Some None
            | :? IPropertySymbol as p when p.ContainingType.Name = "HeaderNames" -> Some None
            | :? ILocalSymbol as local ->
                local.DeclaringSyntaxReferences
                |> Seq.tryHead
                |> Option.bind (fun reference ->
                    match reference.GetSyntax() with
                    | :? VariableDeclaratorSyntax as variable when not (isNull variable.Initializer) ->
                        let value = model.GetConstantValue variable.Initializer.Value

                        let assignedAgain =
                            inv.Ancestors()
                            |> Seq.tryLast
                            |> Option.exists (fun root ->
                                root.DescendantNodes()
                                |> Seq.exists (fun node ->
                                    match node with
                                    | :? AssignmentExpressionSyntax as assignment ->
                                        assignment.Left.ToString() = local.Name
                                    | _ -> false))

                        match value.HasValue, value.Value with
                        | true, (:? string as literal) when not assignedAgain -> Some(Some literal)
                        | _ -> None
                    | _ -> None)
            | _ -> None

        let header =
            match name.HasValue, name.Value, known with
            | true, (:? string as literal), _ when holdsSecret literal -> Some $"The {literal} header"
            | true, _, _ -> None
            | false, _, Some(Some literal) when holdsSecret literal -> Some $"The {literal} header"
            | false, _, Some _ -> None
            | false, _, None -> Some "This header, whose name comes from elsewhere,"

        header
        |> Option.map (fun header ->
            {
                Code = SecretHeaderCode
                Message =
                    $"{header} goes to wherever a redirect points: .NET follows redirects by default and drops only Authorization. Use a client with AllowAutoRedirect = false and treat a 3xx as a failure; if the caller supplies the HttpClient, tell callers to."
                Span = inv.Span
                Fixes = []
            })
    else
        None

let private followingClient (span: Text.TextSpan) (maker: string) =
    Some
        {
            Code = FollowingClientCode
            Message =
                $"This {maker} follows redirects, which send the body on a 307 and custom headers on any redirect to whatever host one names. If it sends secrets or personal data, set AllowAutoRedirect = false and treat a 3xx as a failure."
            Span = span
            Fixes = []
        }

let private madeClient (model: SemanticModel) (creation: BaseObjectCreationExpressionSyntax) =
    match model.GetTypeInfo(creation).Type with
    | null -> None
    | t ->
        match t.ToDisplayString() with
        | "System.Net.Http.HttpClient" when isNull creation.ArgumentList || creation.ArgumentList.Arguments.Count = 0 ->
            followingClient creation.Span "HttpClient"
        | "System.Net.Http.HttpClientHandler" -> followingClient creation.Span "HttpClientHandler"
        | "System.Net.Http.SocketsHttpHandler" -> followingClient creation.Span "SocketsHttpHandler"
        | "System.Net.WebClient" -> followingClient creation.Span "WebClient"
        | _ -> None

let private calledClient (inv: InvocationExpressionSyntax) (m: IMethodSymbol) =
    match m.Name, m.ContainingType.ToDisplayString() with
    | ("Create" | "CreateHttp"), ("System.Net.WebRequest" | "System.Net.HttpWebRequest") ->
        followingClient inv.Span "WebRequest"
    | "AddHttpClient", container when container.EndsWith "HttpClientFactoryServiceCollectionExtensions" ->
        followingClient inv.Span "AddHttpClient"
    | _ -> None

let analyze (tree: SyntaxTree) (model: SemanticModel) (_: RuleContext) : Suggestion list =
    if Text.isTestFileIn model.Compilation tree || redirectsOff tree then
        []
    else
        tree.GetRoot().DescendantNodes()
        |> Seq.collect (fun n ->
            match n with
            | :? InvocationExpressionSyntax as inv ->
                match model.GetSymbolInfo(inv).Symbol with
                | :? IMethodSymbol as m -> [ secretHeader model inv m; calledClient inv m ] |> List.choose id
                | _ -> []
            | :? BaseObjectCreationExpressionSyntax as creation -> madeClient model creation |> Option.toList
            | _ -> [])
        |> List.ofSeq
