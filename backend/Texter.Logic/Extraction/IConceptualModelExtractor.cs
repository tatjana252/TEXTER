using Texter.Model.Conceptual;

namespace Texter.Logic.Extraction
{
    /// <summary>Step 1 of TEXTER: requirements text -> traceable EER conceptual model.</summary>
    public interface IConceptualModelExtractor
    {
        Task<ConceptualModel> ExtractAsync(string requirements, CancellationToken cancellationToken = default);
    }
}
