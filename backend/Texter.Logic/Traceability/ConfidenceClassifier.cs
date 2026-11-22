using Microsoft.Extensions.Options;
using Texter.Model.Conceptual;

namespace Texter.Logic.Traceability
{
    public class ConfidenceClassifier
    {
        private readonly ConfidenceOptions _options;

        public ConfidenceClassifier(IOptions<ConfidenceOptions> options) => _options = options.Value;

        public ConfidenceLevel Classify(double confidence, bool isTraceable, bool hasAssumedBounds = false)
        {
            var level = confidence >= _options.HighThreshold ? ConfidenceLevel.High
                : confidence >= _options.MediumThreshold ? ConfidenceLevel.Medium
                : ConfidenceLevel.Low;

            if (!isTraceable) level = Min(level, _options.UntraceableCap);
            if (hasAssumedBounds) level = Min(level, _options.AssumedBoundsCap);
            return level;
        }

        private static ConfidenceLevel Min(ConfidenceLevel a, ConfidenceLevel b) => a < b ? a : b;
    }
}
