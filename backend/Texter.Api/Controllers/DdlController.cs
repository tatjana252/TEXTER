using Microsoft.AspNetCore.Mvc;
using Texter.Api.Dto;
using Texter.Erl;
using Texter.Erl.Ddl;
using Texter.Erl.Semantics;

namespace Texter.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class DdlController : ControllerBase
    {
        /// <summary>ERLD source -> semantic analysis -> SQL Server DDL.</summary>
        [HttpPost("sqlserver")]
        public DdlResponse SqlServer(GenerateDdlRequest request)
        {
            var analysis = ErldModelBuilder.Analyze(request.Code);
            if (!analysis.Succeeded) return new DdlResponse("", analysis.Errors, analysis.Warnings);

            var result = SqlServerDdlGenerator.Generate(analysis.Model!, request.IncludeAttribution);
            var warnings = analysis.Warnings.Concat(result.Warnings.Select(w => new ErlSyntaxError(0, 0, w))).ToList();
            return new DdlResponse(result.Sql, new List<ErlSyntaxError>(), warnings);
        }
    }
}
