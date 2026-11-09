using System.Security.Cryptography;
using System.Text;
using TempBin.Models;
using TempBin.Storage;

namespace TempBin.Services;

public interface ISlugGenerator { string Generate(int length); }
public sealed class SlugGenerator : ISlugGenerator
{
    public const string Alphabet = "abcdefghjkmnpqrstuvwxyz23456789";
    public string Generate(int length)
    {
        ValidateLength(length);
        return string.Create(length, 0, (chars, _) =>
        {
            for (var i = 0; i < chars.Length; i++) chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        });
    }
    public static void ValidateLength(int length)
    {
        if (length is < 8 or > 16) throw new PasteValidationException("error.slug");
    }
    public static bool IsValid(string slug) => slug.Length is >= 8 and <= 16 && slug.All(Alphabet.Contains);
}

public sealed class PasteService(IPasteStore store, ISlugGenerator generator)
{
    public const int MaxBytes = 256 * 1024;
    public const int TtlSeconds = 1800;
    public Task<PasteLookup> FindAsync(string slug) => SlugGenerator.IsValid(slug)
        ? store.FindAsync(slug) : Task.FromResult(new PasteLookup(PasteState.Missing));

    public Task<DeleteState> DeleteAsync(string slug, string owner) => SlugGenerator.IsValid(slug)
        ? store.DeleteAsync(slug, owner) : Task.FromResult(DeleteState.Missing);

    public async Task<Paste> CreateAsync(string? content, int length, string owner = "")
    {
        SlugGenerator.ValidateLength(length);
        if (string.IsNullOrWhiteSpace(content)) throw new PasteValidationException("error.empty");
        if (Encoding.UTF8.GetByteCount(content) > MaxBytes) throw new PasteValidationException("error.size", 413);
        for (var attempt = 0; attempt < 12; attempt++)
        {
            var paste = await store.TryCreateAsync(generator.Generate(length), content, owner);
            if (paste is not null) return paste;
        }
        throw new PasteValidationException("error.retry", 503);
    }
}
