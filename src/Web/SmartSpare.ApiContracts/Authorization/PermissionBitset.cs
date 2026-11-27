using System.Buffers.Text;

namespace SmartSpare.ApiContracts.Authorization;

/// <summary>
/// The compact form of a permission set in the internal token (ADR 0028): bit <c>n</c> is the permission with
/// <see cref="PermissionInfo.Code"/> <c>n</c>, as base64url. 150 permissions take about 26 characters instead of about
/// 6 KB of names. The token also carries <see cref="Permissions.CatalogueVersion"/>; codes are append-only, so a service
/// with an older catalogue ignores the bits it does not know: no endpoint of that service can require them.
/// </summary>
public static class PermissionBitset
{
    public static string Encode(IEnumerable<string> permissions)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        var codes = permissions.Select(Permissions.Find).OfType<PermissionInfo>().Select(p => p.Code).ToList();
        var bytes = new byte[codes.Count == 0 ? 0 : (codes.Max() / 8) + 1];
        foreach (var code in codes)
        {
            bytes[code / 8] |= (byte)(1 << (code % 8));
        }

        return Base64Url.EncodeToString(bytes);
    }

    /// <summary>The permissions of the catalogue whose bits are set; bits of unknown codes are ignored.</summary>
    public static IReadOnlyList<string> Decode(string? bitset)
    {
        if (string.IsNullOrEmpty(bitset))
        {
            return [];
        }

        var bytes = Base64Url.DecodeFromChars(bitset);
        var names = new List<string>();
        for (var code = 0; code < bytes.Length * 8; code++)
        {
            if ((bytes[code / 8] & (1 << (code % 8))) != 0 && Permissions.Find(code) is { } permission)
            {
                names.Add(permission.Name);
            }
        }

        return names;
    }
}
