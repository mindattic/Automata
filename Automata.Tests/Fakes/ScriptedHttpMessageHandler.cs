using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Automata.Tests.Fakes;

/// <summary>
/// Stub <see cref="HttpMessageHandler"/> for provider adapter tests: records every request body
/// (parsed as JSON) and returns a scripted sequence of responses, one per call — the last entry
/// repeats for any call beyond the scripted count. A <c>retryAfterSeconds</c> of 0 on a scripted
/// response lets retry-path tests run without the adapter's real backoff delay actually elapsing.
/// </summary>
public sealed class ScriptedHttpMessageHandler : HttpMessageHandler
{
    private readonly List<(HttpStatusCode Status, string Body, int? RetryAfterSeconds)> responses = new();
    private readonly List<JsonNode?> requestBodies = new();
    private readonly List<HttpRequestMessage> requests = new();
    private int callCount;

    public IReadOnlyList<JsonNode?> RequestBodies => requestBodies;
    public IReadOnlyList<HttpRequestMessage> Requests => requests;
    public int CallCount => callCount;

    public ScriptedHttpMessageHandler Respond(HttpStatusCode status, string body, int? retryAfterSeconds = null)
    {
        responses.Add((status, body, retryAfterSeconds));
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        requests.Add(request);
        var bodyText = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        requestBodies.Add(bodyText is null ? null : JsonNode.Parse(bodyText));

        var index = Math.Min(callCount, responses.Count - 1);
        var (status, respBody, retryAfter) = responses[index];
        callCount++;

        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(respBody, Encoding.UTF8, "application/json"),
        };
        if (retryAfter is { } seconds)
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
        return response;
    }
}
