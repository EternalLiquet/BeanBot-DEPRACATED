namespace BeanBot.Discord.RoleMenus;

internal static class RoleMenuConstants
{
    internal const int MaximumRoles = 25;
    internal const int MaximumListedMenus = 25;
    internal const int MaximumTitleLength = 100;
    internal const int MaximumDescriptionLength = 1000;
    internal const int MaximumDrafts = 64;
    internal const int PanelReconciliationSearchLimit = 100;
    internal const int MaximumResponseContentLength = 2000;
    internal static readonly TimeSpan DraftLifetime = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan InteractionOperationTimeout = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan InteractionFeedbackTimeout = TimeSpan.FromSeconds(3);
    internal static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(1);
}
