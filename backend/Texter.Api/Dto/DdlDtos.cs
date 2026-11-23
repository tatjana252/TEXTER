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
    }

    /// <param name="Sql">DDL script; empty when the ERLD has errors.</param>
    /// <param name="Errors">Syntax and semantic errors in the ERLD (line, column).</param>
    /// <param name="Warnings">ERLD warnings (with line) and generator warnings (line 0).</param>
    public record DdlResponse(string Sql, List<ErlSyntaxError> Errors, List<ErlSyntaxError> Warnings);
}
