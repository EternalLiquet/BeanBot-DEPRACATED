using BeanBot.Logging;
using Discord;
using Discord.Commands;
using Microsoft.Extensions.Logging;

namespace BeanBot.Discord.Commands;

internal interface ILegacyCommandFeedbackDelivery
{
    Task SendAsync(ICommandContext context, string message);
}

internal sealed class DiscordLegacyCommandFeedbackDelivery : ILegacyCommandFeedbackDelivery
{
    internal static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(10);
    internal static AllowedMentions SafeAllowedMentions => AllowedMentions.None;

    public async Task SendAsync(ICommandContext context, string message)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        Task sendTask = context.Channel.SendMessageAsync(
            message,
            allowedMentions: SafeAllowedMentions);
        try
        {
            await sendTask.WaitAsync(SendTimeout);
        }
        catch (TimeoutException)
        {
            ObserveLateFault(sendTask);
            throw;
        }
    }

    private static void ObserveLateFault(Task sendTask)
    {
        if (sendTask.IsCompleted)
        {
            _ = sendTask.Exception;
            return;
        }

        _ = sendTask.ContinueWith(
            completedTask => _ = completedTask.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}

internal sealed class LegacyCommandFeedbackResponder
{
    internal const int MaxFeedbackLength = 500;
    private const string HelpHint = "Try `%help`.";
    private const string GenericFailureMessage = "Bean Bot couldn't complete that command. Try again in a moment.";

    private readonly ILegacyCommandFeedbackDelivery _delivery;
    private readonly ILogger<LegacyCommandFeedbackResponder> _logger;

    public LegacyCommandFeedbackResponder(
        ILegacyCommandFeedbackDelivery delivery,
        ILogger<LegacyCommandFeedbackResponder> logger)
    {
        _delivery = delivery ?? throw new ArgumentNullException(nameof(delivery));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task RespondAsync(
        Optional<CommandInfo> command,
        ICommandContext context,
        IResult result)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(result);

        if (result.IsSuccess)
        {
            return;
        }

        var feedback = CreateFeedback(command, result);
        try
        {
            await _delivery.SendAsync(context, feedback);
        }
        catch (Exception exception)
        {
            BeanBotLog.LegacyCommandFeedbackDeliveryFailed(
                _logger,
                result.Error,
                exception);
        }
    }

    internal static string CreateFeedback(Optional<CommandInfo> command, IResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var feedback = result.Error switch
        {
            CommandError.UnknownCommand => $"I don't know that command. {HelpHint}",
            CommandError.BadArgCount or
            CommandError.ParseFailed or
            CommandError.ObjectNotFound or
            CommandError.MultipleMatches => CreateArgumentFeedback(command),
            CommandError.UnmetPrecondition =>
                $"You can't use that command in this context. Check your permissions and try `%help` for usage details.",
            _ => GenericFailureMessage
        };

        return BoundFeedback(feedback);
    }

    internal static string BoundFeedback(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Length <= MaxFeedbackLength)
        {
            return message;
        }

        return string.Concat(message.AsSpan(0, MaxFeedbackLength - 1), "…");
    }

    private static string CreateArgumentFeedback(Optional<CommandInfo> command)
    {
        var usage = command.IsSpecified
            ? CreateUsage(command.Value)
            : null;
        return usage == null
            ? $"I couldn't understand those arguments. {HelpHint}"
            : $"I couldn't understand those arguments. Usage: `{usage}`. Try `%help` for more details.";
    }

    private static string? CreateUsage(CommandInfo command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(command.Name))
        {
            return null;
        }

        var parameters = command.Parameters
            .Select(parameter => $"<{parameter.Name}>");
        var parameterList = string.Join(" ", parameters);
        return string.IsNullOrEmpty(parameterList)
            ? $"%{command.Name}"
            : $"%{command.Name} {parameterList}";
    }
}
