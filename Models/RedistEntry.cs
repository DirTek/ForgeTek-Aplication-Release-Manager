namespace ForgeTekUpdatePackager.Models;

public class RedistEntry
{
    public string Name { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
    public string DetectionKeyPath { get; set; } = string.Empty;
    public string DetectionValueName { get; set; } = string.Empty;
    public string DetectionExpectedValue { get; set; } = string.Empty;
}
