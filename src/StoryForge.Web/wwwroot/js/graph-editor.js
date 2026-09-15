// LiteGraph.js glue for the flow-graph editor. Deliberately request/response rather than live-event-driven
// for this first pass: init() builds the canvas once from a full snapshot, exportGraph() reads the whole
// canvas state back out on demand (the Blazor page calls it when the user clicks 存檔) — no per-drag
// round trips to the server, which is the whole point of doing the canvas interaction client-side at all.
window.storyForgeGraph = (function () {
    let graph = null;
    let canvas = null;
    let nodesByGuid = new Map();
    let guidByNode = new Map();
    let outputSlotsByType = new Map();  // typeName -> { fieldName: slotIndex }
    let inputSlotsByType = new Map();   // typeName -> { fieldName: slotIndex } — one slot per field, always
    let currentPayload = null;
    let canvasResizeObserver = null; // held here so it isn't garbage-collected the moment init() returns
    let newNodeGuids = new Set(); // guids created client-side this session, not yet known to the server
    let initialNodeGuids = new Set(); // guids the server actually knows about, snapshotted at init()
    let selfRef = null; // DotNetObjectReference captured from init() — needed outside its own closure so
                         // instantiateNodeFromMenu can notify C# the moment a node is created, not just on
                         // selection change (see OnNodeCreated below)
    let groupPadding = 25.6;      // from AppSettings.Padding — see recomputeGroupBounds
    let groupTitleBarHeight = 60; // from AppSettings.TitleBarHeight
    let liteTypeToTypeName = new Map(); // "storyforge/xxx" -> original schema typeName, for snapshot/restore
    let searchMinZoom = 0.5;      // from AppSettings.SearchMinZoom — see focusOnNode

    // Ported from the WinForms tool's ProcessGraphCanvas.SearchNode: matches by identity title only
    // (hasIdentityTitle — playscriptId/flagId, never a node's generic type-label fallback), case-insensitive
    // substring. Repeating the exact same query cycles to the next match instead of re-jumping to the
    // first one; a new/different query starts a fresh match list.
    let lastSearchQuery = null;
    let lastSearchMatchGuids = [];
    let lastSearchMatchIndex = -1;

    patchLiteGraphForMultiInputLinks();
    patchEdgeBodyRewiring();
    patchAddNodeMenu();
    patchDisableNodePropertiesPanel();
    patchCaptureShiftOnMouseUp();
    patchNodeContextMenu();
    patchDeleteWithUndo();
    patchUndoRedoKeys();
    patchPersistViewportOnWheel();
    patchGroupRendering();

    // Set for the duration of a multi-node drag (see patchEdgeBodyRewiring's beginMultiNodeDrag) so
    // onNodeMoved — which LiteGraph only ever calls once, for whichever node the mouse actually grabbed —
    // knows to recompute group membership for every dragged node, not just that one. Cleared the moment
    // onNodeMoved consumes it.
    let pendingMultiDragNodes = null;

    // Set the moment a REWIRE gesture begins — either our own edge-body grab (beginRewireFromEdgeGrab)
    // or LiteGraph's native "grab an input dot that already has a link" gesture (stock processMouseDown,
    // gated on allow_reconnect_links) — and consumed once at drop time by showConnectionMenu. Distinguishes
    // "this connecting_node drag is moving an EXISTING line" from "this is a brand-new line being pulled
    // off a port" (which sets up the identical connecting_node/connecting_input/connecting_output state,
    // but should always show the add-node menu on an empty-space drop). Ported from the WinForms tool's
    // own DragStartThreshold/IsNearNode grace (ProcessGraphCanvas.OnMouseMove/OnMouseUp), which this port's
    // first pass explicitly skipped (see the comment above EDGE_CLICK_THRESHOLD_PX).
    let pendingRewireInfo = null;
    const REWIRE_DRAG_THRESHOLD_PX = 4;   // world units = this / ds.scale — WinForms' own DragStartThreshold
    const REWIRE_SNAPBACK_BUFFER_PX = 24; // world units = this / ds.scale — WinForms' own IsNearNode buffer

    // Shift's role here (held when a node drag ends = detach from its current group instead of the
    // default "stays a member, group just grows to follow" — see updateNodeGroupMembership) needs the
    // modifier state at the exact moment the drag ends, not just "is it down right now" — reading it off
    // the mouseup event itself (standard MouseEvent.shiftKey, exactly what LiteGraph's own processMouseUp
    // already receives) is both simpler and more precise than tracking window keydown/keyup separately.
    let lastMouseUpShiftKey = false;

    function patchCaptureShiftOnMouseUp() {
        const originalProcessMouseUp = LGraphCanvas.prototype.processMouseUp;
        LGraphCanvas.prototype.processMouseUp = function (e) {
            lastMouseUpShiftKey = !!e.shiftKey;

            // A plain click (no real drag) on empty canvas should clear the current selection — matching
            // every other "click empty space deselects everything" gesture in this app (and the original
            // WinForms tool's own OnMouseUp: "_pendingBoxSelectStartScreen != null && !_isBoxSelecting ->
            // clear selection"). Our own forced-ctrlKey box-select (see patchEdgeBodyRewiring) means EVERY
            // such click now goes through LiteGraph's native dragging_rectangle release code instead of its
            // separate plain-click branch — and that code only ever calls selectNodes when the rectangle
            // actually overlaps a node, silently leaving a prior selection untouched otherwise. Needs
            // adjustMouseEvent first since nothing has computed e.canvasX/canvasY for this event yet.
            if (this.dragging_rectangle && !e.shiftKey && Object.keys(this.selected_nodes).length > 0) {
                this.adjustMouseEvent(e);
                const width = Math.abs(this.dragging_rectangle[2]);
                const height = Math.abs(this.dragging_rectangle[3]);
                const nodeAtPoint = this.graph.getNodeOnPos(e.canvasX, e.canvasY, this.visible_nodes);
                if (!nodeAtPoint && !(width > 10 && height > 10))
                    this.deselectAllNodes();
            }

            const result = originalProcessMouseUp.call(this, e);
            commitUndoableChange();
            saveViewport();
            return result;
        };
    }

    // Mouse-wheel zoom changes ds.scale directly inside LiteGraph's own processMouseWheel, with no
    // mouseup/mousedown pair around it for patchCaptureShiftOnMouseUp's own saveViewport() call to piggyback
    // on — needs its own wrapper so a wheel-only zoom (no click/drag) still gets remembered across a reload.
    function patchPersistViewportOnWheel() {
        const originalProcessMouseWheel = LGraphCanvas.prototype.processMouseWheel;
        LGraphCanvas.prototype.processMouseWheel = function (e) {
            const result = originalProcessMouseWheel.call(this, e);
            saveViewport();
            return result;
        };
    }

    // Snapshot-based undo/redo — mirrors the old WinForms tool's own approach (ProcessGraphCanvas's
    // BeginUndoableChange/Undo snapshot the whole GraphDocumentModel) rather than recording individual
    // commands, since a full-state snapshot is far simpler to get right than diffing/inverting every
    // possible mutation (drag, rewire, delete, group resize, ...) by hand. Scoped to what the canvas itself
    // owns — node positions, connections, node/group existence and group membership — NOT the inspector
    // sidebar's text fields (playscriptId/memo/衝突/變化), which write straight into the server-side model
    // (see GraphEditorState.UpdateNodeField) with no client-side record to snapshot; a known v1 gap, not an
    // oversight, matching the same "server sync is explicit, not automatic" boundary the rest of this file
    // already draws around 存檔.
    let undoStack = [];
    let redoStack = [];
    let pendingUndoSnapshot = null; // "before" state for whichever gesture is currently in flight
    const MAX_UNDO_DEPTH = 50;

    // Idempotent within one gesture — a drag/rewire/etc. spans a mousedown..mouseup pair, sometimes with
    // several internal steps (e.g. our own edge-rewire detaches at mousedown, reconnects at mouseup); only
    // the FIRST call in that span should capture "before", matching the original tool's own
    // BeginUndoGroupIfNeeded/_undoSnapshotPending guard.
    function beginUndoableChange() {
        if (pendingUndoSnapshot === null)
            pendingUndoSnapshot = captureSnapshot();
    }

    // Only actually pushes an undo entry if the gesture turned out to change something — a plain click,
    // click-to-deselect, or box-select-that-selected-nothing all call beginUndoableChange() (blanket
    // coverage is simpler than trying to enumerate every mutating code path) but must not each burn an
    // undo slot for a no-op.
    function commitUndoableChange() {
        if (pendingUndoSnapshot === null)
            return;
        const before = pendingUndoSnapshot;
        pendingUndoSnapshot = null;
        const after = captureSnapshot();
        if (JSON.stringify(before) === JSON.stringify(after))
            return;
        undoStack.push(before);
        if (undoStack.length > MAX_UNDO_DEPTH)
            undoStack.shift();
        redoStack = [];
    }

    function performUndo() {
        if (undoStack.length === 0)
            return;
        const current = captureSnapshot();
        const previous = undoStack.pop();
        redoStack.push(current);
        restoreSnapshot(current, previous);
    }

    function performRedo() {
        if (redoStack.length === 0)
            return;
        const current = captureSnapshot();
        const next = redoStack.pop();
        undoStack.push(current);
        restoreSnapshot(current, next);
    }

    // Full node/edge/group state, keyed by guid/port-name rather than LiteGraph's own internal numeric ids
    // (which aren't stable across a rebuild) — the same shape init()'s payload uses, so buildGraphFromData
    // (factored out of init() below) can rebuild from either one.
    function captureSnapshot() {
        const nodes = [];
        for (const node of graph._nodes) {
            const guid = guidByNode.get(node);
            if (!guid) continue;
            nodes.push({
                guid: guid,
                typeName: liteTypeToTypeName.get(node.type),
                x: node.pos[0], y: node.pos[1],
                width: node._baseWidth, height: node.size[1],
                title: node._displayTitle,
                hasIdentityTitle: !!node._hasIdentityTitle,
            });
        }

        const edges = [];
        for (const linkId in graph.links) {
            const link = graph.links[linkId];
            if (!link) continue;
            const fromNode = graph.getNodeById(link.origin_id);
            const toNode = graph.getNodeById(link.target_id);
            const fromGuid = guidByNode.get(fromNode);
            const toGuid = guidByNode.get(toNode);
            if (!fromGuid || !toGuid) continue;
            edges.push({
                fromNodeGuid: fromGuid,
                fromPort: findPortName(outputSlotsByType, liteTypeToTypeName.get(fromNode.type), link.origin_slot),
                toNodeGuid: toGuid,
                toPort: findPortName(inputSlotsByType, liteTypeToTypeName.get(toNode.type), link.target_slot),
            });
        }

        const groups = (graph._groups || []).map(group => ({
            clientId: group._clientId || null,
            title: group._displayTitle || "",
            x: group.pos[0], y: group.pos[1], width: group.size[0], height: group.size[1],
            color: group.color,
            memberNodeGuids: [...(group._memberGuids || [])],
        }));

        return { nodes: nodes, edges: edges, groups: groups, newNodeGuids: [...newNodeGuids] };
    }

    function findPortName(slotMap, typeName, slotIndex) {
        const slots = slotMap.get(typeName) || {};
        return Object.keys(slots).find(k => slots[k] === slotIndex);
    }

    // Tears down and fully rebuilds the graph from a snapshot — simpler and safer than trying to diff/patch
    // the live LiteGraph objects back to an earlier state in place, at the cost of losing object identity
    // across an undo/redo (harmless here: nothing outside this module holds onto a raw LGraphNode/LGraphGroup
    // reference across a tick). fromState is the state being LEFT (before this restore) — needed only to
    // tell which not-yet-saved nodes this step is adding vs removing, so the server-side model (which
    // learns about a new node immediately, see instantiateNodeFromMenu's OnNodeCreated call) can be kept in
    // sync the same way; every other field here is purely client-side until 存檔, so no other server call is
    // needed.
    function restoreSnapshot(fromState, toState) {
        graph.clear();
        nodesByGuid = new Map();
        guidByNode = new Map();

        buildGraphFromData(toState.nodes, toState.edges, toState.groups);
        newNodeGuids = new Set(toState.newNodeGuids);

        const previouslyNew = new Set(fromState.newNodeGuids);
        const nowNew = new Set(toState.newNodeGuids);
        for (const guid of previouslyNew)
            if (!nowNew.has(guid))
                selfRef?.invokeMethodAsync("OnNodeUndoRemoved", guid);
        for (const guid of nowNew)
            if (!previouslyNew.has(guid)) {
                const nodeDto = toState.nodes.find(n => n.guid === guid);
                if (nodeDto)
                    selfRef?.invokeMethodAsync("OnNodeCreated", guid, nodeDto.typeName, nodeDto.x, nodeDto.y);
            }

        canvas.deselectAllNodes();
        canvas.setDirty(true, true);
    }

    function registerNodeTypes(nodeTypes) {
        for (const typeDto of nodeTypes) {
            inputSlotsByType.set(typeDto.typeName,
                Object.fromEntries(typeDto.inputs.map((p, i) => [p.fieldName, i])));
            outputSlotsByType.set(typeDto.typeName,
                Object.fromEntries(typeDto.outputs.map((p, i) => [p.fieldName, i])));

            // A fresh constructor function per type: LiteGraph keys registered node classes by string and
            // hangs display metadata (title) directly off the constructor, so types can't share one class.
            // allowMultiple on a slot is what patchLiteGraphForMultiInputLinks below keys off of — a plain
            // (non-flagged) input keeps LiteGraph's untouched native one-link-per-slot behavior.
            function StoryForgeNode() {
                for (const input of typeDto.inputs)
                    this.addInput(input.portName, "", { allowMultiple: input.allowMultiple });
                for (const output of typeDto.outputs)
                    this.addOutput(output.portName, "");
            }
            StoryForgeNode.title = typeDto.displayName;

            const liteType = "storyforge/" + sanitize(typeDto.typeName);
            LiteGraph.registerNodeType(liteType, StoryForgeNode);
            liteTypeToTypeName.set(liteType, typeDto.typeName);
        }
    }

    function sanitize(typeName) {
        return typeName.replace(/[^a-zA-Z0-9]+/g, "_");
    }

    // LiteGraph's input slots hold exactly one link each — connecting a second wire to an already-occupied
    // input silently detaches the first (found the hard way: loading a real graph this way dropped 178 of
    // 764 edges without a single error). Patched here rather than working around it in our own data model,
    // so the canvas visually behaves like the original WinForms tool's graph did: one input dot, N wires.
    // Scoped to slots explicitly marked allowMultiple — every ordinary single-link input keeps LiteGraph's
    // stock behavior untouched.
    function patchLiteGraphForMultiInputLinks() {
        const originalConnect = LGraphNode.prototype.connect;
        LGraphNode.prototype.connect = function (outputSlot, targetNode, targetSlot) {
            const resolvedTarget = typeof targetNode === "number" ? this.graph.getNodeById(targetNode) : targetNode;
            const targetSlotIndex = typeof targetSlot === "string" ? resolvedTarget?.findInputSlot(targetSlot) : targetSlot;
            const input = resolvedTarget?.inputs?.[targetSlotIndex];

            // Trick the original into skipping its own "already has a link, disconnect it first" branch —
            // temporarily hide the existing link from it, then restore + merge afterward.
            let previousLinks = null;
            if (input?.allowMultiple && input.link != null) {
                previousLinks = input.links ? input.links.slice() : [input.link];
                input.link = null;
            }

            const newLink = originalConnect.call(this, outputSlot, targetNode, targetSlot);

            if (newLink && input?.allowMultiple) {
                input.links = (previousLinks || []).concat(newLink.id);
                // input.link is left pointing at newLink.id by the original call — kept as "most recent".
            }

            return newLink;
        };

        // slotOrName with no linkId: used when there's no specific link in mind (e.g. dragging an existing
        // wire off an input slot to re-route it) — removes whichever link is "current" (.link, the most
        // recently added). A specific linkId (from removeLink below, i.e. deleting one particular wire by
        // right-clicking it) removes exactly that one and leaves the rest connected.
        const originalDisconnectInput = LGraphNode.prototype.disconnectInput;
        LGraphNode.prototype.disconnectInput = function (slotOrName, linkId) {
            const slotIndex = typeof slotOrName === "string" ? this.findInputSlot(slotOrName) : slotOrName;
            const input = this.inputs?.[slotIndex];

            if (input?.allowMultiple && Array.isArray(input.links) && input.links.length > 1) {
                const removeId = linkId != null ? linkId : input.links[input.links.length - 1];
                const idx = input.links.indexOf(removeId);
                if (idx === -1) return false;
                input.links.splice(idx, 1);

                const link = this.graph.links[removeId];
                if (link) {
                    const originNode = this.graph.getNodeById(link.origin_id);
                    const output = originNode?.outputs?.[link.origin_slot];
                    const outIdx = output?.links?.indexOf(removeId);
                    if (outIdx != null && outIdx !== -1) output.links.splice(outIdx, 1);
                    delete this.graph.links[removeId];
                }

                input.link = input.links[input.links.length - 1];
                this.setDirtyCanvas(false, true);
                this.graph?.connectionChange(this);
                return true;
            }

            return originalDisconnectInput.call(this, slotOrName);
        };

        // Native path for "right-click a specific wire -> Delete" (LiteGraph already detects clicks on a
        // link's own curve via its visible_links/showLinkMenu — no patch needed for that part). It knows
        // exactly which link was clicked, but without this it would still route through disconnectInput
        // with no id and remove the wrong one (whichever happens to be "most recent") on a multi-link slot.
        const originalRemoveLink = LGraph.prototype.removeLink;
        LGraph.prototype.removeLink = function (linkId) {
            const link = this.links[linkId];
            const targetNode = link && this.getNodeById(link.target_id);
            const input = targetNode?.inputs?.[link.target_slot];
            if (input?.allowMultiple) {
                targetNode.disconnectInput(link.target_slot, linkId);
                return;
            }
            originalRemoveLink.call(this, linkId);
        };

        // drawConnections itself is untouched (still draws exactly one curve per input, via input.link) —
        // this adds a second pass afterward for whatever else is sitting in a multi-link input's .links
        // array, reusing LiteGraph's own renderLink/getConnectionPos so the extra curves are pixel-identical
        // to native ones (and register in visible_links the same way, so right-click-to-delete works on them).
        const originalDrawConnections = LGraphCanvas.prototype.drawConnections;
        LGraphCanvas.prototype.drawConnections = function (ctx) {
            originalDrawConnections.call(this, ctx);

            for (const node of this.graph._nodes) {
                if (!node.inputs) continue;
                for (let slotIndex = 0; slotIndex < node.inputs.length; slotIndex++) {
                    const input = node.inputs[slotIndex];
                    if (!input?.allowMultiple || !Array.isArray(input.links) || input.links.length <= 1) continue;

                    for (const linkId of input.links) {
                        if (linkId === input.link) continue; // already drawn by the native pass above
                        const link = this.graph.links[linkId];
                        const originNode = link && this.graph.getNodeById(link.origin_id);
                        if (!originNode) continue;

                        const output = originNode.outputs[link.origin_slot];
                        const originPos = originNode.getConnectionPos(false, link.origin_slot);
                        const targetPos = node.getConnectionPos(true, slotIndex);
                        const startDir = output?.dir || (originNode.horizontal ? LiteGraph.DOWN : LiteGraph.RIGHT);
                        const endDir = input.dir || (node.horizontal ? LiteGraph.UP : LiteGraph.LEFT);
                        this.renderLink(ctx, originPos, targetPos, link, false, 0, null, startDir, endDir);
                    }
                }
            }
        };
    }

    // Ported from the old WinForms tool's ProcessGraphCanvas (HitTestEdge / DetachEdgeForRewiring), which
    // had this exact problem already solved well: LiteGraph only lets you grab an existing connection by
    // clicking precisely on its *dot* (input or output) — for an AllowMultiple slot that's ambiguous (which
    // of several wires touching that one dot do you mean?), so the original design instead makes wires
    // themselves grabbable, at either end, by clicking along the curve — and reserves each dot for always
    // starting a fresh new wire. Ported here at reduced scope for this first pass: same curve hit-testing
    // and nearest-end detach, but skipping the original's drag-start-threshold (a plain click here can
    // immediately pick up a wire, rather than requiring a small confirming drag first) and its magnetic
    // snap-to-port radius during the drag (LiteGraph's own default hit tolerance on drop still applies).
    const EDGE_CLICK_THRESHOLD_PX = 8; // screen pixels — matches the original tool's own tolerance exactly

    function patchEdgeBodyRewiring() {
        const originalProcessMouseDown = LGraphCanvas.prototype.processMouseDown;
        LGraphCanvas.prototype.processMouseDown = function (e) {
            if (!this.graph || this.connecting_node)
                return originalProcessMouseDown.call(this, e);

            pendingRewireInfo = null;

            // Blanket "before" snapshot for every left-button-ish gesture that could turn into a mutation
            // (node/group drag or resize, our edge-rewire grab, our multi-node drag, a native port-to-port
            // wire connect) — far simpler than hooking each one individually, and commitUndoableChange()
            // (called from processMouseUp) silently no-ops when nothing actually changed, so a plain click
            // or a box-select doesn't burn an undo slot. Node/group creation via a context-menu click and
            // Delete-key removal aren't mouse-gesture pairs at all, so those get their own begin/commit
            // wrapping at their own call sites instead (see instantiateNodeFromMenu, patchDeleteWithUndo,
            // and the 新增群組 callback below).
            beginUndoableChange();

            this.adjustMouseEvent(e);
            const point = [e.canvasX, e.canvasY];
            const isLeftButton = e.button === 0;

            // A click landing inside any node's box always belongs to that node (selecting/dragging it, or
            // one of its own port dots, which the original processMouseDown already handles) — only open
            // canvas space between nodes is fair game for grabbing a wire by its curve. Non-left buttons are
            // left to native handling here too: stock LiteGraph already gates node select/drag/port-drag
            // behind a left-button check internally, so a middle-click over a node falls through to its own
            // dragging_canvas branch same as empty canvas — this must not be short-circuited, or middle-click
            // panning breaks the moment the cursor is over a node.
            //
            // The trailing 5 is a hit-test margin in world units, matching LiteGraph's own native
            // processMouseDown (it calls getNodeOnPos with the same margin internally). Without it here,
            // this routing check was stricter than native's own, so a click within that margin — a near-miss
            // any real mouse produces constantly, especially on small nodes at typical zoom — fell through
            // to the empty-canvas branch below (forced into a ctrlKey box-select) instead of being treated as
            // a node click. A plain click there (no real drag) finds nothing to select AND clears whatever
            // was already selected via patchCaptureShiftOnMouseUp's own deselect-on-miss, i.e. exactly the
            // "click a node, the inspector flashes and disappears" bug — masked by dragging because a real
            // drag's larger rectangle is resolved via getBounding() overlap instead of this single point.
            const nodeHit = this.graph.getNodeOnPos(point[0], point[1], this.visible_nodes, 5);
            if (nodeHit) {
                // LiteGraph's OWN native rewire gesture: clicking directly on an input dot that already
                // has a link picks that link up and starts dragging its input end — same condition stock
                // processMouseDown itself uses (allow_reconnect_links defaults true here) — falls straight
                // through to originalProcessMouseDown below to actually do it; this only records which
                // link is about to go loose so showConnectionMenu can tell "moving an old line" apart from
                // "pulling a new one" at drop time.
                if (isLeftButton && (this.allow_reconnect_links || e.shiftKey)) {
                    const inputSlotIndex = this.isOverNodeInput(nodeHit, point[0], point[1]);
                    const input = inputSlotIndex !== -1 ? nodeHit.inputs[inputSlotIndex] : null;
                    const existingLink = input && input.link != null ? this.graph.links[input.link] : null;
                    if (existingLink)
                        pendingRewireInfo = { link: existingLink, looseNode: nodeHit, mouseDownPoint: point.slice() };
                }

                // Plain left-click on a node that's already part of a multi-selection: the original
                // WinForms tool's own special case (ProcessGraphCanvas.OnMouseDown's
                // _multiSelectedNodeGuids.Contains(node.Guid) -> StartMultiDrag) — drag the WHOLE
                // selection together instead of stock LiteGraph's default (selectNode with no modifier
                // always deselects everything else first, so only the clicked node would move). Ports and
                // the resize handle keep their normal single-node meaning even on a multi-selected node —
                // a shift/ctrl click keeps native's own add/remove-from-selection toggle behavior.
                if (isLeftButton && !e.shiftKey && !e.ctrlKey && !e.metaKey &&
                    this.selected_nodes[nodeHit.id] && Object.keys(this.selected_nodes).length > 1 &&
                    !isOverResizeCorner(nodeHit, point[0], point[1]) &&
                    this.isOverNodeInput(nodeHit, point[0], point[1]) === -1 &&
                    this.isOverNodeOutput(nodeHit, point[0], point[1]) === -1)
                    return beginMultiNodeDrag(this, e, nodeHit);

                return originalProcessMouseDown.call(this, e);
            }

            // Empty canvas space with a non-left button — leave it to native handling (e.g. middle-click
            // canvas panning) rather than our own left-click-only edge-grab gesture below.
            if (!isLeftButton)
                return originalProcessMouseDown.call(this, e);

            const hit = findNearestEdgeAtPoint(this, point, EDGE_CLICK_THRESHOLD_PX / this.ds.scale);
            if (hit) {
                // The original processMouseDown's very first job (before any hit-testing) is re-pointing
                // the move/up listeners from the canvas to the document — since a drag can legitimately
                // leave the canvas element while still being tracked. Returning early without this meant
                // our custom-started drag never received another mousemove/mouseup once bypassed here.
                LiteGraph.pointerListenerRemove(this.canvas, "move", this._mousemove_callback);
                LiteGraph.pointerListenerAdd(this.getCanvasWindow().document, "move", this._mousemove_callback, true);
                LiteGraph.pointerListenerAdd(this.getCanvasWindow().document, "up", this._mouseup_callback, true);

                beginRewireFromEdgeGrab(this, hit.link, hit.nearerToOrigin, point);
                e.stopPropagation();
                e.preventDefault();
                return false;
            }

            // Plain empty-canvas left-drag: the original WinForms tool's box-select gesture
            // (ProcessGraphCanvas's _pendingBoxSelectStartScreen) needs no modifier at all — Shift only
            // makes it additive — and the tool only ever pans via middle-click drag, never a plain
            // left-drag. Stock LiteGraph instead reserves box-select for ctrl+drag and treats a plain
            // left-drag on empty space as canvas panning (allow_dragcanvas), which is what made this
            // feature look entirely missing here. Forcing ctrlKey true reuses LiteGraph's own native
            // box-select bookkeeping (dragging_rectangle, drawn in draw(), resolved in processMouseUp)
            // instead of reimplementing it.
            //
            // A group only counts as "the user wants to drag it" when the point is over its title bar
            // (groupTitleBarHeight-tall strip reserved along its top edge — see recomputeGroupBounds and
            // drawFrozenLabels, which draw/measure the title in that same strip) — per explicit user
            // feedback that grabbing anywhere in a group's body made it impossible to box-select nodes
            // inside a group. Everywhere else in a group's box is treated as empty canvas. Native's own
            // getGroupOnPos (called unconditionally inside originalProcessMouseDown, regardless of
            // ctrlKey) is stubbed out for just that one nested call so it can't re-discover the group and
            // start a drag/resize anyway — restored immediately after, since other call sites (e.g. the
            // right-click "Edit Group" menu) still need the real one. This also fully removes group
            // resizing as a side effect: the resize-corner hit test only ever runs when a group was
            // grabbed in the first place, so a click on the corner (outside the title strip) now falls
            // through to box-select same as any other non-title point — see patchGroupRendering for the
            // matching removal of the drawn resize-handle triangle.
            const groupHit = this.graph.getGroupOnPos(point[0], point[1]);
            if (!groupHit)
                return originalProcessMouseDown.call(this, forceCtrlKey(e));

            if (isOverGroupTitleBar(groupHit, point[0], point[1]))
                return originalProcessMouseDown.call(this, e);

            this.graph.getGroupOnPos = () => null;
            try {
                return originalProcessMouseDown.call(this, forceCtrlKey(e));
            } finally {
                delete this.graph.getGroupOnPos; // falls back to LGraph.prototype.getGroupOnPos
            }
        };
    }

    function isOverGroupTitleBar(group, x, y) {
        return x >= group.pos[0] && x <= group.pos[0] + group.size[0] &&
            y >= group.pos[1] && y <= group.pos[1] + groupTitleBarHeight;
    }

    function forceCtrlKey(e) {
        return new Proxy(e, {
            get(target, prop) {
                if (prop === "ctrlKey") return true;
                const value = target[prop];
                return typeof value === "function" ? value.bind(target) : value;
            },
        });
    }

    // Same rect test LiteGraph's own native resize-corner hit test uses (a 10x10 world-space box at the
    // node's bottom-right corner) — kept in sync with it by inspection of the vendored source rather than
    // calling into it directly, since it isn't exposed as its own method.
    function isOverResizeCorner(node, x, y) {
        if ((node.flags && node.flags.collapsed) || node.resizable === false) return false;
        const rx = node.pos[0] + node.size[0] - 5;
        const ry = node.pos[1] + node.size[1] - 5;
        return rx < x && rx + 10 > x && ry < y && ry + 10 > y;
    }

    // Starts dragging the anchor node exactly the way LiteGraph's own native node-drag does (same document-
    // level move/up listener re-pointing as beginRewireFromEdgeGrab, same node_dragged field) — the rest of
    // the gesture (every selected node following node_dragged's delta each frame, position rounding and
    // onNodeMoved at drop) is entirely LiteGraph's own already-working processMouseMove/processMouseUp
    // machinery, since it drives itself off this.selected_nodes rather than node_dragged alone. Only
    // group-membership bookkeeping (updateNodeGroupMembership, which onNodeMoved only ever runs for the one
    // node LiteGraph calls it with) needs its own multi-node fan-out — see pendingMultiDragNodes.
    function beginMultiNodeDrag(canvasInstance, e, anchorNode) {
        LiteGraph.pointerListenerRemove(canvasInstance.canvas, "move", canvasInstance._mousemove_callback);
        LiteGraph.pointerListenerAdd(canvasInstance.getCanvasWindow().document, "move", canvasInstance._mousemove_callback, true);
        LiteGraph.pointerListenerAdd(canvasInstance.getCanvasWindow().document, "up", canvasInstance._mouseup_callback, true);

        pendingMultiDragNodes = Object.values(canvasInstance.selected_nodes);
        canvasInstance.graph.beforeChange();
        canvasInstance.node_dragged = anchorNode;
        canvasInstance.last_mouse[0] = e.clientX;
        canvasInstance.last_mouse[1] = e.clientY;
        canvasInstance.dirty_canvas = true;

        e.stopPropagation();
        e.preventDefault();
        return false;
    }

    function findNearestEdgeAtPoint(canvasInstance, point, worldThreshold) {
        const graphRef = canvasInstance.graph;
        let best = null;
        let bestDist = worldThreshold;

        for (const linkId in graphRef.links) {
            const link = graphRef.links[linkId];
            if (!link) continue;
            const originNode = graphRef.getNodeById(link.origin_id);
            const targetNode = graphRef.getNodeById(link.target_id);
            if (!originNode || !targetNode) continue;

            const from = originNode.getConnectionPos(false, link.origin_slot);
            const to = targetNode.getConnectionPos(true, link.target_slot);
            // Same control-point construction as the curve LiteGraph itself renders (renderLink): a
            // horizontal-tangent cubic bezier, so the sampled curve matches what's actually drawn on screen.
            const dx = Math.max(40, Math.abs(to[0] - from[0]) * 0.5);
            const p1 = [from[0] + dx, from[1]];
            const p2 = [to[0] - dx, to[1]];

            // Sample density scales with the curve's (approximate) length so a long wire doesn't have gaps
            // wide enough for a click to fall through between samples.
            const approxLength = distanceBetween(from, p1) + distanceBetween(p1, p2) + distanceBetween(p2, to);
            const sampleCount = Math.min(300, Math.max(12, Math.round(approxLength / 6)));

            for (let i = 0; i <= sampleCount; i++) {
                const sampled = sampleCubicBezier(from, p1, p2, to, i / sampleCount);
                const dist = distanceBetween(sampled, point);
                if (dist < bestDist) {
                    bestDist = dist;
                    best = { link: link, nearerToOrigin: distanceBetween(from, point) <= distanceBetween(to, point) };
                }
            }
        }

        return best;
    }

    function distanceBetween(a, b) {
        const dx = a[0] - b[0], dy = a[1] - b[1];
        return Math.sqrt(dx * dx + dy * dy);
    }

    function sampleCubicBezier(p0, p1, p2, p3, t) {
        const mt = 1 - t;
        const a = mt * mt * mt, b = 3 * mt * mt * t, c = 3 * mt * t * t, d = t * t * t;
        return [
            a * p0[0] + b * p1[0] + c * p2[0] + d * p3[0],
            a * p0[1] + b * p1[1] + c * p2[1] + d * p3[1],
        ];
    }

    // Detaches the grabbed link completely (regardless of which end was clicked — a rewire always yields
    // either a brand new link on successful reconnect, or none at all if dropped in empty space) and hands
    // off to LiteGraph's own connecting_node/connecting_input/connecting_output drag state — the exact same
    // state its native "grab an input's existing link" gesture sets up, so the rest of the gesture (drag
    // preview, drop-on-a-compatible-port reconnects, drop-elsewhere leaves it deleted) is LiteGraph's own
    // already-working machinery, not anything reimplemented here.
    function beginRewireFromEdgeGrab(canvasInstance, link, nearerToOrigin, mouseDownPoint) {
        const graphRef = canvasInstance.graph;
        const originNode = graphRef.getNodeById(link.origin_id);
        const targetNode = graphRef.getNodeById(link.target_id);
        if (!originNode || !targetNode) return;

        pendingRewireInfo = {
            link: link,
            looseNode: nearerToOrigin ? originNode : targetNode,
            mouseDownPoint: mouseDownPoint.slice(),
        };

        targetNode.disconnectInput(link.target_slot, link.id);

        if (nearerToOrigin) {
            // Grabbed near the output (origin) end: that end goes loose, the input side anchors and stays
            // put — connecting_input means "dragging this loose end, looking for an output to attach to".
            canvasInstance.connecting_node = targetNode;
            canvasInstance.connecting_input = targetNode.inputs[link.target_slot];
            canvasInstance.connecting_input.slot_index = link.target_slot;
            canvasInstance.connecting_slot = link.target_slot;
            canvasInstance.connecting_pos = targetNode.getConnectionPos(true, link.target_slot);
        } else {
            // Grabbed near the input end: that end goes loose, the output side anchors and stays put.
            canvasInstance.connecting_node = originNode;
            canvasInstance.connecting_output = originNode.outputs[link.origin_slot];
            canvasInstance.connecting_slot = link.origin_slot;
            canvasInstance.connecting_pos = originNode.getConnectionPos(false, link.origin_slot);
        }

        canvasInstance.dirty_bgcanvas = canvasInstance.dirty_canvas = true;
    }

    // Replaces LiteGraph's stock "Add Node" menus. The node-type list itself stays the FLAT, MenuName-sorted
    // list ported from the old WinForms tool's BuildContextMenu/ShowWiringCreateNodeMenu (ProcessGraphCanvas.cs)
    // — MenuName only ever sorts, never groups into clickable categories. Where the two call sites differ:
    // the empty-canvas right-click menu (getCanvasMenuOptions) nests that flat list one level down, behind a
    // single "新增節點" submenu entry (explicit user request, reversing an earlier "keep it fully flat"
    // request — the earlier version had it fully expanded at the top level); the wire-drop menu
    // (showConnectionMenu) still shows its flat per-compatible-port list with no extra wrapping, matching the
    // original tool's ShowWiringCreateNodeMenu exactly. Overriding both call sites directly (rather than the
    // shared static onMenuAdd hook they both used to funnel through) also removes LiteGraph's own
    // "Add Node"/"Search" wrapper level that stock showConnectionMenu otherwise inserts before ever reaching
    // onMenuAdd.
    function patchAddNodeMenu() {
        // Off by default in stock LiteGraph: dropping a wire on empty space would otherwise just leave it
        // detached with no menu at all. Turning this on is what makes showConnectionMenu (and therefore the
        // override below) fire on an empty-space drop, restoring the original tool's behavior.
        LiteGraph.release_link_on_empty_shows_menu = true;

        // Right-click on empty canvas. Stock getCanvasMenuOptions() return value becomes the ENTIRE
        // top-level menu with no extra wrapping (confirmed by reading the vendored source), so building our
        // own array here — instead of leaving stock's {content:"Add Node", has_submenu:true} entry in place
        // — controls exactly what nests and what doesn't.
        LGraphCanvas.prototype.getCanvasMenuOptions = function () {
            const worldPos = this.graph_mouse ? this.graph_mouse.slice() : [0, 0];
            const moveToGroupItems = buildMoveToGroupItems(this);
            const items = [
                {
                    content: "新增群組",
                    callback: () => {
                        beginUndoableChange();
                        const group = new LiteGraph.LGraphGroup(" ");
                        group._displayTitle = "新群組";
                        group.pos = [worldPos[0], worldPos[1]];
                        group.size = [200, 100];
                        this.graph.add(group);
                        this.setDirty(true, true);
                        commitUndoableChange();
                    },
                },
                null,
                {
                    content: "移動至群組",
                    has_submenu: true,
                    disabled: moveToGroupItems.length === 0,
                    submenu: { title: "移動至群組", options: moveToGroupItems },
                },
                null,
                {
                    content: "新增節點",
                    has_submenu: true,
                    submenu: { title: "新增節點", options: buildFlatAddNodeItems(this, null) },
                },
            ];
            return items;
        };

        // Dropping a dragged wire on empty canvas space. Bypasses stock showConnectionMenu's own body
        // entirely (which would otherwise show an "Add Node"/"Search" chooser first) and goes straight to
        // the flat per-compatible-port list, exactly like the original's ShowWiringCreateNodeMenu.
        LGraphCanvas.prototype.showConnectionMenu = function (options) {
            const opts = options || {};

            // Consumed once per gesture regardless of outcome — a stale flag must never leak into the
            // NEXT unrelated wire drag.
            const rewire = pendingRewireInfo;
            pendingRewireInfo = null;
            if (rewire && shouldSnapRewireBack(this, rewire, opts.e)) {
                reconnectOriginalLink(this, rewire.link);
                return false;
            }

            const anchorIsOutput = !!(opts.nodeFrom && opts.slotFrom);
            if (!anchorIsOutput && !(opts.nodeTo && opts.slotTo)) {
                console.warn("storyForgeGraph: showConnectionMenu called with no anchor");
                return false;
            }

            const wireContext = {
                anchorIsOutput: anchorIsOutput,
                fromNode: opts.nodeFrom || null,
                fromSlot: opts.slotFrom || null,
                toNode: opts.nodeTo || null,
                toSlot: opts.slotTo || null,
            };
            const items = buildFlatAddNodeItems(this, wireContext);
            if (items.length === 0) return false;

            new LiteGraph.ContextMenu(items, { event: opts.e }, this.getCanvasWindow());
            return false;
        };
    }

    // Ported from the WinForms tool's own grace for a rewire drop (ProcessGraphCanvas.OnMouseUp's
    // DragStartThreshold/IsNearNode checks): a rewire that barely moved from its mousedown point, or that
    // ended back near the node the loose end came from, reads as "didn't really mean to move this" rather
    // than "drop it here" — only a rewire dragged genuinely far from both should ever reach the add-node
    // menu, same as a brand-new wire always does.
    function shouldSnapRewireBack(canvasInstance, rewire, dropEvent) {
        if (!dropEvent) return true;
        const dropPoint = [dropEvent.canvasX, dropEvent.canvasY];
        const scale = canvasInstance.ds.scale;
        if (distanceBetween(rewire.mouseDownPoint, dropPoint) < REWIRE_DRAG_THRESHOLD_PX / scale)
            return true;
        return isNearNodeBox(rewire.looseNode, dropPoint, REWIRE_SNAPBACK_BUFFER_PX / scale);
    }

    function isNearNodeBox(node, point, buffer) {
        return point[0] >= node.pos[0] - buffer && point[0] <= node.pos[0] + node.size[0] + buffer &&
            point[1] >= node.pos[1] - buffer && point[1] <= node.pos[1] + node.size[1] + buffer;
    }

    // Recreates the exact same two endpoints the rewire detached — a fresh LLink object with a new id
    // (LiteGraph has no "undo the detach" primitive), but edges round-trip through 存檔 purely by
    // (fromNodeGuid, fromPort, toNodeGuid, toPort) — see exportGraph/captureSnapshot — so nothing is lost.
    function reconnectOriginalLink(canvasInstance, link) {
        const originNode = canvasInstance.graph.getNodeById(link.origin_id);
        const targetNode = canvasInstance.graph.getNodeById(link.target_id);
        if (originNode && targetNode)
            originNode.connect(link.origin_slot, targetNode, link.target_slot);
        canvasInstance.dirty_bgcanvas = canvasInstance.dirty_canvas = true;
    }

    // wireContext is null for a plain canvas right-click (one item per node type); non-null for a
    // wire-drop, where — matching ShowWiringCreateNodeMenu exactly — it's one item PER COMPATIBLE PORT
    // (an output anchor needs one of the new node's inputs, and vice versa), labeled with that port's name
    // since a type can expose more than one compatible port (e.g. an AND node's several condition inputs).
    function buildFlatAddNodeItems(canvasInstance, wireContext) {
        const types = (currentPayload?.nodeTypes || []).slice().sort((a, b) => {
            const ga = a.menuName || "其他", gb = b.menuName || "其他";
            return ga < gb ? -1 : ga > gb ? 1 : 0;
        });

        const items = [];
        for (const typeDto of types) {
            if (wireContext) {
                const candidatePorts = wireContext.anchorIsOutput ? typeDto.inputs : typeDto.outputs;
                for (const port of candidatePorts) {
                    items.push({
                        content: `新增：${typeDto.displayName} → ${port.portName}`,
                        callback: (item, menuOptions, clickEvent, menuInstance) =>
                            createNodeForWire(canvasInstance, typeDto.typeName, port, wireContext,
                                menuInstance.getFirstEvent()),
                    });
                }
            } else {
                items.push({
                    content: `新增：${typeDto.displayName}`,
                    callback: (item, menuOptions, clickEvent, menuInstance) =>
                        createNode(canvasInstance, typeDto.typeName, menuInstance.getFirstEvent()),
                });
            }
        }
        return items;
    }

    // Lists every NAMED group in the graph (not just those currently scrolled into view) for the empty-canvas
    // menu's "移動至群組" submenu. A blank-titled group (_displayTitle "") is excluded — it renders no label
    // on the canvas either (see drawFrozenLabels' own `if (!group._displayTitle) continue`), so a menu entry
    // for it would just be an unlabeled, unidentifiable item. Sorted with numeric:true so "A2" sorts before
    // "A10" instead of after it.
    function buildMoveToGroupItems(canvasInstance) {
        const groups = (canvasInstance.graph._groups || [])
            .filter(group => group._displayTitle)
            .slice()
            .sort((a, b) => a._displayTitle.localeCompare(b._displayTitle, undefined, { numeric: true }));

        return groups.map(group => ({
            content: group._displayTitle,
            callback: () => focusOnGroup(canvasInstance, group),
        }));
    }

    // Pans so the group's center lands at the canvas center — deliberately leaves ds.scale untouched (unlike
    // focusOnNode's search-jump, which raises zoom to a minimum), and selects nothing: this app has no
    // group-selection concept to drive.
    function focusOnGroup(canvasInstance, group) {
        const canvasEl = canvasInstance.canvas;
        const cssWidth = canvasEl.clientWidth;
        const cssHeight = canvasEl.clientHeight;

        const centerX = group.pos[0] + group.size[0] / 2;
        const centerY = group.pos[1] + group.size[1] / 2;
        canvasInstance.ds.offset[0] = cssWidth / (2 * canvasInstance.ds.scale) - centerX;
        canvasInstance.ds.offset[1] = cssHeight / (2 * canvasInstance.ds.scale) - centerY;
        canvasInstance.setDirty(true, true);
    }

    // Shared setup for a client-created node: instantiate, position at the triggering event (matching
    // stock onMenuAdd's own convertEventToCanvasOffset placement), and register a client-generated guid so
    // it round-trips through 存檔 correctly (see exportGraph's newNodes and GraphEditorState.ApplyAndSave,
    // which creates the matching StoryForge.Core node using this exact guid rather than a new random one).
    function instantiateNodeFromMenu(canvasInstance, typeName, originEvent) {
        const liteType = "storyforge/" + sanitize(typeName);
        const node = LiteGraph.createNode(liteType);
        if (!node) {
            console.error("storyForgeGraph: unknown node type", typeName);
            return null;
        }

        beginUndoableChange();

        const typeDto = currentPayload.nodeTypes.find(t => t.typeName === typeName);
        node.pos = canvasInstance.convertEventToCanvasOffset(originEvent);
        const naturalSize = node.computeSize();
        node.size = naturalSize;
        node.title = " "; // see init()'s node loop for why not ""
        node._displayTitle = typeDto ? typeDto.displayName : typeName;
        node._hasIdentityTitle = false; // a just-created node has no playscriptId/flagId value yet
        node._baseWidth = node.size[0];
        node.onDrawTitleBox = function () {};

        canvasInstance.graph.add(node);

        const guid = crypto.randomUUID();
        nodesByGuid.set(guid, node);
        guidByNode.set(node, guid);
        newNodeGuids.add(guid);
        currentPayload.nodes.push({
            guid: guid, typeName: typeName, title: node._displayTitle,
            x: node.pos[0], y: node.pos[1], width: node.size[0], height: node.size[1], fields: {},
        });

        // Without this, the node exists only in this JS module's own bookkeeping until 存檔 — selecting it
        // to edit its fields in the inspector sidebar would find nothing, since GraphEditorState.GetNodeFields
        // /UpdateNodeField both read/write the live C# model, which wouldn't know this node exists yet. This
        // makes it real in the model immediately (GraphEditorState.CreateNode, mirroring the same
        // AddNode(schema, x, y, guid) call ApplyAndSave's NewNodes loop makes at 存檔 time — safe to also run
        // there since it no-ops on a guid that's already present).
        selfRef?.invokeMethodAsync("OnNodeCreated", guid, typeName, node.pos[0], node.pos[1]);

        return node;
    }

    // Auto-selects the newly created node — matching the old WinForms tool's own CreateNodeAndConnectWiring
    // (SelectNode(newNode)) — which also opens our inspector sidebar on it immediately via the normal
    // onSelectionChange path, ready to edit.
    function createNode(canvasInstance, typeName, originEvent) {
        const node = instantiateNodeFromMenu(canvasInstance, typeName, originEvent);
        if (!node) return;
        canvasInstance.selectNode(node);
        canvasInstance.setDirty(true, true);
        commitUndoableChange();
    }

    // Connects the new node to the wire's anchor at exactly the port the user picked — precise port-index
    // matching rather than LiteGraph's type-based connectByType/connectByTypeOutput, since every one of our
    // slots shares the same (empty) LiteGraph "type" and so isn't type-distinguishable at all.
    function createNodeForWire(canvasInstance, typeName, port, wireContext, originEvent) {
        const node = instantiateNodeFromMenu(canvasInstance, typeName, originEvent);
        if (!node) return;

        if (wireContext.anchorIsOutput) {
            const anchorSlotIndex = wireContext.fromNode.outputs.indexOf(wireContext.fromSlot);
            const newNodeSlotIndex = node.findInputSlot(port.portName);
            if (anchorSlotIndex !== -1 && newNodeSlotIndex !== -1)
                wireContext.fromNode.connect(anchorSlotIndex, node, newNodeSlotIndex);
        } else {
            const anchorSlotIndex = wireContext.toNode.inputs.indexOf(wireContext.toSlot);
            const newNodeSlotIndex = node.findOutputSlot(port.portName);
            if (anchorSlotIndex !== -1 && newNodeSlotIndex !== -1)
                node.connect(newNodeSlotIndex, wireContext.toNode, anchorSlotIndex);
        }

        canvasInstance.selectNode(node);
        canvasInstance.setDirty(true, true);
        commitUndoableChange();
    }

    // Stock LiteGraph opens its own "Properties" panel (title/mode/color widgets + a Delete button) on
    // double-clicking a node — confusing here since it duplicates our own inspector sidebar and exposes
    // raw LiteGraph internals (node "mode", color swatches) that mean nothing in this app. The original
    // WinForms tool never had any double-click behavior at all, so this just suppresses it rather than
    // replacing it with something else.
    function patchDisableNodePropertiesPanel() {
        LGraphCanvas.prototype.showShowNodePanel = function () {};
    }

    // Stock LiteGraph's right-click-on-a-node menu (Inputs/Outputs/Properties/Title/Mode/Resize/Collapse/
    // Pin/Colors/Shapes/Clone/Remove, all in English) was pure clutter here per earlier explicit user
    // feedback, and got removed outright (getNodeMenuOptions returning null). This replaces it with our own
    // app-specific menu: 開啟劇本檔/複製劇本名稱 (劇本 nodes only) plus 刪除/複製 (every node).
    //
    // Overriding processContextMenu itself, not just getNodeMenuOptions, because two of those four items
    // need a live server read (whether the .md 劇本檔 actually exists on disk yet, the current playscriptId)
    // that getNodeMenuOptions can't provide — LiteGraph calls it synchronously and paints the menu from
    // whatever it returns immediately, with no way to await anything. processContextMenu is the entry point
    // one level up (called directly from processMouseDown on a right-click), so overriding it gives an
    // async foothold before the menu ever appears. A right-click on empty canvas or a slot (node is
    // null/undefined) goes to showCanvasContextMenu below, not the stock original — needed so the
    // getCanvasMenuOptions patch's 新增節點/移動至群組 submenus can open on hover (see that function's own
    // comment for why delegating to the original no longer works once that menu has submenus in it).
    function patchNodeContextMenu() {
        LGraphCanvas.prototype.processContextMenu = function (node, event) {
            if (!node) {
                showCanvasContextMenu(this, event);
                return false;
            }

            showNodeContextMenu(this, node, event);
            return false;
        };
    }

    // Reimplements stock processContextMenu's own null-node branch (confirmed by reading the vendored
    // source: getCanvasMenuOptions() plus, when the click landed on top of a group, an appended "Edit Group"
    // submenu built from graph.getGroupOnPos/getGroupMenuOptions) rather than delegating to it, purely to add
    // autoopen:true — the ONLY way a submenu opens on hover instead of needing an extra click to expand.
    // LiteGraph reads that flag once, off the top-level ContextMenu's own options, and threads it down to
    // every nested submenu (new nested ContextMenu instances each get `autoopen: d.autoopen` from their
    // parent) — there is no per-item way to request it, so it has to be set here, at top-level construction.
    function showCanvasContextMenu(canvasInstance, event) {
        const items = canvasInstance.getCanvasMenuOptions();
        const group = canvasInstance.graph.getGroupOnPos(event.canvasX, event.canvasY);
        if (group) {
            items.push(null, {
                content: "Edit Group",
                has_submenu: true,
                submenu: { title: "Group", extra: group, options: canvasInstance.getGroupMenuOptions(group) },
            });
        }
        new LiteGraph.ContextMenu(items, { event: event, autoopen: true }, canvasInstance.getCanvasWindow());
    }

    // this.selected_nodes is already exactly right by the time this runs: LiteGraph's own (unpatched)
    // right-click mousedown handling replaces the selection with just the clicked node unless it's already
    // part of a multi-selection (or Shift/Ctrl/Cmd is held) — the same selected_nodes-driven scoping native
    // LiteGraph's own onMenuNodeClone/onMenuNodeRemove use. 刪除/複製 act on that whole selection; 開啟劇本檔
    // /複製劇本名稱 always act on the single right-clicked node specifically — "open/copy the name of THIS
    // script" has no sensible multi-node meaning.
    async function showNodeContextMenu(canvasInstance, node, event) {
        const guid = guidByNode.get(node);
        const typeName = liteTypeToTypeName.get(node.type);
        const typeDto = currentPayload.nodeTypes.find(t => t.typeName === typeName);
        const isPlayscriptNode = !!(typeDto && typeDto.identityFields.includes("playscriptId"));

        let playscriptName = "";
        let hasScriptFile = false;
        if (isPlayscriptNode && guid) {
            const info = await selfRef.invokeMethodAsync("GetNodeContextMenuInfo", guid);
            playscriptName = (info && info.playscriptName) || "";
            hasScriptFile = !!(info && info.hasScriptFile);
        }

        const items = [];
        if (isPlayscriptNode) {
            items.push({
                content: "開啟劇本檔",
                disabled: !hasScriptFile,
                callback: () => selfRef.invokeMethodAsync("OpenScriptTableForGuid", guid),
            });
            items.push({
                content: "複製劇本名稱",
                disabled: !playscriptName,
                callback: () => copyTextToClipboard(playscriptName),
            });
            items.push(null);
        }
        items.push({ content: "刪除", callback: () => canvasInstance.deleteSelectedNodes() });
        items.push({ content: "複製", callback: () => duplicateSelectedNodes(canvasInstance) });

        new LiteGraph.ContextMenu(items, { event: event }, canvasInstance.getCanvasWindow());
    }

    function copyTextToClipboard(text) {
        if (!text || !navigator.clipboard || !navigator.clipboard.writeText)
            return;
        navigator.clipboard.writeText(text).catch(() => {});
    }

    // 複製 — clones every currently-selected node (see showNodeContextMenu's own comment on why that's
    // already the right set to act on). Every field — including playscriptId itself, deliberately not
    // cleared: a duplicate is allowed to temporarily share its source's script name, the user renames it
    // afterward — and, for a 劇本 node, its memo/衝突/變化 content round-trip through
    // GraphEditorState.DuplicateNode, since none of that lives in JS at all (fields/content are
    // server-side-only state — see the module-level comment on server sync being explicit, not automatic).
    // Connections to/from every duplicated node are recreated too: both links within the duplicated set and
    // links out to untouched neighbors, EXCEPT into an external neighbor's single-link (non allowMultiple)
    // input that's already occupied — recreating that one too would silently evict the original node's
    // existing wire (see patchLiteGraphForMultiInputLinks's own note on exactly that failure mode), which
    // would make 複製 a destructive action on an unrelated node. Skipping just that one connection is a far
    // smaller surprise than quietly breaking someone else's link.
    async function duplicateSelectedNodes(canvasInstance) {
        const sourceNodes = Object.values(canvasInstance.selected_nodes || {});
        if (sourceNodes.length === 0)
            return;

        beginUndoableChange();

        // Snapshotted before any connect() calls below start mutating graph.links mid-iteration.
        const originalLinks = Object.values(canvasInstance.graph.links).filter(Boolean);

        const guidMap = new Map(); // source guid -> new duplicate guid
        const createdNodes = [];
        for (const sourceNode of sourceNodes) {
            const sourceGuid = guidByNode.get(sourceNode);
            const typeName = liteTypeToTypeName.get(sourceNode.type);
            if (!sourceGuid || !typeName)
                continue;

            const liteType = "storyforge/" + sanitize(typeName);
            const newNode = LiteGraph.createNode(liteType);
            if (!newNode)
                continue;

            const newGuid = crypto.randomUUID();
            newNode.pos = [sourceNode.pos[0] + 30, sourceNode.pos[1] + 30];
            const naturalSize = newNode.computeSize();
            newNode.size = [Math.max(sourceNode._baseWidth || 0, naturalSize[0]), naturalSize[1]];
            newNode.title = " "; // see init()'s node loop for why not ""
            newNode.onDrawTitleBox = function () {};
            canvasInstance.graph.add(newNode);
            nodesByGuid.set(newGuid, newNode);
            guidByNode.set(newNode, newGuid);
            newNodeGuids.add(newGuid);
            guidMap.set(sourceGuid, newGuid);

            const result = await selfRef.invokeMethodAsync(
                "DuplicateNode", sourceGuid, newGuid, newNode.pos[0], newNode.pos[1]);
            if (!result) continue;

            newNode._displayTitle = result.title;
            newNode._hasIdentityTitle = result.hasIdentityTitle;
            newNode._baseWidth = newNode.size[0];

            currentPayload.nodes.push({
                guid: newGuid, typeName: typeName, title: result.title,
                x: newNode.pos[0], y: newNode.pos[1], width: newNode.size[0], height: newNode.size[1],
                fields: result.fields,
            });

            createdNodes.push(newNode);
        }

        for (const link of originalLinks) {
            const fromNode = canvasInstance.graph.getNodeById(link.origin_id);
            const toNode = canvasInstance.graph.getNodeById(link.target_id);
            const fromDupGuid = guidMap.get(guidByNode.get(fromNode));
            const toDupGuid = guidMap.get(guidByNode.get(toNode));
            if (!fromDupGuid && !toDupGuid)
                continue;

            const newFromNode = fromDupGuid ? nodesByGuid.get(fromDupGuid) : fromNode;
            const newToNode = toDupGuid ? nodesByGuid.get(toDupGuid) : toNode;
            if (!newFromNode || !newToNode)
                continue;

            const targetInput = newToNode.inputs && newToNode.inputs[link.target_slot];
            if (!toDupGuid && targetInput && !targetInput.allowMultiple && targetInput.link != null)
                continue; // would silently steal an untouched neighbor's existing single-link wire

            newFromNode.connect(link.origin_slot, newToNode, link.target_slot);
        }

        if (createdNodes.length > 0)
            canvasInstance.selectNodes(createdNodes);
        canvasInstance.setDirty(true, true);
        commitUndoableChange();
    }

    // Delete key (native processKey, gated on the canvas having focus and the event not targeting a text
    // input — see this component's own tabindex="0" comment) isn't a mousedown/mouseup pair, so it needs
    // its own begin/commit wrapping rather than relying on the blanket coverage in patchEdgeBodyRewiring.
    function patchDeleteWithUndo() {
        const originalDeleteSelectedNodes = LGraphCanvas.prototype.deleteSelectedNodes;
        LGraphCanvas.prototype.deleteSelectedNodes = function () {
            beginUndoableChange();
            originalDeleteSelectedNodes.call(this);
            commitUndoableChange();
        };
    }

    // Ctrl+Z / Ctrl+Y (and Ctrl+Shift+Z as a Redo alias) — stock LiteGraph's own processKey has no undo/redo
    // at all (only Ctrl+A select-all, Ctrl+C/V clipboard, Delete/Backspace), so "z"/"y" are free to claim.
    // Guards against an editable target the same way native's own Delete-key handling does, so this doesn't
    // fire while the user is typing in the inspector sidebar's text inputs.
    function patchUndoRedoKeys() {
        const originalProcessKey = LGraphCanvas.prototype.processKey;
        LGraphCanvas.prototype.processKey = function (e) {
            const targetName = e.target && e.target.localName;
            const isEditableTarget = targetName === "input" || targetName === "textarea";
            if (!isEditableTarget && e.type === "keydown" && (e.ctrlKey || e.metaKey)) {
                const key = (e.key || "").toLowerCase();
                if (key === "z" && !e.shiftKey) {
                    performUndo();
                    e.preventDefault();
                    return false;
                }
                if (key === "y" || (key === "z" && e.shiftKey)) {
                    performRedo();
                    e.preventDefault();
                    return false;
                }
            }
            return originalProcessKey.call(this, e);
        };
    }

    // Native drawGroups (LGraphCanvas.prototype) always draws a resize-handle triangle at each group's
    // bottom-right corner, regardless of whether resizing is actually reachable — misleading now that
    // dragging that corner no longer resizes anything (patchEdgeBodyRewiring restricts group-drag/resize
    // to the title bar only, and the corner falls outside it), so it's dropped here. Mirrors native's own
    // drawGroups exactly (same fill/stroke/title-text steps, from the vendored litegraph.min.js source)
    // minus the triangle draw call — there's no per-group flag to suppress just that piece.
    function patchGroupRendering() {
        LGraphCanvas.prototype.drawGroups = function (_unused, ctx) {
            if (!this.graph) return;
            const groups = this.graph._groups;
            ctx.save();
            ctx.globalAlpha = 0.5 * this.editor_alpha;
            for (const group of groups) {
                if (!LiteGraph.overlapBounding(this.visible_area, group._bounding)) continue;
                ctx.fillStyle = group.color || "#335";
                ctx.strokeStyle = group.color || "#335";
                const pos = group._pos, size = group._size;
                ctx.globalAlpha = 0.25 * this.editor_alpha;
                ctx.beginPath();
                ctx.rect(pos[0] + 0.5, pos[1] + 0.5, size[0], size[1]);
                ctx.fill();
                ctx.globalAlpha = this.editor_alpha;
                ctx.stroke();
                const fontSize = group.font_size || LiteGraph.DEFAULT_GROUP_FONT_SIZE;
                ctx.font = fontSize + "px Arial";
                ctx.textAlign = "left";
                ctx.fillText(group.title, pos[0] + 4, pos[1] + fontSize);
            }
            ctx.restore();
        };
    }

    // <canvas> has two independent sizes: its CSS box (set by our flexbox layout) and its backing-store
    // resolution (the width/height *attributes*, in physical pixels). Leaving the backing store at 1
    // pixel per CSS pixel is fine on a standard-DPI display, but on any scaled display (125%/150% Windows
    // scaling, Retina, etc.) the browser then stretches that low-res buffer up to fill the CSS box —
    // the blurry/blocky look this fixes, by matching the backing store to the real device pixel count.
    //
    // LiteGraph itself only ever reasons in CSS pixels (its own mouse handling maps clientX/clientY
    // straight onto its ds.scale/offset world-space transform with no devicePixelRatio division) — proven
    // empirically: dispatching a synthetic click at a node's raw ds-computed pixel position hit it, while
    // dividing that position by devicePixelRatio first missed it entirely. So ds.scale/offset (set in
    // fitView, from clientWidth/clientHeight) must stay in CSS-pixel terms; the extra device pixel density
    // is applied as a context transform LiteGraph never has to know about, via ctx.scale — setTransform
    // first makes this idempotent no matter how many times a resize calls it, instead of compounding.
    function fixCanvasResolution(canvasEl) {
        const ratio = window.devicePixelRatio || 1;
        const cssWidth = canvasEl.clientWidth;
        const cssHeight = canvasEl.clientHeight;
        canvasEl.width = Math.round(cssWidth * ratio);
        canvasEl.height = Math.round(cssHeight * ratio);

        const ctx = canvasEl.getContext("2d");
        ctx.setTransform(ratio, 0, 0, ratio, 0, 0);
    }

    // Shared node/edge/group construction, factored out of init() so restoreSnapshot (undo/redo) can rebuild
    // the graph from a snapshot exactly the same way init() builds it from the server payload — the two
    // shapes line up field-for-field (guid/typeName/x/y/width/height/title/hasIdentityTitle for nodes,
    // fromNodeGuid/fromPort/toNodeGuid/toPort for edges, clientId/title/x/y/width/height/color/
    // memberNodeGuids for groups) by design. Assumes graph/nodesByGuid/guidByNode have already been reset by
    // the caller and node TYPES are already registered (registerNodeTypes only ever needs to run once, at
    // the real init() — a snapshot never contains a type the server payload didn't already register).
    function buildGraphFromData(nodeDtos, edgeDtos, groupDtos) {
        for (const groupDto of groupDtos) {
            // " " not "" — an empty title makes LiteGraph fall back to drawing a default/type label of
            // its own (that's what the ghostly second line of text behind our own title actually was, not
            // a background-canvas caching issue as first suspected). A space is falsy-safe but invisible.
            const group = new LiteGraph.LGraphGroup(" ");
            group._displayTitle = groupDto.title;
            group.pos = [groupDto.x, groupDto.y];
            group.size = [groupDto.width, groupDto.height];
            group.color = groupDto.color;
            // _clientId round-trips this group back to the matching StoryForge.Core GraphGroupVm at 存檔
            // (GraphEditorState.ApplyAndSave looks groups up by this exact id). _memberGuids is this
            // group's own membership set — see updateNodeGroupMembership/recomputeGroupBounds below; a
            // plain Set of guid strings rather than direct node references so it stays valid across a node
            // being recreated (e.g. by undo) without needing to be rebuilt.
            group._clientId = groupDto.clientId;
            group._memberGuids = new Set(groupDto.memberNodeGuids || []);
            graph.add(group);
        }

        for (const nodeDto of nodeDtos) {
            const liteType = "storyforge/" + sanitize(nodeDto.typeName);
            const node = LiteGraph.createNode(liteType);
            if (!node) {
                console.error("storyForgeGraph: unknown node type", nodeDto.typeName);
                continue;
            }
            node.pos = [nodeDto.x, nodeDto.y];
            // The saved width/height came from the old tool's layout, back when a multi-connection field
            // needed one row per wire (see the AllowMultiple slot-splitting this port used to do, before
            // patchLiteGraphForMultiInputLinks made a single slot able to hold them all) — so it's often
            // taller than this node needs now that it's back to exactly one row per field. computeSize()
            // asks LiteGraph itself for the box that actually fits the ports this node has today; only the
            // saved width is still honored (as a minimum, not a fixed value — a node with a short title
            // shouldn't shrink from whatever width the original graph laid it out at).
            const naturalSize = node.computeSize();
            node.size = [Math.max(nodeDto.width || 0, naturalSize[0]), naturalSize[1]];
            // " " not "" — see the group loop above for why an actually-empty title backfires.
            node.title = " ";
            node._displayTitle = nodeDto.title;
            node._hasIdentityTitle = !!nodeDto.hasIdentityTitle;
            // LiteGraph also draws a small default color badge in the title bar's corner regardless of
            // title text — an unset boxcolor rendered as a plain gray circle sitting on top of our label.
            // No-op override is the officially-supported way to suppress it (see drawNodeShape: it only
            // draws the badge itself when the node has no onDrawTitleBox of its own).
            node.onDrawTitleBox = function () {};
            // A reasonable width at "natural" (unfrozen) zoom — drawFrozenLabels grows this further, every
            // frame, for whatever the *current* zoom actually needs, all the way down to the frozen floor;
            // this is just a stable starting point so a node doesn't visibly jump in width on first render.
            canvas.ctx.font = NATURAL_NODE_WORLD_SIZE + "px sans-serif";
            const titleWidth = canvas.ctx.measureText(nodeDto.title).width + 8;
            if (node.size[0] < titleWidth)
                node.size[0] = titleWidth;
            node._baseWidth = node.size[0];
            graph.add(node);
            nodesByGuid.set(nodeDto.guid, node);
            guidByNode.set(node, nodeDto.guid);
        }

        const nodeTypeByGuid = new Map(nodeDtos.map(n => [n.guid, n.typeName]));
        for (const edgeDto of edgeDtos) {
            const fromNode = nodesByGuid.get(edgeDto.fromNodeGuid);
            const toNode = nodesByGuid.get(edgeDto.toNodeGuid);
            if (!fromNode || !toNode) {
                console.error("storyForgeGraph: edge references unknown node", edgeDto);
                continue;
            }

            const outputSlot = (outputSlotsByType.get(nodeTypeByGuid.get(edgeDto.fromNodeGuid)) || {})[edgeDto.fromPort];
            const inputSlot = (inputSlotsByType.get(nodeTypeByGuid.get(edgeDto.toNodeGuid)) || {})[edgeDto.toPort];
            if (outputSlot === undefined || inputSlot === undefined) {
                console.error("storyForgeGraph: edge references unknown port", edgeDto);
                continue;
            }

            fromNode.connect(outputSlot, toNode, inputSlot);
        }
    }

    function init(canvasElementId, payload, dotNetRef) {
        currentPayload = payload;
        selfRef = dotNetRef;
        if (payload.settings) {
            frozenNodeFontPx = payload.settings.nodeFrozenTextSize;
            maxNodeFontPx = payload.settings.nodeMaxTextSize;
            frozenGroupFontPx = payload.settings.groupFrozenTextSize;
            maxGroupFontPx = payload.settings.groupMaxTextSize;
            groupPadding = payload.settings.groupPadding;
            groupTitleBarHeight = payload.settings.groupTitleBarHeight;
            searchMinZoom = payload.settings.searchMinZoom;
        }
        graph = new LGraph();
        const canvasEl = document.getElementById(canvasElementId);
        canvas = new LGraphCanvas("#" + canvasElementId, graph);
        canvas.allow_searchbox = false;
        canvas.render_canvas_border = false;
        canvas.onDrawOverlay = drawFrozenLabels;

        // Single-selection only for now (the inspector sidebar shows one node's fields at a time) — with
        // more than one selected, or none, there's nothing for it to show.
        //
        // Debounced via setTimeout(0) rather than dispatched immediately: LiteGraph's own selectNodes()
        // unconditionally calls deselectAllNodes() first — even for a plain click on a node with nothing
        // previously selected — so a single click fires this callback twice synchronously (once with an
        // empty selection, then again with the real one). Without the debounce each call was its own
        // Blazor invokeMethodAsync round trip, so the sidebar visibly disappeared and reappeared (a flash
        // on a plain click; a drag masked it because onNodeMoved's later setDirty repaint happened to land
        // after both had already resolved). Collapsing same-tick calls to just the last one sends a single
        // net update instead.
        let pendingSelectionChange = null;
        canvas.onSelectionChange = function (selectedNodes) {
            clearTimeout(pendingSelectionChange);
            pendingSelectionChange = setTimeout(() => {
                const ids = Object.keys(selectedNodes || {});
                const guid = ids.length === 1 ? guidByNode.get(selectedNodes[ids[0]]) : null;
                dotNetRef.invokeMethodAsync("OnNodeSelectionChanged", guid || null);
            }, 0);
        };

        // Fires once when a node drag ends (not continuously during it — see updateNodeGroupMembership's
        // own comment for why that's an acceptable simplification of the original WinForms tool's
        // per-frame UpdateNodeDragLive). Ported from GraphDocumentModel's own group-membership rules
        // (UpdateNodeDragLive/RemoveNodeFromCurrentGroup/FindJoinableGroup/RecomputeGroupBounds) since our
        // canvas interaction is entirely client-side — those C# methods are otherwise never reached at all
        // outside 存檔 time.
        canvas.onNodeMoved = function (node) {
            if (pendingMultiDragNodes && pendingMultiDragNodes.length > 1) {
                for (const draggedNode of pendingMultiDragNodes)
                    updateNodeGroupMembership(draggedNode, lastMouseUpShiftKey);
            } else {
                updateNodeGroupMembership(node, lastMouseUpShiftKey);
            }
            pendingMultiDragNodes = null;
            canvas.setDirty(true, true);
        };

        // Re-sync the backing-store resolution whenever the canvas element's own box size changes — not
        // just on a browser window resize (which is what a plain "resize" listener would catch), but also
        // when a sibling appearing/disappearing (the inspector sidebar) squeezes it via flex layout with no
        // window resize involved at all. Without this, the CSS box shrinks immediately but the backing
        // store doesn't, so the browser stretches the old raster into the new (narrower) box for one frame
        // — the "canvas squishes" flash this fixes.
        canvasResizeObserver?.disconnect();
        canvasResizeObserver = new ResizeObserver(() => {
            fixCanvasResolution(canvasEl);
            canvas.draw(true, true);
        });
        canvasResizeObserver.observe(canvasEl);

        nodesByGuid = new Map();
        guidByNode = new Map();
        outputSlotsByType = new Map();
        inputSlotsByType = new Map();
        newNodeGuids = new Set();
        initialNodeGuids = new Set(payload.nodes.map(n => n.guid));
        lastSearchQuery = null;
        lastSearchMatchGuids = [];
        lastSearchMatchIndex = -1;

        registerNodeTypes(payload.nodeTypes);
        buildGraphFromData(payload.nodes, payload.edges, payload.groups);

        // Right after Blazor inserts the <canvas> into the DOM, its flex-computed layout box isn't
        // necessarily settled yet — clientWidth/clientHeight (which fixCanvasResolution and fitView both
        // depend on) can still read 0 for more than one animation frame. Poll instead of assuming a fixed
        // number of frames is enough.
        waitForLayout(canvasEl, () => {
            fixCanvasResolution(canvasEl);
            restoreViewportOrFitView();
            canvas.draw(true, true);
        });
    }

    // Ported from GraphDocumentModel.UpdateNodeDragLive — the C# original ran this continuously as a node
    // was dragged (hence "Live"); here it only runs once, when LiteGraph's own onNodeMoved fires at drag
    // end. That's an accepted simplification: the group box won't visibly grow/shrink WHILE the node is
    // mid-drag, only once it's dropped — the end result (which group a node belongs to, and that group's
    // final bounds) is identical either way, and per-frame membership recompute during a drag would add
    // real cost for a purely cosmetic mid-drag animation nobody asked for.
    //
    // suppressMembershipChange (Shift held at drop time) detaches the node from its current group instead
    // of the default "stays a member, group just grows to follow" — matching RemoveNodeFromCurrentGroup.
    function updateNodeGroupMembership(node, suppressMembershipChange) {
        const guid = guidByNode.get(node);
        if (!guid) return;

        if (suppressMembershipChange) {
            const current = findContainingGroup(guid);
            if (current) {
                current._memberGuids.delete(guid);
                recomputeGroupBounds(current);
            }
            return;
        }

        // A node that's already a member never switches groups here — its box just grows/recomputes to
        // keep including wherever the node currently is. Only a node with NO current group looks for one
        // to join.
        const current = findContainingGroup(guid);
        if (current) {
            recomputeGroupBounds(current);
            return;
        }

        const target = findJoinableGroup(node);
        if (!target) return;
        target._memberGuids.add(guid);
        recomputeGroupBounds(target);
    }

    function findContainingGroup(guid) {
        for (const group of graph._groups || []) {
            if (group._memberGuids && group._memberGuids.has(guid)) return group;
        }
        return null;
    }

    // A node joins a group once its own center point lands inside the group's box — mirrors
    // GraphDocumentModel.FindJoinableGroup exactly (a plain overlap test triggers too easily just from a
    // node's edge brushing past a group it wasn't meant to enter). Last match wins when boxes overlap,
    // same as the original's OrderByDescending(SourceIndex) picking the most-recently-added candidate.
    function findJoinableGroup(node) {
        const centerX = node.pos[0] + node.size[0] / 2;
        const centerY = node.pos[1] + node.size[1] / 2;
        let found = null;
        for (const group of graph._groups || []) {
            if (centerX >= group.pos[0] && centerX <= group.pos[0] + group.size[0] &&
                centerY >= group.pos[1] && centerY <= group.pos[1] + group.size[1]) {
                found = group;
            }
        }
        return found;
    }

    // Mirrors GraphDocumentModel.RecomputeGroupBounds exactly (same padding/titleBarHeight-derived
    // formula, from AppSettings via the payload) — a still-empty group (including one that just lost its
    // last member) keeps whatever box it already had rather than collapsing to nothing.
    function recomputeGroupBounds(group) {
        const members = [...group._memberGuids].map(guid => nodesByGuid.get(guid)).filter(Boolean);
        if (members.length === 0) return;

        let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
        for (const n of members) {
            minX = Math.min(minX, n.pos[0]);
            minY = Math.min(minY, n.pos[1]);
            maxX = Math.max(maxX, n.pos[0] + n.size[0]);
            maxY = Math.max(maxY, n.pos[1] + n.size[1]);
        }

        group.pos[0] = minX - groupPadding;
        group.pos[1] = minY - groupTitleBarHeight;
        group.size[0] = maxX - minX + groupPadding * 2;
        group.size[1] = maxY - minY + groupTitleBarHeight + groupPadding;
    }

    function waitForLayout(canvasEl, callback, attemptsLeft) {
        if (attemptsLeft === undefined) attemptsLeft = 30;
        if (canvasEl.clientWidth > 0 && canvasEl.clientHeight > 0) {
            callback();
            return;
        }
        if (attemptsLeft <= 0) {
            console.error("storyForgeGraph: canvas never got a non-zero layout size");
            callback();
            return;
        }
        requestAnimationFrame(() => waitForLayout(canvasEl, callback, attemptsLeft - 1));
    }

    // Remembers the user's own pan/zoom across a reload (F5, 讀取, a Blazor circuit reconnect) — without
    // this, every load fell through straight to fitView()'s "fit everything" framing, which for a graph
    // this size (hundreds of nodes spread wide) computes a very small scale: indistinguishable from having
    // zoomed all the way out, every single time, even mid-session. Client-side only (localStorage, keyed
    // per browser) since the viewport is a pure UI concern with nothing to do with the saved graph itself.
    const VIEWPORT_STORAGE_KEY = "storyforge-graph-viewport";

    function saveViewport() {
        if (!canvas) return;
        try {
            localStorage.setItem(VIEWPORT_STORAGE_KEY, JSON.stringify({
                scale: canvas.ds.scale,
                offsetX: canvas.ds.offset[0],
                offsetY: canvas.ds.offset[1],
            }));
        } catch { /* localStorage unavailable (private mode, quota) — losing the remembered viewport is harmless */ }
    }

    function loadSavedViewport() {
        try {
            const raw = localStorage.getItem(VIEWPORT_STORAGE_KEY);
            if (!raw) return null;
            const v = JSON.parse(raw);
            if (typeof v.scale !== "number" || !isFinite(v.scale) || v.scale <= 0) return null;
            if (typeof v.offsetX !== "number" || typeof v.offsetY !== "number") return null;
            return v;
        } catch {
            return null;
        }
    }

    // fitView() still establishes the zoom-out floor (ds.min_scale) and a sane default from the graph's
    // own bounds — only the resulting scale/offset are then overridden by whatever was last saved, so a
    // saved viewport from before nodes moved/were added still gets a floor consistent with today's graph.
    function restoreViewportOrFitView() {
        fitView();
        const saved = loadSavedViewport();
        if (saved) {
            canvas.ds.scale = Math.max(saved.scale, canvas.ds.min_scale);
            canvas.ds.offset[0] = saved.offsetX;
            canvas.ds.offset[1] = saved.offsetY;
        }
    }

    // The graph's saved node positions come from the old WinForms canvas's own coordinate space, so a
    // freshly-opened editor needs to frame them itself rather than starting at LiteGraph's default
    // origin/zoom (which for a graph this size shows nothing but empty grid). Also the explicit fallback/
    // reset used by restoreViewportOrFitView (no saved viewport yet) and the 縮放至全部 button.
    function fitView() {
        if (graph._nodes.length === 0) return;

        let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
        for (const node of graph._nodes) {
            minX = Math.min(minX, node.pos[0]);
            minY = Math.min(minY, node.pos[1]);
            maxX = Math.max(maxX, node.pos[0] + (node.size ? node.size[0] : 140));
            maxY = Math.max(maxY, node.pos[1] + (node.size ? node.size[1] : 60));
        }

        const boundsWidth = Math.max(1, maxX - minX);
        const boundsHeight = Math.max(1, maxY - minY);
        // CSS pixels (clientWidth/Height), not the backing store's physical canvas.width/height — LiteGraph's
        // own mouse handling maps clientX/clientY straight onto ds.scale/offset with no devicePixelRatio
        // division, so calibrating ds against physical pixels here would silently offset every click/drag
        // by that ratio (nodes would render fine but be nearly unclickable — exactly what fixCanvasResolution's
        // ctx.scale now handles separately, for the backing-store sharpness only).
        const canvasEl = canvas.canvas;
        const cssWidth = canvasEl.clientWidth;
        const cssHeight = canvasEl.clientHeight;
        const scale = Math.min(cssWidth / boundsWidth, cssHeight / boundsHeight) * 0.9;

        canvas.ds.scale = Math.min(scale, 1);
        // LiteGraph's own zoom-out floor (ds.min_scale, default 0.1) has no relation to this graph's
        // "fit everything" scale — for a graph big enough that fitting it needs a smaller scale than
        // that floor, the very next wheel tick doesn't nudge the scale, it clamps straight up to 0.1
        // (changeScale snaps anything below min_scale to it), a jarring multi-x jump instead of the
        // usual ~5% step. Let the floor track fit-all instead (with a little headroom to still zoom
        // out slightly further than fit), so scrolling after 縮放至全部 always steps gradually.
        canvas.ds.min_scale = Math.min(0.1, canvas.ds.scale * 0.5);
        canvas.ds.offset[0] = -minX + (cssWidth / canvas.ds.scale - boundsWidth) / 2;
        canvas.ds.offset[1] = -minY + (cssHeight / canvas.ds.scale - boundsHeight) / 2;
    }

    // query is a raw user string (may be empty/whitespace-only while they're still typing). Returns false
    // when nothing matched (a bad query, or no nodes at all) so the caller can show a "not found" status.
    function searchNode(query) {
        if (!graph || typeof query !== "string" || !query.trim())
            return false;

        const trimmed = query.trim();
        if (trimmed !== lastSearchQuery) {
            lastSearchQuery = trimmed;
            const lowerQuery = trimmed.toLowerCase();
            lastSearchMatchGuids = (currentPayload?.nodes || [])
                .filter(n => n.hasIdentityTitle && n.title && n.title.toLowerCase().includes(lowerQuery)
                    && nodesByGuid.has(n.guid))
                .map(n => n.guid);
            lastSearchMatchIndex = -1;
        }

        if (lastSearchMatchGuids.length === 0)
            return false;

        lastSearchMatchIndex = (lastSearchMatchIndex + 1) % lastSearchMatchGuids.length;
        const node = nodesByGuid.get(lastSearchMatchGuids[lastSearchMatchIndex]);
        if (!node) return false;

        focusOnNode(node);
        return true;
    }

    // Pans so the node's center lands at the canvas center, and selects it (which drives the inspector
    // sidebar open via the normal onSelectionChange path, same as any other click-to-select) — matching
    // ProcessGraphCanvas.FocusOnNode, except zoom is only ever raised to searchMinZoom, never lowered: a
    // match found while already zoomed in close enough stays at that zoom instead of snapping back out.
    function focusOnNode(node) {
        const canvasEl = canvas.canvas;
        const cssWidth = canvasEl.clientWidth;
        const cssHeight = canvasEl.clientHeight;

        if (canvas.ds.scale < searchMinZoom)
            canvas.ds.scale = searchMinZoom;

        const centerX = node.pos[0] + node.size[0] / 2;
        const centerY = node.pos[1] + node.size[1] / 2;
        canvas.ds.offset[0] = cssWidth / (2 * canvas.ds.scale) - centerX;
        canvas.ds.offset[1] = cssHeight / (2 * canvas.ds.scale) - centerY;

        canvas.selectNode(node);
        canvas.setDirty(true, true);
    }

    // Entry point for "跳到劇情節點" (PipelineStatusPanel's right-click menu) — that action switches the
    // Blazor tab shell to 流程圖 *and* calls this in the same server-side round trip, but Home.razor's
    // display:none -> block toggle for this canvas's wrapper div is applied by the browser asynchronously
    // (a Blazor Server render batch over SignalR, not something the C# side can block on) — searchNode's own
    // focusOnNode reads canvasEl.clientWidth/clientHeight live, which is still 0 while the wrapper is
    // display:none, producing a garbage pan offset. Polling on requestAnimationFrame until the canvas
    // actually has real dimensions (bounded so a canvas that's somehow never going to become visible can't
    // spin forever) sidesteps guessing a fixed delay for how long that round trip takes.
    function focusOnPlayscriptWhenVisible(query) {
        return new Promise((resolve) => {
            let attempts = 0;
            function tryFocus() {
                const canvasEl = canvas && canvas.canvas;
                if (canvasEl && canvasEl.clientWidth > 0 && canvasEl.clientHeight > 0) {
                    resolve(searchNode(query));
                    return;
                }

                attempts++;
                if (attempts > 60) {
                    resolve(searchNode(query));
                    return;
                }

                requestAnimationFrame(tryFocus);
            }

            tryFocus();
        });
    }

    // The only title renderer for both nodes and groups (their native LiteGraph titles are blanked at
    // creation) — drawn here, in onDrawOverlay's screen space, for three reasons found by hand:
    //  1. LiteGraph's own title draws at a fixed *world*-space size, so it shrinks with everything else
    //     as the graph zooms out, past legible and then past LiteGraph even bothering to draw it — a
    //     "frozen" floor size (like the old WinForms tool had) needs a renderer that isn't world-scaled.
    //  2. It doesn't clip to the node's width, so a long playscript name spills out past the node border.
    //  3. Doing both jobs in one custom renderer means exactly one thing decides what's on screen — no
    //     gap between "native gave up" and "our floor kicks in", and no double-drawn overlapping text.
    const NATURAL_NODE_WORLD_SIZE = 12;
    const NATURAL_GROUP_WORLD_SIZE = 18;
    const MIN_BOX_SCREEN_PX = 20; // below this, the box itself is barely a speck — its title would be pure clutter

    // Configurable via the 設定 page (StoryForge.Core.Graph.AppSettings) — read fresh from the payload on
    // every init() rather than hardcoded, so a settings change takes effect on the next 重新載入. Defaults
    // here only cover the (currently impossible) case of a payload with no settings block at all.
    //
    // Both node and group titles grow between their own frozen floor and capped ceiling as the canvas
    // zooms in/out — but for a node, ONLY when its title is a real identity value (playscriptId/flagId,
    // see NodeDto.HasIdentityTitle), never for a node showing its type's generic display name (e.g.
    // "結束劇本", "AND", or a just-created empty node with no identity value yet) — those stay fixed at
    // NodeFrozenTextSize always, per explicit user feedback that a generic type label enlarging alongside
    // real playscript names was confusing.
    let frozenNodeFontPx = 10;
    let maxNodeFontPx = 32;
    let frozenGroupFontPx = 12;
    let maxGroupFontPx = 40;

    function drawFrozenLabels(ctx) {
        // onDrawOverlay also fires once for LiteGraph's cached offscreen bg layer (canvas.bgctx), separate
        // from the live foreground (canvas.ctx) — harmless to skip here since the bg layer never shows our
        // labels directly anyway, but no reason to do the work twice per frame either.
        if (ctx !== canvas.ctx) return;

        const scale = canvas.ds.scale;
        const offset = canvas.ds.offset;
        const titleHeight = LiteGraph.NODE_TITLE_HEIGHT || 30;

        ctx.save();
        // "top" (not "middle") so both titles pivot from their box's top-left corner as fontPx changes —
        // the anchor point (screenX, screenY) below is then a pure affine transform of a fixed world point,
        // never offset by fontPx itself, so a node's and a group's title always grow down-and-right from
        // the same corner instead of drifting toward each other when their boxes sit close together.
        ctx.textBaseline = "top";

        ctx.fillStyle = "#e0e0e0";
        for (const group of graph._groups || []) {
            if (!group._displayTitle) continue;
            const fontPx = Math.min(maxGroupFontPx, Math.max(frozenGroupFontPx, NATURAL_GROUP_WORLD_SIZE * scale));
            if (group.size[0] * scale < MIN_BOX_SCREEN_PX) continue; // too small on screen to matter
            const screenX = (group.pos[0] + offset[0]) * scale + 6;
            const screenY = (group.pos[1] + offset[1]) * scale + 4;
            drawText(ctx, group._displayTitle, screenX, screenY, fontPx);
        }

        ctx.fillStyle = "#ffffff";
        for (const node of graph._nodes) {
            if (!node._displayTitle) continue;
            // Gate on _baseWidth (the size before any dynamic widening below), not the current node.size —
            // otherwise a node widened on a previous, more-zoomed-in frame would stay "big enough to
            // matter" forever after, even once zoomed back out past where it'd naturally have been skipped.
            if (node._baseWidth * scale < MIN_BOX_SCREEN_PX) continue;

            // Display size = original size × scale, per the user's own precise definition. A node with a
            // real identity value clamps that between a floor and a ceiling (protected — never unreadably
            // tiny when zoomed out, never oversized when zoomed in); a node showing only its type's generic
            // display name (no identity value — e.g. AND/OR/結束劇本) gets NO clamp at all, just plain
            // proportional scaling like everything else on the canvas that isn't specially protected.
            const fontPx = node._hasIdentityTitle
                ? Math.min(maxNodeFontPx, Math.max(frozenNodeFontPx, NATURAL_NODE_WORLD_SIZE * scale))
                : NATURAL_NODE_WORLD_SIZE * scale;
            ctx.font = fontPx + "px sans-serif";
            // Widen (never shrink past _baseWidth) so the title fits at THIS frame's zoom — recomputed
            // every frame rather than once, since the answer changes continuously as scale changes. A
            // node's ports live at fixed rows off its left/right edges, so this shifts the right-hand ports
            // outward with it exactly as if the node really were that wide, not just its drawn label.
            const neededWorldWidth = (ctx.measureText(node._displayTitle).width + 8) / scale;
            node.size[0] = Math.max(node._baseWidth, neededWorldWidth);

            const screenX = (node.pos[0] + offset[0]) * scale + 4;
            const screenY = (node.pos[1] - titleHeight + offset[1]) * scale + 4;
            drawText(ctx, node._displayTitle, screenX, screenY, fontPx);
        }

        ctx.restore();
    }

    // Always the full title, however wide — growing the node to fit it at creation (see init) keeps this
    // from overflowing in the common case, but at the low end of the zoom range the frozen floor size can
    // still outgrow even that box; letting it spill past the border there beats silently cutting off part
    // of a playscript's name, which is what an ellipsis would otherwise be hiding.
    function drawText(ctx, text, x, y, fontPx) {
        ctx.font = fontPx + "px sans-serif";
        ctx.fillText(text, x, y);
    }

    function exportGraph() {
        const nodePositions = [];
        for (const node of graph._nodes) {
            const guid = guidByNode.get(node);
            if (!guid) continue;
            nodePositions.push({ guid: guid, x: node.pos[0], y: node.pos[1] });
        }

        // Iterates graph.links (every link object the graph knows about, keyed by id) rather than walking
        // each input's own .link/.links — multi-link inputs (patched in patchLiteGraphForMultiInputLinks)
        // still register every one of their links here exactly the same as a plain single-link input does,
        // so this needs no special casing for them at all.
        const edges = [];
        for (const linkId in graph.links) {
            const link = graph.links[linkId];
            if (!link) continue;
            const fromNode = graph.getNodeById(link.origin_id);
            const toNode = graph.getNodeById(link.target_id);
            const fromGuid = guidByNode.get(fromNode);
            const toGuid = guidByNode.get(toNode);
            if (!fromGuid || !toGuid) continue;

            const fromSlots = outputSlotsByType.get(currentPayload.nodes.find(n => n.guid === fromGuid).typeName) || {};
            const fromPort = Object.keys(fromSlots).find(k => fromSlots[k] === link.origin_slot);
            const toSlots = inputSlotsByType.get(currentPayload.nodes.find(n => n.guid === toGuid).typeName) || {};
            const toPort = Object.keys(toSlots).find(k => toSlots[k] === link.target_slot);

            edges.push({ fromNodeGuid: fromGuid, fromPort: fromPort, toNodeGuid: toGuid, toPort: toPort });
        }

        const newNodes = [];
        for (const guid of newNodeGuids) {
            const node = nodesByGuid.get(guid);
            if (!node) continue;
            const nodeDto = currentPayload.nodes.find(n => n.guid === guid);
            if (!nodeDto) continue;
            newNodes.push({ guid: guid, typeName: nodeDto.typeName, x: node.pos[0], y: node.pos[1] });
        }

        // A node removed this session (Delete key, or LiteGraph's own native node-menu Remove) is simply
        // absent from graph._nodes now — diffed against the guids the server actually knew about at load
        // time (initialNodeGuids) rather than against currentPayload.nodes, since that list also grows to
        // include this session's newNodes and a node created-then-deleted before ever being saved should
        // just vanish silently, not round-trip to the server as a delete of something it never knew about.
        const stillPresentGuids = new Set(nodePositions.map(p => p.guid));
        const deletedNodeGuids = [...initialNodeGuids].filter(guid => !stillPresentGuids.has(guid));

        // Only groups with a _clientId (known to the server already) round-trip here — a group created
        // this session via 新增群組 has none yet and is silently skipped server-side too (ApplyAndSave's
        // FindGroup-by-ClientId lookup just won't match anything), same accepted v1 gap new nodes had
        // before OnNodeCreated existed.
        const groups = [];
        for (const group of graph._groups || []) {
            if (!group._clientId) continue;
            groups.push({
                clientId: group._clientId,
                title: group._displayTitle || "",
                x: group.pos[0], y: group.pos[1], width: group.size[0], height: group.size[1],
                color: group.color,
                memberNodeGuids: [...(group._memberGuids || [])],
            });
        }

        return {
            nodePositions: nodePositions, edges: edges, newNodes: newNodes,
            deletedNodeGuids: deletedNodeGuids, groups: groups,
        };
    }

    function fitViewAndRedraw() {
        fitView();
        saveViewport();
        canvas.draw(true, true);
    }

    // Called from the 設定 dialog's 套用/確定 button — pushes edited values straight into the live render
    // without requiring a full 重新載入, matching the original WinForms SettingsForm's onApply behavior
    // (live-refreshing an already-open panel instead of only taking effect after a reopen).
    function updateSettings(settings) {
        if (!settings) return;
        frozenNodeFontPx = settings.nodeFrozenTextSize;
        maxNodeFontPx = settings.nodeMaxTextSize;
        frozenGroupFontPx = settings.groupFrozenTextSize;
        maxGroupFontPx = settings.groupMaxTextSize;
        if (canvas) canvas.draw(true, true);
    }

    // Called from FlowGraphPanel's OnFieldChanged right after a sidebar identity-field edit
    // (GraphEditorState.UpdateNodeField/GetNodeTitleInfo) — the counterpart to the "title only updates on
    // next reload" gap the server-side model otherwise accepts for a plain field edit: this pushes the
    // freshly recomputed title straight to the canvas node instead of leaving it stale until the next
    // LoadGraph. _baseWidth is recomputed the same way buildGraphFromData/duplicateSelectedNodes do it for
    // a node's initial size — drawFrozenLabels' own per-frame widening then takes over from here at
    // whatever the current zoom needs.
    function updateNodeTitle(guid, title, hasIdentityTitle) {
        const node = nodesByGuid.get(guid);
        if (!node) return;

        node._displayTitle = title;
        node._hasIdentityTitle = !!hasIdentityTitle;

        const naturalSize = node.computeSize();
        canvas.ctx.font = NATURAL_NODE_WORLD_SIZE + "px sans-serif";
        const titleWidth = canvas.ctx.measureText(title).width + 8;
        node.size[0] = Math.max(naturalSize[0], titleWidth);
        node._baseWidth = node.size[0];

        // Keeps currentPayload's own node list from going stale relative to the live model for this one
        // field — read by exportGraph's newNodes lookup (a not-yet-saved node) and duplicateSelectedNodes'
        // own title-width fallback, neither of which otherwise learns about this edit.
        const nodeDto = currentPayload.nodes.find(n => n.guid === guid);
        if (nodeDto) {
            nodeDto.title = title;
            nodeDto.hasIdentityTitle = !!hasIdentityTitle;
        }

        canvas.setDirty(true, true);
    }

    return {
        init: init,
        exportGraph: exportGraph,
        fitView: fitViewAndRedraw,
        updateSettings: updateSettings,
        updateNodeTitle: updateNodeTitle,
        searchNode: searchNode,
        focusOnPlayscriptWhenVisible: focusOnPlayscriptWhenVisible,
        _debug: {
            getGraph: () => graph, getCanvas: () => canvas, guidByNode: () => guidByNode,
            lastMouseUpShiftKey: () => lastMouseUpShiftKey,
        },
    };
})();
