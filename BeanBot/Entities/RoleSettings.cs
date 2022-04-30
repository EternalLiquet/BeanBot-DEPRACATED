using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace BeanBot.Entities
{
    public class RoleSettings
    {
        [BsonId]
        public ObjectId id;
        public List<RoleEmotePair> roleEmotePair;
        public string guildId;
        public string channelId;
        public string messageId;
        public DateTime lastAccessed;

        public RoleSettings() { }

        public RoleSettings(List<RoleEmotePair> roleEmotePair, string guildId, string channelId, string messageId)
        {
            this.roleEmotePair = roleEmotePair;
            this.guildId = guildId;
            this.channelId = channelId;
            this.messageId = messageId;
        }

        public override string ToString()
        {
            return JsonConvert.SerializeObject(this, Formatting.Indented);
        }
    }
}
