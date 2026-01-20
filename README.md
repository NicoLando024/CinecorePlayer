# 🎬 Cinecore Player 2025

[![License: CC BY-NC-SA 4.0](https://img.shields.io/badge/License-CC%20BY--NC--SA%204.0-lightgrey.svg)](https://creativecommons.org/licenses/by-nc-sa/4.0/)
[![.NET 9.0](https://img.shields.io/badge/.NET-9.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/platform-Windows-0078D6?logo=windows&logoColor=white)](#%EF%B8%8F-system-requirements-end-users)
[![Status](https://img.shields.io/badge/status-alpha-orange)](#-project-status-truthful-current)
[![Build](https://img.shields.io/github/actions/workflow/status/NicoLando024/CinecorePlayer/build.yml?branch=main)](https://github.com/NicoLando024/CinecorePlayer/actions)
[![Downloads](https://img.shields.io/github/downloads/NicoLando024/CinecorePlayer/total.svg)](https://github.com/NicoLando024/CinecorePlayer/releases)
[![Stars](https://img.shields.io/github/stars/NicoLando024/CinecorePlayer.svg?style=social&label=Star)](https://github.com/NicoLando024/CinecorePlayer)

A **free**, **non‑commercial** media player for **Windows**, built in **C# / .NET 9.0** and powered by a **unified DirectShow engine**.  
It features intelligent **HDR management** and supports multiple high‑end **video renderer backends**, including **madVR**, **MPC Video Renderer (MPCVR)**, and **EVR**.

Designed also for **audiophiles**, the player includes a dedicated **Audio Mode** with real‑time visualizations such as **oscilloscope** and **spectrum analyzer**.  
Audio output supports both **bitstream** and **PCM**, with full compatibility for **exclusive** and **non‑exclusive** modes.

---

## 📸 Screenshots

![Home](Screenshots/home.png)

![Library](Screenshots/library.png)

![Audio Graphs](Screenshots/audio-graphs.png)

![Video Player](Screenshots/video-player.png)

![Info Overlay](Screenshots/info-overlay.png)

---

## ✅ Working features

- **Video playback via madVR and EVR**  
  Core playback is stable on both renderers.

- **Audio playback (PCM & Bitstream)**  
  Standard playback works reliably across formats.

- **Audio graphs**  
  Rendering is functional; overall accuracy appears correct, pending further validation.

- **Photo viewer**  
  Fully operational, with planned quality‑of‑life improvements.

- **HUD / On‑Screen Display**  
  Generally stable and responsive.

- **Online Remote Control**  
  Nearly fully integrated and works consistently during playback.

---

## ⚠️ Known issues

- **Renderer settings not yet integrated**  
  Player‑side configuration panels are incomplete; users must adjust settings directly inside each renderer (madVR / EVR).

- **HUD visual glitches**  
  Occasional minor graphical artifacts still under investigation.

- **Remote pairing persistence**  
  Device pairing is not saved reliably; the player may request re‑pairing on each startup.

- **Audio graphs in exclusive PCM mode**  
  In audio‑only playback, graphs work only in non‑exclusive PCM. Exclusive mode support is planned.

- **DLNA module**  
  Highly incomplete and prone to errors despite partial implementation.

---

## 🚧 Current limitations

- **3D‑to‑2D conversion**  
  Currently functional only with EVR; madVR support is planned.

- **Player settings coverage**  
  Several configuration sections are only partially implemented.

---

## 🛠️ In development

- **Favorites**
- **Playlists**
- **YouTube integration**
- **DLNA improvements**
- **Expanded renderer settings**
- **Additional HUD refinements**
- **General QoL enhancements across the UI**

Many other addition are being developed.
English localization is in development.
