// SwiftGet Download Manager - Background Service Worker (Manifest V3)

const NATIVE_HOST_NAME = "com.swiftget.downloader";
const LOOPBACK_URL = "http://127.0.0.1:52384/api/v1/downloads";

const DEFAULT_EXTENSIONS = [
  ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz", ".iso", ".tgz",
  ".exe", ".msi", ".bat", ".cmd", ".ps1", ".apk", ".appx", ".msix",
  ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".epub",
  ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".flv", ".m4v", ".ts",
  ".mp3", ".wav", ".flac", ".aac", ".ogg", ".m4a", ".opus",
  ".bin", ".dmg", ".pkg", ".deb", ".rpm"
];

const DEFAULT_BLACKLIST = [
  "google.com", "accounts.google.com", "apis.google.com", "gstatic.com",
  "bing.com", "duckduckgo.com", "yahoo.com",
  "live.com", "login.live.com", "microsoft.com", "login.microsoftonline.com",
  "apple.com", "icloud.com",
  "facebook.com", "twitter.com", "x.com", "instagram.com", "linkedin.com",
  "netflix.com", "spotify.com", "github.com",
  "bankofamerica.com", "chase.com", "wellsfargo.com", "paypal.com", "stripe.com"
];

// Initialize default settings on install
chrome.runtime.onInstalled.addListener(async () => {
  const current = await chrome.storage.local.get(["interceptionEnabled", "interceptTorrents", "extensions", "blacklist", "altKeyBypass", "insertKeyForce"]);
  if (current.interceptionEnabled === undefined) {
    await chrome.storage.local.set({
      interceptionEnabled: true,
      interceptTorrents: true,
      altKeyBypass: true,
      insertKeyForce: true,
      extensions: DEFAULT_EXTENSIONS,
      blacklist: DEFAULT_BLACKLIST
    });
  }

  // Create context menus
  chrome.contextMenus.removeAll(() => {
    chrome.contextMenus.create({
      id: "swiftget_download_link",
      title: "Download with SwiftGet",
      contexts: ["link"]
    });

    chrome.contextMenus.create({
      id: "swiftget_download_magnet",
      title: "Add magnet to SwiftGet",
      contexts: ["link"],
      targetUrlPatterns: ["magnet:*"]
    });

    chrome.contextMenus.create({
      id: "swiftget_download_torrent",
      title: "Download .torrent with SwiftGet",
      contexts: ["link"],
      targetUrlPatterns: ["*://*/*.torrent*"]
    });

    chrome.contextMenus.create({
      id: "swiftget_download_media",
      title: "Download Media with SwiftGet",
      contexts: ["image", "video", "audio"]
    });

    chrome.contextMenus.create({
      id: "swiftget_download_all",
      title: "Download all links with SwiftGet",
      contexts: ["page"]
    });
  });
});

// Track modifier key state per tab (IDM parity)
const altKeyHeldTabs = new Set();
const insertKeyHeldTabs = new Set();

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (message.type === "ALT_KEY_STATE" && sender.tab?.id) {
    if (message.isHeld) {
      altKeyHeldTabs.add(sender.tab.id);
    } else {
      altKeyHeldTabs.delete(sender.tab.id);
    }
    sendResponse({ received: true });
  } else if (message.type === "INSERT_KEY_STATE" && sender.tab?.id) {
    if (message.isHeld) {
      insertKeyHeldTabs.add(sender.tab.id);
    } else {
      insertKeyHeldTabs.delete(sender.tab.id);
    }
    sendResponse({ received: true });
  } else if (message.type === "FORCE_INTERCEPT_LINK") {
    // IDM forced interception on clicked link with Insert key held
    const url = message.url;
    const tabUrl = sender.tab?.url || "";
    const rawFileName = message.fileName || message.text || "";
    chrome.cookies.getAll({ url }).then(cookies => {
      const cookieHeader = (cookies || []).map(c => `${c.name}=${c.value}`).join("; ");
      sendDownloadToSwiftGet(url, tabUrl, rawFileName, cookieHeader, "", true).then(() => {
        sendResponse({ success: true, forced: true });
      });
    }).catch(() => {
      sendDownloadToSwiftGet(url, tabUrl, rawFileName, "", "", true).then(() => {
        sendResponse({ success: true, forced: true });
      });
    });
    return true;
  } else if (message.type === "CHECK_CONNECTION") {
    // Ping/status native host
    chrome.runtime.sendNativeMessage(NATIVE_HOST_NAME, { action: "status" }, (response) => {
      if (chrome.runtime.lastError || !response) {
        sendResponse({ connected: false, error: chrome.runtime.lastError?.message || "Not responding" });
      } else {
        if (response.authToken) {
          chrome.storage.local.set({ authToken: response.authToken });
        }
        sendResponse({
          connected: true,
          version: response.version || "1.1.0",
          torrentSupported: response.torrentSupported !== false
        });
      }
    });
    return true; // async sendResponse
  } else if (message.type === "INTERCEPT_MEDIA") {
    sendDownloadToSwiftGet(
      message.url,
      message.pageUrl || sender.tab?.url || "",
      message.fileName || message.title || "",
      "",
      message.savePath || message.destinationPath || "",
      message.startImmediately !== false,
      message.correlationId || ""
    ).then(() => {
      sendResponse({ success: true });
    });
    return true;
  } else if (message.type === "SHELL_EXECUTE") {
    const payload = {
      action: "shellExecute",
      subAction: message.action || "open",
      filePath: message.filePath || ""
    };
    chrome.runtime.sendNativeMessage(NATIVE_HOST_NAME, payload, (res) => {
      if (chrome.runtime.lastError || !res) {
        sendResponse({ success: true, simulated: true });
      } else {
        sendResponse(res);
      }
    });
    return true;
  } else if (message.type === "INTERCEPT_MAGNET") {
    chrome.storage.local.get(["interceptTorrents"]).then((st) => {
      if (st.interceptTorrents !== false) {
        sendMagnetToSwiftGet(message.url, sender.tab?.url || "").then(() => {
          sendResponse({ success: true });
        });
      } else {
        sendResponse({ success: false, bypassed: true });
      }
    });
    return true;
  } else if (message.type === "INTERCEPT_TORRENT_LINK") {
    chrome.storage.local.get(["interceptTorrents"]).then((st) => {
      if (st.interceptTorrents !== false) {
        sendTorrentLinkToSwiftGet(message.url, sender.tab?.url || "", message.text || "").then(() => {
          sendResponse({ success: true });
        });
      } else {
        sendResponse({ success: false, bypassed: true });
      }
    });
    return true;
  }
});

// Handle Context Menu clicks
chrome.contextMenus.onClicked.addListener(async (info, tab) => {
  if (info.menuItemId === "swiftget_download_magnet" && info.linkUrl) {
    await sendMagnetToSwiftGet(info.linkUrl, tab?.url);
  } else if (info.menuItemId === "swiftget_download_torrent" && info.linkUrl) {
    await sendTorrentLinkToSwiftGet(info.linkUrl, tab?.url, "");
  } else if (info.menuItemId === "swiftget_download_link" && info.linkUrl) {
    if (info.linkUrl.startsWith("magnet:?")) {
      await sendMagnetToSwiftGet(info.linkUrl, tab?.url);
    } else if (info.linkUrl.toLowerCase().endsWith(".torrent") || info.linkUrl.toLowerCase().includes(".torrent?")) {
      await sendTorrentLinkToSwiftGet(info.linkUrl, tab?.url, "");
    } else {
      await sendDownloadToSwiftGet(info.linkUrl, tab?.url, "");
    }
  } else if (info.menuItemId === "swiftget_download_media" && info.srcUrl) {
    await sendDownloadToSwiftGet(info.srcUrl, tab?.url, "");
  } else if (info.menuItemId === "swiftget_download_all" && tab?.id) {
    chrome.tabs.sendMessage(tab.id, { type: "GATHER_ALL_LINKS" }, async (response) => {
      if (response && response.links && response.links.length > 0) {
        for (const link of response.links) {
          if (link.url.startsWith("magnet:?")) {
            await sendMagnetToSwiftGet(link.url, tab.url);
          } else if (link.url.toLowerCase().endsWith(".torrent") || link.url.toLowerCase().includes(".torrent?")) {
            await sendTorrentLinkToSwiftGet(link.url, tab.url, link.text);
          } else {
            await sendDownloadToSwiftGet(link.url, tab.url, link.text);
          }
        }
      }
    });
  }
});

// Main Download Interception Hook
chrome.downloads.onCreated.addListener(async (downloadItem) => {
  const settings = await chrome.storage.local.get(["interceptionEnabled", "extensions", "blacklist", "altKeyBypass", "insertKeyForce"]);
  if (settings.interceptionEnabled === false) {
    return; // Interception disabled
  }

  // Check active tab
  const tabs = await chrome.tabs.query({ active: true, currentWindow: true });
  const activeTabId = tabs.length > 0 ? tabs[0].id : null;

  // Check Alt key bypass
  if (settings.altKeyBypass !== false && activeTabId && altKeyHeldTabs.has(activeTabId)) {
    console.log("[SwiftGet] Download bypassed via Alt key held.");
    return;
  }

  const isForceInsert = (settings.insertKeyForce !== false) && activeTabId && insertKeyHeldTabs.has(activeTabId);

  const url = downloadItem.finalUrl || downloadItem.url;
  if (!url || (!url.startsWith("http://") && !url.startsWith("https://"))) {
    return;
  }

  const urlObj = new URL(url);

  // Check blacklist (unless forced by Insert key)
  if (!isForceInsert) {
    const blacklist = settings.blacklist || DEFAULT_BLACKLIST;
    if (blacklist.some(domain => urlObj.hostname.toLowerCase().endsWith(domain.toLowerCase()))) {
      console.log(`[SwiftGet] Skipping download from blacklisted domain: ${urlObj.hostname}`);
      return;
    }
  }

  const rawFileName = downloadItem.filename || urlObj.pathname.split("/").pop() || "";
  const fileName = rawFileName.replace(/\\/g, "/").split("/").pop() || "";
  const ext = fileName.includes(".") ? ("." + fileName.split(".").pop().toLowerCase()) : "";
  const allowedExtensions = settings.extensions || DEFAULT_EXTENSIONS;

  // Check if this download is a BitTorrent transfer
  const mime = (downloadItem.mime || "").toLowerCase();
  const isTorrentMime = mime === "application/x-bittorrent" || mime === "application/x-torrent";
  const isTorrentFile = fileName.toLowerCase().endsWith(".torrent") || urlObj.pathname.toLowerCase().endsWith(".torrent");

  if (isTorrentMime || isTorrentFile) {
    const torrentSettings = await chrome.storage.local.get(["interceptTorrents"]);
    if (torrentSettings.interceptTorrents !== false) {
      try {
        await chrome.downloads.cancel(downloadItem.id);
        await chrome.downloads.erase({ id: downloadItem.id });
      } catch (err) {
        console.warn("[SwiftGet] Failed to cancel browser torrent download:", err);
      }

      let cookieHeader = "";
      try {
        const cookies = await chrome.cookies.getAll({ url });
        cookieHeader = cookies.map(c => `${c.name}=${c.value}`).join("; ");
      } catch {}

      await sendTorrentLinkToSwiftGet(url, downloadItem.referrer || "", fileName, cookieHeader);
      return;
    }
  }

  if (!isForceInsert && ext && !allowedExtensions.includes(ext)) {
    // File extension not in intercept list and not forced
    return;
  }

  // Intercept: cancel browser download and pass to SwiftGet
  try {
    await chrome.downloads.cancel(downloadItem.id);
    await chrome.downloads.erase({ id: downloadItem.id });
  } catch (err) {
    console.warn("[SwiftGet] Failed to cancel browser download:", err);
  }

  // Gather cookies for authentication handoff
  let cookieHeader = "";
  try {
    const cookies = await chrome.cookies.getAll({ url });
    cookieHeader = cookies.map(c => `${c.name}=${c.value}`).join("; ");
  } catch (cookieErr) {
    console.warn("[SwiftGet] Could not read cookies for URL:", cookieErr);
  }

  const referrer = downloadItem.referrer || "";
  await sendDownloadToSwiftGet(url, referrer, fileName, cookieHeader);
});

async function sendDownloadToSwiftGet(url, referrer, fileName, cookies = "", destinationPath = "", startImmediately = true, correlationId = "") {
  const payload = {
    action: "interceptDownload",
    url: url,
    fileName: fileName || "",
    referrer: referrer || "",
    cookies: cookies || "",
    destinationPath: destinationPath || "",
    startImmediately: startImmediately,
    correlationId: correlationId || ("corr_" + Date.now() + "_" + Math.random().toString(36).substring(2, 10)),
    userAgent: navigator.userAgent
  };

  // Try Native Messaging first
  chrome.runtime.sendNativeMessage(NATIVE_HOST_NAME, payload, (response) => {
    if (chrome.runtime.lastError || !response || !response.success) {
      console.log("[SwiftGet] Native host not reached. Trying loopback HTTP fallback...");
      sendViaLoopbackFallback(payload);
    } else {
      console.log("[SwiftGet] Successfully dispatched download to native host:", response);
    }
  });
}

async function sendMagnetToSwiftGet(magnetUri, referrer = "") {
  const payload = {
    action: "addMagnet",
    magnetUri: magnetUri,
    url: magnetUri,
    referrer: referrer || "",
    userAgent: navigator.userAgent
  };

  chrome.runtime.sendNativeMessage(NATIVE_HOST_NAME, payload, (response) => {
    if (chrome.runtime.lastError || !response || !response.success) {
      console.log("[SwiftGet] Native host not responding for magnet. Trying loopback HTTP fallback...");
      sendViaLoopbackFallback(payload);
    } else {
      console.log("[SwiftGet] Successfully dispatched magnet to native host:", response);
    }
  });
}

async function sendTorrentLinkToSwiftGet(url, referrer = "", fileName = "", cookies = "") {
  let base64Payload = null;

  // Attempt direct fetch to capture .torrent bytes under active session
  try {
    const fetchHeaders = {};
    if (cookies) {
      fetchHeaders["Cookie"] = cookies;
    }
    const res = await fetch(url, {
      credentials: "include",
      headers: fetchHeaders
    });
    if (res.ok) {
      const buf = await res.arrayBuffer();
      const uint8 = new Uint8Array(buf);
      // Valid bencoded dictionary starts with ASCII 'd' (0x64)
      if (uint8.length > 0 && uint8[0] === 0x64) {
        let binary = "";
        const len = uint8.byteLength;
        for (let i = 0; i < len; i++) {
          binary += String.fromCharCode(uint8[i]);
        }
        base64Payload = btoa(binary);
      }
    }
  } catch (fetchErr) {
    console.log("[SwiftGet] Direct .torrent fetch skipped/fallback:", fetchErr);
  }

  const payload = {
    action: "addTorrentFile",
    url: url,
    fileName: fileName || "",
    referrer: referrer || "",
    cookies: cookies || "",
    userAgent: navigator.userAgent
  };

  if (base64Payload) {
    payload.fileBytesBase64 = base64Payload;
  }

  chrome.runtime.sendNativeMessage(NATIVE_HOST_NAME, payload, (response) => {
    if (chrome.runtime.lastError || !response || !response.success) {
      console.log("[SwiftGet] Native host not responding for .torrent. Trying loopback HTTP fallback...");
      sendViaLoopbackFallback(payload);
    } else {
      console.log("[SwiftGet] Successfully dispatched .torrent to native host:", response);
    }
  });
}

async function sendViaLoopbackFallback(payload) {
  try {
    let st = await chrome.storage.local.get(["authToken"]);
    let token = st?.authToken;
    if (!token) {
      try {
        const tokenRes = await fetch("http://127.0.0.1:52384/api/v1/auth/token");
        if (tokenRes.ok) {
          const tokenData = await tokenRes.json();
          if (tokenData?.authToken) {
            token = tokenData.authToken;
            await chrome.storage.local.set({ authToken: token });
          }
        }
      } catch {}
    }

    const headers = {
      "Content-Type": "application/json"
    };
    if (token) {
      headers["X-SwiftGet-Token"] = token;
    }
    const res = await fetch(LOOPBACK_URL, {
      method: "POST",
      headers: headers,
      body: JSON.stringify(payload)
    });
    if (res.ok) {
      const respData = await res.json();
      console.log("[SwiftGet] Successfully dispatched download via loopback fallback:", respData);
    } else {
      console.warn("[SwiftGet] Loopback fallback returned status:", res.status);
    }
  } catch (e) {
    console.warn("[SwiftGet] Loopback fallback connection error:", e);
  }
}
