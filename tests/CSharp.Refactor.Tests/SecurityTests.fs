module CSharp.Refactor.Tests.SecurityTests

open Xunit
open CSharp.Refactor.Tests.Harness

let private dbShim =
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
let ``a hole filled from a constant is not a value; a var local or a parameter is`` () =
    let source =
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

// ---- CR0125 / CR0126 ----

[<Fact>]
let ``weak crypto and certificate bypasses are noted; obsolete constructors become factories unless the name is mentioned elsewhere``
    ()
    =
    let source =
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
    Assert.Equal(3, weak.Length)
    let obsolete = suggestCode "CR0126" source
    // B and G; D mentions SHA512Managed as a declared type
    Assert.Equal(2, obsolete.Length)
    let fixedSource = fixAll "CR0126" source
    Assert.Contains("using var sha = SHA256.Create();", fixedSource)
    Assert.Contains("using var rng = RandomNumberGenerator.Create();", fixedSource)
    Assert.Contains("SHA512Managed sha = new SHA512Managed();", fixedSource)
