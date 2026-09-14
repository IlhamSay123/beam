const tokenKey = "beam-token";

function getToken() {
  return localStorage.getItem(tokenKey);
}

function showSetupIfNeeded() {
  if (!getToken()) {
    document.getElementById("setup").hidden = false;
  }
}

document.getElementById("saveTokenButton").addEventListener("click", () => {
  const value = document.getElementById("tokenInput").value.trim();
  if (value) {
    localStorage.setItem(tokenKey, value);
    document.getElementById("setup").hidden = true;
    loadOutbox();
  }
});

async function loadOutbox() {
  const token = getToken();
  if (!token) {
    return;
  }

  const response = await fetch("/outbox", { headers: { "X-Beam-Token": token } });
  if (!response.ok) {
    return;
  }

  const items = await response.json();
  renderItems(items);
}

function renderItems(items) {
  const list = document.getElementById("items");
  list.innerHTML = "";

  for (const item of items) {
    const li = document.createElement("li");
    li.className = "item item-enter";

    if (item.isText) {
      const button = document.createElement("button");
      button.textContent = `Copy: ${item.textContent}`;
      button.addEventListener("click", () => copyText(item));
      li.appendChild(button);
    } else {
      const link = document.createElement("a");
      link.textContent = `Download ${item.name}`;
      link.href = `/outbox/${item.id}/content`;
      link.addEventListener("click", (event) => {
        event.preventDefault();
        downloadFile(item);
      });
      li.appendChild(link);
    }

    list.appendChild(li);
  }
}

async function downloadFile(item) {
  const token = getToken();
  const response = await fetch(`/outbox/${item.id}/content`, {
    headers: { "X-Beam-Token": token },
  });
  if (!response.ok) {
    return;
  }

  const blob = await response.blob();
  const url = URL.createObjectURL(blob);
  const link = document.createElement("a");
  link.href = url;
  link.download = item.name;
  document.body.appendChild(link);
  link.click();
  document.body.removeChild(link);
  URL.revokeObjectURL(url);

  await ack(item.id);
  loadOutbox();
}

async function copyText(item) {
  await navigator.clipboard.writeText(item.textContent);
  await ack(item.id);
  loadOutbox();
}

async function ack(id) {
  const token = getToken();
  await fetch(`/outbox/${id}/ack`, { method: "POST", headers: { "X-Beam-Token": token } });
}

showSetupIfNeeded();
loadOutbox();
setInterval(loadOutbox, 4000);
