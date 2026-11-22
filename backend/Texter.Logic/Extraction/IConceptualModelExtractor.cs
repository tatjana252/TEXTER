using Texter.Model.Conceptual;
using Texter.Model.Traceability;

namespace Texter.Logic.Extraction
{
    /// <summary>Step 1 of TEXTER: requirements text -> attributed PMER conceptual model.</summary>
    public interface IConceptualModelExtractor
    {
        Task<ExtractionResult> ExtractAsync(string requirements, CancellationToken cancellationToken = default);
    }

    /// <param name="Sentences">Sentences S1, S2, ... the attribution refers to.</param>
    /// <param name="Notes">Problems and ambiguities found by the pipeline.</param>
    public record ExtractionResult(ConceptualModel Model, List<Sentence> Sentences, List<string> Notes);
}
