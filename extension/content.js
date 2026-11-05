// SwiftGet Content Script - Alt/Insert Key Tracker, Link Grabber & Floating Media Sniffer (4-Stage IDM Flow)

let isAltHeld = false;
let isInsertHeld = false;
let isSnifferEnabled = true;
let userExcludedHosts = [];

// DRM-protected streaming services. Their streams are encrypted (Widevine/PlayReady/FairPlay),
// SwiftGet never circumvents DRM, so the "Download this video/audio" overlay is never shown here.
// This list is built in and always applies, independent of the user-editable exclusion list.
const DRM_STREAMING_HOSTS = [
  "netflix.com", "primevideo.com", "disneyplus.com", "hotstar.com", "hulu.com",
  "max.com", "hbomax.com", "tv.apple.com", "music.apple.com", "peacocktv.com",
  "paramountplus.com", "crunchyroll.com", "spotify.com", "tidal.com", "deezer.com",
  "dazn.com", "starplus.com", "starzplay.com", "osnplus.com", "shahid.mbc.net",
  "zee5.com", "sonyliv.com", "jiocinema.com", "mubi.com", "showmax.com",
  "stan.com.au", "canalplus.com", "nowtv.com", "skyshowtime.com", "play.max.com"
];

// Prime Video is also served from regular Amazon storefront domains (amazon.com, amazon.co.uk, ...).
const AMAZON_VIDEO_PATH_PATTERN = /\/(gp\/video|prime-?video|amazon-?video)(\/|$|\?)/i;

function hostMatchesList(host, list) {
  if (!host) return false;
  const h = host.toLowerCase().replace(/\.$/, "");
  return list.some(d => h === d || h.endsWith("." + d));
}

function isAmazonPrimeVideoUrl(host, path) {
  if (!host) return false;
  const h = host.toLowerCase();
  return /(^|\.)amazon\.[a-z.]+$/.test(h) && AMAZON_VIDEO_PATH_PATTERN.test(path || "");
}

function isOverlayBlockedOnThisPage() {
  try {
    const loc = (typeof window !== "undefined" && window.location) ? window.location : null;
    if (!loc || !loc.href) return false;
    const url = new URL(loc.href);
    const hosts = [url.hostname];
    // Embedded player iframes inherit the block from the page that hosts them
    if (loc.ancestorOrigins && typeof loc.ancestorOrigins.length === "number") {
      for (let i = 0; i < loc.ancestorOrigins.length; i++) {
        try { hosts.push(new URL(loc.ancestorOrigins[i]).hostname); } catch {}
      }
    }
    for (const h of hosts) {
      if (hostMatchesList(h, DRM_STREAMING_HOSTS)) return true;
      if (hostMatchesList(h, userExcludedHosts)) return true;
    }
    if (isAmazonPrimeVideoUrl(url.hostname, url.pathname)) return true;
  } catch {}
  return false;
}

// Only plain host entries from the user exclusion list apply to the overlay
// (wildcard/path patterns such as "*/api/*" are download-capture rules, not sites).
function parseUserExcludedHosts(list) {
  if (!Array.isArray(list)) return [];
  return list
    .map(e => String(e || "").trim().toLowerCase())
    .filter(e => e && !e.includes("*") && !e.includes("/") && !e.includes(":") && e.includes("."));
}

function removeAllOverlayButtons() {
  try {
    if (typeof document === "undefined" || !document.querySelectorAll) return;
    document.querySelectorAll(".swiftget-video-overlay-btn").forEach(b => { try { b.remove(); } catch {} });
    document.querySelectorAll(".swiftget-video-dropdown").forEach(b => { try { b.remove(); } catch {} });
    document.querySelectorAll("video, audio").forEach(m => {
      m._swiftgetOverlayBtn = null;
      m._swiftgetDropdown = null;
      if (m.dataset) delete m.dataset.swiftgetAttached;
    });
  } catch {}
}

function closeAllMediaDropdowns() {
  try {
    if (typeof document === "undefined" || !document.querySelectorAll) return;
    document.querySelectorAll(".swiftget-video-dropdown").forEach(d => {
      try { d.remove(); } catch {}
    });
    document.querySelectorAll("video, audio").forEach(m => {
      m._swiftgetDropdown = null;
    });
  } catch {}
}

try {
  const api = typeof chrome !== "undefined" ? chrome : (typeof browser !== "undefined" ? browser : null);
  if (api && api.storage && api.storage.local && typeof api.storage.local.get === "function") {
    const p = api.storage.local.get(["snifferEnabled", "blacklist"]);
    if (p && typeof p.then === "function") {
      p.then((st) => {
        if (st && st.snifferEnabled === false) {
          isSnifferEnabled = false;
        }
        if (st && Array.isArray(st.blacklist)) {
          userExcludedHosts = parseUserExcludedHosts(st.blacklist);
          if (isOverlayBlockedOnThisPage()) removeAllOverlayButtons();
        }
      }).catch(() => {});
    }
  }
  if (api && api.storage && api.storage.onChanged && typeof api.storage.onChanged.addListener === "function") {
    api.storage.onChanged.addListener((changes) => {
      if (changes && changes.blacklist) {
        userExcludedHosts = parseUserExcludedHosts(changes.blacklist.newValue);
        if (isOverlayBlockedOnThisPage()) removeAllOverlayButtons();
        else scanAndAttachVideoOverlays();
      }
      if (changes && changes.snifferEnabled) {
        isSnifferEnabled = changes.snifferEnabled.newValue !== false;
        if (isSnifferEnabled) {
          scanAndAttachVideoOverlays();
        } else {
          removeAllOverlayButtons();
        }
      }
    });
  }
} catch {}

function getExtensionApi() {
  return typeof chrome !== "undefined" ? chrome : (typeof browser !== "undefined" ? browser : null);
}

if (typeof window !== "undefined") {
  window.addEventListener("keydown", (e) => {
    if (e.key === "Alt" && !isAltHeld) {
      isAltHeld = true;
      notifyModifierState("ALT_KEY_STATE", true);
    }
    if (e.key === "Insert" && !isInsertHeld) {
      isInsertHeld = true;
      notifyModifierState("INSERT_KEY_STATE", true);
    }
  }, true);

  window.addEventListener("keyup", (e) => {
    if (e.key === "Alt" && isAltHeld) {
      isAltHeld = false;
      notifyModifierState("ALT_KEY_STATE", false);
    }
    if (e.key === "Insert" && isInsertHeld) {
      isInsertHeld = false;
      notifyModifierState("INSERT_KEY_STATE", false);
    }
  }, true);

  window.addEventListener("blur", () => {
    if (isAltHeld) {
      isAltHeld = false;
      notifyModifierState("ALT_KEY_STATE", false);
    }
    if (isInsertHeld) {
      isInsertHeld = false;
      notifyModifierState("INSERT_KEY_STATE", false);
    }
  }, true);
}

function notifyModifierState(type, isHeld) {
  try {
    const api = getExtensionApi();
    if (api && api.runtime && api.runtime.sendMessage) {
      api.runtime.sendMessage({
        type: type,
        isHeld: isHeld
      });
    }
  } catch {
    // Ignore runtime disconnected errors
  }
}

function notifyAltState(isHeld) {
  notifyModifierState("ALT_KEY_STATE", isHeld);
}

// ----------------------------------------------------
// Click Interception: Alt Bypass, Insert Force, Torrents & Magnets
// ----------------------------------------------------
function handleDocumentClick(e) {
  // Alt key bypass: permit browser native transfer unconditionally
  if (e.altKey || isAltHeld) {
    return;
  }

  const anchor = e.target && e.target.closest ? e.target.closest("a[href]") : null;
  if (!anchor) return;

  const rawHref = anchor.getAttribute("href");
  if (!rawHref) return;

  const href = rawHref.trim();
  const lowerHref = href.toLowerCase();

  // 0. Insert key held: force SwiftGet interception on ANY clicked link
  if (isInsertHeld) {
    try {
      const base = (typeof document !== "undefined" && document.baseURI) || (typeof window !== "undefined" && window.location && window.location.href) || "";
      const resolvedUrl = new URL(href, base).href;
      if (resolvedUrl.startsWith("http://") || resolvedUrl.startsWith("https://") || resolvedUrl.startsWith("ftp://")) {
        e.preventDefault();
        e.stopPropagation();
        const api = getExtensionApi();
        if (api && api.runtime && api.runtime.sendMessage) {
          api.runtime.sendMessage({
            type: "FORCE_INTERCEPT_LINK",
            url: resolvedUrl,
            text: (anchor.textContent || anchor.title || "").trim()
          });
        }
        return;
      }
    } catch {}
  }

  // 1. Magnet link interception
  if (lowerHref.startsWith("magnet:?")) {
    e.preventDefault();
    e.stopPropagation();
    try {
      const api = getExtensionApi();
      if (api && api.runtime && api.runtime.sendMessage) {
        api.runtime.sendMessage({
          type: "INTERCEPT_MAGNET",
          url: href,
          text: (anchor.textContent || anchor.title || "").trim()
        });
      }
    } catch (err) {
      console.warn("[SwiftGet] Failed to dispatch magnet link to extension worker:", err);
    }
    return;
  }

  // 2. Direct .torrent file link click interception
  if (lowerHref.endsWith(".torrent") || lowerHref.includes(".torrent?")) {
    try {
      const base = (typeof document !== "undefined" && document.baseURI) || (typeof window !== "undefined" && window.location && window.location.href) || "";
      const resolvedUrl = new URL(href, base).href;
      if (resolvedUrl.startsWith("http://") || resolvedUrl.startsWith("https://")) {
        e.preventDefault();
        e.stopPropagation();
        const api = getExtensionApi();
        if (api && api.runtime && api.runtime.sendMessage) {
          api.runtime.sendMessage({
            type: "INTERCEPT_TORRENT_LINK",
            url: resolvedUrl,
            text: (anchor.textContent || anchor.title || "").trim()
          });
        }
      }
    } catch {
      // Ignore URL resolution failure
    }
  }
}

if (typeof window !== "undefined") {
  window.addEventListener("click", handleDocumentClick, true);
}

// ----------------------------------------------------
// Link Grabber & Classification
// ----------------------------------------------------
function classifyLinkCategory(url) {
  if (!url) return "general";
  const lower = url.toLowerCase().split(/[?#]/)[0];
  if (lower.startsWith("magnet:?")) return "torrent";

  const extMatch = lower.match(/\.([a-z0-9]{2,5})$/);
  const ext = extMatch ? "." + extMatch[1] : "";

  const videoExts = [".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".ts", ".m3u8", ".mpd"];
  const audioExts = [".mp3", ".m4a", ".aac", ".wav", ".flac", ".ogg", ".wma", ".opus"];
  const archiveExts = [".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz", ".iso", ".dmg", ".cab"];
  const docExts = [".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".rtf", ".epub"];
  const imageExts = [".jpg", ".jpeg", ".png", ".gif", ".webp", ".svg", ".bmp", ".ico", ".tiff"];
  const torrentExts = [".torrent"];

  if (videoExts.includes(ext)) return "video";
  if (audioExts.includes(ext)) return "audio";
  if (archiveExts.includes(ext)) return "archive";
  if (docExts.includes(ext)) return "document";
  if (imageExts.includes(ext)) return "image";
  if (torrentExts.includes(ext)) return "torrent";

  return "general";
}

if (typeof chrome !== "undefined" && chrome.runtime && chrome.runtime.onMessage) {
  chrome.runtime.onMessage.addListener((request, sender, sendResponse) => {
    if (request && request.type === "GATHER_ALL_LINKS" && typeof document !== "undefined") {
      const candidates = [];
      if (document.querySelectorAll) {
        const anchors = document.querySelectorAll("a[href], area[href]");
        anchors.forEach(a => {
          candidates.push({
            rawHref: a.getAttribute("href") || a.href,
            text: (a.textContent || a.title || a.alt || "").trim()
          });
        });

        // Also discover embedded media sources
        const mediaSources = document.querySelectorAll("video source[src], audio source[src]");
        mediaSources.forEach(s => {
          const src = s.getAttribute("src");
          if (src) {
            candidates.push({
              rawHref: src,
              text: (s.getAttribute("title") || s.parentElement?.title || "Media Stream").trim()
            });
          }
        });
      }

      const seen = new Set();
      const links = [];
      const base = document.baseURI || (typeof window !== "undefined" && window.location && window.location.href) || "";

      for (const item of candidates) {
        const raw = (item.rawHref || "").trim();
        if (!raw || raw.startsWith("#") || raw.startsWith("javascript:") || raw.startsWith("mailto:") || raw.startsWith("tel:")) {
          continue;
        }

        try {
          const url = raw.startsWith("magnet:?") ? raw : new URL(raw, base).href;
          if ((url.startsWith("http://") || url.startsWith("https://") || url.startsWith("magnet:?")) && !seen.has(url)) {
            const extMatch = url.split(/[?#]/)[0].match(/\.([a-z0-9]{2,5})$/i);
            const ext = extMatch ? "." + extMatch[1].toLowerCase() : "";
            const category = classifyLinkCategory(url);
            // Only gather downloadable file links or magnet/torrent links, not generic navigation pages
            if (url.startsWith("magnet:?") || category !== "general" || (ext && ![".html", ".htm", ".php", ".asp", ".aspx", ".jsp"].includes(ext))) {
              seen.add(url);
              links.push({
                url: url,
                text: item.text,
                ext: ext,
                category: category
              });
            }
          }
        } catch {
          // Ignore invalid URLs
        }
      }

      sendResponse({ links: links });
    }
  });
}

// ----------------------------------------------------
// SVG Icons Definitions (Embedded for Zero Dependencies)
// ----------------------------------------------------
const SG_ICONS = {
  downloadTray: `<svg viewBox="0 0 24 24"><path d="M19 9h-4V3H9v6H5l7 7 7-7zM5 18v2h14v-2H5z"/></svg>`,
  appLogo: `<svg viewBox="0 0 24 24"><circle cx="12" cy="12" r="10" fill="#0078d4"/><path d="M12 4a8 8 0 100 16 8 8 0 000-16zm-1 4h2v5.5l2.5-2.5 1.4 1.4-4.9 4.9-4.9-4.9 1.4-1.4 2.5 2.5V8z" fill="#ffffff"/></svg>`,
  vlcCone: `<svg viewBox="0 0 48 48">
    <ellipse cx="24" cy="42" rx="18" ry="4" fill="#ff6a00"/>
    <path d="M12 41 L19 28 L29 28 L36 41 Z" fill="#ff8c00"/>
    <path d="M15 35 L18 29 L30 29 L33 35 Z" fill="#ffffff"/>
    <path d="M19 28 L21 16 L27 16 L29 28 Z" fill="#ff7900"/>
    <path d="M20.5 22 L22 17 L26 17 L27.5 22 Z" fill="#ffffff"/>
    <path d="M21 16 L23 8 L25 8 L27 16 Z" fill="#ff9900"/>
    <ellipse cx="24" cy="8" rx="2" ry="1" fill="#ffaa33"/>
  </svg>`,
  yellowEnvelope: `<svg viewBox="0 0 56 56">
    <path d="M6 20 L28 34 L50 20 L50 46 C50 48 48 50 46 50 L10 50 C8 50 6 48 6 46 Z" fill="#fbc02d"/>
    <path d="M14 6 L42 6 C44 6 45 7 45 9 L45 28 L11 28 L11 9 C11 7 12 6 14 6 Z" fill="#42a5f5"/>
    <path d="M16 12 L36 12" stroke="#ffffff" stroke-width="2" stroke-linecap="round"/>
    <path d="M16 17 L32 17" stroke="#ffffff" stroke-width="2" stroke-linecap="round"/>
    <path d="M16 22 L26 22" stroke="#ffffff" stroke-width="2" stroke-linecap="round"/>
    <path d="M6 20 L28 34 L50 20" stroke="#f57f17" stroke-width="2" fill="none"/>
    <path d="M6 20 L28 8 L50 20" fill="#fdd835"/>
  </svg>`,
  dragBadge: `<svg viewBox="0 0 28 28">
    <path d="M14 2 L17 6 L15 6 L15 13 L22 13 L22 11 L26 14 L22 17 L22 15 L15 15 L15 22 L17 22 L14 26 L11 22 L13 22 L13 15 L6 15 L6 17 L2 14 L6 11 L6 13 L13 13 L13 6 L11 6 Z" fill="#e65100"/>
  </svg>`
};

// ----------------------------------------------------
// Media Sniffer Floating Overlay ( Video & Audio )
// ----------------------------------------------------
function resolveOverlayHost(media) {
  if (!media) return (typeof document !== "undefined" && document.body) || null;

  // Prefer the outer player container so the button sits above all internal player overlays
  // (e.g., YouTube's .ytp-chrome-top, .ytp-iv-video-content, .ytp-cued-thumbnail-overlay, click-catchers)
  if (typeof media.closest === "function") {
    try {
      const playerRoot = media.closest(
        "#movie_player, .html5-video-player, .video-js, .plyr, .jwplayer, .flowplayer, .shaka-video-container, [data-testid='videoPlayer'], [data-player]"
      );
      if (playerRoot) {
        return playerRoot;
      }
    } catch {}
  }

  let current = media.parentElement;
  if (current && typeof window !== "undefined" && typeof window.getComputedStyle === "function" && typeof media.getBoundingClientRect === "function") {
    try {
      const mediaRect = media.getBoundingClientRect();
      for (let depth = 0; depth < 4 && current && current.parentElement && current.parentElement !== document.body && current.parentElement !== document.documentElement; depth++) {
        const cs = window.getComputedStyle(current);
        const curRect = typeof current.getBoundingClientRect === "function" ? current.getBoundingClientRect() : null;
        const parentRect = typeof current.parentElement.getBoundingClientRect === "function" ? current.parentElement.getBoundingClientRect() : null;
        const isZeroOrPointerNone = cs.pointerEvents === "none" || (curRect && curRect.height < 20);
        const isSameBox = parentRect && mediaRect.width > 0 &&
          Math.abs(parentRect.width - mediaRect.width) <= 48 &&
          Math.abs(parentRect.height - mediaRect.height) <= 48;
        if (isZeroOrPointerNone || isSameBox) {
          current = current.parentElement;
        } else {
          break;
        }
      }
    } catch {}
  }

  return current || media.parentElement || (typeof document !== "undefined" && document.body) || null;
}

function resolveMediaSourceAndTitle(media) {
  const isAudio = media && media.tagName && media.tagName.toLowerCase() === "audio";
  let rawTitle = (typeof document !== "undefined" && document.title) || (isAudio ? "Audio Track" : "Video");
  rawTitle = rawTitle.replace(/^\(\d+\)\s*/, "").replace(/\s*-\s*YouTube$/i, "").replace(/\s*-\s*Vimeo$/i, "").trim();
  if (!rawTitle) rawTitle = isAudio ? "Online Audio Stream" : "Online Video Stream";

  const detectedUrls = [];
  if (media && media.querySelectorAll) {
    try {
      const sources = media.querySelectorAll("source");
      sources.forEach(s => {
        const src = s.getAttribute("src");
        if (src && !src.startsWith("blob:")) {
          try {
            const resolved = new URL(src, document.baseURI || window.location.href).href;
            if (!detectedUrls.includes(resolved)) detectedUrls.push(resolved);
          } catch {}
        }
      });
    } catch {}
  }

  if (typeof performance !== "undefined" && performance.getEntriesByType) {
    try {
      const entries = performance.getEntriesByType("resource");
      for (let i = entries.length - 1; i >= 0 && detectedUrls.length < 5; i--) {
        const name = entries[i].name || "";
        const lower = name.toLowerCase();
        if (lower.includes(".m3u8") || lower.includes(".mpd") || lower.includes(".mp4") ||
            lower.includes(".webm") || lower.includes(".m4a") || lower.includes(".ts") ||
            lower.includes(".aac") || lower.includes(".mp3")) {
          if (!detectedUrls.includes(name)) {
            detectedUrls.push(name);
          }
        }
      }
    } catch {}
  }

  let mediaUrl = media ? (media.currentSrc || media.src) : "";
  if (!mediaUrl || mediaUrl.startsWith("blob:")) {
    mediaUrl = detectedUrls.length > 0 ? detectedUrls[0] : ((typeof window !== "undefined" && window.location && window.location.href) || "https://example.com/video");
  }

  return { mediaUrl, rawTitle, isAudio };
}

function triggerMediaDownloadForElement(media, btn) {
  const now = Date.now();
  if (btn && btn._lastTriggerMs && (now - btn._lastTriggerMs) < 450) {
    return;
  }
  if (btn) {
    btn._lastTriggerMs = now;
  }

  const { mediaUrl, rawTitle, isAudio } = resolveMediaSourceAndTitle(media);
  dispatchMediaDownload(mediaUrl, rawTitle, "best");

  if (btn && btn.style) {
    try {
      const defaultLabel = isAudio ? "Download this audio" : "Download this video";
      btn.innerHTML = `${SG_ICONS.downloadTray}<span>Opening SwiftGet...</span>`;
      if (typeof setTimeout === "function") {
        setTimeout(() => {
          try {
            btn.innerHTML = `${SG_ICONS.downloadTray}<span>${defaultLabel}</span>`;
          } catch {}
        }, 1000);
      }
    } catch {}
  }
}

function scanAndAttachVideoOverlays() {
  if (!isSnifferEnabled) return;
  if (typeof document === "undefined" || !document.querySelectorAll) return;
  // Single-page apps (e.g. Amazon storefront -> Prime Video) can navigate into a blocked page
  // without reloading, so re-check on every scan and strip any button that is already attached.
  if (isOverlayBlockedOnThisPage()) {
    removeAllOverlayButtons();
    return;
  }

  const mediaElements = document.querySelectorAll("video, audio");
  mediaElements.forEach(media => {
    const isAudio = media.tagName.toLowerCase() === "audio";

    // Bind media lifecycle listeners once so 0x0 lazy-loaded videos attach as soon as they become active
    if (!media._swiftgetLifecycleBound && typeof media.addEventListener === "function") {
      media._swiftgetLifecycleBound = true;
      const rescan = () => scanAndAttachVideoOverlays();
      media.addEventListener("loadedmetadata", rescan);
      media.addEventListener("play", rescan);
      media.addEventListener("mouseenter", rescan);
    }

    // Minimum size check for video (skip tiny trackers/invisible elements)
    if (!isAudio && typeof media.getBoundingClientRect === "function") {
      const rect = media.getBoundingClientRect();
      if (rect.width < 120 || rect.height < 90) return;
    }

    const parent = resolveOverlayHost(media);
    if (!parent || typeof parent.appendChild !== "function") return;

    // Check if an overlay button is already attached and still live in the target container
    const existingBtn = media._swiftgetOverlayBtn;
    if (media.dataset && media.dataset.swiftgetAttached === "true" && existingBtn) {
      const stillConnected = existingBtn.isConnected !== false;
      const sameParent = !existingBtn.parentElement || existingBtn.parentElement === parent;
      if (stillConnected && sameParent) {
        // Keep button as the last child of the player container so dynamic overlays don't cover it
        if (parent.lastElementChild && parent.lastElementChild !== existingBtn) {
          try {
            parent.appendChild(existingBtn);
          } catch {}
        }
        return;
      }
      try {
        if ( typeof existingBtn.remove === "function") existingBtn.remove();
      } catch {}
    }

    if (media.dataset) {
      media.dataset.swiftgetAttached = "true";
    }

    if (typeof window !== "undefined" && window.getComputedStyle && parent.style) {
      try {
        if (window.getComputedStyle(parent).position === "static") {
          parent.style.position = "relative";
        }
      } catch {}
    }

    // Floating Overlay Button
    const btn = document.createElement("button");
    btn.className = "swiftget-video-overlay-btn" + (isAudio ? " swiftget-audio-overlay-btn" : "");
    btn.setAttribute("type", "button");
    if (btn.style) {
      btn.style.pointerEvents = "auto";
      btn.style.zIndex = "2147483647";
      btn.style.cursor = "pointer";
    }
    btn.innerHTML = `${SG_ICONS.downloadTray}<span>${isAudio ? "Download this audio" : "Download this video"}</span>`;

    const activateHandler = (e) => {
      if (e) {
        if (e._swiftgetHandled) return;
        e._swiftgetHandled = true;
        if (typeof e.preventDefault === "function") e.preventDefault();
        if (typeof e.stopPropagation === "function") e.stopPropagation();
        if (typeof e.stopImmediatePropagation === "function") e.stopImmediatePropagation();
      }

      triggerMediaDownloadForElement(media, btn);
    };

    btn._swiftgetActivate = activateHandler;
    media._swiftgetOverlayBtn = btn;

    const shieldEvent = (e) => {
      if (!e) return;
      if (typeof e.stopPropagation === "function") e.stopPropagation();
      if (typeof e.stopImmediatePropagation === "function") e.stopImmediatePropagation();
    };

    if (typeof btn.addEventListener === "function") {
      // Stop pointerdown / mousedown / mouseup / touchstart from reaching video player controls
      btn.addEventListener("pointerdown", shieldEvent, true);
      btn.addEventListener("pointerdown", shieldEvent, false);
      btn.addEventListener("mousedown", shieldEvent, true);
      btn.addEventListener("mousedown", shieldEvent, false);
      btn.addEventListener("mouseup", shieldEvent, true);
      btn.addEventListener("mouseup", shieldEvent, false);
      btn.addEventListener("touchstart", shieldEvent, true);
      btn.addEventListener("touchend", shieldEvent, true);
      btn.addEventListener("dblclick", shieldEvent, true);
      btn.addEventListener("click", activateHandler);
    }

    parent.appendChild(btn);
  });
}

// Window-level capture shield so video player capture listeners (e.g. YouTube #movie_player)
// can never intercept or block clicks on the overlay button or its quality dropdown
function findOverlayButtonFromEvent(e) {
  if (!e) return null;
  if (typeof e.composedPath === "function") {
    try {
      const path = e.composedPath();
      for (const node of path) {
        if (node && node.className && typeof node.className === "string") {
          if (node.className.includes("swiftget-video-overlay-btn") ||
              node.className.includes("swiftget-video-dropdown") ||
              node.className.includes("swiftget-dropdown-item")) {
            return node;
          }
        }
      }
    } catch {}
  }
  const t = e.target;
  if (!t) return null;
  if (t.className && typeof t.className === "string" &&
      (t.className.includes("swiftget-video-overlay-btn") ||
       t.className.includes("swiftget-video-dropdown") ||
       t.className.includes("swiftget-dropdown-item"))) {
    return t;
  }
  if (typeof t.closest === "function") {
    try {
      const btn = t.closest(".swiftget-video-overlay-btn");
      if (btn) return btn;
      const dd = t.closest(".swiftget-video-dropdown");
      if (dd) return dd;
      const item = t.closest(".swiftget-dropdown-item");
      if (item) return item;
    } catch {}
  }
  return null;
}

if (typeof window !== "undefined" && typeof window.addEventListener === "function") {
  for (const evtName of ["pointerdown", "mousedown", "mouseup", "touchstart", "touchend", "dblclick"]) {
    window.addEventListener(evtName, (e) => {
      const el = findOverlayButtonFromEvent(e);
      if (el) {
        if (typeof e.stopPropagation === "function") e.stopPropagation();
        if (typeof e.stopImmediatePropagation === "function") e.stopImmediatePropagation();
      }
    }, true);
  }

  window.addEventListener("click", (e) => {
    const el = findOverlayButtonFromEvent(e);
    if (el) {
      if (typeof e.stopPropagation === "function") e.stopPropagation();
      if (typeof e.stopImmediatePropagation === "function") e.stopImmediatePropagation();
      if (el.className && el.className.includes("swiftget-video-overlay-btn") && typeof el._swiftgetActivate === "function") {
        el._swiftgetActivate(e);
      }
    } else {
      closeAllMediaDropdowns();
    }
  }, true);
}

// ----------------------------------------------------
// Stage 2: Create Media Quality Dropdown Menu
// ----------------------------------------------------
function createVideoDropdown(media, closeCallback) {
  const container = document.createElement("div");
  container.className = "swiftget-video-dropdown";
  if (container.style) {
    container.style.zIndex = "2147483647";
    container.style.pointerEvents = "auto";
  }
  const { mediaUrl, rawTitle, isAudio } = resolveMediaSourceAndTitle(media);
  const truncatedTitle = rawTitle.length > 36 ? rawTitle.substring(0, 36) + "..." : rawTitle;

  const defaultVideoStreams = [
    { label: "1080p Full HD", ext: "MP4 file", quality: "1080p" },
    { label: "720p HD", ext: "MP4 file", quality: "720p" },
    { label: "480p", ext: "MP4 file", quality: "480p" },
    { label: "360p", ext: "MP4 file", quality: "360p" },
    { label: "Best Available Quality", ext: "MP4 file", quality: "best" }
  ];

  const defaultAudioStreams = [
    { label: "High Quality Audio (320 kbps)", ext: "M4A audio", quality: "audio_hq" },
    { label: "Standard Audio (128 kbps)", ext: "MP3 audio", quality: "audio" }
  ];

  const streams = isAudio ? defaultAudioStreams : defaultVideoStreams;

  const allItem = document.createElement("div");
  allItem.className = "swiftget-dropdown-item all-item";
  allItem.textContent = "Download all";
  allItem.addEventListener("click", (e) => {
    e.stopPropagation();
    if (closeCallback) closeCallback();
    closeAllMediaDropdowns();
    dispatchMediaDownload(mediaUrl, rawTitle, "all");
  });
  container.appendChild(allItem);

  const divider = document.createElement("div");
  divider.className = "swiftget-dropdown-divider";
  container.appendChild(divider);

  streams.forEach((stream, idx) => {
    const item = document.createElement("div");
    item.className = "swiftget-dropdown-item";

    const titleSpan = document.createElement("span");
    titleSpan.className = "sg-title";
    titleSpan.textContent = `${idx + 1}. ${truncatedTitle}`;

    const formatSpan = document.createElement("span");
    formatSpan.className = "sg-format";
    formatSpan.textContent = `${stream.ext}, ${stream.label}`;

    item.appendChild(titleSpan);
    item.appendChild(formatSpan);

    item.addEventListener("click", (e) => {
      e.stopPropagation();
      if (closeCallback) closeCallback();
      closeAllMediaDropdowns();
      dispatchMediaDownload(mediaUrl, rawTitle, stream.quality);
    });

    container.appendChild(item);
  });

  return container;
}

function cleanYouTubeMixUrl(rawUrl) {
  if (!rawUrl) return "";
  try {
    const u = new URL(rawUrl);
    if (u.hostname.toLowerCase().includes("youtube.com") || u.hostname.toLowerCase().includes("youtu.be")) {
      const listParam = u.searchParams.get("list") || "";
      if (listParam.toUpperCase().startsWith("RD") || u.searchParams.has("v")) {
        u.searchParams.delete("list");
        u.searchParams.delete("start_radio");
        u.searchParams.delete("index");
        u.searchParams.delete("pp");
      }
      return u.toString();
    }
    return rawUrl;
  } catch {
    return rawUrl;
  }
}

async function sendMediaViaLoopbackDirect(targetUrl, pageUrl, title, correlationId, quality) {
  if (typeof fetch !== "function") return;
  try {
    let token = "";
    try {
      const tokenRes = await fetch("http://127.0.0.1:52384/api/v1/auth/token");
      if (tokenRes.ok) {
        const tokenData = await tokenRes.json();
        token = tokenData?.authToken || "";
      }
    } catch {}

    const headers = { "Content-Type": "application/json" };
    if (token) headers["X-SwiftGet-Token"] = token;

    await fetch("http://127.0.0.1:52384/api/v1/downloads", {
      method: "POST",
      headers,
      body: JSON.stringify({
        url: targetUrl,
        referrer: pageUrl,
        initiator: pageUrl,
        fileName: title || "",
        eventId: correlationId,
        createdAtUtc: new Date().toISOString(),
        userGesture: true,
        explicitUserAction: true,
        startImmediately: true,
        showDialog: true,
        quality: quality || "best"
      })
    });
  } catch {}
}

function dispatchMediaDownload(url, title, quality) {
  const correlationId = "corr_" + Date.now() + "_" + Math.random().toString(36).substring(2, 10);
  const rawPageUrl = (typeof window !== "undefined" && window.location && window.location.href) || "";
  const pageUrl = cleanYouTubeMixUrl(rawPageUrl);

  // If on a recognized streaming site (YouTube, etc.), pass pageUrl so yt-dlp gets the full video formats
  const lowerPage = pageUrl.toLowerCase();
  const isVideoPlatform = lowerPage.includes("youtube.com") || lowerPage.includes("youtu.be") ||
                          lowerPage.includes("vimeo.com") || lowerPage.includes("dailymotion.com") ||
                          lowerPage.includes("tiktok.com") || lowerPage.includes("twitch.tv") ||
                          lowerPage.includes("bilibili.com") || lowerPage.includes("soundcloud.com");
  const targetUrl = isVideoPlatform ? pageUrl : cleanYouTubeMixUrl(url || pageUrl);

  const api = getExtensionApi();
  try {
    if (api && api.runtime && typeof api.runtime.sendMessage === "function") {
      api.runtime.sendMessage({
        type: "INTERCEPT_MEDIA",
        url: targetUrl,
        pageUrl: pageUrl,
        title: title || "",
        quality: quality || "best",
        correlationId: correlationId,
        createdAt: new Date().toISOString(),
        userGesture: true,
        explicitUserAction: true,
        startImmediately: true
      }, (resp) => {
        if ((api.runtime && api.runtime.lastError) || (resp && resp.success === false)) {
          sendMediaViaLoopbackDirect(targetUrl, pageUrl, title, correlationId, quality);
        }
      });
      return;
    }
  } catch (err) {
    console.warn("[SwiftGet] Extension runtime sendMessage failed, falling back to loopback:", err);
  }

  sendMediaViaLoopbackDirect(targetUrl, pageUrl, title, correlationId, quality);
}

// ----------------------------------------------------
// Initialization & Observers
// ----------------------------------------------------
if (typeof document !== "undefined") {
  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", () => {
      scanAndAttachVideoOverlays();
    });
  } else {
    scanAndAttachVideoOverlays();
  }

  document.addEventListener("fullscreenchange", () => {
    scanAndAttachVideoOverlays();
  });
  document.addEventListener("yt-navigate-finish", () => {
    scanAndAttachVideoOverlays();
  });

  // Active MutationObserver for instant detection of dynamically mounted video players
  if (typeof MutationObserver !== "undefined") {
    try {
      const observer = new MutationObserver((mutations) => {
        let shouldScan = false;
        for (const m of mutations) {
          if (m.addedNodes && m.addedNodes.length > 0) {
            for (const node of m.addedNodes) {
              if (node.nodeType === 1) {
                if (node.tagName === "VIDEO" || node.tagName === "AUDIO" || (node.querySelector && node.querySelector("video, audio"))) {
                  shouldScan = true;
                  break;
                }
              }
            }
          }
          if (shouldScan) break;
        }
        if (shouldScan) {
          scanAndAttachVideoOverlays();
        }
      });

      const rootTarget = document.documentElement || document.body;
      if (rootTarget) {
        observer.observe(rootTarget, { childList: true, subtree: true });
      }
    } catch {}
  }
}

if (typeof window !== "undefined" && typeof window.setInterval === "function") {
  window.setInterval(scanAndAttachVideoOverlays, 1500);
}
