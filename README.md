# CS2 Soundboard

A Windows desktop soundboard for Counter-Strike 2. It captures your physical microphone continuously, mixes sound effects into it in real time, and sends the combined stream to a virtual audio cable (VB-CABLE). CS2 uses the cable's recording side as its microphone, so teammates hear your normal voice at all times, plus any sound effects you trigger with global hotkeys.

```
Physical mic ──► CS2 Soundboard mixer ──► CABLE Input ──► (VB-CABLE driver) ──► CABLE Output ──► CS2 microphone
                       ▲
          sound effects (global hotkeys)
```

All audio processing is local. Nothing is sent to any network service.

---

## Contents

1. [Requirements](#requirements)
2. [Install VB-CABLE](#1-install-vb-cable-one-time)
3. [Build and run](#2-build-and-run)
4. [Configure the soundboard](#3-configure-the-soundboard)
5. [Configure CS2](#4-configure-cs2)
6. [Verify mic passthrough and sound effects](#5-verify-mic-passthrough-and-sound-effects)
7. [Using the app](#using-the-app)
8. [Publishing a standalone exe](#publishing-a-standalone-exe)
9. [Supported formats and dependencies](#supported-formats-and-dependencies)
10. [Architecture](#architecture)
11. [Tests](#tests)
12. [Known limitations](#known-limitations)
13. [Troubleshooting](#troubleshooting)

---

## Requirements

- Windows 10 or 11 (x64).
- [VB-CABLE](https://vb-audio.com/Cable/) virtual audio driver (free). Any virtual cable with a playback side and a recording side works.
- To build: .NET SDK 10 or later. The published exe needs no .NET installation.

## 1. Install VB-CABLE (one time)

1. Download the VB-CABLE driver pack from <https://vb-audio.com/Cable/>.
2. Extract the ZIP **completely**. Running the installer from inside the ZIP fails.
3. Right-click `VBCABLE_Setup_x64.exe`, choose **Run as administrator**, then click **Install Driver**.
4. Restart Windows.
5. Open Windows Sound settings (`Win+R` → `mmsys.cpl`). You should see:
   - **Playback** tab: `CABLE Input (VB-Audio Virtual Cable)`. The soundboard writes the mix here.
   - **Recording** tab: `CABLE Output (VB-Audio Virtual Cable)`. CS2 reads its microphone from here.
6. Keep your **real headphones/speakers as the Windows default playback device**. If `CABLE Input` is the default playback device, game and system sounds are sent to your teammates. The app warns you about this.

If the app does not detect VB-CABLE, the Audio Mixer tab shows a red panel with these steps and a **Refresh devices** button.

## 2. Build and run

```powershell
# from the repository root
dotnet build CS2Soundboard.sln -c Release
dotnet run --project src/CS2Soundboard -c Release
```

Command-line option: `--minimized` starts hidden in the system tray. The Windows startup entry uses it.

## 3. Configure the soundboard

Open the **Audio Mixer** tab.

| Setting | Select |
|---|---|
| **Physical microphone (input)** | Your real microphone, e.g. a headset or USB mic. Never select `CABLE Output` here: the app refuses it as a feedback loop. |
| **Virtual microphone output** | `CABLE Input (VB-Audio Virtual Cable)`. It is preselected when VB-CABLE is installed. |
| **Listening device** | Your headphones or speakers. They are used only for previews and monitoring. |
| **Output buffer** | 30 ms default. Raise to 50–80 ms if you hear crackling. |

Selecting a microphone in the app does **not** inject audio into that microphone. The app *captures* it and forwards it to the virtual cable. CS2 must use the virtual cable's recording side.

On first launch the app preselects the Windows default recording device. If that device is virtual, it picks the first physical microphone instead. It also preselects `CABLE Input` when present. The banner at the top must be green: **"Live: microphone + soundboard → CABLE Input"**.

Then open the **Soundboard** tab, click **Choose folder…**, and pick the folder that contains your sounds. Every supported file in it, and optionally in its subfolders, is listed automatically.

## 4. Configure CS2

1. In CS2, open **Settings → Audio** and find the voice/microphone **input device** setting. Select **`CABLE Output (VB-Audio Virtual Cable)`**.
2. If your CS2 version has no input-device selector and uses the Windows default, open `mmsys.cpl` → **Recording**. Right-click **CABLE Output**, then choose **Set as Default Device** and **Set as Default Communication Device**. Your physical mic still works because the soundboard forwards it.
3. Your voice settings (push-to-talk or open mic) work as before. CS2 only transmits while voice is active, so **teammates hear sound effects only while CS2 is transmitting**. With push-to-talk, hold your voice key while triggering a sound.
4. You never need to switch microphones while playing. CS2 always uses CABLE Output, and the soundboard always feeds it while it runs.

## 5. Verify mic passthrough and sound effects

On the **Audio Mixer** tab:

1. **Run routing self-test** and speak continuously for about 4 seconds. The test captures `CABLE Output`, the device CS2 uses, while it:
   - plays a test tone through the soundboard path. Expect `PASS Soundboard audio reached "CABLE Output…"`.
   - measures your voice. Expect `PASS Your live microphone reached "CABLE Output…"`. If you were silent, it reports `INCONCLUSIVE` rather than claiming success.
2. Tick **Measure what CS2 actually receives**. The **Virtual recording (CS2 input)** meter must move when you talk, even with no sound playing.
3. Set **Listen on my headphones to** = **Virtual recording device**. You hear exactly what CS2 receives. Use headphones.
4. Press **Mute mic**. The virtual-recording meter drops while sounds still play. Press **Mute soundboard**. Your voice still shows and sounds are silent.
5. In CS2, use its microphone test, if available, and trigger a hotkey.

**Monitoring vs. verification:**
- *Physical microphone* monitoring only proves your mic works.
- *Mixed output* shows what the soundboard sends.
- *Virtual recording device* is the real end-to-end check: it is what CS2 hears.

Monitoring always plays on your listening device and never feeds back into the mix.

---

## Using the app

### Soundboard tab
- **▶ Play** sends the sound to CS2. It also plays on your listening device if *Also play triggered sounds on my listening device* is enabled. Click again to stop.
- **🎧 Preview** plays on your own headphones only. It is never sent to CS2.
- **Hotkey** button: click and press a combination such as `Ctrl+Alt+1`, `Ctrl+Shift+F5` or `Num 7`. **✕** clears it.
- **Status** shows `Ready`, `Playing`, `Error` (hover for the decoder message), or `Missing file`. A missing file is a hotkeyed sound whose file was removed. Restore the file or clear its hotkey.
- **Rescan** re-reads the folder. With *Rescan automatically on changes*, added, removed and changed files are picked up automatically.
- **Soundboard volume**, **Stop current** (the most recently started sound), and **Stop all**.

### Global hotkeys
- Hotkeys work while CS2 is in the foreground, including full screen and when CS2 runs as administrator. They also work while the soundboard is minimized or hidden in the tray.
- Holding a key triggers once. Keyboard auto-repeat is ignored.
- Ctrl, Alt and Shift can be combined with any key. A key without a modifier is allowed only for F1–F24, numpad keys, and Insert/Home/End/PageUp/PageDown/Pause/ScrollLock. This prevents blocking movement or chat keys.
- When you assign a combination already used by another sound or command, the app names the current owner and asks before moving it.
- A combination already registered by another application, or reserved by Windows, is rejected with an explanation. The previous hotkey is kept.
- **Settings → Global hotkeys** sets:
  - **Stop all sounds**, default `Ctrl+Alt+End`
  - **Toggle microphone mute**
  - **Toggle soundboard mute**

### Playback behavior (Settings tab)
- **Overlap** (default): sounds stack, including copies of the same sound.
- **Stop previous**: a new sound stops everything else.
- **Overlap, no duplicates**: different sounds overlap. Re-triggering a playing sound restarts it.
- **Toggle**: pressing a playing sound's hotkey stops it. This works with any mode.

### Automatic push-to-talk (Settings tab)
If you use push-to-talk in CS2, teammates hear sound effects only while your voice key is held. With **Automatic push-to-talk** enabled, triggering a sound does the following:

1. The app presses your CS2 push-to-talk key, for example `V`, or Mouse 4, Mouse 5 or the middle button.
2. It waits **Delay before the sound starts**, 100 ms by default, so CS2 opens the microphone.
3. It plays the sound. The key stays held while any soundboard sound plays, and further sounds extend the hold.
4. It releases the key **Release after the sound ends** later, 300 ms by default, so the end of the sound is transmitted.

How it behaves around your own key:
- You can talk with your own key at any time. If you let go of it while a sound is still playing, the app keeps the key down until the sound ends.
- If you are still holding your key when the sound ends, the app does not release it under you.
- To do this, a small observe-only input hook watches **only** the push-to-talk key. It runs on its own thread and never blocks any other input.

Safety:
- **Only press the key when CS2 is the active window** is on by default. Triggering a sound while you are in Discord or a chat box therefore never types the key there.
- No key is pressed for previews, when the soundboard is muted, when routing is off, or when a press only stops a playing sound in toggle mode.
- The key is always released on stop, on exit, when the key is changed, and if the app crashes.
- A soundboard hotkey that uses the push-to-talk key itself, such as `Ctrl+V` with push-to-talk on `V`, is refused. It would swallow the key before CS2 sees it.
- The key is sent as a hardware scan code, which is what games read.

If CS2 runs **as administrator**, run the soundboard as administrator too. Windows blocks simulated input from a normal program into an elevated one.

### System tray
- Minimize or close to the tray, depending on your settings. Mic passthrough and hotkeys keep working.
- Tray menu items:
  - Open
  - Stop all sounds
  - Mute microphone (checked when muted)
  - Mute soundboard (checked when muted)
  - Exit
- **Exit** stops all audio, releases every device and hotkey, and saves settings.
- Optional **Start with Windows**, per user via `HKCU\…\Run`, with **Start minimized**.
- Only one instance runs. Launching it again brings the existing window to the front.

### Settings persistence
Settings are saved automatically to:

```
%APPDATA%\CS2Soundboard\settings.json
```

They include:
- the sound folder
- hotkeys
- device selections, by ID with a name fallback
- mic gain, soundboard volume, master volume and listening volume
- mute states
- playback behavior
- tray and startup preferences

A corrupt file is backed up as `settings.corrupt-<date>.json` and defaults are loaded, with a warning. Logs are kept in `%APPDATA%\CS2Soundboard\logs\soundboard.log`. For testing, set the environment variable `CS2SB_SETTINGS_DIR` to use a different folder.

---

## Publishing a standalone exe

```powershell
powershell -ExecutionPolicy Bypass -File .\publish.ps1
```

This runs the unit tests, then produces a self-contained single file, `publish\CS2Soundboard.exe` (about 72 MB). It runs on any Windows 10/11 x64 PC without installing .NET. Copy the exe anywhere and run it.

Equivalent manual command:

```powershell
dotnet publish src/CS2Soundboard -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

## Supported formats and dependencies

| Format | Decoder |
|---|---|
| WAV (PCM/float; compressed WAV via Windows codecs) | NAudio |
| MP3 | NAudio (Windows ACM), with Media Foundation fallback |
| OGG Vorbis | NAudio.Vorbis / NVorbis |
| AIFF | NAudio |
| FLAC, M4A/AAC, WMA | Windows Media Foundation, if the codec is installed (standard on Windows 10/11) |

Undecodable or corrupt files are listed with status **Error** and a reason. They never crash playback.

Dependencies:
- **NAudio 2.2.1**: WASAPI capture/render, decoding, resampling.
- **NAudio.Vorbis 1.5.0**: OGG decoding.
- WPF and WinForms, which are part of .NET. WinForms is used only for the tray icon.

Tests use xUnit.

## Architecture

```
src/CS2Soundboard.Core      audio engine, devices, hotkey model, library scan, settings (testable, no UI)
  Audio/AudioService.cs       owns all device streams; start/stop/recover; watchdog; monitoring; self-test
  Audio/MixGraph.cs           the virtual-mic mix: mic + SoundBus → master → limiter → meter
  Audio/SoundBus.cs           lock-free (command queue) voice mixer for sound effects
  Audio/CaptureSink.cs        capture-thread conversion (any PCM/float format → mono float) + meter
  Audio/BufferedFloatProvider jitter/drift buffer between capture clock and output clock
  Audio/PeakLimiter.cs        clipping protection (-1 dBFS ceiling)
  Audio/ClipCache.cs          pre-decoded clips for instant hotkey playback; long files are streamed
  Audio/Devices.cs            endpoint catalog, VB-CABLE pairing, feedback-loop validation
src/CS2Soundboard           WPF app (MVVM): MainWindow, hotkey dialog, tray, RegisterHotKey manager
tests/CS2Soundboard.Tests   offline DSP/scenario tests + opt-in hardware tests
```

Key design decisions:

- **Continuous passthrough.** The output device's real-time thread pulls the mix graph, and the graph always renders the mic path, whether or not any sound is playing. Sound effects are *added* to the mic signal. They never replace it.
- **Separate clocks.** The physical mic and the virtual cable run on different hardware clocks. A jitter buffer bridges them, targeting 30 ms and capped at 150 ms. It fills underruns with silence, so the mixer never stalls, and skips ahead on drift. Differing sample rates are resampled. Any channel count and PCM/float format are converted.
- **Real-time safety.**
  - Audio callbacks take no locks, do no I/O, and do not allocate in steady state.
  - The UI and hotkeys talk to the audio thread through lock-free command queues and volatile parameters.
  - Gain changes are ramped per block to avoid clicks.
  - Stopped sounds fade out over 8 ms.
- **Device work** runs on a dedicated control thread, never the UI thread.
- **No silent re-routing.**
  - If the configured mic or output is missing, the app shows a red status and retries every 3 seconds, and on device-change notifications. It never substitutes the default device.
  - A watchdog detects streams that stop delivering audio and restarts them.
  - The banner always states whether your microphone is being forwarded.
- **Feedback protection.**
  - The recording side of the selected virtual output cannot be used as the microphone.
  - The listening device cannot be the virtual output.
  - Monitoring taps only feed the local listening device, never the mix.
- **Hotkeys** use Win32 `RegisterHotKey` with `MOD_NOREPEAT` on a message-only window. No keyboard hook is installed, so all other input is untouched.

Estimated added latency with the defaults is about 80 ms: capture block, jitter buffer and output buffer. VB-CABLE adds a little more on top of that.

## Tests

```powershell
dotnet test tests/CS2Soundboard.Tests --filter "Category!=Hardware"   # 54 offline tests
dotnet test tests/CS2Soundboard.Tests --filter "Category=Hardware"    # 5 tests against real devices (needs VB-CABLE + a mic)
```

The offline tests drive the real pipeline with a synthetic microphone. They push capture buffers into `CaptureSink` and pull the `MixGraph` block by block, as the WASAPI threads do. Tones at distinct frequencies tell voice and effects apart. They cover:
- mic passthrough while idle
- voice and effect together
- overlapping effects
- mic mute and soundboard mute
- limiting under extreme gain
- 44.1 kHz/16-bit and 24-bit/6-channel format conversion
- mic dropout and recovery
- the three playback modes
- monitor tap
- WAV and MP3 decoding, and corrupt/missing files
- streaming of long files
- clip cache invalidation
- folder scanning
- settings round-trip, corruption recovery and clamping
- hotkey parsing and validation
- feedback-loop detection
- the playback controller

The hardware tests use the real microphone, `CABLE Input` and `CABLE Output`. They play only quiet, short test tones into CABLE Input and nothing on speakers. Results on the development PC:

| Scenario | Measured on `CABLE Output` |
|---|---|
| Idle, no sound playing: mic reaches the virtual recording device | mic noise floor arrived at 1.10× the expected level |
| Sound effect playing | tone at -20.9 dBFS, expected -20.9 |
| Mic muted: effects continue | tone unchanged, -20.9 dBFS |
| Soundboard muted: voice continues | tone -118 dBFS (silent), voice level unchanged |
| Mic muted, nothing playing | -120 dBFS (silent) |
| `CABLE Output` chosen as the mic | refused as "Feedback loop", effects still work |
| Configured output missing | clear error, no fallback device opened |
| Start, stop, restart | devices released and re-opened cleanly |

Automatic push-to-talk was tested with real Windows input in an isolated app instance. F24 stood in for the voice key, and the system key state was sampled every 5 ms:

| Case | Observed |
|---|---|
| 1.0 s sound | key down 22 ms after the hotkey, sound about 100 ms later, key up at 1.5 s (sound + latency + 300 ms tail) |
| User taps the key mid-sound (300–500 ms) | the user's release was held back; key up at 1.46 s when the sound ended |
| User still holding when the sound ends, released at 2.0 s | the app did not release it; key up exactly when the user let go |

The press/hold/release logic is also unit-tested, covering stacked sounds, key changes while held, and the user already talking. Mouse-button push-to-talk is unit-tested but was not exercised with real input.

A GUI smoke test was also run with a sandboxed settings folder:
- The app started hidden in the tray.
- Global hotkeys played a sound twice while the app was hidden, reported a missing file, and stopped all sounds.
- A second launch brought the first instance forward.
- Closing the window released all devices.
- A restart restored the devices and hotkeys.

## Known limitations

- A virtual audio driver such as VB-CABLE is required. A desktop app cannot add audio to a physical microphone directly.
- Teammates hear sound effects only while CS2 transmits: push-to-talk held, or open mic.
- The microphone is mixed as mono. Multi-channel mics are averaged. If your interface puts the mic on one channel only, raise the mic gain.
- A registered hotkey is captured system-wide while the app runs. Other apps and CS2 do not receive that combination, so choose combinations CS2 does not use.
- Shared-mode WASAPI only. A device held in exclusive mode by another app cannot be opened, and the error says so.
- Files up to 90 s are pre-decoded into memory, with a 512 MB budget. Longer files are streamed from disk.
- FLAC, M4A/AAC and WMA depend on the Windows Media Foundation codecs installed.
- Automatic push-to-talk uses simulated input. It is the same mechanism other soundboards use for this feature, and it does not touch the game's memory. Whether any game or anti-cheat accepts simulated input is up to that game.
- If the user still holds the modifiers of a soundboard hotkey, such as `Ctrl` in `Ctrl+Alt+1`, when push-to-talk is pressed, the game also sees those keys. In CS2, `Ctrl` is crouch by default. Numpad or F13–F24 hotkeys avoid this.
- Sound files are not resampled to a different pitch or speed. Effects such as EQ or pitch shift are not provided.

## Troubleshooting

| Symptom | Fix |
|---|---|
| Red banner: "virtual output … not available" | VB-CABLE is not installed or is disabled. Install it, reboot, then click **Refresh devices**. In `mmsys.cpl`, make sure CABLE Input/Output are enabled. Right-click → Show Disabled Devices if needed. |
| "Your microphone is NOT being forwarded" | The mic is unplugged, disabled, or in exclusive use. The app reconnects automatically when it returns. Check the Audio Mixer status line. |
| Mic meter stays at "< -60 dB" or "digital silence" warning | Check the mic's hardware mute. In Windows Settings → Privacy & security → Microphone, allow **desktop apps**. Check the mic's level in `mmsys.cpl`. |
| "Feedback loop" error | `CABLE Output` was chosen as the physical mic. Choose your real microphone. |
| Teammates hear game sounds | CABLE Input is your Windows default playback device. Make your headphones the default. |
| Teammates hear nothing | CS2's input device is not `CABLE Output`. Or you are not transmitting: hold push-to-talk. Run the self-test. |
| Crackling or dropouts | Raise **Output buffer** to 50–80 ms. Disable "Allow applications to take exclusive control" on CABLE Input/Output and on the mic (device Properties → Advanced). Check the underrun counter under Technical details. |
| Echo while monitoring | Use headphones, or set monitoring to **Off**. |
| Hotkey shows a red ⚠ | Another application already owns that combination. Hover for details and choose another. |
| Automatic push-to-talk does nothing in CS2 | Check that the key matches your CS2 voice key. Make sure CS2 is the active window, or untick the CS2-only option. If CS2 runs as administrator, run the soundboard as administrator too. |
| Start or end of the sound is cut with push-to-talk | Raise *Delay before the sound starts* or *Release after the sound ends*. |
| A sound shows **Error** | Hover the status for the decoder message. Convert the file to WAV, MP3 or OGG. |
| `.exe` built with `dotnet build` says ".NET must be installed" | Your system-wide .NET is older than 10. Use the self-contained `publish.ps1` build, or install the .NET 10 Desktop Runtime. |
