using BeanBot.Entities;

namespace BeanBot.Repository;

public interface IRoleReactRepository
{
    Task InsertNewRoleSettingsAsync(RoleSettings roleSettings, CancellationToken cancellationToken = default);

    Task<RoleSettings?> GetRoleSettingAsync(ulong messageId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RoleSettings>> GetRecentRoleSettingsAsync(CancellationToken cancellationToken = default);
}
