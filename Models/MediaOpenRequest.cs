namespace StealthWorkspace.Models;

public sealed record VaultViewState(string Search, int FilterIndex, string? SelectedId);

public sealed record MediaOpenRequest(VaultItem Item, IReadOnlyList<VaultItem> Images, VaultViewState? ReturnState);
