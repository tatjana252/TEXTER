namespace Texter.Logic.Traceability
{
    public class ConfidenceOptions
    {
        public const string SectionName = "Confidence";

        /// <summary>Confidence at or above this is High (green).</summary>
        public double HighThreshold { get; set; } = 0.8;

        /// <summary>Confidence at or above this is Medium (yellow); below is Low (red).</summary>
        public double MediumThreshold { get; set; } = 0.5;

        /// <summary>
        /// An element whose evidence cannot be found in the text has no traceable reason,
        /// so its level is capped at this value.
        /// </summary>
        public Model.Conceptual.ConfidenceLevel UntraceableCap { get; set; } = Model.Conceptual.ConfidenceLevel.Low;
    }
}
