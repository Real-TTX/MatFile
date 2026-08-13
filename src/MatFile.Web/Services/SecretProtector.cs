using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace MatFile.Web.Services;

/// <summary>
/// Verschlüsselt Zugangsdaten von Freigaben. Die Schlüssel liegen im Volume unter /data/keys,
/// die Daten bleiben also auch nach einem Neustart lesbar - und ohne das Volume unbrauchbar.
/// </summary>
public sealed class SecretProtector
{
    private readonly IDataProtector _protector;

    public SecretProtector(IDataProtectionProvider provider)
        => _protector = provider.CreateProtector("MatFile.StorageRoot.Secret");

    public string? Protect(string? value)
        => string.IsNullOrEmpty(value) ? null : _protector.Protect(value);

    public string? Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        try
        {
            return _protector.Unprotect(value);
        }
        catch (CryptographicException)
        {
            // Schlüssel weg oder Wert beschädigt - die Verbindung schlägt dann sichtbar fehl.
            return null;
        }
    }
}
