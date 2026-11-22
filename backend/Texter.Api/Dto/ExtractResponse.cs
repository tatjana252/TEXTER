using Texter.Model.Conceptual;
using Texter.Model.GoJs;

namespace Texter.Api.Dto
{
    /// <param name="Model">Conceptual model (input for the next TEXTER step: ERLD).</param>
    /// <param name="Diagram">GoJS model for display.</param>
    public record ExtractResponse(ConceptualModel Model, DiagramModel Diagram);
}
