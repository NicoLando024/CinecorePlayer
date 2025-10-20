# 🎬 Cinecore Player 2025

[![License: CC BY-NC-SA 4.0](https://img.shields.io/badge/License-CC%20BY--NC--SA%204.0-lightgrey.svg)](https://creativecommons.org/licenses/by-nc-sa/4.0/)

A **free**, **non‑commercial** media player for Windows, written in **C# / .NET 9.0**, built on a **unified DirectShow engine** with smart HDR handling and multiple **video renderer backends** — **madVR**, **MPC Video Renderer (MPCVR)**, and **EVR**.

> **Terminology** — in this document, **Renderer** always means **video renderer backend** (e.g., *madVR*, *MPC Video Renderer*, *EVR*).

> **Note** — the legacy **VMR9** path has been removed.

---

## 🚦 Project status (truthful, current)

* ✅ **Playback engine**: audio/video paths (HDR & SDR).
* ⚠️ **HUD**: fairly feature‑complete
* ⚠️ **Code quality**: **messy / not optimized**; no obvious performance issues, but it needs refactoring.
* ⚠️ **Bitstream volume**: **binary ON/OFF** only (no fine‑grained control on passthrough).
* ⚠️ **Info overlay**: **NOT WORKING**.
* ⚠️ **3D conversion (SBS/TAB → 2D)**: **works only with EVR**.
* ⚠️ **Language & Chapters**: get **stuck/blocked** if selected **before** opening a movie (initialization bug).
* ❌ **Subtitles**: **not working** (selection ineffective / pipeline not wired yet).
* ⚠️ **MPCVR black‑screen**: known issue on some systems; fallbacks/logs exist but don’t always help.

> Bottom line: it **plays local files** and is useful for testing, but UX is **unstable** and several key features are **missing or buggy**.

---

## ✅ What works today

### DirectShow unified engine
- LAV Splitter + LAV Video/Audio wiring.
- Video renderer selection: **madVR**, **MPCVR**, **EVR**.
- **Auto order**: **HDR → madVR ➜ MPCVR**, **SDR → EVR** (unless manually forced).
- **HDR Auto** / **Force SDR** (tone‑mapping via madVR/MPCVR).

### Audio
- **Bitstream heuristic** (AC‑3 / E‑AC‑3 / TrueHD / DTS) with safe **PCM fallback**.
- **Audio renderer picker** (DirectSound) with **HDMI?** hint and shortcut to Windows Sound Settings.
- **Session volume** (DirectShow + CoreAudio). *Note:* on bitstream it’s **ON/OFF** only.

### FFmpeg‑powered media probe
- Duration; video/audio codecs; bit depth & pixel format; **color primaries/transfer** (HDR flags).
- **Chapters**.
- **Timeline thumbnails** (frame extraction on the fly).

### UI / overlays
- **Loading overlay** → **Splash** (3 icons; Settings/Info are placeholders).
- **HUD** with auto‑hide, timeline, preview thumbnails, ±10s jumps, chapters, volume, fullscreen (renderer‑dependent glitches remain).
- **Info overlay** (two columns **VIDEO / AUDIO** + **System**). Data can be unreliable.
- **Audio‑Only overlay** (center banner/icons) — known visual bug.

### 3D / utilities
- **SBS** and **TAB** → **2D crop** (**EVR only**).

### Snapshots
- **EVR/MF**: `GetCurrentImage()` works. *(Windowed madVR/MPCVR: no standard API).*

### Context menu
- Renderer (madVR / MPCVR / EVR / Auto), **HDR Auto/SDR**, 3D Off/SBS/TAB.
- Audio languages / Subtitles (**currently broken**) + **Chapters…**.
- **Info overlay** toggle.

---

## ⌨️ Keyboard shortcuts

- **Space** – Play / Pause  
- **F** – Fullscreen toggle (non‑exclusive)  
- **← / →** – **−10s / +10s**  
- **PageDown / PageUp** – **Prev / Next chapter** *(mind the initialization bug)*  
- **O** – **Open…** **S** – **Remove/Stop**

> Mouse wheel over the HUD adjusts **volume** (when visible). With bitstream it remains **ON/OFF**.

---

## Important audio note — PCM vs Bitstream

- **HDMI bitstream passthrough** (AC‑3 / E‑AC‑3 / TrueHD / DTS) **when** the chain allows it; otherwise **PCM** decode is used.  
- The heuristic prefers bitstream on “**HDMI‑like**” devices and eligible codecs; it falls back to **PCM** when in doubt.

---

## 🗺️ Roadmap (when time allows)

- Refactor & code cleanup; **stable overlays/HUD**; **working subtitles**.
- **Exclusive fullscreen**; better volume handling on bitstream.
- **Network/URL playback** (SMB/NFS/UPnP/HTTP); real‑time upscaling (scalers / ML).
- **RTX Video HDR**; **PCM DSP** (EQ, loudness, profiles).
- **Dolby Vision** *(technical/legal TBD)*; **3D MVC**.
- **madVR auto‑update** (EULA‑compliant).

---

## 💾 Distribution (Full Edition ZIP)

- **madVR** — included **unmodified** with the original EULA; **written permission** for **non‑commercial** redistribution.
- **MPC Audio Renderer**, **MPC Video Renderer (MPCVR)**, **LAV Filters** — included.
- **FFmpeg** native DLLs — included (`ffmpeg/win-x64/*`).  
- NuGet deps: **FFmpeg.AutoGen**, **DirectShowLib**.

All third‑party licenses/EULAs are in `ThirdParty/`. Do **not** modify third‑party binaries.

---

## 🖥️ System requirements (end‑users)

- **OS:** Windows 11 (x64)  
- **.NET:** .NET Desktop Runtime 9.0  
- **HDR:** HDR‑capable GPU & display; Windows HDR enabled  
- **Audio:** for bitstream, **HDMI** to AVR/soundbar; otherwise **PCM** is fine  
- **Disk:** ~300 MB (binaries + ThirdParty)

---

## 🚀 Quick start

1. Download the **Full Edition** ZIP (or clone & build if you’re a developer).  
2. Extract (e.g., `C:\CinecorePlayer\`).  
3. Run `CinecorePlayer2025.exe`.  
4. Press **O** (or use the Splash button) and open a media file.

> Heads‑up: the **Known Issues** below apply — subtitles don’t work, info can be wrong, 3D is EVR‑only, overlays can glitch with some renderers.

---

## 🧯 Known issues (consolidated)

- HUD/overlays fight with certain renderers (focus, z‑order, repaint, opacity/timing).  
- Info overlay incomplete/inaccurate; values may show *n/a* or be wrong.  
- Subtitles selection does not take effect.  
- Language & Chapter selection break if used **before** opening a file.  
- 3D→2D conversion (SBS/TAB) works **only** with EVR.  
- Audio‑only mode hides parts of the UI due to a drawing bug.  
- Bitstream volume is **ON/OFF** only.  
- No **exclusive** fullscreen; only borderless fullscreen.  
- MPCVR black‑screen on some systems.  
- Codebase needs cleanup; not performance‑critical right now.

---

## 🧩 Third‑party software (summary)

- **madVR** — Proprietary EULA (included unmodified; non‑commercial permission granted)  
- **MPC Audio Renderer** — GPL‑3.0 (included)  
- **MPC Video Renderer (MPCVR)** — GPL‑3.0 (included)  
- **LAV Filters** — GPL‑2.0+ (included)  
- **FFmpeg** — LGPL/GPL depending on build (included in `ffmpeg/win-x64`)  
- **FFmpeg.AutoGen** — MIT (NuGet)  
- **DirectShowLib** — MIT (NuGet)

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
```

---

## 🤝 Contributing (highest impact)

- **Subtitles** pipeline & Language/Chapter init‑order bug.  
- Overlay/HUD stability (focus/z‑order, repaint, timing, opacity).  
- **Exclusive fullscreen**; bitstream volume beyond ON/OFF.  
- Info overlay: data sourcing & accuracy; developer‑friendly debug.  
- MPCVR black‑screen mitigations / robust fallbacks.  
- Network/URL playback, ML upscaling, PCM DSP & audio UI.

By contributing you agree to **CC BY‑NC‑SA 4.0**.

---

## 👤 Credits & Acknowledgements

**Author / Maintainer**  
Niccolò Landolfi — Independent developer & CS student  
Email: [nicolando024@gmail.com](mailto:nicolando024@gmail.com)  
GitHub: [https://github.com/NicoLando024](https://github.com/NicoLando024)

**Special thanks & permissions**  
- **Mathias “madshi” Rauen** — for support and **explicit written permission** to redistribute **madVR** unmodified for **non‑commercial** use (EULA included). Permission is stored in `docs/permissions/madvr/` (PDF + text).  
- MPC‑HC / MPC‑BE teams — MPC Audio Renderer & MPC Video Renderer.  
- Hendrik Leppkes — LAV Filters.  
- FFmpeg contributors.  
- The .NET & DirectShow communities.

---

## 📝 License

**Cinecore Player** © 2025 Niccolò Landolfi  
Licensed under **Creative Commons Attribution–NonCommercial–ShareAlike 4.0 International (CC BY‑NC‑SA 4.0)**

You may:

* **Share** — copy and redistribute this work  
* **Adapt** — remix, transform, and build upon it

Under the following terms:

* **Attribution** — credit **Niccolò Landolfi**  
* **NonCommercial** — no commercial use  
* **ShareAlike** — the same license for derivatives

Full text: https://creativecommons.org/licenses/by-nc-sa/4.0/
