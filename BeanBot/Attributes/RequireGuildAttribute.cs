using System;
using System.Threading.Tasks;
using Discord.Commands;
using Discord.WebSocket;


namespace BeanBot.Attributes
{
    public class RequireGuildAttribute : PreconditionAttribute
    {
        // Override the CheckPermissions method
        public override Task<PreconditionResult> CheckPermissionsAsync(ICommandContext context, CommandInfo command, IServiceProvider services)
        {
            // Check if this user is a Guild User
            if (context.User is SocketGuildUser)
            {
                // Since no async work is done, the result has to be wrapped with `Task.FromResult` to avoid compiler errors
                return Task.FromResult(PreconditionResult.FromSuccess());
            }
            else
                return Task.FromResult(PreconditionResult.FromError("You must be in a guild to run this command."));
        }
    }
}
