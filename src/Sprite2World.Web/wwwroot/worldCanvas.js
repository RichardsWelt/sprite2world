window.sprite2world = (() => {
    const states = new Map();
    const colors = { Void: '#070911', Floor: '#353947', Wall: '#171a25', Door: '#8c5737', Obstacle: '#70442d', Decoration: '#74578d', Start: '#2ec17e', Exit: '#e64d62' };

    function get(id) { return states.get(id); }
    function init(id, dotnet) {
        const canvas = document.getElementById(id); if (!canvas) return;
        const existing = states.get(id);
        if (existing && existing.canvas === canvas) { if (dotnet) existing.dotnet = dotnet; return; }
        const callback = dotnet || existing?.dotnet;
        if (existing) { existing.resizeObserver?.disconnect(); if (existing.renderFrame) cancelAnimationFrame(existing.renderFrame); if (existing.cameraNotifyTimer) clearTimeout(existing.cameraNotifyTimer); states.delete(id); }
        const state = { canvas, context: canvas.getContext('2d'), dotnet: callback, world: null, assets: new Map(), images: new Map(), litCache: new Map(), materialExportCache: new Map(), normalSources: new Map(), lightCanvas: document.createElement('canvas'), options: {}, player: null, playing: false, won: false, panX: 0, panY: 0, cameraInitialized: false, cameraNotifyTimer: null, pointerX: null, pointerY: null, spaceHeld: false, dragging: false, painting: false, lastEdit: null, lights: [], cursorLight: null, frameLights: [], renderFrame: null };
        states.set(id, state);
        canvas.addEventListener('keydown', event => { if (event.code === 'Space' && !state.playing) { state.spaceHeld = true; event.preventDefault(); return; } key(state, event); });
        canvas.addEventListener('keyup', event => { if (event.code === 'Space') { state.spaceHeld = false; event.preventDefault(); } });
        canvas.addEventListener('blur', () => { state.spaceHeld = false; });
        canvas.addEventListener('pointerdown', event => {
            rememberPointer(state, event);
            const wantsPan = state.options.tool === 'pan' || event.button === 1 || event.button === 2 || state.spaceHeld;
            if (wantsPan) { event.preventDefault(); state.dragging = true; state.lastX = event.clientX; state.lastY = event.clientY; canvas.style.cursor = 'grabbing'; canvas.setPointerCapture(event.pointerId); return; }
            if (state.options.tool === 'light-place') { canvas.setPointerCapture(event.pointerId); toggleLightAt(state, event); return; }
            if (state.options.tool === 'light-cursor') { updateCursorLight(state, event); return; }
            if (state.options.tool === 'draw' || state.options.tool === 'erase') { state.painting = true; state.lastEdit = null; canvas.setPointerCapture(event.pointerId); editAt(state, event); return; }
        });
        canvas.addEventListener('pointermove', event => { rememberPointer(state, event); if (state.dragging) { state.panX += event.clientX - state.lastX; state.panY += event.clientY - state.lastY; state.lastX = event.clientX; state.lastY = event.clientY; scheduleDraw(state); return; } if (state.options.tool === 'light-cursor') { updateCursorLight(state, event); return; } if (state.painting) editAt(state, event); });
        canvas.addEventListener('pointerleave', () => { if (state.options.tool === 'light-cursor' && state.cursorLight) { state.cursorLight = null; scheduleDraw(state); } });
        canvas.addEventListener('pointerup', () => endPointerAction(state));
        canvas.addEventListener('pointercancel', () => endPointerAction(state));
        canvas.addEventListener('contextmenu', event => event.preventDefault());
        canvas.addEventListener('dragover', event => { if (window.__sprite2worldDraggingAsset || event.dataTransfer?.types?.includes?.('application/x-sprite2world-asset')) { event.preventDefault(); if (event.dataTransfer) event.dataTransfer.dropEffect = 'copy'; } });
        canvas.addEventListener('drop', event => { const assetId = window.__sprite2worldDraggingAsset || event.dataTransfer?.getData('application/x-sprite2world-asset') || event.dataTransfer?.getData('text/plain'); if (!assetId || !state.transform || !state.dotnet) return; event.preventDefault(); const asset = state.assets.get(assetId), point = placementPoint(state, event, asset); state.dotnet.invokeMethodAsync('CanvasDrop', assetId, point.x, point.y, point.offsetX, point.offsetY).catch(() => {}); window.__sprite2worldDraggingAsset = null; });
        if (!window.__sprite2worldDragInstalled) { document.addEventListener('dragstart', event => { const source = event.target?.closest?.('[data-drag-asset]'); if (!source || !event.dataTransfer) return; window.__sprite2worldDraggingAsset = source.dataset.dragAsset; event.dataTransfer.setData('application/x-sprite2world-asset', source.dataset.dragAsset); event.dataTransfer.setData('text/plain', source.dataset.dragAsset); event.dataTransfer.effectAllowed = 'copy'; }); document.addEventListener('dragend', () => window.__sprite2worldDraggingAsset = null); window.__sprite2worldDragInstalled = true; }
        canvas.addEventListener('wheel', event => { event.preventDefault(); rememberPointer(state, event); const delta = event.deltaMode === 1 ? event.deltaY * 16 : event.deltaMode === 2 ? event.deltaY * canvas.clientHeight : event.deltaY; zoomAt(state, (state.options.zoom || 1) * Math.exp(-delta * .0015), state.pointerX, state.pointerY); }, { passive: false });
        state.resizeObserver = new ResizeObserver(() => scheduleDraw(state)); state.resizeObserver.observe(canvas.parentElement);
    }
    async function render(id, world, assets, options) {
        init(id); const state = get(id); if (!state) return;
        const changed = !state.world || state.world.seed !== world.seed || state.world.width !== world.width || state.world.height !== world.height;
        const incoming = options || {}, cameraZoom = state.cameraInitialized && !changed ? state.options.zoom : clampZoom(incoming.zoom);
        state.world = world; state.options = { ...state.options, ...incoming, zoom: cameraZoom }; state.cameraInitialized = true; state.assets = new Map((assets || []).map(a => [a.id, a])); state.materialExportCache.clear();
        state.canvas.style.cursor = state.options.tool === 'light-cursor' || state.options.tool === 'light-place' ? "url('/lightbulb-cursor.svg') 8 8, crosshair" : state.options.tool === 'draw' ? 'crosshair' : state.options.tool === 'erase' ? 'cell' : 'grab';
        if (state.options.tool !== 'light-cursor') state.cursorLight = null;
        if (changed || !state.player) { state.player = { x: world.start.x, y: world.start.y }; state.won = false; state.panX = 0; state.panY = 0; state.lights = []; state.cursorLight = null; state.litCache.clear(); }
        const sources = [...new Set((assets || []).flatMap(a => [a.url, a.normalMapUrl, a.metallicMapUrl, a.roughnessMapUrl]).filter(Boolean))];
        await Promise.all(sources.map(src => loadImage(state, src)));
        draw(state); drawMini(state);
    }
    function loadImage(state, src) {
        if (state.images.has(src)) return Promise.resolve();
        return new Promise(resolve => { const image = new Image(); image.onload = () => { state.images.set(src, image); resolve(); }; image.onerror = resolve; image.src = src; });
    }
    function size(state) {
        const rect = state.canvas.parentElement.getBoundingClientRect(); const dpr = Math.min(2, window.devicePixelRatio || 1);
        const width = Math.max(320, Math.floor(rect.width)); const height = Math.max(300, Math.floor(rect.height));
        if (state.canvas.width !== width * dpr || state.canvas.height !== height * dpr) { state.canvas.width = width * dpr; state.canvas.height = height * dpr; state.canvas.style.width = width + 'px'; state.canvas.style.height = height + 'px'; }
        state.context.setTransform(dpr, 0, 0, dpr, 0, 0); return { width, height };
    }
    function scheduleDraw(state) { if (state.renderFrame) return; state.renderFrame = requestAnimationFrame(() => { state.renderFrame = null; draw(state); }); }
    function clampZoom(value) { const parsed = Number(value); return Math.max(.4, Math.min(3, Number.isFinite(parsed) ? parsed : 1)); }
    function rememberPointer(state, event) { const rect = state.canvas.getBoundingClientRect(); state.pointerX = event.clientX - rect.left; state.pointerY = event.clientY - rect.top; }
    function restoreCursor(state) { state.canvas.style.cursor = state.options.tool === 'light-cursor' || state.options.tool === 'light-place' ? "url('/lightbulb-cursor.svg') 8 8, crosshair" : state.options.tool === 'draw' ? 'crosshair' : state.options.tool === 'erase' ? 'cell' : 'grab'; }
    function endPointerAction(state) { state.dragging = false; state.painting = false; state.lastEdit = null; restoreCursor(state); }
    function notifyCamera(state) {
        if (!state.dotnet) return; if (state.cameraNotifyTimer) clearTimeout(state.cameraNotifyTimer);
        state.cameraNotifyTimer = setTimeout(() => { state.cameraNotifyTimer = null; state.dotnet?.invokeMethodAsync('WorldCameraChanged', state.options.zoom).catch(() => {}); }, 70);
    }
    function zoomAt(state, requestedZoom, anchorX, anchorY) {
        if (!state.world) return; const nextZoom = clampZoom(requestedZoom), currentZoom = clampZoom(state.options.zoom); if (Math.abs(nextZoom - currentZoom) < .0001) return;
        const { width, height } = size(state), world = state.world, fit = Math.min((width - 36) / world.width, (height - 36) / world.height), oldTile = Math.max(5, fit * currentZoom), nextTile = Math.max(5, fit * nextZoom);
        const oldOriginX = (width - world.width * oldTile) / 2 + state.panX, oldOriginY = (height - world.height * oldTile) / 2 + state.panY;
        const x = Number.isFinite(anchorX) ? anchorX : width / 2, y = Number.isFinite(anchorY) ? anchorY : height / 2;
        const worldX = (x - oldOriginX) / oldTile, worldY = (y - oldOriginY) / oldTile;
        state.panX = x - worldX * nextTile - (width - world.width * nextTile) / 2; state.panY = y - worldY * nextTile - (height - world.height * nextTile) / 2;
        state.options.zoom = nextZoom; scheduleDraw(state); notifyCamera(state);
    }
    function draw(state) {
        if (!state.world) return; const { width, height } = size(state); const ctx = state.context; const world = state.world;
        ctx.imageSmoothingEnabled = false; ctx.fillStyle = '#070910'; ctx.fillRect(0, 0, width, height);
        const fit = Math.min((width - 36) / world.width, (height - 36) / world.height); const tile = Math.max(5, fit * (state.options.zoom || 1));
        let originX = (width - world.width * tile) / 2 + state.panX; let originY = (height - world.height * tile) / 2 + state.panY;
        if (state.playing && state.player) { originX = width / 2 - (state.player.x + .5) * tile; originY = height / 2 - (state.player.y + .5) * tile; }
        state.transform = { tile, originX, originY };
        state.frameLights = state.cursorLight ? [...state.lights, state.cursorLight] : state.lights;
        for (const cell of world.tiles) {
            const x = originX + cell.x * tile, y = originY + cell.y * tile;
            if (x + tile < 0 || y + tile < 0 || x > width || y > height) continue;
            ctx.fillStyle = colors[cell.kind] || '#ff00ff'; ctx.fillRect(Math.floor(x), Math.floor(y), Math.ceil(tile), Math.ceil(tile));
            const asset = cell.assetId ? state.assets.get(cell.assetId) : null;
            if (asset && cell.kind !== 'Void') drawAsset(state, asset, cell.x, cell.y, x, y, tile);
            if (cell.kind === 'Start') marker(ctx, x, y, tile, '#4cde91', 'S');
            if (cell.kind === 'Exit') marker(ctx, x, y, tile, '#f15468', 'E');
        }
        for (const layer of [...(world.layers || [])].filter(layer => layer.visible).sort((a, b) => a.order - b.order)) {
            for (const placement of layer.placements || []) {
                const asset = state.assets.get(placement.assetId);
                const footprint = assetFootprint(state, asset), gridX = placement.x + (Number(placement.offsetX) || 0), gridY = placement.y + (Number(placement.offsetY) || 0), x = originX + gridX * tile, y = originY + gridY * tile, drawWidth = footprint.width * tile, drawHeight = footprint.height * tile;
                if (x + drawWidth < 0 || y + drawHeight < 0 || x > width || y > height) continue;
                if (asset) drawAsset(state, asset, gridX, gridY, x, y, tile, footprint.width, footprint.height);
                else { ctx.fillStyle = '#a05cff88'; ctx.fillRect(Math.floor(x), Math.floor(y), Math.ceil(tile), Math.ceil(tile)); }
                if (layer.id === state.options.activeLayerId && state.options.tool !== 'pan' && tile >= 9) { ctx.strokeStyle = 'rgba(184,159,255,.55)'; ctx.lineWidth = 1; ctx.strokeRect(Math.floor(x) + 1.5, Math.floor(y) + 1.5, Math.ceil(drawWidth) - 2, Math.ceil(drawHeight) - 2); }
            }
        }
        if (state.options.lightingEnabled) drawContinuousLightField(state, ctx, width, height, tile, originX, originY);
        // The grid is a canvas overlay: world tiles and every drawing layer stay below it.
        if (state.options.showGrid) drawInfiniteGrid(ctx, width, height, tile, originX, originY);
        if (state.options.lightingEnabled) drawLightMarkers(state, ctx, tile, originX, originY);
        if (state.player && (state.playing || state.options.playtest)) {
            const px = originX + state.player.x * tile, py = originY + state.player.y * tile;
            ctx.fillStyle = '#9b7bff'; ctx.beginPath(); ctx.arc(px + tile / 2, py + tile / 2, Math.max(4, tile * .32), 0, Math.PI * 2); ctx.fill();
            ctx.strokeStyle = '#fff'; ctx.lineWidth = Math.max(1, tile * .08); ctx.stroke();
        }
        if (state.won) { const german = state.options.language === 'de'; ctx.fillStyle = 'rgba(7,9,16,.78)'; ctx.fillRect(0, 0, width, height); ctx.textAlign = 'center'; ctx.fillStyle = '#fff'; ctx.font = '700 28px system-ui'; ctx.fillText(german ? 'AUSGANG ERREICHT' : 'EXIT REACHED', width / 2, height / 2 - 8); ctx.fillStyle = '#a99aff'; ctx.font = '14px system-ui'; ctx.fillText(german ? 'Die generierte Welt ist spielbar.' : 'The generated world is playable.', width / 2, height / 2 + 22); }
    }
    function activeLights(state) { return state.frameLights; }
    function lightingAt(state, worldX, worldY) {
        let vectorX = 0, vectorY = 0, energy = 0;
        const range = Math.max(1, Number(state.options.lightRange) || 10), intensity = Math.max(.1, Number(state.options.lightIntensity) || 1);
        for (const light of activeLights(state)) {
            const dx = light.x - worldX, dy = light.y - worldY, distance = Math.hypot(dx, dy), linear = Math.max(0, 1 - distance / range), falloff = linear * linear * (3 - 2 * linear);
            if (falloff <= 0) continue; const length = distance || 1; vectorX += dx / length * falloff; vectorY += dy / length * falloff; energy += falloff * intensity;
        }
        const length = Math.hypot(vectorX, vectorY) || 1;
        return { x: vectorX / length, y: vectorY / length, intensity: Math.min(3, energy) };
    }
    function drawAsset(state, asset, gridX, gridY, x, y, tile, widthTiles = 1, heightTiles = 1) {
        const base = state.images.get(asset.url); if (!base) return;
        let image = base;
        if (state.options.lightingEnabled && asset.normalMapUrl && state.images.has(asset.normalMapUrl)) image = litAsset(state, asset, gridX, gridY) || base;
        state.context.drawImage(image, Math.floor(x), Math.floor(y), Math.ceil(tile * widthTiles), Math.ceil(tile * heightTiles));
    }
    function litAsset(state, asset, gridX, gridY) {
        const base = state.images.get(asset.url), normal = state.images.get(asset.normalMapUrl); if (!base || !normal) return base;
        const light = lightingAt(state, gridX + .5, gridY + .5); if (light.intensity < .015) return base;
        const direction = Math.round(((Math.atan2(light.y, light.x) + Math.PI * 2) % (Math.PI * 2)) / (Math.PI * 2) * 32) % 32, energy = Math.round(Math.min(1.5, light.intensity) * 8) / 8, normalStrength = Number(asset.normalStrength) || 1, colorValue = /^#[0-9a-f]{6}$/i.test(state.options.lightColor || '') ? state.options.lightColor.toLowerCase() : '#ffd36a', key = `${asset.id}|${asset.url}|${asset.normalMapUrl}|${direction}|${energy}|${normalStrength}|${colorValue}`;
        if (state.litCache.has(key)) return state.litCache.get(key);
        const width = base.naturalWidth || asset.width || 16, height = base.naturalHeight || asset.height || 16, sourceKey = `${asset.url}|${asset.normalMapUrl}`; let source = state.normalSources.get(sourceKey);
        if (!source) { const baseSource = document.createElement('canvas'), normalSource = document.createElement('canvas'); baseSource.width = normalSource.width = width; baseSource.height = normalSource.height = height; const baseSourceCtx = baseSource.getContext('2d', { willReadFrequently: true }), normalSourceCtx = normalSource.getContext('2d', { willReadFrequently: true }); baseSourceCtx.imageSmoothingEnabled = normalSourceCtx.imageSmoothingEnabled = false; baseSourceCtx.drawImage(base,0,0,width,height); normalSourceCtx.drawImage(normal,0,0,width,height); source = { base: baseSourceCtx.getImageData(0,0,width,height), normal: normalSourceCtx.getImageData(0,0,width,height), width, height }; state.normalSources.set(sourceKey, source); }
        const baseCanvas = document.createElement('canvas'); baseCanvas.width = width; baseCanvas.height = height; const baseCtx = baseCanvas.getContext('2d'), pixels = new ImageData(new Uint8ClampedArray(source.base.data), width, height), normals = source.normal, angle = direction / 32 * Math.PI * 2, lx0 = Math.cos(angle) * .58, ly0 = Math.sin(angle) * .58, lz0 = .82, ll = Math.hypot(lx0, ly0, lz0) || 1, color = parseColor(colorValue);
        for (let i = 0; i < pixels.data.length; i += 4) {
            if (!pixels.data[i + 3]) continue; let nx = (normals.data[i] / 127.5 - 1) * normalStrength, ny = (normals.data[i + 1] / 127.5 - 1) * normalStrength, nz = normals.data[i + 2] / 127.5 - 1, nl = Math.hypot(nx, ny, nz) || 1; nx /= nl; ny /= nl; nz /= nl;
            const diffuse = Math.max(0, nx * lx0 / ll + ny * ly0 / ll + nz * lz0 / ll), shade = .82 + energy * (.08 + diffuse * .32), tint = energy * diffuse * .09;
            pixels.data[i] = Math.min(255, pixels.data[i] * shade + color.r * tint); pixels.data[i + 1] = Math.min(255, pixels.data[i + 1] * shade + color.g * tint); pixels.data[i + 2] = Math.min(255, pixels.data[i + 2] * shade + color.b * tint);
        }
        baseCtx.putImageData(pixels, 0, 0); state.litCache.set(key, baseCanvas); if (state.litCache.size > 800) state.litCache.delete(state.litCache.keys().next().value); return baseCanvas;
    }
    function updateCursorLight(state, event) {
        if (!state.transform) return; const rect = state.canvas.getBoundingClientRect(), { tile, originX, originY } = state.transform;
        state.cursorLight = { x: (event.clientX - rect.left - originX) / tile, y: (event.clientY - rect.top - originY) / tile, cursor: true }; scheduleDraw(state);
    }
    function toggleLightAt(state, event) {
        if (!state.world || !state.transform) return; const point = worldPoint(state, event); if (point.x < 0 || point.y < 0 || point.x >= state.world.width || point.y >= state.world.height) return;
        const index = state.lights.findIndex(light => Math.hypot(light.x - point.x, light.y - point.y) < .55); if (index >= 0) state.lights.splice(index, 1); else state.lights.push(point); scheduleDraw(state);
    }
    function parseColor(value) { const hex = /^#[0-9a-f]{6}$/i.test(value || '') ? value.slice(1) : 'ffd36a'; return { r: parseInt(hex.slice(0,2),16), g: parseInt(hex.slice(2,4),16), b: parseInt(hex.slice(4,6),16) }; }
    function drawContinuousLightField(state, ctx, width, height, tile, originX, originY) {
        const lights = activeLights(state), overlay = state.lightCanvas; if (overlay.width !== Math.ceil(width) || overlay.height !== Math.ceil(height)) { overlay.width = Math.ceil(width); overlay.height = Math.ceil(height); }
        const lightCtx = overlay.getContext('2d'); lightCtx.clearRect(0,0,width,height); lightCtx.fillStyle = 'rgba(4,6,11,.68)'; lightCtx.fillRect(0,0,width,height); lightCtx.globalCompositeOperation = 'destination-out';
        const range = Math.max(1, Number(state.options.lightRange) || 10), intensity = Math.max(.1, Number(state.options.lightIntensity) || 1), erase = Math.min(.94, .5 + intensity * .18);
        for (const light of lights) { const x = originX + light.x * tile, y = originY + light.y * tile, radius = range * tile, gradient = lightCtx.createRadialGradient(x,y,0,x,y,radius); gradient.addColorStop(0,`rgba(0,0,0,${erase})`); gradient.addColorStop(.55,`rgba(0,0,0,${erase * .62})`); gradient.addColorStop(1,'rgba(0,0,0,0)'); lightCtx.fillStyle = gradient; lightCtx.fillRect(x-radius,y-radius,radius*2,radius*2); }
        lightCtx.globalCompositeOperation = 'source-over'; ctx.drawImage(overlay,0,0,width,height); const color = parseColor(state.options.lightColor); ctx.save(); ctx.globalCompositeOperation = 'screen';
        for (const light of lights) { const x = originX + light.x * tile, y = originY + light.y * tile, radius = range * tile * .72, gradient = ctx.createRadialGradient(x,y,0,x,y,radius); gradient.addColorStop(0,`rgba(${color.r},${color.g},${color.b},${Math.min(.34,.1+intensity*.08)})`); gradient.addColorStop(1,`rgba(${color.r},${color.g},${color.b},0)`); ctx.fillStyle = gradient; ctx.fillRect(x-radius,y-radius,radius*2,radius*2); } ctx.restore();
    }
    function drawLightMarkers(state, ctx, tile, originX, originY) {
        for (const light of activeLights(state)) {
            const x = originX + light.x * tile, y = originY + light.y * tile, radius = Math.max(8, tile * (light.cursor ? .48 : .34));
            const glow = ctx.createRadialGradient(x, y, 0, x, y, radius * 2.8); glow.addColorStop(0, 'rgba(255,226,122,.5)'); glow.addColorStop(1, 'rgba(255,188,70,0)'); ctx.fillStyle = glow; ctx.beginPath(); ctx.arc(x, y, radius * 2.8, 0, Math.PI * 2); ctx.fill();
            ctx.fillStyle = light.cursor ? '#fff4b2' : '#ffd45f'; ctx.strokeStyle = '#5d4315'; ctx.lineWidth = 1.5; ctx.beginPath(); ctx.arc(x, y, Math.max(3, radius * .28), 0, Math.PI * 2); ctx.fill(); ctx.stroke();
        }
    }
    function drawInfiniteGrid(ctx, width, height, tile, originX, originY) {
        if (tile < 6) return; ctx.beginPath(); ctx.strokeStyle = 'rgba(151,160,190,.10)'; ctx.lineWidth = 1;
        for (let x = originX % tile; x <= width; x += tile) { ctx.moveTo(Math.floor(x) + .5, 0); ctx.lineTo(Math.floor(x) + .5, height); }
        for (let y = originY % tile; y <= height; y += tile) { ctx.moveTo(0, Math.floor(y) + .5); ctx.lineTo(width, Math.floor(y) + .5); }
        ctx.stroke();
    }
    function gridPoint(state, event) { const rect = state.canvas.getBoundingClientRect(); const { tile, originX, originY } = state.transform; return { x: Math.floor((event.clientX - rect.left - originX) / tile), y: Math.floor((event.clientY - rect.top - originY) / tile) }; }
    function worldPoint(state, event) { const rect = state.canvas.getBoundingClientRect(); const { tile, originX, originY } = state.transform; return { x: (event.clientX - rect.left - originX) / tile, y: (event.clientY - rect.top - originY) / tile }; }
    function assetFootprint(state, asset) { const unit = Math.max(1, Number(state.options.spriteTilePixels) || 48); return { width: Math.max(1, (Number(asset?.width) || unit) / unit), height: Math.max(1, (Number(asset?.height) || unit) / unit) }; }
    function assetAlwaysSnaps(asset) { return asset?.role === 'Floor' || asset?.role === 'Wall'; }
    function placementPoint(state, event, asset) {
        if (!asset || assetAlwaysSnaps(asset) || state.options.snapElementsToGrid !== false) { const point = gridPoint(state, event); return { ...point, offsetX: 0, offsetY: 0, free: false }; }
        const point = worldPoint(state, event), footprint = assetFootprint(state, asset), left = point.x - footprint.width / 2, top = point.y - footprint.height / 2, x = Math.floor(left), y = Math.floor(top);
        return { x, y, offsetX: left - x, offsetY: top - y, free: true };
    }
    function placementAt(state, layer, event) {
        const point = worldPoint(state, event);
        return [...(layer.placements || [])].reverse().find(item => { const asset = state.assets.get(item.assetId), footprint = assetFootprint(state, asset), left = item.x + (Number(item.offsetX) || 0), top = item.y + (Number(item.offsetY) || 0); return point.x >= left && point.y >= top && point.x < left + footprint.width && point.y < top + footprint.height; });
    }
    function editAt(state, event) {
        if (!state.world || !state.transform || !state.dotnet) return;
        const layer = (state.world.layers || []).find(item => item.id === state.options.activeLayerId); if (!layer || layer.locked) return;
        layer.placements = layer.placements || [];
        let point;
        if (state.options.tool === 'erase') { const hit = placementAt(state, layer, event); if (!hit) return; point = { x: hit.x, y: hit.y, offsetX: Number(hit.offsetX) || 0, offsetY: Number(hit.offsetY) || 0 }; layer.placements = layer.placements.filter(item => item !== hit); }
        else if (state.options.tool === 'draw' && state.options.brushAssetId) { const asset = state.assets.get(state.options.brushAssetId); point = placementPoint(state, event, asset); if (point.x < 0 || point.y < 0 || point.x >= state.world.width || point.y >= state.world.height) return; layer.placements = layer.placements.filter(item => item.x !== point.x || item.y !== point.y || Math.abs((Number(item.offsetX) || 0) - point.offsetX) > .0001 || Math.abs((Number(item.offsetY) || 0) - point.offsetY) > .0001); layer.placements.push({ id: `local-${point.x}-${point.y}`, assetId: state.options.brushAssetId, x: point.x, y: point.y, offsetX: point.offsetX, offsetY: point.offsetY }); if (point.free) state.painting = false; }
        else return;
        const key = `${state.options.tool}:${point.x}:${point.y}:${point.offsetX.toFixed(4)}:${point.offsetY.toFixed(4)}`; if (state.lastEdit === key) return; state.lastEdit = key;
        draw(state); state.dotnet.invokeMethodAsync('CanvasEdit', point.x, point.y, point.offsetX, point.offsetY, state.options.tool).catch(() => {});
    }
    function marker(ctx, x, y, tile, color, label) { if (tile < 10) return; ctx.fillStyle = color; ctx.beginPath(); ctx.arc(x + tile / 2, y + tile / 2, Math.max(2, tile * .16), 0, Math.PI * 2); ctx.fill(); if (tile > 22) { ctx.fillStyle = '#fff'; ctx.font = `700 ${Math.max(8, tile * .3)}px system-ui`; ctx.textAlign = 'center'; ctx.fillText(label, x + tile / 2, y + tile * .61); } }
    function key(state, event) {
        if (!state.playing || state.won) return; const directions = { ArrowUp: [0,-1], w: [0,-1], W: [0,-1], ArrowDown: [0,1], s: [0,1], S: [0,1], ArrowLeft: [-1,0], a: [-1,0], A: [-1,0], ArrowRight: [1,0], d: [1,0], D: [1,0] };
        const delta = directions[event.key]; if (!delta) return; event.preventDefault(); const nx = state.player.x + delta[0], ny = state.player.y + delta[1]; const cell = state.world.tiles.find(t => t.x === nx && t.y === ny);
        if (cell && effectivelyWalkable(state, cell)) { state.player = { x: nx, y: ny }; state.won = nx === state.world.exit.x && ny === state.world.exit.y; draw(state); drawMini(state); }
    }
    function effectivelyWalkable(state, cell) {
        const placement = [...(state.world.layers || [])].filter(layer => layer.visible).sort((a, b) => b.order - a.order).flatMap(layer => (layer.placements || []).filter(item => { const footprint = assetFootprint(state, state.assets.get(item.assetId)), left = item.x + (Number(item.offsetX) || 0), top = item.y + (Number(item.offsetY) || 0); return cell.x + .5 >= left && cell.y + .5 >= top && cell.x + .5 < left + footprint.width && cell.y + .5 < top + footprint.height; }).slice(0, 1))[0];
        if (!placement) return cell.walkable; const role = state.assets.get(placement.assetId)?.role;
        if (['Wall', 'Obstacle', 'Building', 'Water', 'Lava'].includes(role)) return false;
        if (!role || role === 'Unknown' || role === 'Unused') return cell.walkable; return true;
    }
    function drawMini(state) {
        const canvas = document.getElementById('miniCanvas'); if (!canvas || !state.world) return; canvas.width = 180; canvas.height = 118; const ctx = canvas.getContext('2d'); const sx = canvas.width / state.world.width, sy = canvas.height / state.world.height;
        ctx.fillStyle = '#070911'; ctx.fillRect(0,0,canvas.width,canvas.height); for (const cell of state.world.tiles) { if (cell.kind === 'Void') continue; ctx.fillStyle = colors[cell.kind] || '#555'; ctx.fillRect(cell.x*sx, cell.y*sy, Math.ceil(sx), Math.ceil(sy)); }
        if (state.player) { ctx.fillStyle = '#a58cff'; ctx.fillRect(state.player.x*sx-1,state.player.y*sy-1,Math.max(3,sx),Math.max(3,sy)); }
    }
    function start(id) { const s = get(id); if (!s) return; s.playing = true; s.won = false; s.canvas.focus(); draw(s); }
    function stop(id) { const s = get(id); if (!s) return; s.playing = false; draw(s); }
    function resetPlayer(id) { const s = get(id); if (!s || !s.world) return; s.player = { x: s.world.start.x, y: s.world.start.y }; s.won = false; s.canvas.focus(); draw(s); drawMini(s); }
    function zoomIn(id) { const s = get(id); if (!s) return; zoomAt(s, (s.options.zoom || 1) * 1.25, s.pointerX, s.pointerY); }
    function zoomOut(id) { const s = get(id); if (!s) return; zoomAt(s, (s.options.zoom || 1) / 1.25, s.pointerX, s.pointerY); }
    function fit(id) { const s = get(id); if (!s) return; s.panX = 0; s.panY = 0; s.options.zoom = 1; draw(s); notifyCamera(s); }
    function clearLights(id) { const s = get(id); if (!s) return; s.lights = []; s.cursorLight = null; s.litCache.clear(); draw(s); }
    function downloadText(name, text, type) { const blob = new Blob([text], {type}); download(name, URL.createObjectURL(blob)); }
    function downloadBase64(name, base64, type) { const bytes = Uint8Array.from(atob(base64), c => c.charCodeAt(0)); const blob = new Blob([bytes], {type}); download(name, URL.createObjectURL(blob)); }
    const crcTable = (() => { const table = new Uint32Array(256); for (let n = 0; n < 256; n++) { let c = n; for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1; table[n] = c >>> 0; } return table; })();
    function crc32(bytes) { let crc = 0xffffffff; for (const value of bytes) crc = crcTable[(crc ^ value) & 0xff] ^ (crc >>> 8); return (crc ^ 0xffffffff) >>> 0; }
    function zipArchive(entries) {
        const encoder = new TextEncoder(), locals = [], centrals = []; let offset = 0;
        const write16 = (view, at, value) => view.setUint16(at, value, true), write32 = (view, at, value) => view.setUint32(at, value >>> 0, true);
        for (const entry of entries) {
            const name = encoder.encode(entry.name), data = entry.bytes, crc = crc32(data), local = new Uint8Array(30 + name.length + data.length), lv = new DataView(local.buffer);
            write32(lv, 0, 0x04034b50); write16(lv, 4, 20); write16(lv, 6, 0); write16(lv, 8, 0); write16(lv, 10, 0); write16(lv, 12, 0); write32(lv, 14, crc); write32(lv, 18, data.length); write32(lv, 22, data.length); write16(lv, 26, name.length); write16(lv, 28, 0); local.set(name, 30); local.set(data, 30 + name.length); locals.push(local);
            const central = new Uint8Array(46 + name.length), cv = new DataView(central.buffer); write32(cv, 0, 0x02014b50); write16(cv, 4, 20); write16(cv, 6, 20); write16(cv, 8, 0); write16(cv, 10, 0); write16(cv, 12, 0); write16(cv, 14, 0); write32(cv, 16, crc); write32(cv, 20, data.length); write32(cv, 24, data.length); write16(cv, 28, name.length); write16(cv, 30, 0); write16(cv, 32, 0); write16(cv, 34, 0); write16(cv, 36, 0); write32(cv, 38, 0); write32(cv, 42, offset); central.set(name, 46); centrals.push(central); offset += local.length;
        }
        const centralSize = centrals.reduce((sum, value) => sum + value.length, 0), end = new Uint8Array(22), ev = new DataView(end.buffer); write32(ev, 0, 0x06054b50); write16(ev, 4, 0); write16(ev, 6, 0); write16(ev, 8, entries.length); write16(ev, 10, entries.length); write32(ev, 12, centralSize); write32(ev, 16, offset); write16(ev, 20, 0);
        const output = new Uint8Array(offset + centralSize + end.length); let cursor = 0; for (const part of [...locals, ...centrals, end]) { output.set(part, cursor); cursor += part.length; } return output;
    }
    function downloadZipBase64(name, entries) { const files = (entries || []).filter(entry => entry?.base64 || entry?.Base64).map(entry => ({ name: entry.name || entry.Name, bytes: Uint8Array.from(atob(entry.base64 || entry.Base64), c => c.charCodeAt(0)) })); if (!files.length) return; const blob = new Blob([zipArchive(files)], { type: 'application/zip' }); download(name, URL.createObjectURL(blob)); }
    function safeExportName(value) { return String(value || 'layer').normalize('NFKD').replace(/[^a-zA-Z0-9]+/g, '-').replace(/^-|-$/g, '').toLowerCase() || 'layer'; }
    function materialSource(state, asset, channel) {
        if (!asset) return null; const directUrl = channel === 'base' ? asset.url : channel === 'normal' ? asset.normalMapUrl : channel === 'metallic' ? asset.metallicMapUrl : asset.roughnessMapUrl;
        if (directUrl && state.images.has(directUrl)) return state.images.get(directUrl); const base = state.images.get(asset.url); if (!base) return null;
        const value = channel === 'normal' ? '#8080ff' : channel === 'metallic' ? Math.round(Math.max(0, Math.min(1, Number(asset.metallicStrength) || 0)) * 255) : Math.round(Math.max(0, Math.min(1, Number(asset.roughnessStrength) || .5)) * 255), key = `${asset.id}|${asset.url}|${channel}|${value}`;
        if (state.materialExportCache.has(key)) return state.materialExportCache.get(key); const canvas = document.createElement('canvas'); canvas.width = base.naturalWidth || asset.width || 48; canvas.height = base.naturalHeight || asset.height || 48; const ctx = canvas.getContext('2d'); ctx.drawImage(base, 0, 0, canvas.width, canvas.height); ctx.globalCompositeOperation = 'source-in'; ctx.fillStyle = typeof value === 'string' ? value : `rgb(${value},${value},${value})`; ctx.fillRect(0, 0, canvas.width, canvas.height); ctx.globalCompositeOperation = 'source-over'; state.materialExportCache.set(key, canvas); return canvas;
    }
    function fillMaterialCell(ctx, channel, kind, x, y, tile) { if (kind === 'Void' && channel !== 'base') return; ctx.fillStyle = channel === 'base' ? (colors[kind] || '#ff00ff') : channel === 'normal' ? '#8080ff' : channel === 'roughness' ? '#808080' : '#000'; ctx.fillRect(x, y, tile, tile); }
    function drawMaterialAsset(state, ctx, asset, channel, x, y, width, height) { const source = materialSource(state, asset, channel); if (source) { ctx.imageSmoothingEnabled = false; ctx.drawImage(source, Math.round(x), Math.round(y), Math.round(width), Math.round(height)); } }
    function worldExportLayers(state) { return [{ type: 'base', name: 'generated-base' }, ...[...(state.world.layers || [])].sort((a, b) => a.order - b.order).map(layer => ({ type: 'layer', name: layer.name || `layer-${layer.order + 1}`, layer }))]; }
    function renderWorldMaterialCanvas(state, channel, descriptor = null) {
        const tile = Math.max(1, Number(state.options.spriteTilePixels) || 48), canvas = document.createElement('canvas'); canvas.width = state.world.width * tile; canvas.height = state.world.height * tile; const ctx = canvas.getContext('2d'); ctx.clearRect(0, 0, canvas.width, canvas.height);
        const drawBase = () => { for (const cell of state.world.tiles || []) { const x = cell.x * tile, y = cell.y * tile, asset = cell.assetId ? state.assets.get(cell.assetId) : null; if (asset && cell.kind !== 'Void') drawMaterialAsset(state, ctx, asset, channel, x, y, tile, tile); else fillMaterialCell(ctx, channel, cell.kind, x, y, tile); } };
        const drawLayer = layer => { for (const placement of layer.placements || []) { const asset = state.assets.get(placement.assetId); if (!asset) continue; const footprint = assetFootprint(state, asset), x = (placement.x + (Number(placement.offsetX) || 0)) * tile, y = (placement.y + (Number(placement.offsetY) || 0)) * tile; drawMaterialAsset(state, ctx, asset, channel, x, y, footprint.width * tile, footprint.height * tile); } };
        if (!descriptor) { drawBase(); for (const layer of [...(state.world.layers || [])].filter(layer => layer.visible).sort((a, b) => a.order - b.order)) drawLayer(layer); }
        else if (descriptor.type === 'base') drawBase(); else drawLayer(descriptor.layer);
        return canvas;
    }
    function previewBase64(id, maxDimension = 1600) {
        const state = get(id); if (!state?.world) return null;
        const world = state.world, limit = Math.max(256, Math.min(1600, Number(maxDimension) || 1600)), naturalTile = Math.max(1, Number(state.options.spriteTilePixels) || 48), tile = Math.min(naturalTile, limit / Math.max(1, world.width), limit / Math.max(1, world.height));
        const canvas = document.createElement('canvas'); canvas.width = Math.max(1, Math.ceil(world.width * tile)); canvas.height = Math.max(1, Math.ceil(world.height * tile));
        const ctx = canvas.getContext('2d'); ctx.imageSmoothingEnabled = false; ctx.fillStyle = colors.Void; ctx.fillRect(0, 0, canvas.width, canvas.height);
        const rect = (x, y, w = 1, h = 1) => ({ x: Math.floor(x * tile), y: Math.floor(y * tile), width: Math.max(1, Math.ceil((x + w) * tile) - Math.floor(x * tile)), height: Math.max(1, Math.ceil((y + h) * tile) - Math.floor(y * tile)) });
        for (const cell of world.tiles || []) {
            const bounds = rect(cell.x, cell.y), asset = cell.assetId ? state.assets.get(cell.assetId) : null;
            ctx.fillStyle = colors[cell.kind] || '#ff00ff'; ctx.fillRect(bounds.x, bounds.y, bounds.width, bounds.height);
            if (asset && cell.kind !== 'Void') drawMaterialAsset(state, ctx, asset, 'base', bounds.x, bounds.y, bounds.width, bounds.height);
        }
        for (const layer of [...(world.layers || [])].filter(layer => layer.visible).sort((a, b) => a.order - b.order)) {
            for (const placement of layer.placements || []) {
                const asset = state.assets.get(placement.assetId); if (!asset) continue;
                const footprint = assetFootprint(state, asset), x = placement.x + (Number(placement.offsetX) || 0), y = placement.y + (Number(placement.offsetY) || 0), bounds = rect(x, y, footprint.width, footprint.height);
                drawMaterialAsset(state, ctx, asset, 'base', bounds.x, bounds.y, bounds.width, bounds.height);
            }
        }
        return canvasBase64(canvas);
    }
    function canvasBase64(canvas) { return canvas.toDataURL('image/png').split(',')[1]; }
    function worldMaterialEntries(state, channels, separateLayers, stem) { const descriptors = separateLayers ? worldExportLayers(state) : [null], entries = [], project = safeExportName(stem); for (const descriptor of descriptors) for (const channel of channels) { const prefix = descriptor ? `${project}-${String(descriptors.indexOf(descriptor) + 1).padStart(2, '0')}-${safeExportName(descriptor.name)}` : project; entries.push({ name: `${prefix}-${channel}.png`, base64: canvasBase64(renderWorldMaterialCanvas(state, channel, descriptor)) }); } return entries; }
    function exportWorldMaterial(id, channel, separateLayers, stem) { const state = get(id); if (!state?.world) return; const entries = worldMaterialEntries(state, [String(channel).toLowerCase()], !!separateLayers, stem); if (entries.length === 1) downloadBase64(entries[0].name, entries[0].base64, 'image/png'); else downloadZipBase64(`${safeExportName(stem)}-${String(channel).toLowerCase()}-layers.zip`, entries); }
    function exportWorldMaterialPackage(id, separateLayers, stem) { const state = get(id); if (!state?.world) return; const entries = worldMaterialEntries(state, ['base', 'normal', 'roughness', 'metallic'], !!separateLayers, stem); downloadZipBase64(`${safeExportName(stem)}-materials${separateLayers ? '-layers' : ''}.zip`, entries); }
    function download(name, url) { const anchor = document.createElement('a'); anchor.href = url; anchor.download = name; anchor.click(); setTimeout(() => URL.revokeObjectURL(url), 1000); }
    function getPreference(key) { try { return localStorage.getItem(`sprite2world.${key}`); } catch { return null; } }
    function setPreference(key, value) { try { localStorage.setItem(`sprite2world.${key}`, value); if (key === 'language') document.documentElement.lang = value; } catch {} }
    return { init, render, start, stop, resetPlayer, zoomIn, zoomOut, fit, clearLights, previewBase64, downloadText, downloadBase64, downloadZipBase64, exportWorldMaterial, exportWorldMaterialPackage, getPreference, setPreference };
})();
