using BeanBot.Discord.RoleMenus;
using BeanBot.Persistence.Models;
using Xunit;

namespace BeanBot.Tests.Discord.RoleMenus;

public class RoleMenuPresentationTests
{
    private static readonly IReadOnlyDictionary<ulong, string> RoleNames =
        new Dictionary<ulong, string>
        {
            [1UL] = "Gamer",
            [2UL] = "Reader",
            [3UL] = "Artist",
            [4UL] = "Test Role"
        };

    private static readonly string[] InternalTerms =
    [
        "Bean Bot rechecked",
        "Discord's current role state",
        "reconciliation",
        "configuration",
        "MongoDB",
        "committed",
        "orphaned",
        "panel"
    ];

    [Fact]
    public void FormatReconciliation_SingleRemoval_SaysOnlyWhatChanged()
    {
        var content = Format(removed: [4UL]);

        Assert.Equal("Removed Test Role.", content);
    }

    [Fact]
    public void FormatReconciliation_SingleAddition_SaysOnlyWhatChanged()
    {
        var content = Format(added: [1UL]);

        Assert.Equal("Added Gamer.", content);
    }

    [Fact]
    public void FormatReconciliation_TwoRoles_JoinsWithAnd()
    {
        var content = Format(added: [1UL, 2UL]);

        Assert.Equal("Added Gamer and Reader.", content);
    }

    [Fact]
    public void FormatReconciliation_ThreeRoles_UsesReadableList()
    {
        var content = Format(added: [1UL, 2UL, 3UL], removed: [4UL]);

        Assert.Equal("Added Gamer, Reader, and Artist. Removed Test Role.", content);
    }

    [Fact]
    public void FormatReconciliation_NoChangeWithEmptySelection_SaysMemberHasNoMenuRoles()
    {
        var content = Format();

        Assert.Equal("You don't have any roles from this menu.", content);
    }

    [Fact]
    public void FormatReconciliation_NoChangeWithKeptRoles_NamesRolesAlreadyHeld()
    {
        var content = Format(unchanged: [1UL, 2UL]);

        Assert.Equal("You already have Gamer and Reader.", content);
    }

    [Fact]
    public void FormatReconciliation_PartialResult_ReportsOnlyConfirmedOutcomesAndNextStep()
    {
        var content = Format(added: [1UL], stillAssigned: [2UL]);

        Assert.Equal(
            "Added Gamer. I couldn't remove Reader. Open the menu again to check your roles " +
            "before trying again.",
            content);
    }

    [Fact]
    public void FormatReconciliation_MissingSelection_ReportsFailedAddWithoutClaimingSuccess()
    {
        var content = Format(missing: [3UL]);

        Assert.Equal(
            "I couldn't add Artist. Open the menu again to check your roles before trying again.",
            content);
        Assert.DoesNotContain("Added", content, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatReconciliation_UnknownRoleName_StillReadsNaturally()
    {
        var content = RoleMenuPresentation.FormatReconciliation(
            new RoleMenuSelectionReconciliation([99UL], [], [], [], []),
            RoleNames);

        Assert.Equal("Added an unknown role.", content);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FormatReconciliation_NeverIncludesStateRecheckNarration(bool complete)
    {
        var content = complete
            ? Format(added: [1UL], removed: [2UL])
            : Format(missing: [1UL], stillAssigned: [2UL]);

        AssertNoInternalTerms(content);
        Assert.DoesNotContain("No roles outside this menu", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_TracksSelectedRolesThatWereAlreadyHeld()
    {
        var result = RoleMenuSelectionReconciler.Create(
            [1UL, 2UL, 3UL],
            [1UL, 2UL],
            [1UL, 99UL],
            [1UL, 2UL, 99UL]);

        Assert.Equal([1UL], result.UnchangedSelectedRoleIds);
        Assert.Equal([2UL], result.AddedRoleIds);
    }

    [Fact]
    public void FormatDeletion_FullSuccess_IsShortAndPlain()
    {
        var content = RoleMenuPresentation.FormatDeletion(new RoleMenuDeletionResult(
            RoleMenuConfigurationDeletionStatus.Deleted,
            RoleMenuPanelDeletionStatus.DeletedOrMissing));

        Assert.Equal("Role menu deleted.", content);
    }

    [Fact]
    public void FormatDeletion_SavedSettingsKept_TellsAdministratorHowToFinish()
    {
        var content = RoleMenuPresentation.FormatDeletion(new RoleMenuDeletionResult(
            RoleMenuConfigurationDeletionStatus.Kept,
            RoleMenuPanelDeletionStatus.DeletedOrMissing));

        Assert.Equal(
            "The menu message is gone, but I couldn't remove its saved settings. Run " +
            "`/role-menu delete` again to finish.",
            content);
    }

    [Theory]
    [InlineData((int)RoleMenuConfigurationDeletionStatus.Deleted, (int)RoleMenuPanelDeletionStatus.Failed)]
    [InlineData((int)RoleMenuConfigurationDeletionStatus.Deleted, (int)RoleMenuPanelDeletionStatus.OutcomeUnknown)]
    [InlineData((int)RoleMenuConfigurationDeletionStatus.Kept, (int)RoleMenuPanelDeletionStatus.UnexpectedMessage)]
    [InlineData((int)RoleMenuConfigurationDeletionStatus.OutcomeUnknown, (int)RoleMenuPanelDeletionStatus.UnexpectedMessage)]
    [InlineData((int)RoleMenuConfigurationDeletionStatus.OutcomeUnknown, (int)RoleMenuPanelDeletionStatus.DeletedOrMissing)]
    public void FormatDeletion_IncompleteOutcomes_NeverClaimSuccessAndGiveNextStep(
        int configurationStatus,
        int panelStatus)
    {
        var content = RoleMenuPresentation.FormatDeletion(new RoleMenuDeletionResult(
            (RoleMenuConfigurationDeletionStatus)configurationStatus,
            (RoleMenuPanelDeletionStatus)panelStatus));

        Assert.NotEqual("Role menu deleted.", content);
        Assert.Contains("`/role-menu delete` again", content, StringComparison.Ordinal);
        AssertNoInternalTerms(content);
    }

    [Fact]
    public void FormatDeletion_UnexpectedMessageWithSettingsRemoved_SaysMessageWasLeftAlone()
    {
        var content = RoleMenuPresentation.FormatDeletion(new RoleMenuDeletionResult(
            RoleMenuConfigurationDeletionStatus.Deleted,
            RoleMenuPanelDeletionStatus.UnexpectedMessage));

        Assert.Contains("I removed the menu's saved settings", content, StringComparison.Ordinal);
        Assert.Contains("left it alone", content, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatDeletion_AuthorizationDenied_ExplainsPermission()
    {
        var content = RoleMenuPresentation.FormatDeletion(new RoleMenuDeletionResult(
            RoleMenuConfigurationDeletionStatus.Kept,
            RoleMenuPanelDeletionStatus.DeletedOrMissing,
            authorizationDenied: true));

        Assert.Contains("**Manage Roles**", content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData((int)RoleMenuPublicationStatus.PanelOutcomeUnknown)]
    [InlineData((int)RoleMenuPublicationStatus.PersistenceAbsentRollbackFailed)]
    [InlineData((int)RoleMenuPublicationStatus.PersistenceOutcomeUnknown)]
    public void FormatTerminalPublication_UsesPlainLanguageAndWarnsAboutDuplicates(
        int status)
    {
        var content = RoleMenuPresentation.FormatTerminalPublication(
            (RoleMenuPublicationStatus)status);

        Assert.Contains("`/role-menu create` again", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Your role menu is ready", content, StringComparison.Ordinal);
        AssertNoInternalTerms(content);
    }

    [Theory]
    [InlineData((int)RoleMenuRoleIssueKind.BotMissingManageRoles, "I need the **Manage Roles** permission")]
    [InlineData((int)RoleMenuRoleIssueKind.Duplicate, "You picked **Gamer** more than once.")]
    [InlineData((int)RoleMenuRoleIssueKind.Managed, "I can't assign **Gamer** because Discord or an integration")]
    [InlineData((int)RoleMenuRoleIssueKind.BotHierarchy, "Move my role above it, then try again.")]
    [InlineData((int)RoleMenuRoleIssueKind.AdministratorHierarchy, "You can't add **Gamer**")]
    public void FormatRoleValidationFailure_NamesTheRoleAndNextStep(
        int kind,
        string expectedText)
    {
        var content = RoleMenuPresentation.FormatRoleValidationFailure(
            new RoleMenuRoleValidationResult(
                [],
                [new RoleMenuRoleIssue(1UL, "Gamer", (RoleMenuRoleIssueKind)kind)]));

        Assert.Contains(expectedText, content, StringComparison.Ordinal);
        Assert.DoesNotContain("Bean Bot", content, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatRoleValidationFailure_UnnamedRole_StillReadsNaturally()
    {
        var content = RoleMenuPresentation.FormatRoleValidationFailure(
            new RoleMenuRoleValidationResult(
                [],
                [new RoleMenuRoleIssue(null, null, RoleMenuRoleIssueKind.BotHierarchy)]));

        Assert.StartsWith("I can't assign one of those roles because", content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(RoleMenuSelectionMode.Multiple, false, "Choose the roles you want. Your changes apply right away.")]
    [InlineData(
        RoleMenuSelectionMode.Exclusive,
        false,
        "Choose one role. Picking a different one replaces the one you have. Your changes apply right away.")]
    [InlineData(
        RoleMenuSelectionMode.Exclusive,
        true,
        "You have more than one role from this menu. Choose one to keep, or remove them all. " +
        "Your changes apply right away.")]
    public void FormatSelectorInstructions_ExplainsModeAndImmediateChanges(
        RoleMenuSelectionMode selectionMode,
        bool hadConflict,
        string expected)
    {
        Assert.Equal(
            expected,
            RoleMenuMemberService.FormatSelectorInstructions(selectionMode, hadConflict));
    }

    private static string Format(
        IReadOnlyList<ulong>? added = null,
        IReadOnlyList<ulong>? removed = null,
        IReadOnlyList<ulong>? missing = null,
        IReadOnlyList<ulong>? stillAssigned = null,
        IReadOnlyList<ulong>? unchanged = null)
        => RoleMenuPresentation.FormatReconciliation(
            new RoleMenuSelectionReconciliation(
                added ?? [],
                removed ?? [],
                missing ?? [],
                stillAssigned ?? [],
                unchanged ?? []),
            RoleNames);

    private static void AssertNoInternalTerms(string content)
    {
        foreach (var term in InternalTerms)
        {
            Assert.DoesNotContain(term, content, StringComparison.OrdinalIgnoreCase);
        }
    }
}
