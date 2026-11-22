namespace Texter.Model.Conceptual
{
    /// <summary>
    /// Base for every element of the conceptual model. Carries the TEXTER attribution:
    /// which sentences / excerpts of the requirements the element comes from and how confident the LLM is.
    /// </summary>
    public abstract class ModelElement
    {
        public string Id { get; set; } = default!;
        public string Name { get; set; } = default!;

        /// <summary>Sentences (S1, S2, ...) the element is attributed to.</summary>
        public List<string> SentenceIds { get; set; } = new();

        /// <summary>Verbatim excerpts that justify the element.</summary>
        public List<string> Evidence { get; set; } = new();

        /// <summary>Evidence resolved to positions in the requirements text.</summary>
        public List<TextSpan> Spans { get; set; } = new();

        /// <summary>Confidence, 0..1.</summary>
        public double Confidence { get; set; }
        public ConfidenceLevel ConfidenceLevel { get; set; }

        public string Rationale { get; set; } = string.Empty;
    }
}
