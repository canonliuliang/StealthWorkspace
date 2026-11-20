(function openImageViewer(startSource, animateEnabled, saveTicket) {
    'use strict';
    const rootId = 'stealth-image-viewer-7b54c4';
    const existing = document.getElementById(rootId);
    if (existing) { existing.focus(); return { status: 'already-open' }; }
    if (!document.body || typeof HTMLDialogElement === 'undefined') return { status: 'unavailable' };

    const seen = new Set();
    const pictures = [];
    const resolveImageSource = image => {
        const current = image.currentSrc || image.getAttribute('src') || '';
        // Lazy galleries commonly keep a 1px placeholder while the real URL is
        // in a data attribute. Resolve only a declared source, and load only the
        // image selected by the reader. Leave every page element untouched.
        const placeholder = !current || image.naturalWidth <= 2 && image.naturalHeight <= 2;
        const candidates = placeholder
            ? ['data-src', 'data-original', 'data-lazy-src'].map(name => image.getAttribute(name)).concat(current)
            : [current];
        for (const candidate of candidates) {
            if (!candidate) continue;
            try {
                const url = new URL(candidate, document.baseURI);
                if (/^(https?:|blob:|data:image\/)/i.test(url.href)) return url.href;
            } catch { }
        }
        return '';
    };
    for (const image of document.images) {
        const source = resolveImageSource(image);
        if (!source || seen.has(source)) continue;
        const box = image.getBoundingClientRect();
        const substantial = image.naturalWidth >= 80 && image.naturalHeight >= 80 || box.width >= 120 && box.height >= 120 ||
            Number(image.getAttribute('width')) >= 120 && Number(image.getAttribute('height')) >= 120;
        if (source !== startSource && image.currentSrc !== startSource && !substantial) continue;
        seen.add(source);
        const visibleArea = Math.max(0, Math.min(innerWidth, box.right) - Math.max(0, box.left)) *
            Math.max(0, Math.min(innerHeight, box.bottom) - Math.max(0, box.top));
        pictures.push({ element: image, source, title: image.alt || image.title || '', visibleArea,
            requested: source === startSource || image.currentSrc === startSource });
        if (pictures.length >= 2000) break;
    }
    if (!pictures.length) return { status: 'empty' };

    const previousFocus = document.activeElement;
    const pageStyle = document.documentElement.style;
    const scrollPosition = { x: window.scrollX, y: window.scrollY };
    const originalOverflow = ['overflow-x', 'overflow-y'].map(name => ({ name, value: pageStyle.getPropertyValue(name), priority: pageStyle.getPropertyPriority(name) }));
    const restorePageScroll = () => {
        for (const item of originalOverflow) {
            if (pageStyle.getPropertyValue(item.name) !== 'hidden' || pageStyle.getPropertyPriority(item.name) !== 'important') continue;
            if (item.value) pageStyle.setProperty(item.name, item.value, item.priority); else pageStyle.removeProperty(item.name);
        }
        window.scrollTo(scrollPosition.x, scrollPosition.y);
    };
    const lifetime = new AbortController();
    const motion = animateEnabled !== false && !matchMedia('(prefers-reduced-motion: reduce)').matches;
    const animate = (node, frames, duration) => {
        if (!motion || typeof node.animate !== 'function') return;
        node.getAnimations().forEach(animation => animation.cancel());
        node.animate(frames, { duration, easing: 'cubic-bezier(.16,1,.3,1)' });
    };
    const listen = (element, name, action, options = {}) => element.addEventListener(name, action, { ...options, signal: lifetime.signal });
    const create = (tag, css, parent) => {
        const node = document.createElement(tag);
        node.style.cssText = css;
        if (parent) parent.append(node);
        return node;
    };
    const dialog = create('dialog', 'all:initial;display:block;position:fixed;inset:0;margin:0;width:100vw;height:100dvh;max-width:none;max-height:none;box-sizing:border-box;border:0;padding:0;color:#f3f3f3;background:#181818;font:14px "Segoe UI",system-ui,sans-serif;color-scheme:dark;overflow:hidden;');
    dialog.id = rootId;
    dialog.tabIndex = -1;
    dialog.setAttribute('aria-label', '网页图片阅读器');
    const shadowHost = create('div', 'display:block;width:100%;height:100%;', dialog);
    const shadow = shadowHost.attachShadow({ mode: 'open' });
    const shell = create('div', 'height:100%;display:grid;grid-template-rows:auto minmax(0,1fr) auto;box-sizing:border-box;', shadow);
    const bar = create('div', 'display:flex;align-items:center;flex-wrap:wrap;gap:6px;padding:8px 12px;border-bottom:1px solid #383838;background:#202020;box-sizing:border-box;', shell);
    const title = create('div', 'flex:1 1 130px;min-width:0;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;font-weight:600;padding:0 8px;', bar);
    title.textContent = '图片阅读';
    const button = (text, label, action, parent = bar) => {
        const node = create('button', 'font:inherit;line-height:20px;color:inherit;background:transparent;border:1px solid transparent;border-radius:6px;min-width:36px;height:36px;padding:6px 10px;cursor:pointer;flex-shrink:0;box-sizing:border-box;outline:none;', parent);
        node.type = 'button'; node.textContent = text; node.title = label; node.setAttribute('aria-label', label);
        listen(node, 'click', action);
        listen(node, 'pointerenter', () => { if (!node.disabled) node.style.background = '#353535'; });
        listen(node, 'pointerleave', () => node.style.background = 'transparent');
        listen(node, 'focus', () => node.style.borderColor = '#8dc9ff');
        listen(node, 'blur', () => node.style.borderColor = 'transparent');
        return node;
    };
    const viewport = create('div', 'position:relative;min-height:0;overflow:auto;overscroll-behavior:contain;scrollbar-gutter:stable both-edges;outline:none;', shell);
    viewport.tabIndex = 0;
    viewport.setAttribute('aria-label', '图片内容；左右键切换，滚动查看长图');
    const canvas = create('div', 'display:flex;min-height:100%;min-width:100%;width:max-content;align-items:flex-start;justify-content:center;box-sizing:border-box;padding:16px;', viewport);
    const footer = create('div', 'display:flex;align-items:center;justify-content:center;gap:12px;padding:6px 12px;min-height:34px;border-top:1px solid #383838;background:#202020;box-sizing:border-box;', shell);
    const previous = button('‹', '上一张（左方向键）', () => move(-1), footer);
    previous.style.fontSize = '26px';
    const counter = create('span', 'min-width:75px;text-align:center;font-variant-numeric:tabular-nums;', footer);
    counter.setAttribute('aria-live', 'polite');
    const next = button('›', '下一张（右方向键）', () => move(1), footer);
    next.style.fontSize = '26px';
    const status = create('span', 'max-width:45vw;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;color:#b8b8b8;font-size:12px;', footer);
    status.setAttribute('role', 'status');

    let index = pictures.findIndex(picture => picture.requested);
    if (index < 0) index = pictures.reduce((best, picture, at) => picture.visibleArea > pictures[best].visibleArea ? at : best, 0);
    let mode = 'fit', chosenMode = null, scale = 1, currentImage = null, displayedImage = null, disposed = false, drag = null;
    const fit = button('适合窗口', '适合窗口（0）', () => setMode('fit'));
    const width = button('按宽度', '按窗口宽度阅读长图（W）', () => setMode('width'));
    button('−', '缩小（−）', () => zoom(1 / 1.25));
    const zoomLabel = button('100%', '原始尺寸（1）', () => { chosenMode = mode = 'manual'; scale = 1; layout(); });
    zoomLabel.style.minWidth = '64px';
    button('+', '放大（+）', () => zoom(1.25));
    const save = button('加密保存', '加密保存到媒体库', event => {
        if (event.isTrusted && displayedImage === currentImage && saveTicket) window.chrome.webview.postMessage(saveTicket);
    });
    const close = button('×', '关闭阅读器（Esc）', closeViewer);
    close.style.fontSize = '24px';

    function enable(node, enabled) { node.disabled = !enabled; node.style.opacity = enabled ? '1' : '.3'; node.style.cursor = enabled ? 'pointer' : 'default'; }
    function layout() {
        if (!currentImage || !currentImage.naturalWidth || disposed) return;
        const availableWidth = Math.max(1, viewport.clientWidth - 32), availableHeight = Math.max(1, viewport.clientHeight - 32);
        if (mode === 'fit') scale = Math.min(1, availableWidth / currentImage.naturalWidth, availableHeight / currentImage.naturalHeight);
        else if (mode === 'width') scale = availableWidth / currentImage.naturalWidth;
        if (mode === 'manual') scale = Math.max(.05, Math.min(8, scale));
        currentImage.style.width = Math.round(currentImage.naturalWidth * scale) + 'px';
        currentImage.style.height = Math.round(currentImage.naturalHeight * scale) + 'px';
        canvas.style.alignItems = currentImage.naturalHeight * scale <= availableHeight ? 'center' : 'flex-start';
        currentImage.style.cursor = currentImage.naturalWidth * scale > availableWidth || currentImage.naturalHeight * scale > availableHeight ? 'grab' : 'default';
        zoomLabel.textContent = Math.round(scale * 100) + '%';
        fit.setAttribute('aria-pressed', String(mode === 'fit')); width.setAttribute('aria-pressed', String(mode === 'width'));
        fit.style.color = mode === 'fit' ? '#8dc9ff' : '#f3f3f3'; width.style.color = mode === 'width' ? '#8dc9ff' : '#f3f3f3';
    }
    function setMode(value) { chosenMode = mode = value; layout(); viewport.scrollTo(0, 0); }
    function zoom(multiplier) {
        const oldScale = scale;
        const centerX = viewport.scrollLeft + viewport.clientWidth / 2;
        const centerY = viewport.scrollTop + viewport.clientHeight / 2;
        chosenMode = mode = 'manual'; scale = Math.max(.05, Math.min(8, scale * multiplier)); layout();
        viewport.scrollTo(centerX * scale / oldScale - viewport.clientWidth / 2, centerY * scale / oldScale - viewport.clientHeight / 2);
    }
    function render() {
        if (currentImage && currentImage !== displayedImage) releaseImage(currentImage);
        // Keep the previous decoded frame until the new one is ready. There are
        // at most two reader images, and no speculative network prefetching.
        if (displayedImage) displayedImage.style.opacity = '.35';
        else canvas.replaceChildren();
        const picture = pictures[index];
        const image = create('img', 'display:block;flex:none;max-width:none;max-height:none;object-fit:contain;user-select:none;');
        currentImage = image; image.alt = picture.title || ('图片 ' + (index + 1)); image.draggable = false;
        image.decoding = 'async'; image.loading = 'eager'; image.referrerPolicy = picture.element.referrerPolicy;
        if (picture.element.crossOrigin !== null) image.crossOrigin = picture.element.crossOrigin;
        let decoding = false;
        image.onload = async () => {
            if (image !== currentImage || disposed || decoding) return;
            decoding = true;
            try { await image.decode(); } catch { /* A loaded image may still be drawn. */ }
            if (image !== currentImage || disposed) return;
            if (displayedImage && displayedImage !== image) releaseImage(displayedImage);
            displayedImage = image; canvas.replaceChildren(image);
            enable(save, true);
            status.textContent = image.naturalWidth + ' × ' + image.naturalHeight;
            // Long strips should be readable on first open, not a miniature
            // column. An explicit user choice remains in force while switching.
            if (!chosenMode) mode = image.naturalHeight / image.naturalWidth >= 2 ? 'width' : 'fit';
            layout(); viewport.scrollTo(0, 0);
            animate(image, [{ opacity: .75 }, { opacity: 1 }], 100);
        };
        image.onerror = () => {
            if (image !== currentImage || disposed) return;
            if (displayedImage) releaseImage(displayedImage);
            displayedImage = null; canvas.replaceChildren();
            canvas.style.alignItems = 'center';
            const message = create('div', 'display:flex;flex-direction:column;align-items:center;gap:14px;padding:32px;text-align:center;', canvas);
            create('span', 'color:#b8b8b8;', message).textContent = '此图片暂时无法显示';
            const retry = button('重试', '重新加载此图片', render, message);
            retry.style.borderColor = '#555';
            status.textContent = '图片加载失败，可重试或切换其他图片';
        };
        status.textContent = '正在加载第 ' + (index + 1) + ' 张图片…';
        enable(save, false);
        counter.textContent = (index + 1) + ' / ' + pictures.length;
        title.textContent = picture.title || '图片阅读'; title.title = picture.title;
        enable(previous, index > 0); enable(next, index < pictures.length - 1);
        image.src = resolveImageSource(picture.element) || picture.source;
        viewport.scrollTo(0, 0);
        if (image.complete && image.naturalWidth) image.onload();
    }
    function releaseImage(image) { image.onload = image.onerror = null; image.getAnimations().forEach(animation => animation.cancel()); image.removeAttribute('src'); }
    function move(offset) { const target = Math.max(0, Math.min(pictures.length - 1, index + offset)); if (target !== index) { index = target; render(); } }
    function closeViewer() {
        if (disposed) return; disposed = true;
        lifetime.abort(); resize.disconnect();
        if (currentImage) releaseImage(currentImage);
        if (displayedImage && displayedImage !== currentImage) releaseImage(displayedImage);
        dialog.close(); dialog.remove(); restorePageScroll();
        if (previousFocus?.isConnected && typeof previousFocus.focus === 'function') previousFocus.focus({ preventScroll: true });
    }
    listen(dialog, 'cancel', event => { event.preventDefault(); closeViewer(); });
    listen(dialog, 'keydown', event => {
        if (event.ctrlKey || event.altKey || event.metaKey) return;
        let handled = true;
        switch (event.key) {
            case 'ArrowLeft': move(-1); break;
            case 'ArrowRight': move(1); break;
            case 'Home': index = 0; render(); break;
            case 'End': index = pictures.length - 1; render(); break;
            case '+': case '=': zoom(1.25); break;
            case '-': zoom(1 / 1.25); break;
            case '0': setMode('fit'); break;
            case '1': chosenMode = mode = 'manual'; scale = 1; layout(); break;
            case 'w': case 'W': setMode('width'); break;
            case 'Escape': closeViewer(); break;
            case 'ArrowDown': viewport.scrollBy(0, 48); break;
            case 'ArrowUp': viewport.scrollBy(0, -48); break;
            case 'PageDown': viewport.scrollBy(0, viewport.clientHeight * .9); break;
            case 'PageUp': viewport.scrollBy(0, -viewport.clientHeight * .9); break;
            case ' ':
                if (event.composedPath()[0] instanceof HTMLButtonElement) handled = false;
                else viewport.scrollBy(0, viewport.clientHeight * .9 * (event.shiftKey ? -1 : 1));
                break;
            default: handled = false;
        }
        if (handled) { event.preventDefault(); event.stopImmediatePropagation(); }
    }, { capture: true });
    listen(viewport, 'dblclick', () => { if (mode === 'fit') { chosenMode = mode = 'manual'; scale = 1; layout(); } else setMode('fit'); });
    listen(viewport, 'wheel', event => { if (!event.ctrlKey) return; event.preventDefault(); zoom(event.deltaY < 0 ? 1.1 : 1 / 1.1); }, { passive: false });
    listen(viewport, 'pointerdown', event => {
        if (event.button !== 0 || event.pointerType !== 'mouse' || event.target !== displayedImage ||
            viewport.scrollWidth <= viewport.clientWidth && viewport.scrollHeight <= viewport.clientHeight) return;
        drag = { id: event.pointerId, x: event.clientX, y: event.clientY, left: viewport.scrollLeft, top: viewport.scrollTop };
        viewport.setPointerCapture(event.pointerId); displayedImage.style.cursor = 'grabbing';
        viewport.focus({ preventScroll: true });
    });
    listen(viewport, 'pointermove', event => {
        if (!drag || event.pointerId !== drag.id) return;
        viewport.scrollTo(drag.left + drag.x - event.clientX, drag.top + drag.y - event.clientY);
        event.preventDefault();
    });
    const stopDrag = () => { drag = null; layout(); };
    listen(viewport, 'lostpointercapture', stopDrag);
    listen(viewport, 'pointerup', event => { if (drag?.id === event.pointerId) { viewport.releasePointerCapture(event.pointerId); stopDrag(); } });
    const resize = new ResizeObserver(layout);
    pageStyle.setProperty('overflow', 'hidden', 'important');
    document.body.append(dialog);
    try { dialog.showModal(); } catch { lifetime.abort(); dialog.remove(); restorePageScroll(); return { status: 'unavailable' }; }
    resize.observe(viewport); render(); viewport.focus({ preventScroll: true });
    animate(shadowHost, [{ opacity: .7, transform: 'translateY(4px)' }, { opacity: 1, transform: 'none' }], 140);
    return { status: 'opened', count: pictures.length, index };
})
