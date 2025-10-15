# 🎬 Cinecore Player 2025

[![License: CC BY-NC-SA 4.0](https://img.shields.io/badge/License-CC%20BY--NC--SA%204.0-lightgrey.svg)](https://creativecommons.org/licenses/by-nc-sa/4.0/)

A **free**, **non-commercial** media player for Windows, written in **C# / .NET 9.0**, built on a **unified DirectShow engine** with smart HDR handling and multiple renderer backends (**madVR**, **MPCVR**, **EVR**, **VMR9**).
The goal of Cinecore Player is to deliver **high-quality HDR and SDR playback** while keeping a clean, open-and-play user experience.

---

## 🚦 Project Status (truthful)

* ✅ **Playback engine:** audio and video paths (HDR and SDR) are implemented and functionally complete for many use cases.
* ⚠️ **Info overlay:** still **full of problems** — inconsistent values, stale readings and formatting bugs; consider it experimental.
* ⚠️ **HUD:** not stable and **not yet translated**. UI displays and overlay timing/opacity often glitch.
* ⚠️ **Keyboard shortcuts:** **NOT YET IMPLEMENTED**. They are planned but currently absent — do not rely on them.
* ⚠️ **MPC Video Renderer black screen issue:** known bug under investigation.
* ⚠️ **Many settings & usage areas are incomplete:** the Settings UI, some usage notes and advanced configuration screens are still TODO.
* 🪪 **madVR redistribution licensed:** Cinecore Player includes **madVR** under **explicit written permission** from **Mathias “madshi” Rauen** for **non-commercial redistribution**, provided the binaries remain **unmodified** and **include the original EULA**.

Practical note: the playback core works for local files and is usable for testing — but expect UX roughness and missing features.

---

## ✅ Implemented Features (what actually works today)

* **Unified DirectShow engine**

  * LAV Splitter + LAV Video/Audio wiring
  * Renderer selection: **madVR**, **MPC Video Renderer (MPCVR)**, **EVR**, **VMR9 (windowless)**
  * **HDR Auto / Force SDR** modes — prefers HQ renderers for HDR where possible
  * **Bitstream detection** heuristic (AC-3 / E-AC-3 / TrueHD / DTS) with PCM fallback
  * **Audio renderer picker** — prefers **MPC Audio Renderer** if installed
* **FFmpeg-powered media probe**

  * Duration; video/audio codecs; pixel format & bit depth; color primaries/transfer (HDR flags)
  * Chapter list + **thumbnail previews** (seek bar) — implemented but may show artifacts in some cases
* **UI overlays**

  * **HUD** (autohide) — present but unstable and not translated
  * **Info overlay (horizontal)** — implemented but full of problems (see Project Status)
  * **Debug overlay** — useful for development; shows negotiated media type dumps and log tail
  * **Splash** center panel (open file)
* **3D utilities:** **SBS** / **TAB** → 2D crop modes
* **Snapshots** on EVR/VMR9 paths; note: madVR/MPCVR windowed snapshot not standard
* **Core audio integration:** session volume mapping (safe with bitstream)
* **Dedicated audio path / player for advanced audio formats:** there is a specialized audio playback path (and UI) to handle multi-channel and object-based formats with specific presets and playback options (implemented but still evolving)

---

## Important audio note — PCM vs Bitstream

* Cinecore Player supports both:

  * **HDMI bitstream passthrough** to AVR/soundbar (AC-3 / E-AC-3 / TrueHD / DTS) when the selected audio renderer and system path allow it. The engine includes heuristics to prefer bitstream when the hardware looks like HDMI and the codec is a passthrough candidate.
  * **PCM output** (decoded to PCM) for systems or user choices where passthrough is not available or desired.
* The player includes a **dedicated audio playback path** (audio-only mode, special handling and UI) to better manage PCM rendering, advanced layouts and presets — useful for high-resolution multichannel audio testing.
* Do not assume only HDMI passthrough is supported — PCM is fully supported and often the safer fallback.

---

## 🗺️ Roadmap (next milestones)

* **360°/VR playback mode**
* **LAN / network playback** (SMB/NFS/UPnP/HTTP)
* **URL playback with on-the-fly upscaling**
* **Decrypted ISO** reading (Blu-ray/DVD) *(legal/DRM note: only for lawfully obtained, decrypted content)*
* **Real-time upscaling** pipeline (scalers / ML)
* **RTX Video HDR** integration
* **PCM audio enhancements**: EQ, loudness, presets/profiles
* **Dolby Vision** support *(profiles & pipeline TBD; subject to legal/technical feasibility)*
* **3D Frame-Packed (Blu-ray MVC)** playback/output
* **madVR auto-update script / mechanism** (handle time-limited builds)

> Many of the above items are in early design or research phase; dates and priorities will change.

---

## 💾 Distribution (what is included)

Cinecore Player **Full Edition** can be distributed as either a preinstalled portable package or as source. Current plan for “Full Edition” (what we ship in the ZIP):

* **madVR** — included **unmodified** and with the original EULA (explicit written permission obtained; non-commercial redistribution only).
* **MPC Audio Renderer** — included.
* **MPC Video Renderer (MPCVR)** — included. (Note: black-screen bug known.)
* **LAV Filters** — included.
* **FFmpeg** native DLLs — included (`ffmpeg/win-x64/*`).
* **FFmpeg.AutoGen**, **DirectShowLib** — used as NuGet packages / dependencies.

All third-party licenses and EULAs are included in `ThirdParty/`. Do not modify any third-party binaries.

---

## 🖥️ System requirements (end-users)

* **OS:** Windows 11 (x64)
* **.NET:** .NET Desktop Runtime 9.0
* **GPU/Display (if you want HDR):** HDR-capable GPU and display, Windows HDR enabled
* **Audio (if you want passthrough):** HDMI output to AVR/soundbar that supports your target codecs — but PCM output is also supported.
* **Disk:** ~300 MB for binaries + ThirdParty folder

---

## 🚀 Quick start (end-users)

1. Download the Full Edition ZIP (or clone & build if developer).
2. Extract the ZIP (e.g. `C:\CinecorePlayer\`).
3. Run `CinecorePlayer2025.exe`.
4. Open a media file.

> UX caveats: HUD and Info overlay have known issues. Settings pages are incomplete. Keyboard shortcuts are not yet functional.

---

## ⚙️ Settings & Usage Notes — CURRENTLY INCOMPLETE (TODO)

Many settings panels, advanced usage notes, and preference pages are *work in progress*. The current distribution includes basic menus and context options, but expect missing options and incomplete documentation in the following areas:

* Full localization of the HUD and overlays (HUD not translated) — TODO
* Detailed Settings UI for audio DSP/PCM presets — partial/placeholder only — TODO
* Advanced renderer tuning (madVR/MPCVR profile dialogs) — the player can detect and launch renderer config UIs but we do not ship preconfigured profiles — TODO
* Network/URL source configuration (SMB/NFS/UPnP) — not implemented — TODO
* Auto-update UI for madVR — backend planned but not user-facing yet — TODO

We intentionally ship a minimal, working surface and will iterate the Settings UX in subsequent releases.

---

## 🧩 Third-party software (summary)

* **madVR** — Proprietary EULA (included unmodified; permission granted for non-commercial redistribution)
* **MPC Audio Renderer** — GPL-3.0 (included)
* **MPC Video Renderer (MPCVR)** — GPL-3.0 (included) — *known black-screen issue*
* **LAV Filters** — GPL-2.0+ (included)
* **FFmpeg** — LGPL/GPL depending on build (included in `ffmpeg/win-x64`)
* **FFmpeg.AutoGen** — MIT (NuGet)
* **DirectShowLib** — MIT (NuGet)

Full license texts and EULAs are included in `ThirdParty/`.

---

## 🛠️ Build from source (developers)

> For contributors only — end users do not need to build.

Requirements:

* Windows 11 (x64)
* .NET 9.0 SDK
* Visual Studio 2022 (Desktop development with .NET)
* NuGet packages: `DirectShowLib`, `FFmpeg.AutoGen`
* Project: enable `/unsafe`
* Place FFmpeg DLLs in `ffmpeg/win-x64` when running from source

Build steps:

```bash
git clone https://github.com/NicoLando024/CinecorePlayer.git
cd CinecorePlayer
# open CinecorePlayer.sln in Visual Studio
# set x64 configuration, enable /unsafe, Build & Run
```

---

## 🤝 Contributing

Contributions are welcome and appreciated. The highest impact areas right now:

* Fixing HUD / Info overlay stability and translations
* Resolving MPCVR black-screen cases or providing mitigations/fallbacks
* Implementing keyboard shortcuts and improving input handling
* Building madVR auto-update safely (respecting EULA & official links)
* Network playback and URL upscaling pipelines
* PCM DSP (EQ/loudness) and dedicated audio UI polish

Please open issues and PRs on GitHub. By contributing you agree to release your changes under CC BY-NC-SA 4.0.

---

## 👤 Credits & Acknowledgements

**Author / Maintainer**
Niccolò Landolfi — Independent developer & computer science student
Email: [nicolando024@gmail.com](mailto:nicolando024@gmail.com)
GitHub: [https://github.com/NicoLando024](https://github.com/NicoLando024)

**Special Thanks & Permissions**

* **Mathias “madshi” Rauen** — many thanks for his prompt help and for granting **explicit written permission** to redistribute **madVR** unmodified for **non-commercial** use (EULA included). This permission is saved in the repository under `docs/permissions/madvr/` (PDF + text).
* MPC-HC / MPC-BE teams — MPC Audio Renderer & MPC Video Renderer.
* Hendrik Leppkes — LAV Filters.
* FFmpeg contributors — FFmpeg libraries.
* The wider .NET and DirectShow communities.

---

## 📝 License

**Cinecore Player** © 2025 Niccolò Landolfi
Licensed under **Creative Commons Attribution–NonCommercial–ShareAlike 4.0 International (CC BY-NC-SA 4.0)**

You may:

* **Share** — copy and redistribute this work
* **Adapt** — remix, transform, and build upon it

Under the following terms:

* **Attribution** — credit **Niccolò Landolfi**
* **NonCommercial** — no commercial use
* **ShareAlike** — same license for derivatives

Full text: [https://creativecommons.org/licenses/by-nc-sa/4.0/](https://creativecommons.org/licenses/by-nc-sa/4.0/)
