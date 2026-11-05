module CSharp.Refactor.Tests.RedirectExposureTests

open Xunit
open CSharp.Refactor
open CSharp.Refactor.Tests.Harness

// ---- CR0198 ----

[<Fact>]
let ``CR0198 tells names that hold secrets from those that don't`` () =
    for name in
        [
            "PLAID-SECRET"
            "X-Auth-Token"
            "x-api-key"
            "X-TM-Key"
            "CMG-AccessToken"
            "Signature"
            "DigitalSignature"
            "AuthenticationPwd"
            "Ocp-Apim-Subscription-Key"
        ] do
        Assert.True(RedirectExposure.holdsSecret name, name)

    for name in
        [
            "Authorization"
            "Idempotency-Key"
            "Stripe-Account"
            "Accept"
            "Plaid-Version"
            "X-Request-Id"
            "Monkey"
        ] do
        Assert.False(RedirectExposure.holdsSecret name, name)

[<Fact>]
let ``CR0198 notes a secret on a request or a client's defaults`` () =
    let source =
        csharp
            """
            using System.Net.Http;
            class C
            {
                void Send(HttpClient client, HttpRequestMessage request, string secret)
                {
                    request.Headers.Add("PLAID-SECRET", secret);
                    client.DefaultRequestHeaders.Add("X-Auth-Token", secret);
                    request.Headers.TryAddWithoutValidation("x-api-key", secret);
                }
            }
            """

    Assert.Equal<string list>(
        [
            "request.Headers.Add(\"PLAID-SECRET\", secret)"
            "client.DefaultRequestHeaders.Add(\"X-Auth-Token\", secret)"
            "request.Headers.TryAddWithoutValidation(\"x-api-key\", secret)"
        ],
        firedText source (suggestCode "CR0198" source)
    )

[<Fact>]
let ``CR0198 reads a constant name, and a name a helper is passed may be a secret`` () =
    let source =
        csharp
            """
            using System.Collections.Generic;
            using System.Net;
            class C
            {
                const string KeyHeader = "X-TM-Key";
                void Post(string url, string key, IEnumerable<(string, string)> headers)
                {
                    var req = WebRequest.CreateHttp(url);
                    req.Headers.Add(KeyHeader, key);
                    foreach (var (h, v) in headers) req.Headers.Add(h, v);
                }
            }
            """

    Assert.Equal<string list>(
        [ "req.Headers.Add(KeyHeader, key)"; "req.Headers.Add(h, v)" ],
        firedText source (suggestCode "CR0198" source)
    )

[<Fact>]
let ``CR0198 leaves Authorization, plain headers and other collections alone`` () =
    let source =
        csharp
            """
            using System.Collections.Generic;
            using System.Net.Http;
            class Message { public Dictionary<string, string> Headers = new(); }
            class C
            {
                void Send(Message m, HttpRequestMessage request, string token)
                {
                    request.Headers.Add("Authorization", token);
                    request.Headers.Add("Idempotency-Key", token);
                    request.Headers.Add("Accept", "application/json");
                    m.Headers.Add("X-Api-Key", token);
                }
            }
            """

    Assert.Empty(suggestCode "CR0198" source)

[<Fact>]
let ``CR0198 leaves a response's headers alone: they go nowhere`` () =
    let source =
        csharp
            """
            using System.Net;
            using System.Net.Http;
            class C
            {
                HttpResponseMessage Answer(string signature)
                {
                    var response = new HttpResponseMessage();
                    response.Headers.Add("DigitalSignature", signature);
                    return response;
                }
                void Listen(HttpListenerResponse listener, string token) => listener.Headers.Add("X-Auth-Token", token);
            }
            """

    Assert.Empty(suggestCode "CR0198" source)

[<Fact>]
let ``CR0198 is quiet in a file that turns redirects off`` () =
    let source =
        csharp
            """
            using System.Net.Http;
            class C
            {
                static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false });
                void Send(string secret) => Client.DefaultRequestHeaders.Add("X-Auth-Token", secret);
            }
            """

    Assert.Empty(suggestCode "CR0198" source)

[<Fact>]
let ``CR0198 reads a local bound once to a literal, and HeaderNames as standard`` () =
    let quiet =
        csharp
            """
            using System.Net.Http;
            static class HeaderNames { public static readonly string Accept = "Accept"; }
            class C
            {
                void Send(HttpRequestMessage r)
                {
                    var name = "Accept";
                    r.Headers.Add(name, "x");
                    r.Headers.Add(HeaderNames.Accept, "x");
                }
            }
            """

    Assert.Empty(suggestCode "CR0198" quiet)

    let secret =
        csharp
            """
            using System.Net.Http;
            class C
            {
                void Send(HttpRequestMessage r)
                {
                    var name = "X-Api-Key";
                    r.Headers.Add(name, "x");
                }
            }
            """

    let found = Assert.Single(suggestCode "CR0198" secret)
    Assert.Contains("X-Api-Key", found.Message)

    let changed =
        csharp
            """
            using System.Net.Http;
            class C
            {
                void Send(HttpRequestMessage r, string other)
                {
                    var name = "Accept";
                    name = other;
                    r.Headers.Add(name, "x");
                }
            }
            """

    Assert.Single(suggestCode "CR0198" changed) |> ignore

[<Fact>]
let ``CR0198 isn't quieted by a mention of AllowAutoRedirect that sets nothing`` () =
    for quiet in
        [
            "// AllowAutoRedirect = false"
            "const string Note = \"AllowAutoRedirect = false\";"
        ] do
        let source =
            csharp
                $"""
                using System.Net.Http;
                class C
                {{
                    {quiet}
                    void Send(HttpRequestMessage r, string secret) => r.Headers.Add("X-Auth-Token", secret);
                }}
                """

        Assert.Single(suggestCode "CR0198" source) |> ignore

// ---- CR0199 ----

[<Fact>]
let ``CR0199 notes every way to make a request that follows redirects`` () =
    let source =
        csharp
            """
            using System.Net;
            using System.Net.Http;
            class C
            {
                HttpClient a = new HttpClient();
                HttpClient b = new();
                HttpClientHandler c = new HttpClientHandler { UseCookies = false };
                SocketsHttpHandler d = new SocketsHttpHandler();
                WebRequest e = WebRequest.Create("https://example.com");
                HttpWebRequest f = WebRequest.CreateHttp("https://example.com");
                WebClient g = new WebClient();
            }
            """

    Assert.Equal<string list>(
        [
            "new HttpClient()"
            "new()"
            "new HttpClientHandler { UseCookies = false }"
            "new SocketsHttpHandler()"
            "WebRequest.Create(\"https://example.com\")"
            "WebRequest.CreateHttp(\"https://example.com\")"
            "new WebClient()"
        ],
        firedText source (suggestCode "CR0199" source)
    )

[<Fact>]
let ``CR0199 notes AddHttpClient, the usual way C# builds a client`` () =
    let source =
        csharp
            """
            namespace Microsoft.Extensions.DependencyInjection
            {
                public interface IServiceCollection { }
                public static class HttpClientFactoryServiceCollectionExtensions
                {
                    public static IServiceCollection AddHttpClient(this IServiceCollection services, string name) => services;
                }
            }
            namespace App
            {
                using Microsoft.Extensions.DependencyInjection;
                class Startup
                {
                    void Configure(IServiceCollection services) => services.AddHttpClient("plaid");
                }
            }
            """

    Assert.Equal<string list>([ "services.AddHttpClient(\"plaid\")" ], firedText source (suggestCode "CR0199" source))

[<Fact>]
let ``CR0199 leaves a client over a given handler to that handler's own note`` () =
    let source =
        csharp
            """
            using System.Net.Http;
            class C
            {
                HttpClient Make(HttpMessageHandler handler) => new HttpClient(handler);
            }
            """

    Assert.Empty(suggestCode "CR0199" source)

[<Fact>]
let ``CR0199 is off until asked for, and CR0198 is on`` () =
    Assert.False(RuleCatalog.isDefaultOn "CR0199")
    Assert.True(RuleCatalog.isDefaultOn "CR0198")
