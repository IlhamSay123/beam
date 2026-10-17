const tokenKey = "beam-token";
const preparedFiles = new Map();
const preparingFiles = new Set();
let currentItems = [];
let lastItems = "";
let loading = null;
let receiveStatusIsConnection = false;
let senderReady = false;
let uploadLimits = { maxUploadBytes: 104857600, maxTextBytes: 65536 };
let uploadJobs = [];
let activeUpload = null;
let nextUploadId = 0;
let loadingCapabilities = null;
let seenFirstOutbox = false;
const seenOutboxIds = new Set();
let pairingVersion = 0;
let pairingValidated = false;
let heartbeatStarted = false;
let heartbeatAttempt = null;
let heartbeatTimer = null;
let pageSuspended = false;
const sessionBytes = crypto.getRandomValues(new Uint8Array(16));
sessionBytes[6] = (sessionBytes[6] & 15) | 64;
sessionBytes[8] = (sessionBytes[8] & 63) | 128;
const browserSessionId = Array.from(sessionBytes, value => value.toString(16).padStart(2, "0")).join("");

function pageVisible() { return !document.hidden && !pageSuspended; }

function currentPairing(token, version) { return getToken() === token && pairingVersion === version; }

function connectionState(state, message = "") {
  const label = document.getElementById("connectionStatus");
  label.textContent = { connecting: "Connecting", connected: "Connected", unavailable: "PC unavailable", pairing: "Pairing needed" }[state];
  label.dataset.state = state;
  document.getElementById("connectionNotice").hidden = state !== "unavailable";
  document.getElementById("connectionMessage").textContent = message;
}

function connectionFailed(error, reportReceive = false) {
  const message = error.message.startsWith("PC returned") ? error.message : "Cannot connect to PC. Check that Beam is open and both devices are on the same reachable local network. Retrying while this page is open.";
  connectionState("unavailable", message);
  if (reportReceive && (receiveStatusIsConnection || !document.getElementById("status").textContent)) {
    setStatus("Connection interrupted. Items are kept for retry.", true);
  }
}

function connectionVerified() {
  connectionState("connected");
  if (receiveStatusIsConnection) setStatus("Connected to PC.", true);
}

function stopHeartbeat() {
  clearTimeout(heartbeatTimer);
  heartbeatTimer = null;
  if (heartbeatAttempt) {
    const attempt = heartbeatAttempt;
    heartbeatAttempt = null;
    clearTimeout(attempt.timeout);
    attempt.controller.abort();
  }
}

function invalidatePairingRequests() {
  pairingVersion++;
  senderReady = false;
  if (activeUpload) cancelUpload(activeUpload, "Pairing changed; receipt is unconfirmed.", "failed");
  pairingValidated = false;
  heartbeatStarted = false;
  stopHeartbeat();
  loading?.controller.abort();
  loadingCapabilities?.controller.abort();
  loading = null;
  loadingCapabilities = null;
  document.getElementById("items").replaceChildren();
  document.getElementById("receiveCount").textContent = "";
  document.getElementById("empty").hidden = true;
  for (const url of preparedFiles.values()) URL.revokeObjectURL(url);
  preparedFiles.clear();
  preparingFiles.clear();
  currentItems = [];
  lastItems = "";
  seenFirstOutbox = false;
  seenOutboxIds.clear();
  window.BeamMotion?.clear();
  window.dispatchEvent(new Event("beam:pairing-reset"));
  setStatus("");
}

async function sendHeartbeat() {
  if (!getToken() || !pairingValidated || !pageVisible() || heartbeatAttempt) return;
  clearTimeout(heartbeatTimer);
  const token = getToken(), version = pairingVersion;
  const attempt = { controller: new AbortController(), timeout: null, timedOut: false };
  heartbeatStarted = true;
  heartbeatAttempt = attempt;
  attempt.timeout = setTimeout(() => { attempt.timedOut = true; attempt.controller.abort(); }, 5000);
  try {
    const response = await request("/connection/heartbeat", {
      method: "POST", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ sessionId: browserSessionId }), signal: attempt.controller.signal,
    });
    if (response.status !== 204) throw new Error("PC returned an invalid activity response. Try again.");
    if (heartbeatAttempt === attempt && currentPairing(token, version) && pageVisible()) connectionVerified();
  } catch (error) {
    if (heartbeatAttempt === attempt && currentPairing(token, version) && pageVisible() && (!attempt.controller.signal.aborted || attempt.timedOut)) connectionFailed(error);
  } finally {
    clearTimeout(attempt.timeout);
    if (heartbeatAttempt === attempt) {
      heartbeatAttempt = null;
      if (currentPairing(token, version) && pageVisible()) heartbeatTimer = setTimeout(sendHeartbeat, 5000);
    }
  }
}

function recheckConnection() {
  if (!pageVisible() || !getToken()) return;
  stopHeartbeat();
  sendHeartbeat();
  loadOutbox();
  loadCapabilities();
}

function getToken() {
  return localStorage.getItem(tokenKey);
}

function setStatus(message, connectionMessage = false) {
  receiveStatusIsConnection = connectionMessage;
  document.getElementById("status").textContent = message;
}

function adoptTokenFromUrl() {
  const token = new URLSearchParams(window.location.search).get("token");
  if (token) {
    localStorage.setItem(tokenKey, token);
    window.history.replaceState({}, "", window.location.pathname);
  }
}

function showSetupIfNeeded() {
  document.getElementById("setup").hidden = Boolean(getToken());
  document.getElementById("transferShell").hidden = !getToken();
}

function resetPairing() {
  invalidatePairingRequests();
  localStorage.removeItem(tokenKey);
  senderReady = false;
  if (activeUpload?.xhr) activeUpload.xhr.abort();
  showSetupIfNeeded();
  updateSenderControls();
  connectionState("pairing");
  document.getElementById("setup").hidden = false;
  document.getElementById("tokenInput").value = "";
  document.getElementById("tokenError").hidden = false;
}

async function request(url, options = {}, acceptedStatuses = []) {
  const token = getToken();
  const version = pairingVersion;
  const response = await fetch(url, {
    ...options,
    headers: { ...options.headers, "X-Beam-Token": token },
    signal: options.signal === undefined ? AbortSignal.timeout(30000) : options.signal,
    cache: "no-store",
  });
  if (!currentPairing(token, version)) throw new Error("Pairing changed. Retry this request.");
  if (options.signal?.aborted) throw new DOMException("Request cancelled.", "AbortError");
  if (response.status === 401) {
    resetPairing();
    throw new Error("Pair again with the PC.");
  }
  if (!response.ok && !acceptedStatuses.includes(response.status)) throw new Error("PC returned error " + response.status + ". Try again.");
  return response;
}

document.getElementById("saveTokenButton").addEventListener("click", () => {
  const value = document.getElementById("tokenInput").value.trim();
  if (value) {
    resetPairing();
    localStorage.setItem(tokenKey, value);
    document.getElementById("tokenError").hidden = true;
    connectionState("connecting");
    showSetupIfNeeded();
    loadOutbox();
    loadCapabilities();
  }
});

async function loadOutbox() {
  if (!getToken() || loading || !pageVisible()) return;
  const token = getToken();
  const version = pairingVersion;
  const attempt = { controller: new AbortController() };
  loading = attempt;
  const timeout = setTimeout(() => attempt.controller.abort(), 30000);
  try {
    const response = await request("/outbox", { signal: attempt.controller.signal });
    const items = await response.json();
    if (!currentPairing(token, version) || loading !== attempt) return;
    if (!Array.isArray(items) || items.some(item => !item || typeof item.id !== "string" || typeof item.name !== "string")) throw new Error("Invalid pending items response.");
    currentItems = items;
    pairingValidated = true;
    connectionVerified();
    if (!heartbeatStarted) sendHeartbeat();
    document.getElementById("receiveCount").textContent = items.length ? String(items.length) : "";
    document.getElementById("tokenError").hidden = true;
    document.getElementById("empty").hidden = items.length !== 0;
    for (const [id, url] of preparedFiles) {
      if (!items.some(item => item.id === id)) {
        // Let a download already handed to the browser consume the blob.
        setTimeout(() => URL.revokeObjectURL(url), 60000);
        preparedFiles.delete(id);
      }
    }
    const snapshot = JSON.stringify(items);
    if (snapshot !== lastItems) {
      const arrived = items.filter(item => !seenOutboxIds.has(item.id));
      if (seenFirstOutbox && arrived.length) window.BeamMotion?.update({ id: "arrival:" + arrived[0].id, direction: "incoming", item: arrived[0], phase: "arrived", progress: 1, detail: arrived.length > 1 ? arrived.length + " new items" : "" });
      for (const item of items) seenOutboxIds.add(item.id);
      seenFirstOutbox = true;
      renderItems(items);
      lastItems = snapshot;
      window.dispatchEvent(new Event("beam:history-changed"));
    }
    if (!senderReady) loadCapabilities();
  } catch (error) {
    if (currentPairing(token, version) && loading === attempt && pageVisible()) connectionFailed(error, true);
  } finally {
    clearTimeout(timeout);
    if (loading === attempt) loading = null;
  }
}

function saveLink(item, url) {
  const link = document.createElement("a");
  link.href = url;
  link.download = item.name;
  link.textContent = "Save " + item.name;
  link.addEventListener("click", () => {
    // Start the native download in this tap, before asynchronous work.
    setTimeout(async () => {
      try {
        await ack(item.id);
        setStatus("Download started.");
        await loadOutbox();
      } catch (error) {
        setStatus("Download started, but could not confirm receipt. " + error.message);
      }
    }, 0);
  });
  return link;
}

function renderItems(items) {
  const list = document.getElementById("items");
  list.replaceChildren();
  for (const item of items) {
    const li = document.createElement("li");
    li.className = "item item-enter";
    if (!item.isText && preparedFiles.has(item.id)) {
      li.appendChild(saveLink(item, preparedFiles.get(item.id)));
    } else {
      const button = document.createElement("button");
      button.type = "button";
      button.textContent = item.isText ? "Copy: " + item.textContent : "Download " + item.name;
      if (preparingFiles.has(item.id)) {
        button.disabled = true;
        button.textContent = "Preparing " + item.name + "...";
      }
      button.addEventListener("click", () => item.isText ? copyText(item, li) : downloadFile(item));
      li.appendChild(button);
    }
    list.appendChild(li);
  }
}

async function downloadFile(item) {
  if (preparingFiles.has(item.id)) return;
  const startingToken = getToken();
  preparingFiles.add(item.id);
  renderItems(currentItems);
  try {
    const blob = await fetchFileBlob(item, "/outbox/" + encodeURIComponent(item.id) + "/content", "receive:" + item.id,
      { isCurrent: () => currentItems.some(value => value.id === item.id) });
    if (getToken() !== startingToken || !currentItems.some(value => value.id === item.id)) return;
    const url = URL.createObjectURL(blob);
    preparedFiles.set(item.id, url);
    window.BeamMotion?.update({ id: "receive:" + item.id, direction: "incoming", item, phase: "ready", progress: 1 });
    setStatus("Ready to save.");
  } catch (error) {
    setStatus(error.message);
  } finally {
    preparingFiles.delete(item.id);
    renderItems(currentItems);
  }
}

async function fetchFileBlob(item, endpoint, id, options = {}) {
  const token = getToken();
  const isCurrent = options.isCurrent || (() => true);
  const update = values => {
    if (getToken() === token && isCurrent()) window.BeamMotion?.update({ id, direction: "incoming", item, ...values });
  };
  update({ phase: "preparing", progress: null, detail: formatBytes(item.sizeBytes || 0) });
  try {
    const response = await request(endpoint, { signal: options.signal || null });
    let blob;
    if (response.body?.getReader) {
      const reader = response.body.getReader();
      const total = Number(response.headers.get("Content-Length")) || item.sizeBytes || 0;
      const chunks = [];
      let received = 0;
      while (true) {
        const { done, value } = await reader.read();
        if (getToken() !== token || !isCurrent()) { await reader.cancel(); throw new Error("Download no longer selected. Prepare it again when needed."); }
        if (done) break;
        chunks.push(value); received += value.byteLength;
        update({ phase: "preparing", progress: total ? Math.min(1, received / total) : null });
      }
      blob = new Blob(chunks, { type: response.headers.get("Content-Type") || item.contentType || "application/octet-stream" });
    } else blob = await response.blob();
    if (getToken() !== token || !isCurrent()) throw new Error("Download no longer selected. Prepare it again when needed.");
    return blob;
  } catch (error) { update({ phase: "failed", progress: null }); throw error; }
}

function selectText(text, container) {
  container.querySelector("textarea")?.remove();
  const field = document.createElement("textarea");
  field.value = text;
  field.readOnly = true;
  field.setAttribute("aria-label", "Text to copy");
  container.appendChild(field);
  field.focus();
  field.select();
  field.setSelectionRange(0, text.length);
  return field;
}

async function copyText(item, container, options = {}) {
  const report = options.onStatus || setStatus;
  const token = getToken();
  let copied = false;
  if (navigator.clipboard?.writeText) {
    try {
      await navigator.clipboard.writeText(item.textContent);
      copied = true;
    } catch { /* Fall back if clipboard permission is denied. */ }
  }
  if (!copied) {
    const field = selectText(item.textContent, container);
    try { copied = document.execCommand("copy"); } catch { copied = false; }
    if (copied) field.remove();
  }
  if (!copied) {
    report("Select and copy the text.");
    return;
  }
  if (getToken() !== token) return;
  try {
    if (options.acknowledge !== false) await ack(item.id);
    report("Copied to clipboard.");
    window.BeamMotion?.update({ id: "copy:" + item.id, direction: "incoming", item, phase: "copied", progress: 1 });
    await loadOutbox();
  } catch (error) {
    report("Copied, but could not confirm receipt. " + error.message);
  }
}

async function ack(id) {
  await request("/outbox/" + id + "/ack", { method: "POST" });
  window.dispatchEvent(new Event("beam:history-changed"));
}

function formatBytes(bytes) {
  if (bytes < 1024) return bytes + " B";
  if (bytes < 1048576) return (bytes / 1024).toFixed(1).replace(/\.0$/, "") + " KB";
  return (bytes / 1048576).toFixed(1).replace(/\.0$/, "") + " MB";
}

function selectTab(value, focus = false) {
  const selected = typeof value === "boolean" ? value ? "send" : "receive" : value;
  for (const name of ["receive", "send", "vault"]) {
    const active = name === selected;
    const tab = document.getElementById(name + "Tab");
    tab.setAttribute("aria-selected", String(active));
    tab.tabIndex = active ? 0 : -1;
    if (active && focus) tab.focus();
    document.getElementById(name + "Panel").hidden = !active;
  }
  window.dispatchEvent(new CustomEvent("beam:tab", { detail: selected }));
}
document.getElementById("sendTab").addEventListener("click", () => selectTab(true));
document.getElementById("receiveTab").addEventListener("click", () => selectTab(false));
document.getElementById("vaultTab").addEventListener("click", () => selectTab("vault"));
document.querySelector(".tabs").addEventListener("keydown", event => {
  if (["ArrowLeft", "ArrowRight", "Home", "End"].includes(event.key)) {
    event.preventDefault();
    const names = ["receive", "send", "vault"];
    const index = names.findIndex(name => document.getElementById(name + "Tab").getAttribute("aria-selected") === "true");
    const next = event.key === "Home" ? 0 : event.key === "End" ? 2 : (index + (event.key === "ArrowRight" ? 1 : -1) + names.length) % names.length;
    selectTab(names[next], true);
  }
});

async function loadCapabilities() {
  if (!getToken() || loadingCapabilities || !pageVisible()) return;
  const attempt = { controller: new AbortController() };
  loadingCapabilities = attempt;
  const token = getToken();
  const version = pairingVersion;
  const timeout = setTimeout(() => attempt.controller.abort(), 30000);
  try {
    const response = await request("/capabilities", { signal: attempt.controller.signal });
    const limits = await response.json();
    if (!currentPairing(token, version) || loadingCapabilities !== attempt) return;
    if (limits?.safeRetryVersion !== 1) throw new Error("Update Beam on the PC to use safe retries.");
    if (!(limits.maxUploadBytes > 0 && limits.maxTextBytes > 0)) throw new Error("Invalid upload limits.");
    uploadLimits = limits;
    senderReady = true;
    document.getElementById("uploadLimit").textContent = formatBytes(limits.maxUploadBytes) + " / file";
    document.getElementById("noteSize").textContent = formatBytes(limits.maxTextBytes) + " max";
    document.getElementById("sendStatus").textContent = "";
    updateSenderControls();
    processUploadQueue();
  } catch (error) {
    if (!currentPairing(token, version) || loadingCapabilities !== attempt) return;
    senderReady = false;
    updateSenderControls();
    document.getElementById("sendStatus").textContent = "Sending unavailable. " + error.message;
  } finally {
    clearTimeout(timeout);
    if (loadingCapabilities === attempt) loadingCapabilities = null;
  }
}

function updateSenderControls() {
  const enabled = senderReady && Boolean(getToken());
  for (const id of ["fileInput", "photoInput", "noteInput"]) document.getElementById(id).disabled = !enabled;
  document.getElementById("sendNoteButton").disabled = !enabled || uploadJobs.some(job => job.text !== undefined && ["queued", "checking", "uploading", "confirming"].includes(job.state));
  for (const job of uploadJobs) updateUploadRow(job);
}

function enqueueFiles(files) {
  for (const file of files) {
    const tooLarge = file.size > uploadLimits.maxUploadBytes;
    createUploadJob({ name: file.name, file, bytes: file.size, state: tooLarge ? "failed" : "queued",
      error: tooLarge ? "File exceeds " + formatBytes(uploadLimits.maxUploadBytes) + "." : "", retryable: !tooLarge });
  }
  processUploadQueue();
}
for (const id of ["fileInput", "photoInput"]) {
  document.getElementById(id).addEventListener("change", event => {
    enqueueFiles(Array.from(event.target.files));
    event.target.value = "";
  });
}
document.getElementById("noteForm").addEventListener("submit", event => {
  event.preventDefault();
  if (!senderReady || !getToken() || document.getElementById("sendNoteButton").disabled) return;
  const text = document.getElementById("noteInput").value;
  const bytes = new TextEncoder().encode(text).length;
  if (!text.trim() || bytes > uploadLimits.maxTextBytes) {
    document.getElementById("sendStatus").textContent = text.trim() ? "Note exceeds " + formatBytes(uploadLimits.maxTextBytes) + "." : "Enter a note or link first.";
    return;
  }
  document.getElementById("sendStatus").textContent = "";
  createUploadJob({ name: text.split(/\r?\n/)[0].slice(0, 60), text, bytes, state: "queued", error: "", retryable: true });
  updateSenderControls();
  processUploadQueue();
});

function createUploadJob(values) {
  const transferId = Array.from(crypto.getRandomValues(new Uint8Array(16)), value => value.toString(16).padStart(2, "0")).join("");
  const job = { id: ++nextUploadId, transferId, progress: 0, progressKnown: true, xhr: null, attempt: null, ...values };
  job.item = { name: job.name, isText: job.text !== undefined, contentType: job.file?.type || "text/plain" };
  if (job.file && /^(image\/(png|jpeg|gif|webp|avif|bmp))$/i.test(job.file.type) && job.file.size <= 8388608) job.previewUrl = URL.createObjectURL(job.file);
  const row = document.createElement("li");
  row.className = "upload-row";
  const title = document.createElement("strong");
  title.textContent = job.name;
  const size = document.createElement("span");
  size.className = "muted";
  size.textContent = formatBytes(job.bytes);
  const state = document.createElement("p");
  state.className = "upload-state";
  const progress = document.createElement("progress");
  progress.max = 100;
  progress.setAttribute("aria-label", "Upload progress for " + job.name);
  const actions = document.createElement("div");
  actions.className = "upload-actions";
  const retry = document.createElement("button");
  retry.textContent = "Retry";
  retry.setAttribute("aria-label", "Retry " + job.name);
  retry.addEventListener("click", () => {
    if (!senderReady || !getToken() || !job.retryable || !["failed", "cancelled", "pending"].includes(job.state)) return;
    job.needsReceipt = true;
    job.state = "queued"; job.error = ""; job.progress = 0; job.progressKnown = false;
    updateSenderControls(); processUploadQueue();
  });
  const cancel = document.createElement("button");
  cancel.textContent = "\u00d7";
  cancel.title = "Cancel " + job.name;
  cancel.setAttribute("aria-label", "Cancel " + job.name);
  cancel.addEventListener("click", () => cancelUpload(job));
  actions.append(retry, cancel);
  row.append(title, size, state, progress, actions);
  job.dom = { row, state, progress, retry, cancel };
  uploadJobs.push(job);
  document.getElementById("uploadList").appendChild(row);
  document.getElementById("queueSection").hidden = false;
  updateUploadRow(job);
  return job;
}

function updateUploadRow(job) {
  job.dom.row.dataset.state = job.state;
  const active = ["queued", "checking", "uploading", "confirming"].includes(job.state);
  job.dom.state.textContent = job.state === "sent" ? job.receiptConfirmed ? "Already received on PC" : "Sent to PC" : job.state === "checking" ? "Checking PC..." : job.state === "queued" ? "Queued" : job.state === "uploading" ? job.progressKnown ? "Uploading " + job.progress + "%" : "Uploading..." : job.state === "confirming" ? "Saving on PC..." : job.error;
  job.dom.progress.hidden = !["uploading", "confirming"].includes(job.state);
  if (job.progressKnown) job.dom.progress.value = job.progress;
  else job.dom.progress.removeAttribute("value");
  job.dom.retry.hidden = active || job.state === "sent" || !job.retryable;
  job.dom.retry.disabled = !senderReady || !getToken();
  job.dom.retry.textContent = job.state === "pending" ? "Check again" : "Retry";
  job.dom.retry.setAttribute("aria-label", (job.state === "pending" ? "Check again " : "Retry ") + job.name);
  job.dom.cancel.hidden = !active;
  const sent = uploadJobs.filter(item => item.state === "sent").length;
  const failed = uploadJobs.filter(item => ["failed", "cancelled", "pending"].includes(item.state)).length;
  document.getElementById("uploadSummary").textContent = sent + " of " + uploadJobs.length + " sent" + (failed ? " / " + failed + " not sent or unconfirmed" : "");
  if (job.motionStarted && (activeUpload === job || !activeUpload) && job.state !== "queued") {
    window.BeamMotion?.update({ id: "upload:" + job.id, direction: "outgoing", item: job.item, phase: job.state, progress: job.progressKnown ? job.progress / 100 : null, previewUrl: job.previewUrl,
      label: job.receiptConfirmed ? "Already received on PC" : undefined,
      receiptConfirmed: Boolean(job.receiptConfirmed),
      detail: uploadJobs.length > 1 ? (uploadJobs.indexOf(job) + 1) + " / " + uploadJobs.length : formatBytes(job.bytes) });
  }
}

function processUploadQueue() {
  if (activeUpload || !senderReady || !getToken()) return;
  const job = uploadJobs.find(item => item.state === "queued");
  if (!job) return;
  activeUpload = job;
  job.motionStarted = true;
  const attempt = { token: getToken(), version: pairingVersion, controller: new AbortController(), timeout: null };
  job.attempt = attempt;
  if (job.needsReceipt) checkUploadReceipt(job, attempt);
  else startUpload(job, attempt);
}

function ownsUploadAttempt(job, attempt) {
  return job.attempt === attempt && activeUpload === job;
}

function currentUploadAttempt(job, attempt) {
  if (!ownsUploadAttempt(job, attempt)) return false;
  if (currentPairing(attempt.token, attempt.version)) return true;
  finishUploadAttempt(job, attempt, "failed", "Pairing changed; receipt is unconfirmed.");
  return false;
}

function finishUploadAttempt(job, attempt, state, message = "") {
  if (!ownsUploadAttempt(job, attempt)) return;
  clearTimeout(attempt.timeout);
  job.attempt = null;
  job.xhr = null;
  job.state = state;
  job.error = message;
  if (state === "sent") {
    job.progress = 100;
    job.progressKnown = true;
    if (job.text !== undefined && document.getElementById("noteInput").value === job.text) document.getElementById("noteInput").value = "";
    job.file = null;
    job.text = undefined;
    window.dispatchEvent(new Event("beam:history-changed"));
  }
  updateUploadRow(job);
  activeUpload = null;
  updateSenderControls();
  queueMicrotask(processUploadQueue);
}

function cancelUpload(job, message = "Cancelled. Receipt is unconfirmed; Retry checks the PC first.", state = "cancelled") {
  if (job.attempt && ownsUploadAttempt(job, job.attempt)) {
    const attempt = job.attempt, xhr = job.xhr;
    // Retire ownership before abort: synchronous and late callbacks must be inert.
    finishUploadAttempt(job, attempt, state, message);
    attempt.controller.abort();
    xhr?.abort();
  } else if (job.state === "queued") {
    job.state = state; job.error = message;
    updateSenderControls();
  }
}

function validReceiveResult(result) {
  return Array.isArray(result?.ids) && result.ids.length === 1 && typeof result.ids[0] === "string" && Boolean(result.ids[0].trim());
}

async function checkUploadReceipt(job, attempt) {
  job.state = "checking";
  job.progressKnown = false;
  updateSenderControls();
  attempt.timeout = setTimeout(() => attempt.controller.abort(), 30000);
  try {
    // Only an explicit unknown receipt permits another upload of retained bytes.
    const response = await request("/receive/receipts/" + job.transferId, { signal: attempt.controller.signal }, [404, 409]);
    if (!currentUploadAttempt(job, attempt)) return;
    if (response.status === 404) {
      clearTimeout(attempt.timeout);
      startUpload(job, attempt);
    } else if (response.status === 202) {
      finishUploadAttempt(job, attempt, "pending", "PC is still saving. Check again to confirm receipt.");
    } else if (response.status === 409) {
      job.retryable = false;
      finishUploadAttempt(job, attempt, "failed", "Transfer conflict. The PC could not verify this receipt.");
    } else {
      if (response.status !== 200) throw new Error("PC did not confirm receipt.");
      const result = await response.json();
      if (!currentUploadAttempt(job, attempt)) return;
      if (!validReceiveResult(result)) throw new Error("PC returned an invalid receipt.");
      job.receiptConfirmed = true;
      finishUploadAttempt(job, attempt, "sent");
    }
  } catch (error) {
    if (!currentUploadAttempt(job, attempt)) return;
    finishUploadAttempt(job, attempt, "failed", "Receipt is unconfirmed. Check again with Retry. " + (attempt.controller.signal.aborted ? "Receipt check timed out." : error.message));
  }
}

function startUpload(job, attempt) {
  if (!currentUploadAttempt(job, attempt)) return;
  if (!senderReady || uploadLimits.safeRetryVersion !== 1) {
    finishUploadAttempt(job, attempt, "failed", "Sending unavailable. Receipt is unconfirmed; restore safe retries on the PC, then Retry.");
    return;
  }
  const xhr = new XMLHttpRequest();
  job.xhr = xhr;
  job.state = "uploading";
  updateUploadRow(job);
  updateSenderControls();
  const finish = (sent, message) => finishUploadAttempt(job, attempt, sent ? "sent" : "failed", message);
  xhr.open("POST", "/receive");
  xhr.setRequestHeader("X-Beam-Token", attempt.token);
  xhr.setRequestHeader("X-Beam-Transfer-Id", job.transferId);
  xhr.timeout = 300000;
  xhr.upload.onprogress = event => {
    if (!currentUploadAttempt(job, attempt)) return;
    job.progressKnown = event.lengthComputable;
    if (event.lengthComputable) job.progress = Math.round(event.loaded * 100 / event.total);
    job.state = job.progressKnown && job.progress === 100 ? "confirming" : "uploading";
    updateUploadRow(job);
  };
  xhr.onload = () => {
    if (!currentUploadAttempt(job, attempt)) return;
    if (xhr.status === 401) { finish(false, "Pair again with the PC."); resetPairing(); return; }
    if (xhr.status !== 200) {
      if (xhr.status === 409) job.retryable = false;
      finish(false, xhr.status === 409 ? "Transfer conflict. This job cannot be retried." : xhr.status === 413 ? "Upload exceeds the PC limit." : "PC returned error " + xhr.status + ". Receipt is unconfirmed; Retry checks the PC first.");
      return;
    }
    try {
      const result = JSON.parse(xhr.responseText);
      if (!validReceiveResult(result)) throw new Error();
      finish(true, "");
    } catch { finish(false, "PC did not confirm receipt. Retry checks the PC first."); }
  };
  xhr.onerror = () => { if (!currentUploadAttempt(job, attempt)) return; senderReady = false; finish(false, "Connection lost. Receipt is unconfirmed; Retry checks the PC first."); };
  xhr.ontimeout = () => { if (!currentUploadAttempt(job, attempt)) return; senderReady = false; finish(false, "Upload timed out. Receipt is unconfirmed; Retry checks the PC first."); };
  xhr.onabort = () => {
    if (!currentUploadAttempt(job, attempt)) return;
    finishUploadAttempt(job, attempt, "cancelled", "Cancelled. Receipt is unconfirmed; Retry checks the PC first.");
  };
  const form = new FormData();
  if (job.file) form.append("files", job.file, job.name);
  else form.append("text", job.text);
  try { xhr.send(form); }
  catch { finish(false, "Upload could not start. Retry when the connection is available."); }
}

document.getElementById("clearFinishedButton").addEventListener("click", () => {
  for (const job of uploadJobs.filter(item => ["sent", "failed", "cancelled", "pending"].includes(item.state))) {
    job.dom.row.remove();
    if (job.previewUrl) { URL.revokeObjectURL(job.previewUrl); job.previewUrl = null; }
  }
  if (!activeUpload) window.BeamMotion?.clear();
  uploadJobs = uploadJobs.filter(item => ["queued", "checking", "uploading", "confirming"].includes(item.state));
  document.getElementById("queueSection").hidden = uploadJobs.length === 0;
  for (const job of uploadJobs) updateUploadRow(job);
});

adoptTokenFromUrl();
showSetupIfNeeded();
connectionState(getToken() ? "connecting" : "pairing");
loadOutbox();
loadCapabilities();
document.getElementById("retryConnection").addEventListener("click", () => {
  connectionState("connecting");
  recheckConnection();
});
document.addEventListener("visibilitychange", () => {
  if (document.hidden) stopHeartbeat();
  else recheckConnection();
});
window.addEventListener("pagehide", () => { pageSuspended = true; stopHeartbeat(); });
window.addEventListener("pageshow", () => { pageSuspended = false; recheckConnection(); });
window.addEventListener("online", recheckConnection);
window.addEventListener("offline", () => {
  stopHeartbeat();
  if (getToken()) connectionFailed(new Error("Network unavailable."));
});
window.addEventListener("storage", event => {
  if (event.key !== tokenKey) return;
  invalidatePairingRequests();
  senderReady = false;
  showSetupIfNeeded();
  updateSenderControls();
  connectionState(getToken() ? "connecting" : "pairing");
  recheckConnection();
});
setInterval(loadOutbox, 4000);
