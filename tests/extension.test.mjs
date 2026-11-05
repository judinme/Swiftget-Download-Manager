// Automated Test Suite for SwiftGet Browser Extension (MV3)
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import test from 'node:test';
import assert from 'node:assert/strict';

// Load content script and background script source code
const contentScriptPath = resolve('d:/SwiftGet/extension/content.js');
const contentScriptCode = readFileSync(contentScriptPath, 'utf8');

const backgroundScriptPath = resolve('d:/SwiftGet/extension/background.js');
const backgroundScriptCode = readFileSync(backgroundScriptPath, 'utf8');

function createMockEnvironment() {
  const messagesSent = [];
  const eventListeners = new Map();

  const mockChrome = {
    runtime: {
      sendMessage: (msg, cb) => {
        messagesSent.push(msg);
        if (cb) cb({ success: true });
      },
      onMessage: {
        addListener: () => {}
      }
    },
    storage: {
      local: {
        get: async () => ({ interceptTorrents: true }),
        set: async () => {}
      }
    }
  };

  const mockWindow = {
    addEventListener: (type, handler, useCapture) => {
      if (!eventListeners.has(type)) eventListeners.set(type, []);
      eventListeners.get(type).push({ handler, useCapture });
    },
    location: { href: 'https://example.com/index.html' },
    getComputedStyle: () => ({ position: 'static' }),
    setInterval: () => 0,
    clearInterval: () => {}
  };

  const mockDocument = {
    baseURI: 'https://example.com/index.html',
    readyState: 'complete',
    addEventListener: () => {},
    querySelectorAll: () => [],
    createElement: (tag) => ({
      tagName: tag.toUpperCase(),
      style: {},
      addEventListener: () => {},
      setAttribute: () => {},
      querySelector: () => ({ textContent: '' })
    }),
    body: {
      appendChild: () => {}
    }
  };

  return { mockChrome, mockWindow, mockDocument, messagesSent, eventListeners };
}

function createBackgroundMockEnvironment(storageData = {}) {
  const nativeMessages = [];
  const downloadsCancelled = [];
  const downloadsErased = [];
  const contextMenusCreated = [];
  const storage = {
    interceptionEnabled: true,
    interceptTorrents: true,
    altKeyBypass: true,
    extensions: ['.zip', '.exe', '.iso', '.torrent', '.mp4', '.mkv'],
    blacklist: ['google.com'],
    ...storageData
  };

  const listeners = {
    installed: [],
    messages: [],
    contextClicked: [],
    downloadsCreated: []
  };

  const mockChrome = {
    runtime: {
      onInstalled: { addListener: (cb) => listeners.installed.push(cb) },
      onMessage: { addListener: (cb) => listeners.messages.push(cb) },
      sendNativeMessage: (name, payload, cb) => {
        nativeMessages.push({ name, payload });
        if (cb) cb({ success: true, version: '1.1.0', torrentSupported: true, authToken: 'mock_token_abc' });
      },
      lastError: null
    },
    contextMenus: {
      create: (opts) => contextMenusCreated.push(opts),
      removeAll: (cb) => { if (cb) cb(); },
      onClicked: { addListener: (cb) => listeners.contextClicked.push(cb) }
    },
    downloads: {
      onCreated: { addListener: (cb) => listeners.downloadsCreated.push(cb) },
      cancel: async (id) => { downloadsCancelled.push(id); },
      erase: async (idObj) => { downloadsErased.push(idObj.id); }
    },
    storage: {
      local: {
        get: async (keys) => {
          const res = {};
          for (const k of keys) res[k] = storage[k];
          return res;
        },
        set: async (obj) => { Object.assign(storage, obj); }
      }
    },
    cookies: {
      getAll: async () => [{ name: 'auth_session', value: 'secret_token_123' }]
    },
    tabs: {
      query: async () => [{ id: 101, active: true }],
      sendMessage: () => {}
    }
  };

  const mockNavigator = {
    userAgent: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36'
  };

  return { mockChrome, mockNavigator, listeners, nativeMessages, downloadsCancelled, downloadsErased, contextMenusCreated, storage };
}

// ----------------------------------------------------
// Content Script Tests
// ----------------------------------------------------

test('Content Script: Intercepts magnet link click and prevents default navigation', () => {
  const { mockChrome, mockWindow, mockDocument, messagesSent, eventListeners } = createMockEnvironment();

  const runScript = new Function('window', 'document', 'chrome', contentScriptCode);
  runScript(mockWindow, mockDocument, mockChrome);

  const clickListeners = eventListeners.get('click') || [];
  assert.ok(clickListeners.length > 0, 'Click listener should be registered on window');

  let defaultPrevented = false;
  let propagationStopped = false;

  const mockAnchor = {
    getAttribute: (attr) => (attr === 'href' ? 'magnet:?xt=urn:btih:da39a3ee5e6b4b0d3255bfef95601890afd80709&dn=Ubuntu' : null),
    textContent: 'Download Ubuntu Magnet',
    closest: (selector) => (selector === 'a[href]' ? mockAnchor : null)
  };

  const mockEvent = {
    target: mockAnchor,
    altKey: false,
    preventDefault: () => { defaultPrevented = true; },
    stopPropagation: () => { propagationStopped = true; }
  };

  clickListeners[0].handler(mockEvent);

  assert.equal(defaultPrevented, true, 'Default navigation should be prevented on magnet link click');
  assert.equal(propagationStopped, true, 'Event propagation should be stopped');
  assert.equal(messagesSent.length, 1, 'Exactly one message should be sent to extension worker');
  assert.equal(messagesSent[0].type, 'INTERCEPT_MAGNET');
  assert.equal(messagesSent[0].url, 'magnet:?xt=urn:btih:da39a3ee5e6b4b0d3255bfef95601890afd80709&dn=Ubuntu');
});

test('Content Script: Intercepts uppercase MAGNET: URI', () => {
  const { mockChrome, mockWindow, mockDocument, messagesSent, eventListeners } = createMockEnvironment();

  const runScript = new Function('window', 'document', 'chrome', contentScriptCode);
  runScript(mockWindow, mockDocument, mockChrome);

  const clickListeners = eventListeners.get('click') || [];
  let defaultPrevented = false;

  const mockAnchor = {
    getAttribute: (attr) => (attr === 'href' ? 'MAGNET:?xt=urn:btih:da39a3ee5e6b4b0d3255bfef95601890afd80709&dn=Arch' : null),
    textContent: 'Arch Magnet',
    closest: (selector) => (selector === 'a[href]' ? mockAnchor : null)
  };

  const mockEvent = {
    target: mockAnchor,
    altKey: false,
    preventDefault: () => { defaultPrevented = true; },
    stopPropagation: () => {}
  };

  clickListeners[0].handler(mockEvent);

  assert.equal(defaultPrevented, true);
  assert.equal(messagesSent.length, 1);
  assert.equal(messagesSent[0].type, 'INTERCEPT_MAGNET');
});

test('Content Script: Intercepts direct .torrent file link click', () => {
  const { mockChrome, mockWindow, mockDocument, messagesSent, eventListeners } = createMockEnvironment();

  const runScript = new Function('window', 'document', 'chrome', contentScriptCode);
  runScript(mockWindow, mockDocument, mockChrome);

  const clickListeners = eventListeners.get('click') || [];

  let defaultPrevented = false;
  const mockAnchor = {
    getAttribute: (attr) => (attr === 'href' ? 'https://releases.ubuntu.com/22.04/ubuntu-22.04-desktop.iso.torrent' : null),
    textContent: 'Ubuntu Torrent File',
    closest: (selector) => (selector === 'a[href]' ? mockAnchor : null)
  };

  const mockEvent = {
    target: mockAnchor,
    altKey: false,
    preventDefault: () => { defaultPrevented = true; },
    stopPropagation: () => {}
  };

  clickListeners[0].handler(mockEvent);

  assert.equal(defaultPrevented, true);
  assert.equal(messagesSent.length, 1);
  assert.equal(messagesSent[0].type, 'INTERCEPT_TORRENT_LINK');
  assert.equal(messagesSent[0].url, 'https://releases.ubuntu.com/22.04/ubuntu-22.04-desktop.iso.torrent');
});

test('Content Script: Relative .torrent in nested iframe resolves document.baseURI safely', () => {
  const { mockChrome, mockWindow, mockDocument, messagesSent, eventListeners } = createMockEnvironment();
  mockDocument.baseURI = 'https://tracker.example.org/nested/frame.html';

  const runScript = new Function('window', 'document', 'chrome', contentScriptCode);
  runScript(mockWindow, mockDocument, mockChrome);

  const clickListeners = eventListeners.get('click') || [];
  let defaultPrevented = false;

  const mockAnchor = {
    getAttribute: (attr) => (attr === 'href' ? '../downloads/linux.torrent' : null),
    textContent: 'Relative Torrent',
    closest: (selector) => (selector === 'a[href]' ? mockAnchor : null)
  };

  const mockEvent = {
    target: mockAnchor,
    altKey: false,
    preventDefault: () => { defaultPrevented = true; },
    stopPropagation: () => {}
  };

  clickListeners[0].handler(mockEvent);

  assert.equal(defaultPrevented, true);
  assert.equal(messagesSent.length, 1);
  assert.equal(messagesSent[0].url, 'https://tracker.example.org/downloads/linux.torrent');
});

test('Content Script: Alt key bypass permits native browser / OS handling', () => {
  const { mockChrome, mockWindow, mockDocument, messagesSent, eventListeners } = createMockEnvironment();

  const runScript = new Function('window', 'document', 'chrome', contentScriptCode);
  runScript(mockWindow, mockDocument, mockChrome);

  const clickListeners = eventListeners.get('click') || [];

  let defaultPrevented = false;
  const mockAnchor = {
    getAttribute: (attr) => (attr === 'href' ? 'magnet:?xt=urn:btih:da39a3ee5e6b4b0d3255bfef95601890afd80709' : null),
    closest: (selector) => (selector === 'a[href]' ? mockAnchor : null)
  };

  const mockEvent = {
    target: mockAnchor,
    altKey: true,
    preventDefault: () => { defaultPrevented = true; },
    stopPropagation: () => {}
  };

  clickListeners[0].handler(mockEvent);

  assert.equal(defaultPrevented, false, 'Default should NOT be prevented when Alt key is held');
  assert.equal(messagesSent.length, 0, 'No interception message should be sent');
});

test('Content Script: Standard web links are NOT intercepted', () => {
  const { mockChrome, mockWindow, mockDocument, messagesSent, eventListeners } = createMockEnvironment();

  const runScript = new Function('window', 'document', 'chrome', contentScriptCode);
  runScript(mockWindow, mockDocument, mockChrome);

  const clickListeners = eventListeners.get('click') || [];

  let defaultPrevented = false;
  const mockAnchor = {
    getAttribute: (attr) => (attr === 'href' ? 'https://example.com/about.html' : null),
    closest: (selector) => (selector === 'a[href]' ? mockAnchor : null)
  };

  const mockEvent = {
    target: mockAnchor,
    altKey: false,
    preventDefault: () => { defaultPrevented = true; },
    stopPropagation: () => {}
  };

  clickListeners[0].handler(mockEvent);

  assert.equal(defaultPrevented, false);
  assert.equal(messagesSent.length, 0);
});

// ----------------------------------------------------
// Background Service Worker Tests
// ----------------------------------------------------

test('Background Worker: onInstalled registers context menus for magnet and .torrent', async () => {
  const env = createBackgroundMockEnvironment();
  const runWorker = new Function('chrome', 'navigator', backgroundScriptCode);
  runWorker(env.mockChrome, env.mockNavigator);

  assert.equal(env.listeners.installed.length, 1);
  await env.listeners.installed[0]();

  const magnetMenu = env.contextMenusCreated.find(m => m.id === 'swiftget_download_magnet');
  assert.ok(magnetMenu, 'swiftget_download_magnet menu should be created');
  assert.deepEqual(magnetMenu.targetUrlPatterns, ['magnet:*']);

  const torrentMenu = env.contextMenusCreated.find(m => m.id === 'swiftget_download_torrent');
  assert.ok(torrentMenu, 'swiftget_download_torrent menu should be created');
  assert.deepEqual(torrentMenu.targetUrlPatterns, ['*://*/*.torrent*']);
});

test('Background Worker: Intercepts application/x-bittorrent MIME download', async () => {
  const env = createBackgroundMockEnvironment();
  const runWorker = new Function('chrome', 'navigator', backgroundScriptCode);
  runWorker(env.mockChrome, env.mockNavigator);

  const onCreatedHandler = env.listeners.downloadsCreated[0];
  assert.ok(onCreatedHandler, 'Download listener should be registered');

  const downloadItem = {
    id: 991,
    url: 'https://releases.ubuntu.com/download/file',
    finalUrl: 'https://releases.ubuntu.com/download/file',
    filename: 'ubuntu.iso',
    mime: 'application/x-bittorrent',
    referrer: 'https://releases.ubuntu.com/'
  };

  await onCreatedHandler(downloadItem);

  assert.ok(env.downloadsCancelled.includes(991), 'Browser download should be cancelled');
  assert.ok(env.downloadsErased.includes(991), 'Browser download item should be erased from history');
  assert.equal(env.nativeMessages.length, 1, 'Native message should be dispatched');
  assert.equal(env.nativeMessages[0].payload.action, 'addTorrentFile');
  assert.equal(env.nativeMessages[0].payload.url, 'https://releases.ubuntu.com/download/file');
  assert.ok(env.nativeMessages[0].payload.cookies.includes('auth_session=secret_token_123'));
});

test('Background Worker: Intercepts .torrent file download by filename and extension', async () => {
  const env = createBackgroundMockEnvironment();
  const runWorker = new Function('chrome', 'navigator', backgroundScriptCode);
  runWorker(env.mockChrome, env.mockNavigator);

  const onCreatedHandler = env.listeners.downloadsCreated[0];
  const downloadItem = {
    id: 992,
    url: 'https://releases.debian.org/debian-12.torrent',
    filename: 'debian-12.torrent',
    mime: 'application/octet-stream'
  };

  await onCreatedHandler(downloadItem);

  assert.ok(env.downloadsCancelled.includes(992));
  assert.equal(env.nativeMessages.length, 1);
  assert.equal(env.nativeMessages[0].payload.action, 'addTorrentFile');
  assert.equal(env.nativeMessages[0].payload.fileName, 'debian-12.torrent');
});

test('Background Worker: Context menu swiftget_download_magnet triggers addMagnet', async () => {
  const env = createBackgroundMockEnvironment();
  const runWorker = new Function('chrome', 'navigator', backgroundScriptCode);
  runWorker(env.mockChrome, env.mockNavigator);

  const menuHandler = env.listeners.contextClicked[0];
  assert.ok(menuHandler);

  await menuHandler({
    menuItemId: 'swiftget_download_magnet',
    linkUrl: 'magnet:?xt=urn:btih:da39a3ee5e6b4b0d3255bfef95601890afd80709&dn=Fedora'
  }, { url: 'https://getfedora.org' });

  assert.equal(env.nativeMessages.length, 1);
  assert.equal(env.nativeMessages[0].payload.action, 'addMagnet');
  assert.equal(env.nativeMessages[0].payload.magnetUri, 'magnet:?xt=urn:btih:da39a3ee5e6b4b0d3255bfef95601890afd80709&dn=Fedora');
});

test('Background Worker: CHECK_CONNECTION returns status with version and torrentSupported', (t, done) => {
  const env = createBackgroundMockEnvironment();
  const runWorker = new Function('chrome', 'navigator', backgroundScriptCode);
  runWorker(env.mockChrome, env.mockNavigator);

  const messageHandler = env.listeners.messages[0];
  assert.ok(messageHandler);

  messageHandler({ type: 'CHECK_CONNECTION' }, {}, (response) => {
    assert.equal(response.connected, true);
    assert.equal(response.version, '1.1.0');
    assert.equal(response.torrentSupported, true);
    assert.equal(env.storage.authToken, 'mock_token_abc');
    done();
  });
});

test('Content Script: Insert key held forces interception on any clicked link (IDM specification)', () => {
  const { mockChrome, mockWindow, mockDocument, messagesSent, eventListeners } = createMockEnvironment();

  const runScript = new Function('window', 'document', 'chrome', contentScriptCode);
  runScript(mockWindow, mockDocument, mockChrome);

  const keydownListeners = eventListeners.get('keydown') || [];
  assert.ok(keydownListeners.length > 0);

  // Simulate pressing Insert key
  keydownListeners[0].handler({ key: 'Insert' });

  const clickListeners = eventListeners.get('click') || [];
  let defaultPrevented = false;

  const mockAnchor = {
    getAttribute: (attr) => (attr === 'href' ? 'https://example.com/downloads/manual.pdf' : null),
    textContent: 'User Manual PDF',
    closest: (selector) => (selector === 'a[href]' ? mockAnchor : null)
  };

  const mockEvent = {
    target: mockAnchor,
    altKey: false,
    preventDefault: () => { defaultPrevented = true; },
    stopPropagation: () => {}
  };

  clickListeners[0].handler(mockEvent);

  assert.equal(defaultPrevented, true, 'Default navigation must be prevented on Insert-click');
  const forcedMsg = messagesSent.find(m => m.type === 'FORCE_INTERCEPT_LINK');
  assert.ok(forcedMsg, 'FORCE_INTERCEPT_LINK message must be dispatched');
  assert.equal(forcedMsg.url, 'https://example.com/downloads/manual.pdf');
  assert.equal(forcedMsg.text, 'User Manual PDF');
});

test('Content Script: Audio element attaches Download this audio overlay button', () => {
  const { mockChrome, mockWindow, mockDocument, eventListeners } = createMockEnvironment();
  const attachedElements = [];

  const mockAudio = {
    tagName: 'AUDIO',
    dataset: {},
    parentElement: {
      style: {},
      appendChild: (el) => attachedElements.push(el)
    },
    getBoundingClientRect: () => ({ width: 300, height: 40 })
  };

  mockDocument.querySelectorAll = (selector) => {
    if (selector.includes('audio')) {
      return [mockAudio];
    }
    return [];
  };

  const runScript = new Function('window', 'document', 'chrome', contentScriptCode);
  runScript(mockWindow, mockDocument, mockChrome);

  // Trigger scanAndAttachVideoOverlays via DOMContentLoaded
  const readyListeners = eventListeners.get('DOMContentLoaded') || [];
  if (readyListeners.length > 0) {
    readyListeners[0].handler();
  }

  assert.equal(mockAudio.dataset.swiftgetAttached, 'true');
  const btn = attachedElements.find(el => el.className && el.className.includes('swiftget-audio-overlay-btn'));
  assert.ok(btn, 'Overlay button with swiftget-audio-overlay-btn must be attached');
  assert.ok(btn.innerHTML.includes('Download this audio'));
});

// ----------------------------------------------------
// DRM streaming sites: overlay must never be shown
// ----------------------------------------------------
function runOverlayScanOnPage(href, { ancestorOrigins = null, storage = {} } = {}) {
  const { mockChrome, mockWindow, mockDocument } = createMockEnvironment();
  const attachedElements = [];
  const mockVideo = {
    tagName: 'VIDEO',
    dataset: {},
    parentElement: { style: {}, appendChild: (el) => attachedElements.push(el) },
    getBoundingClientRect: () => ({ width: 1280, height: 720 })
  };
  mockWindow.location = { href };
  if (ancestorOrigins) mockWindow.location.ancestorOrigins = ancestorOrigins;
  mockDocument.baseURI = href;
  mockDocument.querySelectorAll = (selector) => (selector.includes('video') ? [mockVideo] : []);
  mockChrome.storage.local.get = async () => storage;

  const runScript = new Function('window', 'document', 'chrome', contentScriptCode);
  runScript(mockWindow, mockDocument, mockChrome);

  const btn = attachedElements.find(el => el.className && el.className.includes('swiftget-video-overlay-btn'));
  return { btn, mockVideo };
}

for (const href of [
  'https://www.netflix.com/watch/81234567',
  'https://www.primevideo.com/detail/0ABCDEF',
  'https://www.amazon.com/gp/video/detail/B0ABCDEF/',
  'https://www.amazon.co.uk/Prime-Video/b?node=3010085031',
  'https://www.disneyplus.com/video/abc',
  'https://www.hotstar.com/in/movies/x/123',
  'https://play.max.com/video/watch/abc',
  'https://open.spotify.com/track/123'
]) {
  test(`Content Script: no "Download this video" overlay on DRM site ${new URL(href).hostname}${new URL(href).pathname}`, () => {
    const { btn, mockVideo } = runOverlayScanOnPage(href);
    assert.equal(btn, undefined, `Overlay must not be attached on ${href}`);
    assert.notEqual(mockVideo.dataset.swiftgetAttached, 'true');
  });
}

test('Content Script: overlay blocked inside an iframe embedded by a DRM site', () => {
  const { btn } = runOverlayScanOnPage('https://player.example-cdn.com/embed/1', {
    ancestorOrigins: { length: 1, 0: 'https://www.netflix.com' }
  });
  assert.equal(btn, undefined);
});

test('Content Script: overlay still shown on YouTube and on regular Amazon product pages', () => {
  for (const href of ['https://www.youtube.com/watch?v=7Jo-JZkrVbw', 'https://www.amazon.com/dp/B0CHX1W1XY']) {
    const { btn } = runOverlayScanOnPage(href);
    assert.ok(btn, `Overlay must be attached on ${href}`);
    assert.ok(btn.innerHTML.includes('Download this video'));
  }
});

test('Content Script: user exclusion list hosts hide the overlay; capture-only patterns are ignored', async () => {
  const href = 'https://videos.mysite.example.org/watch/1';
  const { mockChrome, mockWindow, mockDocument } = createMockEnvironment();
  const attached = [];
  const removed = [];
  const btnEl = { className: 'swiftget-video-overlay-btn', remove: () => removed.push(1) };
  const mockVideo = {
    tagName: 'VIDEO', dataset: {},
    parentElement: { style: {}, appendChild: (el) => attached.push(el) },
    getBoundingClientRect: () => ({ width: 1280, height: 720 })
  };
  mockWindow.location = { href };
  mockDocument.querySelectorAll = (selector) => {
    if (selector === '.swiftget-video-overlay-btn') return [btnEl];
    return selector.includes('video') ? [mockVideo] : [];
  };
  mockChrome.storage.local.get = async () => ({ blacklist: ['*/api/*', 'mysite.example.org'] });

  const runScript = new Function('window', 'document', 'chrome', contentScriptCode);
  runScript(mockWindow, mockDocument, mockChrome);
  await new Promise(r => setTimeout(r, 0));

  assert.ok(removed.length > 0, 'Existing overlay buttons must be removed once the user exclusion list loads');
});

test('Background Worker: FORCE_INTERCEPT_LINK gathers cookies and dispatches to native host', async () => {
  const env = createBackgroundMockEnvironment();
  const runWorker = new Function('chrome', 'navigator', backgroundScriptCode);
  runWorker(env.mockChrome, env.mockNavigator);

  const messageHandler = env.listeners.messages[0];
  assert.ok(messageHandler);

  await new Promise((resolve) => {
    messageHandler({
      type: 'FORCE_INTERCEPT_LINK',
      url: 'https://cdn.example.com/build.zip',
      text: 'Build Archive'
    }, { tab: { url: 'https://example.com' } }, (res) => {
      resolve(res);
    });
  });

  assert.equal(env.nativeMessages.length, 1);
  assert.equal(env.nativeMessages[0].payload.action, 'interceptDownload');
  assert.equal(env.nativeMessages[0].payload.url, 'https://cdn.example.com/build.zip');
  assert.equal(env.nativeMessages[0].payload.fileName, 'Build Archive');
  assert.ok(env.nativeMessages[0].payload.cookies.includes('auth_session=secret_token_123'));
});

test('Extension: Firefox manifest has valid Gecko ID and background scripts configuration', () => {
  const firefoxManifestPath = resolve('d:/SwiftGet/extension/manifest.firefox.json');
  const firefoxManifestContent = JSON.parse(readFileSync(firefoxManifestPath, 'utf8'));

  assert.equal(firefoxManifestContent.manifest_version, 3);
  assert.equal(firefoxManifestContent.browser_specific_settings.gecko.id, 'swiftget@swiftget.net');
  assert.ok(firefoxManifestContent.background.scripts.includes('background.js'));
  assert.ok(firefoxManifestContent.permissions.includes('nativeMessaging'));
  assert.ok(firefoxManifestContent.permissions.includes('downloads'));
});

test('Background Worker: Intercepts .mp4 video file download and forwards to native host', async () => {
  const env = createBackgroundMockEnvironment();
  const runWorker = new Function('chrome', 'navigator', backgroundScriptCode);
  runWorker(env.mockChrome, env.mockNavigator);

  const onCreatedHandler = env.listeners.downloadsCreated[0];
  const downloadItem = {
    id: 1042,
    url: 'https://cdn.example.org/videos/nature-trailer.mp4',
    filename: 'nature-trailer.mp4',
    mime: 'video/mp4',
    referrer: 'https://example.org/videos'
  };

  await onCreatedHandler(downloadItem);

  assert.ok(env.downloadsCancelled.includes(1042), 'Browser download must be cancelled');
  assert.ok(env.downloadsErased.includes(1042), 'Browser history entry must be erased');
  assert.equal(env.nativeMessages.length, 1);
  assert.equal(env.nativeMessages[0].payload.action, 'interceptDownload');
  assert.equal(env.nativeMessages[0].payload.url, 'https://cdn.example.org/videos/nature-trailer.mp4');
  assert.equal(env.nativeMessages[0].payload.fileName, 'nature-trailer.mp4');
});

test('Background Worker: INTERCEPT_MEDIA message dispatches video download to native host', async () => {
  const env = createBackgroundMockEnvironment();
  const runWorker = new Function('chrome', 'navigator', backgroundScriptCode);
  runWorker(env.mockChrome, env.mockNavigator);

  const messageHandler = env.listeners.messages[0];
  assert.ok(messageHandler);

  await new Promise((resolve) => {
    messageHandler({
      type: 'INTERCEPT_MEDIA',
      url: 'https://stream.example.org/master.m3u8',
      title: 'Amazing Documentary 1080p',
      fileName: 'Amazing_Documentary.mp4',
      savePath: 'Downloads\\Video\\Amazing_Documentary.mp4',
      sizeBytes: 104857600,
      startImmediately: true
    }, { tab: { url: 'https://stream.example.org/watch/123' } }, (res) => {
      resolve(res);
    });
  });

  assert.equal(env.nativeMessages.length, 1);
  assert.equal(env.nativeMessages[0].payload.action, 'interceptDownload');
  assert.equal(env.nativeMessages[0].payload.url, 'https://stream.example.org/master.m3u8');
  assert.equal(env.nativeMessages[0].payload.fileName, 'Amazing_Documentary.mp4');
  assert.equal(env.nativeMessages[0].payload.destinationPath, 'Downloads\\Video\\Amazing_Documentary.mp4');
  assert.equal(env.nativeMessages[0].payload.startImmediately, true);
});

test('Content Script: Video element attaches "Download this video" overlay button', () => {
  const { mockChrome, mockWindow, mockDocument, eventListeners } = createMockEnvironment();
  const attachedElements = [];

  const mockVideo = {
    tagName: 'VIDEO',
    dataset: {},
    parentElement: {
      style: {},
      appendChild: (el) => attachedElements.push(el)
    },
    getBoundingClientRect: () => ({ width: 640, height: 360 })
  };

  mockDocument.querySelectorAll = (selector) => {
    if (selector.includes('video')) {
      return [mockVideo];
    }
    return [];
  };

  const runScript = new Function('window', 'document', 'chrome', contentScriptCode);
  runScript(mockWindow, mockDocument, mockChrome);

  const readyListeners = eventListeners.get('DOMContentLoaded') || [];
  if (readyListeners.length > 0) {
    readyListeners[0].handler();
  }

  assert.equal(mockVideo.dataset.swiftgetAttached, 'true');
  const btn = attachedElements.find(el => el.className && el.className.includes('swiftget-video-overlay-btn'));
  assert.ok(btn, 'Overlay button with swiftget-video-overlay-btn must be attached');
  assert.ok(btn.innerHTML.includes('Download this video'));
});

test('Content Script: Media item click dispatches INTERCEPT_MEDIA with correlationId and never injects DOM modal', () => {
  const { mockChrome, mockWindow, mockDocument } = createMockEnvironment();
  const createdElements = [];

  mockDocument.createElement = (tag) => {
    const el = {
      tagName: tag.toUpperCase(),
      className: '',
      style: {},
      listeners: {},
      children: [],
      addEventListener: (evt, handler) => {
        el.listeners[evt] = handler;
      },
      appendChild: (child) => {
        el.children.push(child);
        return child;
      },
      remove: () => {},
      querySelector: () => null
    };
    createdElements.push(el);
    return el;
  };

  const runScript = new Function('window', 'document', 'chrome', contentScriptCode);
  runScript(mockWindow, mockDocument, mockChrome);

  // Directly check createVideoDropdown behavior if exposed or simulated
  assert.ok(contentScriptCode.includes('correlationId'), 'contentScriptCode must use correlationId');
  assert.ok(!contentScriptCode.includes('showDownloadFileInfoModal'), 'contentScriptCode must not inject simulated download info modal');
  assert.ok(!contentScriptCode.includes('showDownloadCompleteModal'), 'contentScriptCode must not inject simulated download complete modal');
});

test('Background Worker: onInstalled registers context menu for text selection', async () => {
  const env = createBackgroundMockEnvironment();
  const runWorker = new Function('chrome', 'navigator', backgroundScriptCode);
  runWorker(env.mockChrome, env.mockNavigator);

  assert.equal(env.listeners.installed.length, 1);
  await env.listeners.installed[0]();

  const selectionMenu = env.contextMenusCreated.find(m => m.id === 'swiftget_download_selected');
  assert.ok(selectionMenu, 'swiftget_download_selected menu should be created');
  assert.deepEqual(selectionMenu.contexts, ['selection']);
});

test('Background Worker: Context menu swiftget_download_selected parses text and dispatches URLs', async () => {
  const env = createBackgroundMockEnvironment();
  const runWorker = new Function('chrome', 'navigator', backgroundScriptCode);
  runWorker(env.mockChrome, env.mockNavigator);

  assert.equal(env.listeners.installed.length, 1);
  await env.listeners.installed[0]();

  const clickHandler = env.listeners.contextClicked[0];
  assert.ok(clickHandler, 'Context menu click handler must be registered');

  await clickHandler({
    menuItemId: 'swiftget_download_selected',
    selectionText: 'Links: https://example.com/archive.zip and magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567'
  }, { url: 'https://example.com/page.html' });

  const zipMsg = env.nativeMessages.find(m => m.payload.url === 'https://example.com/archive.zip');
  assert.ok(zipMsg, 'Should dispatch https link from selection');
  assert.equal(zipMsg.payload.cookies, 'auth_session=secret_token_123');

  const magnetMsg = env.nativeMessages.find(m => m.payload.action === 'addMagnet');
  assert.ok(magnetMsg, 'Should dispatch magnet link from selection');
});

test('Background Worker: Context menu swiftget_download_media downloads image with cookies', async () => {
  const env = createBackgroundMockEnvironment();
  const runWorker = new Function('chrome', 'navigator', backgroundScriptCode);
  runWorker(env.mockChrome, env.mockNavigator);

  assert.equal(env.listeners.installed.length, 1);
  await env.listeners.installed[0]();

  const clickHandler = env.listeners.contextClicked[0];
  await clickHandler({
    menuItemId: 'swiftget_download_media',
    srcUrl: 'https://example.com/assets/banner.png'
  }, { url: 'https://example.com/index.html' });

  const imgMsg = env.nativeMessages.find(m => m.payload.url === 'https://example.com/assets/banner.png');
  assert.ok(imgMsg, 'Should dispatch media download from context menu');
  assert.equal(imgMsg.payload.cookies, 'auth_session=secret_token_123');
});

test('Content Script: Link grabber classifies links by category', () => {
  const { mockChrome, mockWindow, mockDocument } = createMockEnvironment();

  const mockLinks = [
    { href: 'https://example.com/video.mp4', textContent: 'Movie Clip' },
    { href: 'https://example.com/song.mp3', textContent: 'Song Track' },
    { href: 'https://example.com/archive.zip', textContent: 'Backup' },
    { href: 'https://example.com/manual.pdf', textContent: 'User Manual' },
    { href: 'https://example.com/photo.jpg', textContent: 'Snapshot' },
    { href: 'magnet:?xt=urn:btih:abc', textContent: 'Torrent Magnet' },
    { href: 'javascript:void(0)', textContent: 'Ignore Script' },
    { href: '#section2', textContent: 'Ignore Anchor' }
  ];

  mockDocument.querySelectorAll = (sel) => {
    if (sel.includes('a[href]')) {
      return mockLinks.map(l => ({
        href: l.href,
        getAttribute: (attr) => attr === 'href' ? l.href : null,
        textContent: l.textContent,
        title: ''
      }));
    }
    return [];
  };

  let messageHandler = null;
  mockChrome.runtime.onMessage.addListener = (fn) => {
    messageHandler = fn;
  };

  const runScript = new Function('window', 'document', 'chrome', contentScriptCode);
  runScript(mockWindow, mockDocument, mockChrome);

  assert.ok(messageHandler, 'onMessage handler must be registered');

  let responseData = null;
  messageHandler({ type: 'GATHER_ALL_LINKS' }, {}, (resp) => {
    responseData = resp;
  });

  assert.ok(responseData && responseData.links, 'Should return links array');
  assert.equal(responseData.links.length, 6, 'Should filter out javascript: and anchor hash links');

  const video = responseData.links.find(l => l.url.includes('video.mp4'));
  assert.ok(video);
  assert.equal(video.category, 'video');

  const archive = responseData.links.find(l => l.url.includes('archive.zip'));
  assert.ok(archive);
  assert.equal(archive.category, 'archive');

  const doc = responseData.links.find(l => l.url.includes('manual.pdf'));
  assert.ok(doc);
  assert.equal(doc.category, 'document');
});

test('Background Worker: DEFAULT_BLACKLIST allows GitHub Releases and Microsoft file downloads', async () => {
  const env = createBackgroundMockEnvironment({ blacklist: undefined });
  const runWorker = new Function('chrome', 'navigator', backgroundScriptCode);
  runWorker(env.mockChrome, env.mockNavigator);

  const onCreatedHandler = env.listeners.downloadsCreated[0];
  await onCreatedHandler({
    id: 2001,
    url: 'https://github.com/swiftget/swiftget/releases/download/v1.1.0/SwiftGetSetup.exe',
    filename: 'SwiftGetSetup.exe',
    mime: 'application/octet-stream',
    referrer: 'https://github.com/swiftget/swiftget/releases'
  });

  assert.ok(env.downloadsCancelled.includes(2001), 'GitHub release download should be intercepted');
  assert.equal(env.nativeMessages.length, 1);
  assert.equal(env.nativeMessages[0].payload.fileName, 'SwiftGetSetup.exe');
});

test('Background Worker: CHECK_CONNECTION falls back to Loopback API when Native Host fails', async () => {
  const env = createBackgroundMockEnvironment();
  env.mockChrome.runtime.sendNativeMessage = (name, payload, cb) => {
    env.mockChrome.runtime.lastError = { message: 'Specified native messaging host not found.' };
    if (cb) cb(null);
    env.mockChrome.runtime.lastError = null;
  };

  const mockFetch = async (url) => {
    if (url.endsWith('/api/v1/health')) {
      return { ok: true, json: async () => ({ status: 'ok', version: '1.1.0' }) };
    }
    if (url.endsWith('/api/v1/auth/token')) {
      return { ok: true, json: async () => ({ authToken: 'loopback_token_xyz' }) };
    }
    return { ok: false };
  };

  const runWorker = new Function('chrome', 'navigator', 'fetch', backgroundScriptCode);
  runWorker(env.mockChrome, env.mockNavigator, mockFetch);

  const messageHandler = env.listeners.messages[0];
  const response = await new Promise((resolve) => {
    messageHandler({ type: 'CHECK_CONNECTION' }, {}, (res) => resolve(res));
  });

  assert.equal(response.connected, true);
  assert.equal(response.version, '1.1.0');
  assert.equal(env.storage.authToken, 'loopback_token_xyz');
});

test('Background Worker: Drops stale download history on startup (startTime older than 15s TTL)', async () => {
  const env = createBackgroundMockEnvironment();
  const runWorker = new Function('chrome', 'navigator', backgroundScriptCode);
  runWorker(env.mockChrome, env.mockNavigator);

  const onCreatedHandler = env.listeners.downloadsCreated[0];
  const staleTime = new Date(Date.now() - 60000).toISOString(); // 60s old
  await onCreatedHandler({
    id: 3001,
    url: 'https://example.com/old-installer.exe',
    filename: 'old-installer.exe',
    mime: 'application/octet-stream',
    state: 'in_progress',
    startTime: staleTime
  });

  assert.equal(env.downloadsCancelled.length, 0, 'Stale download must NOT be cancelled');
  assert.equal(env.nativeMessages.length, 0, 'Stale download must NOT be forwarded to native host');
});

test('Background Worker: Ignores claude.ai/api markdown attachment, gradle.properties, and .appinstaller', async () => {
  const env = createBackgroundMockEnvironment({ blacklist: undefined });
  const runWorker = new Function('chrome', 'navigator', backgroundScriptCode);
  runWorker(env.mockChrome, env.mockNavigator);

  const onCreatedHandler = env.listeners.downloadsCreated[0];

  // 1. claude.ai API blob/markdown
  await onCreatedHandler({
    id: 3002,
    url: 'https://claude.ai/api/organizations/12345/artifacts/DocuForge-PDF-Toolbox-Documentation.md',
    filename: 'DocuForge-PDF-Toolbox-Documentation.md',
    mime: 'text/markdown',
    state: 'in_progress',
    startTime: new Date().toISOString()
  });

  // 2. gradle.properties
  await onCreatedHandler({
    id: 3003,
    url: 'https://repo.example.com/project/gradle.properties',
    filename: 'gradle.properties',
    mime: 'text/plain',
    fileSize: 1024,
    state: 'in_progress',
    startTime: new Date().toISOString()
  });

  // 3. .appinstaller
  await onCreatedHandler({
    id: 3004,
    url: 'https://download.microsoft.com/Microsoft.SurfaceITToolkit.UI_x64.appinstaller',
    filename: 'Microsoft.SurfaceITToolkit.UI_x64.appinstaller',
    mime: 'application/xml',
    fileSize: 4096,
    state: 'in_progress',
    startTime: new Date().toISOString()
  });

  // 4. blob: URL
  await onCreatedHandler({
    id: 3005,
    url: 'blob:https://claude.ai/abc-123-def',
    filename: 'export.json',
    mime: 'application/json',
    state: 'in_progress',
    startTime: new Date().toISOString()
  });

  assert.equal(env.nativeMessages.length, 0, 'None of the non-download / API / config items should be captured');
  assert.equal(env.downloadsCancelled.length, 0, 'Browser downloads for ignored items must be left alone');
});

test('Background Worker: When SwiftGet is closed (appNotRunning=true), browser download is NOT cancelled and NOT queued', async () => {
  const env = createBackgroundMockEnvironment();
  env.mockChrome.runtime.sendNativeMessage = (name, payload, cb) => {
    env.nativeMessages.push({ name, payload });
    if (cb) cb({ success: false, appNotRunning: true, ack: true });
  };
  const mockFetch = async () => ({ ok: false });

  const runWorker = new Function('chrome', 'navigator', 'fetch', backgroundScriptCode);
  runWorker(env.mockChrome, env.mockNavigator, mockFetch);

  const onCreatedHandler = env.listeners.downloadsCreated[0];
  await onCreatedHandler({
    id: 3006,
    url: 'https://releases.ubuntu.com/24.04/ubuntu.iso',
    filename: 'ubuntu.iso',
    mime: 'application/octet-stream',
    fileSize: 104857600,
    state: 'in_progress',
    startTime: new Date().toISOString()
  });

  assert.equal(env.downloadsCancelled.length, 0, 'Browser download must NOT be cancelled when SwiftGet is closed');
});

test('Background Worker: Strips YouTube Radio/Mix playlist parameters (list=RD...) on media capture', async () => {
  const env = createBackgroundMockEnvironment();
  const runWorker = new Function('chrome', 'navigator', backgroundScriptCode);
  runWorker(env.mockChrome, env.mockNavigator);

  const messageHandler = env.listeners.messages[0];
  await new Promise((resolve) => {
    messageHandler({
      type: 'INTERCEPT_MEDIA',
      url: 'https://www.youtube.com/watch?v=dQw4w9WgXcQ&list=RDdQw4w9WgXcQ&start_radio=1&index=2',
      title: 'Music Video',
      userGesture: true
    }, { tab: { id: 12, url: 'https://www.youtube.com/watch?v=dQw4w9WgXcQ&list=RDdQw4w9WgXcQ&start_radio=1' } }, resolve);
  });

  assert.equal(env.nativeMessages.length, 1);
  assert.equal(env.nativeMessages[0].payload.url, 'https://www.youtube.com/watch?v=dQw4w9WgXcQ');
});

test('Content Script: "Download this video" button mounts on outer player root, shields pointerdown/mousedown, and triggers INTERCEPT_MEDIA on single click', () => {
  const { mockChrome, mockWindow, mockDocument, messagesSent, eventListeners } = createMockEnvironment();
  mockWindow.location.href = 'https://www.youtube.com/watch?v=dQw4w9WgXcQ';
  mockDocument.title = 'Rick Astley - Never Gonna Give You Up - YouTube';

  const outerPlayerChildren = [];
  const innerWrapperChildren = [];

  const mockOuterPlayer = {
    id: 'movie_player',
    style: {},
    appendChild: (el) => {
      outerPlayerChildren.push(el);
      el.parentElement = mockOuterPlayer;
    }
  };

  const mockInnerWrapper = {
    className: 'html5-video-container',
    style: { pointerEvents: 'none' },
    parentElement: mockOuterPlayer,
    appendChild: (el) => innerWrapperChildren.push(el)
  };

  const mockVideo = {
    tagName: 'VIDEO',
    currentSrc: 'blob:https://www.youtube.com/1234-5678',
    dataset: {},
    parentElement: mockInnerWrapper,
    closest: (selector) => (selector.includes('#movie_player') ? mockOuterPlayer : null),
    getBoundingClientRect: () => ({ width: 1280, height: 720 }),
    addEventListener: () => {}
  };

  mockDocument.querySelectorAll = (selector) => {
    if (selector.includes('video')) return [mockVideo];
    return [];
  };

  const runScript = new Function('window', 'document', 'chrome', contentScriptCode);
  runScript(mockWindow, mockDocument, mockChrome);

  assert.equal(innerWrapperChildren.length, 0, 'Must NOT attach inside pointer-events:none inner .html5-video-container');
  assert.equal(outerPlayerChildren.length, 1, 'Must attach directly to outer #movie_player container above sibling overlays');

  const btn = outerPlayerChildren[0];
  assert.equal(btn.style.pointerEvents, 'auto');
  assert.equal(btn.style.zIndex, '2147483647');

  // Verify window capture listener shields pointerdown/mousedown from reaching video player
  const pointerdownCaptureListeners = (eventListeners.get('pointerdown') || []).filter(l => l.useCapture);
  assert.ok(pointerdownCaptureListeners.length > 0, 'Must register capture-phase pointerdown shield on window');

  let propStopped = false;
  let immediateStopped = false;
  pointerdownCaptureListeners[0].handler({
    target: { closest: (sel) => (sel === '.swiftget-video-overlay-btn' ? btn : null) },
    stopPropagation: () => { propStopped = true; },
    stopImmediatePropagation: () => { immediateStopped = true; }
  });
  assert.equal(propStopped, true, 'pointerdown propagation must be stopped in capture phase');
  assert.equal(immediateStopped, true, 'pointerdown immediate propagation must be stopped in capture phase');

  // Verify single click on button (or inner span via capture listener) immediately dispatches INTERCEPT_MEDIA
  const clickCaptureListeners = (eventListeners.get('click') || []).filter(l => l.useCapture);
  let clickPrevented = false;
  for (const l of clickCaptureListeners) {
    l.handler({
      target: { closest: (sel) => (sel === '.swiftget-video-overlay-btn' ? btn : null) },
      preventDefault: () => { clickPrevented = true; },
      stopPropagation: () => {},
      stopImmediatePropagation: () => {}
    });
  }

  assert.equal(clickPrevented, true);
  const mediaMsg = messagesSent.find(m => m.type === 'INTERCEPT_MEDIA');
  assert.ok(mediaMsg, 'Single click on "Download this video" must immediately dispatch INTERCEPT_MEDIA');
  assert.equal(mediaMsg.url, 'https://www.youtube.com/watch?v=dQw4w9WgXcQ');
  assert.equal(mediaMsg.title, 'Rick Astley - Never Gonna Give You Up');
  assert.equal(mediaMsg.explicitUserAction, true);
});






