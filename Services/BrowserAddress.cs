namespace StealthWorkspace.Services;

internal static class BrowserAddress
{
    internal static bool SamePage(string left, string right)
    {
        if (!Uri.TryCreate(left, UriKind.Absolute, out var a) || !Uri.TryCreate(right, UriKind.Absolute, out var b))
            return string.Equals(left, right, StringComparison.Ordinal);
        return SamePage(a, b);
    }

    // Reuse a parsed current address when matching an entire bookmark list.
    internal static bool SamePage(Uri left, string right) =>
        Uri.TryCreate(right, UriKind.Absolute, out var parsed) && SamePage(left, parsed);

    internal static bool SamePage(Uri a, Uri b)
    {
        // Hosts are case-insensitive. Paths, query values and fragments are not.
        return a.Scheme == b.Scheme && a.IdnHost.Equals(b.IdnHost, StringComparison.OrdinalIgnoreCase) && a.Port == b.Port &&
            a.UserInfo == b.UserInfo && a.GetComponents(UriComponents.PathAndQuery | UriComponents.Fragment, UriFormat.UriEscaped) ==
            b.GetComponents(UriComponents.PathAndQuery | UriComponents.Fragment, UriFormat.UriEscaped);
    }

    internal static Uri? Resolve(string? input, Func<string, string> searchUrl)
    {
        string text = input?.Trim() ?? "";
        if (text.Length == 0) return null;
        if (text.StartsWith("//", StringComparison.Ordinal)) text = "https:" + text;
        if (text.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            return WebUri(text);

        // Reject explicit unsupported schemes before trying the implicit-host path.
        if (text.Contains("://", StringComparison.Ordinal) || text.StartsWith(@"\\", StringComparison.Ordinal) ||
            (text.Length >= 3 && char.IsAsciiLetter(text[0]) && text[1] == ':' && text[2] is '/' or '\\') ||
            new[] { "javascript:", "data:", "file:", "about:", "edge:", "chrome:", "mailto:" }
            .Any(prefix => text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) return null;

        // Uri accepts shorthand/octal/hex IPv4 ("9" becomes 0.0.0.9). In an
        // omnibox, numbers and decimal/version strings are usually search terms.
        string authority = text.Split('/', '?', '#')[0];
        string host = authority.StartsWith('[') ? authority : authority.Split(':')[0];

        if (!text.Any(char.IsWhiteSpace) && Uri.TryCreate("http://" + text, UriKind.Absolute, out var address) && !string.IsNullOrEmpty(address.Host) && string.IsNullOrEmpty(address.UserInfo))
        {
            if ((address.HostNameType == UriHostNameType.IPv4 && IsDottedIPv4(host)) ||
                (address.HostNameType == UriHostNameType.IPv6 && authority.StartsWith('[')) ||
                address.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || address.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
                return address;
            // A DNS name with an explicit port is typically a development/intranet URL.
            if (address.HostNameType == UriHostNameType.Dns && authority.LastIndexOf(':') is > 0 and var colon &&
                authority[(colon + 1)..] is { Length: > 0 } port && port.All(char.IsAsciiDigit))
                return address;
            if (address.HostNameType == UriHostNameType.Dns && address.Host.Contains('.') &&
                address.Host.TrimEnd('.').Split('.')[^1].Any(char.IsLetter) && string.IsNullOrEmpty(address.UserInfo))
                return WebUri("https://" + text);
        }

        return WebUri(searchUrl(text));
    }

    private static bool IsDottedIPv4(string host)
    {
        string[] octets = host.Split('.');
        return octets.Length == 4 && octets.All(octet => octet.Length is >= 1 and <= 3 &&
            (octet.Length == 1 || octet[0] != '0') && octet.All(char.IsAsciiDigit) &&
            byte.TryParse(octet, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _));
    }

    private static Uri? WebUri(string text) => Uri.TryCreate(text, UriKind.Absolute, out var uri) &&
        uri.Scheme is "http" or "https" && !string.IsNullOrEmpty(uri.Host) ? uri : null;
}
