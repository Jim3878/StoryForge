// Client half of the BGM/音效 tabs (AudioBrowserPanel.razor): browser-side waveform decoding, canvas
// drawing, hover/click playback with a progress line, and the list view's column drag/resize. Everything
// per-pixel or per-frame lives here so it never round-trips over SignalR; the server only ever hears about
// finished results (a computed waveform, a dropped column, a released resize handle).
//
// Waveforms are decoded here rather than server-side because Chrome already decodes ogg/mp3/wav natively
// (the server would need extra Vorbis/MP3 packages). Results go back to AudioWaveformCacheService via
// OnWaveformComputed, so each file is only ever decoded once per change.
window.storyForgeAudio = (function () {
    const BUCKETS = 1000;
    const HOVER_DELAY_MS = 500;
    const DECODE_CONCURRENCY = 2;
    const WAVE_COLOR = "#6fa8ff";
    const WAVE_COLOR_PLAYING = "#9fd08a";

    // rootId -> { root, dotNet, opts, urls: Map, peaks: Map<fileName, Uint8Array|"error">, queue: [] }
    const instances = {};

    let audio = null;
    // { rootId, name } while something is playing; one player for the whole app so starting a clip on
    // either tab always stops whatever was playing before.
    let playing = null;
    let playToken = 0;
    let progressFrame = 0;
    let hoverTimer = 0;
    let activeDecodes = 0;

    const resizeObserver = new ResizeObserver(function (entries) {
        for (const entry of entries) drawCanvas(entry.target);
    });

    function init(rootId, dotNetRef, opts) {
        const root = document.getElementById(rootId);
        if (!root) return;

        let inst = instances[rootId];
        if (!inst) {
            inst = { root: root, urls: new Map(), peaks: new Map(), queue: [] };
            instances[rootId] = inst;
        }
        inst.dotNet = dotNetRef;
        inst.opts = opts;

        // The root div survives every Blazor re-render of this always-mounted panel, so delegated listeners
        // attached once keep working for every wave element the panel renders later (list or card view).
        if (root._audioInit) return;
        root._audioInit = true;

        // Capture phase: mouseenter/mouseleave don't bubble, but capture-phase listeners on an ancestor
        // still see them for each descendant entered.
        root.addEventListener("mouseenter", function (e) {
            const wave = e.target.classList && e.target.classList.contains("ab-wave") ? e.target : null;
            if (!wave || !inst.opts.autoPlay) return;
            clearTimeout(hoverTimer);
            hoverTimer = setTimeout(function () { play(rootId, wave.dataset.name); }, HOVER_DELAY_MS);
        }, true);

        root.addEventListener("mouseleave", function (e) {
            const wave = e.target.classList && e.target.classList.contains("ab-wave") ? e.target : null;
            if (!wave) return;
            clearTimeout(hoverTimer);
            if (playing && playing.rootId === rootId && playing.name === wave.dataset.name) stop();
        }, true);

        // Click = play from the clicked point along the waveform (seeking in place if this clip is already
        // the one playing, rather than stopping it). Moving the cursor off the waveform still stops it.
        root.addEventListener("click", function (e) {
            const wave = e.target.closest && e.target.closest(".ab-wave");
            if (!wave) return;
            clearTimeout(hoverTimer);
            const rect = wave.getBoundingClientRect();
            const fraction = rect.width > 0 ? Math.min(1, Math.max(0, (e.clientX - rect.left) / rect.width)) : 0;
            if (playing && playing.rootId === rootId && playing.name === wave.dataset.name) seek(fraction);
            else play(rootId, wave.dataset.name, fraction);
        });
    }

    function updateOptions(rootId, opts) {
        const inst = instances[rootId];
        if (!inst) return;
        inst.opts = opts;
        if (audio && playing && playing.rootId === rootId) {
            audio.volume = opts.volume;
            audio.loop = opts.loop;
        }
    }

    // entries: [{ name, url, peaks }] — peaks is the cached waveform (a byte[] server-side, which Blazor's
    // interop delivers as a Uint8Array), or null when the server has nothing valid cached and this side
    // needs to decode the file itself.
    function setEntries(rootId, entries) {
        const inst = instances[rootId];
        if (!inst) return;

        inst.urls = new Map();
        inst.queue = [];
        const nextPeaks = new Map();
        for (const entry of entries) {
            inst.urls.set(entry.name, entry.url);
            if (entry.peaks) {
                nextPeaks.set(entry.name, entry.peaks);
            } else {
                inst.queue.push(entry.name);
            }
        }
        inst.peaks = nextPeaks;
        redraw(rootId, true);
        pumpDecodeQueue();
    }

    // "刷新音紋圖" — drop this file's waveform and decode it again, ahead of anything still queued.
    function recompute(rootId, name) {
        const inst = instances[rootId];
        if (!inst) return;
        inst.peaks.delete(name);
        inst.queue = inst.queue.filter(function (n) { return n !== name; });
        inst.queue.unshift(name);
        redrawName(rootId, name);
        pumpDecodeQueue();
    }

    // Called after every Blazor render: picks up canvases the render just created (view-mode switch, filter
    // change) and draws any whose data changed. ResizeObserver handles size changes — column resize, card
    // size slider, and a hidden tab becoming visible (0 -> real width) — on its own.
    function redraw(rootId, force) {
        const inst = instances[rootId];
        if (!inst) return;
        inst.root.querySelectorAll("canvas.ab-wave-canvas").forEach(function (canvas) {
            if (!canvas._observed) {
                canvas._observed = true;
                resizeObserver.observe(canvas);
            } else if (force || canvas._drawnName !== canvas.dataset.name) {
                drawCanvas(canvas);
            }
        });
    }

    function redrawName(rootId, name) {
        const inst = instances[rootId];
        if (!inst) return;
        inst.root.querySelectorAll("canvas.ab-wave-canvas").forEach(function (canvas) {
            if (canvas.dataset.name === name) drawCanvas(canvas);
        });
    }

    function drawCanvas(canvas) {
        const root = canvas.closest("[data-audio-root]");
        const inst = root && instances[root.id];
        const width = canvas.clientWidth;
        const height = canvas.clientHeight;
        if (!inst || width === 0 || height === 0) return;

        const dpr = window.devicePixelRatio || 1;
        canvas.width = Math.round(width * dpr);
        canvas.height = Math.round(height * dpr);
        const ctx = canvas.getContext("2d");
        ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
        ctx.clearRect(0, 0, width, height);
        canvas._drawnName = canvas.dataset.name;

        const peaks = inst.peaks.get(canvas.dataset.name);
        const mid = height / 2;

        if (!peaks || peaks === "error") {
            ctx.fillStyle = "#555";
            ctx.fillRect(0, mid, width, 1);
            ctx.fillStyle = peaks === "error" ? "#d08080" : "#888";
            ctx.font = "11px sans-serif";
            ctx.fillText(peaks === "error" ? "無法解碼" : "產生音紋中…", 6, 13);
            return;
        }

        const isPlaying = playing && playing.rootId === root.id && playing.name === canvas.dataset.name;
        ctx.fillStyle = isPlaying ? WAVE_COLOR_PLAYING : WAVE_COLOR;
        const columns = Math.max(1, Math.floor(width));
        for (let x = 0; x < columns; x++) {
            const from = Math.floor(x / columns * peaks.length);
            const to = Math.max(from + 1, Math.floor((x + 1) / columns * peaks.length));
            let max = 0;
            for (let i = from; i < to && i < peaks.length; i++) if (peaks[i] > max) max = peaks[i];
            const half = Math.max(0.5, max / 255 * (mid - 1));
            ctx.fillRect(x, mid - half, 1, half * 2);
        }
    }

    function pumpDecodeQueue() {
        while (activeDecodes < DECODE_CONCURRENCY) {
            const job = nextJob();
            if (!job) return;
            activeDecodes++;
            decode(job.rootId, job.name).finally(function () {
                activeDecodes--;
                pumpDecodeQueue();
            });
        }
    }

    function nextJob() {
        for (const rootId in instances) {
            const inst = instances[rootId];
            while (inst.queue.length > 0) {
                const name = inst.queue.shift();
                if (inst.urls.has(name) && !inst.peaks.has(name)) return { rootId: rootId, name: name };
            }
        }
        return null;
    }

    async function decode(rootId, name) {
        const inst = instances[rootId];
        const url = inst.urls.get(name);
        try {
            const response = await fetch(url, { cache: "no-store" });
            if (!response.ok) throw new Error("HTTP " + response.status);
            const data = await response.arrayBuffer();
            // An OfflineAudioContext needs no user gesture (unlike a live AudioContext) and resamples to its
            // own rate; 22.05kHz is plenty for a peak envelope and halves the decoded buffer's memory.
            const ctx = new OfflineAudioContext(1, 1, 22050);
            const buffer = await ctx.decodeAudioData(data);
            const peaks = computePeaks(buffer);
            inst.peaks.set(name, peaks);
            redrawName(rootId, name);
            await inst.dotNet.invokeMethodAsync("OnWaveformComputed", name, buffer.duration, peaks);
        } catch (err) {
            inst.peaks.set(name, "error");
            redrawName(rootId, name);
        }
    }

    // Peak |amplitude| per bucket across all channels, normalized to the file's own loudest bucket.
    function computePeaks(buffer) {
        const raw = new Float32Array(BUCKETS);
        const length = buffer.length;
        for (let ch = 0; ch < buffer.numberOfChannels; ch++) {
            const samples = buffer.getChannelData(ch);
            for (let b = 0; b < BUCKETS; b++) {
                const from = Math.floor(b / BUCKETS * length);
                const to = Math.floor((b + 1) / BUCKETS * length);
                let max = raw[b];
                for (let i = from; i < to; i++) {
                    const v = samples[i] < 0 ? -samples[i] : samples[i];
                    if (v > max) max = v;
                }
                raw[b] = max;
            }
        }

        let loudest = 0;
        for (let b = 0; b < BUCKETS; b++) if (raw[b] > loudest) loudest = raw[b];
        const bytes = new Uint8Array(BUCKETS);
        if (loudest > 0) for (let b = 0; b < BUCKETS; b++) bytes[b] = Math.round(raw[b] / loudest * 255);
        return bytes;
    }

    function play(rootId, name, fraction) {
        const inst = instances[rootId];
        const url = inst && inst.urls.get(name);
        if (!url) return;

        stop();
        if (!audio) {
            audio = new Audio();
            audio.addEventListener("ended", function () { if (!audio.loop) stop(); });
            // requestAnimationFrame (below) gives the smooth line, but it never fires while the window
            // isn't painting (minimized, covered); timeupdate still moves the line a few times a second.
            audio.addEventListener("timeupdate", function () { if (playing) setProgress(); });
        }
        audio.src = url;
        audio.loop = !!inst.opts.loop;
        audio.volume = inst.opts.volume;
        playing = { rootId: rootId, name: name };
        // duration is unknown until metadata arrives, so a start offset has to wait for it.
        // The token drops a listener left over from an earlier play() whose metadata never arrived before
        // the source changed, so it can't seek the new clip to the old clip's offset.
        const token = ++playToken;
        if (fraction > 0) audio.addEventListener("loadedmetadata", function () { if (token === playToken) seek(fraction); }, { once: true });
        audio.play().catch(function () { /* autoplay blocked before the page's first click — nothing to do */ });

        inst.root.querySelectorAll(".ab-wave").forEach(function (wave) {
            if (wave.dataset.name === name) wave.classList.add("playing");
        });
        redrawName(rootId, name);
        progressFrame = requestAnimationFrame(updateProgress);
    }

    function seek(fraction) {
        if (!audio || !(audio.duration > 0)) return;
        audio.currentTime = fraction * audio.duration;
        if (audio.paused) audio.play().catch(function () { });
        if (playing) setProgress();
    }

    function stop() {
        cancelAnimationFrame(progressFrame);
        clearTimeout(hoverTimer);
        if (audio) audio.pause();
        if (!playing) return;

        const stopped = playing;
        playing = null;
        const inst = instances[stopped.rootId];
        if (!inst) return;
        inst.root.querySelectorAll(".ab-wave.playing").forEach(function (wave) {
            wave.classList.remove("playing");
            const line = wave.querySelector(".ab-wave-progress");
            if (line) line.style.left = "0%";
        });
        redrawName(stopped.rootId, stopped.name);
    }

    function updateProgress() {
        if (!playing || !audio) return;
        setProgress();
        progressFrame = requestAnimationFrame(updateProgress);
    }

    function setProgress() {
        const inst = instances[playing.rootId];
        const fraction = audio.duration > 0 ? audio.currentTime / audio.duration : 0;
        inst.root.querySelectorAll(".ab-wave").forEach(function (wave) {
            if (wave.dataset.name !== playing.name) return;
            wave.classList.add("playing");
            const line = wave.querySelector(".ab-wave-progress");
            if (line) line.style.left = (fraction * 100) + "%";
        });
    }

    // List view's inline note editor: focus with the caret at the end, and grow with the content.
    function focusNote(elementId) {
        const el = document.getElementById(elementId);
        if (!el || el._noteInit) return;
        el._noteInit = true;
        const autosize = function () {
            el.style.height = "auto";
            el.style.height = el.scrollHeight + "px";
        };
        el.addEventListener("input", autosize);
        autosize();
        el.focus();
        el.setSelectionRange(el.value.length, el.value.length);
    }

    // List view header: SortableJS drag-to-reorder plus a drag-to-resize handle on each <th>'s right edge.
    // The resize handle is excluded from Sortable via `filter`, and the click that follows releasing a
    // resize is swallowed so it doesn't also toggle that column's sort.
    function initColumns(headerRowId, dotNetRef) {
        const row = document.getElementById(headerRowId);
        if (!row) return;

        storyForgeColumnSort.initReverting(headerRowId, dotNetRef, "OnColumnReordered", ".ab-col-resize");

        row._dotNet = dotNetRef;
        if (row._resizeInit) return;
        row._resizeInit = true;
        // Pointer events with pointer capture (not mousedown/mousemove on document): SortableJS listens for
        // pointerdown on this same row, and a compatibility mousedown/mousemove sequence isn't guaranteed
        // once a pointer event handler has run — capture routes every move to the handle regardless.
        row.addEventListener("pointerdown", function (e) {
            const handle = e.target.closest(".ab-col-resize");
            if (!handle || e.button !== 0) return;
            e.preventDefault();
            e.stopPropagation();
            handle.setPointerCapture(e.pointerId);

            const th = handle.parentElement;
            const table = th.closest("table");
            const startX = e.clientX;
            const startWidth = th.getBoundingClientRect().width;
            const startTableWidth = table.getBoundingClientRect().width;
            let width = startWidth;
            document.body.classList.add("ab-col-resizing");

            const onMove = function (ev) {
                width = Math.max(40, Math.round(startWidth + ev.clientX - startX));
                th.style.width = width + "px";
                table.style.width = (startTableWidth + width - startWidth) + "px";
            };
            const onUp = function () {
                handle.removeEventListener("pointermove", onMove);
                handle.removeEventListener("pointerup", onUp);
                handle.removeEventListener("pointercancel", onUp);
                document.body.classList.remove("ab-col-resizing");
                const swallow = function (ev) { ev.stopPropagation(); ev.preventDefault(); };
                window.addEventListener("click", swallow, true);
                setTimeout(function () { window.removeEventListener("click", swallow, true); }, 0);
                if (width !== startWidth) row._dotNet.invokeMethodAsync("OnColumnResized", th.dataset.key, width);
            };
            handle.addEventListener("pointermove", onMove);
            handle.addEventListener("pointerup", onUp);
            handle.addEventListener("pointercancel", onUp);
        }, true);
    }

    // ---- 調整音量 dialog preview ----
    // original -> GainNode(total gain) -> WaveShaper hard clip at ±1 -> GainNode(tab volume) -> speakers.
    // The clip stage sits BEFORE the tab's monitor volume, so clipping the written file would have is
    // audible even with the tab volume turned down, while +12 dB never blasts the listener. A/B only
    // swaps the gain value; the source keeps running, so the playback position never jumps.
    let previewCtx = null;
    let preview = null;
    let previewToken = 0;
    let previewKeyHandler = null;
    const previewBuffers = new Map();

    function dbToGain(db) { return Math.pow(10, db / 20); }

    async function previewStart(url, gainDb, monitorVolume) {
        previewStop();
        stop();
        const token = ++previewToken;
        if (!previewCtx) previewCtx = new AudioContext();
        if (previewCtx.state === "suspended") await previewCtx.resume();

        let buffer = previewBuffers.get(url);
        if (!buffer) {
            const response = await fetch(url, { cache: "no-store" });
            if (!response.ok) throw new Error("HTTP " + response.status);
            buffer = await previewCtx.decodeAudioData(await response.arrayBuffer());
            previewBuffers.clear();
            previewBuffers.set(url, buffer);
        }
        if (token !== previewToken) return false;

        const source = previewCtx.createBufferSource();
        source.buffer = buffer;
        source.loop = true;
        const gain = previewCtx.createGain();
        gain.gain.value = dbToGain(gainDb);
        const clip = previewCtx.createWaveShaper();
        clip.curve = new Float32Array([-1, 1]);
        const monitor = previewCtx.createGain();
        monitor.gain.value = monitorVolume;
        source.connect(gain).connect(clip).connect(monitor).connect(previewCtx.destination);
        source.start();
        preview = { source: source, gain: gain, monitor: monitor };
        return true;
    }

    function previewSetGain(gainDb) {
        if (preview) preview.gain.gain.setTargetAtTime(dbToGain(gainDb), previewCtx.currentTime, 0.01);
    }

    function previewStop() {
        previewToken++;
        if (!preview) return;
        try { preview.source.stop(); } catch (e) { }
        preview.source.disconnect();
        preview = null;
    }

    // Space toggles A/B while the dialog is open — except while typing in its number field.
    function previewBindKeys(dotNetRef) {
        previewUnbindKeys();
        previewKeyHandler = function (e) {
            if (e.key !== " " && e.code !== "Space") return;
            const tag = e.target && e.target.tagName;
            if (tag === "TEXTAREA" || (tag === "INPUT" && e.target.type !== "range" && e.target.type !== "checkbox")) return;
            e.preventDefault();
            e.stopPropagation();
            if (e.type === "keydown" && !e.repeat) dotNetRef.invokeMethodAsync("OnPreviewToggleAB");
        };
        document.addEventListener("keydown", previewKeyHandler, true);
        document.addEventListener("keyup", previewKeyHandler, true);
    }

    function previewUnbindKeys() {
        if (!previewKeyHandler) return;
        document.removeEventListener("keydown", previewKeyHandler, true);
        document.removeEventListener("keyup", previewKeyHandler, true);
        previewKeyHandler = null;
    }

    return {
        previewStart: previewStart,
        previewSetGain: previewSetGain,
        previewStop: previewStop,
        previewBindKeys: previewBindKeys,
        previewUnbindKeys: previewUnbindKeys,
        init: init,
        updateOptions: updateOptions,
        setEntries: setEntries,
        recompute: recompute,
        redraw: redraw,
        stopAll: stop,
        focusNote: focusNote,
        initColumns: initColumns,
    };
})();
