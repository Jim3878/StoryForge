// Called once by UpdateDialog.razor right after it writes the update-request signal file, while showing a
// full-page "updating, please wait" overlay. StoryForge.Launcher kills this very server process to apply the
// update, so there's no interop call that could tell the page when the new one is back up — it just has to
// poll from outside until a fetch succeeds again, then hard-reload to pick up the new build.
window.storyForgeUpdate = {
    waitAndReload() {
        const poll = () => {
            fetch(window.location.href, { method: "GET", cache: "no-store" })
                .then(res => {
                    if (res.ok)
                        window.location.reload();
                    else
                        setTimeout(poll, 2000);
                })
                .catch(() => setTimeout(poll, 2000));
        };

        // Give Launcher a moment to actually kill the old server first — otherwise the very first poll can
        // succeed against the still-running old process before the swap has even started.
        setTimeout(poll, 3000);
    },
};
