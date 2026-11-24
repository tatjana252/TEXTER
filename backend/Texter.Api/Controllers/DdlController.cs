using Microsoft.AspNetCore.Mvc;
using Texter.Api.Dto;
using Texter.Erl;
using Texter.Erl.Ddl;
using Texter.Erl.Semantics;
using Texter.Erl.Mongo;
using Texter.Logic.Mongo;
using Texter.Logic.Sandbox;

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

            var result = SqlServerDdlGenerator.Generate(analysis.Model!, request.IncludeAttribution, request.DropExisting);
            var warnings = analysis.Warnings.Concat(result.Warnings.Select(w => new ErlSyntaxError(0, 0, w))).ToList();
            return new DdlResponse(result.Sql, new List<ErlSyntaxError>(), warnings);
        }

        /// <summary>ERLD source -> MongoDB schema: collections with $jsonSchema validators and unique indexes (mongosh).</summary>
        [HttpPost("mongodb")]
        public MongoSchemaResponse MongoDb(MongoSchemaRequest request)
        {
            var analysis = ErldModelBuilder.Analyze(request.Code);
            if (!analysis.Succeeded) return new MongoSchemaResponse("", new(), analysis.Errors, analysis.Warnings);
            var result = MongoSchemaGenerator.Generate(analysis.Model!, request.DropExisting);
            var warnings = analysis.Warnings.Concat(result.Warnings.Select(w => new ErlSyntaxError(0, 0, w))).ToList();
            return new MongoSchemaResponse(result.Script, result.NotEnforced, new(), warnings);
        }

        /// <summary>Creates the MongoDB database's collections (validators) and indexes.</summary>
        [HttpPost("mongodb/deploy")]
        public async Task<DeployResponse> MongoDbDeploy(MongoDeployRequest request, [FromServices] MongoStore mongo, CancellationToken ct)
        {
            var analysis = ErldModelBuilder.Analyze(request.Code);
            if (!analysis.Succeeded) return new DeployResponse(null, null, false, new(), analysis.Errors, "The ERLD has errors; fix them in step 2.");
            try
            {
                var result = await mongo.DeployAsync(request.Database, MongoSchemaGenerator.Generate(analysis.Model!), request.DropExisting, ct);
                return new DeployResponse(result.Database, result.Server, result.Created, result.Collections, new(), null);
            }
            catch (SandboxException e)
            {
                return new DeployResponse(request.Database, null, false, new(), new(), e.Message);
            }
        }

        /// <summary>Creates the database (unless it exists) on the configured SQL Server and runs the DDL in it.</summary>
        [HttpPost("deploy")]
        public async Task<DeployResponse> Deploy(DeployDdlRequest request, [FromServices] DatabaseDeployer deployer, CancellationToken ct)
        {
            var analysis = ErldModelBuilder.Analyze(request.Code);
            if (!analysis.Succeeded) return new DeployResponse(null, null, false, new(), analysis.Errors, "The ERLD has errors; fix them in step 2.");

            var ddl = SqlServerDdlGenerator.Generate(analysis.Model!, request.IncludeAttribution, request.DropExisting);
            try
            {
                var result = await deployer.DeployAsync(request.Database, ddl.Sql, ct);
                return new DeployResponse(result.Database, result.Server, result.Created, result.Tables, new(), null);
            }
            catch (SandboxException e)
            {
                return new DeployResponse(request.Database, null, false, new(), new(), e.Message);
            }
        }
    }
}
