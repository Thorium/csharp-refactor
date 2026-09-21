/// Security and hygiene: injection sinks, secrets in source, weak crypto.
///
/// CR0120 (correctness, note, priority): SQL text built from values —
/// `cmd.CommandText = $"SELECT … WHERE id={id}"`, `new SqlCommand("… " +
/// name)`, `db.Database.ExecuteSqlRaw($"…")`, Dapper's `Query($"…")` — is
/// an injection. Sinks: `CommandText` setters, `*Command` constructors,
/// `FromSqlRaw`/`ExecuteSqlRaw`/`SqlQueryRaw`/`ExecuteSqlRawAsync`,
/// Dapper's `Query*`/`Execute*`/`QueryFirst*`/`QuerySingle*`, and helpers
/// named for SQL (`ExecuteSql`, `RunQuery`, `SqlExec`, `QueryDb`). A
/// `FormattableString`-typed parameter (`FromSqlInterpolated`, `FromSql`)
/// is the safe API and never fires (typed). The text is followed one hop
/// through the nearest local; a parameter resolves to nothing — the
/// caller is where the string was built. Yields to CA2100, CA3001.
///
/// CR0121 (correctness, note): a SQL command whose text is a plain DML
/// literal (`SELECT`/`INSERT`/`UPDATE`/`DELETE`/`MERGE`) with no
/// parameter marker in any dialect (`@name`, `:name`, `?`, `$1`) and a
/// `WHERE`, `VALUES` or `SET` — the values must be coming from somewhere,
/// and a later edit will concatenate them.
///
/// CR0122 (correctness, note, priority): `Process.Start("cmd", $"/c
/// {input}")`, `psi.Arguments = "… " + input` — a command line built from
/// a value. `ProcessStartInfo.ArgumentList` takes each argument
/// unescaped; the note names it. A fixed file name and a literal
/// argument string never fire; a dynamically built string reaching
/// `FileName`, `Arguments` or the two-string `Start` does.
///
/// CR0123 (correctness, note, priority): a literal matching a provider's
/// documented key format — `sk-ant-`, `sk-`/`sk_live_`, `AIza`, `ghp_`/
/// `gho_`/`github_pat_`, `AKIA`, `xoxb-`/`xoxp-`, `whsec_`, a PEM
/// header, a three-segment JWT, `Bearer eyJ…`, an Azure `AccountKey=`.
/// Format anchoring, not entropy; a literal containing `test`, `example`,
/// `sample`, `dummy`, `fake`, `placeholder` or `123456` is a test
/// credential and stays quiet; the literal parts of an interpolated string
/// are scanned.
///
/// CR0124 (correctness, note): a credential in a `const` (or `static
/// readonly`) connection string on a non-loopback server — `Password=`/
/// `Pwd=` beside another connection key (`Server=`, `Data Source=`,
/// `Host=`, `User Id=`). Loopback servers (`localhost`, `127.0.0.1`,
/// `::1`, `(local)`, `(localdb)\…`, `.`) and placeholder passwords
/// (`password`, `test`, `changeme`, `<…>`, `{…}`, `%…%`, `$(…)`) are
/// never reported.
///
/// CR0125 (correctness, note, priority): `MD5.Create()`, `SHA1`, `DES`,
/// `TripleDES`, `RC2`, `new MD5CryptoServiceProvider()`;
/// `ServerCertificateCustomValidationCallback = (…) => true`,
/// `ServicePointManager.ServerCertificateValidationCallback` returning
/// `true`; `SecurityProtocolType.Tls`/`Tls11`/`Ssl3`. Notes; the editor
/// offers SHA-256 for a hash. The WebSocket handshake SHA-1 beside RFC
/// 6455's GUID (`258EAFA5-…`) is quiet; a SHA-1 in a `switch` arm whose
/// sibling constructs SHA-256 or stronger is a caller's format option;
/// retiring a protocol changes what the wire negotiates, so that is a
/// note only. Yields to CA5350, CA5351, CA5359, CA5364, CA5386, CA5397.
///
/// CR0126 (idiom, fix): `new SHA256Managed()`, `new
/// SHA256CryptoServiceProvider()`, `new RNGCryptoServiceProvider()`, `new
/// MD5CryptoServiceProvider()`… are `SHA256.Create()`,
/// `RandomNumberGenerator.Create()`, `MD5.Create()`: the same algorithm,
/// so behaviour is preserved (a weak one keeps its CR0125 note). Guards:
/// zero-argument constructors only; the factory returns the base type, so
/// any other mention of the obsolete name in the file — a type
/// annotation, `is`, `typeof`, a generic argument — vetoes that name (the
/// declared type of `SHA256Managed x = new SHA256Managed()` would no
/// longer fit). Yields to SYSLIB0021.
module CSharp.Refactor.SecurityRules

open System
open System.Text.RegularExpressions
open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax
open Microsoft.CodeAnalysis.Text

[<Literal>]
let SqlInjectionCode = "CR0120"

[<Literal>]
let UnparameterisedCode = "CR0121"

[<Literal>]
let CommandInjectionCode = "CR0122"

[<Literal>]
let SecretLiteralCode = "CR0123"

[<Literal>]
let ConnectionStringCode = "CR0124"

[<Literal>]
let WeakCryptoCode = "CR0125"

[<Literal>]
let ObsoleteCryptoCode = "CR0126"

// ---- shared: is a string built from values? ----

/// A string expression built from values: an interpolation with a hole, a
/// `+` chain with a non-literal operand, `string.Format`/`Concat`, or a
/// local bound one hop to such a thing. A parameter resolves to nothing.
let rec private builtFromValues (model: SemanticModel) (e: ExpressionSyntax) : bool =
    // a hole filled from a constant (`$"SET search_path = {Schema}"` over a
    // `const string Schema`) is text the author wrote, not a value
    let holeOfValue (c: InterpolatedStringContentSyntax) =
        match c with
        | :? InterpolationSyntax as h -> not (model.GetConstantValue(h.Expression).HasValue)
        | _ -> false

    match e with
    | :? InterpolatedStringExpressionSyntax as i -> i.Contents |> Seq.exists holeOfValue
    | :? BinaryExpressionSyntax as b when b.IsKind SyntaxKind.AddExpression ->
        let operands =
            let rec flat (x: ExpressionSyntax) =
                match x with
                | :? BinaryExpressionSyntax as bb when bb.IsKind SyntaxKind.AddExpression ->
                    flat bb.Left @ flat bb.Right
                | x -> [ x ]

            flat b

        operands
        |> List.exists (fun o ->
            match o with
            | :? LiteralExpressionSyntax -> false
            | :? InterpolatedStringExpressionSyntax as i -> i.Contents |> Seq.exists holeOfValue
            | o ->
                // a constant is not a value
                not (model.GetConstantValue(o).HasValue))
    | :? InvocationExpressionSyntax as inv ->
        let name = inv.Expression.ToString()

        name = "string.Format"
        || name = "String.Format"
        || name = "string.Concat"
        || name = "String.Concat"
    | :? ParenthesizedExpressionSyntax as p -> builtFromValues model p.Expression
    | :? IdentifierNameSyntax as id ->
        match model.GetSymbolInfo(id).Symbol with
        | :? ILocalSymbol as l ->
            l.DeclaringSyntaxReferences
            |> Seq.exists (fun r ->
                match r.GetSyntax() with
                | :? VariableDeclaratorSyntax as v when not (isNull v.Initializer) ->
                    builtFromValues model v.Initializer.Value
                | _ -> false)
        | :? IFieldSymbol as f when f.IsStatic && not f.IsConst ->
            f.DeclaringSyntaxReferences
            |> Seq.exists (fun r ->
                match r.GetSyntax() with
                | :? VariableDeclaratorSyntax as v when not (isNull v.Initializer) ->
                    builtFromValues model v.Initializer.Value
                | _ -> false)
        | _ -> false
    | _ -> false

// ---- CR0120 / CR0121 ----

let private sqlSinkNames =
    [
        "FromSqlRaw"
        "ExecuteSqlRaw"
        "ExecuteSqlRawAsync"
        "SqlQueryRaw"
        "ExecuteSqlCommand"
        "ExecuteSqlCommandAsync"
    ]

let private dapperNames =
    [
        "Query"
        "QueryAsync"
        "QueryFirst"
        "QueryFirstAsync"
        "QueryFirstOrDefault"
        "QueryFirstOrDefaultAsync"
        "QuerySingle"
        "QuerySingleAsync"
        "QuerySingleOrDefault"
        "QuerySingleOrDefaultAsync"
        "QueryMultiple"
        "QueryMultipleAsync"
        "Execute"
        "ExecuteAsync"
        "ExecuteScalar"
        "ExecuteScalarAsync"
        "ExecuteReader"
        "ExecuteReaderAsync"
    ]

let private namedForSql (name: string) =
    let l = name.ToLowerInvariant()

    l.Contains "sql"
    || l.StartsWith "runquery"
    || l.StartsWith "querydb"
    || l = "executequery"

/// The SQL text expressions a node hands to a sink, if it is a sink.
let private sqlSink (model: SemanticModel) (node: SyntaxNode) : ExpressionSyntax list =
    match node with
    // `cmd.CommandText = …`
    | :? AssignmentExpressionSyntax as a when
        a.IsKind SyntaxKind.SimpleAssignmentExpression
        && (match a.Left with
            | :? MemberAccessExpressionSyntax as m -> m.Name.Identifier.ValueText = "CommandText"
            | _ -> false)
        ->
        [ a.Right ]
    // `new SqlCommand(text, …)`, `new NpgsqlCommand(text)`
    | :? ObjectCreationExpressionSyntax as c when
        not (isNull c.ArgumentList)
        && c.ArgumentList.Arguments.Count >= 1
        && (let t = model.GetTypeInfo(c).Type
            not (isNull t) && t.Name.EndsWith "Command")
        ->
        [ c.ArgumentList.Arguments.[0].Expression ]
    | :? InvocationExpressionSyntax as inv ->
        match model.GetSymbolInfo(inv).Symbol with
        | :? IMethodSymbol as m ->
            let isDapper =
                m.ContainingNamespace.ToDisplayString().StartsWith "Dapper"
                && List.contains m.Name dapperNames

            let isEf = List.contains m.Name sqlSinkNames

            let isHelper =
                namedForSql m.Name
                && not (m.ContainingNamespace.ToDisplayString().StartsWith "System")

            if isDapper || isEf || isHelper then
                // the first string parameter that is not a FormattableString
                let parameters = m.Parameters |> List.ofSeq
                let args = inv.ArgumentList.Arguments |> List.ofSeq

                parameters
                |> List.tryFindIndex (fun p -> p.Type.SpecialType = SpecialType.System_String)
                |> Option.bind (fun i ->
                    // reduced extension methods drop the receiver
                    let index = if not (isNull m.ReducedFrom) then i else i

                    args
                    |> List.tryFind (fun a ->
                        not (isNull a.NameColon)
                        && a.NameColon.Name.Identifier.ValueText = parameters.[i].Name)
                    |> Option.orElse (List.tryItem index args)
                    |> Option.map (fun a -> a.Expression))
                |> Option.toList
            else
                []
        | _ -> []
    | _ -> []

let private dml =
    Regex(@"^\s*(SELECT|INSERT|UPDATE|DELETE|MERGE)\b", RegexOptions.IgnoreCase ||| RegexOptions.Compiled)

let private parameterMarker = Regex(@"(@\w+|:\w+|\?|\$\d+)", RegexOptions.Compiled)

let private needsValues =
    Regex(@"\b(WHERE|VALUES|SET)\b", RegexOptions.IgnoreCase ||| RegexOptions.Compiled)

let private sqlSinks (tree: SyntaxTree) (model: SemanticModel) : Suggestion list =
    tree.GetRoot().DescendantNodes()
    |> Seq.collect (fun n ->
        sqlSink model n
        |> List.choose (fun text ->
            if builtFromValues model text then
                Some(
                    Suggestion.note
                        SqlInjectionCode
                        "SQL text built from values is an injection: pass the values as parameters (or the interpolated FormattableString API, which parameterises the holes)"
                        text.Span
                )
            else
                let constant = model.GetConstantValue text

                match constant.Value with
                | :? string as sql when
                    dml.IsMatch sql && needsValues.IsMatch sql && not (parameterMarker.IsMatch sql)
                    ->
                    Some(
                        Suggestion.note
                            UnparameterisedCode
                            "A SQL command with no parameter at all: the values it filters or writes are coming from somewhere, and the next edit will concatenate them — pass them as parameters when they arrive"
                            text.Span
                    )
                | _ -> None))
    |> List.ofSeq

// ---- CR0122 ----

let private commandLines (tree: SyntaxTree) (model: SemanticModel) : Suggestion list =
    let isProcessType (t: ITypeSymbol) =
        not (isNull t)
        && (t.ToDisplayString() = "System.Diagnostics.ProcessStartInfo"
            || t.ToDisplayString() = "System.Diagnostics.Process")

    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun n ->
        match n with
        // `psi.Arguments = …`, `psi.FileName = …`
        | :? AssignmentExpressionSyntax as a when
            a.IsKind SyntaxKind.SimpleAssignmentExpression
            && (match a.Left with
                | :? MemberAccessExpressionSyntax as m ->
                    (m.Name.Identifier.ValueText = "Arguments"
                     || m.Name.Identifier.ValueText = "FileName")
                    && isProcessType (model.GetTypeInfo(m.Expression).Type)
                | :? IdentifierNameSyntax as id ->
                    // inside an object initializer of a ProcessStartInfo
                    (id.Identifier.ValueText = "Arguments" || id.Identifier.ValueText = "FileName")
                    && (match a.Parent with
                        | :? InitializerExpressionSyntax as i -> isProcessType (model.GetTypeInfo(i.Parent).Type)
                        | _ -> false)
                | _ -> false)
            && builtFromValues model a.Right
            ->
            Some(
                Suggestion.note
                    CommandInjectionCode
                    "A command line built from a value is an injection: ProcessStartInfo.ArgumentList takes each argument unescaped"
                    a.Right.Span
            )
        // `Process.Start(file, arguments)`, `new ProcessStartInfo(file, arguments)`
        | :? InvocationExpressionSyntax as inv when
            inv.Expression.ToString() = "Process.Start"
            && inv.ArgumentList.Arguments.Count = 2
            && inv.ArgumentList.Arguments
               |> Seq.exists (fun a -> builtFromValues model a.Expression)
            ->
            Some(
                Suggestion.note
                    CommandInjectionCode
                    "A command line built from a value is an injection: ProcessStartInfo.ArgumentList takes each argument unescaped"
                    inv.Span
            )
        | :? ObjectCreationExpressionSyntax as c when
            isProcessType (model.GetTypeInfo(c).Type)
            && not (isNull c.ArgumentList)
            && c.ArgumentList.Arguments
               |> Seq.exists (fun a -> builtFromValues model a.Expression)
            ->
            Some(
                Suggestion.note
                    CommandInjectionCode
                    "A command line built from a value is an injection: ProcessStartInfo.ArgumentList takes each argument unescaped"
                    c.Span
            )
        | _ -> None)
    |> List.ofSeq

// ---- CR0123 / CR0124 ----

let private keyPatterns =
    [
        "an Anthropic API key", Regex(@"\bsk-ant-[A-Za-z0-9_-]{20,}", RegexOptions.Compiled)
        "an OpenAI-style secret key", Regex(@"\bsk-[A-Za-z0-9]{20,}", RegexOptions.Compiled)
        "a Stripe live key", Regex(@"\b(sk|rk|pk)_live_[A-Za-z0-9]{16,}", RegexOptions.Compiled)
        "a Stripe/Svix webhook secret", Regex(@"\bwhsec_[A-Za-z0-9]{16,}", RegexOptions.Compiled)
        "a Google API key", Regex(@"\bAIza[A-Za-z0-9_-]{30,}", RegexOptions.Compiled)
        "a GitHub token",
        Regex(@"\b(ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9]{30,}|\bgithub_pat_[A-Za-z0-9_]{30,}", RegexOptions.Compiled)
        "an AWS access key id", Regex(@"\bAKIA[A-Z0-9]{16}\b", RegexOptions.Compiled)
        "a Slack token", Regex(@"\bxox[bpoas]-[A-Za-z0-9-]{10,}", RegexOptions.Compiled)
        "a private key", Regex(@"-----BEGIN (RSA |EC |DSA |OPENSSH |PGP )?PRIVATE KEY-----", RegexOptions.Compiled)
        "a JWT", Regex(@"\beyJ[A-Za-z0-9_-]{10,}\.eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}", RegexOptions.Compiled)
        "a bearer token", Regex(@"\bBearer eyJ[A-Za-z0-9_-]{10,}", RegexOptions.Compiled)
        "an Azure storage key", Regex(@"AccountKey=[A-Za-z0-9+/]{40,}={0,2}", RegexOptions.Compiled)
    ]

/// `123456` is the standard made-up key; six specific digits do not occur
/// by chance in real key material (one in 64^6 per position of base64).
let private placeholderWords =
    [
        "test"
        "example"
        "sample"
        "dummy"
        "fake"
        "placeholder"
        "xxxx"
        "your"
        "123456"
    ]

let private isPlaceholder (s: string) =
    let l = s.ToLowerInvariant()
    // an elided key (`MIIEvQ...`) is a sample too
    placeholderWords |> List.exists l.Contains || l.Contains "..." || l.Contains "…"

let private literalTexts (node: SyntaxNode) : (string * TextSpan) list =
    match node with
    | :? LiteralExpressionSyntax as l when l.IsKind SyntaxKind.StringLiteralExpression -> [ l.Token.ValueText, l.Span ]
    | :? InterpolatedStringTextSyntax as t -> [ t.TextToken.ValueText, t.Span ]
    | _ -> []

let private secrets (tree: SyntaxTree) : Suggestion list =
    tree.GetRoot().DescendantNodes()
    |> Seq.collect literalTexts
    |> Seq.choose (fun (text, span) ->
        if text.Length < 16 || isPlaceholder text then
            None
        else
            keyPatterns
            |> List.tryFind (fun (_, r) -> r.IsMatch text)
            |> Option.map (fun (what, _) ->
                Suggestion.note
                    SecretLiteralCode
                    $"This literal matches the format of {what}: a secret in source is in every clone and every log of it — move it to configuration and rotate it"
                    span))
    |> List.ofSeq

let private connectionKeys =
    Regex(
        @"\b(Server|Data Source|Host|User Id|Uid|Database|Initial Catalog)\s*=",
        RegexOptions.IgnoreCase ||| RegexOptions.Compiled
    )

let private passwordKey =
    Regex(@"\b(Password|Pwd)\s*=\s*([^;]*)", RegexOptions.IgnoreCase ||| RegexOptions.Compiled)

let private serverKey =
    Regex(@"\b(Server|Data Source|Host|Addr|Address)\s*=\s*([^;,]*)", RegexOptions.IgnoreCase ||| RegexOptions.Compiled)

let private loopback (server: string) =
    let s = server.Trim().ToLowerInvariant()

    s = "localhost"
    || s.StartsWith "localhost\\"
    || s.StartsWith "127."
    || s = "::1"
    || s = "(local)"
    || s.StartsWith "(localdb)"
    || s = "."
    || s.StartsWith ".\\"

let private placeholderPassword (p: string) =
    let l = p.Trim().ToLowerInvariant()

    l = ""
    || l = "password"
    || l = "test"
    || l = "changeme"
    || l = "secret"
    || l.StartsWith "<"
    || l.StartsWith "{"
    || l.StartsWith "%"
    || l.StartsWith "$("
    || l.StartsWith "${"

let private connectionStrings (tree: SyntaxTree) : Suggestion list =
    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun n ->
        match n with
        | :? VariableDeclaratorSyntax as v when not (isNull v.Initializer) ->
            let constant =
                match v.Parent.Parent with
                | :? FieldDeclarationSyntax as f ->
                    f.Modifiers |> Seq.exists (fun m -> m.IsKind SyntaxKind.ConstKeyword)
                    || (f.Modifiers |> Seq.exists (fun m -> m.IsKind SyntaxKind.StaticKeyword)
                        && f.Modifiers |> Seq.exists (fun m -> m.IsKind SyntaxKind.ReadOnlyKeyword))
                | :? LocalDeclarationStatementSyntax as d -> d.IsConst
                | _ -> false

            match v.Initializer.Value with
            | :? LiteralExpressionSyntax as l when constant && l.IsKind SyntaxKind.StringLiteralExpression ->
                let text = l.Token.ValueText
                let password = passwordKey.Match text
                let server = serverKey.Match text

                if
                    password.Success
                    && connectionKeys.IsMatch text
                    && not (placeholderPassword password.Groups.[2].Value)
                    && not (server.Success && loopback server.Groups.[2].Value)
                then
                    Some(
                        Suggestion.note
                            ConnectionStringCode
                            "A credential in a constant connection string to a non-loopback server: move it to configuration and rotate it"
                            l.Span
                    )
                else
                    None
            | _ -> None
        | _ -> None)
    |> List.ofSeq

// ---- CR0125 / CR0126 ----

let private weakHashes = [ "MD5"; "SHA1"; "DES"; "TripleDES"; "RC2"; "RIPEMD160" ]
let private weakHashesProbeSet = System.Collections.Generic.HashSet(weakHashes)

let private obsoleteToFactory =
    dict
        [
            "SHA256Managed", "SHA256"
            "SHA256CryptoServiceProvider", "SHA256"
            "SHA256Cng", "SHA256"
            "SHA384Managed", "SHA384"
            "SHA384CryptoServiceProvider", "SHA384"
            "SHA384Cng", "SHA384"
            "SHA512Managed", "SHA512"
            "SHA512CryptoServiceProvider", "SHA512"
            "SHA512Cng", "SHA512"
            "SHA1Managed", "SHA1"
            "SHA1CryptoServiceProvider", "SHA1"
            "SHA1Cng", "SHA1"
            "MD5CryptoServiceProvider", "MD5"
            "MD5Cng", "MD5"
            "RNGCryptoServiceProvider", "RandomNumberGenerator"
            "AesManaged", "Aes"
            "AesCryptoServiceProvider", "Aes"
            "AesCng", "Aes"
        ]

[<Literal>]
let private rfc6455 = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"

let private weakCrypto (tree: SyntaxTree) (model: SemanticModel) : Suggestion list =
    let fileText = tree.GetRoot().ToString()
    let websocketHandshake = fileText.Contains rfc6455

    let strongerSibling (node: SyntaxNode) =
        // a switch section or arm whose sibling constructs SHA-256 or stronger
        node.Ancestors()
        |> Seq.tryPick (fun a ->
            match a with
            | :? SwitchStatementSyntax as s -> Some(s.ToString())
            | :? SwitchExpressionSyntax as s -> Some(s.ToString())
            | _ -> None)
        |> Option.exists (fun s -> s.Contains "SHA256" || s.Contains "SHA384" || s.Contains "SHA512")

    tree.GetRoot().DescendantNodes()
    |> Seq.choose (fun n ->
        match n with
        // `MD5.Create()`, `SHA1.Create()`, `new MD5CryptoServiceProvider()`
        | :? InvocationExpressionSyntax as inv when
            Linq.nameOf inv = "Create"
            && (match model.GetSymbolInfo(inv).Symbol with
                | :? IMethodSymbol as m ->
                    weakHashesProbeSet.Contains m.ContainingType.Name
                    && m.ContainingNamespace.ToDisplayString() = "System.Security.Cryptography"
                | _ -> false)
            ->
            let algorithm =
                (model.GetSymbolInfo(inv).Symbol :?> IMethodSymbol).ContainingType.Name

            if (algorithm = "SHA1" && websocketHandshake) || strongerSibling inv then
                None
            else
                let hash = algorithm = "MD5" || algorithm = "SHA1" || algorithm = "RIPEMD160"

                Some
                    {
                        Code = WeakCryptoCode
                        Message =
                            (if hash then
                                 $"{algorithm} is broken for anything security-relevant: SHA-256 or stronger (a checksum of non-hostile data can keep it, and say so)"
                             else
                                 $"{algorithm} is a weak cipher: AES")
                        Span = inv.Span
                        Fixes =
                            (if hash then
                                 [
                                     Suggestion.fix
                                         "Use SHA256.Create()"
                                         WeakCryptoCode
                                         [ Suggestion.replace inv.Span "SHA256.Create()" ]
                                     |> Suggestion.editorOnly
                                 ]
                             else
                                 [])
                    }
        | :? ObjectCreationExpressionSyntax as c ->
            match model.GetTypeInfo(c).Type with
            | null -> None
            | t when
                t.ContainingNamespace.ToDisplayString() = "System.Security.Cryptography"
                && (weakHashes |> List.exists t.Name.StartsWith)
                && not (websocketHandshake && t.Name.StartsWith "SHA1")
                && not (strongerSibling c)
                ->
                Some(
                    Suggestion.note
                        WeakCryptoCode
                        $"{t.Name} is a weak algorithm: SHA-256 or stronger for a hash, AES for a cipher (a checksum of non-hostile data can keep it, and say so)"
                        c.Span
                )
            | _ -> None
        // `ServerCertificateCustomValidationCallback = (…) => true`
        | :? AssignmentExpressionSyntax as a when
            (match a.Left with
             | :? MemberAccessExpressionSyntax as m ->
                 m.Name.Identifier.ValueText.EndsWith "CertificateValidationCallback"
                 || m.Name.Identifier.ValueText = "ServerCertificateCustomValidationCallback"
             | :? IdentifierNameSyntax as id -> id.Identifier.ValueText = "ServerCertificateCustomValidationCallback"
             | _ -> false)
            && (match a.Right with
                | :? LambdaExpressionSyntax as l ->
                    match l.Body with
                    | :? LiteralExpressionSyntax as lit -> lit.IsKind SyntaxKind.TrueLiteralExpression
                    | :? BlockSyntax as b ->
                        b.Statements.Count = 1
                        && b.Statements.[0].ToString().Replace(" ", "") = "returntrue;"
                    | _ -> false
                | _ -> a.Right.ToString().Contains "DangerousAcceptAnyServerCertificateValidator")
            ->
            Some(
                Suggestion.note
                    WeakCryptoCode
                    "Accepting every server certificate disables TLS: pin or validate the certificate, and never ship this"
                    a.Span
            )
        // `SecurityProtocolType.Tls11`, `.Tls`, `.Ssl3`
        | :? MemberAccessExpressionSyntax as m when
            m.Expression.ToString().EndsWith "SecurityProtocolType"
            && List.contains m.Name.Identifier.ValueText [ "Ssl3"; "Tls"; "Tls11" ]
            ->
            Some(
                Suggestion.note
                    WeakCryptoCode
                    $"{m.Name.Identifier.ValueText} is a retired protocol: TLS 1.2 or later (or leave the OS default, SystemDefault)"
                    m.Span
            )
        | _ -> None)
    |> List.ofSeq

let private obsoleteCrypto (tree: SyntaxTree) (model: SemanticModel) : Suggestion list =
    let root = tree.GetRoot()

    // every mention of an obsolete name in the file, by name
    let mentions (name: string) =
        root.DescendantTokens()
        |> Seq.filter (fun t -> t.IsKind SyntaxKind.IdentifierToken && t.ValueText = name)
        |> Seq.length

    root.DescendantNodes()
    |> Seq.choose (fun n ->
        match n with
        | :? ObjectCreationExpressionSyntax as c when isNull c.ArgumentList || c.ArgumentList.Arguments.Count = 0 ->
            match model.GetTypeInfo(c).Type with
            | null -> None
            | t when
                obsoleteToFactory.ContainsKey t.Name
                && t.ContainingNamespace.ToDisplayString() = "System.Security.Cryptography"
                ->
                let factory = obsoleteToFactory.[t.Name]

                // the construction sites mention the name once each; any surplus mention vetoes
                let constructions =
                    root.DescendantNodes()
                    |> Seq.filter (fun x ->
                        match x with
                        | :? ObjectCreationExpressionSyntax as o -> o.Type.ToString().EndsWith t.Name
                        | _ -> false)
                    |> Seq.length

                if mentions t.Name > constructions then
                    None
                else
                    let spelled =
                        if Linq.resolvesBare model c.SpanStart "System.Security.Cryptography" factory then
                            factory
                        else
                            "System.Security.Cryptography." + factory

                    let edit = Suggestion.replace c.Span (spelled + ".Create()")

                    if Guards.speculativeCheck model [ edit ] then
                        Some
                            {
                                Code = ObsoleteCryptoCode
                                Message =
                                    $"'{t.Name}' is obsolete: '{factory}.Create()' gives the same algorithm on every platform"
                                Span = c.Span
                                Fixes = [ Suggestion.fix $"Use {factory}.Create()" ObsoleteCryptoCode [ edit ] ]
                            }
                    else
                        None
            | _ -> None
        | _ -> None)
    |> List.ofSeq

let analyze (tree: SyntaxTree) (model: SemanticModel) (_ctx: RuleContext) : Suggestion list =
    sqlSinks tree model
    @ commandLines tree model
    @ secrets tree
    @ connectionStrings tree
    @ weakCrypto tree model
    @ obsoleteCrypto tree model
