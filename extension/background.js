// SwiftGet Download Manager - Background Service Worker (Manifest V3 / Firefox MV3)

const NATIVE_HOST_NAME = "com.swiftget.downloader";
const LOOPBACK_URL = "http://127.0.0.1:52384/api/v1/downloads";
const CAPTURE_TTL_MS = 15000; // 15 seconds TTL on all capture messages
const SAME_URL_DEDUPE_MS = 3000; // 3 seconds same-URL deduplication
const MAX_DEDUPE_EVENTS = 200; // Ring buffer size for eventIds
const DEFAULT_MIN_SIZE_BYTES = 256 * 1024; // 256 KB minimum threshold

const DEFAULT_EXTENSIONS = [
  ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz", ".iso", ".tgz",
  ".exe", ".msi", ".apk", ".appx", ".msix", ".ipa", ".dmg", ".pkg", ".deb", ".rpm",
  ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".epub",
  ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".flv", ".m4v", ".ts",
  ".mp3", ".wav", ".flac", ".aac", ".ogg", ".m4a", ".opus",
  ".bin"
];

const DEFAULT_BLACKLIST = [
  "claude.ai/api", "*/api/*", "chrome-extension://", "moz-extension://",
  "localhost", "127.0.0.1", "*.appinstaller", "gradle.properties",
  "accounts.google.com", "apis.google.com", "gstatic.com",
  "bing.com", "duckduckgo.com", "yahoo.com",
  "live.com", "login.live.com", "login.microsoftonline.com",
  "icloud.com",
  "facebook.com", "twitter.com", "x.com", "instagram.com", "linkedin.com",
  "netflix.com", "spotify.com",
  "bankofamerica.com", "chase.com", "wellsfargo.com", "paypal.com", "stripe.com"
];

// Record sessionStart when the service worker initializes
let sessionStart = Date.now();

// ----------------------------------------------------
// 1. In-Memory Fast Cache for Zero-Latency Decisions
// ----------------------------------------------------
const settingsCache = {
  interceptionEnabled: true,
  interceptTorrents: true,
  snifferEnabled: true,
  altKeyBypass: true,
  insertKeyForce: true,
  minFileSizeBytes: DEFAULT_MIN_SIZE_BYTES,
  extensions: DEFAULT_EXTENSIONS,
  blacklist: DEFAULT_BLACKLIST
};

// Seed settings synchronously from storage and purge any stale offline queues
if (typeof chrome !== "undefined" && chrome.storage && chrome.storage.local) {
  try {
    const syncKeys = [
      "interceptionEnabled", "interceptTorrents", "snifferEnabled",
      "altKeyBypass", "insertKeyForce", "minFileSizeBytes",
      "extensions", "blacklist", "pendingCaptures", "retryQueue", "queuedDownloads"
    ];
    const applyLoadedItems = (items) => {
      if (!items) return;
      if ((items.pendingCaptures || items.retryQueue || items.queuedDownloads) &&
          typeof chrome.storage.local.remove === "function") {
        chrome.storage.local.remove(["pendingCaptures", "retryQueue", "queuedDownloads"]);
      }
      for (const [k, v] of Object.entries(items)) {
        if (v !== undefined) {
          settingsCache[k] = v;
        }
      }
    };
    const maybePromise = chrome.storage.local.get(syncKeys, applyLoadedItems);
    if (maybePromise && typeof maybePromise.then === "function") {
      maybePromise.then(applyLoadedItems).catch(() => {});
    }
  } catch {}

  if (chrome.storage.onChanged && typeof chrome.storage.onChanged.addListener === "function") {
    chrome.storage.onChanged.addListener((changes, area) => {
      if (area === "local") {
        for (const [key, change] of Object.entries(changes)) {
          settingsCache[key] = change.newValue;
        }
      }
    });
  }
}

if (typeof chrome !== "undefined" && chrome.runtime && chrome.runtime.onStartup) {
  chrome.runtime.onStartup.addListener(() => {
    sessionStart = Date.now();
    deDupCache.clear();
    eventIdRing.length = 0;
    earlyHeaderCache.clear();
  });
}

// Keep-alive periodic alarm to prevent premature worker suspension during active use
if (typeof chrome !== "undefined" && chrome.alarms) {
  chrome.alarms.create("swiftget_keepalive", { periodInMinutes: 0.4 }); // Every 24s
  chrome.alarms.onAlarm.addListener((alarm) => {
    if (alarm.name === "swiftget_keepalive") {
      cleanExpiredCaches();
    }
  });
}

// ----------------------------------------------------
// 2. Deduplication (200 eventIds + 3s URL window) & Early Header Cache
// ----------------------------------------------------
const deDupCache = new Map(); // normalizedUrlOrId -> { timestamp, eventId, isMedia, downloadId }
const eventIdRing = []; // last 200 eventIds
const earlyHeaderCache = new Map(); // url -> { disposition, contentType, contentLength, tabId, timestamp, isSwiftGetOrigin }
const swiftGetOriginUrls = new Set();

function generateEventId() {
  if (typeof crypto !== "undefined" && typeof crypto.randomUUID === "function") {
    return crypto.randomUUID();
  }
  return "evt_" + Date.now() + "_" + Math.random().toString(36).substring(2, 11);
}

function cleanExpiredCaches() {
  const now = Date.now();
  for (const [k, v] of deDupCache.entries()) {
    if (now - v.timestamp > CAPTURE_TTL_MS) {
      deDupCache.delete(k);
    }
  }
  for (const [k, v] of earlyHeaderCache.entries()) {
    if (now - v.timestamp > CAPTURE_TTL_MS) {
      earlyHeaderCache.delete(k);
    }
  }
}

function sanitizeMediaUrl(url) {
  if (!url) return "";
  try {
    const u = new URL(url);
    const host = u.hostname.toLowerCase();
    if (host.includes("youtube.com") || host.includes("youtu.be")) {
      // Never add automatic YouTube Mix/Radio playlist parameters (list=RD..., start_radio=...)
      const listParam = u.searchParams.get("list") || "";
      if (listParam.toUpperCase().startsWith("RD") || u.searchParams.has("v")) {
        u.searchParams.delete("list");
        u.searchParams.delete("start_radio");
        u.searchParams.delete("index");
        u.searchParams.delete("pp");
      }
      return u.toString();
    }
    return url;
  } catch {
    return url;
  }
}

function normalizeUrlKey(url) {
  if (!url) return "";
  const cleaned = sanitizeMediaUrl(url);
  try {
    const u = new URL(cleaned);
    if (u.hostname.toLowerCase().includes("youtube.com") && u.searchParams.has("v")) {
      return u.origin + u.pathname + "?v=" + u.searchParams.get("v");
    }
    return u.origin + u.pathname;
  } catch {
    return cleaned.split("?")[0].toLowerCase();
  }
}

function isDuplicate(key, windowMs = SAME_URL_DEDUPE_MS) {
  cleanExpiredCaches();
  const entry = deDupCache.get(key);
  if (!entry) return false;
  return (Date.now() - entry.timestamp) <= windowMs;
}

function isEventIdSeen(eventId) {
  return eventId && eventIdRing.includes(eventId);
}

function recordCapture(key, meta = {}) {
  cleanExpiredCaches();
  const evtId = meta.eventId || meta.correlationId || generateEventId();
  if (!eventIdRing.includes(evtId)) {
    eventIdRing.push(evtId);
    while (eventIdRing.length > MAX_DEDUPE_EVENTS) {
      eventIdRing.shift();
    }
  }
  deDupCache.set(key, { ...meta, eventId: evtId, timestamp: Date.now() });
  return evtId;
}

// ----------------------------------------------------
// 3. Early Header Sniffer & Loop Prevention (webRequest)
// ----------------------------------------------------
if (typeof chrome !== "undefined" && chrome.webRequest) {
  try {
    if (chrome.webRequest.onBeforeSendHeaders) {
      chrome.webRequest.onBeforeSendHeaders.addListener(
        (details) => {
          if (!details.requestHeaders || !details.url) return;
          for (const h of details.requestHeaders) {
            if (h.name && h.name.toLowerCase() === "x-swiftget-origin") {
              swiftGetOriginUrls.add(details.url);
              return;
            }
          }
        },
        { urls: ["<all_urls>"] },
        ["requestHeaders"]
      );
    }

    if (chrome.webRequest.onHeadersReceived) {
      chrome.webRequest.onHeadersReceived.addListener(
        (details) => {
          // Strict filter: only inspect top-frame or sub-frame navigations with a valid tabId
          if (!details.responseHeaders || !details.url) return;
          if (typeof details.tabId === "number" && details.tabId < 0) return;

          let disposition = null;
          let contentType = null;
          let contentLength = null;

          for (const h of details.responseHeaders) {
            const name = h.name.toLowerCase();
            if (name === "content-disposition") disposition = h.value;
            else if (name === "content-type") contentType = h.value;
            else if (name === "content-length") contentLength = parseInt(h.value, 10);
          }

          if (disposition || (contentType && isBinaryDownloadContentType(contentType))) {
            earlyHeaderCache.set(details.url, {
              disposition,
              contentType,
              contentLength: Number.isFinite(contentLength) ? contentLength : null,
              tabId: details.tabId,
              initiator: details.initiator || details.documentUrl || "",
              timestamp: Date.now()
            });
          }
        },
        { urls: ["<all_urls>"], types: ["main_frame", "sub_frame"] },
        ["responseHeaders"]
      );
    }
  } catch (err) {
    console.warn("[SwiftGet] webRequest setup skipped:", err);
  }
}

function isBinaryDownloadContentType(ct) {
  if (!ct) return false;
  const lower = ct.toLowerCase().split(";")[0].trim();
  if (isNonBinaryTextOrJsonMime(lower)) return false;
  return lower === "application/octet-stream" ||
         lower === "application/zip" ||
         lower === "application/x-rar-compressed" ||
         lower === "application/x-7z-compressed" ||
         lower === "application/x-msdownload" ||
         lower === "application/x-msi" ||
         lower === "application/x-iso9660-image" ||
         lower === "application/pdf" ||
         lower === "application/x-bittorrent" ||
         lower.startsWith("video/") ||
         lower.startsWith("audio/");
}

function isNonBinaryTextOrJsonMime(mime) {
  if (!mime) return false;
  const clean = mime.toLowerCase().split(";")[0].trim();
  return clean.startsWith("text/") ||
         clean === "application/json" ||
         clean === "application/ld+json" ||
         clean === "application/javascript" ||
         clean === "application/x-javascript" ||
         clean === "application/xml" ||
         clean === "application/xhtml+xml";
}

function matchesIgnorePattern(url, host, fileName, initiator, pattern) {
  if (!pattern) return false;
  const p = pattern.trim().toLowerCase();
  if (!p) return false;
  const lowerUrl = (url || "").toLowerCase();
  const lowerHost = (host || "").toLowerCase();
  const lowerFile = (fileName || "").toLowerCase();
  const lowerInit = (initiator || "").toLowerCase();

  if (p.startsWith("*.")) {
    const suffix = p.substring(1);
    if (lowerFile.endsWith(suffix) || lowerHost.endsWith(suffix) || lowerUrl.endsWith(suffix) || lowerUrl.includes(suffix + "?")) {
      return true;
    }
  }
  if (p.includes("*")) {
    const parts = p.split("*").filter(Boolean);
    if (parts.length > 0 && parts.every(part => lowerUrl.includes(part))) {
      return true;
    }
  }
  if (lowerHost === p || lowerHost.endsWith("." + p)) return true;
  if (lowerFile && lowerFile === p) return true;
  if (lowerUrl.includes(p)) return true;
  if (lowerInit && lowerInit.includes(p)) return true;
  return false;
}

// ----------------------------------------------------
// 4. Modifier Key State & Explicit User Actions
// ----------------------------------------------------
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
    return false;
  }

  if (message.type === "INSERT_KEY_STATE" && sender.tab?.id) {
    if (message.isHeld) {
      insertKeyHeldTabs.add(sender.tab.id);
    } else {
      insertKeyHeldTabs.delete(sender.tab.id);
    }
    sendResponse({ received: true });
    return false;
  }

  if (message.type === "FORCE_INTERCEPT_LINK") {
    const url = sanitizeMediaUrl(message.url);
    const tabUrl = sender.tab?.url || "";
    const tabId = sender.tab?.id ?? -1;
    const rawFileName = message.fileName || message.text || "";
    const eventId = generateEventId();
    recordCapture(normalizeUrlKey(url), { eventId });

    chrome.cookies.getAll({ url }).then(cookies => {
      const cookieHeader = (cookies || []).map(c => `${c.name}=${c.value}`).join("; ");
      sendDownloadToSwiftGet(url, tabUrl, rawFileName, cookieHeader, "", true, eventId, {
        tabId,
        initiator: tabUrl,
        userGesture: true,
        explicitUserAction: true
      }).then((ok) => {
        sendResponse({ success: ok, forced: true });
      });
    }).catch(() => {
      sendDownloadToSwiftGet(url, tabUrl, rawFileName, "", "", true, eventId, {
        tabId,
        initiator: tabUrl,
        userGesture: true,
        explicitUserAction: true
      }).then((ok) => {
        sendResponse({ success: ok, forced: true });
      });
    });
    return true;
  }

  if (message.type === "INTERCEPT_MEDIA") {
    // Requirement D: Media (YouTube and similar): capture ONLY when the user explicitly clicks the SwiftGet button
    if (message.userGesture === false) {
      sendResponse({ success: false, reason: "user_gesture_required" });
      return false;
    }

    const mediaUrl = sanitizeMediaUrl(message.url);
    const pageUrl = sanitizeMediaUrl(message.pageUrl || sender.tab?.url || "");
    const tabId = sender.tab?.id ?? -1;
    const fileName = message.fileName || message.title || "";
    const destPath = message.savePath || "";
    const startImm = message.startImmediately !== false;
    const eventId = message.correlationId || generateEventId();

    const normMedia = normalizeUrlKey(mediaUrl);
    if (isDuplicate(normMedia, 400) || isEventIdSeen(eventId)) {
      sendResponse({ success: true, deduplicated: true });
      return false;
    }

    recordCapture(normMedia, { eventId, isMedia: true });
    if (pageUrl) {
      recordCapture(normalizeUrlKey(pageUrl), { eventId, isMedia: true });
    }

    chrome.cookies.getAll({ url: mediaUrl }).then(cookies => {
      const cookieHeader = (cookies || []).map(c => `${c.name}=${c.value}`).join("; ");
      sendDownloadToSwiftGet(mediaUrl, pageUrl, fileName, cookieHeader, destPath, startImm, eventId, {
        tabId,
        initiator: pageUrl,
        userGesture: true,
        explicitUserAction: true
      }).then((ok) => {
        sendResponse({ success: ok });
      });
    }).catch(() => {
      sendDownloadToSwiftGet(mediaUrl, pageUrl, fileName, "", destPath, startImm, eventId, {
        tabId,
        initiator: pageUrl,
        userGesture: true,
        explicitUserAction: true
      }).then((ok) => {
        sendResponse({ success: ok });
      });
    });
    return true;
  }

  if (message.type === "CHECK_CONNECTION") {
    chrome.runtime.sendNativeMessage(NATIVE_HOST_NAME, { action: "status" }, async (response) => {
      if (chrome.runtime.lastError || !response) {
        const nativeErr = chrome.runtime.lastError?.message || "Not responding";
        if (typeof fetch === "function") {
          try {
            const healthRes = await fetch("http://127.0.0.1:52384/api/v1/health");
            if (healthRes.ok) {
              const healthData = await healthRes.json();
              try {
                const tokenRes = await fetch("http://127.0.0.1:52384/api/v1/auth/token");
                if (tokenRes.ok) {
                  const tokenData = await tokenRes.json();
                  if (tokenData?.authToken) {
                    await chrome.storage.local.set({ authToken: tokenData.authToken });
                  }
                }
              } catch {}
              sendResponse({
                connected: true,
                version: healthData?.version || "1.1.0",
                torrentSupported: true
              });
              return;
            }
          } catch {}
        }
        sendResponse({ connected: false, error: nativeErr });
      } else {
        if (response.authToken) {
          chrome.storage.local.set({ authToken: response.authToken });
        }
        sendResponse({
          connected: response.appRunning !== false,
          version: response.version || "1.1.0",
          torrentSupported: response.torrentSupported !== false
        });
      }
    });
    return true;
  }

  if (message.type === "SHELL_EXECUTE") {
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
  }

  if (message.type === "INTERCEPT_MAGNET") {
    if (settingsCache.interceptTorrents !== false) {
      sendMagnetToSwiftGet(message.url, sender.tab?.url || "").then(() => {
        sendResponse({ success: true });
      });
    } else {
      sendResponse({ success: false, bypassed: true });
    }
    return true;
  }

  if (message.type === "INTERCEPT_TORRENT_LINK") {
    if (settingsCache.interceptTorrents !== false) {
      sendTorrentLinkToSwiftGet(message.url, sender.tab?.url || "", message.text || "").then(() => {
        sendResponse({ success: true });
      });
    } else {
      sendResponse({ success: false, bypassed: true });
    }
    return true;
  }
});

// ----------------------------------------------------
// 5. Official Download Interception Hooks (Strict Live & User-Initiated Only)
// ----------------------------------------------------

// Primary Hook 1: onDeterminingFilename
if (typeof chrome !== "undefined" && chrome.downloads && chrome.downloads.onDeterminingFilename) {
  chrome.downloads.onDeterminingFilename.addListener((downloadItem, suggest) => {
    handleBrowserDownloadEvent(downloadItem, "onDeterminingFilename");
  });
}

// Fallback Hook 2: onCreated
if (typeof chrome !== "undefined" && chrome.downloads && chrome.downloads.onCreated) {
  chrome.downloads.onCreated.addListener((downloadItem) => {
    return handleBrowserDownloadEvent(downloadItem, "onCreated");
  });
}

async function handleBrowserDownloadEvent(downloadItem, triggerSource) {
  if (!downloadItem) return;

  const decision = evaluateDownloadInterception(downloadItem);
  if (!decision.shouldIntercept) {
    return; // Let the browser handle the download normally
  }

  const normKey = normalizeUrlKey(downloadItem.finalUrl || downloadItem.url);
  if (isDuplicate(normKey, SAME_URL_DEDUPE_MS) || isDuplicate("dlid_" + String(downloadItem.id), CAPTURE_TTL_MS)) {
    return;
  }

  const eventId = generateEventId();
  recordCapture(normKey, { downloadId: downloadItem.id, eventId });
  recordCapture("dlid_" + String(downloadItem.id), { url: downloadItem.url, eventId });

  // Attempt to forward to a running SwiftGet instance FIRST, and ONLY cancel the browser download
  // if SwiftGet is running and accepts the handoff! Never cancel and re-trigger chrome.downloads.download().
  await executeInterceptionHandoff(downloadItem, decision, eventId, triggerSource);
}

function evaluateDownloadInterception(downloadItem) {
  if (settingsCache.interceptionEnabled === false) {
    return { shouldIntercept: false, reason: "interception_disabled" };
  }

  // 1. State & Event-Time Guard (Requirement A):
  // Never replay old download history on startup or service worker wakeup!
  if (downloadItem.state && downloadItem.state !== "in_progress") {
    return { shouldIntercept: false, reason: "not_in_progress" };
  }

  if (downloadItem.startTime) {
    const startMs = Date.parse(downloadItem.startTime);
    if (!Number.isNaN(startMs)) {
      const ageMs = Date.now() - startMs;
      if (ageMs > CAPTURE_TTL_MS || startMs < (sessionStart - 2000)) {
        return { shouldIntercept: false, reason: "stale_event_ttl" };
      }
    }
  }

  // Alt key bypass: user holding Alt wants browser native transfer
  for (const tabId of altKeyHeldTabs) {
    if (settingsCache.altKeyBypass !== false) {
      return { shouldIntercept: false, reason: "alt_key_bypass" };
    }
  }

  const rawUrl = downloadItem.finalUrl || downloadItem.url || "";
  if (!rawUrl) {
    return { shouldIntercept: false, reason: "empty_url" };
  }

  // 2. Reject blob:, data:, chrome-extension:, moz-extension:, about:, javascript: (Requirement D)
  const lowerUrl = rawUrl.toLowerCase();
  if (lowerUrl.startsWith("blob:") ||
      lowerUrl.startsWith("data:") ||
      lowerUrl.startsWith("chrome-extension:") ||
      lowerUrl.startsWith("moz-extension:") ||
      lowerUrl.startsWith("about:") ||
      lowerUrl.startsWith("javascript:")) {
    return { shouldIntercept: false, reason: "non_network_scheme" };
  }

  if (!lowerUrl.startsWith("http://") && !lowerUrl.startsWith("https://")) {
    return { shouldIntercept: false, reason: "unsupported_scheme" };
  }

  // 3. Loop prevention: skip requests originated by SwiftGet itself (Requirement E)
  if (swiftGetOriginUrls.has(rawUrl) || swiftGetOriginUrls.has(downloadItem.url)) {
    return { shouldIntercept: false, reason: "swiftget_origin_loop" };
  }

  let urlObj;
  try {
    urlObj = new URL(rawUrl);
  } catch {
    return { shouldIntercept: false, reason: "invalid_url" };
  }

  const host = urlObj.hostname.toLowerCase();
  if (host === "localhost" || host === "127.0.0.1" || host === "::1") {
    return { shouldIntercept: false, reason: "localhost_ignored" };
  }

  // Reject API endpoints (/api/)
  if (urlObj.pathname.toLowerCase().includes("/api/")) {
    return { shouldIntercept: false, reason: "api_endpoint_ignored" };
  }

  // Never auto-capture YouTube watch URLs from downloads API (handled only via explicit user click on overlay)
  if (host.includes("youtube.com") || host.includes("youtu.be")) {
    return { shouldIntercept: false, reason: "media_requires_explicit_button" };
  }

  const earlyHeader = earlyHeaderCache.get(rawUrl) || earlyHeaderCache.get(downloadItem.url) || null;
  if (earlyHeader && typeof earlyHeader.tabId === "number" && earlyHeader.tabId < 0) {
    return { shouldIntercept: false, reason: "background_tab_negative_one" };
  }

  const rawFileName = downloadItem.filename || (earlyHeader?.disposition) || urlObj.pathname.split("/").pop() || "";
  const fileName = rawFileName.replace(/\\/g, "/").split("/").pop() || "";
  const ext = fileName.includes(".") ? ("." + fileName.split(".").pop().toLowerCase()) : "";
  const initiator = downloadItem.referrer || earlyHeader?.initiator || "";

  // 4. Configurable Ignore List check (Requirement D)
  const blacklist = settingsCache.blacklist || DEFAULT_BLACKLIST;
  for (const pattern of blacklist) {
    if (matchesIgnorePattern(rawUrl, host, fileName, initiator, pattern)) {
      return { shouldIntercept: false, reason: `ignored_pattern:${pattern}` };
    }
  }

  const mime = (downloadItem.mime || earlyHeader?.contentType || "").toLowerCase().split(";")[0].trim();
  const isTorrent = mime === "application/x-bittorrent" || mime === "application/x-torrent" ||
                    fileName.toLowerCase().endsWith(".torrent") || urlObj.pathname.toLowerCase().endsWith(".torrent");

  if (isTorrent) {
    return { shouldIntercept: settingsCache.interceptTorrents !== false, isTorrent: true, fileName };
  }

  // 5. Reject non-binary text/json/markdown MIME types & enforce minimum size threshold (Requirement D)
  const knownSize = (downloadItem.fileSize && downloadItem.fileSize > 0)
    ? downloadItem.fileSize
    : (earlyHeader?.contentLength && earlyHeader.contentLength > 0 ? earlyHeader.contentLength : -1);
  const minSizeBytes = typeof settingsCache.minFileSizeBytes === "number"
    ? settingsCache.minFileSizeBytes
    : DEFAULT_MIN_SIZE_BYTES;

  if (isNonBinaryTextOrJsonMime(mime)) {
    return { shouldIntercept: false, reason: `non_binary_mime:${mime}` };
  }

  if (knownSize > 0 && knownSize < minSizeBytes) {
    return { shouldIntercept: false, reason: `below_min_size:${knownSize}` };
  }

  const allowedExtensions = settingsCache.extensions || DEFAULT_EXTENSIONS;

  // Extension match check
  if (ext && allowedExtensions.includes(ext)) {
    return { shouldIntercept: true, isTorrent: false, fileName };
  }

  // Binary attachment with allowed/binary MIME and valid size
  if (earlyHeader && earlyHeader.disposition && earlyHeader.disposition.toLowerCase().includes("attachment")) {
    if (ext && !allowedExtensions.includes(ext)) {
      return { shouldIntercept: false, reason: `extension_not_allowed:${ext}` };
    }
    if (knownSize <= 0 && !isBinaryDownloadContentType(mime)) {
      return { shouldIntercept: false, reason: "unknown_size_non_binary_attachment" };
    }
    return { shouldIntercept: true, isTorrent: false, fileName };
  }

  if (mime && isBinaryDownloadContentType(mime) && (knownSize < 0 || knownSize >= minSizeBytes)) {
    return { shouldIntercept: true, isTorrent: false, fileName };
  }

  return { shouldIntercept: false, reason: "no_matching_rule" };
}

async function executeInterceptionHandoff(downloadItem, decision, eventId, triggerSource) {
  const url = downloadItem.finalUrl || downloadItem.url;
  const referrer = downloadItem.referrer || "";
  const earlyHeader = earlyHeaderCache.get(url) || null;

  // 1. Resolve browser window bounds for multi-monitor dialog placement
  let windowBounds = null;
  try {
    if (chrome.windows) {
      const win = await chrome.windows.getLastFocused();
      if (win) {
        windowBounds = {
          left: win.left || 0,
          top: win.top || 0,
          width: win.width || 1024,
          height: win.height || 768,
          state: win.state || "normal"
        };
      }
    }
  } catch {}

  // 2. Fetch session cookies
  let cookieHeader = "";
  try {
    const cookies = await chrome.cookies.getAll({ url });
    cookieHeader = (cookies || []).map(c => `${c.name}=${c.value}`).join("; ");
  } catch {}

  if (decision.isTorrent) {
    const accepted = await sendTorrentLinkToSwiftGet(url, referrer, decision.fileName, cookieHeader, windowBounds);
    if (accepted && downloadItem.id !== undefined) {
      chrome.downloads.cancel(downloadItem.id, () => {
        chrome.downloads.erase({ id: downloadItem.id });
      });
      chrome.downloads.erase({ id: downloadItem.id });
    }
    return;
  }

  const payload = {
    action: "interceptDownload",
    eventId: eventId,
    correlationId: eventId,
    createdAt: new Date().toISOString(),
    timestamp: Date.now(),
    tabId: earlyHeader?.tabId ?? -1,
    initiator: referrer || earlyHeader?.initiator || "",
    userGesture: true,
    triggerSource: triggerSource || "downloads_api",
    url: url,
    fileName: decision.fileName || "",
    referrer: referrer,
    cookies: cookieHeader,
    destinationPath: "",
    startImmediately: true,
    userAgent: navigator.userAgent,
    fileSize: downloadItem.fileSize || earlyHeader?.contentLength || 0,
    mimeType: downloadItem.mime || earlyHeader?.contentType || "",
    incognito: downloadItem.incognito || false,
    windowBounds: windowBounds
  };

  // 3. Dispatch to running SwiftGet instance.
  // Only cancel the browser's own download if SwiftGet is running and accepted the capture!
  const accepted = await dispatchToRunningSwiftGet(payload);
  if (accepted && downloadItem.id !== undefined) {
    chrome.downloads.cancel(downloadItem.id, () => {
      chrome.downloads.erase({ id: downloadItem.id });
    });
    chrome.downloads.erase({ id: downloadItem.id });
  }
}

async function dispatchToRunningSwiftGet(payload) {
  const nativeAccepted = await new Promise((resolve) => {
    let resolved = false;
    const timeout = setTimeout(() => {
      if (!resolved) {
        resolved = true;
        resolve(false);
      }
    }, 1500);

    try {
      chrome.runtime.sendNativeMessage(NATIVE_HOST_NAME, payload, (response) => {
        clearTimeout(timeout);
        if (resolved) return;
        resolved = true;
        if (chrome.runtime.lastError || !response || response.success === false || response.appNotRunning === true) {
          resolve(false);
        } else {
          resolve(true);
        }
      });
    } catch {
      clearTimeout(timeout);
      resolve(false);
    }
  });

  if (nativeAccepted) {
    return true;
  }

  // Fallback to Loopback HTTP Server (only succeeds if SwiftGet.App is actively running and listening)
  return await sendViaLoopbackFallback(payload);
}

async function sendDownloadToSwiftGet(url, referrer, fileName, cookies = "", destinationPath = "", startImmediately = true, eventId = "", meta = {}) {
  let windowBounds = null;
  try {
    if (chrome.windows) {
      const win = await chrome.windows.getLastFocused();
      if (win) {
        windowBounds = {
          left: win.left || 0,
          top: win.top || 0,
          width: win.width || 1024,
          height: win.height || 768
        };
      }
    }
  } catch {}

  const resolvedId = eventId || generateEventId();
  const payload = {
    action: "interceptDownload",
    eventId: resolvedId,
    correlationId: resolvedId,
    createdAt: new Date().toISOString(),
    timestamp: Date.now(),
    tabId: meta.tabId ?? -1,
    initiator: meta.initiator || referrer || "",
    userGesture: meta.userGesture !== false,
    explicitUserAction: meta.explicitUserAction === true,
    url: sanitizeMediaUrl(url),
    fileName: fileName || "",
    referrer: referrer || "",
    cookies: cookies || "",
    destinationPath: destinationPath || "",
    startImmediately: startImmediately,
    userAgent: navigator.userAgent,
    windowBounds: windowBounds
  };

  return await dispatchToRunningSwiftGet(payload);
}

async function sendMagnetToSwiftGet(magnetUri, referrer = "") {
  const payload = {
    action: "addMagnet",
    eventId: generateEventId(),
    createdAt: new Date().toISOString(),
    magnetUri: magnetUri,
    url: magnetUri,
    referrer: referrer || "",
    userAgent: navigator.userAgent
  };

  return await dispatchToRunningSwiftGet(payload);
}

async function sendTorrentLinkToSwiftGet(url, referrer = "", fileName = "", cookies = "", windowBounds = null) {
  let base64Payload = null;

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
      if (uint8.length > 0 && uint8[0] === 0x64) {
        let binary = "";
        const len = uint8.byteLength;
        for (let i = 0; i < len; i++) {
          binary += String.fromCharCode(uint8[i]);
        }
        base64Payload = btoa(binary);
      }
    }
  } catch {}

  const payload = {
    action: "addTorrentFile",
    eventId: generateEventId(),
    createdAt: new Date().toISOString(),
    url: url,
    fileName: fileName || "",
    referrer: referrer || "",
    cookies: cookies || "",
    userAgent: navigator.userAgent,
    windowBounds: windowBounds
  };

  if (base64Payload) {
    payload.fileBytesBase64 = base64Payload;
  }

  return await dispatchToRunningSwiftGet(payload);
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

    const headers = { "Content-Type": "application/json" };
    if (token) headers["X-SwiftGet-Token"] = token;

    let res = await fetch(LOOPBACK_URL, {
      method: "POST",
      headers: headers,
      body: JSON.stringify(payload)
    });
    if (res.status === 401) {
      try {
        const tokenRes = await fetch("http://127.0.0.1:52384/api/v1/auth/token");
        if (tokenRes.ok) {
          const tokenData = await tokenRes.json();
          if (tokenData?.authToken) {
            token = tokenData.authToken;
            await chrome.storage.local.set({ authToken: token });
            headers["X-SwiftGet-Token"] = token;
            res = await fetch(LOOPBACK_URL, {
              method: "POST",
              headers: headers,
              body: JSON.stringify(payload)
            });
          }
        }
      } catch {}
    }
    if (!res.ok) return false;
    const data = await res.json().catch(() => null);
    return data ? data.success !== false : true;
  } catch {
    return false;
  }
}

// ----------------------------------------------------
// 6. Context Menu Handler Setup
// ----------------------------------------------------
chrome.runtime.onInstalled.addListener(() => {
  sessionStart = Date.now();
  chrome.contextMenus.removeAll(() => {
    chrome.contextMenus.create({
      id: "swiftget_download_link",
      title: "Download with SwiftGet",
      contexts: ["link"]
    });

    chrome.contextMenus.create({
      id: "swiftget_download_selected",
      title: "Download selected links with SwiftGet",
      contexts: ["selection"]
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

chrome.contextMenus.onClicked.addListener(async (info, tab) => {
  const tabId = tab?.id ?? -1;
  const tabUrl = tab?.url || "";
  if (info.menuItemId === "swiftget_download_magnet" && info.linkUrl) {
    await sendMagnetToSwiftGet(info.linkUrl, tabUrl);
  } else if (info.menuItemId === "swiftget_download_torrent" && info.linkUrl) {
    let cookieHeader = "";
    try {
      const cookies = await chrome.cookies.getAll({ url: info.linkUrl });
      cookieHeader = (cookies || []).map(c => `${c.name}=${c.value}`).join("; ");
    } catch {}
    await sendTorrentLinkToSwiftGet(info.linkUrl, tabUrl, "", cookieHeader);
  } else if (info.menuItemId === "swiftget_download_link" && info.linkUrl) {
    if (info.linkUrl.startsWith("magnet:?")) {
      await sendMagnetToSwiftGet(info.linkUrl, tabUrl);
    } else {
      let cookieHeader = "";
      try {
        const cookies = await chrome.cookies.getAll({ url: info.linkUrl });
        cookieHeader = (cookies || []).map(c => `${c.name}=${c.value}`).join("; ");
      } catch {}

      if (info.linkUrl.toLowerCase().endsWith(".torrent") || info.linkUrl.toLowerCase().includes(".torrent?")) {
        await sendTorrentLinkToSwiftGet(info.linkUrl, tabUrl, "", cookieHeader);
      } else {
        await sendDownloadToSwiftGet(info.linkUrl, tabUrl, "", cookieHeader, "", true, "", {
          tabId,
          initiator: tabUrl,
          userGesture: true,
          explicitUserAction: true
        });
      }
    }
  } else if (info.menuItemId === "swiftget_download_selected" && info.selectionText) {
    const text = info.selectionText;
    const urlMatches = text.match(/\bhttps?:\/\/[^\s"'<>]+\b/gi) || [];
    const magnetMatches = text.match(/magnet:\?[^\s"'<>]+/gi) || [];

    for (const mag of magnetMatches) {
      await sendMagnetToSwiftGet(mag, tabUrl);
    }

    for (const url of urlMatches) {
      let cookieHeader = "";
      try {
        const cookies = await chrome.cookies.getAll({ url });
        cookieHeader = (cookies || []).map(c => `${c.name}=${c.value}`).join("; ");
      } catch {}

      if (url.toLowerCase().endsWith(".torrent") || url.toLowerCase().includes(".torrent?")) {
        await sendTorrentLinkToSwiftGet(url, tabUrl, "", cookieHeader);
      } else {
        await sendDownloadToSwiftGet(url, tabUrl, "", cookieHeader, "", true, "", {
          tabId,
          initiator: tabUrl,
          userGesture: true,
          explicitUserAction: true
        });
      }
    }
  } else if (info.menuItemId === "swiftget_download_media" && info.srcUrl) {
    let cookieHeader = "";
    try {
      const cookies = await chrome.cookies.getAll({ url: info.srcUrl });
      cookieHeader = (cookies || []).map(c => `${c.name}=${c.value}`).join("; ");
    } catch {}
    await sendDownloadToSwiftGet(info.srcUrl, tabUrl, "", cookieHeader, "", true, "", {
      tabId,
      initiator: tabUrl,
      userGesture: true,
      explicitUserAction: true
    });
  } else if (info.menuItemId === "swiftget_download_all" && tab?.id) {
    chrome.tabs.sendMessage(tab.id, { type: "GATHER_ALL_LINKS" }, async (response) => {
      if (response && response.links && response.links.length > 0) {
        for (const link of response.links) {
          if (link.url.startsWith("magnet:?")) {
            await sendMagnetToSwiftGet(link.url, tabUrl);
          } else if (link.url.toLowerCase().endsWith(".torrent") || link.url.toLowerCase().includes(".torrent?")) {
            await sendTorrentLinkToSwiftGet(link.url, tabUrl, link.text);
          } else {
            await sendDownloadToSwiftGet(link.url, tabUrl, link.text, "", "", true, "", {
              tabId,
              initiator: tabUrl,
              userGesture: true,
              explicitUserAction: true
            });
          }
        }
      }
    });
  }
});
