using System.ComponentModel.DataAnnotations;

namespace Texter.Api.Dto
{
    public class ExtractRequest
    {
        [Required, MinLength(10), MaxLength(50_000)]
        public string Text { get; set; } = default!;
    }
}
