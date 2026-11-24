using System.ComponentModel.DataAnnotations;
using Texter.Erl;

namespace Texter.Api.Dto
{
    public class GenerateDdlRequest
    {
        /// <summary>ERLD source (as generated or edited in step 2).</summary>
        [Required]
        public string Code { get; set; } = default!;

        /// <summary>Keep @source / @confidence as SQL comments.</summary>
        public bool IncludeAttribution { get; set; } = true;

        /// <summary>Start the script by dropping its tables if they exist, so it can be run again (their data is lost).</summary>
        public bool DropExisting { get; set; }
    }

    public class DeployDdlRequest : GenerateDdlRequest
    {
        /// <summary>Database to create (if it does not exist) and run the DDL in.</summary>
        [Required]
        public string Database { get; set; } = default!;
    }

    /// <param name="Error">Why the database could not be created or the DDL failed; null on success.</param>
    public record DeployResponse(string? Database, string? Server, bool Created, List<string> Tables, List<ErlSyntaxError> Errors, string? Error);

    public class MongoSchemaRequest
    {
        [Required]
        public string Code { get; set; } = default!;

        /// <summary>Start the script by dropping its collections (their documents are lost).</summary>
        public bool DropExisting { get; set; }
    }

    public class MongoDeployRequest : MongoSchemaRequest
    {
        [Required]
        public string Database { get; set; } = default!;
    }

    /// <param name="Script">mongosh script; empty when the ERLD has errors.</param>
    /// <param name="NotEnforced">What MongoDB itself does not check.</param>
    public record MongoSchemaResponse(string Script, List<string> NotEnforced, List<ErlSyntaxError> Errors, List<ErlSyntaxError> Warnings);

    /// <param name="Sql">DDL script; empty when the ERLD has errors.</param>
    /// <param name="Errors">Syntax and semantic errors in the ERLD (line, column).</param>
    /// <param name="Warnings">ERLD warnings (with line) and generator warnings (line 0).</param>
    public record DdlResponse(string Sql, List<ErlSyntaxError> Errors, List<ErlSyntaxError> Warnings);
}
