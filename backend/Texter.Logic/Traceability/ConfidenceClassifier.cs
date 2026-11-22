using Microsoft.Extensions.Options;
using Texter.Model.Conceptual;

namespace Texter.Logic.Traceability
{
    public class ConfidenceClassifier
    {
        private readonly ConfidenceOptions _options;

        public ConfidenceClassifier(IOptions<ConfidenceOptions> options) => _options = options.Value;

        public ConfidenceLevel Classify(ConfidenceCategory confidence, bool isTraceable, bool hasAssumedBounds = false, bool hasUnsupportedDecision = false)
        {
            var level = confidence switch
            {
                ConfidenceCategory.Stated => ConfidenceLevel.High,
                ConfidenceCategory.Inferred => ConfidenceLevel.Medium,
                _ => ConfidenceLevel.Low
            };

            if (!isTraceable) level = Min(level, _options.UntraceableCap);
            if (hasAssumedBounds) level = Min(level, _options.AssumedBoundsCap);
            if (hasUnsupportedDecision) level = Min(level, _options.UnsupportedDecisionCap);
            return level;
        }

        private static ConfidenceLevel Min(ConfidenceLevel a, ConfidenceLevel b) => a < b ? a : b;
    }
}
