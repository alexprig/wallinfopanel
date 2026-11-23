using System.ComponentModel.DataAnnotations;

namespace WallIServer;

/// <summary>Configuration section <c>WallIServer</c>.</summary>
public sealed class WallIServerOptions
{
    public const string SectionName = "WallIServer";

    /// <summary>Required. Value expected in the <c>X-Api-Key</c> header of every admin request.</summary>
    [Required(AllowEmptyStrings = false)]
    [MinLength(8)]
    public string AdminApiKey { get; set; } = "";

    /// <summary>Root of the file storage (relative paths resolve against the content root).</summary>
    [Required(AllowEmptyStrings = false)]
    public string DataDirectory { get; set; } = "./data";

    /// <summary>Number of image versions kept per device for region updates (protocol.md §2.4: 8).</summary>
    [Range(1, 64)]
    public int MaxVersionsPerDevice { get; set; } = 8;

    /// <summary>A region is sent only if its body is ≤ this fraction of a full image (protocol.md §2.4: 0.6).</summary>
    [Range(0.0, 1.0)]
    public double RegionMaxRatio { get; set; } = 0.6;
}
