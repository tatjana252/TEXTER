using Microsoft.AspNetCore.Mvc;
using Texter.Api.Dto;
using Texter.Erl;
using Texter.Erl.Ddl;
using Texter.Erl.Mongo;
using Texter.Erl.Query;
using Texter.Erl.Semantics;
using Texter.Logic.Llm;
using Texter.Logic.Mongo;
using Texter.Logic.Sandbox;

namespace Texter.Api.Controllers
{
    /// <summary>
    /// Step 4: ERLQ over the physical store, SQL Server or MongoDB ("store"). Every call sends the ERLD source; the
    /// schema (and with it the temporary database) is derived from it, so the client keeps no server state.
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    public class ErlqController(SandboxDatabase sandbox, MongoStore mongo) : ControllerBase
    {
        /// <summary>The database to query (created from the schema if temporary); counts and example queries.</summary>
        [HttpPost("database")]
        public async Task<ActionResult<ErlqDatabaseResponse>> Database(ErlqModelRequest request, CancellationToken ct)
        {
            if (Model(request.Code, out var model, out var ddl) is { } invalid) return invalid;
            try
            {
                var examples = ErlqExamples.For(model!, ddl.Map);
                if (request.IsMongo)
                {
                    var schema = MongoSchemaGenerator.Generate(model!);
                    var info = await mongo.EnsureAsync(schema, request.Database, ct);
                    var mongoExamples = examples.Where(q => ErlqMongoTranslator.Translate(q, model!, schema.Map).Query is not null).ToList();
                    return new ErlqDatabaseResponse(info.Server, info.Database, info.Tables, mongoExamples);
                }
                var sqlInfo = await sandbox.EnsureAsync(ddl.Sql, ddl.Map, request.Database, ct);
                return new ErlqDatabaseResponse(sqlInfo.Server, sqlInfo.Database, sqlInfo.Tables, examples);
            }
            catch (SandboxException e)
            {
                return Problem(e.Message, statusCode: StatusCodes.Status503ServiceUnavailable, title: "Database error");
            }
        }

        /// <summary>The LLM fills the tables / collections (guided by the requirements); what the database rejects is reported.</summary>
        [HttpPost("sample-data")]
        public async Task<ActionResult<ErlqSampleDataResponse>> SampleData(ErlqSampleDataRequest request,
            [FromServices] SampleDataGenerator generator, [FromServices] MongoSampleDataGenerator mongoGenerator, CancellationToken ct)
        {
            if (Model(request.Code, out var model, out var ddl) is { } invalid) return invalid;
            try
            {
                if (request.IsMongo)
                {
                    var schema = MongoSchemaGenerator.Generate(model!);
                    await mongo.EnsureAsync(schema, request.Database, ct); // before asking the LLM: fail fast without MongoDB
                    var documents = await mongoGenerator.GenerateAsync(request.Requirements, schema, ct);
                    var loaded = await mongo.LoadAsync(schema, documents, request.Database, ct);
                    return new ErlqSampleDataResponse(loaded.Tables, loaded.Rejected);
                }
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

        /// <summary>ERLQ -> T-SQL or aggregation pipeline -> rows. Translation errors have line and column.</summary>
        [HttpPost("run")]
        public async Task<ActionResult<ErlqRunResponse>> Run(ErlqRunRequest request, CancellationToken ct)
        {
            if (Model(request.Code, out var model, out var ddl) is { } invalid) return invalid;

            if (request.IsMongo)
            {
                var schema = MongoSchemaGenerator.Generate(model!);
                var mongoTranslation = ErlqMongoTranslator.Translate(request.Query, model!, schema.Map);
                if (mongoTranslation.Query is not { } query) return new ErlqRunResponse(null, new(), new(), false, mongoTranslation.Errors, null);
                var columns = query.Columns.Select(c => c.Header).ToList();
                try
                {
                    var result = await mongo.QueryAsync(schema, query, request.Database, ct);
                    return new ErlqRunResponse(query.Script, columns, result.Rows, result.Truncated, new(), null);
                }
                catch (SandboxException e)
                {
                    return new ErlqRunResponse(query.Script, columns, new(), false, new(), e.Message);
                }
            }

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

        /// <summary>Rows of one table, or documents of one collection, for browsing the data.</summary>
        [HttpPost("table")]
        public async Task<ActionResult<ErlqTableResponse>> Table(ErlqTableRequest request, CancellationToken ct)
        {
            if (Model(request.Code, out var model, out var ddl) is { } invalid) return invalid;
            try
            {
                var result = request.IsMongo
                    ? await mongo.CollectionAsync(MongoSchemaGenerator.Generate(model!), request.Table, request.Database, ct)
                    : await sandbox.TableAsync(ddl.Sql, ddl.Map, request.Table, request.Database, ct);
                return new ErlqTableResponse(result.Columns, result.Rows, result.Truncated);
            }
            catch (SandboxException e)
            {
                return Problem(e.Message, statusCode: StatusCodes.Status503ServiceUnavailable, title: "Database error");
            }
        }

        /// <summary>ERLD source -> model and SQL Server DDL; a 422 with the ERLD errors when it does not analyse.</summary>
        private ActionResult? Model(string code, out ErldModel? model, out SqlServerDdlGenerator.DdlResult ddl)
        {
            var analysis = ErldModelBuilder.Analyze(code);
            model = analysis.Model;
            ddl = default!;
            if (!analysis.Succeeded)
                return UnprocessableEntity(new { title = "ERLD errors", detail = $"The ERLD has {analysis.Errors.Count} error(s); fix them in step 2.", errors = analysis.Errors });
            ddl = SqlServerDdlGenerator.Generate(model!);
            return null;
        }
    }
}
