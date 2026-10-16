const tokenKey = "beam-token";
const preparedFiles = new Map();
const preparingFiles = new Set();
let currentItems = [];
let lastItems = "";
let loading = false;
let disconnected = false;
let senderReady = false;
let uploadLimits = { maxUploadBytes: 104857600, maxTextBytes: 65536 };
let uploadJobs = [];
let activeUpload = null;
let nextUploadId = 0;
let loadingCapabilities = false;

function getToken() {
  return localStorage.getItem(tokenKey);
}

function setStatus(message) {
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
  localStorage.removeItem(tokenKey);
  senderReady = false;
  if (activeUpload?.xhr) activeUpload.xhr.abort();
  showSetupIfNeeded();
  updateSenderControls();
  document.getElementById("connectionStatus").textContent = "Pairing needed";
  document.getElementById("setup").hidden = false;
  document.getElementById("tokenInput").value = "";
  document.getElementById("tokenError").hidden = false;
  document.getElementById("items").replaceChildren();
  document.getElementById("empty").hidden = true;
  for (const url of preparedFiles.values()) URL.revokeObjectURL(url);
  preparedFiles.clear();
  preparingFiles.clear();
  currentItems = [];
  lastItems = "";
  setStatus("");
}

async function request(url, options = {}) {
  const token = getToken();
  const response = await fetch(url, {
    ...options,
    headers: { ...options.headers, "X-Beam-Token": token },
    signal: options.signal === undefined ? AbortSignal.timeout(30000) : options.signal,
    cache: "no-store",
  });
  if (response.status === 401) {
    if (getToken() !== token) throw new Error("Pairing changed. Retry this request.");
    resetPairing();
    throw new Error("Pair again with the PC.");
  }
  if (!response.ok) throw new Error("PC returned error " + response.status + ". Try again.");
  return response;
}

document.getElementById("saveTokenButton").addEventListener("click", () => {
  const value = document.getElementById("tokenInput").value.trim();
  if (value) {
    localStorage.setItem(tokenKey, value);
    showSetupIfNeeded();
    loadOutbox();
    loadCapabilities();
  }
});

async function loadOutbox() {
  if (!getToken() || loading) return;
  const token = getToken();
  loading = true;
  try {
    const response = await request("/outbox");
    const items = await response.json();
    if (getToken() !== token) return;
    currentItems = items;
    document.getElementById("connectionStatus").textContent = "Connected";
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
      renderItems(items);
      lastItems = snapshot;
    }
    if (disconnected) setStatus("Connected to PC.");
    disconnected = false;
    if (!senderReady) loadCapabilities();
  } catch (error) {
    if (getToken() === token) {
      disconnected = true;
      document.getElementById("connectionStatus").textContent = "PC unavailable";
      setStatus("Cannot connect to PC. Check Beam and WiFi; retrying...");
    }
  } finally {
    loading = false;
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
    const response = await request("/outbox/" + item.id + "/content", { signal: null });
    const blob = await response.blob();
    if (getToken() !== startingToken || !currentItems.some(value => value.id === item.id)) return;
    const url = URL.createObjectURL(blob);
    preparedFiles.set(item.id, url);
    setStatus("Ready to save.");
  } catch (error) {
    setStatus(error.message);
  } finally {
    preparingFiles.delete(item.id);
    renderItems(currentItems);
  }
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

async function copyText(item, container) {
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
    setStatus("Select and copy the text.");
    return;
  }
  try {
    await ack(item.id);
    setStatus("Copied to clipboard.");
    await loadOutbox();
  } catch (error) {
    setStatus("Copied, but could not confirm receipt. " + error.message);
  }
}

async function ack(id) {
  await request("/outbox/" + id + "/ack", { method: "POST" });
}

function formatBytes(bytes) {
  if (bytes < 1024) return bytes + " B";
  if (bytes < 1048576) return (bytes / 1024).toFixed(1).replace(/\.0$/, "") + " KB";
  return (bytes / 1048576).toFixed(1).replace(/\.0$/, "") + " MB";
}

function selectTab(send, focus = false) {
  for (const [id, selected] of [["sendTab", send], ["receiveTab", !send]]) {
    const tab = document.getElementById(id);
    tab.setAttribute("aria-selected", String(selected));
    tab.tabIndex = selected ? 0 : -1;
    if (selected && focus) tab.focus();
  }
  document.getElementById("sendPanel").hidden = !send;
  document.getElementById("receivePanel").hidden = send;
}
document.getElementById("sendTab").addEventListener("click", () => selectTab(true));
document.getElementById("receiveTab").addEventListener("click", () => selectTab(false));
document.querySelector(".tabs").addEventListener("keydown", event => {
  if (["ArrowLeft", "ArrowRight", "Home", "End"].includes(event.key)) {
    event.preventDefault();
    selectTab(event.key === "End" || (event.key !== "Home" && document.getElementById("sendTab").getAttribute("aria-selected") !== "true"), true);
  }
});

async function loadCapabilities() {
  if (!getToken() || loadingCapabilities) return;
  loadingCapabilities = true;
  const token = getToken();
  try {
    const response = await request("/capabilities");
    const limits = await response.json();
    if (getToken() !== token) return;
    if (!(limits.maxUploadBytes > 0 && limits.maxTextBytes > 0)) throw new Error("Invalid upload limits.");
    uploadLimits = limits;
    senderReady = true;
    document.getElementById("uploadLimit").textContent = formatBytes(limits.maxUploadBytes) + " / file";
    document.getElementById("noteSize").textContent = formatBytes(limits.maxTextBytes) + " max";
    document.getElementById("sendStatus").textContent = "";
    updateSenderControls();
    processUploadQueue();
  } catch (error) {
    if (getToken() !== token) return;
    senderReady = false;
    updateSenderControls();
    document.getElementById("sendStatus").textContent = "Sending unavailable. " + error.message;
  } finally {
    loadingCapabilities = false;
    if (getToken() && getToken() !== token) queueMicrotask(loadCapabilities);
  }
}

function updateSenderControls() {
  const enabled = senderReady && Boolean(getToken());
  for (const id of ["fileInput", "photoInput", "noteInput"]) document.getElementById(id).disabled = !enabled;
  document.getElementById("sendNoteButton").disabled = !enabled || uploadJobs.some(job => job.text !== undefined && ["queued", "uploading", "confirming"].includes(job.state));
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
  const job = { id: ++nextUploadId, progress: 0, xhr: null, ...values };
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
  retry.addEventListener("click", () => { job.state = "queued"; job.error = ""; job.progress = 0; updateUploadRow(job); processUploadQueue(); });
  const cancel = document.createElement("button");
  cancel.textContent = "\u00d7";
  cancel.title = "Cancel " + job.name;
  cancel.setAttribute("aria-label", "Cancel " + job.name);
  cancel.addEventListener("click", () => {
    if (job.xhr) job.xhr.abort();
    else { job.state = "cancelled"; job.error = "Cancelled before upload."; updateUploadRow(job); updateSenderControls(); }
  });
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
  const active = ["queued", "uploading", "confirming"].includes(job.state);
  job.dom.state.textContent = job.state === "sent" ? "Sent to PC" : job.state === "queued" ? "Queued" : job.state === "uploading" ? "Uploading " + job.progress + "%" : job.state === "confirming" ? "Saving on PC..." : job.error;
  job.dom.progress.hidden = !["uploading", "confirming"].includes(job.state);
  job.dom.progress.value = job.progress;
  job.dom.retry.hidden = active || job.state === "sent" || !job.retryable;
  job.dom.retry.disabled = !senderReady || !getToken();
  job.dom.cancel.hidden = !active;
  const sent = uploadJobs.filter(item => item.state === "sent").length;
  const failed = uploadJobs.filter(item => ["failed", "cancelled"].includes(item.state)).length;
  document.getElementById("uploadSummary").textContent = sent + " of " + uploadJobs.length + " sent" + (failed ? " / " + failed + " not sent or unconfirmed" : "");
}

function processUploadQueue() {
  if (activeUpload || !senderReady || !getToken()) return;
  const job = uploadJobs.find(item => item.state === "queued");
  if (!job) return;
  activeUpload = job;
  const token = getToken();
  const xhr = new XMLHttpRequest();
  job.xhr = xhr;
  job.state = "uploading";
  updateUploadRow(job);
  updateSenderControls();
  let settled = false;
  function finish(sent, message) {
    if (settled) return;
    settled = true;
    job.xhr = null;
    job.state = sent ? "sent" : "failed";
    job.error = message;
    if (sent) {
      if (job.text !== undefined && document.getElementById("noteInput").value === job.text) document.getElementById("noteInput").value = "";
      job.file = null;
      job.text = undefined;
    }
    updateUploadRow(job);
    activeUpload = null;
    updateSenderControls();
    queueMicrotask(processUploadQueue);
  }
  xhr.open("POST", "/receive");
  xhr.setRequestHeader("X-Beam-Token", token);
  xhr.timeout = 300000;
  xhr.upload.onprogress = event => {
    if (settled) return;
    if (event.lengthComputable) job.progress = Math.round(event.loaded * 100 / event.total);
    job.state = job.progress === 100 ? "confirming" : "uploading";
    updateUploadRow(job);
  };
  xhr.onload = () => {
    if (settled) return;
    if (getToken() !== token) { finish(false, "Pairing changed; receipt is unconfirmed."); return; }
    if (xhr.status === 401) { finish(false, "Pair again with the PC."); resetPairing(); return; }
    if (xhr.status < 200 || xhr.status >= 300) {
      finish(false, xhr.status === 413 ? "Upload exceeds the PC limit." : "PC returned error " + xhr.status + ". Receipt is unconfirmed; retry may create a duplicate.");
      return;
    }
    try {
      const result = JSON.parse(xhr.responseText);
      if (!Array.isArray(result.ids) || result.ids.length !== 1 || typeof result.ids[0] !== "string" || !result.ids[0]) throw new Error();
      finish(true, "");
    } catch { finish(false, "PC did not confirm receipt. Retry may create a duplicate."); }
  };
  xhr.onerror = () => { senderReady = false; finish(false, "Connection lost. Receipt is unconfirmed; retry may create a duplicate."); };
  xhr.ontimeout = () => { senderReady = false; finish(false, "Upload timed out. Receipt is unconfirmed; retry may create a duplicate."); };
  xhr.onabort = () => {
    if (settled) return;
    finish(false, "Cancelled. The PC may already have saved it; retry may create a duplicate.");
    job.state = "cancelled";
    updateUploadRow(job);
  };
  const form = new FormData();
  if (job.file) form.append("files", job.file, job.name);
  else form.append("text", job.text);
  try { xhr.send(form); }
  catch { finish(false, "Upload could not start. Retry when the connection is available."); }
}

document.getElementById("clearFinishedButton").addEventListener("click", () => {
  for (const job of uploadJobs.filter(item => ["sent", "failed", "cancelled"].includes(item.state))) job.dom.row.remove();
  uploadJobs = uploadJobs.filter(item => ["queued", "uploading", "confirming"].includes(item.state));
  document.getElementById("queueSection").hidden = uploadJobs.length === 0;
  for (const job of uploadJobs) updateUploadRow(job);
});

adoptTokenFromUrl();
showSetupIfNeeded();
loadOutbox();
loadCapabilities();
document.addEventListener("visibilitychange", () => { if (!document.hidden) { loadOutbox(); loadCapabilities(); } });
setInterval(loadOutbox, 4000);
