namespace BeanBot.Hosting;

internal static class ContainerSmokeTest
{
    internal const string Argument = "--container-smoke-test";

    public static int Run(
        string persistentDataDirectory,
        string punResourcePath,
        TextWriter output,
        TextWriter error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(persistentDataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(punResourcePath);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        try
        {
            Directory.CreateDirectory(persistentDataDirectory);
            if (!File.Exists(punResourcePath) || new FileInfo(punResourcePath).Length == 0)
            {
                throw new InvalidOperationException("The published pun resource is missing or empty.");
            }

            var probePath = Path.Combine(
                persistentDataDirectory,
                $"container-smoke-{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(probePath, "BeanBot container smoke test");
            }
            finally
            {
                File.Delete(probePath);
            }

            output.WriteLine(
                $"BeanBot container smoke test passed. Version={BuildIdentity.Current.Version}, CommitSha={BuildIdentity.Current.CommitSha}");
            return 0;
        }
        catch (Exception exception)
        {
            error.WriteLine($"BeanBot container smoke test failed: {exception.Message}");
            return 1;
        }
    }
}
