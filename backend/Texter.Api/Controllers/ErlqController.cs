using Microsoft.AspNetCore.Mvc;
using Texter.Api.Dto;
using Texter.Erl;
using Texter.Erl.Ddl;
using Texter.Erl.Query;
using Texter.Erl.Semantics;
using Texter.Logic.Llm;
using Texter.Logic.Sandbox;

namespace Texter.Api.Controllers
{
    /// <summary>
    /// Step 4: ERLQ over the physical store. Every call sends the ERLD source; the DDL (and with it the sandbox
    /// database) is derived from it, so the client keeps no server state.
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    public class ErlqController(SandboxDatabase sandbox) : ControllerBase
    {
        /// <summary>Creates the database from the DDL (if needed); row counts and example queries.</summary>
        [HttpPost("database")]
        public async Task<ActionResult<ErlqDatabaseResponse>> Database(ErlqModelRequest request, CancellationToken ct)
        {
            if (Model(request.Code, out var model, out var ddl, out var errors) is { } invalid) return invalid;
            try
            {
                var info = await sandbox.EnsureAsync(ddl.Sql, ddl.Map, request.Database, ct);
                return new ErlqDatabaseResponse(info.Server, info.Database, info.Tables, ErlqExamples.For(model!, ddl.Map));
            }
            catch (SandboxException e)
            {
                return Problem(e.Message, statusCode: StatusCodes.Status503ServiceUnavailable, title: "Database error");
            }
        }

        /// <summary>The LLM fills the tables (guided by the requirements); rows that break a constraint are reported.</summary>
        [HttpPost("sample-data")]
        public async Task<ActionResult<ErlqSampleDataResponse>> SampleData(ErlqSampleDataRequest request, [FromServices] SampleDataGenerator generator, CancellationToken ct)
        {
            if (Model(request.Code, out _, out var ddl, out _) is { } invalid) return invalid;
            try
            {
                var data = await generator.GenerateAsync(request.Requirements, ddl.Sql, ddl.Map, ct);
                var result = await sandbox.LoadAsync(ddl.Sql, ddl.Map, data, request.Database, ct);
                return new ErlqSampleDataResponse(result.Tables, result.Rejected);
            }
            catch (LlmException e)
            {
                return Problem(e.Message, statusCode: StatusCodes.Status502BadGateway, title: "LLM error");
            }
            catch (SandboxException e)
            {
                return Problem(e.Message, statusCode: StatusCodes.Status503ServiceUnavailable, title: "Database error");
            }
        }

        /// <summary>ERLQ -> T-SQL -> rows. Translation errors have line and column; SQL errors come back as text.</summary>
        [HttpPost("run")]
        public async Task<ActionResult<ErlqRunResponse>> Run(ErlqRunRequest request, CancellationToken ct)
        {
            if (Model(request.Code, out var model, out var ddl, out _) is { } invalid) return invalid;
            var translation = ErlqTranslator.Translate(request.Query, model!, ddl.Map);
            if (translation.Sql is null) return new ErlqRunResponse(null, new(), new(), false, translation.Errors, null);
            try
            {
                var result = await sandbox.QueryAsync(ddl.Sql, ddl.Map, translation.Sql, request.Database, ct);
                return new ErlqRunResponse(translation.Sql, translation.Columns, result.Rows, result.Truncated, new(), null);
            }
            catch (SandboxException e)
            {
                return new ErlqRunResponse(translation.Sql, translation.Columns, new(), false, new(), e.Message);
            }
        }

        /// <summary>Rows of one table, for browsing the data.</summary>
        [HttpPost("table")]
        public async Task<ActionResult<ErlqTableResponse>> Table(ErlqTableRequest request, CancellationToken ct)
        {
            if (Model(request.Code, out _, out var ddl, out _) is { } invalid) return invalid;
            try
            {
                var result = await sandbox.TableAsync(ddl.Sql, ddl.Map, request.Table, request.Database, ct);
                return new ErlqTableResponse(result.Columns, result.Rows, result.Truncated);
            }
            catch (SandboxException e)
            {
                return Problem(e.Message, statusCode: StatusCodes.Status503ServiceUnavailable, title: "Database error");
            }
        }

        /// <summary>ERLD source -> model and DDL; a 422 with the ERLD errors when it does not analyse.</summary>
        private ActionResult? Model(string code, out ErldModel? model, out SqlServerDdlGenerator.DdlResult ddl, out List<ErlSyntaxError> errors)
        {
            var analysis = ErldModelBuilder.Analyze(code);
            model = analysis.Model;
            errors = analysis.Errors;
            ddl = default!;
            if (!analysis.Succeeded)
                return UnprocessableEntity(new { title = "ERLD errors", detail = $"The ERLD has {errors.Count} error(s); fix them in step 2.", errors });
            ddl = SqlServerDdlGenerator.Generate(model!);
            return null;
        }
    }
}
