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

    patchLiteGraphForMultiInputLinks();
    patchEdgeBodyRewiring();
    patchAddNodeMenu();
    patchDisableNodePropertiesPanel();
    patchCaptureShiftOnMouseUp();
    patchDisableNodeContextMenu();

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
            return originalProcessMouseUp.call(this, e);
        };
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

            LiteGraph.registerNodeType("storyforge/" + sanitize(typeDto.typeName), StoryForgeNode);
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

            this.adjustMouseEvent(e);
            const point = [e.canvasX, e.canvasY];
            const isLeftButton = e.button === 0;

            // A click landing inside any node's box always belongs to that node (selecting/dragging it, or
            // one of its own port dots, which the original processMouseDown already handles) — only open
            // canvas space between nodes is fair game for grabbing a wire by its curve.
            if (this.graph.getNodeOnPos(point[0], point[1], this.visible_nodes)) {
                // Node interaction (select/drag/port-wire-drag) is a left-button-only gesture — stock
                // LiteGraph's own button branching doesn't actually exclude other buttons from reaching
                // that same node hit-test code path, so a middle-click landing on a node was starting the
                // exact same port/wire-drag interaction a left-click would.
                if (!isLeftButton) {
                    e.stopPropagation();
                    e.preventDefault();
                    return false;
                }
                return originalProcessMouseDown.call(this, e);
            }

            // Empty canvas space with a non-left button — leave it to native handling (e.g. middle-click
            // canvas panning) rather than our own left-click-only edge-grab gesture below.
            if (!isLeftButton)
                return originalProcessMouseDown.call(this, e);

            const hit = findNearestEdgeAtPoint(this, point, EDGE_CLICK_THRESHOLD_PX / this.ds.scale);
            if (!hit)
                return originalProcessMouseDown.call(this, e);

            // The original processMouseDown's very first job (before any hit-testing) is re-pointing the
            // move/up listeners from the canvas to the document — since a drag can legitimately leave the
            // canvas element while still being tracked. Returning early without this meant our custom-
            // started drag never received another mousemove/mouseup at all once bypassed here.
            LiteGraph.pointerListenerRemove(this.canvas, "move", this._mousemove_callback);
            LiteGraph.pointerListenerAdd(this.getCanvasWindow().document, "move", this._mousemove_callback, true);
            LiteGraph.pointerListenerAdd(this.getCanvasWindow().document, "up", this._mouseup_callback, true);

            beginRewireFromEdgeGrab(this, hit.link, hit.nearerToOrigin);
            e.stopPropagation();
            e.preventDefault();
            return false;
        };
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
    function beginRewireFromEdgeGrab(canvasInstance, link, nearerToOrigin) {
        const graphRef = canvasInstance.graph;
        const originNode = graphRef.getNodeById(link.origin_id);
        const targetNode = graphRef.getNodeById(link.target_id);
        if (!originNode || !targetNode) return;

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

    // Replaces LiteGraph's stock "Add Node" menus with a FLAT list, one item per node type — ported
    // directly from the old WinForms tool's BuildContextMenu/ShowWiringCreateNodeMenu (ProcessGraphCanvas.cs),
    // which never nested submenus at all: MenuName is used only to SORT the flat list, not to group it into
    // clickable categories. An earlier version of this file built a two-level (sometimes three-level, for a
    // MenuName group with only one member) nested ContextMenu instead — flagged by the user as confusing and
    // needlessly deep compared to the original. Overriding both call sites directly (rather than the shared
    // static onMenuAdd hook they both used to funnel through) also removes LiteGraph's own "Add Node"/"Search"
    // wrapper level that stock showConnectionMenu otherwise inserts before ever reaching onMenuAdd.
    function patchAddNodeMenu() {
        // Off by default in stock LiteGraph: dropping a wire on empty space would otherwise just leave it
        // detached with no menu at all. Turning this on is what makes showConnectionMenu (and therefore the
        // override below) fire on an empty-space drop, restoring the original tool's behavior.
        LiteGraph.release_link_on_empty_shows_menu = true;

        // Right-click on empty canvas. Stock getCanvasMenuOptions() return value becomes the ENTIRE
        // top-level menu with no extra wrapping (confirmed by reading the vendored source), so building our
        // own flat array here — instead of leaving stock's {content:"Add Node", has_submenu:true} entry in
        // place — removes that one extra level too, matching the original's flat BuildContextMenu exactly.
        LGraphCanvas.prototype.getCanvasMenuOptions = function () {
            const worldPos = this.graph_mouse ? this.graph_mouse.slice() : [0, 0];
            const items = [
                {
                    content: "新增群組",
                    callback: () => {
                        const group = new LiteGraph.LGraphGroup(" ");
                        group._displayTitle = "新群組";
                        group.pos = [worldPos[0], worldPos[1]];
                        group.size = [200, 100];
                        this.graph.add(group);
                        this.setDirty(true, true);
                    },
                },
                null,
                ...buildFlatAddNodeItems(this, null),
            ];
            return items;
        };

        // Dropping a dragged wire on empty canvas space. Bypasses stock showConnectionMenu's own body
        // entirely (which would otherwise show an "Add Node"/"Search" chooser first) and goes straight to
        // the flat per-compatible-port list, exactly like the original's ShowWiringCreateNodeMenu.
        LGraphCanvas.prototype.showConnectionMenu = function (options) {
            const opts = options || {};
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
    // Pin/Colors/Shapes/Clone/Remove, all in English) is pure clutter here — none of it is relevant to this
    // app, per explicit user feedback to just remove it outright rather than translate/curate it. Returning
    // null (not an empty array — an empty array is still truthy, so `g&&new ContextMenu(g,...)` would still
    // pop up a blank floating box) is what stock LiteGraph itself checks for to skip building the menu at
    // all — confirmed by reading the vendored source's own `g&&new e.ContextMenu(g,c,f)` call site.
    function patchDisableNodeContextMenu() {
        LGraphCanvas.prototype.getNodeMenuOptions = function () {
            return null;
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
        }
        graph = new LGraph();
        const canvasEl = document.getElementById(canvasElementId);
        canvas = new LGraphCanvas("#" + canvasElementId, graph);
        canvas.allow_searchbox = false;
        canvas.render_canvas_border = false;
        canvas.onDrawOverlay = drawFrozenLabels;

        // Single-selection only for now (the inspector sidebar shows one node's fields at a time) — with
        // more than one selected, or none, there's nothing for it to show.
        canvas.onSelectionChange = function (selectedNodes) {
            const ids = Object.keys(selectedNodes || {});
            const guid = ids.length === 1 ? guidByNode.get(selectedNodes[ids[0]]) : null;
            dotNetRef.invokeMethodAsync("OnNodeSelectionChanged", guid || null);
        };

        // Fires once when a node drag ends (not continuously during it — see updateNodeGroupMembership's
        // own comment for why that's an acceptable simplification of the original WinForms tool's
        // per-frame UpdateNodeDragLive). Ported from GraphDocumentModel's own group-membership rules
        // (UpdateNodeDragLive/RemoveNodeFromCurrentGroup/FindJoinableGroup/RecomputeGroupBounds) since our
        // canvas interaction is entirely client-side — those C# methods are otherwise never reached at all
        // outside 存檔 time.
        canvas.onNodeMoved = function (node) {
            updateNodeGroupMembership(node, lastMouseUpShiftKey);
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

        registerNodeTypes(payload.nodeTypes);

        for (const groupDto of payload.groups) {
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
            // being recreated (e.g. by undo, once that exists client-side) without needing to be rebuilt.
            group._clientId = groupDto.clientId;
            group._memberGuids = new Set(groupDto.memberNodeGuids || []);
            graph.add(group);
        }

        for (const nodeDto of payload.nodes) {
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

        const nodeTypeByGuid = new Map(payload.nodes.map(n => [n.guid, n.typeName]));
        for (const edgeDto of payload.edges) {
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

        // Right after Blazor inserts the <canvas> into the DOM, its flex-computed layout box isn't
        // necessarily settled yet — clientWidth/clientHeight (which fixCanvasResolution and fitView both
        // depend on) can still read 0 for more than one animation frame. Poll instead of assuming a fixed
        // number of frames is enough.
        waitForLayout(canvasEl, () => {
            fixCanvasResolution(canvasEl);
            fitView();
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

    // The graph's saved node positions come from the old WinForms canvas's own coordinate space, so a
    // freshly-opened editor needs to frame them itself rather than starting at LiteGraph's default
    // origin/zoom (which for a graph this size shows nothing but empty grid).
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
        canvas.ds.offset[0] = -minX + (cssWidth / canvas.ds.scale - boundsWidth) / 2;
        canvas.ds.offset[1] = -minY + (cssHeight / canvas.ds.scale - boundsHeight) / 2;
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
        ctx.textBaseline = "middle";

        ctx.fillStyle = "#e0e0e0";
        for (const group of graph._groups || []) {
            if (!group._displayTitle) continue;
            const fontPx = Math.min(maxGroupFontPx, Math.max(frozenGroupFontPx, NATURAL_GROUP_WORLD_SIZE * scale));
            if (group.size[0] * scale < MIN_BOX_SCREEN_PX) continue; // too small on screen to matter
            const screenX = (group.pos[0] + offset[0]) * scale + 6;
            const screenY = (group.pos[1] + offset[1]) * scale + fontPx;
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
            const screenY = (node.pos[1] - titleHeight / 2 + offset[1]) * scale;
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

    return {
        init: init,
        exportGraph: exportGraph,
        fitView: fitViewAndRedraw,
        updateSettings: updateSettings,
        _debug: {
            getGraph: () => graph, getCanvas: () => canvas, guidByNode: () => guidByNode,
            lastMouseUpShiftKey: () => lastMouseUpShiftKey,
        },
    };
})();
