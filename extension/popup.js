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

  const grabLinksBtn = document.getElementById("grab-links-btn");
  grabLinksBtn?.addEventListener("click", async () => {
    try {
      const tabs = await api.tabs.query({ active: true, currentWindow: true });
      if (!tabs || tabs.length === 0 || !tabs[0].id) return;
      const tab = tabs[0];
      grabLinksBtn.textContent = "Scanning links...";
      grabLinksBtn.disabled = true;

      api.tabs.sendMessage(tab.id, { type: "GATHER_ALL_LINKS" }, async (response) => {
        if (response && response.links && response.links.length > 0) {
          grabLinksBtn.textContent = `Found ${response.links.length} links! Sending...`;
          for (const link of response.links) {
            if (link.url.startsWith("magnet:?")) {
              api.runtime.sendMessage({ type: "INTERCEPT_MAGNET", url: link.url });
            } else if (link.url.toLowerCase().endsWith(".torrent") || link.url.toLowerCase().includes(".torrent?")) {
              api.runtime.sendMessage({ type: "INTERCEPT_TORRENT_LINK", url: link.url, text: link.text });
            } else {
              api.runtime.sendMessage({ type: "FORCE_INTERCEPT_LINK", url: link.url, text: link.text });
            }
          }
          setTimeout(() => {
            grabLinksBtn.textContent = "Download Links on Page";
            grabLinksBtn.disabled = false;
          }, 1800);
        } else {
          grabLinksBtn.textContent = "No downloadable links found";
          setTimeout(() => {
            grabLinksBtn.textContent = "Download Links on Page";
            grabLinksBtn.disabled = false;
          }, 1500);
        }
      });
    } catch {
      grabLinksBtn.textContent = "Cannot scan this page";
      setTimeout(() => {
        grabLinksBtn.textContent = "Download Links on Page";
        grabLinksBtn.disabled = false;
      }, 1500);
    }
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
