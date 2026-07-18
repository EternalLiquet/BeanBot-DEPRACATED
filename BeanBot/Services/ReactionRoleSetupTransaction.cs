using System;
using System.Threading.Tasks;

namespace BeanBot.Services
{
    internal static class ReactionRoleSetupTransaction
    {
        public static async Task<TMessage> ExecuteAsync<TMessage>(
            Func<Task<TMessage>> createMessage,
            Func<TMessage, Task> configureAndPersist,
            Func<TMessage, Task> deleteMessage,
            Action<Exception> onCompensationFailure)
            where TMessage : class
        {
            TMessage message = null;
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
}
