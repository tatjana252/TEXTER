using Microsoft.Extensions.Options;

namespace Texter.Logic.Llm
{
    /// <summary>Returns a canned answer from a file. For UI development and tests without an API key.</summary>
    public class FakeLlmClient : ILlmClient
    {
        private readonly LlmOptions _options;

        public FakeLlmClient(IOptions<LlmOptions> options) => _options = options.Value;

        public Task<string> CompleteJsonAsync(LlmJsonRequest request, CancellationToken cancellationToken = default)
        {
            var path = Path.IsPathRooted(_options.FakeResponsePath)
                ? _options.FakeResponsePath
                : Path.Combine(AppContext.BaseDirectory, _options.FakeResponsePath);
            return File.ReadAllTextAsync(path, cancellationToken);
        }
    }
}
