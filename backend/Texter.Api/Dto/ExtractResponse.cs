using Texter.Model.Conceptual;
using Texter.Model.GoJs;
using Texter.Model.Traceability;

namespace Texter.Api.Dto
{
    /// <param name="Model">Attributed conceptual model (input for the next TEXTER step: ERLD).</param>
    /// <param name="Diagram">GoJS model for display.</param>
    /// <param name="Sentences">Sentences S1, S2, ... the attribution refers to.</param>
    /// <param name="Notes">Problems and ambiguities found by the pipeline.</param>
    public record ExtractResponse(ConceptualModel Model, DiagramModel Diagram, List<Sentence> Sentences, List<string> Notes);
}
