namespace Texter.Logic.Llm
{
    public class LlmOptions
    {
        public const string SectionName = "Llm";

        /// <summary>"OpenAI" or "Fake" (canned answers, useful without an API key).</summary>
        public string Provider { get; set; } = "OpenAI";

        public string Model { get; set; } = "gpt-5.4-mini";
        public string ApiKey { get; set; } = string.Empty;
        public string BaseUrl { get; set; } = "https://api.openai.com/v1/";

        /// <summary>Optional: minimal / low / medium / high. Empty = provider default.</summary>
        public string? ReasoningEffort { get; set; }

        public int TimeoutSeconds { get; set; } = 480;

        /// <summary>Folder with canned answers for the Fake provider, one file per schema: {SchemaName}.json.</summary>
        public string FakeResponsesDir { get; set; } = "Samples/fake";
    }
}
