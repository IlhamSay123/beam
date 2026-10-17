(() => {
  const panel = document.getElementById("motionPanel");
  const track = panel.querySelector(".motion-track");
  const packet = panel.querySelector(".motion-packet");
  const fill = document.getElementById("motionFill");
  const reduced = matchMedia("(prefers-reduced-motion: reduce)");
  const activePhases = new Set(["uploading", "confirming", "preparing", "checking"]);
  const states = new Map();
  let active = null;
  let animation = null;
  let animationOwner = null;
  let iconKey = "";
  const kindIcons = { image: "image", video: "film", audio: "music-2", archive: "archive", text: "message-square", file: "file-text" };

  function kindFor(item) {
    if (item.isText) return "text";
    const type = (item.contentType || item.type || "").toLowerCase();
    const extension = (item.name || "").split(".").pop().toLowerCase();
    if (type.startsWith("image/") || /^(png|jpg|jpeg|gif|webp|bmp|avif|heic|heif)$/.test(extension)) return "image";
    if (type.startsWith("video/") || /^(mp4|mov|m4v|webm|avi|mkv)$/.test(extension)) return "video";
    if (type.startsWith("audio/") || /^(mp3|m4a|wav|aac|flac|ogg)$/.test(extension)) return "audio";
    if (/^(zip|7z|rar|gz|tar)$/.test(extension)) return "archive";
    return "file";
  }

  function icon(name) {
    const element = document.createElement("i");
    element.dataset.lucide = name;
    element.setAttribute("aria-hidden", "true");
    return element;
  }

  function icons() { window.lucide?.createIcons({ attrs: { width: 18, height: 18, "aria-hidden": "true" } }); }
  function stopAnimation() { animation?.cancel(); animation = null; animationOwner = null; }

  function render() {
    const keepArrival = animation?.playState === "running" && animationOwner?.complete && active?.id === animationOwner.id
      && active?.phase === animationOwner.phase && animationOwner.width === track.clientWidth && !reduced.matches && !document.hidden;
    if (!keepArrival) stopAnimation();
    if (!active) {
      panel.dataset.phase = "idle";
      document.getElementById("motionState").textContent = "Ready to beam";
      document.getElementById("motionName").textContent = "Beam";
      document.getElementById("motionProgress").textContent = "Standby";
      document.getElementById("motionDetail").textContent = "";
      packet.replaceChildren(icon("file")); iconKey = "";
      packet.style.transform = "translateX(0px)";
      packet.style.setProperty("--trail-length", "0px");
      fill.style.width = "0%";
      icons(); return;
    }
    const outgoing = active.direction === "outgoing";
    const phase = active.phase;
    const known = Number.isFinite(active.progress);
    const progress = known ? Math.max(0, Math.min(1, active.progress)) : 0;
    const complete = ["sent", "ready", "arrived", "copied", "download"].includes(phase);
    const failed = ["failed", "cancelled"].includes(phase);
    panel.dataset.direction = active.direction;
    panel.dataset.phase = phase;
    const labels = { checking: "Checking PC...", pending: "Waiting for PC receipt", uploading: "Sending to PC", confirming: "Saving on PC...", preparing: "Receiving from PC", sent: "Saved on PC", ready: "Ready to save", arrived: "New from PC", copied: "Copied from PC", download: "Download started", failed: "Transfer unconfirmed", cancelled: "Transfer cancelled" };
    document.getElementById("motionState").textContent = active.label || labels[phase] || "Ready to beam";
    document.getElementById("motionProgress").textContent = failed ? "Not confirmed" : phase === "confirming" ? "Confirming" : complete ? (phase === "sent" ? "Confirmed" : "Ready") : known ? Math.round(progress * 100) + "%" : "In progress";
    document.getElementById("motionName").textContent = active.item.name || (active.item.isText ? "Note" : "File");
    document.getElementById("motionDetail").textContent = active.detail || "";
    const key = kindFor(active.item) + ":" + (active.previewUrl || "") + ":" + (failed ? "pause" : phase === "sent" ? "check" : "type");
    if (iconKey !== key) {
      iconKey = key;
      if (active.previewUrl && !failed && phase !== "sent") {
        const image = document.createElement("img"); image.src = active.previewUrl; image.alt = "";
        image.addEventListener("error", () => { packet.replaceChildren(icon("image")); icons(); }, { once: true });
        packet.replaceChildren(image);
      } else packet.replaceChildren(icon(failed ? "pause" : phase === "sent" ? "check" : kindIcons[kindFor(active.item)]));
    }
    const distance = Math.max(0, track.clientWidth - packet.offsetWidth);
    const position = complete ? (outgoing ? 1 : 0) : known ? (outgoing ? progress : 1 - progress) : .5;
    const x = position * distance;
    packet.style.transform = "translateX(" + x + "px)";
    const availableTrail = outgoing ? x - 5 : distance - x - 5;
    packet.style.setProperty("--trail-length", Math.max(0, Math.min(32, availableTrail)) + "px");
    fill.style.width = complete ? "100%" : known ? progress * 100 + "%" : "0%";
    fill.style.marginLeft = outgoing ? "0" : "auto";
    panel.querySelector(".motion-lane").setAttribute("aria-label", (outgoing ? "Outgoing" : "Incoming") + " " + kindFor(active.item) + ": " + (active.label || labels[phase] || phase) + (known && !complete ? ", " + Math.round(progress * 100) + " percent" : ""));
    if (!known && activePhases.has(phase) && !reduced.matches && !document.hidden) {
      const from = distance * (outgoing ? .15 : .85);
      const to = distance * (outgoing ? .65 : .35);
      packet.style.setProperty("--trail-length", "0px");
      animation = packet.animate([{ transform: "translateX(" + from + "px)" }, { transform: "translateX(" + to + "px)" }], { duration: 1500, direction: "alternate", iterations: Infinity, easing: "ease-in-out" });
    } else if (complete && active.needsArrival && !reduced.matches && !document.hidden && !keepArrival) {
      const from = outgoing ? 0 : distance;
      packet.style.setProperty("--trail-length", "0px");
      animation = packet.animate([{ transform: "translateX(" + from + "px)" }, { transform: "translateX(" + x + "px)" }], { duration: 700, easing: "cubic-bezier(.2,.75,.3,1)" });
      animationOwner = { id: active.id, phase, width: track.clientWidth, complete: true };
    }
    active.needsArrival = false;
    icons();
  }

  function update(value) {
    if (!value?.id || !value.item) return;
    const previous = states.get(value.id);
    const next = { ...previous, ...value };
    next.hasMoved = previous?.hasMoved || (activePhases.has(next.phase) && Number.isFinite(next.progress) && next.progress > 0 && next.progress < 1);
    next.needsArrival = ["sent", "ready", "arrived", "copied", "download"].includes(next.phase)
      && previous?.phase !== next.phase && !next.hasMoved && !next.receiptConfirmed;
    states.set(value.id, next);
    if (active && active.id !== next.id && activePhases.has(active.phase) && !activePhases.has(next.phase)) return;
    active = next;
    if (states.size > 40) for (const [id, item] of states) if (states.size > 30 && !activePhases.has(item.phase) && id !== active.id) states.delete(id);
    render();
  }

  function clear() { states.clear(); active = null; render(); }
  function discard(id) { states.delete(id); if (active?.id === id) { active = null; render(); } }
  window.BeamMotion = { update, clear, discard, kindFor, icon, icons };
  new ResizeObserver(render).observe(track);
  reduced.addEventListener("change", render);
  document.addEventListener("visibilitychange", render);
  render();
})();
