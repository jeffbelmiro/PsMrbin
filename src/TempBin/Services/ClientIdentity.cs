using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using TempBin.Storage;

namespace TempBin.Services;

// Only a keyed fingerprint is attached to a paste. Raw IPs are never stored or logged.
public sealed class ClientIdentity(IOptions<TempBinOptions> options)
{
    private readonly byte[] key = options.Value.DeletionKey is { Length: > 0 } configured
        ? Convert.FromBase64String(configured) : RandomNumberGenerator.GetBytes(32);
    public string Fingerprint(IPAddress? address)
    {
        if (address is null) return "";
        return Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(address.MapToIPv6().ToString())));
    }
    public static bool ValidKey(string? value)
    {
        if (string.IsNullOrEmpty(value)) return true;
        try { return Convert.FromBase64String(value).Length >= 32; }
        catch (FormatException) { return false; }
    }
}
