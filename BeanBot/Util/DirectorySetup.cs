namespace BeanBot.Util;

public static class DirectorySetup
{
    public static string BotBaseDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "BeanBotFiles");

    public static string LogsDirectory { get; } = Path.Combine(BotBaseDirectory, "Logs");

    public static string ResourcesDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "Resources");

    public static void EnsureDirectoriesExist()
    {
        Directory.CreateDirectory(BotBaseDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }
}
