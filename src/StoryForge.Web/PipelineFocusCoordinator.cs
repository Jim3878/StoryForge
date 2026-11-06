namespace StoryForge.Web;

// Scoped per circuit — FlowGraphFocusCoordinator's mirror image, for FlowGraphPanel's node right-click
// "跳到管線狀態": Home.razor owns the tab switch, PipelineStatusPanel owns revealing/highlighting/scrolling
// to the row. Same two fixed single-subscriber steps, run tab-switch first (see FlowGraphFocusCoordinator).
public sealed class PipelineFocusCoordinator
{
    public event Func<Task>? TabActivationRequested;
    public event Func<string, Task>? FocusRequested;

    public void SubscribeTabActivation(Func<Task> handler) => TabActivationRequested += handler;
    public void UnsubscribeTabActivation(Func<Task> handler) => TabActivationRequested -= handler;

    public void SubscribeFocus(Func<string, Task> handler) => FocusRequested += handler;
    public void UnsubscribeFocus(Func<string, Task> handler) => FocusRequested -= handler;

    public async Task RequestFocusAsync(string playscriptName)
    {
        if (TabActivationRequested != null)
            await TabActivationRequested.Invoke();

        if (FocusRequested != null)
            await FocusRequested.Invoke(playscriptName);
    }
}
