using System.Globalization;
using System.Reflection;

namespace TempBin.Web;

public static class BuildInfo
{
    private static readonly Assembly Assembly = typeof(BuildInfo).Assembly;
    public static string Version { get; } = Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];
    public static DateTimeOffset BuiltAt { get; } = DateTimeOffset.Parse(Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(attribute => attribute.Key == "BuildDateUtc").Value!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
}
