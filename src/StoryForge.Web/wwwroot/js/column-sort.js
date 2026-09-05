// Thin glue between SortableJS (vendored in lib/sortable — its default drag UX, ghost element, drop
// placeholder gap, and animation are all far better out of the box than a hand-rolled HTML5
// draggable/dragover/drop implementation, which was flagged as feeling nothing like a real list reorder)
// and a Blazor component's own C# state, which stays the single source of truth for column order —
// SortableJS only reports WHICH indices swapped via onEnd; Blazor re-renders the actual DOM afterward.
window.storyForgeColumnSort = (function () {
    function init(rowElementId, dotNetRef) {
        const row = document.getElementById(rowElementId);
        if (!row) return;

        // Re-initializing on every render (rather than guarding to firstRender only) keeps this correct
        // even if the row element itself gets recreated by Blazor's diffing — destroying any previous
        // instance first avoids stacking duplicate Sortable instances (and duplicate onEnd callbacks) on
        // the same element across renders.
        if (row._sortableInstance) {
            row._sortableInstance.destroy();
        }

        row._sortableInstance = Sortable.create(row, {
            animation: 150,
            onEnd: function (evt) {
                if (evt.oldIndex !== evt.newIndex) {
                    dotNetRef.invokeMethodAsync("OnColumnReordered", evt.oldIndex, evt.newIndex);
                }
            },
        });
    }

    return { init: init };
})();
