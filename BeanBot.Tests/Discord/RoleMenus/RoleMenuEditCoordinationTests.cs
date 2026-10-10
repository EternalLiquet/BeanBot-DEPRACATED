using BeanBot.Discord.RoleMenus;
using Xunit;

namespace BeanBot.Tests.Discord.RoleMenus;

public class RoleMenuEditCoordinationTests
{
    [Fact]
    public async Task RunMenuWriteAsync_EditAndDeleteForSameMenu_AreSerialized()
    {
        var coordinator = new RoleMenuMutationCoordinator();
        var editEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseEdit = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var deleteEntered = false;

        var edit = coordinator.RunMenuWriteAsync(
            "menu:edit-delete",
            async _ =>
            {
                editEntered.SetResult();
                await releaseEdit.Task;
                return 1;
            },
            CancellationToken.None);
        await editEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var delete = coordinator.RunMenuWriteAsync(
            "menu:edit-delete",
            _ =>
            {
                deleteEntered = true;
                return Task.FromResult(2);
            },
            CancellationToken.None);

        await Task.Yield();
        Assert.False(deleteEntered);
        releaseEdit.SetResult();

        await Task.WhenAll(edit, delete);
        Assert.True(deleteEntered);
    }
}
