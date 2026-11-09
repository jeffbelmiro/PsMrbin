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
        await store.TryCreateAsync("aaaaaaaaaaaa", "original");
        var generator = new SequenceGenerator();
        var service = new PasteService(store, generator);
        var paste = await service.CreateAsync("new");
        Assert.Equal("aaaaaaaaaaaaa", paste.Slug);
        Assert.Equal(new[] { 12, 13 }, generator.Lengths);
        Assert.Equal("original", (await store.FindAsync("aaaaaaaaaaaa")).Paste!.Content);
    }
    private sealed class SequenceGenerator : ISlugGenerator
    {
        public List<int> Lengths { get; } = [];
        public string Generate(int length) { Lengths.Add(length); return new string('a', length); }
    }
    [Fact]
    public async Task ConcurrentCreationProducesUniquePastes()
    {
        var service = new PasteService(new MemoryStore(new ManualClock()), new SlugGenerator());
        var results = await Task.WhenAll(Enumerable.Range(0, 300).Select(i => service.CreateAsync($"content {i}")));
        Assert.Equal(300, results.Select(p => p.Slug).Distinct().Count());
    }
    [Fact]
    public async Task ExpiryIsExactlyThirtyMinutesAndReadsDoNotRenew()
    {
        var clock = new ManualClock();
        var service = new PasteService(new MemoryStore(clock), new SlugGenerator());
        var paste = await service.CreateAsync("test");
        Assert.Equal(TimeSpan.FromMinutes(30), paste.ExpiresAt - paste.CreatedAt);
        clock.Advance(TimeSpan.FromMinutes(29));
        Assert.Equal(paste.ExpiresAt, (await service.FindAsync(paste.Slug)).Paste!.ExpiresAt);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(PasteState.Expired, (await service.FindAsync(paste.Slug)).State);
    }
}
