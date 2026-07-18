using BeanBot.Entities;
using Discord;
using MongoDB.Driver;
using Serilog;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace BeanBot.Repository
{
    public class RoleReactRepository
    {
        private readonly IMongoCollection<RoleSettings> _roleSettings;

        public RoleReactRepository(IMongoDatabase database)
        {
            _roleSettings = (database ?? throw new ArgumentNullException(nameof(database)))
                .GetCollection<RoleSettings>("roleSettings");
        }

        public async Task InsertNewRoleSettings(RoleSettings roleSettings)
        {
            try
            {
                roleSettings.lastAccessed = DateTime.UtcNow;
                await _roleSettings.InsertOneAsync(roleSettings);
                Log.Information("Reaction-role settings successfully created for message {MessageId}", roleSettings.messageId);
            }
            catch (Exception e)
            {
                Log.Error(e, "Error inserting reaction-role settings into the database");
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
                Log.Error(e, "Error retrieving recent reaction-role settings from the database");
                throw;
            }
        }

        public async Task<RoleSettings> GetRoleSetting(IUserMessage message)
        {
            try
            {
                var filterByMessageId = Builders<RoleSettings>.Filter.Where(doc => doc.messageId == message.Id.ToString());
                return await _roleSettings.Find(filterByMessageId).FirstOrDefaultAsync();
            }
            catch (Exception e)
            {
                Log.Error(e, "Error retrieving reaction-role settings for message {MessageId}", message.Id);
                return null;
            }
        }
    }
}
