using System.ComponentModel.DataAnnotations;
using Texter.Erl;
using Texter.Logic.Sandbox;

namespace Texter.Api.Dto
{
    public class ErlqModelRequest
    {
        /// <summary>ERLD source of the model (step 2).</summary>
        [Required]
        public string Code { get; set; } = default!;

        /// <summary>Database created in step 3 to work in; empty: a temporary database built from the DDL.</summary>
        public string? Database
        {
            get => _database;
            set => _database = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private string? _database;
    }

    public class ErlqSampleDataRequest : ErlqModelRequest
    {
        /// <summary>Requirements text, so the data fits the domain.</summary>
        public string Requirements { get; set; } = "";
    }

    public class ErlqRunRequest : ErlqModelRequest
    {
        [Required]
        public string Query { get; set; } = default!;
    }

    public class ErlqTableRequest : ErlqModelRequest
    {
        [Required]
        public string Table { get; set; } = default!;
    }

    /// <param name="Server">SQL Server instance of the database (connect to it in SSMS to look at the data).</param>
    public record ErlqDatabaseResponse(string Server, string Database, List<TableCount> Tables, List<string> Examples);

    /// <param name="Rejected">Rows the database refused (constraint violations), with the reason.</param>
    public record ErlqSampleDataResponse(List<TableCount> Tables, List<string> Rejected);

    /// <param name="Sql">The T-SQL the query was translated to (null when it has errors).</param>
    /// <param name="Errors">ERLQ errors with line and column.</param>
    /// <param name="ExecutionError">SQL Server's message when the translated query failed to run.</param>
    public record ErlqRunResponse(string? Sql, List<string> Columns, List<List<string?>> Rows, bool Truncated, List<ErlSyntaxError> Errors, string? ExecutionError);

    public record ErlqTableResponse(List<string> Columns, List<List<string?>> Rows, bool Truncated);
}
