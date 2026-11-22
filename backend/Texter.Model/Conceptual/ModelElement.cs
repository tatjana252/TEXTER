namespace Texter.Model.Conceptual
{
    /// <summary>
    /// Base for every element of the conceptual model. Carries the TEXTER attribution:
    /// the facts the element rests on, the sentences those facts are read from, and how directly the facts support it.
    /// </summary>
    public abstract class ModelElement
    {
        public string Id { get; set; } = default!;
        public string Name { get; set; } = default!;

        /// <summary>Facts (F1, F2, ...) that carry the element; empty when it rests on modeling convention.</summary>
        public List<string> FactIds { get; set; } = new();

        /// <summary>Sentences (S1, S2, ...) the cited facts are read from, including the facts of the element's decisions.</summary>
        public List<string> SentenceIds { get; set; } = new();

        /// <summary>Those sentences as positions in the requirements text.</summary>
        public List<TextSpan> Spans { get; set; } = new();

        public ConfidenceCategory Confidence { get; set; }
        public ConfidenceLevel ConfidenceLevel { get; set; }

        /// <summary>Why the element takes this form (with the heuristic code); empty when it is stated outright.</summary>
        public string Rationale { get; set; } = string.Empty;
    }
}
