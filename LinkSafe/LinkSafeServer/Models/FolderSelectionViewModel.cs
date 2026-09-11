namespace LinkSafeServer.Models;

public sealed record FolderSelectionViewModel(IEnumerable<string> Options, IEnumerable<string> Selected);
