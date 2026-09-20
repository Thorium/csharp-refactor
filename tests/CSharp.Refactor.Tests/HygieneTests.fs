module CSharp.Refactor.Tests.HygieneTests

open Xunit
open CSharp.Refactor.Tests.Harness

// ---- CR0112 ----

[<Fact>]
let ``a hidden character in a literal is escaped, in a comment or identifier noted, a joiner in text exempt`` () =
    let source =
        "\nclass C\n{\n    string A() => \"ab\u200Bc\";\n    // a comment with \u202E reversed text\n    string B() => \"family: ‍\";\n    string D() => @\"x\u200By\";\n}\n"

    let fired = suggestCode "CR0112" source
    Assert.Equal(3, fired.Length)
    Assert.Equal(2, fired |> List.filter (fun s -> s.Fixes.IsEmpty) |> List.length)
    let fixedSource = fixAll "CR0112" source
    Assert.Contains("\"ab\\u200Bc\"", fixedSource)
    Assert.Contains("@\"x\u200By\"", fixedSource)

// ---- CR0113 ----

[<Fact>]
let ``arithmetic near the ceiling and on the bound is noted with a checked offer; small literals and checked contexts are quiet``
    ()
    =
    let source =
        """
class C
{
    int A(int balance) => balance + 2_000_000_000;
    long B(int seconds) => seconds * 1_000_000;
    int D(int x) => int.MaxValue + x;
    int E(int x) => int.MaxValue - x;
    int F(int x) => x + 100;
    int G(int x) => checked(x + 2_000_000_000);
    long H(long x) => x + 9_000_000_000_000_000_000L;
    long I(long x) => x + 10_000_000_000L;
}
"""

    let fired = suggestCode "CR0113" source

    Assert.Equal<string list>(
        [
            "balance + 2_000_000_000"
            "int.MaxValue + x"
            "x + 9_000_000_000_000_000_000L"
        ],
        firedText source fired
    )

    Assert.True(fired |> List.forall (fun s -> s.Fixes |> List.forall (fun f -> f.EditorOnly)))

// ---- CR0114 / CR0115 ----

let private loggerShim =
    """
namespace Microsoft.Extensions.Logging
{
    public interface ILogger { }
    public struct EventId { public EventId(int id) { } }
    public static class LoggerExtensions
    {
        public static void LogError(this ILogger l, string message, params object[] args) { }
        public static void LogError(this ILogger l, System.Exception exception, string message, params object[] args) { }
        public static void LogError(this ILogger l, EventId eventId, string message, params object[] args) { }
        public static void LogInformation(this ILogger l, string message, params object[] args) { }
    }
}
"""

[<Fact>]
let ``log templates that do not fit their arguments are noted`` () =
    let source =
        """
using Microsoft.Extensions.Logging;
class C
{
    readonly ILogger log;
    void A(int id) => log.LogInformation("{Id} then {Id}", id, id);
    void B(int id) => log.LogInformation("{Id} and {Name}", id);
    void D(int id) => log.LogInformation("plain", id);
    void E(int id) => log.LogInformation($"id {id}");
    void F(int id, string name) => log.LogInformation("{Id} {Name}", id, name);
    void G(object[] args) => log.LogInformation("{A} {B}", args);
    void H(int id) => log.LogInformation("{{literal}} {Id}", id);
    void I() => log.LogInformation($"constant");
}
"""
        + loggerShim

    let fired = suggestCode "CR0114" source
    // A (duplicate), B (arity), D (arity), E (interpolation)
    Assert.Equal(4, fired.Length)

[<Fact>]
let ``a log line inside a catch that drops the exception gains it first; a mention or an EventId first is not rewritten``
    ()
    =
    let source =
        """
using System;
using Microsoft.Extensions.Logging;
class C
{
    readonly ILogger log;
    void A(int id) { try { } catch (Exception ex) { log.LogError("sync failed {Id}", id); } }
    void B(int id) { try { } catch (Exception ex) { log.LogError("sync failed {Id}: {Msg}", id, ex.Message); } }
    void D(int id) { try { } catch (Exception ex) { log.LogError(new EventId(3), "sync failed {Id}", id); } }
    void E(int id) { try { } catch (Exception ex) { log.LogError(ex, "sync failed {Id}", id); } }
    void F(int id) { try { } catch (Exception) { log.LogError("sync failed {Id}", id); } }
}
"""
        + loggerShim

    let fired = suggestCode "CR0115" source
    Assert.Equal(2, fired.Length)
    Assert.Equal(1, fired |> List.filter (fun s -> not s.Fixes.IsEmpty) |> List.length)
    Assert.Contains("log.LogError(ex, \"sync failed {Id}\", id);", fixAll "CR0115" source)
