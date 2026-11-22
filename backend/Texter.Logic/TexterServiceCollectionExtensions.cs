using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Texter.Logic.Extraction;
using Texter.Logic.Llm;
using Texter.Logic.Traceability;

namespace Texter.Logic
{
    public static class TexterServiceCollectionExtensions
    {
        public static IServiceCollection AddTexter(this IServiceCollection services, IConfiguration configuration)
        {
            services.Configure<LlmOptions>(configuration.GetSection(LlmOptions.SectionName));
            services.Configure<ConfidenceOptions>(configuration.GetSection(ConfidenceOptions.SectionName));

            var provider = configuration.GetSection(LlmOptions.SectionName)[nameof(LlmOptions.Provider)];
            if (string.Equals(provider, "Fake", StringComparison.OrdinalIgnoreCase))
                services.AddSingleton<ILlmClient, FakeLlmClient>();
            else
                services.AddHttpClient<ILlmClient, OpenAiClient>();

            services.AddSingleton<ConfidenceClassifier>();

            services.AddScoped<IConceptualModelExtractor, LlmConceptualModelExtractor>();
            return services;
        }
    }
}
