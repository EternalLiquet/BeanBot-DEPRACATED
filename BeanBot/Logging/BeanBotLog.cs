using System.Net;
using Discord;
using Microsoft.Extensions.Logging;

namespace BeanBot.Logging;

internal static partial class BeanBotLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Starting BeanBot. Version={Version}, CommitSha={CommitSha}")]
    internal static partial void ApplicationStarting(ILogger logger, string version, string commitSha);

    [LoggerMessage(Level = LogLevel.Information, Message = "Instantiating Command Handler")]
    internal static partial void CommandHandlerCreated(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Installing Commands")]
    internal static partial void CommandsInstalling(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Initializing New Member Handler")]
    internal static partial void NewMemberHandlerInitializing(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Sent the welcome message to {UserId}")]
    internal static partial void WelcomeMessageSent(ILogger logger, ulong userId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not send the welcome message to {UserId}")]
    internal static partial void WelcomeMessageFailed(ILogger logger, ulong userId, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Initializing Daily Pun Posting Service")]
    internal static partial void PunServiceInitializing(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Next pun scheduled for {NextLocal} Chicago ({NextUtc} UTC). Now: {NowLocal} Chicago")]
    internal static partial void PunScheduled(ILogger logger, string nextLocal, string nextUtc, string nowLocal);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Shutting down pun service")]
    internal static partial void PunServiceShuttingDown(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error in PunHandler loop; retrying in 30s")]
    internal static partial void PunLoopFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Posting daily pun at {LocalTime} Chicago")]
    internal static partial void PunPosting(ILogger logger, DateTimeOffset localTime);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not find general channel with ID {ChannelId} to post daily pun")]
    internal static partial void PunChannelMissing(ILogger logger, ulong channelId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Loaded {PunCount} puns from {ResourcePath}")]
    internal static partial void PunResourceLoaded(ILogger logger, int punCount, string resourcePath);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No usable puns were found in {ResourcePath}")]
    internal static partial void PunResourceEmpty(ILogger logger, string resourcePath);

    [LoggerMessage(Level = LogLevel.Error, Message = "Pun resource file was not found at {ResourcePath}")]
    internal static partial void PunResourceMissing(ILogger logger, string resourcePath);

    [LoggerMessage(Level = LogLevel.Error, Message = "Pun resource file at {ResourcePath} could not be loaded")]
    internal static partial void PunResourceInvalid(ILogger logger, string resourcePath, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error occurred while posting daily pun")]
    internal static partial void PunPostingFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Instantiating React Handler")]
    internal static partial void ReactHandlerCreated(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Instantiating Role Services")]
    internal static partial void RoleServicesCreated(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipping Discord stop/logout and client disposal because a startup lifecycle operation is still running; process exit will reclaim it")]
    internal static partial void DiscordStopSkipped(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "BeanBot cleanup failed after application startup did not complete")]
    internal static partial void StartupCleanupFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "BeanBot shutdown stage {ShutdownStage} failed; continuing safe cleanup")]
    internal static partial void ShutdownStageFailed(ILogger logger, string shutdownStage, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "BeanBot shutdown stage {ShutdownStage} failed after its bounded wait ended")]
    internal static partial void ShutdownStageLateFailure(ILogger logger, string shutdownStage, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "BeanBot shutdown stage {ShutdownStage} observed host cancellation; continuing non-blocking cleanup")]
    internal static partial void ShutdownStageCanceled(ILogger logger, string shutdownStage);

    [LoggerMessage(Level = LogLevel.Information, Message = "Skipping BeanBot shutdown stage {ShutdownStage} because the host shutdown deadline elapsed")]
    internal static partial void ShutdownStageSkipped(ILogger logger, string shutdownStage);

    [LoggerMessage(Level = LogLevel.Information, Message = "Instantiating Command Services")]
    internal static partial void CommandServicesCreated(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipping Discord {Operation} operation because the host shutdown deadline elapsed")]
    internal static partial void DiscordShutdownOperationSkipped(ILogger logger, string operation);

    [LoggerMessage(Level = LogLevel.Error, Message = "Discord {Operation} operation failed after its shutdown wait ended")]
    internal static partial void DiscordShutdownLateFailure(ILogger logger, string operation, Exception? exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Discord {Operation} operation did not complete during bounded shutdown")]
    internal static partial void DiscordShutdownOperationFailed(ILogger logger, string operation, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "BeanBot Discord gateway reached Ready. LoginState={LoginState}, ConnectionState={ConnectionState}")]
    internal static partial void DiscordReady(ILogger logger, LoginState loginState, ConnectionState connectionState);

    [LoggerMessage(Level = LogLevel.Error, Message = "Discord reached Ready, but the persisted outage recovery notification could not be processed")]
    internal static partial void OutageRecoveryProcessingFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "BeanBot disconnected from Discord. LoginState={LoginState}, ConnectionState={ConnectionState}, MostRecentDisconnectReason={MostRecentDisconnectReason}")]
    internal static partial void DiscordDisconnected(ILogger logger, string loginState, string connectionState, string? mostRecentDisconnectReason, Exception? exception = null);

    [LoggerMessage(Level = LogLevel.Critical, Message = "An unhandled application exception occurred")]
    internal static partial void UnhandledApplicationException(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Critical, Message = "An unhandled non-Exception error occurred: {Error}")]
    internal static partial void UnhandledApplicationError(ILogger logger, object error);

    [LoggerMessage(Level = LogLevel.Error, Message = "An unobserved task exception occurred")]
    internal static partial void UnobservedTaskException(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not delete incomplete reaction-role message after setup failed")]
    internal static partial void IncompleteReactionRoleCleanupFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not clean up the reaction-role setup messages")]
    internal static partial void ReactionRoleCleanupFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not delete the source message for echo command {MessageId}")]
    internal static partial void EchoSourceDeleteFailed(ILogger logger, ulong messageId, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "The meme API request failed")]
    internal static partial void MemeApiFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not attach the invalid-question Gordon GIF")]
    internal static partial void GordonAttachmentFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to download an image from {ImageUrl}")]
    internal static partial void ImageDownloadFailed(ILogger logger, Uri imageUrl, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Reaction-role settings successfully created for message {MessageId}")]
    internal static partial void ReactionRoleSettingsCreated(ILogger logger, string messageId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not delete {MessageCount} setup message(s) using {DeleteMode}; continuing cleanup")]
    internal static partial void MessageCleanupFailed(ILogger logger, int messageCount, string deleteMode, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Health check endpoint listening on {BindAddress}:{Port}{Path} with a {RateLimitSeconds}s per-client poll limit")]
    internal static partial void HealthEndpointListening(ILogger logger, IPAddress bindAddress, int port, string path, int rateLimitSeconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Health check endpoint is listening without a bearer token on {BindAddress}:{Port}{Path}")]
    internal static partial void HealthEndpointUnauthenticated(ILogger logger, IPAddress bindAddress, int port, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Health check endpoint exceeded its {ShutdownTimeoutSeconds}s shutdown timeout; aborting remaining requests")]
    internal static partial void HealthEndpointShutdownTimedOut(ILogger logger, double shutdownTimeoutSeconds);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Could not find a cached fortune response for edited message {MessageId}")]
    internal static partial void FortuneResponseMissing(ILogger logger, ulong messageId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Attempt {Attempt} to replace an edited fortune response failed")]
    internal static partial void FortuneResponseReplaceAttemptFailed(ILogger logger, int attempt, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not replace the response to edited fortune message {MessageId}")]
    internal static partial void FortuneResponseReplaceFailed(ILogger logger, ulong messageId, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to {Action} a reaction role for message {MessageId}")]
    internal static partial void ReactionRoleActionFailed(ILogger logger, string action, ulong messageId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Timed out draining {InFlightReactionOperationCount} reaction-role operation(s); leaving the cache lock for process exit")]
    internal static partial void ReactionRoleDrainTimedOut(ILogger logger, int inFlightReactionOperationCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A reaction-role operation failed while shutdown was draining in-flight work")]
    internal static partial void ReactionRoleShutdownOperationFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Discord {Operation} operation failed after its startup wait ended")]
    internal static partial void DiscordStartupLateFailure(ILogger logger, string operation, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Discord started, but the initial presence could not be set")]
    internal static partial void DiscordPresenceFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Attempting Discord login. Attempt={Attempt}, MaximumAttempts={MaximumAttempts}")]
    internal static partial void DiscordLoginAttempting(ILogger logger, int attempt, int maximumAttempts);

    [LoggerMessage(Level = LogLevel.Information, Message = "Discord login succeeded. Attempt={Attempt}, MaximumAttempts={MaximumAttempts}")]
    internal static partial void DiscordLoginSucceeded(ILogger logger, int attempt, int maximumAttempts);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Discord rejected the configured bot token. Update BEANBOT_BOT_TOKEN and restart the process. Attempt={Attempt}, MaximumAttempts={MaximumAttempts}")]
    internal static partial void DiscordTokenRejected(ILogger logger, int attempt, int maximumAttempts, Exception exception);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Discord login failed after all startup attempts. Attempt={Attempt}, MaximumAttempts={MaximumAttempts}")]
    internal static partial void DiscordLoginExhausted(ILogger logger, int attempt, int maximumAttempts, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Discord login attempt failed; delaying before retry. Attempt={Attempt}, MaximumAttempts={MaximumAttempts}, RetryDelay={RetryDelay}")]
    internal static partial void DiscordLoginRetrying(ILogger logger, int attempt, int maximumAttempts, TimeSpan retryDelay, Exception exception);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Discord startup failed with a non-retryable login error. Attempt={Attempt}, MaximumAttempts={MaximumAttempts}")]
    internal static partial void DiscordLoginFailed(ILogger logger, int attempt, int maximumAttempts, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Attempting Discord outage recovery notification. DisconnectedAtUtc={DisconnectedAtUtc}, ProcessRestartRequested={ProcessRestartRequested}")]
    internal static partial void OutageNotificationAttempting(ILogger logger, DateTimeOffset disconnectedAtUtc, bool processRestartRequested);

    [LoggerMessage(Level = LogLevel.Error, Message = "Discord outage recovery notification failed after {DeliveryAttempts} attempts; persisted outage retained. DisconnectedAtUtc={DisconnectedAtUtc}")]
    internal static partial void OutageNotificationFailed(ILogger logger, int deliveryAttempts, DateTimeOffset disconnectedAtUtc);

    [LoggerMessage(Level = LogLevel.Information, Message = "Discord outage recovery notification delivered. DisconnectedAtUtc={DisconnectedAtUtc}")]
    internal static partial void OutageNotificationDelivered(ILogger logger, DateTimeOffset disconnectedAtUtc);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Discord outage recovery notification delivery attempt failed. DeliveryAttempt={DeliveryAttempt}, MaximumDeliveryAttempts={MaximumDeliveryAttempts}")]
    internal static partial void OutageNotificationDeliveryFailed(ILogger logger, int deliveryAttempt, int maximumDeliveryAttempts, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Discord outage manual recovery attempt persisted. DisconnectedAtUtc={DisconnectedAtUtc}")]
    internal static partial void OutageManualRecoveryPersisted(ILogger logger, DateTimeOffset disconnectedAtUtc);

    [LoggerMessage(Level = LogLevel.Information, Message = "Discord outage process restart request persisted")]
    internal static partial void OutageRestartPersisted(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Persisted Discord outage cleared")]
    internal static partial void OutageCleared(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Persisted Discord outage loaded. DisconnectedAtUtc={DisconnectedAtUtc}, ManualRecoveryAttempted={ManualRecoveryAttempted}, ProcessRestartRequested={ProcessRestartRequested}")]
    internal static partial void OutageLoaded(ILogger logger, DateTimeOffset disconnectedAtUtc, bool manualRecoveryAttempted, bool processRestartRequested);

    [LoggerMessage(Level = LogLevel.Information, Message = "Meaningful Discord outage persisted. DisconnectedAtUtc={DisconnectedAtUtc}, ManualRecoveryAttempted={ManualRecoveryAttempted}, ProcessRestartRequested={ProcessRestartRequested}")]
    internal static partial void OutagePersisted(ILogger logger, DateTimeOffset disconnectedAtUtc, bool manualRecoveryAttempted, bool processRestartRequested);

    [LoggerMessage(Level = LogLevel.Error, Message = "Corrupted Discord outage state encountered and quarantined. QuarantinePath={QuarantinePath}")]
    internal static partial void OutageQuarantined(ILogger logger, string quarantinePath, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Corrupted Discord outage state encountered but could not be quarantined. OutageFilePath={OutageFilePath}")]
    internal static partial void OutageQuarantineFailed(ILogger logger, string outageFilePath, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not process Discord paginator reaction for message {MessageId}")]
    internal static partial void PaginatorReactionFailed(ILogger logger, ulong messageId, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Could not remove expired paginator control from message {MessageId}")]
    internal static partial void PaginatorControlRemoveFailed(ILogger logger, ulong messageId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Discord paginator expiration failed for message {MessageId}")]
    internal static partial void PaginatorExpirationFailed(ILogger logger, ulong messageId, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Could not remove paginator reaction from user {UserId}")]
    internal static partial void PaginatorUserReactionRemoveFailed(ILogger logger, ulong userId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Discord paginator shutdown exceeded {Timeout}; cleanup will finish in the background")]
    internal static partial void PaginatorShutdownTimedOut(ILogger logger, TimeSpan timeout);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Discord paginator shutdown encountered a cleanup failure")]
    internal static partial void PaginatorShutdownFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Deferred Discord paginator cleanup failed")]
    internal static partial void PaginatorDeferredCleanupFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Discord gateway {Operation} operation failed after its recovery wait ended")]
    internal static partial void DiscordRecoveryLateFailure(ILogger logger, string operation, Exception? exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Discord gateway recovery grace period started. LoginState={LoginState}, ConnectionState={ConnectionState}, GracePeriod={GracePeriod}, MostRecentDisconnectReason={MostRecentDisconnectReason}")]
    internal static partial void DiscordRecoveryGraceStarted(ILogger logger, string loginState, string connectionState, TimeSpan gracePeriod, string? mostRecentDisconnectReason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Discord gateway remained unhealthy for {UnhealthyDuration}; beginning one manual reconnect cycle. LoginState={LoginState}, ConnectionState={ConnectionState}, MostRecentDisconnectReason={MostRecentDisconnectReason}")]
    internal static partial void DiscordManualRecoveryStarting(ILogger logger, TimeSpan unhealthyDuration, string loginState, string connectionState, string? mostRecentDisconnectReason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Discord manual reconnect cycle failed. LoginState={LoginState}, ConnectionState={ConnectionState}, MostRecentDisconnectReason={MostRecentDisconnectReason}")]
    internal static partial void DiscordManualRecoveryFailed(ILogger logger, string loginState, string connectionState, string? mostRecentDisconnectReason, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Discord manual reconnect succeeded after {UnhealthyDuration}. LoginState={LoginState}, ConnectionState={ConnectionState}")]
    internal static partial void DiscordManualRecoverySucceeded(ILogger logger, TimeSpan unhealthyDuration, string loginState, string connectionState);

    [LoggerMessage(Level = LogLevel.Error, Message = "Discord manual reconnect failed to reach Ready after {UnhealthyDuration}. LoginState={LoginState}, ConnectionState={ConnectionState}, MostRecentDisconnectReason={MostRecentDisconnectReason}")]
    internal static partial void DiscordManualRecoveryNotReady(ILogger logger, TimeSpan unhealthyDuration, string loginState, string connectionState, string? mostRecentDisconnectReason);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Discord gateway recovery was exhausted; exiting with code 1 so Docker can restart BeanBot")]
    internal static partial void DiscordRecoveryExhausted(ILogger logger);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Discord gateway recovery monitor failed unexpectedly")]
    internal static partial void DiscordRecoveryMonitorFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Exiting with code 1 because the Discord gateway recovery monitor cannot continue safely")]
    internal static partial void DiscordRecoveryMonitorExiting(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not persist the meaningful Discord outage before manual recovery. DisconnectedAtUtc={DisconnectedAtUtc}")]
    internal static partial void DiscordOutagePersistBeforeRecoveryFailed(ILogger logger, DateTimeOffset disconnectedAtUtc, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not persist the Discord outage restart request before process exit")]
    internal static partial void DiscordOutageRestartPersistFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Discord gateway recovered naturally after {UnhealthyDuration}; manual reconnect was not needed. LoginState={LoginState}, ConnectionState={ConnectionState}")]
    internal static partial void DiscordNaturalRecovery(ILogger logger, TimeSpan unhealthyDuration, string loginState, string connectionState);

    [LoggerMessage(Message = "{DiscordMessage}")]
    internal static partial void DiscordMessage(ILogger logger, LogLevel level, string discordMessage, Exception? exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Discord log ({Severity}): {DiscordMessage}")]
    internal static partial void DiscordMessageFallback(ILogger logger, LogSeverity severity, string discordMessage, Exception? exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Discord user {Username} ({UserId}) joined {Guild}")]
    internal static partial void DiscordUserJoined(ILogger logger, string username, ulong userId, object guild);

    [LoggerMessage(Level = LogLevel.Information, Message = "Discord command {CommandName} was executed")]
    internal static partial void DiscordCommandExecuted(ILogger logger, string commandName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Discord command {CommandName} was rejected with {Error}: {Reason}")]
    internal static partial void DiscordCommandRejected(ILogger logger, string commandName, object? error, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Discord command {CommandName} failed with {Error}: {Reason}")]
    internal static partial void DiscordCommandFailed(ILogger logger, string commandName, object? error, string reason);
}
