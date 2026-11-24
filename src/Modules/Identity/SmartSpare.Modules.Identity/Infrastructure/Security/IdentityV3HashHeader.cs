using System.Buffers.Binary;

namespace SmartSpare.Modules.Identity.Infrastructure.Security;

/// <summary>
/// The header of an ASP.NET Core Identity V3 password hash (base64 of: format marker <c>0x01</c>, PRF,
/// iteration count, salt length — each a big-endian 32-bit integer — then salt and subkey). Checking the
/// decoded header replaces checks that pinned a literal prefix, which changes with the iteration count.
/// </summary>
internal readonly record struct IdentityV3HashHeader(uint Prf, uint IterationCount, uint SaltLength, int TotalLength)
{
    public const byte FormatMarker = 0x01;

    /// <summary><c>KeyDerivationPrf.HMACSHA512</c>.</summary>
    public const uint HmacSha512 = 2;

    private const int HeaderLength = 13;

    /// <summary>V3 marker, HMAC-SHA512, at least <paramref name="minimumIterations"/> iterations and a 16-byte salt.</summary>
    public bool IsCurrent(int minimumIterations) =>
        Prf == HmacSha512 && IterationCount >= (uint)minimumIterations && SaltLength == 16 && TotalLength >= HeaderLength + SaltLength + 32;

    public static bool TryParse(string? hash, out IdentityV3HashHeader header)
    {
        header = default;
        if (string.IsNullOrEmpty(hash))
        {
            return false;
        }

        var bytes = new byte[(hash.Length * 3 / 4) + 3];
        if (!Convert.TryFromBase64String(hash, bytes, out var length) || length < HeaderLength || bytes[0] != FormatMarker)
        {
            return false;
        }

        header = new IdentityV3HashHeader(
            Prf: BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(1, 4)),
            IterationCount: BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(5, 4)),
            SaltLength: BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(9, 4)),
            TotalLength: length);
        return true;
    }
}
