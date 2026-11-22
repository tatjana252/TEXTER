using Texter.Model.Conceptual;

namespace Texter.Logic.Traceability
{
    /// <summary>
    /// Display level of an element: stated -> High (green), inferred -> Medium (yellow), assumed -> Low (red),
    /// then capped by the rules below.
    /// </summary>
    public class ConfidenceOptions
    {
        public const string SectionName = "Confidence";

        /// <summary>An element whose facts lead to no sentence of the text has no traceable reason.</summary>
        public ConfidenceLevel UntraceableCap { get; set; } = ConfidenceLevel.Low;

        /// <summary>A relationship with at least one mapping whose bounds no fact states.</summary>
        public ConfidenceLevel AssumedBoundsCap { get; set; } = ConfidenceLevel.Medium;

        /// <summary>A weak entity or an aggregation whose decision no fact carries (empty weak / aggregation evidence).</summary>
        public ConfidenceLevel UnsupportedDecisionCap { get; set; } = ConfidenceLevel.Low;
    }
}
