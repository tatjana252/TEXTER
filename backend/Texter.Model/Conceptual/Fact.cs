namespace Texter.Model.Conceptual
{
    /// <summary>
    /// Step 1 of the extraction: one atomic claim the requirements state, in the words of the text,
    /// read from sentences S1, S2, ... Model elements cite facts, not sentences.
    /// </summary>
    public class Fact
    {
        /// <summary>F1, F2, ...</summary>
        public string Id { get; set; } = default!;
        public string Statement { get; set; } = default!;
        public List<string> SentenceIds { get; set; } = new();
    }
}
