"use strict";

document.querySelectorAll("[data-selection-form]").forEach(form => {
    const snapshot = () => JSON.stringify([...new FormData(form)].filter(([key]) => key !== "__RequestVerificationToken" && key !== "Action"));
    const initial = snapshot();
    let submitting = false;
    const update = () => {
        const selected = form.querySelectorAll('input[type="checkbox"]:checked').length;
        const custom = form.querySelector('[name="CustomFoldersText"]');
        const paths = new Set((custom?.value || "").split(/\r?\n/).map(p => p.trim().toLowerCase()).filter(Boolean));
        const count = selected + paths.size;
        const summary = form.querySelector(".selection-summary");
        if (summary) summary.textContent = `${count} backup source${count === 1 ? "" : "s"} selected`;
        const hint = form.querySelector("[data-save-hint]");
        if (hint) hint.textContent = snapshot() !== initial
            ? "Unsaved changes. Save when you are ready."
            : "Changes are applied at the next logon scan or with Run Backup Now.";
    };
    form.addEventListener("input", update);
    form.addEventListener("change", update);
    form.addEventListener("submit", () => { submitting = true; });
    window.addEventListener("pageshow", () => { submitting = false; });
    window.addEventListener("beforeunload", event => {
        if (!submitting && snapshot() !== initial) { event.preventDefault(); event.returnValue = ""; }
    });
    update();
});

const search = document.querySelector("[data-client-search]");
if (search) {
    const filter = () => {
        const rows = [...document.querySelectorAll("[data-client-row]")];
        const query = search.value.trim().toLowerCase();
        let count = 0;
        rows.forEach(row => { row.hidden = !row.dataset.searchText.toLowerCase().includes(query); if (!row.hidden) count++; });
        document.querySelector("[data-client-count]").textContent = `${count} of ${rows.length} computers`;
        document.querySelector("[data-client-empty]").hidden = count !== 0;
        document.dispatchEvent(new Event("clients-filtered"));
    };
    search.addEventListener("input", filter);
    document.addEventListener("clients-updated", filter);
}
document.addEventListener("submit", event => {
    const form = event.target.closest("form[data-confirm]");
    if (form && !window.confirm(form.dataset.confirm)) event.preventDefault();
});

const bulkForm = document.querySelector("#bulk-clients");
if (bulkForm) {
    const boxes = () => [...document.querySelectorAll("[data-select-client]")];
    const selectVisible = document.querySelector("[data-select-visible]");
    const action = bulkForm.querySelector("select");
    const visible = () => boxes().filter(box => !box.closest("[data-client-row]").hidden);
    const update = () => {
        const count = boxes().filter(box => box.checked).length;
        const hiddenCount = boxes().filter(box => box.checked && box.closest("[data-client-row]").hidden).length;
        bulkForm.querySelector("[data-bulk-count]").textContent = `${count} selected${hiddenCount ? ` (${hiddenCount} hidden by search)` : ""}`;
        const shown = visible();
        selectVisible.checked = shown.length > 0 && shown.every(box => box.checked);
        selectVisible.indeterminate = shown.some(box => box.checked) && !selectVisible.checked;
        selectVisible.disabled = !shown.length;
        bulkForm.querySelector("[data-bulk-review]").disabled = count < 1 || count > 200 || !action.value;
        bulkForm.querySelector("[data-bulk-clear]").disabled = count === 0;
    };
    document.addEventListener("change", event => { if (event.target.matches("[data-select-client]")) update(); });
    action.addEventListener("change", update);
    selectVisible.addEventListener("change", () => { visible().forEach(box => { box.checked = selectVisible.checked; }); update(); });
    bulkForm.querySelector("[data-bulk-clear]").addEventListener("click", () => { boxes().forEach(box => { box.checked = false; }); update(); });
    document.addEventListener("clients-filtered", update);
    window.addEventListener("pageshow", update);
    update();
}
