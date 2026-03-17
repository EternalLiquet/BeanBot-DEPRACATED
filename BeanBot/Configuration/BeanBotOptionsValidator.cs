using Microsoft.Extensions.Options;

namespace BeanBot.Configuration;

public sealed class BeanBotOptionsValidator : IValidateOptions<BeanBotOptions>
{
    public ValidateOptionsResult Validate(string? name, BeanBotOptions options)
    {
        List<string> errors = [];

        if (string.IsNullOrWhiteSpace(options.BotToken))
        {
            errors.Add("BEANBOT_BOT_TOKEN is required.");
        }

        if (string.IsNullOrWhiteSpace(options.MongoConnectionString))
        {
            errors.Add("BEANBOT_MONGO_CONNECTION_STRING is required.");
        }

        if (options.GeneralChannelId == 0)
        {
            errors.Add("BEANBOT_GENERAL_CHANNEL_ID must be a valid Discord channel ID.");
        }

        if (!Uri.TryCreate(options.HatoeteUrl, UriKind.Absolute, out _))
        {
            errors.Add("BEANBOT_HATOETE_URL must be a valid absolute URL.");
        }

        if (!Uri.TryCreate(options.YoshimaruUrl, UriKind.Absolute, out _))
        {
            errors.Add("BEANBOT_YOSHIMARU_URL must be a valid absolute URL.");
        }

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }
}
