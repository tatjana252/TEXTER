using Microsoft.AspNetCore.Mvc;
using Texter.Api.Dto;
using Texter.Logic.Diagram;
using Texter.Logic.Extraction;
using Texter.Logic.Llm;

namespace Texter.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class ConceptualModelController : ControllerBase
    {
        private readonly IConceptualModelExtractor _extractor;

        public ConceptualModelController(IConceptualModelExtractor extractor) => _extractor = extractor;

        /// <summary>Requirements text -> EER model with traceability and confidence, plus its diagram.</summary>
        [HttpPost("extract")]
        public async Task<ActionResult<ExtractResponse>> Extract(ExtractRequest request, CancellationToken cancellationToken)
        {
            try
            {
                var model = await _extractor.ExtractAsync(request.Text, cancellationToken);
                return new ExtractResponse(model, ConvertToDiagram.Convert(model));
            }
            catch (LlmException ex)
            {
                return Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway, title: "LLM error");
            }
        }
    }
}
