# 🎬 Cinecore Player 2025

[![License: CC BY-NC-SA 4.0](https://img.shields.io/badge/License-CC%20BY--NC--SA%204.0-lightgrey.svg)](https://creativecommons.org/licenses/by-nc-sa/4.0/)

A **free**, **non‑commercial** media player for Windows, written in **C# / .NET 9.0**, built on a **unified DirectShow engine** with smart HDR handling and multiple **video renderer backends** — **madVR**, **MPC Video Renderer (MPCVR)**, and **EVR**.

> **Terminology** — in this document, **Renderer** always means **video renderer backend** (e.g., *madVR*, *MPC Video Renderer*, *EVR*).
>
> **Note** — the legacy **VMR9** path has been removed.

---

## 🚦 Project status (truthful, current)

* ✅ **Playback engine**: audio/video paths (HDR & SDR) with live PCM/Bitstream detection & notifications.
* ✅ **HUD**: stable auto‑hide, timeline, preview thumbnails; still minor renderer‑dependent glitches.
* ✅ **Info overlay**: **works** and is generally accurate; **bitrate readouts are not guaranteed 100%** (approx./lag‑prone).
* ✅ **Audio‑only meters (PCM)**: **VU / Spectrum / Oscilloscope / Crest / Balance / Correlation**. Hidden on bitstream.
* ⚠️ **Code quality**: **messy / not optimized**; no blocking perf issues, but needs refactors.
* ⚠️ **Bitstream volume**: **ON/OFF** only (session volume applies only in PCM).
* ⚠️ **3D conversion (SBS/TAB → 2D)**: **EVR only**.
* ⚠️ **Language & Chapters**: can **break** if used **before** opening a movie (init‑order bug); **work once a file is loaded**.
* ❌ **Subtitles**: menu present, but **rendering not reliable** yet (pipeline incomplete).
* ⚠️ **MPCVR black‑screen**: known on some systems; fallbacks/logs exist but don’t always help.

> Bottom line: it **plays local files**, HUD/overlays are **usable**, info overlay **works** (bitrate may drift), audio meters are solid on **PCM**, but UX is **still unstable** and subtitles/3D remain limited.

---

## ✅ Implemented & working functions

### Core playback & navigation

* **Open file** (local paths), **Remove/Stop**.
* **Play/Pause** (Space), **seek ±10s** (Left/Right), **chapter Prev/Next** (PageDown/PageUp) — works **after** a file is opened.
* **Timeline scrubbing** with **thumbnail preview**.
* **Fullscreen toggle** (borderless; non‑exclusive) with cursor‑driven **HUD auto‑hide**.

### Video pipeline

* **Renderer selection**: **madVR**, **MPCVR**, **EVR** (Auto mode: **HDR → madVR ➜ MPCVR**, **SDR → EVR** unless forced).
* **HDR modes**: **Auto** (passthrough/tone‑map via renderer) and **Force SDR**.
* **3D utilities**: **SBS/TAB → 2D crop** (works with **EVR** only).
* **Snapshot**: **EVR/MF `GetCurrentImage()`** available (no standard API for windowed madVR/MPCVR).

### Audio pipeline

* **Bitstream heuristic** (AC‑3 / E‑AC‑3 / TrueHD / DTS) with safe **PCM fallback**.
* **PCM vs Bitstream UI state**: the app reflects the **active mode** and adapts volume/meters accordingly.
* **Audio renderer picker** (DirectSound; HDMI‑like hinting); session **volume** via CoreAudio when in **PCM**.
* **Bitstream volume**: by design treated as **ON/OFF** (PCM has fine control).

### Probing & metadata (FFmpeg)

* **Duration**, **video/audio codecs**, **bit‑depth/pixel format**.
* **HDR flags**: color primaries / transfer characteristics (PQ/HLG, BT.2020, etc.).
* **Chapters** list.
* **On‑the‑fly thumbnails** for timeline preview.

### Overlays & UI

* **Layered overlay host** (true transparent top‑level) for HUD/Info/Audio‑only.
* **HUD**: timeline, preview thumbnails, ±10s jumps, chapters list, volume, fullscreen toggle.
* **Info overlay**: two columns (**VIDEO / AUDIO**) + **System** — **works**; **bitrate fields may be approximate or lagging**.
* **Audio‑only overlay**:

  * **PCM** → **live meters** (VU, Spectrum in dBFS, Oscilloscope, Crest factor, Balance %, Correlation).
  * **Bitstream** → banner (meters disabled by design).
* **Context menu**: Renderer (madVR/MPCVR/EVR/Auto), HDR Auto/SDR, 3D Off/SBS/TAB, Audio Languages, **Chapters…**, **Info overlay** toggle.

---

## 🔊 Audio meters (PCM‑only)

* **VU headroom** (0…+40 dB) with **peak‑hold** and silence gate.
* **Spectrum** in **dBFS** (Hann window, coherent normalization, smoothed dynamic Y‑scale).
* **Oscilloscope** L/R (autoscale ±amp, smoothed) + **downsampled ring buffer**.
* **Crest factor** (dB) = 20·log10(peak/RMS) — not floor‑clamped.
* **Balance** from RMS (%, ±10% view).
* **Correlation** history (−1…+1) with DC‑free Pearson.

> These meters are shown **only in PCM** (or audio‑only PCM). On **bitstream** they’re intentionally **disabled** and volume is forced to 100%.

---

## ⌨️ Keyboard shortcuts

* **Space** – Play / Pause
* **F** – Fullscreen toggle (non‑exclusive)
* **← / →** – **−10s / +10s**
* **PageDown / PageUp** – **Prev / Next chapter** *(works after file open)*
* **O** – **Open…** **S** – **Remove/Stop**

> Mouse wheel over the HUD adjusts **volume** (when visible). With bitstream it remains **ON/OFF**.

---

## Important audio note — PCM vs Bitstream

* **HDMI bitstream passthrough** (AC‑3 / E‑AC‑3 / TrueHD / DTS) **when** the chain allows it; otherwise **PCM** decode is used. Heuristics prefer bitstream on “**HDMI‑like**” devices and eligible codecs; they **fall back** to PCM when in doubt.
* **Meters** appear **only on PCM**; on **bitstream** meters are **disabled** and volume is forced to **100%** by design.

---

## 🗺️ Roadmap (when time allows)

* Refactor & code cleanup; **stable overlays/HUD**; **reliable subtitles**.
* **Exclusive fullscreen**; bitstream volume beyond ON/OFF.
* **Meters robustness** (device changes, WASAPI quirks) and Info overlay bitrate stabilization.
* **Network/URL playback** (SMB/NFS/UPnP/HTTP); real‑time upscaling (scalers / ML).
* **RTX Video HDR**; **PCM DSP** (EQ, loudness, profiles).
* **Dolby Vision** *(technical/legal TBD)*; **3D MVC**.
* **madVR auto‑update** (EULA‑compliant).

---

## 💾 Distribution (Full Edition ZIP)

* **madVR** — included **unmodified** with the original EULA; **written permission** for **non‑commercial** redistribution.
* **MPC Audio Renderer**, **MPC Video Renderer (MPCVR)**, **LAV Filters** — included.
* **FFmpeg** native DLLs — included (`ffmpeg/win-x64/*`).
* NuGet deps: **FFmpeg.AutoGen**, **DirectShowLib**.

All third‑party licenses/EULAs are in `ThirdParty/`. Do **not** modify third‑party binaries.

---

## 🖥️ System requirements (end‑users)

* **OS:** Windows 11 (x64)
* **.NET:** .NET Desktop Runtime 9.0
* **HDR:** HDR‑capable GPU & display; Windows HDR enabled
* **Audio:** for bitstream, **HDMI** to AVR/soundbar; otherwise **PCM** is fine
* **Disk:** ~300 MB (binaries + ThirdParty)

---

## 🚀 Quick start

1. Download the **Full Edition** ZIP (or clone & build if you’re a developer).
2. Extract (e.g., `C:\CinecorePlayer\`).
3. Run `CinecorePlayer2025.exe`.
4. Press **O** (or use the Splash button) and open a media file.

> Heads‑up: see **Known Issues** — subtitles not reliable, 3D is EVR‑only, overlays can glitch with some renderers.

---

## 🧯 Known issues (consolidated)

* HUD/overlays can fight with certain renderers (focus, z‑order, repaint/timing).
* **Info overlay bitrate** can be **approximate/lagging**; other fields generally correct.
* Subtitles selection often ineffective (pipeline not fully wired).
* Language & Chapter selection may break if used **before** opening a file.
* 3D→2D conversion (SBS/TAB) works **only** with EVR.
* Audio‑only meters: **PCM‑only**; after device changes they may require re‑selecting/arming the audio device.
* Bitstream volume is **ON/OFF** only.
* No **exclusive** fullscreen; only borderless fullscreen.
* MPCVR black‑screen on some systems.

---

## 🧩 Third‑party software (summary)

* **madVR** — Proprietary EULA (included unmodified; non‑commercial permission granted)
* **MPC Audio Renderer** — GPL‑3.0 (included)
* **MPC Video Renderer (MPCVR)** — GPL‑3.0 (included)
* **LAV Filters** — GPL‑2.0+ (included)
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
```

---

## 🤝 Contributing (highest impact)

* **Subtitles** pipeline & Language/Chapter init‑order bug.
* Overlay/HUD stability (focus/z‑order, repaint, timing, opacity).
* **Exclusive fullscreen**; bitstream volume beyond ON/OFF.
* Info overlay: bitrate stabilization & sourcing improvements; developer‑friendly debug.
* MPCVR black‑screen mitigations / robust fallbacks.
* Network/URL playback, ML upscaling, PCM DSP & audio UI.

By contributing you agree to **CC BY‑NC‑SA 4.0**.

---

## 👤 Credits & Acknowledgements

**Author / Maintainer**
Niccolò Landolfi — Independent developer & CS student
Email: [nicolando024@gmail.com](mailto:nicolando024@gmail.com)
GitHub: [https://github.com/NicoLando024](https://github.com/NicoLando024)

**Special thanks & permissions**

* **Mathias “madshi” Rauen** — for support and **explicit written permission** to redistribute **madVR** unmodified for **non‑commercial** use (EULA included). Permission is stored in `docs/permissions/madvr/` (PDF + text).
* MPC‑HC / MPC‑BE teams — MPC Audio Renderer & MPC Video Renderer.
* Hendrik Leppkes — LAV Filters.
* FFmpeg contributors.
* The .NET & DirectShow communities.

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

Full text: [https://creativecommons.org/licenses/by-nc-sa/4.0/](https://creativecommons.org/licenses/by-nc-sa/4.0/)
