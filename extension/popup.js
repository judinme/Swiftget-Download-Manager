// SwiftGet Popup Script (IDM Parity Dashboard)

function getApi() {
  return typeof chrome !== "undefined" ? chrome : (typeof browser !== "undefined" ? browser : null);
}

document.addEventListener("DOMContentLoaded", async () => {
  const api = getApi();
  const interceptToggle = document.getElementById("intercept-toggle");
  const torrentsToggle = document.getElementById("torrents-toggle");
  const insertToggle = document.getElementById("insert-toggle");
  const altToggle = document.getElementById("alt-toggle");
  const snifferToggle = document.getElementById("sniffer-toggle");
  const statusDot = document.getElementById("status-dot");
  const statusText = document.getElementById("status-text");
  const optionsBtn = document.getElementById("options-btn");

  // Load state
  const settings = await api.storage.local.get([
    "interceptionEnabled", "interceptTorrents",
    "insertKeyForce", "altKeyBypass", "snifferEnabled"
  ]);

  if (interceptToggle) interceptToggle.checked = settings.interceptionEnabled !== false;
  if (torrentsToggle) torrentsToggle.checked = settings.interceptTorrents !== false;
  if (insertToggle) insertToggle.checked = settings.insertKeyForce !== false;
  if (altToggle) altToggle.checked = settings.altKeyBypass !== false;
  if (snifferToggle) snifferToggle.checked = settings.snifferEnabled !== false;

  interceptToggle?.addEventListener("change", async () => {
    await api.storage.local.set({ interceptionEnabled: interceptToggle.checked });
  });

  torrentsToggle?.addEventListener("change", async () => {
    await api.storage.local.set({ interceptTorrents: torrentsToggle.checked });
  });

  insertToggle?.addEventListener("change", async () => {
    await api.storage.local.set({ insertKeyForce: insertToggle.checked });
  });

  altToggle?.addEventListener("change", async () => {
    await api.storage.local.set({ altKeyBypass: altToggle.checked });
  });

  snifferToggle?.addEventListener("change", async () => {
    await api.storage.local.set({ snifferEnabled: snifferToggle.checked });
  });

  optionsBtn?.addEventListener("click", () => {
    api.runtime.openOptionsPage();
  });

  // Check Native Host connection
  try {
    api.runtime.sendMessage({ type: "CHECK_CONNECTION" }, (response) => {
      if (response && response.connected) {
        statusDot.className = "status-dot connected";
        const torrentLabel = response.torrentSupported ? " • BitTorrent" : "";
        statusText.textContent = `Active (v${response.version || "1.1.0"}${torrentLabel})`;
      } else {
        statusDot.className = "status-dot";
        statusText.textContent = "Disconnected";
      }
    });
  } catch {
    statusDot.className = "status-dot";
    statusText.textContent = "Disconnected";
  }
});
