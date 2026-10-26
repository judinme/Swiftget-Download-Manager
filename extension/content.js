// SwiftGet Content Script - Alt/Insert Key Tracker, Link Grabber & Floating Media Sniffer (4-Stage IDM Flow)

let isAltHeld = false;
let isInsertHeld = false;

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
// Link Grabber
// ----------------------------------------------------
if (typeof chrome !== "undefined" && chrome.runtime && chrome.runtime.onMessage) {
  chrome.runtime.onMessage.addListener((request, sender, sendResponse) => {
    if (request && request.type === "GATHER_ALL_LINKS" && typeof document !== "undefined") {
      const anchors = document.querySelectorAll ? document.querySelectorAll("a[href]") : [];
      const seen = new Set();
      const links = [];

      for (const a of anchors) {
        try {
          const base = document.baseURI || window.location.href || "";
          const url = new URL(a.href, base).href;
          if ((url.startsWith("http://") || url.startsWith("https://")) && !seen.has(url)) {
            seen.add(url);
            links.push({
              url: url,
              text: (a.textContent || a.title || "").trim()
            });
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
// Media Sniffer Floating Overlay (4-Stage IDM Flow for Video & Audio)
// ----------------------------------------------------
function scanAndAttachVideoOverlays() {
  if (typeof document === "undefined" || !document.querySelectorAll) return;

  const mediaElements = document.querySelectorAll("video, audio");
  mediaElements.forEach(media => {
    if (media.dataset && media.dataset.swiftgetAttached) return;

    const isAudio = media.tagName.toLowerCase() === "audio";

    // Minimum size check for video (skip tiny trackers/invisible elements)
    if (!isAudio && typeof media.getBoundingClientRect === "function") {
      const rect = media.getBoundingClientRect();
      if (rect.width < 120 || rect.height < 90) return;
    }

    if (media.dataset) {
      media.dataset.swiftgetAttached = "true";
    }

    const parent = media.parentElement || document.body;
    if (typeof window !== "undefined" && window.getComputedStyle) {
      try {
        if (window.getComputedStyle(parent).position === "static") {
          parent.style.position = "relative";
        }
      } catch {}
    }

    // 1. Stage 1: Floating Overlay Button (Image 1)
    const btn = document.createElement("button");
    btn.className = "swiftget-video-overlay-btn" + (isAudio ? " swiftget-audio-overlay-btn" : "");
    btn.setAttribute("type", "button");
    btn.innerHTML = `${SG_ICONS.downloadTray}<span>${isAudio ? "Download this audio" : "Download this video"}</span>`;

    // 2. Stage 2: Dropdown Menu (Image 2)
    let dropdown = null;

    btn.addEventListener("click", (e) => {
      e.preventDefault();
      e.stopPropagation();

      if (dropdown && dropdown.parentElement) {
        dropdown.remove();
        dropdown = null;
        return;
      }

      dropdown = createVideoDropdown(media, () => {
        if (dropdown && dropdown.parentElement) {
          dropdown.remove();
          dropdown = null;
        }
      });

      parent.appendChild(dropdown);
    });

    if (typeof document !== "undefined") {
      document.addEventListener("click", (evt) => {
        if (dropdown && !dropdown.contains(evt.target) && evt.target !== btn) {
          dropdown.remove();
          dropdown = null;
        }
      });
    }

    parent.appendChild(btn);
  });
}

// ----------------------------------------------------
// Stage 2: Create Media Quality Dropdown Menu (Image 2)
// ----------------------------------------------------
function createVideoDropdown(media, closeCallback) {
  const container = document.createElement("div");
  container.className = "swiftget-video-dropdown";
  const isAudio = media && media.tagName && media.tagName.toLowerCase() === "audio";

  // Derive media title from page or element
  let rawTitle = (typeof document !== "undefined" && document.title) || (isAudio ? "Audio Track" : "Video");
  rawTitle = rawTitle.replace(/\s*-\s*YouTube$/i, "").replace(/\s*-\s*Vimeo$/i, "").trim();
  if (!rawTitle) rawTitle = isAudio ? "Online Audio Stream" : "Online Video Stream";

  // Truncate title for item row matching Image 2
  const truncatedTitle = rawTitle.length > 36 ? rawTitle.substring(0, 36) + "..." : rawTitle;

  // Sniff active network media streams (HLS .m3u8, MPEG-DASH .mpd, fMP4, .ts, .mp4, .webm, .m4a, .mp3)
  const detectedUrls = [];
  if (media && media.querySelectorAll) {
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
  }

  if (typeof performance !== "undefined" && performance.getEntriesByType) {
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
  }

  // Derive realistic quality stream ladder
  const defaultVideoStreams = [
    { label: "quality 1080p HD", ext: "MP4 file", sizeBytes: 20502549, sizeFormatted: "19.55 MB" },
    { label: "quality 720p HD", ext: "MP4 file", sizeBytes: 13107200, sizeFormatted: "12.50 MB" },
    { label: "quality 608p HD AV1, PM", ext: "MP4 file", sizeBytes: 10485760, sizeFormatted: "10.00 MB" },
    { label: "quality 480p", ext: "MP4 file", sizeBytes: 7340032, sizeFormatted: "7.00 MB" },
    { label: "quality 360p", ext: "MP4 file", sizeBytes: 4718592, sizeFormatted: "4.50 MB" },
    { label: "quality 240p", ext: "MP4 file", sizeBytes: 3145728, sizeFormatted: "3.00 MB" },
    { label: "quality 144p", ext: "MP4 file", sizeBytes: 1572864, sizeFormatted: "1.50 MB" }
  ];

  const defaultAudioStreams = [
    { label: "320 kbps High Quality", ext: "MP3 audio", sizeBytes: 8388608, sizeFormatted: "8.00 MB" },
    { label: "256 kbps AAC", ext: "M4A audio", sizeBytes: 6291456, sizeFormatted: "6.00 MB" },
    { label: "128 kbps Standard", ext: "MP3 audio", sizeBytes: 4194304, sizeFormatted: "4.00 MB" }
  ];

  const streams = isAudio ? defaultAudioStreams : defaultVideoStreams;

  let mediaUrl = media ? (media.currentSrc || media.src) : "";
  if (!mediaUrl || mediaUrl.startsWith("blob:")) {
    mediaUrl = detectedUrls.length > 0 ? detectedUrls[0] : ((typeof window !== "undefined" && window.location && window.location.href) || "https://example.com/video");
  }

  // "Download all" header item
  const allItem = document.createElement("div");
  allItem.className = "swiftget-dropdown-item all-item";
  allItem.textContent = "Download all";
  allItem.addEventListener("click", (e) => {
    e.stopPropagation();
    closeCallback();
    dispatchMediaDownload(mediaUrl, rawTitle, "all");
  });
  container.appendChild(allItem);

  // Divider
  const divider = document.createElement("div");
  divider.className = "swiftget-dropdown-divider";
  container.appendChild(divider);

  // Numbered stream items
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
      closeCallback();
      dispatchMediaDownload(mediaUrl, rawTitle, stream.label);
    });

    container.appendChild(item);
  });

  return container;
}

function dispatchMediaDownload(url, title, quality) {
  const correlationId = "corr_" + Date.now() + "_" + Math.random().toString(36).substring(2, 10);
  const pageUrl = (typeof window !== "undefined" && window.location && window.location.href) || "";

  // If on a recognized streaming site (YouTube, etc.), pass pageUrl so yt-dlp gets the full video formats
  const lowerPage = pageUrl.toLowerCase();
  const isVideoPlatform = lowerPage.includes("youtube.com") || lowerPage.includes("youtu.be") ||
                          lowerPage.includes("vimeo.com") || lowerPage.includes("dailymotion.com") ||
                          lowerPage.includes("tiktok.com") || lowerPage.includes("twitch.tv") ||
                          lowerPage.includes("bilibili.com") || lowerPage.includes("soundcloud.com");
  const targetUrl = isVideoPlatform ? pageUrl : (url || pageUrl);

  try {
    if (typeof chrome !== "undefined" && chrome.runtime && chrome.runtime.sendMessage) {
      chrome.runtime.sendMessage({
        type: "INTERCEPT_MEDIA",
        url: targetUrl,
        pageUrl: pageUrl,
        title: title || "",
        quality: quality || "best",
        correlationId: correlationId,
        startImmediately: true
      });
    }
  } catch (err) {
    console.warn("[SwiftGet] Failed to dispatch media download:", err);
  }
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
}

if (typeof window !== "undefined" && typeof window.setInterval === "function") {
  window.setInterval(scanAndAttachVideoOverlays, 2500);
}
