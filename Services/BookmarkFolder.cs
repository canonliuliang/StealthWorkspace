namespace StealthWorkspace.Services;

public sealed record BookmarkFolder(string Id, string ParentId, string Name, int? Position = null)
{
    internal const string Other = "chrome-other-bookmarks";
    public override string ToString() => Name;
}
