using BeanBot.Entities;
using BeanBot.Util;
using Discord;
using MongoDB.Driver;
using Serilog;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace BeanBot.Repository
{
    public class RoleReactRepository
    {
        private readonly IMongoCollection<RoleSettings> _roleSettingsRef = MongoDbClient.beanDatabase.GetCollection<RoleSettings>("roleSettings");
        public async Task InsertNewRoleSettings(RoleSettings roleSettings)
        {
            try
            {
                roleSettings.lastAccessed = DateTime.Now;
                await _roleSettingsRef.InsertOneAsync(roleSettings);
                Log.Information($"Settings successfully created");
            }
            catch (Exception e)
            {
                Log.Error($"Error inserting settings into database: {e.Message}");
            }
        }

        public async Task<List<RoleSettings>> GetAllRoleSettings()
        {
            try
            {
                var results = await _roleSettingsRef.FindAsync(_ => true);
                return await results.ToListAsync();
            }
            catch (Exception e)
            {
                Log.Error($"Error retrieving role settings from the database: {e.Message}");
                return null;
            }
        }

        public async Task<List<RoleSettings>> GetRecentRoleSettings()
        {
            try
            {
                var filterByLastAccessedDate = Builders<RoleSettings>.Filter.Where(result => result.lastAccessed >= DateTime.Now.AddDays(-30));
                var results = await _roleSettingsRef.FindAsync<RoleSettings>(filterByLastAccessedDate);
                return await results.ToListAsync();
            }
            catch (Exception e)
            {
                return null;
            }
        }

        public async Task<RoleSettings> GetRoleSetting(IUserMessage message)
        {
            try
            {
                var filterByMessageId = Builders<RoleSettings>.Filter.Where(doc => doc.messageId == message.Id.ToString());
                var result = await _roleSettingsRef.FindAsync<RoleSettings>(filterByMessageId);
                return result.FirstOrDefault();
            }
            catch (Exception e)
            {
                Log.Error($"Error retrieving role settings from the database: {e.Message}");
                return null;
            }
        }
    }
}
