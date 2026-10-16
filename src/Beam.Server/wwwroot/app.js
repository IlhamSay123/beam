const tokenKey = "beam-token";
const preparedFiles = new Map();
const preparingFiles = new Set();
let currentItems = [];
let lastItems = "";
let loading = false;
let disconnected = false;

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
}

function resetPairing() {
  localStorage.removeItem(tokenKey);
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
  const response = await fetch(url, {
    ...options,
    headers: { ...options.headers, "X-Beam-Token": getToken() },
    signal: options.signal === undefined ? AbortSignal.timeout(30000) : options.signal,
    cache: "no-store",
  });
  if (response.status === 401) {
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
  }
});

async function loadOutbox() {
  if (!getToken() || loading) return;
  loading = true;
  try {
    const response = await request("/outbox");
    const items = await response.json();
    currentItems = items;
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
  } catch (error) {
    if (getToken()) {
      disconnected = true;
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

adoptTokenFromUrl();
showSetupIfNeeded();
loadOutbox();
setInterval(loadOutbox, 4000);
