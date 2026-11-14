namespace StealthWorkspace.Models;

public sealed class BrowserAddressSuggestion
{
    public override string ToString() => Title;
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public string Input { get; init; } = "";
    public string Target { get; init; } = "";
    public string Glyph { get; init; } = "\uE721";
}
