using Discord;
using Discord.Interactions;

namespace BeanBot.Discord.RoleMenus;

public sealed class RoleMenuCreateModal : IModal
{
    public string Title => "Create a role menu";

    [InputLabel("Panel title")]
    [ModalTextInput(
        "title",
        TextInputStyle.Short,
        "Game Roles",
        1,
        RoleMenuConstants.MaximumTitleLength)]
    public string PanelTitle { get; set; } = string.Empty;

    [InputLabel("Description", "Optional text shown below the title")]
    [RequiredInput(false)]
    [ModalTextInput(
        "description",
        TextInputStyle.Paragraph,
        "Choose the games you play. You can update this at any time.",
        0,
        RoleMenuConstants.MaximumDescriptionLength)]
    public string Description { get; set; } = string.Empty;

    [InputLabel("Self-assignable roles", "Choose 1–25 existing roles")]
    [ModalRoleSelect(
        "roles",
        1,
        RoleMenuConstants.MaximumRoles,
        Placeholder = "Choose roles")]
    public IRole[] Roles { get; set; } = [];

    [InputLabel("Selection mode")]
    [ModalRadioGroup("selection-mode")]
    [ModalRadioGroupOption(
        "Multiple",
        "multiple",
        "Members may choose any combination.",
        true)]
    [ModalRadioGroupOption(
        "Single",
        "single",
        "Choosing one role replaces another from this menu.")]
    public string SelectionMode { get; set; } = "multiple";

    [InputLabel("Target channel")]
    [ModalChannelSelect("target-channel", 1, 1, Placeholder = "Choose a text channel")]
    [ChannelTypes(ChannelType.Text)]
    public ITextChannel? TargetChannel { get; set; }
}
