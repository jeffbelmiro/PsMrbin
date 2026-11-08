using TempBin.Models;
using TempBin.Services;
using Xunit;

namespace TempBin.Tests;

public class ServiceTests
{
    [Theory][InlineData(8)][InlineData(12)][InlineData(16)]
    public void SlugsUseRequestedLengthAndReadableAlphabet(int length)
    {
        var generator = new SlugGenerator();
        for (var i = 0; i < 100; i++)
        {
            var slug = generator.Generate(length);
            Assert.Equal(length, slug.Length);
            Assert.All(slug, c => Assert.Contains(c, SlugGenerator.Alphabet));
        }
    }
    [Theory][InlineData(4)][InlineData(7)][InlineData(17)][InlineData(0)]
    public void SlugsBelowEightOrAboveSixteenAreRejected(int length) => Assert.Throws<PasteValidationException>(() => new SlugGenerator().Generate(length));
    [Theory][InlineData("abcdefgh!")][InlineData("ABCDEFGH")][InlineData("abc")][InlineData("00000000")][InlineData("abcdefghijklmnopq")]
    public void InvalidSlugsAreRejected(string slug) => Assert.False(SlugGenerator.IsValid(slug));

    [Fact]
    public async Task CollisionRetriesAndNeverOverwrites()
    {
        var store = new MemoryStore(new ManualClock());
        await store.TryCreateAsync("abcdefgh", "original");
        var service = new PasteService(store, new SequenceGenerator());
        var paste = await service.CreateAsync("new", 8);
        Assert.Equal("jkmnpqrs", paste.Slug);
        Assert.Equal("original", (await store.FindAsync("abcdefgh")).Paste!.Content);
    }
    private sealed class SequenceGenerator : ISlugGenerator
    {
        private int calls;
        public string Generate(int length) => ++calls == 1 ? "abcdefgh" : "jkmnpqrs";
    }
    [Fact]
    public async Task ConcurrentCreationProducesUniquePastes()
    {
        var service = new PasteService(new MemoryStore(new ManualClock()), new SlugGenerator());
        var results = await Task.WhenAll(Enumerable.Range(0, 300).Select(i => service.CreateAsync($"content {i}", 8)));
        Assert.Equal(300, results.Select(p => p.Slug).Distinct().Count());
    }
    [Fact]
    public async Task ExpiryIsExactlyThirtyMinutesAndReadsDoNotRenew()
    {
        var clock = new ManualClock();
        var service = new PasteService(new MemoryStore(clock), new SlugGenerator());
        var paste = await service.CreateAsync("test", 8);
        Assert.Equal(TimeSpan.FromMinutes(30), paste.ExpiresAt - paste.CreatedAt);
        clock.Advance(TimeSpan.FromMinutes(29));
        Assert.Equal(paste.ExpiresAt, (await service.FindAsync(paste.Slug)).Paste!.ExpiresAt);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(PasteState.Expired, (await service.FindAsync(paste.Slug)).State);
    }
}
