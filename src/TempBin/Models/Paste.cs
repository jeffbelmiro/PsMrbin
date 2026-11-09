namespace TempBin.Models;

public sealed record Paste(string Slug, string Content, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, int SizeBytes, int Version = 1);
public sealed record CreatePasteRequest(string? Content);
public enum PasteState { Found, Missing, Expired }
public enum DeleteState { Deleted, Missing, Gone, Forbidden, TooLate }
public sealed record PasteLookup(PasteState State, Paste? Paste = null);
public sealed class PasteValidationException(string message, int status = 400) : Exception(message)
{
    public int Status { get; } = status;
}
