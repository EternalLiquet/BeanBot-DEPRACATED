namespace BeanBot.Discord.ReactionRoles;

internal static class ReactionRoleSetupTransaction
{
    internal static async Task<ReactionRoleAssignabilityStatus> ExecuteIfAssignableAsync<TMessage>(
        Func<ReactionRoleAssignabilityStatus> validate,
        Func<Task<TMessage>> createMessage,
        Func<TMessage, Task> configureAndPersist,
        Func<TMessage, Task> deleteMessage,
        Action<Exception> onCompensationFailure)
        where TMessage : class
    {
        var status = validate();
        if (status == ReactionRoleAssignabilityStatus.Allowed)
        {
            await ExecuteAsync(createMessage, configureAndPersist, deleteMessage, onCompensationFailure);
        }

        return status;
    }

    public static async Task<TMessage> ExecuteAsync<TMessage>(
        Func<Task<TMessage>> createMessage,
        Func<TMessage, Task> configureAndPersist,
        Func<TMessage, Task> deleteMessage,
        Action<Exception> onCompensationFailure)
        where TMessage : class
    {
        TMessage? message = null;
        try
        {
            message = await createMessage();
            await configureAndPersist(message);
            return message;
        }
        catch
        {
            if (message != null)
            {
                try
                {
                    await deleteMessage(message);
                }
                catch (Exception compensationException)
                {
                    onCompensationFailure(compensationException);
                }
            }

            throw;
        }
    }
}
