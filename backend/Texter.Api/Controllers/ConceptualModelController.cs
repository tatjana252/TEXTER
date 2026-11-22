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

        /// <summary>Requirements text -> TEXTER pipeline -> attributed PMER model, plus its diagram.</summary>
        [HttpPost("extract")]
        public async Task<ActionResult<ExtractResponse>> Extract(ExtractRequest request, CancellationToken cancellationToken)
        {
            try
            {
                var result = await _extractor.ExtractAsync(request.Text, cancellationToken);
                return new ExtractResponse(result.Model, ConvertToDiagram.Convert(result.Model), result.Sentences, result.Notes);
            }
            catch (LlmException ex)
            {
                return Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway, title: "LLM error");
            }
        }
    }
}
