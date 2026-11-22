namespace Texter.Logic.Llm
{
    /// <summary>Minimal LLM abstraction: one system + user prompt, answer constrained by a JSON schema.</summary>
    public interface ILlmClient
    {
        Task<string> CompleteJsonAsync(LlmJsonRequest request, CancellationToken cancellationToken = default);
    }

    /// <param name="SchemaName">Name of the structured output schema (OpenAI requires one).</param>
    /// <param name="Schema">JSON schema the answer must conform to.</param>
    public record LlmJsonRequest(string SystemPrompt, string UserPrompt, string SchemaName, string Schema);
}
