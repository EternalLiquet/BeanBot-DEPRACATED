using System.Text;

namespace BeanBot.Hosting;

internal static class LocalRuntimePrerequisites
{
    internal const string MissingPunResourceMessage =
        "The published pun resource is missing or empty.";
    internal const string DataDirectoryUnavailableMessage =
        "The persistent BeanBotFiles directory could not be created or accessed.";
    internal const string DataDirectoryNotWritableMessage =
        "The persistent BeanBotFiles directory is not writable.";
    internal const string ProbeCleanupFailedMessage =
        "The persistent BeanBotFiles directory probe could not be cleaned up.";

    internal static void Validate(
        string persistentDataDirectory,
        string punResourcePath,
        string probePrefix,
        Action<string>? afterProbeWritten = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(persistentDataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(punResourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(probePrefix);

        EnsurePersistentDataDirectory(persistentDataDirectory);
        EnsurePunResource(punResourcePath);
        ProbePersistentDataDirectory(
            persistentDataDirectory,
            probePrefix,
            afterProbeWritten);
    }

    private static void EnsurePersistentDataDirectory(string persistentDataDirectory)
    {
        try
        {
            Directory.CreateDirectory(persistentDataDirectory);
        }
        catch (Exception exception) when (IsFileSystemException(exception))
        {
            throw new LocalRuntimePrerequisiteException(
                DataDirectoryUnavailableMessage,
                exception);
        }
    }

    private static void EnsurePunResource(string punResourcePath)
    {
        try
        {
            if (!File.Exists(punResourcePath) || new FileInfo(punResourcePath).Length == 0)
            {
                throw new LocalRuntimePrerequisiteException(MissingPunResourceMessage);
            }
        }
        catch (LocalRuntimePrerequisiteException)
        {
            throw;
        }
        catch (Exception exception) when (IsFileSystemException(exception))
        {
            throw new LocalRuntimePrerequisiteException(
                MissingPunResourceMessage,
                exception);
        }
    }

    private static void ProbePersistentDataDirectory(
        string persistentDataDirectory,
        string probePrefix,
        Action<string>? afterProbeWritten)
    {
        var probePath = Path.Combine(
            persistentDataDirectory,
            $"{probePrefix}-{Guid.NewGuid():N}.tmp");
        Exception? probeFailure = null;
        Exception? cleanupFailure = null;

        try
        {
            using var stream = new FileStream(
                probePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None);
            stream.Write(Encoding.UTF8.GetBytes("BeanBot local runtime prerequisite probe"));
            afterProbeWritten?.Invoke(probePath);
        }
        catch (Exception exception)
        {
            probeFailure = exception;
        }
        finally
        {
            try
            {
                File.Delete(probePath);
            }
            catch (Exception exception) when (IsFileSystemException(exception))
            {
                cleanupFailure = exception;
            }
        }

        if (probeFailure is not null)
        {
            throw new LocalRuntimePrerequisiteException(
                DataDirectoryNotWritableMessage,
                probeFailure);
        }

        if (cleanupFailure is not null)
        {
            throw new LocalRuntimePrerequisiteException(
                ProbeCleanupFailedMessage,
                cleanupFailure);
        }
    }

    private static bool IsFileSystemException(Exception exception)
        => exception is IOException or UnauthorizedAccessException or NotSupportedException;
}

internal sealed class LocalRuntimePrerequisiteException : Exception
{
    internal LocalRuntimePrerequisiteException(string message)
        : base(message)
    {
    }

    internal LocalRuntimePrerequisiteException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
