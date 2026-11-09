using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Localization;

namespace TempBin.Localization;

public static class Text
{
    public static readonly string[] Cultures = ["en", "es", "pt-BR", "fr", "zh-CN"];
    public static readonly string[] LanguageNames = ["English", "Español", "Português (Brasil)", "Français", "简体中文"];
    public static IReadOnlyDictionary<string, string[]> Catalog { get; } = Load();
    public static string Locale => Resolve(CultureInfo.CurrentUICulture.Name) ?? "en";
    public static string? Resolve(string? value)
    {
        var language = value?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(language)) return null;
        return language.Split('-')[0] switch { "en" => "en", "es" => "es", "pt" => "pt-BR", "fr" => "fr", "zh" => "zh-CN", _ => null };
    }
    public static string Get(string key, string? locale = null)
    {
        if (!Catalog.TryGetValue(key, out var values)) throw new ArgumentException($"Unknown translation key: {key}", nameof(key));
        var index = Array.IndexOf(Cultures, Resolve(locale) ?? Locale);
        return index >= 0 && index < values.Length && !string.IsNullOrEmpty(values[index]) ? values[index] : values[0];
    }
    public static string Format(string key, params object[] values) => string.Format(CultureInfo.CurrentCulture, Get(key), values);
    public static string ClientMessages => JsonSerializer.Serialize(Catalog.Keys.Where(key => key.StartsWith("js.", StringComparison.Ordinal))
        .ToDictionary(key => key[3..], key => Get(key)));

    private static Dictionary<string, string[]> Load()
    {
        using var resource = typeof(Text).Assembly.GetManifestResourceStream("TempBin.Localization.messages.json")
            ?? throw new InvalidOperationException("Translation resource missing.");
        return JsonSerializer.Deserialize<Dictionary<string, string[]>>(resource)!;
    }
    public static void Configure(RequestLocalizationOptions options)
    {
        options.SetDefaultCulture("en").AddSupportedCultures(Cultures).AddSupportedUICultures(Cultures);
        options.ApplyCurrentCultureToResponseHeaders = true;
        options.RequestCultureProviders =
        [
            new CustomRequestCultureProvider(context => Task.FromResult(Resolve(context.Request.Query["lang"].FirstOrDefault()) is { } locale
                ? new ProviderCultureResult(locale) : null)),
            new CookieRequestCultureProvider(),
            new CustomRequestCultureProvider(context =>
            {
                // The website opens in English. API clients can request localized errors with Accept-Language.
                if (!context.Request.Path.StartsWithSegments("/api") && context.Request.Path != "/delete")
                    return Task.FromResult<ProviderCultureResult?>(null);
                var locale = context.Request.GetTypedHeaders().AcceptLanguage?.Where(item => item.Quality != 0)
                    .OrderByDescending(item => item.Quality ?? 1).Select(item => Resolve(item.Value.Value)).FirstOrDefault(value => value is not null);
                return Task.FromResult(locale is null ? null : new ProviderCultureResult(locale));
            })
        ];
    }
}
