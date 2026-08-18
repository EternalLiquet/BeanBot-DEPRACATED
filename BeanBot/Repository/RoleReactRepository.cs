using BeanBot.Entities;
using BeanBot.Util;
using Discord;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;

namespace BeanBot.Repository
{
    public class RoleReactRepository
    {
        private readonly IMongoCollection<RoleSettings> _roleSettings;
        private readonly ILogger<RoleReactRepository> _logger;

        public RoleReactRepository(
            IMongoDatabase database,
            ILogger<RoleReactRepository> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _roleSettings = (database ?? throw new ArgumentNullException(nameof(database)))
                .GetCollection<RoleSettings>("roleSettings");
        }

        public async Task InsertNewRoleSettings(RoleSettings roleSettings)
        {
            try
            {
                roleSettings.lastAccessed = DateTime.UtcNow;
                await _roleSettings.InsertOneAsync(roleSettings);
                BeanBotLog.ReactionRoleSettingsCreated(_logger, roleSettings.messageId);
            }
            catch (Exception e)
            {
                BeanBotLog.ReactionRoleInsertFailed(_logger, e);
                throw;
            }
        }

        public async Task<List<RoleSettings>> GetRecentRoleSettings()
        {
            try
            {
                var filterByLastAccessedDate = Builders<RoleSettings>.Filter.Where(result => result.lastAccessed >= DateTime.UtcNow.AddDays(-30));
                var results = await _roleSettings.FindAsync<RoleSettings>(filterByLastAccessedDate);
                return await results.ToListAsync();
            }
            catch (Exception e)
            {
                BeanBotLog.RecentReactionRoleReadFailed(_logger, e);
                throw;
            }
        }

        public async Task<RoleSettings?> GetRoleSetting(IUserMessage message)
        {
            try
            {
                var messageId = message.Id.ToString(CultureInfo.InvariantCulture);
                var filterByMessageId = Builders<RoleSettings>.Filter.Where(doc => doc.messageId == messageId);
                return await _roleSettings.Find(filterByMessageId).FirstOrDefaultAsync();
            }
            catch (Exception e)
            {
                BeanBotLog.ReactionRoleReadFailed(_logger, message.Id, e);
                return null;
            }
        }
    }
}
