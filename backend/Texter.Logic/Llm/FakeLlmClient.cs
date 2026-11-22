using Microsoft.Extensions.Options;

namespace Texter.Logic.Llm
{
    /// <summary>Returns canned answers ({FakeResponsesDir}/{SchemaName}.json). For UI development and tests.</summary>
    public class FakeLlmClient : ILlmClient
    {
        private readonly LlmOptions _options;

        public FakeLlmClient(IOptions<LlmOptions> options) => _options = options.Value;

        public Task<string> CompleteJsonAsync(LlmJsonRequest request, CancellationToken cancellationToken = default)
        {
            var dir = Path.IsPathRooted(_options.FakeResponsesDir)
                ? _options.FakeResponsesDir
                : Path.Combine(AppContext.BaseDirectory, _options.FakeResponsesDir);
            return File.ReadAllTextAsync(Path.Combine(dir, request.SchemaName + ".json"), cancellationToken);
        }
    }
}
