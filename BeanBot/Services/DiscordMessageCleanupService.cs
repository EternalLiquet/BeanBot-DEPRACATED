using BeanBot.Util;
using Discord;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace BeanBot.Services
{
    public sealed class DiscordMessageCleanupService
    {
        internal const int MaximumBulkDeleteCount = 100;
        private static readonly TimeSpan MaximumBulkDeleteAge = TimeSpan.FromDays(14) - TimeSpan.FromMinutes(5);
        private readonly Func<DateTimeOffset> _getUtcNow;
        private readonly ILogger<DiscordMessageCleanupService> _logger;

        public DiscordMessageCleanupService(ILogger<DiscordMessageCleanupService> logger)
            : this(() => DateTimeOffset.UtcNow, logger)
        {
        }

        internal DiscordMessageCleanupService(
            Func<DateTimeOffset> getUtcNow,
            ILogger<DiscordMessageCleanupService> logger)
        {
            _getUtcNow = getUtcNow ?? throw new ArgumentNullException(nameof(getUtcNow));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public Task DeleteAsync(ITextChannel channel, IReadOnlyCollection<IMessage> messages)
        {
            ArgumentNullException.ThrowIfNull(channel);

            var plan = CreatePlan(messages, message => message.Timestamp, _getUtcNow());
            return ExecutePlanAsync(
                plan,
                batch => channel.DeleteMessagesAsync(batch),
                message => message.DeleteAsync(),
                (exception, itemCount, isBatch) => BeanBotLog.MessageCleanupFailed(
                    _logger,
                    itemCount,
                    isBatch ? "bulk deletion" : "individual deletion",
                    exception));
        }

        internal static MessageCleanupPlan<T> CreatePlan<T>(
            IReadOnlyCollection<T> messages,
            Func<T, DateTimeOffset> getTimestamp,
            DateTimeOffset now)
        {
            ArgumentNullException.ThrowIfNull(messages);
            ArgumentNullException.ThrowIfNull(getTimestamp);

            var oldestBulkDeleteTimestamp = now.Subtract(MaximumBulkDeleteAge);
            var recent = messages.Where(message => getTimestamp(message) >= oldestBulkDeleteTimestamp).ToList();
            var individual = messages.Where(message => getTimestamp(message) < oldestBulkDeleteTimestamp).ToList();
            var batches = new List<IReadOnlyCollection<T>>();

            for (var offset = 0; offset < recent.Count; offset += MaximumBulkDeleteCount)
            {
                var batch = recent.Skip(offset).Take(MaximumBulkDeleteCount).ToList();
                if (batch.Count == 1)
                {
                    individual.Add(batch[0]);
                }
                else if (batch.Count > 1)
                {
                    batches.Add(batch);
                }
            }

            return new MessageCleanupPlan<T>(batches, individual);
        }

        internal static async Task ExecutePlanAsync<T>(
            MessageCleanupPlan<T> plan,
            Func<IReadOnlyCollection<T>, Task> deleteBatch,
            Func<T, Task> deleteIndividual,
            Action<Exception, int, bool> onFailure)
        {
            foreach (var batch in plan.Batches)
            {
                try
                {
                    await deleteBatch(batch);
                }
                catch (Exception exception)
                {
                    onFailure(exception, batch.Count, true);
                }
            }

            foreach (var item in plan.IndividualItems)
            {
                try
                {
                    await deleteIndividual(item);
                }
                catch (Exception exception)
                {
                    onFailure(exception, 1, false);
                }
            }
        }
    }

    internal sealed class MessageCleanupPlan<T>
    {
        public MessageCleanupPlan(
            IReadOnlyCollection<IReadOnlyCollection<T>> batches,
            IReadOnlyCollection<T> individualItems)
        {
            Batches = batches;
            IndividualItems = individualItems;
        }

        public IReadOnlyCollection<IReadOnlyCollection<T>> Batches { get; }
        public IReadOnlyCollection<T> IndividualItems { get; }
    }
}
