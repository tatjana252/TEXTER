using Microsoft.AspNetCore.Mvc;
using Texter.Api.Dto;
using Texter.Erl;
using Texter.Logic.Erld;

namespace Texter.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class ErldController : ControllerBase
    {
        /// <summary>Conceptual model -> ERLD source (checked against the ERLD grammar).</summary>
        [HttpPost("generate")]
        public ErldResponse Generate(GenerateErldRequest request)
        {
            var code = ErldGenerator.Generate(request.Model, request.ModelName, request.IncludeAttribution);
            return new ErldResponse(code, ErldSyntax.Validate(code));
        }

        /// <summary>Syntax check of (user edited) ERLD source.</summary>
        [HttpPost("validate")]
        public ErldResponse Validate(ValidateErldRequest request) =>
            new(request.Code, ErldSyntax.Validate(request.Code));
    }
}
