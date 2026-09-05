// Small, generic drag-by-handle helper for floating panels (currently just the 設定 dialog) — plain
// mousedown/mousemove/mouseup on the real DOM elements, not Blazor's @onmousemove, so dragging doesn't
// send a SignalR round-trip for every pixel of mouse movement.
window.storyForgeUi = (function () {
    function makeDraggable(panelId, handleId) {
        const panel = document.getElementById(panelId);
        const handle = document.getElementById(handleId);
        if (!panel || !handle) return;

        // The panel element is destroyed and recreated each time its owning @if toggles it back into the
        // Blazor render tree, so this always sees a fresh node — but OnAfterRenderAsync (the caller) fires
        // on every re-render while it stays open too (e.g. typing in a field), which would re-run this
        // against that SAME still-alive node and stack duplicate listeners without this guard.
        if (panel._dragInitialized) return;
        panel._dragInitialized = true;

        let dragging = false;
        let startX = 0, startY = 0, startLeft = 0, startTop = 0;

        handle.addEventListener("mousedown", function (e) {
            dragging = true;
            const rect = panel.getBoundingClientRect();
            startX = e.clientX;
            startY = e.clientY;
            startLeft = rect.left;
            startTop = rect.top;
            // Switches from its initial centered position (top/left percentages) to explicit pixel
            // left/top pinned at exactly where it already was, so the drag starts with no visual jump.
            panel.style.left = startLeft + "px";
            panel.style.top = startTop + "px";
            panel.style.transform = "none";
            e.preventDefault();
        });

        document.addEventListener("mousemove", function (e) {
            if (!dragging) return;
            panel.style.left = (startLeft + (e.clientX - startX)) + "px";
            panel.style.top = (startTop + (e.clientY - startY)) + "px";
        });

        document.addEventListener("mouseup", function () {
            dragging = false;
        });
    }

    return { makeDraggable: makeDraggable };
})();
