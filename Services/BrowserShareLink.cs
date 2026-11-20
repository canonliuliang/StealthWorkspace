using Windows.ApplicationModel.DataTransfer;

namespace StealthWorkspace.Services;

internal enum ShareLinkResult { Cleaned, Unchanged, Sensitive, Unavailable, ClipboardBusy }

internal static class BrowserShareLink
{
    private static readonly HashSet<string> Tracking = new(StringComparer.OrdinalIgnoreCase)
    {
        "utm_source", "utm_medium", "utm_campaign", "utm_content", "utm_term", "utm_id",
        "utm_source_platform", "utm_creative_format", "utm_marketing_tactic",
        "gclid", "dclid", "fbclid", "msclkid", "gbraid", "wbraid", "mc_cid", "mc_eid"
    };
    private static readonly HashSet<string> PrivateKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "code", "state", "nonce", "token", "jwt", "auth", "authkey", "session", "sessionid", "sid",
        "key", "api_key", "apikey", "secret", "password", "passwd", "credential", "client_secret", "clientsecret",
        "sig", "sign", "signature", "hmac", "policy", "key_pair_id", "awsaccesskeyid", "googleaccessid",
        "samlrequest", "samlresponse", "relaystate", "redirect_uri", "response_type", "code_verifier", "code_challenge",
        "accesstoken", "authtoken", "idtoken", "refreshtoken", "payment_intent", "setup_intent", "checkout_session_id"
    };
    private static readonly HashSet<string> PrivateRoutes = new(StringComparer.OrdinalIgnoreCase)
    {
        "auth", "oauth", "oauth2", "authorize", "authorization", "sso", "saml", "callback",
        "login", "log-in", "logon", "signin", "sign-in", "logout", "accounts", "password",
        "reset-password", "magic-link", "checkout", "payment", "payments", "pay"
    };

    internal static ShareLinkResult Prepare(string? address, out string? share)
    {
        share = null;
        if (string.IsNullOrEmpty(address) || address.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c == '\\') ||
            !Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            uri.Host.Length == 0) return ShareLinkResult.Unavailable;
        if (uri.UserInfo.Length != 0 || uri.Host.Split('.').Any(PrivateRoutes.Contains) || HasPrivateRoute(uri.AbsolutePath))
            return ShareLinkResult.Sensitive;

        int hash = address.IndexOf('#');
        int queryEnd = hash < 0 ? address.Length : hash;
        int question = address.IndexOf('?', 0, queryEnd);
        string fragment = hash < 0 ? "" : address[(hash + 1)..];
        if (HasPrivateField(fragment) || (fragment.StartsWith('/') && HasPrivateRoute(fragment.Split('?', 2)[0])))
            return ShareLinkResult.Sensitive;

        share = address;
        if (question < 0) return ShareLinkResult.Unchanged;
        string query = address[(question + 1)..queryEnd];
        if (HasPrivateField(query)) { share = null; return ShareLinkResult.Sensitive; }
        // Inspect decoded names only. Keep the original bytes, ordering, bare
        // keys and duplicate fields for every remaining segment and fragment.
        string[] fields = query.Split('&');
        var keep = new List<string>(fields.Length);
        foreach (string field in fields)
        {
            // Some servers also treat ";" as a separator. A mixed segment can
            // contain application fields and must be preserved as written.
            if (field.Contains(';') || !Tracking.Contains(FieldName(field))) keep.Add(field);
        }
        if (keep.Count == fields.Length) return ShareLinkResult.Unchanged;
        share = address[..question] + (keep.Count == 0 ? "" : "?" + string.Join('&', keep)) + address[queryEnd..];
        return ShareLinkResult.Cleaned;
    }

    private static bool HasPrivateRoute(string path) => Uri.UnescapeDataString(path).Split('/').Any(part =>
        PrivateRoutes.Contains(part.Split('.', 2)[0]));

    private static string FieldName(string field)
    {
        int equals = field.IndexOf('=');
        return Uri.UnescapeDataString(equals < 0 ? field : field[..equals]);
    }

    private static bool HasPrivateField(string fields)
    {
        foreach (string field in fields.Split(['&', ';', '?', '#'], StringSplitOptions.RemoveEmptyEntries))
        {
            string name = FieldName(field).Replace('-', '_');
            if (PrivateKeys.Contains(name) || name.EndsWith("_token", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith("_secret", StringComparison.OrdinalIgnoreCase) || name.EndsWith("_signature", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("x_amz_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("x_goog_", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    internal static ShareLinkResult Copy(string? address, Func<DataPackage, ClipboardContentOptions, bool> write)
    {
        var result = Prepare(address, out var share);
        if (share == null) return result;
        try
        {
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetText(share);
            var options = new ClipboardContentOptions { IsAllowedInHistory = false, IsRoamable = false };
            return write(package, options) ? result : ShareLinkResult.ClipboardBusy;
        }
        catch (Exception ex)
        {
            AppLog.Warn("复制网页链接失败: " + ex.GetType().Name);
            return ShareLinkResult.ClipboardBusy;
        }
    }
}
