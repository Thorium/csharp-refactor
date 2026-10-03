module CSharp.Refactor.Tests.SecurityTests

open Xunit
open CSharp.Refactor.Tests.Harness

let private dbShim =
    csharp
        """
        namespace System.Data.Common
        {
            public class DbCommand { public string CommandText { get; set; } }
        }
        namespace Dapper
        {
            public static class SqlMapper
            {
                public static System.Collections.Generic.IEnumerable<T> Query<T>(this System.Data.Common.DbCommand c, string sql, object param = null) => null;
            }
        }
        """

// ---- CR0120 / CR0121 ----

[<Fact>]
let ``SQL text built from values is noted at every sink; a constant DML with no parameter is the other note`` () =
    let source =
        csharp
            """
            using System;
            using System.Data.Common;
            using Dapper;
            class C
            {
                void A(DbCommand cmd, int id) { cmd.CommandText = $"SELECT * FROM t WHERE id = {id}"; }
                void B(DbCommand cmd, string name) { var sql = "SELECT * FROM t WHERE name = '" + name + "'"; cmd.CommandText = sql; }
                void D(DbCommand cmd, int id) { cmd.CommandText = "SELECT * FROM t WHERE id = @id"; }
                void E(DbCommand cmd) { cmd.CommandText = "SELECT * FROM t WHERE id = 1"; }
                void F(DbCommand cmd, string name) { cmd.Query<int>($"SELECT id FROM t WHERE name = {name}"); }
                void G(DbCommand cmd) { cmd.CommandText = "SELECT * FROM t"; }
                void H(DbCommand cmd, string sql) { cmd.CommandText = sql; }
            }
            """
        + dbShim

    Assert.Equal(3, (suggestCode "CR0120" source).Length)
    Assert.Equal(1, (suggestCode "CR0121" source).Length)

[<Fact>]
let ``CR0121 accepts a PostgreSQL debit whose values arrive as positional $1 and $2 parameters`` () =
    // Npgsql's positional markers are parameters as much as SQL Server's @name
    let source =
        csharp
            """
            using System.Data.Common;
            class LedgerRepository
            {
                void PrepareDebit(DbCommand cmd)
                {
                    cmd.CommandText = "UPDATE accounts SET balance = balance - $1 WHERE account_id = $2";
                }
            }
            """

    Assert.Empty(suggestCode "CR0121" source)

[<Fact>]
let ``a hole filled from a constant is not a value; a var local or a parameter is`` () =
    let source =
        csharp
            """
            using System.Data.Common;
            class C
            {
                const string Schema = "app";
                void A(DbCommand cmd) { cmd.CommandText = $"SET search_path = \"{Schema}\", public"; }
                void B(DbCommand cmd) { const string table = "users"; cmd.CommandText = $"SELECT 1 FROM {table} WHERE id = @id"; }
                void D(DbCommand cmd) { cmd.CommandText = $"SELECT {1} FROM t WHERE id = @id"; }
                void E(DbCommand cmd, string schema) { cmd.CommandText = $"SET search_path = {schema}"; }
                void F(DbCommand cmd) { var schema = "app"; cmd.CommandText = $"SET search_path = {schema}"; }
            }
            """
        + dbShim

    // E and F: a parameter, and a `var` the next line may reassign
    Assert.Equal(2, (suggestCode "CR0120" source).Length)

// ---- CR0122 ----

[<Fact>]
let ``a command line built from a value is noted; a fixed one is not`` () =
    let source =
        csharp
            """
            using System.Diagnostics;
            class C
            {
                void A(string input) { Process.Start("cmd", $"/c {input}"); }
                void B(string input) { var psi = new ProcessStartInfo { FileName = "git", Arguments = "log " + input }; }
                void D() { Process.Start("git", "log"); }
                void E(string input) { var psi = new ProcessStartInfo("git"); psi.ArgumentList.Add(input); }
            }
            """

    Assert.Equal(2, (suggestCode "CR0122" source).Length)

// ---- CR0123 / CR0124 ----

[<Fact>]
let ``key-shaped literals and constant connection strings with credentials are noted; placeholders and loopback are not``
    ()
    =
    let source =
        csharp
            """
            class C
            {
                const string Anthropic = "sk-ant-api03-ABCDEFGHIJKLMNOPQRSTUVWXYZ0246813579abcdefghij";
                const string Github = "ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ0246813579abcd";
                const string Aws = "AKIAIOSFODNN7EXAMPLE";
                const string TestKey = "sk-test-ABCDEFGHIJKLMNOPQRSTUVWXYZ0246813579";
                const string MadeUp = "ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ123456789abcde";
                const string Pem = "-----BEGIN RSA PRIVATE KEY-----\nMIIE123456vQIBADANBgkqhkiG9w0BAQEFAASCBKcwggSjAgEAAoIBAQ";
                const string Prod = "Server=db.internal;Database=app;User Id=sa;Password=Hunter2!;";
                const string Local = "Server=localhost;Database=app;User Id=sa;Password=Hunter2!;";
                const string Placeholder = "Server=db.internal;Database=app;User Id=sa;Password=<password>;";
                string Runtime(string p) => "Server=db.internal;Password=" + p;
            }
            """

    // Anthropic, Github (AKIA…EXAMPLE and sk-test are placeholders, and so is
    // anything carrying `123456`, the standard made-up key)
    Assert.Equal(2, (suggestCode "CR0123" source).Length)
    Assert.Equal(1, (suggestCode "CR0124" source).Length)

[<Fact>]
let ``CR0124 notes a literal given to something named as a credential, never showing it; keys, placeholders and prose are quiet``
    ()
    =
    let source =
        csharp
            """
            using System.Net;
            class Settings { public string Password { get; set; } = ""; public string ApiKey = ""; public string PasswordHint = ""; }
            class C
            {
                string servicePassword = "Tr0ub4dor&3";
                const string ClientSecret = @"s3cr3t-Value-91";
                string this[string key] => key;
                void Login(string user, string password) { }
                void Use(string name) { }
                void A()
                {
                    Login("admin", "hunter2-Xy");
                    Login(user: "admin", password: "hunter2-Xz");
                    var s = new Settings { Password = "Winter2024!", ApiKey = "9f8e7d6c5b4a" };
                    s.Password = "Summer2024!";
                    var token = "abc123def456";
                    var cred = new NetworkCredential("svc", "Pa55w0rd!");
                }
                void B()
                {
                    Login("admin", "");
                    Login("admin", "password");
                    Login("admin", "changeme");
                    Login("admin", "<password>");
                    Login("admin", "***");
                    var passwordKey = "Jwt:Password";
                    var password = "Password";
                    var secret = this["ClientSecret"];
                    var apiKey = "X-Api-Key";
                    var pwd = "Enter password:";
                    var s = new Settings { PasswordHint = "your first pet" };
                    Use(nameof(servicePassword));
                    var tokenEndpoint = "https://login.example/token";
                    var token = "Bearer";
                    var accessToken = "https://x/y";
                    var clientSecret = "client_secret";
                    var nextToken = "IDENT_7";
                    var endToken = "while-loop";
                    var pwd2 = "abc";
                }
            }
            """

    let fired = suggestCode "CR0124" source

    Assert.Equal<string list>(
        [
            "\"Tr0ub4dor&3\""
            "@\"s3cr3t-Value-91\""
            "\"hunter2-Xy\""
            "\"hunter2-Xz\""
            "\"Winter2024!\""
            "\"9f8e7d6c5b4a\""
            "\"Summer2024!\""
            "\"abc123def456\""
            "\"Pa55w0rd!\""
        ],
        firedText source fired
    )

    Assert.True(fired |> List.forall (fun s -> s.Fixes.IsEmpty))
    Assert.Contains("'servicePassword'", fired.Head.Message)
    Assert.DoesNotContain("Tr0ub4dor", fired.Head.Message)
    Assert.DoesNotContain("hunter2", fired.[2].Message)

[<Fact>]
let ``review 2026-10-03b CR0124 reads an attribute's and a static field's credential and a hole-less interpolation; names, messages and templates are quiet``
    ()
    =
    let source =
        csharp
            """
            using System;
            [AttributeUsage(AttributeTargets.All)]
            class CredentialAttribute : Attribute
            {
                public string Password { get; set; } = "";
                public string Name { get; set; } = "";
                public string ErrorMessage { get; set; } = "";
                public CredentialAttribute() { }
                public CredentialAttribute(string name) { }
            }
            class Log { public void Info(string message, params object[] args) { } }
            class C
            {
                [Credential(Password = "Winter2024!")] int a;
                [Credential(Name = "password")] int b;
                [Credential("token")] int c;
                [Credential(ErrorMessage = "Password is required")] int d;
                static readonly string ApiKey = "9f8e7d6c5b4a";
                void A(Log log, bool live, string pwdIn)
                {
                    log.Info("Password {Pwd} rejected", pwdIn);
                    var password = $"Summer2024!";
                    var secret = "Sum" + "mer2024!";
                    var token = live ? "abc123def456" : "zzz999yyy888";
                }
            }
            """

    Assert.Equal<string list>(
        [ "\"Winter2024!\""; "\"9f8e7d6c5b4a\""; "$\"Summer2024!\"" ],
        firedText source (suggestCode "CR0124" source)
    )

[<Fact>]
let ``review 2026-10-03b a report's snippet of a credential finding does not carry the credential`` () =
    let line = "        Login(\"admin\", \"hunter2-Xy\");"

    let text =
        Microsoft.CodeAnalysis.Text.SourceText.From("class C\n{\n" + line + "\n}\n")

    let start = text.ToString().IndexOf "\"hunter2-Xy\""
    let span = Microsoft.CodeAnalysis.Text.TextSpan(start, "\"hunter2-Xy\"".Length)

    for code in [ "CR0123"; "CR0124" ] do
        let _, snippet, _, region =
            CSharp.Refactor.Tool.Reports.fingerprintAndSnippet text "Sample.cs" code span

        Assert.DoesNotContain("hunter2", snippet)
        Assert.DoesNotContain("hunter2", region)
        Assert.Contains("Login(\"admin\", ", snippet)

    // any other rule's snippet is the source as written
    let _, snippet, _, region =
        CSharp.Refactor.Tool.Reports.fingerprintAndSnippet text "Sample.cs" "CR0001" span

    Assert.Contains("hunter2-Xy", snippet)
    Assert.Contains("hunter2-Xy", region)

[<Fact>]
let ``CR0124 leaves a test file's credentials alone`` () =
    let source =
        csharp
            """
            using Xunit;
            public class C
            {
                void Login(string user, string password) { }
                [Fact]
                public void A() { Login("admin", "hunter2-Xy"); var apiKey = "9f8e7d6c5b4a"; }
            }
            """

    Assert.Empty(suggestCode "CR0124" source)

// ---- CR0125 / CR0126 ----

[<Fact>]
let ``weak crypto and certificate bypasses are noted; obsolete constructors become factories unless the name is mentioned elsewhere``
    ()
    =
    let source =
        csharp
            """
            using System;
            using System.Net;
            using System.Net.Http;
            using System.Security.Cryptography;
            class C
            {
                byte[] A(byte[] x) { using var md5 = MD5.Create(); return md5.ComputeHash(x); }
                byte[] B(byte[] x) { using var sha = new SHA256Managed(); return sha.ComputeHash(x); }
                byte[] D(byte[] x) { SHA512Managed sha = new SHA512Managed(); return sha.ComputeHash(x); }
                void E() { var h = new HttpClientHandler(); h.ServerCertificateCustomValidationCallback = (m, c, ch, e) => true; }
                void F() { ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls11; }
                void G() { using var rng = new RNGCryptoServiceProvider(); }
                byte[] H(byte[] x, string kind) => kind switch { "sha1" => SHA1.Create().ComputeHash(x), _ => SHA256.Create().ComputeHash(x) };
            }
            """

    let weak = suggestCode "CR0125" source
    // A (MD5), E (callback), F (Tls11); H's SHA1 has a stronger sibling
    assertFired 3 source weak
    let obsolete = suggestCode "CR0126" source
    // B and G; D mentions SHA512Managed as a declared type
    assertFired 2 source obsolete
    let fixedSource = fixAll "CR0126" source
    Assert.Contains("using var sha = SHA256.Create();", fixedSource)
    Assert.Contains("using var rng = RandomNumberGenerator.Create();", fixedSource)
    Assert.Contains("SHA512Managed sha = new SHA512Managed();", fixedSource)
