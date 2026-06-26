using System.ComponentModel.DataAnnotations;

namespace VideoSceneSearch.Models;

public class SearchRequest
{
    [MaxLength(500)]
    public string Query { get; set; } = string.Empty;
}
