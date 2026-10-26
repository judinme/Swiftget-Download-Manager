// SwiftGet Options Script (IDM Parity Configuration)

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

function getApi() {
  return typeof chrome !== "undefined" ? chrome : (typeof browser !== "undefined" ? browser : null);
}

document.addEventListener("DOMContentLoaded", async () => {
  const api = getApi();
  const extensionsInput = document.getElementById("extensions-input");
  const blacklistInput = document.getElementById("blacklist-input");
  const torrentsChk = document.getElementById("intercept-torrents-chk");
  const insertKeyChk = document.getElementById("insert-key-chk");
  const altKeyChk = document.getElementById("alt-key-chk");
  const snifferChk = document.getElementById("sniffer-enabled-chk");
  const saveBtn = document.getElementById("save-btn");
  const resetBtn = document.getElementById("reset-btn");
  const statusMsg = document.getElementById("status-msg");

  const settings = await api.storage.local.get([
    "extensions", "blacklist", "interceptTorrents",
    "insertKeyForce", "altKeyBypass", "snifferEnabled"
  ]);

  extensionsInput.value = (settings.extensions || DEFAULT_EXTENSIONS).join(" ");
  blacklistInput.value = (settings.blacklist || DEFAULT_BLACKLIST).join("\n");
  torrentsChk.checked = settings.interceptTorrents !== false;
  if (insertKeyChk) insertKeyChk.checked = settings.insertKeyForce !== false;
  if (altKeyChk) altKeyChk.checked = settings.altKeyBypass !== false;
  if (snifferChk) snifferChk.checked = settings.snifferEnabled !== false;

  saveBtn.addEventListener("click", async () => {
    const rawExts = extensionsInput.value.split(/[\s,;|]+/).filter(Boolean).map(e => e.startsWith(".") ? e.toLowerCase() : "." + e.toLowerCase());
    const rawBlacklist = blacklistInput.value.split(/[\s,;|]+/).filter(Boolean).map(b => b.toLowerCase());
    const interceptTorrents = torrentsChk.checked;
    const insertKeyForce = insertKeyChk ? insertKeyChk.checked : true;
    const altKeyBypass = altKeyChk ? altKeyChk.checked : true;
    const snifferEnabled = snifferChk ? snifferChk.checked : true;

    await api.storage.local.set({
      extensions: rawExts,
      blacklist: rawBlacklist,
      interceptTorrents: interceptTorrents,
      insertKeyForce: insertKeyForce,
      altKeyBypass: altKeyBypass,
      snifferEnabled: snifferEnabled
    });

    statusMsg.textContent = "Settings saved successfully!";
    setTimeout(() => { statusMsg.textContent = ""; }, 2500);
  });

  resetBtn.addEventListener("click", async () => {
    extensionsInput.value = DEFAULT_EXTENSIONS.join(" ");
    blacklistInput.value = DEFAULT_BLACKLIST.join("\n");
    torrentsChk.checked = true;
    if (insertKeyChk) insertKeyChk.checked = true;
    if (altKeyChk) altKeyChk.checked = true;
    if (snifferChk) snifferChk.checked = true;

    await api.storage.local.set({
      extensions: DEFAULT_EXTENSIONS,
      blacklist: DEFAULT_BLACKLIST,
      interceptTorrents: true,
      insertKeyForce: true,
      altKeyBypass: true,
      snifferEnabled: true
    });

    statusMsg.textContent = "Reset to defaults!";
    setTimeout(() => { statusMsg.textContent = ""; }, 2500);
  });
});
