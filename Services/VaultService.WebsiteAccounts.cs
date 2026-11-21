using System.Security.Cryptography;
using System.Text;

namespace StealthWorkspace.Services;

public partial class VaultService
{
    internal string GetWebsiteAccountIdentity()
    {
        lock (_sync)
        {
            EnsureUnlocked(requireReal: true);
            return WebsiteIdentityFor(_config!);
        }
    }

    private string? WebsiteIdentityForReset()
    {
        lock (_sync)
        {
            if (_config == null || !IsHexHash(_config.RealHashHex) || !IsHexSalt(_config.RealSaltHex)) return null;
            try { return WebsiteIdentityFor(_config); }
            catch (InvalidDataException) { return null; }
        }
    }

    private static string WebsiteIdentityFor(VaultConfig config)
    {
        if (!string.IsNullOrEmpty(config.WebsiteAccountIdentity))
        {
            if (Guid.TryParseExact(config.WebsiteAccountIdentity, "N", out Guid id) && id != Guid.Empty) return id.ToString("N");
            throw new InvalidDataException("保险箱的网站账户标识无效。");
        }
        if (!IsHexSalt(config.RealSaltHex)) throw new InvalidDataException("无法辨识此保险箱的网站账户。");
        // Older catalogs already have a unique random salt. Derive a public
        // identifier without reading a password, derived key or verifier. The
        // next master-password rotation pins the identifier in its atomic
        // catalog commit before changing the salt. Ordinary browsing is read-only.
        byte[] name = Encoding.ASCII.GetBytes("StealthWorkspace.WebsiteAccounts.v1/" + config.RealSaltHex.ToUpperInvariant());
        return Convert.ToHexString(SHA256.HashData(name))[..32].ToLowerInvariant();
    }
}
