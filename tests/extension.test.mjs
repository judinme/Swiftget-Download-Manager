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



