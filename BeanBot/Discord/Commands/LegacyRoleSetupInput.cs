namespace BeanBot.Discord.Commands;

internal static class LegacyRoleSetupInput
{
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

            if (int.TryParse(getContent(answer.Message!), out var count) && count is >= 1 and <= 25)
            {
                return new Answer<T>(AnswerStatus.Accepted, answer.Message, count);
            }

            if (attempt == maximumAttempts)
            {
                await sendFeedback("Please enter a whole number from 1 to 25. Setup ended; start again with %role setting.");
                return new Answer<T>(AnswerStatus.InvalidCountLimit, answer.Message);
            }

            await sendFeedback("Please enter a whole number from 1 to 25, or reply cancel.");
        }

        throw new InvalidOperationException("The role count retry limit was not enforced.");
    }
}
