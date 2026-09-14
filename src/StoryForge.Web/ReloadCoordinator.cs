namespace StoryForge.Web;

// Scoped per circuit — lets the global toolbar's 讀取 button (MainLayout) ask every always-mounted panel
// (世界書/角色卡/流程圖/劇本管線狀態) to reload its own data from AppSettings.ExternalDataFolder in place, without a full
// page reload. A plain page reload would also reset client-only UI state that has nothing to do with the
// data itself — which tab is currently showing, in particular — so panels subscribe here instead and
// refresh themselves individually.
public sealed class ReloadCoordinator
{
    private event Func<Task>? Handlers;

    public void Subscribe(Func<Task> handler) => Handlers += handler;

    public void Unsubscribe(Func<Task> handler) => Handlers -= handler;

    public async Task RequestReloadAsync()
    {
        if (Handlers == null)
            return;

        // Sequential, not Task.WhenAll — GraphEditorState.LoadGraph() and CharacterCardState.Load() both
        // read AppSettings.ExternalDataFolder's files independently, but running them concurrently would
        // still just be racing plain synchronous file I/O for no real benefit, and it's simpler to reason
        // about failures (one panel's exception) one at a time.
        foreach (var handler in Handlers.GetInvocationList().Cast<Func<Task>>())
            await handler();
    }
}
