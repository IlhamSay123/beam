(() => {
  const panel = document.getElementById("vaultPanel");
  const list = document.getElementById("vaultItems");
  const status = document.getElementById("vaultStatus");
  const refreshButton = document.getElementById("vaultRefresh");
  const moreButton = document.getElementById("vaultMore");
  const search = document.getElementById("vaultSearch");
  const direction = document.getElementById("vaultDirection");
  const kind = document.getElementById("vaultKind");
  const entries = new Map();
  const previewUrls = new Map();
  const previewControllers = new Set();
  const previewQueued = new Set();
  const failedPreviews = new Set();
  const previewQueue = [];
  const previewLimit = 8388608;
  let previewCount = 0;
  let items = [];
  let generation = 0;
  let controller = null;
  let tokenForItems = null;
  let nextOffset = 0;
  let lastTotal = 0;
  let lastQuery = "";
  let timer;
  let busy = false;

  const report = text => { status.textContent = text; };
  const icon = name => window.BeamMotion.icon(name);
  const icons = () => window.BeamMotion.icons();
  const observer = new IntersectionObserver(records => {
    for (const record of records) {
      if (!record.isIntersecting) continue;
      observer.unobserve(record.target);
      const entry = entries.get(record.target.dataset.previewId);
      if (entry && !previewQueued.has(entry.item.id) && !failedPreviews.has(entry.item.id)) {
        previewQueued.add(entry.item.id); previewQueue.push(entry);
      }
    }
    processPreviews();
  }, { rootMargin: "80px" });

  function retire(url) { if (url) setTimeout(() => URL.revokeObjectURL(url), 60000); }

  function reset() {
    generation++;
    controller?.abort(); controller = null;
    clearTimeout(timer);
    for (const entry of entries.values()) {
      clearTimeout(entry.retireTimer); entry.preparationController?.abort();
      if (entry.url) URL.revokeObjectURL(entry.url);
    }
    for (const value of previewUrls.values()) URL.revokeObjectURL(value.url);
    for (const request of previewControllers) request.abort();
    observer.disconnect(); previewQueue.length = 0;
    previewUrls.clear(); previewQueued.clear(); failedPreviews.clear(); entries.clear();
    items = []; tokenForItems = null; nextOffset = 0; lastTotal = 0; lastQuery = ""; busy = false;
    list.replaceChildren(); report("");
    document.getElementById("vaultCount").textContent = "";
    document.getElementById("vaultEmpty").hidden = true;
    moreButton.hidden = true; moreButton.disabled = false; refreshButton.disabled = false;
  }

  async function load(append = false, silent = false) {
    const token = getToken();
    if (!token) return;
    if (tokenForItems && tokenForItems !== token) reset();
    tokenForItems = token;
    const requestGeneration = ++generation;
    controller?.abort();
    const requestController = new AbortController(); controller = requestController;
    const timeout = setTimeout(() => requestController.abort(), 30000);
    const offset = append ? nextOffset : 0;
    const queryKey = JSON.stringify([search.value.trim(), direction.value, kind.value]);
    const preserveExtent = silent && !append && queryKey === lastQuery;
    const extent = preserveExtent ? Math.max(40, nextOffset) : 40;
    const query = new URLSearchParams({ limit: String(Math.min(100, extent)), offset: String(offset), search: search.value.trim() });
    if (direction.value) query.set("direction", direction.value);
    if (kind.value) query.set("kind", kind.value);
    busy = true; refreshButton.disabled = true; moreButton.disabled = true;
    if (!silent) report("Loading vault...");
    try {
      let result, pageOffset = offset, targetExtent = extent;
      const received = [];
      do {
        query.set("offset", String(pageOffset));
        const response = await request("/vault?" + query, { signal: requestController.signal });
        result = await response.json();
        if (requestGeneration !== generation || getToken() !== token) return;
        if (!Array.isArray(result.items) || !Number.isFinite(result.total)) throw new Error("Invalid vault response.");
        if (preserveExtent && pageOffset === 0) targetExtent = Math.min(result.total, Math.max(extent, nextOffset + Math.max(0, result.total - lastTotal)));
        received.push(...result.items);
        pageOffset += result.items.length;
        if (!preserveExtent || !result.hasMore || pageOffset >= targetExtent || !result.items.length) break;
        query.set("limit", String(Math.min(100, targetExtent - pageOffset)));
      } while (true);
      const merged = new Map((append ? items : []).map(item => [item.id, item]));
      for (const item of received) if (typeof item.id === "string") merged.set(item.id, item);
      items = Array.from(merged.values());
      nextOffset = pageOffset; lastTotal = result.total; lastQuery = queryKey;
      moreButton.hidden = !result.hasMore;
      document.getElementById("vaultCount").textContent = result.total + (result.total === 1 ? " beam" : " beams");
      const empty = document.getElementById("vaultEmpty");
      empty.hidden = items.length !== 0;
      empty.textContent = search.value || direction.value || kind.value ? "No matching beams." : "No beams yet.";
      render();
      if (!silent) report("");
    } catch (error) {
      if (requestGeneration === generation && getToken() === token && !requestController.signal.aborted) report("Vault unavailable. Check the PC connection, then refresh to retry.");
      else if (requestGeneration === generation && getToken() === token && requestController.signal.aborted) report("Vault request timed out. Refresh to retry.");
    } finally {
      clearTimeout(timeout);
      if (requestGeneration === generation) {
        busy = false; refreshButton.disabled = false; moreButton.disabled = false;
        if (getToken() !== token) reset();
      }
    }
  }

  function imageCandidate(item) {
    return !item.isText && item.available && item.sizeBytes <= previewLimit && window.BeamMotion.kindFor(item) === "image"
      && !/\.(svg|heic|heif)$/i.test(item.name);
  }

  function showPreview(entry, url) {
    if (!entry.node.isConnected) return;
    const image = document.createElement("img"); image.alt = ""; image.src = url;
    image.addEventListener("error", () => { entry.thumbnail.replaceChildren(icon("image")); icons(); }, { once: true });
    entry.thumbnail.replaceChildren(image);
    if (entry.thumbnail instanceof HTMLButtonElement) entry.thumbnail.disabled = false;
  }

  async function boundedPreview(response) {
    const type = (response.headers.get("Content-Type") || "").split(";")[0].toLowerCase();
    if (!/^image\/(png|jpeg|gif|webp|bmp|avif)$/.test(type)) throw new Error("Unsupported preview.");
    if (Number(response.headers.get("Content-Length")) > previewLimit) throw new Error("Preview too large.");
    if (!response.body?.getReader) {
      const blob = await response.blob();
      if (blob.size > previewLimit) throw new Error("Preview too large.");
      return blob;
    }
    const reader = response.body.getReader();
    const chunks = []; let bytes = 0;
    while (true) {
      const { done, value } = await reader.read(); if (done) break;
      bytes += value.byteLength;
      if (bytes > previewLimit) { await reader.cancel(); throw new Error("Preview too large."); }
      chunks.push(value);
    }
    return new Blob(chunks, { type });
  }

  function processPreviews() {
    while (previewCount < 2 && previewQueue.length) {
      const entry = previewQueue.shift();
      if (!entry.node.isConnected || getToken() !== tokenForItems) { previewQueued.delete(entry.item.id); continue; }
      previewCount++;
      const token = getToken();
      const previewController = new AbortController(); previewControllers.add(previewController);
      const timeout = setTimeout(() => previewController.abort(), 15000);
      (async () => {
        try {
          const response = await request("/vault/" + encodeURIComponent(entry.item.id) + "/preview", { signal: previewController.signal });
          const blob = await boundedPreview(response);
          if (getToken() !== token || tokenForItems !== token || !entry.node.isConnected || previewController.signal.aborted) return;
          const url = URL.createObjectURL(blob);
          previewUrls.set(entry.item.id, { url, entry });
          showPreview(entry, url);
          while (previewUrls.size > 12) {
            const [id, value] = previewUrls.entries().next().value;
            URL.revokeObjectURL(value.url); previewUrls.delete(id);
            value.entry.thumbnail.replaceChildren(icon("image"));
            if (value.entry.thumbnail instanceof HTMLButtonElement) value.entry.thumbnail.disabled = true;
          }
          icons();
        } catch { if (tokenForItems === token) failedPreviews.add(entry.item.id); }
        finally { clearTimeout(timeout); previewControllers.delete(previewController); previewQueued.delete(entry.item.id); previewCount--; processPreviews(); }
      })();
    }
  }

  function render() {
    const ids = new Set(items.map(item => item.id));
    for (const [id, entry] of entries) if (!ids.has(id)) {
      entry.preparationController?.abort();
      window.BeamMotion.discard("vault:" + id);
      observer.unobserve(entry.thumbnail); retire(entry.url); entries.delete(id);
      const preview = previewUrls.get(id);
      if (preview) { URL.revokeObjectURL(preview.url); previewUrls.delete(id); }
    }
    const nodes = [];
    for (const item of items) {
      let entry = entries.get(item.id);
      if (!entry) {
        entry = createEntry(item); entries.set(item.id, entry);
      } else {
        entry.item = item;
        entry.downloadButton && (entry.downloadButton.disabled = !item.available || entry.preparing);
      }
      const missing = entry.node.querySelector(".vault-file-state");
      if (!item.available && !missing) {
        const message = document.createElement("p"); message.className = "vault-file-state"; message.textContent = "File missing";
        entry.node.appendChild(message);
      } else if (item.available) missing?.remove();
      nodes.push(entry.node);
    }
    for (const child of Array.from(list.children)) if (!ids.has(child.dataset.id)) child.remove();
    for (let index = 0; index < nodes.length; index++) {
      if (list.children[index] !== nodes[index]) list.insertBefore(nodes[index], list.children[index] || null);
    }
    for (const entry of entries.values()) if (imageCandidate(entry.item)) {
      const preview = previewUrls.get(entry.item.id);
      if (preview) showPreview(entry, preview.url);
      else observer.observe(entry.thumbnail);
    }
    icons();
  }

  function createEntry(item) {
    const node = document.createElement("li"); node.className = "vault-item"; node.dataset.id = item.id; node.dataset.kind = window.BeamMotion.kindFor(item);
    const row = document.createElement("div"); row.className = "vault-row";
    const thumbnail = document.createElement(imageCandidate(item) ? "button" : "span");
    thumbnail.className = "vault-thumbnail"; thumbnail.dataset.previewId = item.id;
    thumbnail.appendChild(icon(({ text: "message-square", image: "image", video: "film", audio: "music-2", archive: "archive" })[node.dataset.kind] || "file-text"));
    const entry = { node, thumbnail, item, url: null, preparing: false, retireTimer: null, preparationController: null };
    if (thumbnail instanceof HTMLButtonElement) {
      thumbnail.type = "button"; thumbnail.disabled = true; thumbnail.setAttribute("aria-label", "Preview " + item.name); thumbnail.setAttribute("aria-expanded", "false");
      thumbnail.addEventListener("click", () => {
        const old = node.querySelector(".vault-image-preview");
        if (old) { old.remove(); thumbnail.setAttribute("aria-expanded", "false"); return; }
        const preview = previewUrls.get(item.id); if (!preview) return;
        const image = document.createElement("img"); image.className = "vault-image-preview"; image.alt = item.name; image.src = preview.url;
        node.appendChild(image); thumbnail.setAttribute("aria-expanded", "true");
      });
    }
    const text = document.createElement("div");
    const title = document.createElement(item.isText ? "button" : "div"); title.className = "vault-name";
    title.textContent = item.isText ? (item.textContent || "Note").split(/\r?\n/)[0] : item.name;
    if (item.isText) {
      title.type = "button"; title.classList.add("vault-text"); title.setAttribute("aria-expanded", "false");
      title.addEventListener("click", () => {
        const old = node.querySelector(".vault-note");
        if (old) { old.remove(); title.setAttribute("aria-expanded", "false"); return; }
        const note = document.createElement("p"); note.className = "vault-note"; note.textContent = entry.item.textContent;
        node.appendChild(note); title.setAttribute("aria-expanded", "true");
      });
    }
    const info = document.createElement("div"); info.className = "vault-info";
    const date = new Date(item.timestampUtc);
    info.textContent = (item.direction === "inbound" ? "Sent to PC" : "Received from PC") + " / " + (item.isText ? "Note" : formatBytes(item.sizeBytes)) + (Number.isNaN(date.getTime()) ? "" : " / " + date.toLocaleDateString(undefined, { day: "numeric", month: "short" }));
    text.append(title, info);
    const actions = document.createElement("div"); actions.className = "vault-actions"; entry.actions = actions;
    const action = document.createElement("button"); action.type = "button"; action.className = "tool-button";
    action.dataset.action = item.isText ? "copy" : "download";
    const label = (item.isText ? "Copy note" : "Download " + item.name); action.setAttribute("aria-label", label); action.title = label;
    action.appendChild(icon(item.isText ? "copy" : "download")); actions.appendChild(action);
    if (item.isText) action.addEventListener("click", async () => {
      if (getToken() !== tokenForItems) { reset(); return; }
      await copyText(entry.item, node, { onStatus: report, acknowledge: entry.item.direction === "outbound" && !entry.item.read });
    });
    else {
      entry.downloadButton = action; action.disabled = !item.available;
      action.addEventListener("click", () => prepare(entry));
    }
    row.append(thumbnail, text, actions); node.appendChild(row);
    if (!item.available) { const missing = document.createElement("p"); missing.className = "vault-file-state"; missing.textContent = "File missing"; node.appendChild(missing); }
    return entry;
  }

  async function prepare(entry) {
    if (entry.preparing) return;
    const token = getToken();
    if (token !== tokenForItems) { reset(); return; }
    entry.preparing = true; entry.downloadButton.disabled = true;
    entry.preparationController = new AbortController();
    const isCurrent = () => entry.node.isConnected && entries.get(entry.item.id) === entry && tokenForItems === token;
    entry.downloadButton.setAttribute("aria-label", "Preparing " + entry.item.name);
    report("Preparing " + entry.item.name + "...");
    try {
      const blob = await fetchFileBlob(entry.item, "/vault/" + encodeURIComponent(entry.item.id) + "/content", "vault:" + entry.item.id,
        { signal: entry.preparationController.signal, isCurrent });
      if (getToken() !== token || tokenForItems !== token || !entry.node.isConnected) return;
      if (entry.url) retire(entry.url);
      entry.url = URL.createObjectURL(blob);
      const link = document.createElement("a"); link.href = entry.url; link.download = entry.item.name; link.textContent = "Save"; link.setAttribute("aria-label", "Save " + entry.item.name);
      link.addEventListener("click", () => {
        const savedUrl = entry.url;
        clearTimeout(entry.retireTimer);
        entry.retireTimer = setTimeout(() => {
          URL.revokeObjectURL(savedUrl);
          if (entry.url === savedUrl) {
            entry.url = null;
            if (entry.node.isConnected) { entry.actions.replaceChildren(entry.downloadButton); icons(); }
          }
        }, 60000);
        setTimeout(async () => {
          if (getToken() !== token || tokenForItems !== token) return;
          try {
            if (entry.item.direction === "outbound" && !entry.item.read) { await ack(entry.item.id); await loadOutbox(); }
            report("Download started. Still in Vault.");
          } catch (error) { report("Download started, but receipt is unconfirmed. " + error.message); }
        }, 0);
      });
      entry.actions.replaceChildren(link);
      window.BeamMotion.update({ id: "vault:" + entry.item.id, direction: "incoming", item: entry.item, phase: "ready", progress: 1 });
      report("Ready to save. Tap Save.");
    } catch (error) { if (getToken() === token && isCurrent()) report("Could not prepare this file. " + error.message); }
    finally {
      if (!isCurrent()) window.BeamMotion.discard("vault:" + entry.item.id);
      entry.preparing = false; entry.downloadButton.disabled = !entry.item.available;
      entry.downloadButton.setAttribute("aria-label", "Download " + entry.item.name);
    }
  }

  refreshButton.addEventListener("click", () => { failedPreviews.clear(); load(); });
  moreButton.addEventListener("click", () => { if (!busy) load(true); });
  search.addEventListener("input", () => { clearTimeout(timer); timer = setTimeout(() => load(), 250); });
  for (const select of [direction, kind]) select.addEventListener("change", () => { clearTimeout(timer); load(); });
  window.addEventListener("beam:tab", event => { if (event.detail === "vault") load(); });
  window.addEventListener("beam:history-changed", () => { if (!panel.hidden) load(false, true); });
  window.addEventListener("beam:pairing-reset", reset);
  window.addEventListener("storage", event => { if (event.key === "beam-token") reset(); });
  document.addEventListener("visibilitychange", () => { if (!document.hidden && !panel.hidden) load(false, true); });
  window.BeamVault = { refresh: load, reset };
  icons();
})();
