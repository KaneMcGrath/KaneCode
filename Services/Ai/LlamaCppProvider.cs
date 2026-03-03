using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace KaneCode.Services.Ai;

/// <summary>
/// AI provider that talks to a local Llama.cpp HTTP server using the
/// OpenAI-compatible <c>/v1/chat/completions</c> endpoint with SSE streaming.
/// </summary>
internal sealed class LlamaCppProvider : IAiProvider, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromMinutes(5) };
    private readonly AiProviderSettings _settings;

    public LlamaCppProvider(AiProviderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
    }

    public string DisplayName => "Llama.cpp (local)";

    public string ProviderId => "llamacpp";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_settings.Endpoint);

    public IReadOnlyList<string> AvailableModels =>
    [
        "default"
    ];

    /// <inheritdoc />
    public async IAsyncEnumerable<string> StreamCompletionAsync(
        IReadOnlyList<AiChatMessage> messages,
        string model,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var endpoint = _settings.Endpoint.TrimEnd('/');
        var url = $"{endpoint}/v1/chat/completions";

        var requestBody = new
        {
            model = string.IsNullOrWhiteSpace(model) ? "default" : model,
            messages = messages.Select(m => new
            {
                role = m.Role switch
                {
                    AiChatRole.System => "system",
                    AiChatRole.User => "user",
                    AiChatRole.Assistant => "assistant",
                    _ => "user"
                },
                content = m.Content
            }),
            stream = true
        };

        var json = JsonSerializer.Serialize(requestBody, JsonOptions);
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (!reader.EndOfStream)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrEmpty(line))
            {
                continue;
            }

            if (!line.StartsWith("data: ", StringComparison.Ordinal))
            {
                continue;
            }

            var data = line["data: ".Length..];

            if (data is "[DONE]")
            {
                yield break;
            }

            var token = ExtractContentToken(data);
            if (token is not null)
            {
                yield return token;
            }
        }
    }

    /// <summary>
    /// Extracts the content delta from an SSE chat completion chunk.
    /// Expected shape: <c>{ "choices": [{ "delta": { "content": "..." } }] }</c>
    /// </summary>
    private static string? ExtractContentToken(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("choices", out var choices) &&
                choices.GetArrayLength() > 0)
            {
                var firstChoice = choices[0];
                if (firstChoice.TryGetProperty("delta", out var delta) &&
                    delta.TryGetProperty("content", out var content))
                {
                    return content.GetString();
                }
            }
        }
        catch (JsonException)
        {
            // Malformed chunk — skip
        }

        return null;
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
