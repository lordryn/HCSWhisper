# HCSWhisper

**GPU-accelerated OpenAI Whisper speech engine purpose-built for [VoiceAttack 2](https://voiceattack.com/) and [HCS Voice Packs](https://www.hcsvoicepacks.com/)** (*Star Citizen — Event Horizon*, *Elite Dangerous — Singularity*, *Ad-Astra*, *No Man's Sky*, *X4*, and more).

Windows Speech Recognition (SAPI) has been the weakest link in VoiceAttack for over a decade—requiring tedious voice training, choking on background noise, and forcing HCS Voice Packs to spell sci-fi acronyms phonetically (`"em eff dee"`, `"E S P"`). **HCSWhisper** completely replaces Windows Speech with local GPU-accelerated Whisper running directly inside VoiceAttack 2.

---

## Quick FAQ ("Does it work, and is it slow?")

* **How fast is it?** **~60–95 ms (0.06–0.09s)** after you finish speaking on modern NVIDIA or AMD GPUs. Audio stays in RAM (no temp `.wav`/`.mp4` files, no audio playback hacks) and fires VoiceAttack's internal C# `Command.Execute()` API directly (`< 1 ms`).
* **Does it require Python, CUDA Toolkits, or a second server app?** **No.** It is a self-contained `.NET 8` VoiceAttack 2 plugin powered by **Vulkan GPU acceleration** (`Whisper.net` / `whisper.cpp`), so it works out of the box on **both NVIDIA GeForce and AMD Radeon GPUs** (with automatic CPU fallback).
* **Do I have to edit locked HCS Voice Pack profiles to bind Push-to-Talk?** **No.** HCS profiles (*Event Horizon*, *Singularity*, etc.) are read-only. HCSWhisper polls your chosen **HOTAS button, Mouse 4/5 button, or Keyboard key** directly—or runs in **Open-Mic (Auto VAD)** mode—with zero VoiceAttack profile edits.
* **Does it understand Star Citizen / Elite Dangerous jargon and HCS trigger quirks?** **Yes.** At startup, HCSWhisper automatically indexes your installed HCS `.hcs` trigger files (plus any live HCS Customiser overrides) and translates natural speech into HCS's exact trigger strings in `< 1 ms`:
  * You say: `"MFD 1 to IFCS"` → Fires HCS: `"em eff dee 1 to I F C S"`
  * You say: `"activate turret ESP"` → Fires HCS: `"activate turret E S P"`
  * You say: `"open mobiGlas"` → Fires HCS: `"open mobi-glass"`
  * You say: `"activate mining slot 1"` → Fires HCS: `"Activate mining slot one"`
  * You say: `"Set a course for Arc-L5"` / `"ArcL5"` → Fires HCS: `"Plot a course to Yellow Core Station"`
  * You say: `"Plot a course to Babbage"` / `"New Beverage"` / `"NB"` → Fires HCS: `"Plot a course to New Babbage"`
  * You say: `"Plot a course to Charles Beach"` / `"Saccharin's Plot"` → Unlocks & plots all **675 routable Star Citizen locations** (including the 490+ Pyro/Stanton/Nyx settlements omitted from HCS's SAPI grammar) via Double-Metaphone homophone matching!
  * You say: `"Plot a course to Obituary"` / `"Horizon"` / `"Korea"` → Fires HCS: `Orbituary` / `Orison` / `Security Post Kareah`
  * You say: `"toggle VR mode"` / `"set tips to leg"` / `"enable vital mode"` → Fires HCS: `"toggle vee ar mode"` / `"set pips to lag"` / `"Enable vtol mode"`
  * You say: `"disembark the ship"` → Fires HCS: `"dismebark the ship"` *(even works around typos in upstream trigger files!)*
  * **Safety Gate:** Destructive commands (`eject`, `self destruct`, `abandon`, `jettison`) require a strict `95%+` match so stray chatter never ejects you into space.
* **Customizable Nicknames & Command Aliases:** Open the **Place Nicknames & Aliases** tab in the HCSWhisper tray window to view, search, add, edit, or import/export custom place nicknames (`"home base"` → `"Port Tressler"`) and full command aliases (`"punch it"` → `"engage quantum jump"`).
* **1-Click Issue & Homophone Reporter:** Click **Report Issue / Homophone** inside HCSWhisper to auto-capture the latest misheard Whisper transcript + sanitized diagnostics and open a **pre-filled GitHub Issue** (or copy a formatted block for Discord) with zero API keys or setup.
* **Built-In Auto-Update Prompt:** Checks GitHub Releases automatically on startup (configurable) and offers a 1-click **Update & Restart VoiceAttack** prompt whenever a new build ships.

---

## One-Click Install

### Requirements
* **Windows 10 / 11 (64-bit)**
* **VoiceAttack 2** (Steam or Standalone)
* Any Vulkan-capable **NVIDIA** or **AMD** GPU *(recommended; CPU fallback included)*

### Option 1 — Double-Click Installer (`.bat`)
1. Download **[`Install-HCSWhisper.bat`](https://github.com/lordryn/HCSWhisper/releases/latest/download/Install-HCSWhisper.bat)** from the [Releases page](https://github.com/lordryn/HCSWhisper/releases/latest).
2. Double-click it. It automatically locates VoiceAttack 2 across all Steam libraries, installs the plugin into `VoiceAttack 2\Apps\HCSWhisper`, and pre-caches the `base.en` Whisper AI model.

### Option 2 — PowerShell One-Liner
Open PowerShell and paste:
```powershell
irm https://raw.githubusercontent.com/lordryn/HCSWhisper/main/install.ps1 | iex
```

### Option 3 — Manual Zip Install
1. Download **`HCSWhisper-win-x64.zip`** from [Releases](https://github.com/lordryn/HCSWhisper/releases/latest).
2. Extract the `HCSWhisper` folder into your `VoiceAttack 2\Apps\` directory (so `VoiceAttack 2\Apps\HCSWhisper\HCSWhisper.dll` exists).

---

## First-Time Setup in VoiceAttack 2 (30 Seconds)

1. Launch **VoiceAttack 2**.
2. Open **Options (wrench icon) → General** and ensure **Enable Plugin Support** is checked. *(If you just turned this on for the first time, restart VoiceAttack 2 once.)*
3. Select your **HCS Voice Pack** profile (e.g., *HCS Event Horizon* or *HCS Singularity*).
   * HCSWhisper automatically mutes VoiceAttack's built-in Windows Speech listener on startup so Windows Speech never competes with it.
   * You'll see green confirmation in the VoiceAttack log:
     * `HCSWhisper indexed: Event Horizon + AdAstra (1,728 commands / 10,603 phrases)`
     * `HCSWhisper ready: model 'base.en' loaded on Vulkan`
4. **Talk!**
   * **Default Push-to-Talk:** Hold **Right Alt**, speak a command (`"spool the quantum drive"`, `"MFD 1 to shields"`, `"weapons free"`), and release.
   * **Configure HOTAS / Mouse / Open-Mic:** **Double-click the cyan HCSWhisper icon** in your Windows system tray:
     * Click **Bind PTT** and press **any button on your HOTAS stick/throttle** (supports up to 16 USB flight controllers, 32 buttons + POV Hat each), **Mouse 4/5**, or **Keyboard key**.
     * Or switch to **Open-Mic (Auto VAD)**—includes automatic **Crew Audio Ducking** (pauses listening while Astra/Vega is speaking over speakers) and an optional **Discord PTT Interlock** (mutes HCSWhisper while you hold your Discord talk button).

---

## Choosing a Whisper Model

Inside the HCSWhisper tray settings window, you can switch models on the fly:

| Model | Download Size | Typical GPU Latency | Best For |
| :--- | :---: | :---: | :--- |
| **`base.en`** *(Default)* | 141 MB | **~55–75 ms** | Best balance of near-instant response and high accuracy |
| **`small.en`** | 466 MB | **~90–120 ms** | Heavy accents, noisy rooms, or complex lore queries |
| **`tiny.en`** | 75 MB | **~35–45 ms** | Ultra-low VRAM / integrated GPUs |

Models are cached once in `%LOCALAPPDATA%\HCSWhisper\models` and config is saved in `%LOCALAPPDATA%\HCSWhisper\config.json`.

---

## Building from Source

```powershell
git clone https://github.com/lordryn/HCSWhisper.git
cd HCSWhisper
powershell -ExecutionPolicy Bypass -File .\scripts\Build-Release.ps1
```
Produces `dist\HCSWhisper\` and `dist\HCSWhisper-win-x64.zip`.

---

## Uninstall

Run in PowerShell:
```powershell
irm https://raw.githubusercontent.com/lordryn/HCSWhisper/main/uninstall.ps1 | iex
```
Or simply delete `VoiceAttack 2\Apps\HCSWhisper`.

---

## Credits & License

* Built on [Whisper.net](https://github.com/sandrohanea/whisper.net) / [whisper.cpp](https://github.com/ggml-org/whisper.cpp) and OpenAI Whisper.
* Inspired by [WhisperAttack](https://github.com/nikoelt/WhisperAttack).
* Licensed under the [MIT License](./LICENSE). Not affiliated with VoiceAttack.com or HCS VoicePacks Ltd.
