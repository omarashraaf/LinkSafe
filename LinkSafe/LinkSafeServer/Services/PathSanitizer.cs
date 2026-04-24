namespace LinkSafeServer.Services;

public sealed class PathSanitizer
{
    private static readonly char[] InvalidChars = Path.GetInvalidFileNameChars()
        .Concat(['\\', '/', ':', '*', '?', '"', '<', '>', '|'])
        .Distinct()
        .ToArray();

    public string ToUserFolderName(string domainUser, string fallbackUser)
    {
        var leafUser = fallbackUser;

        if (!string.IsNullOrWhiteSpace(domainUser))
        {
            leafUser = domainUser.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? fallbackUser;
        }

        leafUser = leafUser.Replace('@', '_');
        return SanitizeFolderName(leafUser);
    }

    public string SanitizeFolderName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown";
        }

        var sanitized = new string(value.Select(character => InvalidChars.Contains(character) ? '_' : character).ToArray());
        sanitized = sanitized.Replace("..", "_");
        sanitized = sanitized.Trim('.', ' ');
        return string.IsNullOrWhiteSpace(sanitized) ? "unknown" : sanitized;
    }
}
