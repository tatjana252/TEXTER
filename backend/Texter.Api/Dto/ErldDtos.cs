using System.ComponentModel.DataAnnotations;
using Texter.Erl;
using Texter.Model.Conceptual;

namespace Texter.Api.Dto
{
    public class GenerateErldRequest
    {
        [Required]
        public ConceptualModel Model { get; set; } = default!;

        public string ModelName { get; set; } = "Texter";

        /// <summary>Write @source / @confidence annotations.</summary>
        public bool IncludeAttribution { get; set; } = true;
    }

    public class ValidateErldRequest
    {
        [Required]
        public string Code { get; set; } = default!;
    }

    public record ErldResponse(string Code, List<ErlSyntaxError> Errors);
}
