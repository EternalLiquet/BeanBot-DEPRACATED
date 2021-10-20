using System;
using System.Collections.Generic;
using System.Text;

namespace BeanBot.Entities
{
    public class RoleEmotePair
    {
        public string roleId { get; set; }
        public string emojiId { get; set; }

        public RoleEmotePair(string roleId, string emojiId)
        {
            this.roleId = roleId;
            this.emojiId = emojiId;
        }
    }
}
