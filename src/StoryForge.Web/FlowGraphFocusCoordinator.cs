namespace StoryForge.Web;

// Scoped per circuit — lets PipelineStatusPanel's "跳到劇情節點" right-click action (which has no direct
// reference to either Home.razor's tab state or FlowGraphPanel's canvas) ask for both a tab switch and a
// canvas focus in one call, without the two always-mounted panels knowing about each other directly. Two
// fixed single-subscriber event slots rather than ReloadCoordinator's open multicast list — there are
// exactly two participants here (Home.razor owns the tab switch, FlowGraphPanel owns the canvas focus), not
// an open-ended set of panels reacting independently, and the two steps must run in that specific order (see
// RequestFocusAsync) rather than in parallel or in subscription order.
public sealed class FlowGraphFocusCoordinator
{
    public event Func<Task>? TabActivationRequested;
    public event Func<string, Task>? FocusRequested;

    public void SubscribeTabActivation(Func<Task> handler) => TabActivationRequested += handler;
    public void UnsubscribeTabActivation(Func<Task> handler) => TabActivationRequested -= handler;

    public void SubscribeFocus(Func<string, Task> handler) => FocusRequested += handler;
    public void UnsubscribeFocus(Func<string, Task> handler) => FocusRequested -= handler;

    // The tab switch must be requested (and its render dispatched) before the canvas focus call runs — the
    // canvas is only ever actually visible once Home.razor's wrapper div flips to display:block, and
    // graph-editor.js's own focusOnPlayscriptWhenVisible polls for that rather than assuming this ordering
    // alone is enough, since a Blazor Server render batch reaching the browser is inherently asynchronous.
    public async Task RequestFocusAsync(string playscriptName)
    {
        if (TabActivationRequested != null)
            await TabActivationRequested.Invoke();

        if (FocusRequested != null)
            await FocusRequested.Invoke(playscriptName);
    }
}
