"use strict";

// Read-only polling: never reload a page, submit an action, or replace editable forms.
(() => {
    const host = document.querySelector("[data-live-url]");
    if (!host) return;
    const indicator = host.querySelector("[data-live-status]");
    const url = new URL(host.dataset.liveUrl, location.href);
    if (url.origin !== location.origin) return;
    let timer, controller, running = false, leaving = false, expired = false, failures = 0;
    const status = text => { if (indicator.textContent !== text) indicator.textContent = text; };

    const updateClients = (current, incoming) => {
        const oldRows = new Map([...current.children].map(row => [row.dataset.clientKey, row]));
        for (const next of incoming.children) {
            const key = next.dataset.clientKey;
            const row = oldRows.get(key);
            if (!row) { current.append(document.importNode(next, true)); continue; }
            oldRows.delete(key);
            row.dataset.searchText = next.dataset.searchText;
            for (const cell of row.querySelectorAll("[data-live-cell]")) {
                const replacement = next.querySelector(`[data-live-cell="${cell.dataset.liveCell}"]`);
                if (replacement && replacement.innerHTML !== cell.innerHTML) cell.replaceChildren(...replacement.cloneNode(true).childNodes);
            }
            // Retain existing forms, tokens, checkbox selection and keyboard focus.
            for (const button of row.querySelectorAll("[data-live-button]")) {
                const replacement = next.querySelector(`[data-live-button="${button.dataset.liveButton}"]`);
                if (replacement) { button.disabled = replacement.disabled; button.textContent = replacement.textContent; }
            }
            for (const input of row.querySelectorAll("[data-live-value]")) {
                const replacement = next.querySelector(`[data-live-value="${input.dataset.liveValue}"]`);
                if (replacement) input.value = replacement.value;
            }
        }
        for (const row of oldRows.values()) row.remove();
        document.dispatchEvent(new Event("clients-updated"));
    };

    const apply = html => {
        const doc = new DOMParser().parseFromString(html, "text/html");
        const regions = [...host.querySelectorAll("[data-live-region]")];
        const incoming = new Map([...doc.querySelectorAll("[data-live-region]")].map(region => [region.dataset.liveRegion, region]));
        if (!regions.length || regions.some(region => !incoming.has(region.dataset.liveRegion))) throw new Error("Missing live regions");
        for (const region of regions) {
            const next = incoming.get(region.dataset.liveRegion);
            if (region.hasAttribute("data-live-clients")) { updateClients(region, next); continue; }
            if (region.innerHTML === next.innerHTML) continue;
            // Keep scroll positions when reading logs or horizontally scrolling a job table.
            const scrolls = [region, ...region.querySelectorAll(".table-scroll")].map(el => [el.scrollTop, el.scrollLeft]);
            if (region.contains(document.activeElement)) continue;
            region.replaceChildren(...next.cloneNode(true).childNodes);
            [region, ...region.querySelectorAll(".table-scroll")].forEach((el, index) => {
                if (scrolls[index]) { el.scrollTop = scrolls[index][0]; el.scrollLeft = scrolls[index][1]; }
            });
        }
    };

    const poll = async () => {
        clearTimeout(timer);
        if (running || leaving || expired || document.hidden) return;
        running = true;
        controller = new AbortController();
        const deadline = setTimeout(() => controller.abort(), 10000);
        try {
            const response = await fetch(url, { credentials: "same-origin", cache: "no-store", redirect: "manual", signal: controller.signal });
            if (response.type === "opaqueredirect" || response.status === 401 || response.status === 403) {
                expired = true;
                status("Session expired. Reload the page to sign in again.");
                return;
            }
            if (!response.ok || response.headers.get("X-LinkSafe-Live") !== "1") throw new Error("Live update unavailable");
            const html = await response.text();
            if (leaving || document.hidden) return;
            apply(html);
            failures = 0;
            status("Live - updates every 5 seconds");
            indicator.title = `Last checked ${new Date().toLocaleTimeString()}`;
        } catch {
            if (!leaving && !document.hidden) {
                failures++;
                status("Connection lost - showing last received data. Retrying...");
            }
        } finally {
            clearTimeout(deadline);
            running = false;
            if (!leaving && !expired && !document.hidden) timer = setTimeout(poll, Math.min(30000, 5000 * (failures + 1)));
        }
    };
    document.addEventListener("visibilitychange", () => {
        clearTimeout(timer);
        if (document.hidden) { controller?.abort(); status("Live updates paused while this tab is hidden"); }
        else if (running) timer = setTimeout(poll, 100);
        else poll();
    });
    document.addEventListener("submit", event => {
        if (!event.defaultPrevented) { leaving = true; clearTimeout(timer); controller?.abort(); }
    });
    window.addEventListener("pagehide", () => { leaving = true; clearTimeout(timer); controller?.abort(); });
    window.addEventListener("pageshow", () => { leaving = false; poll(); });
    poll();
})();
