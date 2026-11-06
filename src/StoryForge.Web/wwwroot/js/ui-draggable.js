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

    // Generic drag-to-resize for a panel with a fixed-width CSS `width` (not a flex-basis — see
    // FlowGraphPanel.razor's .graph-inspector comment on why that distinction matters) and a narrow handle
    // element sitting on its LEFT edge. Deliberately does not duplicate the panel's CSS min-width/max-width
    // here: the browser already clamps a `style.width` write against those on every layout pass, so this
    // can just write the unclamped arithmetic result and let CSS do the clamping.
    function makeResizable(panelId, handleId) {
        const panel = document.getElementById(panelId);
        const handle = document.getElementById(handleId);
        if (!panel || !handle) return;

        // Same re-render guard as makeDraggable above, keyed on the handle since (unlike the settings
        // dialog) this panel is destroyed and recreated each time its owning @if toggles off and back on.
        if (handle._resizeInitialized) return;
        handle._resizeInitialized = true;

        let dragging = false;
        let startX = 0, startWidth = 0;

        handle.addEventListener("mousedown", function (e) {
            dragging = true;
            startX = e.clientX;
            startWidth = panel.getBoundingClientRect().width;
            // Prevents the drag from also selecting page text while the mouse crosses over it.
            document.body.style.userSelect = "none";
            e.preventDefault();
        });

        document.addEventListener("mousemove", function (e) {
            if (!dragging) return;
            // Handle is on the panel's left edge: dragging left (negative delta) should widen the panel.
            panel.style.width = (startWidth - (e.clientX - startX)) + "px";
        });

        document.addEventListener("mouseup", function () {
            if (!dragging) return;
            dragging = false;
            document.body.style.userSelect = "";
        });
    }

    // Same idea as makeResizable but vertical: a full-width strip sitting below a target element (a
    // textarea with a fixed CSS height, or a flex-grow item filling remaining space) that drags its
    // height instead of relying on the browser's native `resize` corner handle, which on a full-width
    // textarea ends up as a tiny triangle far in the bottom-right corner.
    function makeVResizable(targetId, handleId) {
        const target = document.getElementById(targetId);
        const handle = document.getElementById(handleId);
        if (!target || !handle) return;

        if (handle._vResizeInitialized) return;
        handle._vResizeInitialized = true;

        let dragging = false;
        let startY = 0, startHeight = 0;

        handle.addEventListener("mousedown", function (e) {
            dragging = true;
            startY = e.clientY;
            startHeight = target.getBoundingClientRect().height;
            // A flex-grow target (e.g. the last box in a column, filling remaining space) would otherwise
            // have any explicit height overridden by flex-grow on every layout pass — pin it to a fixed
            // size once the user takes manual control via this handle.
            target.style.flex = "0 0 auto";
            document.body.style.userSelect = "none";
            e.preventDefault();
        });

        document.addEventListener("mousemove", function (e) {
            if (!dragging) return;
            target.style.height = (startHeight + (e.clientY - startY)) + "px";
        });

        document.addEventListener("mouseup", function () {
            if (!dragging) return;
            dragging = false;
            document.body.style.userSelect = "";
        });
    }

    // Global Ctrl/Cmd+S → 存檔, replacing the browser's native "Save Page As" dialog. Bound once per page
    // load (guarded on window itself, not a specific element, since MainLayout is the app's single
    // persistent shell that never gets torn down/recreated the way a dialog panel does). Deliberately not
    // scoped to "not inside a text input" the way graph-editor.js's own Ctrl+Z/Y guard is — Ctrl+S never
    // types a literal "s" into a field in the first place (browsers already reserve the combo for their own
    // save dialog), so there's no risk of hijacking normal typing by leaving this unconditional.
    // 存檔提示音：用 Web Audio 直接合成，不需要音檔。成功是上升的「叮咚」、有警告是單一個「叮」、失敗是低沉的「嗡嗡」。
    // 存檔一定由按鍵或按鈕觸發，頁面已經有使用者互動，不會被瀏覽器的自動播放限制擋住。
    function playSaveFeedback(outcome) {
        try {
            const AudioContextClass = window.AudioContext || window.webkitAudioContext;
            if (!AudioContextClass) return;
            const ctx = window._storyForgeAudioContext || (window._storyForgeAudioContext = new AudioContextClass());
            if (ctx.state === "suspended") ctx.resume();

            // [頻率 Hz, 開始秒數, 長度秒數]
            const tones = outcome === "Success" ? [[880, 0, 0.14], [1318.5, 0.11, 0.22]]
                : outcome === "Warning" ? [[660, 0, 0.3]]
                : [[196, 0, 0.18], [196, 0.24, 0.24]];
            const type = outcome === "Failure" ? "square" : "sine";
            const peak = outcome === "Failure" ? 0.06 : 0.16;

            for (const [frequency, start, duration] of tones) {
                const oscillator = ctx.createOscillator();
                const gain = ctx.createGain();
                oscillator.type = type;
                oscillator.frequency.value = frequency;
                const t0 = ctx.currentTime + start;
                gain.gain.setValueAtTime(0.0001, t0);
                gain.gain.exponentialRampToValueAtTime(peak, t0 + 0.015);
                gain.gain.exponentialRampToValueAtTime(0.0001, t0 + duration);
                oscillator.connect(gain).connect(ctx.destination);
                oscillator.start(t0);
                oscillator.stop(t0 + duration + 0.02);
            }
        } catch (e) {
            // 提示音失敗不影響存檔本身
        }
    }

    // 關閉視窗前提醒未存檔：畫布上的變動由 storyForgeGraph.hasCanvasChanges 即時比對；伺服器端的變動
    // （例如改劇本ID）無法在 beforeunload 當下同步詢問伺服器，所以由 MainLayout 定期推送最新狀態過來。
    // 提示文字由瀏覽器決定（「要離開網站嗎？系統可能不會儲存你所做的變更」），網頁無法自訂。
    let serverHasUnsavedChanges = false;

    function setServerUnsaved(value) {
        serverHasUnsavedChanges = !!value;
    }

    function hasUnsavedChanges() {
        if (serverHasUnsavedChanges) return true;
        try {
            return !!(window.storyForgeGraph && window.storyForgeGraph.hasCanvasChanges &&
                window.storyForgeGraph.hasCanvasChanges());
        } catch (e) {
            return false;
        }
    }

    function bindUnsavedGuard() {
        if (window._storyForgeUnsavedGuardBound) return;
        window._storyForgeUnsavedGuardBound = true;

        window.addEventListener("beforeunload", function (e) {
            if (!hasUnsavedChanges()) return;
            e.preventDefault();
            e.returnValue = "";
        });
    }

    function bindSaveShortcut(dotNetRef) {
        if (window._storyForgeSaveShortcutBound) return;
        window._storyForgeSaveShortcutBound = true;

        // 在 user 眼裡這是一個獨立 App，不是 Chrome：會跳出 Chrome 自己介面的快捷鍵一律吞掉
        // （D 書籤、P 列印、F 尋找列、H 歷史、J 下載、U 原始碼、G 尋找下一個）。Ctrl+D 在流程圖的功能
        // 由 graph-editor.js 自己處理。F5／Ctrl+R、F12、Ctrl+Shift+J/I（開發者工具）保留。
        const blockedChromeKeys = new Set(["d", "p", "f", "h", "j", "u", "g"]);

        window.addEventListener("keydown", function (e) {
            const key = (e.key || "").toLowerCase();
            if ((e.ctrlKey || e.metaKey) && !e.altKey && blockedChromeKeys.has(key) && !(e.shiftKey && key === "j")) {
                e.preventDefault();
                return;
            }
            if ((e.ctrlKey || e.metaKey) && key === "s") {
                e.preventDefault();
                if (document.querySelector(".save-result-overlay")) return;
                dotNetRef.invokeMethodAsync("SaveFromShortcut");
            }
        });
    }

    // Handle-initiated drag-to-reorder for a plain vertical list (currently just 角色卡's roster) —
    // relocates the actual row DOM node live under the cursor as it moves (not native HTML5 drag-and-drop,
    // which would need a ghost drag image and its own dragover/drop event dance for comparatively little
    // benefit here), then hands the final order back to Blazor once on mouseup rather than per pixel.
    //
    // Bound once via mousedown delegation on the list container itself, not per-row — the container is
    // never torn down (see CharacterCardPanel's own comment on why), so this keeps working as rows are
    // added/removed without needing to re-bind. itemSelector identifies one row; handleSelector is the
    // small drag-only element inside it (see CharacterCardPanel.razor's own note on why dragging needs a
    // dedicated handle instead of the whole row, so it doesn't fight with click-to-select).
    function makeSortableList(listId, itemSelector, handleSelector, dotNetRef, methodName) {
        const list = document.getElementById(listId);
        if (!list) return;

        if (list._sortableInitialized) return;
        list._sortableInitialized = true;

        // Tracks whichever drag is currently in flight (or null) — module-level, not just inside the
        // mousedown closure, specifically so a NEW mousedown can find and tear down a previous one. Without
        // this, a gesture that never gets a matching mouseup (mouse released outside the browser window,
        // focus lost mid-drag via alt-tab, etc. — confirmed reproducible: mousedown + mousemove with no
        // mouseup) leaves that gesture's onMouseMove/onMouseUp permanently attached to document. Every
        // later drag's mouse movement then ALSO feeds into that stale listener, which drags the earlier
        // gesture's (unrelated) row around using the new drag's coordinates — the actual cause of the
        // "occasionally lands a slot or two off" bug this whole comment block exists to explain, verified
        // by deliberately leaking one and watching a subsequent clean drag corrupt 5 rows instead of 2.
        let activeDrag = null;

        function endDrag() {
            if (!activeDrag) return;
            document.removeEventListener("mousemove", activeDrag.onMouseMove);
            document.removeEventListener("mouseup", activeDrag.onMouseUp);
            activeDrag.draggingItem.classList.remove("dragging");
            activeDrag = null;
        }

        list.addEventListener("mousedown", function (e) {
            const handle = e.target.closest(handleSelector);
            if (!handle || !list.contains(handle)) return;
            const draggingItem = handle.closest(itemSelector);
            if (!draggingItem) return;

            endDrag(); // clean up a previous gesture's listeners if it never got a mouseup — see above

            e.preventDefault();
            draggingItem.classList.add("dragging");

            function onMouseMove(moveEvent) {
                // The first sibling whose vertical midpoint the cursor is still above is where
                // draggingItem belongs (DOM order already matches document/visual order) — insertBefore it
                // there; falling off the end (cursor below every remaining midpoint) means "last".
                const siblings = [...list.querySelectorAll(itemSelector)].filter(el => el !== draggingItem);
                const afterElement = siblings.find(function (sib) {
                    const rect = sib.getBoundingClientRect();
                    return moveEvent.clientY < rect.top + rect.height / 2;
                });
                if (afterElement)
                    list.insertBefore(draggingItem, afterElement);
                else
                    list.appendChild(draggingItem);
            }

            function onMouseUp() {
                endDrag();

                const orderedIds = [...list.querySelectorAll(itemSelector)].map(el => el.dataset.cardId);
                dotNetRef.invokeMethodAsync(methodName, orderedIds);
            }

            activeDrag = { draggingItem: draggingItem, onMouseMove: onMouseMove, onMouseUp: onMouseUp };
            document.addEventListener("mousemove", onMouseMove);
            document.addEventListener("mouseup", onMouseUp);
        });

        // The fallback for the interruption itself, not just its aftermath: losing window focus mid-drag
        // (alt-tab, clicking another application, a devtools popup stealing focus) never fires mouseup at
        // all, so without this the drag would stay "active" — and its listeners attached — until the next
        // mousedown's endDrag() happens to clean it up. Finalizing here (same save-wherever-it-currently-is
        // behavior as a real mouseup) closes that window immediately instead of leaving a stale listener
        // live in the meantime.
        window.addEventListener("blur", function () {
            if (!activeDrag) return;
            endDrag();

            const orderedIds = [...list.querySelectorAll(itemSelector)].map(el => el.dataset.cardId);
            dotNetRef.invokeMethodAsync(methodName, orderedIds);
        });
    }

    // 表格的「反白列」快捷鍵（目前只有管線表格）：表格在畫面上、焦點不在輸入元件時，
    // Ctrl+C 複製反白列的劇本名稱（有用滑鼠反白文字時照瀏覽器原本的行為複製文字），空白鍵切換反白列的勾選。
    // 反白狀態本身在 C# 端；JS 只看 DOM 上有沒有 .ps-row-highlighted 決定要不要攔截按鍵。
    // 每次 render 都會呼叫，用 window 上的旗標確保同一個表格只綁一次。
    function bindRowHighlightKeys(tableId, dotNetRef) {
        const flag = "_storyForgeRowKeysBound_" + tableId;
        if (window[flag]) return;
        window[flag] = true;

        function activeTable() {
            const table = document.getElementById(tableId);
            return table && table.offsetParent !== null ? table : null;
        }

        // Shift／Ctrl 點列是多選，不要讓瀏覽器順便反白一大段文字
        document.addEventListener("mousedown", function (e) {
            const table = activeTable();
            if (table && (e.shiftKey || e.ctrlKey || e.metaKey) && e.target.closest &&
                e.target.closest("#" + tableId + " tbody tr"))
                e.preventDefault();
        });

        document.addEventListener("keydown", function (e) {
            const table = activeTable();
            if (!table || !table.querySelector("tr.ps-row-highlighted")) return;
            const target = e.target;
            const tag = target && target.localName;
            if (tag === "input" || tag === "textarea" || tag === "select" || (target && target.isContentEditable))
                return;
            if (document.querySelector(".ps-context-menu")) return;

            const key = (e.key || "").toLowerCase();
            if ((e.ctrlKey || e.metaKey) && !e.shiftKey && !e.altKey && key === "c") {
                const selection = window.getSelection();
                if (selection && !selection.isCollapsed && selection.toString().trim() !== "") return;
                e.preventDefault();
                dotNetRef.invokeMethodAsync("GetHighlightedNamesForCopy").then(function (text) {
                    if (text && navigator.clipboard) navigator.clipboard.writeText(text).catch(function () {});
                });
            } else if (key === " " && !e.ctrlKey && !e.metaKey && !e.altKey && !e.shiftKey) {
                e.preventDefault();
                if (!e.repeat) dotNetRef.invokeMethodAsync("ToggleHighlightedRowsChecked");
            }
        });
    }

    // 流程圖節點右鍵「跳到管線狀態」：分頁切換和該列的 render 都是非同步送到瀏覽器的，
    // 所以輪詢到表格可見（display:block）且該列已在 DOM 上，才把它捲到畫面中間；最多等 2 秒。
    function scrollRowIntoViewWhenVisible(tableId, playscriptName) {
        const deadline = Date.now() + 2000;
        return new Promise(function (resolve) {
            (function tick() {
                const table = document.getElementById(tableId);
                const row = table && table.offsetParent !== null
                    ? table.querySelector('tbody tr[data-ps-name="' + CSS.escape(playscriptName) + '"]')
                    : null;
                if (row) {
                    row.scrollIntoView({ block: "center" });
                    resolve(true);
                } else if (Date.now() > deadline) {
                    resolve(false);
                } else {
                    requestAnimationFrame(tick);
                }
            })();
        });
    }

    return {
        makeDraggable: makeDraggable, makeResizable: makeResizable, makeVResizable: makeVResizable,
        bindSaveShortcut: bindSaveShortcut, makeSortableList: makeSortableList,
        bindRowHighlightKeys: bindRowHighlightKeys, scrollRowIntoViewWhenVisible: scrollRowIntoViewWhenVisible,
        playSaveFeedback: playSaveFeedback,
        setServerUnsaved: setServerUnsaved, bindUnsavedGuard: bindUnsavedGuard,
    };
})();
