const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");
const vm = require("node:vm");

test("pipeline column drag ignores the selection header and restores the DOM before Blazor reorders", () => {
    const row = {};
    let sortableOptions;
    const calls = [];
    const sandbox = {
        window: {},
        document: {
            getElementById: id => id === "ps-header-row" ? row : null,
        },
        Sortable: {
            create: (_element, options) => {
                sortableOptions = options;
                return { destroy() {} };
            },
        },
    };

    const scriptPath = path.join(__dirname, "..", "src", "StoryForge.Web", "wwwroot", "js", "column-sort.js");
    vm.runInNewContext(fs.readFileSync(scriptPath, "utf8"), sandbox, { filename: scriptPath });
    sandbox.window.storyForgeColumnSort.init("ps-header-row", {
        invokeMethodAsync: (...args) => calls.push(args),
    });

    assert.equal(sortableOptions.draggable, "th:not(.ps-select-col)");
    assert.equal(typeof sortableOptions.onStart, "function");

    const originalNextSibling = { id: "original-next-sibling" };
    const item = { nextSibling: originalNextSibling };
    let restoredBefore;
    const from = {
        insertBefore: (movedItem, sibling) => {
            assert.equal(movedItem, item);
            restoredBefore = sibling;
        },
    };

    sortableOptions.onStart({ item });
    sortableOptions.onEnd({
        item,
        from,
        // Raw DOM indices include the leading selection checkbox header.
        oldIndex: 8,
        newIndex: 3,
        // Draggable indices count only the eight data columns.
        oldDraggableIndex: 7,
        newDraggableIndex: 2,
    });

    assert.equal(restoredBefore, originalNextSibling);
    assert.deepEqual(calls, [["OnColumnReordered", 7, 2]]);
});
