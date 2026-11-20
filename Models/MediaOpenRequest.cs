namespace StealthWorkspace.Models;

public sealed record VaultViewState(string Search, int FilterIndex, string? SelectedId, int SortIndex = 0);

public sealed record MediaOpenRequest(VaultItem Item, IReadOnlyList<VaultItem> Items, VaultViewState? ReturnState);
