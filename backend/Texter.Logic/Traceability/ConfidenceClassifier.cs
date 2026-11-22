using Microsoft.Extensions.Options;
using Texter.Model.Conceptual;

namespace Texter.Logic.Traceability
{
    public class ConfidenceClassifier
    {
        private readonly ConfidenceOptions _options;

        public ConfidenceClassifier(IOptions<ConfidenceOptions> options) => _options = options.Value;

        public ConfidenceLevel Classify(double confidence, bool isTraceable)
        {
            var level = confidence >= _options.HighThreshold ? ConfidenceLevel.High
                : confidence >= _options.MediumThreshold ? ConfidenceLevel.Medium
                : ConfidenceLevel.Low;

            return isTraceable || level <= _options.UntraceableCap ? level : _options.UntraceableCap;
        }
    }
}
