(() => {
    const selector = 'button:not([disabled]),a[href],input:not([disabled]),select:not([disabled]),textarea:not([disabled]),[tabindex]:not([tabindex="-1"])';
    let activeDialog = null;
    let returnFocus = null;

    function visibleDialog() {
        const dialogs = Array.from(document.querySelectorAll('[role="dialog"][aria-modal="true"]'));
        return dialogs.reverse().find(dialog => dialog.getClientRects().length > 0) || null;
    }
    function syncDialog() {
        const next = visibleDialog();
        if (next === activeDialog) return;
        if (!next && returnFocus instanceof HTMLElement) returnFocus.focus({ preventScroll: true });
        if (next) {
            returnFocus = document.activeElement instanceof HTMLElement ? document.activeElement : null;
            requestAnimationFrame(() => (next.querySelector('[autofocus]') || next.querySelector(selector) || next).focus({ preventScroll: true }));
        }
        activeDialog = next;
    }
    document.addEventListener('keydown', event => {
        const dialog = visibleDialog(); if (!dialog) return;
        if (event.key === 'Escape') {
            const close = dialog.querySelector('button[aria-label*="Close"],button[aria-label*="Schließ"]');
            if (close) { event.preventDefault(); close.click(); }
            return;
        }
        if (event.key !== 'Tab') return;
        const focusable = Array.from(dialog.querySelectorAll(selector)).filter(element => element.getClientRects().length > 0);
        if (focusable.length === 0) { event.preventDefault(); dialog.focus(); return; }
        const first = focusable[0], last = focusable[focusable.length - 1];
        if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last.focus(); }
        else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first.focus(); }
    });
    document.addEventListener('pointerdown', event => {
        const target = event.target;
        if (!(target instanceof Element)) return;

        // Close the topmost modal even when the click lands on the permanently
        // visible navigation outside the modal backdrop.
        const outsideDialogs = Array.from(document.querySelectorAll('[role="dialog"][aria-modal="true"], [role="dialog"][data-close-on-outside="true"]'))
            .filter(dialog => dialog.getClientRects().length > 0);
        const dialog = outsideDialogs.at(-1);
        if (dialog && !dialog.contains(target)) {
            const close = dialog.querySelector('button[aria-label*="Close"],button[aria-label*="Schließ"]');
            close?.click();
        }

        // Popovers do not have a backdrop. Each open popover registers its
        // trigger, panel and a hidden Blazor close action under the same key.
        for (const closer of document.querySelectorAll('[data-click-away-close]')) {
            const key = closer.getAttribute('data-click-away-close');
            const escapedKey = CSS.escape(key || '');
            if (target.closest(`[data-click-away-trigger="${escapedKey}"], [data-click-away-panel="${escapedKey}"]`)) continue;
            closer.click();
        }
    });
    new MutationObserver(syncDialog).observe(document.documentElement, { childList: true, subtree: true });
    document.addEventListener('focusin', syncDialog);
})();
