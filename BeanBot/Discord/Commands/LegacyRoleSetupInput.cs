using System.Globalization;
using BeanBot.Persistence.Models;

namespace BeanBot.Discord.Commands;

internal static class LegacyRoleSetupInput
{
    internal const int MaximumRoleCount = 25;

    internal readonly record struct RoleChoice(ulong Id, string Name);

    internal sealed record CompletedSetup(List<RoleEmotePair> Pairs, string Label);

    internal enum AnswerStatus
    {
        Accepted,
        Cancelled,
        TimedOut,
        InvalidCountLimit
    }

    internal readonly record struct Answer<T>(AnswerStatus Status, T? Message, int Count = 0)
        where T : class;

    internal static async Task<Answer<T>> ReadNextAsync<T>(
        Func<Task<T?>> waitForMessage,
        Func<T, string> getContent,
        Func<string, Task> sendFeedback)
        where T : class
    {
        var message = await waitForMessage();
        if (message is null)
        {
            await sendFeedback("Time has expired. Please start again.");
            return new Answer<T>(AnswerStatus.TimedOut, null);
        }

        if (string.Equals(getContent(message).Trim(), "cancel", StringComparison.OrdinalIgnoreCase))
        {
            await sendFeedback("Setup cancelled");
            return new Answer<T>(AnswerStatus.Cancelled, message);
        }

        return new Answer<T>(AnswerStatus.Accepted, message);
    }

    internal static async Task<Answer<T>> ReadCountAsync<T>(
        Func<Task<T?>> waitForMessage,
        Func<T, string> getContent,
        Func<string, Task> sendFeedback)
        where T : class
    {
        const int maximumAttempts = 3;
        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            var answer = await ReadNextAsync(waitForMessage, getContent, sendFeedback);
            if (answer.Status != AnswerStatus.Accepted)
            {
                return answer;
            }

            if (int.TryParse(getContent(answer.Message!), out var count) && count is >= 1 and <= MaximumRoleCount)
            {
                return new Answer<T>(AnswerStatus.Accepted, answer.Message, count);
            }

            if (attempt == maximumAttempts)
            {
                await sendFeedback($"Please enter a whole number from 1 to {MaximumRoleCount}. Setup ended; start again with %role setting.");
                return new Answer<T>(AnswerStatus.InvalidCountLimit, answer.Message);
            }

            await sendFeedback($"Please enter a whole number from 1 to {MaximumRoleCount}, or reply cancel.");
        }

        throw new InvalidOperationException("The role count retry limit was not enforced.");
    }

    internal static async Task<CompletedSetup?> RunAsync<T>(
        Func<Task<T?>> waitForMessage,
        Func<T, string> getContent,
        Func<T, Task<RoleChoice?>> resolveRole,
        Func<T, Task<ulong?>> resolveEmote,
        Func<string, Task> sendMessage)
        where T : class
    {
        await sendMessage($"How many roles do you wish to configure? (1-{MaximumRoleCount}) Reply cancel to stop.");
        var countAnswer = await ReadCountAsync(waitForMessage, getContent, sendMessage);
        if (countAnswer.Status != AnswerStatus.Accepted)
        {
            return null;
        }

        var pairs = new List<RoleEmotePair>();
        for (var index = 0; index < countAnswer.Count; index++)
        {
            await sendMessage("Which role would you like to set up? Reply cancel to stop.");
            var roleAnswer = await ReadNextAsync(waitForMessage, getContent, sendMessage);
            if (roleAnswer.Status != AnswerStatus.Accepted)
            {
                return null;
            }

            var role = await resolveRole(roleAnswer.Message!);
            if (role is null)
            {
                return null;
            }

            await sendMessage($"Which emote would you like to set up with the role {role.Value.Name}? Reply cancel to stop.");
            var emoteAnswer = await ReadNextAsync(waitForMessage, getContent, sendMessage);
            if (emoteAnswer.Status != AnswerStatus.Accepted)
            {
                return null;
            }

            var emoteId = await resolveEmote(emoteAnswer.Message!);
            if (emoteId is null)
            {
                return null;
            }

            var roleIdText = role.Value.Id.ToString(CultureInfo.InvariantCulture);
            var emoteIdText = emoteId.Value.ToString(CultureInfo.InvariantCulture);
            if (pairs.Any(pair => pair.RoleId == roleIdText || pair.EmojiId == emoteIdText))
            {
                await sendMessage("That role or emote is already being configured. Please start again.");
                return null;
            }

            pairs.Add(new RoleEmotePair(roleIdText, emoteIdText));
        }

        await sendMessage("Please label this group of roles (i.e. Games, Position, NSFW, etc). Reply cancel to stop.");
        var labelAnswer = await ReadNextAsync(waitForMessage, getContent, sendMessage);
        return labelAnswer.Status == AnswerStatus.Accepted
            ? new CompletedSetup(pairs, getContent(labelAnswer.Message!))
            : null;
    }
}
