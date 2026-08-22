using BeanBot.Discord.Commands;
using Discord;
using Discord.Commands;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BeanBot.Tests.Discord.Commands;

public class LegacyCommandFeedbackResponderTests
{
    [Fact]
    public async Task UnknownCommand_ReturnsActionableHelpHintWithoutEchoingRawReason()
    {
        var delivery = new RecordingDelivery();
        var responder = CreateResponder(delivery);
        var result = new FakeResult(CommandError.UnknownCommand, "@everyone token=secret");

        await responder.RespondAsync(default, new StubCommandContext(), result);

        var message = Assert.Single(delivery.Messages);
        Assert.Equal("I don't know that command. Try `%help`.", message);
        Assert.DoesNotContain("@everyone", message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(CommandError.BadArgCount)]
    [InlineData(CommandError.ParseFailed)]
    [InlineData(CommandError.ObjectNotFound)]
    [InlineData(CommandError.MultipleMatches)]
    public async Task ArgumentErrors_WithCommandMetadata_ReturnBoundedUsageGuidance(CommandError error)
    {
        var command = await GetUsageCommandAsync();
        var delivery = new RecordingDelivery();
        var responder = CreateResponder(delivery);

        await responder.RespondAsync(
            new Optional<CommandInfo>(command),
            new StubCommandContext(),
            new FakeResult(error, "internal parser detail"));

        var message = Assert.Single(delivery.Messages);
        Assert.Contains("Usage: `%sample <text> <count>`", message, StringComparison.Ordinal);
        Assert.Contains("%help", message, StringComparison.Ordinal);
        Assert.DoesNotContain("internal parser detail", message, StringComparison.Ordinal);
        Assert.True(message.Length <= LegacyCommandFeedbackResponder.MaxFeedbackLength);
    }

    [Fact]
    public async Task ArgumentError_WithoutCommandMetadata_FallsBackToHelpHint()
    {
        var delivery = new RecordingDelivery();
        var responder = CreateResponder(delivery);

        await responder.RespondAsync(
            default,
            new StubCommandContext(),
            new FakeResult(CommandError.ParseFailed, "raw parse detail"));

        Assert.Equal(
            "I couldn't understand those arguments. Try `%help`.",
            Assert.Single(delivery.Messages));
    }

    [Fact]
    public async Task UnmetPrecondition_ReturnsSafeContextFeedbackWithoutRawReason()
    {
        var delivery = new RecordingDelivery();
        var responder = CreateResponder(delivery);

        await responder.RespondAsync(
            default,
            new StubCommandContext(),
            new FakeResult(CommandError.UnmetPrecondition, "database says user 123 lacks secret role 456"));

        var message = Assert.Single(delivery.Messages);
        Assert.Contains("Check your permissions", message, StringComparison.Ordinal);
        Assert.DoesNotContain("database", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("456", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnexpectedFailure_ReturnsOnlyGenericSafeMessage()
    {
        var delivery = new RecordingDelivery();
        var responder = CreateResponder(delivery);

        await responder.RespondAsync(
            default,
            new StubCommandContext(),
            new FakeResult(CommandError.Exception, "Mongo connection string mongodb://secret"));

        var message = Assert.Single(delivery.Messages);
        Assert.Equal("Bean Bot couldn't complete that command. Try again in a moment.", message);
        Assert.DoesNotContain("Mongo", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SuccessfulCommand_DoesNotSendExtraFeedback()
    {
        var delivery = new RecordingDelivery();
        var responder = CreateResponder(delivery);

        await responder.RespondAsync(
            default,
            new StubCommandContext(),
            new FakeResult(null, string.Empty, isSuccess: true));

        Assert.Empty(delivery.Messages);
        Assert.Equal(0, delivery.Attempts);
    }

    [Fact]
    public async Task FeedbackDeliveryFailure_IsWarningOnlyAndDoesNotRecurse()
    {
        var exception = new InvalidOperationException("send failed");
        var delivery = new RecordingDelivery(exception);
        var logger = new RecordingLogger<LegacyCommandFeedbackResponder>();
        var responder = new LegacyCommandFeedbackResponder(delivery, logger);

        await responder.RespondAsync(
            default,
            new StubCommandContext(),
            new FakeResult(CommandError.UnknownCommand, "unknown"));

        Assert.Equal(1, delivery.Attempts);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Same(exception, entry.Exception);
    }

    [Fact]
    public void Feedback_IsBoundedBelowDiscordMessageLimit()
    {
        var feedback = LegacyCommandFeedbackResponder.BoundFeedback(new string('x', 2_000));

        Assert.Equal(LegacyCommandFeedbackResponder.MaxFeedbackLength, feedback.Length);
        Assert.EndsWith("…", feedback, StringComparison.Ordinal);
        Assert.True(feedback.Length < DiscordConfig.MaxMessageSize);
    }

    [Fact]
    public void DiscordDelivery_DisablesMentionsAndUsesBoundedWait()
    {
        Assert.Same(AllowedMentions.None, DiscordLegacyCommandFeedbackDelivery.SafeAllowedMentions);
        Assert.True(DiscordLegacyCommandFeedbackDelivery.SendTimeout > TimeSpan.Zero);
        Assert.True(DiscordLegacyCommandFeedbackDelivery.SendTimeout <= TimeSpan.FromSeconds(30));
    }

    private static LegacyCommandFeedbackResponder CreateResponder(RecordingDelivery delivery)
        => new(delivery, new RecordingLogger<LegacyCommandFeedbackResponder>());

    private static async Task<CommandInfo> GetUsageCommandAsync()
    {
        var commandService = new CommandService();
        await commandService.AddModuleAsync<UsageModule>(null!);
        return Assert.Single(commandService.Commands, command => command.Name == "sample");
    }

    private sealed class UsageModule : ModuleBase<SocketCommandContext>
    {
        [Command("sample")]
        public Task SampleAsync(string text, int count)
            => Task.CompletedTask;
    }

    private sealed class RecordingDelivery : ILegacyCommandFeedbackDelivery
    {
        private readonly Exception? _exception;

        public RecordingDelivery(Exception? exception = null)
        {
            _exception = exception;
        }

        public int Attempts { get; private set; }
        public List<string> Messages { get; } = [];

        public Task SendAsync(ICommandContext context, string message)
        {
            Attempts++;
            if (_exception != null)
            {
                throw _exception;
            }

            Messages.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed record LogEntry(LogLevel Level, Exception? Exception);

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add(new LogEntry(logLevel, exception));
    }

    private sealed class FakeResult : IResult
    {
        public FakeResult(
            CommandError? error,
            string errorReason,
            bool isSuccess = false)
        {
            Error = error;
            ErrorReason = errorReason;
            IsSuccess = isSuccess;
        }

        public CommandError? Error { get; }
        public string ErrorReason { get; }
        public bool IsSuccess { get; }
    }

    private sealed class StubCommandContext : ICommandContext
    {
        public IDiscordClient Client => throw new NotSupportedException();
        public IGuild Guild => throw new NotSupportedException();
        public IMessageChannel Channel => throw new NotSupportedException();
        public IUser User => throw new NotSupportedException();
        public IUserMessage Message => throw new NotSupportedException();
    }
}
