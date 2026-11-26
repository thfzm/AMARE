using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Amare.Windows.Core;

namespace Amare.Interop.Tests;

internal static class Phase32Tests
{
    public static void Register()
    {
        Program.Run("ai_responses_request_and_parse", ResponsesRequestAndParse);
        Program.Run("ai_error_mapping_and_timeout", ErrorMappingAndTimeout);
        Program.Run("ai_streaming_and_cancel", StreamingAndCancel);
        Program.Run("ai_validation_and_endpoints", ValidationAndEndpoints);
        Program.Run("ai_mock_mode_no_network", MockModeNoNetwork);
        Program.Run("ai_page_context_consent", PageContextConsent);
        Program.Run("ai_redaction_and_settings", RedactionAndSettings);
    }

    private static string ResponsesRequestAndParse()
    {
        var settings = Settings();
        using var handler = new CaptureHandler((_, _) => Task.FromResult(Json(HttpStatusCode.OK, """
            {"model":"gpt-6-luna",
             "output":[
               {"type":"reasoning","id":"r1"},
               {"type":"message","role":"assistant","content":[
                 {"type":"output_text","text":"Hello"},
                 {"type":"output_text","text":" world"}
               ]}
             ],
             "usage":{"input_tokens":5,"output_tokens":2,"total_tokens":7}}
            """)));
        var service = AiAssistantService.Create(settings, "sk-test-key", handler);
        var response = service.CompleteAsync(Request("hi")).GetAwaiter().GetResult();
        Require(response.Text == "Hello world", response.Text);
        Require(response.Usage is { InputTokens: 5, OutputTokens: 2, TotalTokens: 7 },
            "usage parse");
        Require(handler.Requests.Count == 1, "request count");
        var request = handler.Requests[0];
        Require(request.Url.AbsoluteUri == "https://api.openai.com/v1/responses", request.Url.AbsoluteUri);
        Require(request.Authorization == "Bearer sk-test-key", "auth header");
        Require(request.Body.Contains("\"model\":\"gpt-6-luna\""), "model missing");
        Require(request.Body.Contains("\"store\":false"), "store must be false");
        Require(request.Body.Contains("\"stream\":false"), "stream flag");
        Require(request.Body.Contains("\"instructions\""), "instructions missing");
        Require(!request.Body.Contains("sk-test-key", StringComparison.Ordinal), "key in request body");
        return "Responses request shape, Bearer header, output aggregation and usage";
    }

    private static string ErrorMappingAndTimeout()
    {
        var settings = Settings();
        using (var handler = new CaptureHandler((_, _) => Task.FromResult(Json(
                   HttpStatusCode.Unauthorized,
                   """{"error":{"message":"invalid key sk-test-key","code":"invalid_api_key"}}"""))))
        {
            var service = AiAssistantService.Create(settings, "sk-test-key", handler);
            var error = Throws<AiException>(() =>
                service.CompleteAsync(Request("hi")).GetAwaiter().GetResult());
            Require(error.Kind == AiErrorKind.InvalidKey, error.Kind.ToString());
            Require(!error.Message.Contains("sk-test-key", StringComparison.Ordinal), "key leaked");
        }

        using (var handler = new CaptureHandler((_, _) =>
               {
                   var response = Json(HttpStatusCode.TooManyRequests,
                       """{"error":{"message":"slow down","code":"rate_limit_exceeded"}}""");
                   response.Headers.Add("retry-after", "12");
                   response.Headers.Add("x-request-id", "req_123");
                   return Task.FromResult(response);
               }))
        {
            var service = AiAssistantService.Create(settings, "sk-test-key", handler);
            var error = Throws<AiException>(() =>
                service.CompleteAsync(Request("hi")).GetAwaiter().GetResult());
            Require(error.Kind == AiErrorKind.RateLimit, error.Kind.ToString());
            Require(error.RetryAfter?.TotalSeconds == 12, "retry-after");
            Require(error.RequestId == "req_123", "request id");
        }

        using (var handler = new CaptureHandler((_, _) => Task.FromResult(Json(
                   (HttpStatusCode)429,
                   """{"error":{"message":"quota","code":"insufficient_quota"}}"""))))
        {
            var service = AiAssistantService.Create(settings, "sk-test-key", handler);
            var error = Throws<AiException>(() =>
                service.CompleteAsync(Request("hi")).GetAwaiter().GetResult());
            Require(error.Kind == AiErrorKind.Quota, error.Kind.ToString());
        }

        using (var handler = new CaptureHandler((_, _) => Task.FromResult(Json(
                   (HttpStatusCode)429,
                   """{"error":{"message":"credits","code":"credit_balance_exhausted"}}"""))))
        {
            var service = AiAssistantService.Create(settings, "sk-test-key", handler);
            Require(Throws<AiException>(() =>
                service.CompleteAsync(Request("hi")).GetAwaiter().GetResult()).Kind
                == AiErrorKind.Quota, "credit quota mapping");
        }

        using (var handler = new CaptureHandler((_, _) =>
               {
                   var response = Json(HttpStatusCode.ServiceUnavailable,
                       """{"error":{"message":"overloaded","code":"server_is_overloaded"}}""");
                   response.Headers.Add("retry-after", "5");
                   return Task.FromResult(response);
               }))
        {
            var service = AiAssistantService.Create(settings, "sk-test-key", handler);
            var error = Throws<AiException>(() =>
                service.CompleteAsync(Request("hi")).GetAwaiter().GetResult());
            Require(error.Kind == AiErrorKind.Server && error.RetryAfter?.TotalSeconds == 5,
                "503 overload mapping");
        }

        using (var handler = new CaptureHandler((_, _) =>
               Task.FromResult(Json(HttpStatusCode.InternalServerError, """{"error":"server"}"""))))
        {
            var service = AiAssistantService.Create(settings, "sk-test-key", handler);
            Require(Throws<AiException>(() =>
                service.CompleteAsync(Request("hi")).GetAwaiter().GetResult()).Kind
                == AiErrorKind.Server, "5xx mapping");
        }

        using (var handler = new CaptureHandler((_, _) =>
               Task.FromResult(Json(HttpStatusCode.NotFound, """{"error":{"message":"model"}}"""))))
        {
            var service = AiAssistantService.Create(settings, "sk-test-key", handler);
            Require(Throws<AiException>(() =>
                service.CompleteAsync(Request("hi")).GetAwaiter().GetResult()).Kind
                == AiErrorKind.InvalidConfiguration, "404 mapping");
        }

        using (var handler = new CaptureHandler((_, _) =>
               Task.FromResult(Json(HttpStatusCode.OK, """{"unexpected":true}"""))))
        {
            var service = AiAssistantService.Create(settings, "sk-test-key", handler);
            Require(Throws<AiException>(() =>
                service.CompleteAsync(Request("hi")).GetAwaiter().GetResult()).Kind
                == AiErrorKind.InvalidResponse, "malformed mapping");
        }

        using (var handler = new CaptureHandler((_, _) =>
               throw new HttpRequestException("offline")))
        {
            var service = AiAssistantService.Create(settings, "sk-test-key", handler);
            Require(Throws<AiException>(() =>
                service.CompleteAsync(Request("hi")).GetAwaiter().GetResult()).Kind
                == AiErrorKind.Network, "network mapping");
        }

        using (var handler = new CaptureHandler(async (_, token) =>
               {
                   await Task.Delay(200, token);
                   return Json(HttpStatusCode.OK, """{"output":[]}""");
               }))
        {
            var service = AiAssistantService.Create(
                settings, "sk-test-key", handler, TimeSpan.FromMilliseconds(30));
            Require(Throws<AiException>(() =>
                service.CompleteAsync(Request("hi")).GetAwaiter().GetResult()).Kind
                == AiErrorKind.Timeout, "timeout mapping");
        }

        using (var handler = new CaptureHandler((_, _) =>
               Task.FromResult(Json(HttpStatusCode.OK, """{"output":[]}"""))))
        {
            var service = AiAssistantService.Create(settings, "", handler);
            Require(Throws<AiException>(() =>
                service.CompleteAsync(Request("hi")).GetAwaiter().GetResult()).Kind
                == AiErrorKind.MissingKey, "missing key");
            Require(handler.Requests.Count == 0, "missing key reached network");
        }

        using (var handler = new CaptureHandler((_, _) =>
               Task.FromResult(Json(HttpStatusCode.OK, """{"output":[]}"""))))
        {
            var service = AiAssistantService.Create(settings with { Enabled = false }, "sk-test-key", handler);
            Require(Throws<AiException>(() =>
                service.CompleteAsync(Request("hi")).GetAwaiter().GetResult()).Kind
                == AiErrorKind.Disabled, "disabled");
        }
        return "401/429/quota/5xx/404/malformed/network/timeout/missing/disabled mapping";
    }

    private static string StreamingAndCancel()
    {
        var settings = Settings();
        using var handler = new CaptureHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "event: response.created\n" +
                "data: {\"type\":\"response.created\"}\n\n" +
                "event: response.output_text.delta\n" +
                "data: {\"type\":\"response.output_text.delta\",\"delta\":\"Hel\"}\n\n" +
                "data: {\"type\":\"response.output_text.delta\",\"delta\":\"lo\"}\n\n" +
                "data: {\"type\":\"response.completed\",\"response\":{\"usage\":{\"input_tokens\":1,\"output_tokens\":2,\"total_tokens\":3}}}\n\n",
                Encoding.UTF8,
                "text/event-stream")
        }));
        var service = AiAssistantService.Create(settings, "sk-test-key", handler);
        var (text, usage) = Collect(service.StreamAsync(Request("hi")));
        Require(text == "Hello", text);
        Require(usage?.TotalTokens == 3, "stream usage");

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var canceledService = AiAssistantService.Create(settings, "sk-test-key", handler);
        Require(Throws<AiException>(() =>
        {
            canceledService.StreamAsync(Request("hi"), canceled.Token)
                .GetAsyncEnumerator().MoveNextAsync().AsTask().GetAwaiter().GetResult();
        }).Kind == AiErrorKind.Canceled, "cancel mapping");
        return "Responses SSE deltas, completed usage and cancellation";
    }

    private static string ValidationAndEndpoints()
    {
        var settings = Settings();
        using (var handler = new CaptureHandler((_, _) =>
               Task.FromResult(Json(HttpStatusCode.OK, "{}"))))
        {
            var service = AiAssistantService.Create(settings, "sk-test-key", handler);
            service.ValidateAsync().GetAwaiter().GetResult();
            Require(handler.Requests.Count == 1, "validation request");
            Require(handler.Requests[0].Url.AbsoluteUri
                    == "https://api.openai.com/v1/models/gpt-6-luna",
                handler.Requests[0].Url.AbsoluteUri);
        }

        using (var handler = new CaptureHandler((_, _) => Task.FromResult(Json(
                   HttpStatusCode.Unauthorized, """{"error":{"message":"bad"}}"""))))
        {
            var service = AiAssistantService.Create(settings, "sk-test-key", handler);
            Require(Throws<AiException>(() => service.ValidateAsync().GetAwaiter().GetResult()).Kind
                == AiErrorKind.InvalidKey, "validation auth");
        }

        using (var noHandler = new CaptureHandler((request, _) =>
               Task.FromResult(request.RequestUri!.AbsolutePath.Contains("/models/", StringComparison.Ordinal)
                   ? Json(HttpStatusCode.OK, "{}")
                   : Json(HttpStatusCode.OK,
                       """{"choices":[{"message":{"content":"compatible"}}]}"""))))
        {
            var compatible = Settings() with
            {
                Provider = AiProviderCatalog.CompatibleId,
                BaseUrl = "https://compatible.example/v1"
            };
            var service = AiAssistantService.Create(compatible, "sk-test-key", noHandler);
            var answer = service.CompleteAsync(Request("hi")).GetAwaiter().GetResult();
            Require(answer.Text == "compatible", answer.Text);
            var chatRequest = noHandler.Requests.Last();
            Require(chatRequest.Url.AbsoluteUri
                    == "https://compatible.example/v1/chat/completions",
                chatRequest.Url.AbsoluteUri);
        }

        var http = Settings() with { BaseUrl = "http://api.openai.com/v1" };
        using var handler2 = new CaptureHandler((_, _) =>
            Task.FromResult(Json(HttpStatusCode.OK, "{}")));
        var httpService = AiAssistantService.Create(http, "sk-test-key", handler2);
        Require(Throws<AiException>(() =>
            httpService.CompleteAsync(Request("hi")).GetAwaiter().GetResult()).Kind
            == AiErrorKind.InvalidConfiguration, "http endpoint must be rejected");
        return "validation endpoint, compatible endpoint and HTTPS-only";
    }

    private static string MockModeNoNetwork()
    {
        var settings = Settings() with { Mode = AiExecutionMode.Mock };
        using var handler = new CaptureHandler((_, _) =>
            throw new InvalidOperationException("mock mode must not use network"));
        var service = AiAssistantService.Create(settings, null, handler);
        var response = service.CompleteAsync(Request("explain", new AiPageContext(
            "Mock page", "https://example.com", "text"))).GetAwaiter().GetResult();
        Require(response.Text.Contains("Mock AI response", StringComparison.Ordinal), response.Text);
        Require(response.Text.Contains("Mock page", StringComparison.Ordinal), "mock context");
        var (streamed, _) = Collect(service.StreamAsync(Request("stream")));
        Require(streamed.Contains("Mock AI response", StringComparison.Ordinal), "mock stream");
        service.ValidateAsync().GetAwaiter().GetResult();
        Require(handler.Requests.Count == 0, "mock mode used network");
        return "mock mode is offline and deterministic";
    }

    private static string PageContextConsent()
    {
        var settings = Settings();
        using var withContext = new CaptureHandler((_, _) =>
            Task.FromResult(Json(HttpStatusCode.OK, """{"output":[{"type":"message","content":[{"type":"output_text","text":"ok"}]}]}""")));
        var service = AiAssistantService.Create(settings, "sk-test-key", withContext);
        service.CompleteAsync(Request("summarize", new AiPageContext(
            "Page title", "https://example.com/page", "Page body text"))).GetAwaiter().GetResult();
        var body = withContext.Requests[0].Body;
        Require(body.Contains("untrusted", StringComparison.OrdinalIgnoreCase), "untrusted label");
        Require(body.Contains("Page title", StringComparison.Ordinal), "title missing");
        Require(body.Contains("Page body text", StringComparison.Ordinal), "body missing");
        var bodyIndex = body.IndexOf("Page body text", StringComparison.Ordinal);
        var promptIndex = body.LastIndexOf("summarize", StringComparison.Ordinal);
        Require(bodyIndex >= 0 && promptIndex > bodyIndex, "context must precede current prompt");

        using var withoutContext = new CaptureHandler((_, _) =>
            Task.FromResult(Json(HttpStatusCode.OK, """{"output":[{"type":"message","content":[{"type":"output_text","text":"ok"}]}]}""")));
        var noContextService = AiAssistantService.Create(settings, "sk-test-key", withoutContext);
        noContextService.CompleteAsync(Request("hello")).GetAwaiter().GetResult();
        Require(!withoutContext.Requests[0].Body.Contains("Page body text", StringComparison.Ordinal),
            "page context sent without consent");
        return "page context is explicit and labeled untrusted";
    }

    private static string RedactionAndSettings()
    {
        var settings = new AiApiSettings(
            "gpt-6-luna", "https://api.openai.com/v1", AiProviderCatalog.OpenAiId,
            Enabled: false, Mode: AiExecutionMode.Mock);
        var roundtrip = JsonSerializer.Deserialize<AiApiSettings>(
            JsonSerializer.Serialize(settings));
        Require(roundtrip?.Enabled == false && roundtrip.Mode == AiExecutionMode.Mock,
            "settings roundtrip");
        Require(AiApiClient.DefaultModel == "gpt-6-luna", "default model");
        var oldJson = JsonSerializer.Deserialize<AiApiSettings>(
            """{"Model":"gpt-test","BaseUrl":"https://api.example/v1","Provider":"openai"}""");
        Require(oldJson is { Enabled: true, Mode: AiExecutionMode.Actual },
            "old settings defaults");
        Require(AiProviderCatalog.Resolve("openai").Protocol == AiProtocol.Responses,
            "OpenAI Responses protocol");
        Require(AiProviderCatalog.Resolve("openai-compatible").Protocol == AiProtocol.ChatCompletions,
            "compatible Chat protocol");
        var redacted = AiRedaction.Redact("Authorization: Bearer sk-secret-key", "sk-secret-key");
        Require(!redacted.Contains("sk-secret-key", StringComparison.Ordinal), redacted);
        Require(redacted.Contains("[redacted]", StringComparison.Ordinal), redacted);
        Require(!new AiException(AiErrorKind.InvalidKey, "bad").UserMessage
            .Contains("sk-", StringComparison.Ordinal), "user message");
        return "settings roundtrip, default model and redaction";
    }

    private static AiApiSettings Settings() => new(
        "gpt-6-luna", "https://api.openai.com/v1", AiProviderCatalog.OpenAiId,
        Enabled: true, Mode: AiExecutionMode.Actual);

    private static AiRequest Request(string prompt, AiPageContext? context = null) =>
        new([AiMessage.User(prompt)], context);

    private static (string Text, AiUsage? Usage) Collect(IAsyncEnumerable<AiStreamChunk> stream)
    {
        var builder = new StringBuilder();
        AiUsage? usage = null;
        var enumerator = stream.GetAsyncEnumerator();
        try
        {
            while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
            {
                builder.Append(enumerator.Current.Text);
                if (enumerator.Current.Done)
                {
                    usage = enumerator.Current.Usage;
                }
            }
        }
        finally
        {
            enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        return (builder.ToString(), usage);
    }

    private static T Throws<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (T exception)
        {
            return exception;
        }
        throw new InvalidOperationException($"expected {typeof(T).Name}");
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class CaptureHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new CapturedRequest(
                request.RequestUri!,
                request.Headers.Authorization?.ToString() ?? "",
                body));
            return await responder(request, cancellationToken);
        }
    }

    private sealed record CapturedRequest(Uri Url, string Authorization, string Body);
}

