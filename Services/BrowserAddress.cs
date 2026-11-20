namespace StealthWorkspace.Services;

internal static class BrowserAddress
{
    private static readonly string[] UnsupportedPrefixes = ["javascript:", "data:", "file:", "about:", "edge:", "chrome:", "mailto:"];

    internal readonly record struct PageKey(string Scheme, string Host, int Port, string UserInfo, string Resource)
    {
        internal static PageKey From(string address) => Uri.TryCreate(address, UriKind.Absolute, out var uri)
            ? new(uri.Scheme, uri.IdnHost.ToUpperInvariant(), uri.Port, uri.UserInfo,
                uri.GetComponents(UriComponents.PathAndQuery | UriComponents.Fragment, UriFormat.UriEscaped))
            : new("", "", -1, "", address);
    }

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

    internal static Uri? Resolve(string? input, Func<string, string> searchUrl) => Resolve(input, searchUrl, out _);

    internal static Uri? Resolve(string? input, Func<string, string> searchUrl, out bool isSearch)
    {
        isSearch = false;
        string text = input?.Trim() ?? "";
        if (text.Length == 0) return null;
        if (text.StartsWith("//", StringComparison.Ordinal)) text = "https:" + text;
        if (text.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            return WebUri(text);

        // Reject explicit unsupported schemes before trying the implicit-host path.
        if (text.Contains("://", StringComparison.Ordinal) || text.StartsWith(@"\\", StringComparison.Ordinal) ||
            (text.Length >= 3 && char.IsAsciiLetter(text[0]) && text[1] == ':' && text[2] is '/' or '\\')) return null;
        foreach (var prefix in UnsupportedPrefixes)
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;

        // Uri accepts shorthand/octal/hex IPv4 ("9" becomes 0.0.0.9). In an
        // omnibox, numbers and decimal/version strings are usually search terms.
        if (!text.Any(char.IsWhiteSpace) && Uri.TryCreate("http://" + text, UriKind.Absolute, out var address) && !string.IsNullOrEmpty(address.Host) && string.IsNullOrEmpty(address.UserInfo))
        {
            int end = text.AsSpan().IndexOfAny('/', '?', '#');
            ReadOnlySpan<char> authority = end < 0 ? text.AsSpan() : text.AsSpan(0, end);
            int colon = authority.LastIndexOf(':');
            if ((address.HostNameType == UriHostNameType.IPv4 && IsDottedIPv4((colon < 0 ? authority : authority[..colon]).ToString())) ||
                (address.HostNameType == UriHostNameType.IPv6 && authority.StartsWith("[")) ||
                address.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || address.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
                return address;
            // A DNS name with an explicit port is typically a development/intranet URL.
            if (address.HostNameType == UriHostNameType.Dns && colon > 0 && colon + 1 < authority.Length &&
                !authority[(colon + 1)..].ContainsAnyExceptInRange('0', '9'))
                return address;
            if (address.HostNameType == UriHostNameType.Dns && address.Host.Contains('.') &&
                address.Host.TrimEnd('.').Split('.')[^1].Any(char.IsLetter) && string.IsNullOrEmpty(address.UserInfo))
                return WebUri("https://" + text);
        }

        isSearch = true;
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
