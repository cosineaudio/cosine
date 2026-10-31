namespace Cosine.Core.Entities;

public class InternetRadioStation
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string StreamUrl { get; set; } = string.Empty;
    public string? HomepageUrl { get; set; }
}
