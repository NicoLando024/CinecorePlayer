# 🎬 Cinecore Player 2025

[![License: CC BY-NC-SA 4.0](https://img.shields.io/badge/License-CC%20BY--NC--SA%204.0-lightgrey.svg)](https://creativecommons.org/licenses/by-nc-sa/4.0/)

A **free** media player for Windows written in **C# / .NET 9.0**, built on a **unified DirectShow engine** with smart HDR handling and multiple renderer backends (**EVR**, **MPCVR**, **VMR9**, **madVR**\*).  
The player targets **high-quality playback** while keeping a simple “open-and-play” UX.

> \* **madVR** is optional and **not included**. Integration will be enabled only with explicit permission from the author.

---

## 🚦 Project status

- ✅ **Playback core is solid**: **audio and video are complete** in both **HDR** and **SDR** paths (with renderer selection and bitstream heuristics).  
- ⚠️ **HUD / UI overlays are unstable**: visual glitches, timing/opacity issues, and occasional layout bugs are known. Expect rough edges while the UX is being refactored.
- ⚠️ **MPC Video Renderer Black screen issue**.

If you can live with UI quirks, the underlying player already works well for local files.

---

## ✅ Implemented features

- **Unified DirectShow engine**
  - LAV Splitter + LAV Video/Audio wiring
  - Renderer selection: **madVR**, **MPC Video Renderer (MPCVR)**, **EVR**, **VMR9 (windowless)**
  - **HDR Auto / Force SDR**: prefers HQ renderers for HDR, clean fallback chain for SDR
  - **Bitstream detection** heuristic (AC-3 / E-AC-3 / TrueHD / DTS) with PCM fallback
  - **Audio renderer picker** — prefers **MPC Audio Renderer** if installed; otherwise DirectSound/default
- **FFmpeg-powered media probe**
  - Duration; video/audio codecs; pixel format & bit depth; color primaries/transfer (HDR flags)
  - Chapter list + **thumbnail previews** on the seek bar
- **UI overlays**
  - **HUD** (autohide): play/pause/seek, +/-10s skip, chapter prev/next, volume bar, preview thumbs
  - **Info overlay (horizontal)**: IN/OUT formats, codec, color data, renderer, HDR mode
  - **Debug overlay**: negotiated media type dump, log tail, window/size info
  - **Splash** center panel (open file)
- **3D utilities**: **SBS** / **TAB** → 2D crop modes
- **Snapshots** on EVR/VMR9 paths; video window management across backends
- **Core audio integration**: session volume mapping (safe with bitstream)

---

## 🗺️ Roadmap (next milestones)

- **360°/VR playback mode**
- **LAN / network playback** (SMB/NFS/UPnP/HTTP)
- **URL playback with on-the-fly upscaling**
- **Decrypted ISO** reading (Blu-ray/DVD) *(legal/DRM note: only for lawfully obtained, decrypted content)*
- **Real-time upscaling** pipeline (scalers / ML)
- **RTX Video HDR** integration
- **PCM audio enhancements**: EQ, loudness, presets/profiles
- **Dolby Vision** support *(profiles & pipeline TBD; subject to legal/technical feasibility)*
- **3D Frame-Packed (Blu-ray MVC)** playback/output
- - **Keyboard**
  - `Space` play/pause • `F` fullscreen • `←/→` seek 10s • `PgUp/PgDn` chapters  
  - `O` open • `S` stop • `D` debug overlay • `I` info overlay

Contributions & suggestions welcome! See **Contributing**.

---

## 💾 Downloads

Pre-release builds will be provided as a **portable ZIP** on the Releases page.  
For now, you can run from source or from a manually packaged folder (**Runtime layout** below).

---

## 🖥️ System requirements (end-users)

- **OS:** Windows 11 (x64)  
- **.NET:** **.NET Desktop Runtime 9.0**  
- **DirectShow filters (install system-wide):**
  - **LAV Filters** (recommended) — required for most formats
  - **MPC Audio Renderer** *(optional, recommended)* — low-latency WASAPI / bit-perfect / bitstream
  - **MPC Video Renderer (MPCVR)** *(optional)* — video renderer
  - **madVR** *(optional)* — HQ video renderer (not bundled; separate license/permission)
  - **EVR/VMR9** — built into Windows
- **GPU/Display (feature-dependent):**
  - HDR playback: HDR-capable GPU & display, Windows HDR enabled
  - Bitstream audio: HDMI to AVR/soundbar that supports AC-3/E-AC-3/TrueHD/DTS

### Runtime layout (portable folder)
The app looks for FFmpeg binaries here at startup:
/CinecorePlayer2025.exe
/ffmpeg/win-x64/avcodec-.dll
/ffmpeg/win-x64/avformat-.dll
/ffmpeg/win-x64/avutil-.dll
/ffmpeg/win-x64/swresample-.dll
/ffmpeg/win-x64/swscale-*.dll
> LAV/MPCVR/MPC Audio Renderer/madVR must be **installed/registered** if you want to use them.

---

## 🚀 Quick start (end-users)

1) Install **.NET Desktop Runtime 9.0**  
2) Install **LAV Filters**  
3) *(Optional, recommended)* Install **MPC Audio Renderer**  
4) *(Optional)* Install **MPCVR** and/or **madVR**  
5) Extract the **portable ZIP** and ensure **FFmpeg DLLs** are under `ffmpeg/win-x64`  
6) Launch `CinecorePlayer2025.exe` → **Open** a media file

---

## ⚙️ Settings & UX notes

- **Video renderer:** right-click → **Renderer video** → Auto / madVR / MPCVR / EVR / VMR9  
- **HDR mode:** right-click → **Immagine (HDR)** → Auto / Force SDR (renderer tone-map)  
- **Audio renderer:** right-click → **Audio renderer** → choose **MPC Audio Renderer** (recommended) or another device  
- **Tracks & subtitles:** language/subtitle menus via IAMStreamSelect  
- **Chapters:** context menu → **Capitoli…**

---

## 🧩 Third-party software

- **LAV Filters** — demuxing/decoding  
- **FFmpeg** via **FFmpeg.AutoGen** (native DLLs shipped in `ffmpeg/win-x64`)  
- **MPC Audio Renderer** *(optional, recommended)* — audio renderer (WASAPI)  
- **MPC Video Renderer (MPCVR)** *(optional)* — video renderer  
- **madVR** *(optional)* — high-quality video renderer (external, proprietary license)

> Each third-party component is under its own license. **madVR is not part of this repository**.

---

## 🛠️ Build from source (developers)

> For **developers** only (not end-users).

- **OS:** Windows 11 (x64)  
- **SDK:** .NET **9.0 SDK**  
- **IDE:** Visual Studio 2022 (latest), “Desktop development with .NET”  
- **NuGet:** `DirectShowLib (>= 2.1.0)`, `FFmpeg.AutoGen (>= 6.x)`  
- **Project:** enable **`/unsafe`**  
- **FFmpeg:** place native DLLs under `ffmpeg/win-x64` (see Runtime layout)


