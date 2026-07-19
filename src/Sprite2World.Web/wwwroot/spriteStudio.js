window.spriteStudio = (() => {
    const states = new Map();
    let activeEditorId = null;

    const hexToRgba = hex => {
        const value = (hex || '#ffffff').replace('#', '').padEnd(6, 'f');
        return [parseInt(value.slice(0, 2), 16), parseInt(value.slice(2, 4), 16), parseInt(value.slice(4, 6), 16), 255];
    };
    const rgbaToHex = (r, g, b) => `#${[r, g, b].map(v => v.toString(16).padStart(2, '0')).join('')}`;
    const loadImage = url => new Promise((resolve, reject) => { const image = new Image(); image.onload = () => resolve(image); image.onerror = reject; image.src = url; });
    const clonePixels = pixels => new ImageData(new Uint8ClampedArray(pixels.data), pixels.width, pixels.height);

    function checker(ctx, width, height, size = 12) {
        ctx.fillStyle = '#0d1118'; ctx.fillRect(0, 0, width, height); ctx.fillStyle = '#171c26';
        for (let y = 0; y < height; y += size) for (let x = 0; x < width; x += size) if ((x / size + y / size) % 2 === 0) ctx.fillRect(x, y, size, size);
    }
    function snapshot(state) { return new Uint8ClampedArray(state.pixels.data); }
    function pushHistory(state) {
        state.history.splice(state.historyIndex + 1); state.history.push(snapshot(state));
        if (state.history.length > 80) state.history.shift(); state.historyIndex = state.history.length - 1;
    }
    function syncActivePixels(state) {
        state.sourceCtx.putImageData(state.pixels, 0, 0);
        if (state.mode === 'base') state.basePixels = clonePixels(state.pixels);
        else if (state.mode === 'normal') state.normalPixels = clonePixels(state.pixels);
        else if (state.mode === 'metallic') state.metallicPixels = clonePixels(state.pixels);
        else if (state.mode === 'roughness') state.roughnessPixels = clonePixels(state.pixels);
    }
    function restore(state, pixels) { state.pixels.data.set(pixels); syncActivePixels(state); state.dirty = true; render(state); notifyDirty(state); }
    function notifyDirty(state) { state.dotnet?.invokeMethodAsync('SpriteDirtyChanged', state.dirty).catch(() => {}); }
    function pixelIndex(state, x, y) { return (y * state.width + x) * 4; }
    function selectionBounds(state) {
        if (!state.selection) return null;
        const left = Math.max(0, Math.min(state.width - 1, Math.min(state.selection.x0, state.selection.x1)));
        const top = Math.max(0, Math.min(state.height - 1, Math.min(state.selection.y0, state.selection.y1)));
        const right = Math.max(0, Math.min(state.width - 1, Math.max(state.selection.x0, state.selection.x1)));
        const bottom = Math.max(0, Math.min(state.height - 1, Math.max(state.selection.y0, state.selection.y1)));
        return right < left || bottom < top ? null : { left, top, right, bottom, width: right - left + 1, height: bottom - top + 1 };
    }
    function isSelected(state, x, y) {
        const bounds = selectionBounds(state);
        return !bounds || (x >= bounds.left && x <= bounds.right && y >= bounds.top && y <= bounds.bottom);
    }
    function setPixel(state, x, y, rgba, respectSelection = true) {
        if (x < 0 || y < 0 || x >= state.width || y >= state.height) return;
        if (respectSelection && !isSelected(state, x, y)) return;
        const i = pixelIndex(state, x, y); for (let c = 0; c < 4; c++) state.pixels.data[i + c] = rgba[c];
    }
    function paint(state, x, y, erase = false) {
        const rgba = erase ? [0, 0, 0, 0] : hexToRgba(state.paintColor || state.color), radius = Math.max(0, Math.floor((state.brushSize - 1) / 2));
        for (let py = y - radius; py <= y + radius; py++) for (let px = x - radius; px <= x + radius; px++) setPixel(state, px, py, rgba);
    }
    function fill(state, x, y, color = state.color) {
        if (x < 0 || y < 0 || x >= state.width || y >= state.height) return;
        if (!isSelected(state, x, y)) return;
        const start = pixelIndex(state, x, y), target = Array.from(state.pixels.data.slice(start, start + 4)), replacement = hexToRgba(color);
        if (target.every((v, i) => v === replacement[i])) return;
        const queue = [[x, y]], visited = new Uint8Array(state.width * state.height);
        while (queue.length) {
            const [px, py] = queue.pop(); if (px < 0 || py < 0 || px >= state.width || py >= state.height || !isSelected(state, px, py)) continue;
            const key = py * state.width + px; if (visited[key]) continue; visited[key] = 1;
            const index = key * 4; if (!target.every((v, i) => state.pixels.data[index + i] === v)) continue;
            setPixel(state, px, py, replacement); queue.push([px - 1, py], [px + 1, py], [px, py - 1], [px, py + 1]);
        }
    }
    function line(state, x0, y0, x1, y1, erase = false) {
        let dx = Math.abs(x1 - x0), sx = x0 < x1 ? 1 : -1, dy = -Math.abs(y1 - y0), sy = y0 < y1 ? 1 : -1, error = dx + dy;
        while (true) { paint(state, x0, y0, erase); if (x0 === x1 && y0 === y1) break; const e2 = 2 * error; if (e2 >= dy) { error += dy; x0 += sx; } if (e2 <= dx) { error += dx; y0 += sy; } }
    }
    function rectangle(state, x0, y0, x1, y1) { line(state, x0, y0, x1, y0); line(state, x1, y0, x1, y1); line(state, x1, y1, x0, y1); line(state, x0, y1, x0, y0); }
    function copySelection(state) {
        const bounds = selectionBounds(state); if (!bounds || state.mode === 'lit') return false;
        const data = new Uint8ClampedArray(bounds.width * bounds.height * 4);
        for (let y = 0; y < bounds.height; y++) for (let x = 0; x < bounds.width; x++) {
            const sourceIndex = pixelIndex(state, bounds.left + x, bounds.top + y), targetIndex = (y * bounds.width + x) * 4;
            data.set(state.pixels.data.slice(sourceIndex, sourceIndex + 4), targetIndex);
        }
        state.clipboard = { width: bounds.width, height: bounds.height, data, x: bounds.left, y: bounds.top };
        return true;
    }
    function deleteSelection(state) {
        const bounds = selectionBounds(state); if (!bounds || state.mode === 'lit') return false;
        for (let y = bounds.top; y <= bounds.bottom; y++) for (let x = bounds.left; x <= bounds.right; x++) setPixel(state, x, y, [0, 0, 0, 0], false);
        commit(state); pushHistory(state); return true;
    }
    function beginFloatingSelection(state, cut) {
        const bounds = selectionBounds(state); if (!bounds || !copySelection(state)) return false;
        const clipboard = state.clipboard;
        const canvas = document.createElement('canvas'); canvas.width = clipboard.width; canvas.height = clipboard.height;
        canvas.getContext('2d').putImageData(new ImageData(new Uint8ClampedArray(clipboard.data), clipboard.width, clipboard.height), 0, 0);
        if (cut) deleteSelection(state);
        state.floating = { clipboard, canvas, x: bounds.left, y: bounds.top };
        state.tool = 'select'; state.editor.style.cursor = 'copy';
        state.dotnet?.invokeMethodAsync('SpriteToolPicked', 'select').catch(() => {});
        render(state); return true;
    }
    function moveFloatingSelection(state, x, y) {
        if (!state.floating) return;
        state.floating.x = Math.max(0, Math.min(state.width - state.floating.clipboard.width, x - Math.floor(state.floating.clipboard.width / 2)));
        state.floating.y = Math.max(0, Math.min(state.height - state.floating.clipboard.height, y - Math.floor(state.floating.clipboard.height / 2)));
        render(state);
    }
    function placeFloatingSelection(state) {
        const floating = state.floating; if (!floating || state.mode === 'lit') return false;
        for (let y = 0; y < floating.clipboard.height; y++) for (let x = 0; x < floating.clipboard.width; x++) {
            const sourceIndex = (y * floating.clipboard.width + x) * 4;
            setPixel(state, floating.x + x, floating.y + y, floating.clipboard.data.slice(sourceIndex, sourceIndex + 4), false);
        }
        state.selection = { x0: floating.x, y0: floating.y, x1: floating.x + floating.clipboard.width - 1, y1: floating.y + floating.clipboard.height - 1 };
        commit(state); pushHistory(state); return true;
    }
    function point(state, event) {
        const rect = state.editor.getBoundingClientRect();
        return { x: Math.floor((event.clientX - rect.left - state.offsetX) / state.scale), y: Math.floor((event.clientY - rect.top - state.offsetY) / state.scale) };
    }
    function canvasSize(canvas, minimumWidth, minimumHeight) {
        const rect = canvas.parentElement.getBoundingClientRect(), dpr = Math.min(2, devicePixelRatio || 1), width = Math.max(minimumWidth, Math.floor(rect.width)), height = Math.max(minimumHeight, Math.floor(rect.height));
        canvas.width = width * dpr; canvas.height = height * dpr; canvas.style.width = `${width}px`; canvas.style.height = `${height}px`;
        const ctx = canvas.getContext('2d'); ctx.setTransform(dpr, 0, 0, dpr, 0, 0); ctx.imageSmoothingEnabled = false; return { ctx, width, height };
    }
    function render(state) {
        if (!state.pixels) return;
        if (state.mode === 'lit') { renderLitCanvas(state, state.editor, 320, 280, state.seamless); renderLit(state); return; }
        const { ctx, width, height } = canvasSize(state.editor, 320, 280);
        const tileCount = state.seamless ? 3 : 1;
        state.scale = Math.max(2, Math.min(48, state.zoom * Math.floor(Math.min((width - 60) / (state.width * tileCount), (height - 60) / (state.height * tileCount)))));
        const spriteWidth = state.width * state.scale, spriteHeight = state.height * state.scale;
        const groupWidth = spriteWidth * tileCount, groupHeight = spriteHeight * tileCount;
        const groupX = Math.floor((width - groupWidth) / 2 + (state.panX || 0)), groupY = Math.floor((height - groupHeight) / 2 + (state.panY || 0));
        state.offsetX = groupX + (state.seamless ? spriteWidth : 0); state.offsetY = groupY + (state.seamless ? spriteHeight : 0);
        ctx.fillStyle = '#181e28'; ctx.fillRect(0, 0, width, height);
        const tileOffsets = state.seamless ? [-1, 0, 1] : [0];
        for (const tileY of tileOffsets) for (const tileX of tileOffsets) {
            const x = state.offsetX + tileX * spriteWidth, y = state.offsetY + tileY * spriteHeight, center = tileX === 0 && tileY === 0;
            ctx.save(); ctx.beginPath(); ctx.rect(x, y, spriteWidth, spriteHeight); ctx.clip(); checker(ctx, width, height, 16); ctx.restore();
            ctx.drawImage(state.source, x, y, spriteWidth, spriteHeight);
            ctx.save(); ctx.strokeStyle = center && state.seamless ? '#a78cff' : 'rgba(119,128,143,.7)'; ctx.lineWidth = center && state.seamless ? 2 : 1; ctx.strokeRect(x + .5, y + .5, spriteWidth, spriteHeight); ctx.restore();
        }
        if (!state.seamless) { ctx.save(); ctx.shadowColor = 'rgba(0,0,0,.72)'; ctx.shadowBlur = 18; ctx.strokeStyle = '#77808f'; ctx.lineWidth = 1.5; ctx.strokeRect(state.offsetX + .5, state.offsetY + .5, spriteWidth, spriteHeight); ctx.restore(); }
        if (state.floating) {
            const floating = state.floating, x = state.offsetX + floating.x * state.scale, y = state.offsetY + floating.y * state.scale;
            ctx.save(); ctx.globalAlpha = .78; ctx.drawImage(floating.canvas, x, y, floating.clipboard.width * state.scale, floating.clipboard.height * state.scale); ctx.globalAlpha = 1;
            ctx.setLineDash([4, 3]); ctx.strokeStyle = '#a78cff'; ctx.lineWidth = 1.5; ctx.strokeRect(x + .5, y + .5, floating.clipboard.width * state.scale, floating.clipboard.height * state.scale); ctx.restore();
        }
        if (state.grid && state.scale >= 5) {
            const gridSize = Math.max(1, Math.min(8, state.gridSize || 1));
            ctx.beginPath(); ctx.strokeStyle = gridSize > 1 ? 'rgba(199,207,231,.62)' : 'rgba(190,199,225,.48)'; ctx.lineWidth = gridSize > 1 ? 1.25 : 1;
            for (let x = 0; x < state.width; x += gridSize) { const px = state.offsetX + x * state.scale + .5; ctx.moveTo(px, state.offsetY); ctx.lineTo(px, state.offsetY + state.height * state.scale); }
            for (let y = 0; y < state.height; y += gridSize) { const py = state.offsetY + y * state.scale + .5; ctx.moveTo(state.offsetX, py); ctx.lineTo(state.offsetX + state.width * state.scale, py); }
            ctx.moveTo(state.offsetX + state.width * state.scale + .5, state.offsetY); ctx.lineTo(state.offsetX + state.width * state.scale + .5, state.offsetY + state.height * state.scale);
            ctx.moveTo(state.offsetX, state.offsetY + state.height * state.scale + .5); ctx.lineTo(state.offsetX + state.width * state.scale, state.offsetY + state.height * state.scale + .5);
            ctx.stroke();
        }
        if (state.selection) {
            const s = selectionBounds(state); ctx.setLineDash([5, 3]); ctx.strokeStyle = '#fff'; ctx.lineWidth = 1;
            ctx.strokeRect(state.offsetX + s.left * state.scale + .5, state.offsetY + s.top * state.scale + .5, s.width * state.scale, s.height * state.scale); ctx.setLineDash([]);
        }
        renderLit(state);
    }
    function buildLitPixels(state) {
        const base = state.basePixels, normal = state.normalPixels, metallic = state.metallicPixels, roughness = state.roughnessPixels, output = new ImageData(state.width, state.height), light = state.light || { x: state.width * .72, y: state.height * .28 };
        for (let y = 0; y < state.height; y++) for (let x = 0; x < state.width; x++) {
            const i = pixelIndex(state, x, y), alpha = base.data[i + 3]; if (!alpha) continue;
            if (!state.lighting) { output.data.set(base.data.slice(i, i + 4), i); continue; }
            let nx = 0, ny = 0, nz = 1;
            if (normal) { nx = (normal.data[i] / 127.5 - 1) * state.normalStrength; ny = (normal.data[i + 1] / 127.5 - 1) * state.normalStrength; nz = normal.data[i + 2] / 127.5 - 1; const nl = Math.hypot(nx, ny, nz) || 1; nx /= nl; ny /= nl; nz /= nl; }
            let lx = light.x - x, ly = light.y - y, lz = Math.max(2, Math.max(state.width, state.height) * .45), length = Math.hypot(lx, ly, lz); lx /= length; ly /= length; lz /= length;
            const diffuse = Math.max(0, nx * lx + ny * ly + nz * lz), distance = Math.hypot(light.x - x, light.y - y), falloff = Math.max(0, 1 - distance / Math.max(state.width, state.height));
            const metallicValue = metallic ? metallic.data[i] / 255 : state.metallicStrength, roughnessValue = roughness ? roughness.data[i] / 255 : state.roughnessStrength;
            const highlight = Math.pow(diffuse, 4 + roughnessValue * 28) * metallicValue * 140, shade = (.14 + diffuse * (.42 + falloff * .72)) * state.lightIntensity;
            output.data[i] = Math.min(255, base.data[i] * shade + (18 + highlight) * falloff); output.data[i + 1] = Math.min(255, base.data[i + 1] * shade + (14 + highlight) * falloff); output.data[i + 2] = Math.min(255, base.data[i + 2] * shade + (8 + highlight) * falloff); output.data[i + 3] = alpha;
        }
        return output;
    }
    function renderLitCanvas(state, canvas, minimumWidth, minimumHeight, seamless = false) {
        if (!canvas || !state.basePixels) return;
        const { ctx, width, height } = canvasSize(canvas, minimumWidth, minimumHeight); checker(ctx, width, height, 12);
        const output = buildLitPixels(state); state.litSource.width = state.width; state.litSource.height = state.height; state.litSource.getContext('2d').putImageData(output, 0, 0);
        const tileCount = seamless ? 3 : 1, scale = Math.max(1, Math.floor(Math.min((width - 20) / (state.width * tileCount), (height - 20) / (state.height * tileCount))));
        const spriteWidth = state.width * scale, spriteHeight = state.height * scale, left = Math.floor((width - spriteWidth * tileCount) / 2), top = Math.floor((height - spriteHeight * tileCount) / 2);
        for (let y = 0; y < tileCount; y++) for (let x = 0; x < tileCount; x++) ctx.drawImage(state.litSource, left + x * spriteWidth, top + y * spriteHeight, spriteWidth, spriteHeight);
        if (seamless) { ctx.strokeStyle = '#a78cff'; ctx.lineWidth = 2; ctx.strokeRect(left + spriteWidth + .5, top + spriteHeight + .5, spriteWidth, spriteHeight); }
    }
    function renderLit(state) { renderLitCanvas(state, state.lit, 220, 150); }
    function commit(state) { syncActivePixels(state); state.dirty = true; render(state); notifyDirty(state); }
    function pointerDown(state, event) {
        if (event.button !== 0 && event.button !== 2) return;
        event.preventDefault(); activeEditorId = state.editor.id; state.editor.focus(); if (state.mode === 'lit') return;
        state.paintColor = event.button === 2 ? state.secondaryColor : state.color;
        const p = point(state, event); state.pointerDown = true; state.start = p; state.last = p;
        if (state.seamless && state.tool !== 'move' && (p.x < 0 || p.y < 0 || p.x >= state.width || p.y >= state.height)) { state.pointerDown = false; state.paintColor = null; return; }
        if (state.floating) { moveFloatingSelection(state, p.x, p.y); placeFloatingSelection(state); state.pointerDown = false; }
        else if (state.tool === 'pointer') { state.selection = null; state.pointerDown = false; render(state); }
        else if (state.tool === 'move') { state.panStart = { x: event.clientX, y: event.clientY, panX: state.panX || 0, panY: state.panY || 0 }; state.editor.style.cursor = 'grabbing'; }
        else if (state.tool === 'fill') { fill(state, p.x, p.y, state.paintColor); commit(state); pushHistory(state); state.pointerDown = false; state.paintColor = null; }
        else if (state.tool === 'eyedropper') { const i = pixelIndex(state, p.x, p.y), picked = rgbaToHex(state.pixels.data[i], state.pixels.data[i + 1], state.pixels.data[i + 2]); if (event.button === 2) { state.secondaryColor = picked; state.dotnet?.invokeMethodAsync('SpriteSecondaryColorPicked', picked).catch(() => {}); } else { state.color = picked; state.dotnet?.invokeMethodAsync('SpriteColorPicked', picked).catch(() => {}); } state.pointerDown = false; state.paintColor = null; }
        else if (state.tool === 'select') {
            if (p.x < 0 || p.y < 0 || p.x >= state.width || p.y >= state.height) { state.selection = null; state.pointerDown = false; }
            else state.selection = { x0: p.x, y0: p.y, x1: p.x, y1: p.y };
            render(state);
        }
        else if (state.tool === 'pencil' || state.tool === 'eraser') { paint(state, p.x, p.y, state.tool === 'eraser'); commit(state); }
    }
    function pointerMove(state, event) {
        if (state.mode === 'lit') { const rect = state.editor.getBoundingClientRect(); state.light = { x: (event.clientX - rect.left) / rect.width * state.width, y: (event.clientY - rect.top) / rect.height * state.height }; render(state); return; }
        const p = point(state, event); if (state.floating) { moveFloatingSelection(state, p.x, p.y); return; } if (!state.pointerDown) return;
        if (state.tool === 'move') { state.panX = state.panStart.panX + event.clientX - state.panStart.x; state.panY = state.panStart.panY + event.clientY - state.panStart.y; render(state); }
        else if (state.tool === 'pencil' || state.tool === 'eraser') { line(state, state.last.x, state.last.y, p.x, p.y, state.tool === 'eraser'); state.last = p; commit(state); }
        else if (state.tool === 'select') { state.selection.x1 = p.x; state.selection.y1 = p.y; render(state); }
    }
    function pointerUp(state, event) {
        if (!state.pointerDown || state.mode === 'lit') return; const p = point(state, event);
        if (state.tool === 'line') { line(state, state.start.x, state.start.y, p.x, p.y); commit(state); pushHistory(state); }
        else if (state.tool === 'rectangle') { rectangle(state, state.start.x, state.start.y, p.x, p.y); commit(state); pushHistory(state); }
        else if (state.tool === 'pencil' || state.tool === 'eraser') pushHistory(state);
        state.pointerDown = false; state.paintColor = null; if (state.tool === 'move') state.editor.style.cursor = 'grab';
    }
    async function pixelsFromUrl(url, width, height, kind, basePixels) {
        const canvas = document.createElement('canvas'); canvas.width = width; canvas.height = height; const ctx = canvas.getContext('2d', { willReadFrequently: true }); ctx.imageSmoothingEnabled = false;
        if (url) { const image = await loadImage(url); ctx.drawImage(image, 0, 0, width, height); return ctx.getImageData(0, 0, width, height); }
        const output = new ImageData(width, height);
        for (let i = 0; i < output.data.length; i += 4) {
            const alpha = basePixels?.data[i + 3] ?? 255;
            if (kind === 'normal') { output.data[i] = 128; output.data[i + 1] = 128; output.data[i + 2] = 255; }
            else if (kind === 'roughness') { output.data[i] = 128; output.data[i + 1] = 128; output.data[i + 2] = 128; }
            output.data[i + 3] = alpha;
        }
        return output;
    }
    async function init(editorId, litId, dotnet, assetId, baseUrl, activeUrl, normalUrl, metallicUrl, roughnessUrl, options) {
        const editor = document.getElementById(editorId), lit = document.getElementById(litId); if (!editor || !lit) return;
        let state = states.get(editorId);
        if (!state || state.editor !== editor) {
            state = { editor, lit, dotnet, source: document.createElement('canvas'), litSource: document.createElement('canvas'), history: [], historyIndex: -1, tool: 'pencil', color: '#d5d7de', secondaryColor: '#1c222b', brushSize: 1, zoom: 1, grid: true, gridSize: 1, seamless: false, lighting: true, mode: 'base', selection: null, dirty: false, panX: 0, panY: 0 };
            states.set(editorId, state); state.sourceCtx = state.source.getContext('2d', { willReadFrequently: true }); editor.tabIndex = 0;
            editor.addEventListener('pointerdown', e => pointerDown(state, e)); editor.addEventListener('pointermove', e => pointerMove(state, e)); editor.addEventListener('pointerup', e => pointerUp(state, e)); editor.addEventListener('pointercancel', e => pointerUp(state, e)); editor.addEventListener('focus', () => activeEditorId = editorId);
            editor.addEventListener('contextmenu', e => e.preventDefault());
            lit.addEventListener('pointermove', e => { const rect = lit.getBoundingClientRect(); state.light = { x: (e.clientX - rect.left) / rect.width * state.width, y: (e.clientY - rect.top) / rect.height * state.height }; render(state); });
            new ResizeObserver(() => render(state)).observe(editor.parentElement); new ResizeObserver(() => renderLit(state)).observe(lit.parentElement);
        }
        activeEditorId = editorId; state.dotnet = dotnet; state.mode = options?.mode || 'base'; state.tool = options?.tool || state.tool; state.color = options?.color || state.color; state.secondaryColor = options?.secondaryColor || state.secondaryColor; state.brushSize = options?.brushSize || state.brushSize; state.zoom = options?.zoom || state.zoom; state.grid = options?.grid ?? state.grid; state.gridSize = options?.gridSize ?? state.gridSize; state.seamless = options?.seamless ?? state.seamless; state.lighting = options?.lighting ?? state.lighting; state.normalStrength = options?.normalStrength ?? 1; state.metallicStrength = options?.metallicStrength ?? 0; state.roughnessStrength = options?.roughnessStrength ?? .5; state.lightIntensity = options?.lightIntensity ?? 1; state.normalUrl = normalUrl || ''; state.metallicUrl = metallicUrl || ''; state.roughnessUrl = roughnessUrl || '';
        const cacheKey = [assetId, baseUrl, activeUrl, normalUrl, metallicUrl, roughnessUrl, state.mode].join('|');
        if (state.cacheKey !== cacheKey) {
            const baseImage = await loadImage(baseUrl); state.width = baseImage.naturalWidth; state.height = baseImage.naturalHeight;
            state.basePixels = await pixelsFromUrl(baseUrl, state.width, state.height, 'base'); state.normalPixels = await pixelsFromUrl(normalUrl, state.width, state.height, 'normal', state.basePixels); state.metallicPixels = await pixelsFromUrl(metallicUrl, state.width, state.height, 'metallic', state.basePixels); state.roughnessPixels = await pixelsFromUrl(roughnessUrl, state.width, state.height, 'roughness', state.basePixels);
            state.pixels = state.mode === 'normal' ? clonePixels(state.normalPixels) : state.mode === 'metallic' ? clonePixels(state.metallicPixels) : state.mode === 'roughness' ? clonePixels(state.roughnessPixels) : clonePixels(state.basePixels);
            if (activeUrl && state.mode !== 'base' && state.mode !== 'lit') state.pixels = await pixelsFromUrl(activeUrl, state.width, state.height, state.mode, state.basePixels);
            state.source.width = state.width; state.source.height = state.height; state.sourceCtx.putImageData(state.pixels, 0, 0); state.history = [snapshot(state)]; state.historyIndex = 0; state.dirty = false; state.selection = null; state.floating = null; state.cacheKey = cacheKey; notifyDirty(state);
        }
        setTool(editorId, state.tool); render(state);
    }
    function setTool(id, tool) { const state = states.get(id); if (state) { if (state.floating && tool !== 'select') { tool = 'select'; state.dotnet?.invokeMethodAsync('SpriteToolPicked', 'select').catch(() => {}); } state.tool = tool; state.editor.style.cursor = state.floating ? 'copy' : state.mode === 'lit' ? 'crosshair' : tool === 'pointer' ? 'default' : tool === 'move' ? 'grab' : tool === 'eyedropper' ? 'copy' : tool === 'fill' ? 'cell' : 'crosshair'; } }
    function setColor(id, color) { const state = states.get(id); if (state) state.color = color; }
    function setSecondaryColor(id, color) { const state = states.get(id); if (state) state.secondaryColor = color; }
    function setColors(id, color, secondaryColor) { const state = states.get(id); if (state) { state.color = color; state.secondaryColor = secondaryColor; } }
    function setBrushSize(id, size) { const state = states.get(id); if (state) state.brushSize = Math.max(1, Math.min(10, Number(size) || 1)); }
    function setZoom(id, zoom) { const state = states.get(id); if (state) { state.zoom = Math.max(.25, Math.min(8, Number(zoom) || 1)); render(state); } }
    function setGrid(id, grid) { const state = states.get(id); if (state) { state.grid = !!grid; render(state); } }
    function setGridSize(id, gridSize) { const state = states.get(id); if (state) { state.gridSize = [1, 2, 3, 4, 8].includes(Number(gridSize)) ? Number(gridSize) : 1; render(state); } }
    function setSeamless(id, seamless) { const state = states.get(id); if (state) { state.seamless = !!seamless; render(state); } }
    function setLighting(id, lighting) { const state = states.get(id); if (state) { state.lighting = !!lighting; render(state); } }
    function setMaterialSettings(id, normalStrength, metallicStrength, roughnessStrength, lightIntensity) { const state = states.get(id); if (state) { state.normalStrength = Number(normalStrength); state.metallicStrength = Number(metallicStrength); state.roughnessStrength = Number(roughnessStrength); state.lightIntensity = Number(lightIntensity); render(state); } }
    function undo(id) { const state = states.get(id); if (!state || state.mode === 'lit' || state.historyIndex <= 0) return; state.floating = null; state.editor.style.cursor = 'crosshair'; state.historyIndex--; restore(state, state.history[state.historyIndex]); }
    function redo(id) { const state = states.get(id); if (!state || state.mode === 'lit' || state.historyIndex >= state.history.length - 1) return; state.floating = null; state.editor.style.cursor = 'crosshair'; state.historyIndex++; restore(state, state.history[state.historyIndex]); }
    function selectAll(id) { const state = states.get(id); if (!state || state.mode === 'lit') return; state.selection = { x0: 0, y0: 0, x1: state.width - 1, y1: state.height - 1 }; render(state); }
    function copy(id) { const state = states.get(id); if (state) beginFloatingSelection(state, false); }
    function cut(id) { const state = states.get(id); if (state) beginFloatingSelection(state, true); }
    function deleteSelected(id) { const state = states.get(id); if (state) deleteSelection(state); }
    function clearSelection(id) { const state = states.get(id); if (!state) return; state.selection = null; state.floating = null; state.editor.style.cursor = 'crosshair'; render(state); }
    function exportBase64(id) { const state = states.get(id); if (!state) return null; if (state.mode !== 'lit') syncActivePixels(state); return state.source.toDataURL('image/png').split(',')[1]; }
    function pixelsToBase64(pixels) { const canvas = document.createElement('canvas'); canvas.width = pixels.width; canvas.height = pixels.height; canvas.getContext('2d').putImageData(pixels, 0, 0); return canvas.toDataURL('image/png').split(',')[1]; }
    function scalarPixels(state, value) { const output = new ImageData(state.width, state.height), gray = Math.round(Math.max(0, Math.min(1, Number(value) || 0)) * 255); for (let i = 0; i < output.data.length; i += 4) { output.data[i] = output.data[i + 1] = output.data[i + 2] = gray; output.data[i + 3] = state.basePixels.data[i + 3]; } return output; }
    function exportChannelBase64(id, channel) {
        const state = states.get(id); if (!state) return null; const kind = String(channel || 'base').toLowerCase(); if (state.mode !== 'lit') syncActivePixels(state);
        if (kind === 'base') return pixelsToBase64(state.basePixels);
        if (kind === 'normal') return pixelsToBase64(state.normalPixels);
        if (kind === 'metallic') return pixelsToBase64(state.mode === 'metallic' || state.metallicUrl ? state.metallicPixels : scalarPixels(state, state.metallicStrength));
        if (kind === 'roughness') return pixelsToBase64(state.mode === 'roughness' || state.roughnessUrl ? state.roughnessPixels : scalarPixels(state, state.roughnessStrength));
        return null;
    }
    function exportMaterialEntries(id, stem) { return ['base', 'normal', 'roughness', 'metallic'].map(channel => ({ name: `${stem}-${channel}.png`, base64: exportChannelBase64(id, channel) })); }
    function downloadMaterialPackage(id, stem) { window.sprite2world?.downloadZipBase64(`${stem}-materials.zip`, exportMaterialEntries(id, stem)); }
    function markSaved(id) { const state = states.get(id); if (state) state.dirty = false; }
    async function applyGeneratedPixels(id, pixels) {
        const state = states.get(id); if (!state || state.mode !== 'base' || !Array.isArray(pixels)) return;
        state.pixels.data.fill(0); syncActivePixels(state); render(state);
        const ordered = pixels.map(pixel => ({ x: Number(pixel.x ?? pixel.X), y: Number(pixel.y ?? pixel.Y), color: pixel.color ?? pixel.Color }))
            .filter(pixel => Number.isInteger(pixel.x) && Number.isInteger(pixel.y) && /^#[0-9a-f]{6}$/i.test(pixel.color || ''))
            .sort((a, b) => a.y - b.y || a.x - b.x);
        const perFrame = Math.max(1, Math.ceil(ordered.length / 90));
        for (let index = 0; index < ordered.length; index += perFrame) {
            for (const pixel of ordered.slice(index, index + perFrame)) setPixel(state, pixel.x, pixel.y, hexToRgba(pixel.color));
            syncActivePixels(state); render(state);
            await new Promise(resolve => requestAnimationFrame(resolve));
        }
        state.dirty = true; pushHistory(state); notifyDirty(state);
    }
    function renderGeneratedPreview(id, width, height, pixels) {
        const canvas = document.getElementById(id); if (!canvas) return;
        canvas.width = Math.max(1, Number(width)); canvas.height = Math.max(1, Number(height));
        const ctx = canvas.getContext('2d'); ctx.clearRect(0, 0, canvas.width, canvas.height);
        for (const pixel of pixels || []) { ctx.fillStyle = pixel.color; ctx.fillRect(pixel.x, pixel.y, 1, 1); }
    }
    async function generateNormal(sourceUrl, strength = 1, invertY = false) {
        const image = await loadImage(sourceUrl), width = image.naturalWidth, height = image.naturalHeight, source = document.createElement('canvas'); source.width = width; source.height = height; const sourceCtx = source.getContext('2d', { willReadFrequently: true }); sourceCtx.drawImage(image, 0, 0); const pixels = sourceCtx.getImageData(0, 0, width, height), output = new ImageData(width, height);
        const h = (x, y) => { x = Math.max(0, Math.min(width - 1, x)); y = Math.max(0, Math.min(height - 1, y)); const i = (y * width + x) * 4; return pixels.data[i + 3] / 255 * (pixels.data[i] * .2126 + pixels.data[i + 1] * .7152 + pixels.data[i + 2] * .0722) / 255; };
        for (let y = 0; y < height; y++) for (let x = 0; x < width; x++) { const i = (y * width + x) * 4, dx = (h(x - 1, y) - h(x + 1, y)) * strength, rawDy = (h(x, y - 1) - h(x, y + 1)) * strength, dy = invertY ? -rawDy : rawDy, length = Math.hypot(dx, dy, 1); output.data[i] = Math.round((dx / length * .5 + .5) * 255); output.data[i + 1] = Math.round((dy / length * .5 + .5) * 255); output.data[i + 2] = Math.round((1 / length * .5 + .5) * 255); output.data[i + 3] = pixels.data[i + 3]; }
        const canvas = document.createElement('canvas'); canvas.width = width; canvas.height = height; canvas.getContext('2d').putImageData(output, 0, 0); return canvas.toDataURL('image/png').split(',')[1];
    }
    function createBlankBase64(width, height) { const canvas = document.createElement('canvas'); canvas.width = Math.max(1, Math.min(384, Number(width))); canvas.height = Math.max(1, Math.min(384, Number(height))); return canvas.toDataURL('image/png').split(',')[1]; }
    async function resizeBase64(base64, width, height) { const image = await loadImage(`data:image/png;base64,${base64}`), canvas = document.createElement('canvas'); canvas.width = width; canvas.height = height; const ctx = canvas.getContext('2d'); ctx.imageSmoothingEnabled = false; ctx.drawImage(image, 0, 0, width, height); return canvas.toDataURL('image/png').split(',')[1]; }

    window.addEventListener('keydown', event => {
        const state = states.get(activeEditorId); if (!state) return; const target = event.target, typing = target && (target.matches?.('input,textarea,select') || target.isContentEditable); if (typing) return;
        const key = event.key.toLowerCase();
        if ((event.metaKey || event.ctrlKey) && key === 'z' && !event.shiftKey) { event.preventDefault(); undo(activeEditorId); return; }
        if ((event.metaKey || event.ctrlKey) && (key === 'y' || (event.shiftKey && key === 'z'))) { event.preventDefault(); redo(activeEditorId); return; }
        if ((event.metaKey || event.ctrlKey) && key === 'a') { event.preventDefault(); state.selection = { x0: 0, y0: 0, x1: state.width - 1, y1: state.height - 1 }; render(state); return; }
        if ((event.metaKey || event.ctrlKey) && key === 'c') { if (beginFloatingSelection(state, false)) event.preventDefault(); return; }
        if ((event.metaKey || event.ctrlKey) && key === 'x') { if (beginFloatingSelection(state, true)) event.preventDefault(); return; }
        if (key === 'delete' || key === 'backspace') { if (deleteSelection(state)) event.preventDefault(); return; }
        if (key === 'escape' && (state.selection || state.floating)) { event.preventDefault(); state.selection = null; state.floating = null; state.editor.style.cursor = 'crosshair'; render(state); return; }
        const tools = { v: 'pointer', b: 'pencil', e: 'eraser', g: 'fill', i: 'eyedropper', m: 'select', h: 'move' };
        if (tools[key] && !event.metaKey && !event.ctrlKey && !event.altKey) { event.preventDefault(); setTool(activeEditorId, tools[key]); state.dotnet?.invokeMethodAsync('SpriteToolPicked', tools[key]).catch(() => {}); }
    });

    return { init, setTool, setColor, setSecondaryColor, setColors, setBrushSize, setZoom, setGrid, setGridSize, setSeamless, setLighting, setMaterialSettings, undo, redo, selectAll, copy, cut, deleteSelected, clearSelection, exportBase64, exportChannelBase64, exportMaterialEntries, downloadMaterialPackage, markSaved, applyGeneratedPixels, renderGeneratedPreview, generateNormal, createBlankBase64, resizeBase64 };
})();
