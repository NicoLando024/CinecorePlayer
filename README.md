# 🎬 Cinecore Player 2025

[![License: CC BY-NC-SA 4.0](https://img.shields.io/badge/License-CC%20BY--NC--SA%204.0-lightgrey.svg)](https://creativecommons.org/licenses/by-nc-sa/4.0/)
[![.NET 9.0](https://img.shields.io/badge/.NET-9.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/platform-Windows-0078D6?logo=windows&logoColor=white)](#%EF%B8%8F-system-requirements-end-users)
[![Status](https://img.shields.io/badge/status-alpha-orange)](#-project-status-truthful-current)
[![Build](https://img.shields.io/github/actions/workflow/status/NicoLando024/CinecorePlayer/build.yml?branch=main)](https://github.com/NicoLando024/CinecorePlayer/actions)
[![Downloads](https://img.shields.io/github/downloads/NicoLando024/CinecorePlayer/total.svg)](https://github.com/NicoLando024/CinecorePlayer/releases)
[![Stars](https://img.shields.io/github/stars/NicoLando024/CinecorePlayer.svg?style=social&label=Star)](https://github.com/NicoLando024/CinecorePlayer)

A **free**, **non-commercial** media player for Windows, written in **C# / .NET 9.0**, built on a **unified DirectShow engine** with smart HDR handling and multiple **video renderer backends** — **madVR**, **MPC Video Renderer (MPCVR)**, and **EVR**.

> **Terminology** — in this document, **Renderer** always means **video renderer backend** (e.g., *madVR*, *MPC Video Renderer*, *EVR*).
>
> **Note** — the legacy **VMR9** path has been removed.

---

## ⛳ Table of contents

- [🚦 Project status (truthful, current)](#-project-status-truthful-current)
- [✅ Implemented & working functions](#-implemented--working-functions)
  - [Core playback & navigation](#core-playback--navigation)
  - [Video pipeline](#video-pipeline)
  - [Audio pipeline](#audio-pipeline)
  - [Probing & metadata (FFmpeg)](#probing--metadata-ffmpeg)
  - [Overlays & UI](#overlays--ui)
- [🔊 Audio meters (PCM-only)](#-audio-meters-pcm-only)
- [⌨️ Keyboard shortcuts](#%EF%B8%8F-keyboard-shortcuts)
- [Important audio note — PCM vs Bitstream](#important-audio-note--pcm-vs-bitstream)
- [🗺️ Roadmap (when time allows)](#%EF%B8%8F-roadmap-when-time-allows)
- [💾 Distribution (Full Edition ZIP)](#-distribution-full-edition-zip)
- [🖥️ System requirements (end-users)](#%EF%B8%8F-system-requirements-end-users)
- [🚀 Quick start](#-quick-start)
- [🧯 Known issues (consolidated)](#-known-issues-consolidated)
- [🧩 Third-party software (summary)](#-third-party-software-summary)
- [🛠️ Build from source (developers)](#-build-from-source-developers)
- [🤝 Contributing (highest impact)](#-contributing-highest-impact)
- [👤 Credits & Acknowledgements](#-credits--acknowledgements)
- [📝 License](#-license)

---

## 🚦 Project status (truthful, current)

* ✅ **Playback engine**: audio/video paths (HDR & SDR) with live PCM/Bitstream detection & notifications.
* ✅ **HUD**: stable auto-hide, timeline, preview thumbnails; still minor renderer-dependent glitches.
* ✅ **Info overlay**: **works** and is generally accurate; **bitrate readouts are not guaranteed 100%** (approx./lag-prone).
* ✅ **Audio-only meters (PCM)**: **VU / Spectrum / Oscilloscope / Crest / Balance / Correlation**. Hidden on bitstream.
* ⚠️ **Code quality**: **messy / not optimized**; no blocking perf issues, but needs refactors.
* ⚠️ **Bitstream volume**: **ON/OFF** only (session volume applies only in PCM).
* ⚠️ **3D conversion (SBS/TAB → 2D)**: **EVR only**.
* ⚠️ **Language & Chapters**: can **break** if used **before** opening a movie (init-order bug); **work once a file is loaded**.
* ❌ **Subtitles**: menu present, but **rendering not reliable** yet (pipeline incomplete).
* ⚠️ **MPCVR black-screen**: known on some systems; fallbacks/logs exist but don’t always help.

> Bottom line: it **plays local files**, HUD/overlays are **usable**, info overlay **works** (bitrate may drift), audio meters are solid on **PCM**, but UX is **still unstable** and subtitles/3D remain limited.

---

## ✅ Implemented & working functions

### Core playback & navigation

* **Open file** (local paths), **Remove/Stop**.
* **Play/Pause** (Space), **seek ±10s** (Left/Right), **chapter Prev/Next** (PageDown/PageUp) — works **after** a file is opened.
* **Timeline scrubbing** with **thumbnail preview**.
* **Fullscreen toggle** (borderless; non-exclusive) with cursor-driven **HUD auto-hide**.

### Video pipeline

* **Renderer selection**: **madVR**, **MPCVR**, **EVR** (Auto mode: **HDR → madVR ➜ MPCVR**, **SDR → EVR** unless forced).
* **HDR modes**: **Auto** (passthrough/tone-map via renderer) and **Force SDR**.
* **3D utilities**: **SBS/TAB → 2D crop** (works with **EVR** only).
* **Snapshot**: **EVR/MF `GetCurrentImage()`** available (no standard API for windowed madVR/MPCVR).

### Audio pipeline

* **Bitstream heuristic** (AC-3 / E-AC-3 / TrueHD / DTS) with safe **PCM fallback**.
* **PCM vs Bitstream UI state**: the app reflects the **active mode** and adapts volume/meters accordingly.
* **Audio renderer picker** (DirectSound; HDMI-like hinting); session **volume** via CoreAudio when in **PCM**.
* **Bitstream volume**: by design treated as **ON/OFF** (PCM has fine control).

### Probing & metadata (FFmpeg)

* **Duration**, **video/audio codecs**, **bit-depth/pixel format**.
* **HDR flags**: color primaries / transfer characteristics (PQ/HLG, BT.2020, etc.).
* **Chapters** list.
* **On-the-fly thumbnails** for timeline preview.

### Overlays & UI

* **Layered overlay host** (true transparent top-level) for HUD/Info/Audio-only.
* **HUD**: timeline, preview thumbnails, ±10s jumps, chapters list, volume, fullscreen toggle.
* **Info overlay**: two columns (**VIDEO / AUDIO**) + **System** — **works**; **bitrate fields may be approximate or lagging**.
* **Audio-only overlay**:
  * **PCM** → **live meters** (VU, Spectrum in dBFS, Oscilloscope, Crest factor, Balance %, Correlation).
  * **Bitstream** → banner (meters disabled by design).
* **Context menu**: Renderer (madVR/MPCVR/EVR/Auto), HDR Auto/SDR, 3D Off/SBS/TAB, Audio Languages, **Chapters…**, **Info overlay** toggle.

---

## 🔊 Audio meters (PCM-only)

* **VU headroom** (0…+40 dB) with **peak-hold** and silence gate.
* **Spectrum** in **dBFS** (Hann window, coherent normalization, smoothed dynamic Y-scale).
* **Oscilloscope** L/R (autoscale ±amp, smoothed) + **downsampled ring buffer**.
* **Crest factor** (dB) = 20·log10(peak/RMS) — not floor-clamped.
* **Balance** from RMS (%, ±10% view).
* **Correlation** history (−1…+1) with DC-free Pearson.

> These meters are shown **only in PCM** (or audio-only PCM). On **bitstream** they’re intentionally **disabled** and volume is forced to 100%.

---

## ⌨️ Keyboard shortcuts

* **Space** – Play / Pause
* **F** – Fullscreen toggle (non-exclusive)
* **← / →** – **−10s / +10s**
* **PageDown / PageUp** – **Prev / Next chapter** *(works after file open)*
* **O** – **Open…** **S** – **Remove/Stop**

> Mouse wheel over the HUD adjusts **volume** (when visible). With bitstream it remains **ON/OFF**.

---

## Important audio note — PCM vs Bitstream

* **HDMI bitstream passthrough** (AC-3 / E-AC-3 / TrueHD / DTS) **when** the chain allows it; otherwise **PCM** decode is used. Heuristics prefer bitstream on “**HDMI-like**” devices and eligible codecs; they **fall back** to PCM when in doubt.
* **Meters** appear **only on PCM**; on **bitstream** meters are **disabled** and volume is forced to **100%** by design.

---

## 🗺️ Roadmap (when time allows)

* Refactor & code cleanup; **stable overlays/HUD**; **reliable subtitles**.
* **Exclusive fullscreen**; bitstream volume beyond ON/OFF.
* **madVR hotkeys bridge** (HDR/SDR & quality presets) and **Force PCM** wiring.
* **Audio-only overlay HUD piece** & polish (icons/layout).
* **Library**: local indexing/cache to **speed up grid**; improved folder management UI; **Favorites & Playlists**.
* **DLNA**: more robust discovery/browse; UI polish.
* **YouTube**: real integration; **URL** pane to accept **generic HTTP/streams** (not only YT).
* **Info overlay**: bitrate stabilization & sourcing improvements.
* **Network/URL playback** (SMB/NFS/UPnP/HTTP); real-time upscaling (scalers / ML).
* **RTX Video HDR**; **PCM DSP** (EQ, loudness, profiles).
* **Dolby Vision** *(technical/legal TBD)*; **3D MVC**.
* **madVR auto-update** (EULA-compliant).

---

## 💾 Distribution (Full Edition ZIP)

* **madVR** — included **unmodified** with the original EULA; **written permission** for **non-commercial** redistribution.
* **MPC Audio Renderer**, **MPC Video Renderer (MPCVR)**, **LAV Filters** — included.
* **FFmpeg** native DLLs — included (`ffmpeg/win-x64/*`).
* NuGet deps: **FFmpeg.AutoGen**, **DirectShowLib**.

All third-party licenses/EULAs are in `ThirdParty/`. Do **not** modify third-party binaries.

---

## 🖥️ System requirements (end-users)

* **OS:** Windows 11 (x64)
* **.NET:** .NET Desktop Runtime 9.0
* **HDR:** HDR-capable GPU & display; Windows HDR enabled
* **Audio:** for bitstream, **HDMI** to AVR/soundbar; otherwise **PCM** is fine
* **Disk:** ~300 MB (binaries + ThirdParty)

---

## 🚀 Quick start

1. Download the **Full Edition** ZIP (or clone & build if you’re a developer).
2. Extract (e.g., `C:\CinecorePlayer\`).
3. Run `CinecorePlayer2025.exe`.
4. Press **O** (or use the Splash button) and open a media file.

> Heads-up: see **Known Issues** — subtitles not reliable, 3D is EVR-only, overlays can glitch with some renderers.

---

## 🧯 Known issues (consolidated)

**Playback / Renderers**
* **madVR hotkeys** not currently routed → **HDR→SDR toggles & quality presets don’t apply** via hotkeys/UI bridge.
* **MPCVR black-screen** on some systems; fallback logic not always sufficient.
* No **exclusive fullscreen** (borderless only).

**Audio**
* **Force PCM** toggle not wired end-to-end yet → effect **inconsistent/ineffective**.
* **Bitstream volume = ON/OFF** only (by design); fine control only in PCM.
* **Meters** only in PCM; after device hot-plug a re-select/arming might be required.

**Overlays / HUD**
* **Audio-only overlay**: **a HUD piece is missing** (incomplete UI); meters OK in PCM.
* Occasional **focus/z-order** glitches between renderer and overlays (HUD/info repaint/timing).

**Info overlay**
* **Bitrate** can be **approximate/lagging**; with **some sources** it’s inaccurate. On typical movie files it’s **usually fine**.

**Subtitles / Languages / Chapters**
* **Subtitles** pipeline not reliable (menu present, render path incomplete).
* **Language & Chapter** selection can **break** if used **before** opening a file; fine **after** a file is loaded.

**Library / UI**
* **Library form**: not final aesthetically; **slow initial load** (indexing/caching not implemented yet).
* **Favorites** and **Playlists**: **not implemented**.
* **Settings** and **Credits**: **style polish** pending.

**DLNA / Network**
* **DLNA** discovery/browse **not robust**; sometimes fails to find devices; UI not final.
* **YouTube** integration **not implemented**; **URL pane currently only accepts YouTube links** (no generic HTTP yet).

**Misc**
* Snapshot via **EVR** only (`GetCurrentImage()`); no standard snapshot API for windowed **madVR/MPCVR**.

---

## 🧩 Third-party software (summary)

* **madVR** — Proprietary EULA (included unmodified; non-commercial permission granted)
* **MPC Audio Renderer** — GPL-3.0 (included)
* **MPC Video Renderer (MPCVR)** — GPL-3.0 (included)
* **LAV Filters** — GPL-2.0+ (included)
* **FFmpeg** — LGPL/GPL depending on build (included in `ffmpeg/win-x64`)
* **FFmpeg.AutoGen** — MIT (NuGet)
* **DirectShowLib** — MIT (NuGet)

Full texts are shipped under `ThirdParty/`.

---

## 🛠️ Build from source (developers)

**Requirements:** Windows 11 (x64), **.NET 9.0 SDK**, Visual Studio 2022 (Desktop .NET).
**NuGet:** `DirectShowLib`, `FFmpeg.AutoGen`.
**Project:** enable **/unsafe**.
**Runtime:** place FFmpeg DLLs under `ffmpeg/win-x64` before running from source.

```bash
git clone https://github.com/NicoLando024/CinecorePlayer.git
cd CinecorePlayer
# open CinecorePlayer.sln in Visual Studio
# set x64, enable /unsafe, Build & Run
