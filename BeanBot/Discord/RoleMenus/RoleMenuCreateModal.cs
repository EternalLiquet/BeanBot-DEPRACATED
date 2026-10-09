using Discord;
using Discord.Interactions;

namespace BeanBot.Discord.RoleMenus;

public sealed class RoleMenuCreateModal : IModal
{
    public string Title => "Create a role menu";

    [InputLabel("Title")]
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

    [InputLabel("Roles members can choose", "Pick up to 25 roles")]
    [ModalRoleSelect(
        "roles",
        1,
        RoleMenuConstants.MaximumRoles,
        Placeholder = "Choose roles")]
    public IRole[] Roles { get; set; } = [];

    [InputLabel("How many roles can members choose?")]
    [ModalRadioGroup("selection-mode")]
    [ModalRadioGroupOption(
        "Any number",
        "multiple",
        "Members can choose as many as they like.",
        true)]
    [ModalRadioGroupOption(
        "One role",
        "single",
        "Choosing a new role replaces their current one from this menu.")]
    public string SelectionMode { get; set; } = "multiple";

    [InputLabel("Where should this menu appear?")]
    [ModalChannelSelect("target-channel", 1, 1, Placeholder = "Choose a text channel")]
    [ChannelTypes(ChannelType.Text)]
    public ITextChannel? TargetChannel { get; set; }
}
