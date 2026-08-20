using System.Reflection;

namespace BeanBot.Hosting;

internal sealed record BuildIdentity(string Version, string CommitSha)
{
    public static BuildIdentity Current { get; } = FromAssembly(typeof(BuildIdentity).Assembly);

    internal static BuildIdentity FromAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var metadata = assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Where(attribute => !string.IsNullOrWhiteSpace(attribute.Key))
            .ToDictionary(
                attribute => attribute.Key,
                attribute => attribute.Value ?? string.Empty,
                StringComparer.Ordinal);
        var version = metadata.GetValueOrDefault("BeanBotReleaseVersion");
        var commitSha = metadata.GetValueOrDefault("BeanBotCommitSha");
        return new BuildIdentity(
            string.IsNullOrWhiteSpace(version) ? "unknown" : version,
            string.IsNullOrWhiteSpace(commitSha) ? "unknown" : commitSha);
    }
}
