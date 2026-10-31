---
title: winui3-window-converters-and-fdm-details-panel
author: SwiftGet
date: 2026-10-30
branch: master
id: l-9187ab98
scope: repo
status: active
src: agent
touches:
  - extension/background.js
  - extension/content.js
  - extension/popup.html
  - extension/popup.js
  - installer/README.md
  - installer/SwiftGet.Package/Package.wxs
  - installer/assets/com.swiftget.downloader.chrome.json
  - installer/assets/com.swiftget.native.json
  - installer/winget/manifests/s/SwiftGet/SwiftGet/1.0.0/SwiftGet.SwiftGet.installer.yaml
  - scripts/package.ps1
---

In WinUI 3, Window is not a FrameworkElement, so bindings with Converter on Window root fail with CS1503 in SetConverterLookupRoot; expose Visibility directly from ViewModel instead. Inside DataTemplate, x:Bind defaults to OneWay and requires INotifyPropertyChanged on x:DataType unless OneTime or dynamic Binding is used. For torrent piece grids with tens of thousands of pieces, rendering via a custom WriteableBitmap canvas downsampled to <=1500 cells renders in 0.1ms with zero UI visual tree overhead.
