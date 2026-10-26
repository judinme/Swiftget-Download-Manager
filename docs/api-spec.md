# SwiftGet Native Messaging & Local API Specification

This document details the communication protocols between the SwiftGet browser extension (or external integration tools) and the SwiftGet desktop engine.

---

## 1. Native Messaging Host (`SwiftGet.NativeHost`)

The native messaging host acts as the primary, low-latency bridge between Chromium/Firefox browsers and the download manager.

### 1.1 Transport & Framing
- **Standard I/O (`stdin` / `stdout`)**: All communication takes place via binary stdio streams.
- **Framing**: Each message begins with a 32-bit unsigned integer (little-endian) specifying the length of the JSON payload in bytes, followed immediately by the UTF-8 JSON message.
- **Binary Cleanliness**: Zero diagnostic text or console logs are written to `stdout` to maintain framing integrity.

### 1.2 Host Manifest & Origin Security
The native host registration manifest (`com.swiftget.native.json`) pins extension IDs explicitly:
```json
{
  "name": "com.swiftget.native",
  "description": "SwiftGet Native Messaging Host",
  "path": "SwiftGet.NativeHost.exe",
  "type": "stdio",
  "allowed_origins": [
    "chrome-extension://jidfbhpjcelebmiokljcmmljbfgiiolh/",
    "chrome-extension://swiftget-extension-id/"
  ]
}
```

---

## 2. Authenticated Loopback REST & WebSocket API

The loopback server runs inside `SwiftGet.Engine` to serve browser extensions and local CLI tooling when native messaging is not available.

### 2.1 Network Binding & Port Selection
- **Binding Address**: Strictly bound to `127.0.0.1` (loopback only). Never binds to `0.0.0.0` or public network adapters.
- **Port Allocation**: Dynamically selects an ephemeral port (or configured port, e.g. `52384`).

### 2.2 Authentication & Token Security
- **Token Generation**: On startup, the engine generates a 256-bit cryptographically secure random token (64 hex characters).
- **Storage**: Saved to `%LOCALAPPDATA%\SwiftGet\auth.token` with strict Windows DACL rights (`CurrentUser` FullControl only).
- **Validation**: Every request must provide the token in the `Authorization: Bearer <token>` header. Token validation uses constant-time hash comparison to prevent timing attacks.

### 2.3 CORS & Origin Policy
To protect against DNS rebinding, intranet scanning, and cross-site scripting attacks from web pages:
- `Origin: null` (from `file://` URIs and sandboxed iframes) is **explicitly rejected**.
- Wildcard `Access-Control-Allow-Origin: *` is **never emitted**.
- Only allowed extension schemes (`chrome-extension://`, `moz-extension://`) and loopback origins (`http://127.0.0.1`, `http://localhost`) receive valid CORS headers.

---

## 3. Protocol Message Schemas

### 3.1 Check Connection / Handshake
**Request:**
```json
{
  "action": "checkConnection"
}
```
**Response:**
```json
{
  "success": true,
  "version": "1.1.0",
  "torrentSupported": true,
  "authToken": "d8f3b20..."
}
```

### 3.2 Add Download (URL Interception)
**Endpoint**: `POST /api/v1/downloads` (or native host `addDownload`)
**Request:**
```json
{
  "action": "addDownload",
  "url": "https://example.com/files/archive.zip",
  "referrer": "https://example.com/downloads",
  "userAgent": "Mozilla/5.0 ...",
  "cookies": "session=token123; user=alice",
  "fileName": "archive.zip",
  "destinationPath": "archive.zip",
  "categoryId": "compressed",
  "queueId": "default",
  "startImmediately": true
}
```
**Response:**
```json
{
  "success": true,
  "downloadId": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
  "destinationPath": "D:\\Downloads\\Compressed\\archive.zip"
}
```

### 3.3 Add Magnet Link
**Endpoint**: `POST /api/v1/torrents/magnet` (or native host `addMagnet`)
**Request:**
```json
{
  "action": "addMagnet",
  "magnetUri": "magnet:?xt=urn:btih:3b245504cf5f11bbdbe1201cea6a6bf45a004e3a&dn=Ubuntu",
  "destinationPath": "D:\\Downloads\\Torrents",
  "startImmediately": true
}
```
**Response:**
```json
{
  "success": true,
  "downloadId": "3b245504cf5f11bbdbe1201cea6a6bf45a004e3a"
}
```

### 3.4 Add .torrent File (Base64)
**Endpoint**: `POST /api/v1/torrents/file` (or native host `addTorrentFile`)
**Request:**
```json
{
  "action": "addTorrentFile",
  "fileBytesBase64": "ZDEwOmFubm91bmNl...",
  "fileName": "distribution.torrent",
  "destinationPath": "D:\\Downloads\\Torrents",
  "startImmediately": true
}
```
*Note*: `fileBytesBase64` is capped at a maximum of 10 MB decoded to prevent memory exhaustion attacks.

### 3.5 Expired Link Refresh Protocol
When a pre-signed CDN URL expires (HTTP 403 / 410):
1. **Desktop Engine to Extension**:
```json
{
  "type": "REFRESH_LINK_REQ",
  "downloadId": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
  "originalUrl": "https://cdn.example.com/download?exp=1600000",
  "referrer": "https://example.com/download-page"
}
```
2. **Extension to Desktop Engine**:
```json
{
  "type": "REFRESH_LINK_RESP",
  "downloadId": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
  "newUrl": "https://cdn.example.com/download?exp=1700000",
  "cookies": "session=fresh_token"
}
```
