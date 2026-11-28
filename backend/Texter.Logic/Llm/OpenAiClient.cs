using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace Texter.Logic.Llm
{
    /// <summary>OpenAI Chat Completions with Structured Outputs (strict JSON schema).</summary>
    public class OpenAiClient : ILlmClient
    {
        private readonly HttpClient _http;
        private readonly LlmOptions _options;

        public OpenAiClient(HttpClient http, IOptions<LlmOptions> options)
        {
            _http = http;
            _options = options.Value;

            if (string.IsNullOrWhiteSpace(_options.ApiKey))
                throw new InvalidOperationException("OpenAI API key is missing. Set 'Llm:ApiKey' (user-secrets or env var Llm__ApiKey).");

            _http.BaseAddress = new Uri(_options.BaseUrl);
            _http.Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        }

        public async Task<string> CompleteJsonAsync(LlmJsonRequest request, CancellationToken cancellationToken = default)
        {
            var body = new JsonObject
            {
                ["model"] = _options.Model,
                ["messages"] = new JsonArray
                {
                    new JsonObject { ["role"] = "system", ["content"] = request.SystemPrompt },
                    new JsonObject { ["role"] = "user", ["content"] = request.UserPrompt }
                },
                ["response_format"] = new JsonObject
                {
                    ["type"] = "json_schema",
                    ["json_schema"] = new JsonObject
                    {
                        ["name"] = request.SchemaName,
                        ["strict"] = true,
                        ["schema"] = JsonNode.Parse(request.Schema)
                    }
                }
            };
            if (!string.IsNullOrWhiteSpace(_options.ReasoningEffort))
                body["reasoning_effort"] = _options.ReasoningEffort;

            HttpResponseMessage posted;
            try
            {
                posted = await _http.PostAsJsonAsync("chat/completions", body, cancellationToken);
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new LlmException($"The model did not answer within {_options.TimeoutSeconds} s. " +
                    "Try a shorter text, or raise Llm:TimeoutSeconds (or lower Llm:ReasoningEffort).");
            }
            using var response = posted;
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new LlmException($"OpenAI returned {(int)response.StatusCode}: {content}");

            using var json = JsonDocument.Parse(content);
            var message = json.RootElement.GetProperty("choices")[0].GetProperty("message");

            if (message.TryGetProperty("refusal", out var refusal) && refusal.ValueKind == JsonValueKind.String)
                throw new LlmException($"Model refused: {refusal.GetString()}");

            return message.GetProperty("content").GetString()
                ?? throw new LlmException("Model returned an empty answer.");
        }
    }
}
