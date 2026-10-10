namespace BeanBot.Discord.RoleMenus;

internal static class RoleMenuEditMutationWaiter
{
    /// <summary>Give prompt timeout feedback while retaining interaction ownership of the real task.</summary>
    internal static async Task<RoleMenuEditResult?> WaitAsync(
        Task<RoleMenuEditResult> mutation,
        Func<Task> sendTimeoutFeedback,
        CancellationToken timeoutToken)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        ArgumentNullException.ThrowIfNull(sendTimeoutFeedback);
        try
        {
            return await mutation.WaitAsync(timeoutToken);
        }
        catch (OperationCanceledException) when (timeoutToken.IsCancellationRequested)
        {
            try
            {
                await sendTimeoutFeedback();
            }
            finally
            {
                // The caller's interaction task cannot finish while the mutation holds the
                // menu lock or an external write still runs. The caller observes/logs the result.
                try { await mutation; }
                catch { /* observed by the caller's final settlement */ }
            }
            return null;
        }
    }
}
