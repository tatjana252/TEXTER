namespace Texter.Model.Conceptual
{
    /// <summary>
    /// Base for every element of the conceptual model. Carries the TEXTER traceability data:
    /// the evidence (verbatim quotes) the LLM used, where they are in the text, and how confident it is.
    /// </summary>
    public abstract class ModelElement
    {
        public string Id { get; set; } = default!;
        public string Name { get; set; } = default!;

        /// <summary>Confidence reported by the LLM, 0..1.</summary>
        public double Confidence { get; set; }
        public ConfidenceLevel ConfidenceLevel { get; set; }

        /// <summary>Verbatim excerpts from the requirements that justify this element.</summary>
        public List<string> Evidence { get; set; } = new();

        /// <summary>Evidence resolved to positions in the requirements text.</summary>
        public List<TextSpan> Spans { get; set; } = new();

        /// <summary>Short explanation of why the element was modeled this way.</summary>
        public string Rationale { get; set; } = string.Empty;
    }
}
