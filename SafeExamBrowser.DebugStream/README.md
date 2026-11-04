# SafeExamBrowser.DebugStream (debug mode)

Use the Firefox of your **normal desktop** - with your profile, your logins, your extensions - from within **any session**, even
if the session runs on its own isolated desktop (`KioskMode.CreateNewDesktop`).

A window of one desktop cannot be shown on another desktop, so the Firefox window is *streamed*: It keeps running on the original
desktop, a picture of it is sent into the session, and your mouse and keyboard input is sent back.

```
 original desktop                                    isolated desktop of the session
┌───────────────────────────┐   named pipe        ┌──────────────────────────────┐
│ Runtime  (StreamHost)     │ ◄─────────────────► │ Client  (StreamViewer window)│
│   └─ finds / opens Firefox│   frames  ───────►  │   shows the frames           │
│   └─ captures its window  │   ◄─────── input    │   forwards mouse + keyboard  │
│   └─ injects the input    │                     │                              │
└───────────────────────────┘                     └──────────────────────────────┘
```

## Enabling debug mode

This code is part of every build (Debug and Release), but it does nothing unless **debug mode** is turned on. This is deliberate -
see [Safety](#safety). It is on automatically in a Debug build (`dotnet build` / Visual Studio Debug configuration). In a Release
build - i.e. what an installed, internal-testing deployment of SEB normally runs - it has to be turned on explicitly, once, with
administrator rights, on the machine the installation will run on:

- **MSI**: `msiexec /i Setup.msi ENABLEDEBUGMODE=1`
- **Bundle**: `SetupBundle.exe ENABLEDEBUGMODE=1`

Either writes `HKEY_LOCAL_MACHINE\SOFTWARE\SafeExamBrowser\DebugMode` (DWORD) = `1`; `SafeExamBrowser.DebugStream.DebugModeConfiguration.IsEnabled`
is what every entry point of these tools checks before doing anything. **A plain install (no flag) leaves this at 0, and
re-running the installer without the flag turns it back off** - the flag is not sticky across an upgrade, it has to be passed
every time an installer that should have debug mode on is produced or run.

To turn it off again on a machine without reinstalling: delete that registry value, or set it to `0`.

## Usage

**Nothing happens automatically when a session starts** - not the viewer, not the clipboard, not AutoHotkey. The only thing active
from the start is the key combination itself, listening in the background. The first time it is pressed during a session, *all* of
the below activates at once, for the rest of that session; every press after that just hides / shows the viewer again.

1. Install (or build and start) a build with debug mode enabled, see above.
2. Press **`Ctrl + Shift + Alt + F12`** - the first time this is pressed during a session, it activates debug mode (see below) and
   shows the viewer; every press after that just hides / shows the viewer again.
3. Pick the Firefox window to use in the drop down list, or click *New window*. If no Firefox window exists at all, one is opened
   automatically (in your running Firefox if there is one, i.e. with your profile).

Everything is logged by the runtime (`Debug stream host ...`) and the client (`Connected to the debug stream host ...`).

## What the first press activates

All of this happens together, once, the first time `Ctrl+Shift+Alt+F12` is pressed during a session - never before, and never
again for the rest of that same session:

- **The viewer is shown.**
- **Copy & paste starts working** between Firefox and the session. The clipboard is shared by the whole window station, so it only
  needs to be left alone: debug mode sets the clipboard policy to *Allow* and no longer blocks Ctrl+C / Ctrl+V / Ctrl+X, whatever the
  loaded configuration says.
- **Your AutoHotkey scripts start running inside the session.** A script started on the original desktop cannot see or control the
  session's desktop, so the client starts the same scripts a second time on the session's desktop (only if the session uses its own
  desktop). That is why they can read the current selection (`Send ^c` + `A_Clipboard`) and change the clipboard there. Debug mode
  also allows *injected* keys (what `Send` produces) and whitelists the AutoHotkey interpreter so tool tips / message boxes are not
  closed by SEB.
  - Which scripts: all AutoHotkey interpreters running *at the moment of that first press* (not at session start), with the same
    command line. To choose explicitly (or to use compiled scripts), set the environment variable `SEB_DEBUG_AHK` before starting
    SEB, e.g. `"C:\Program Files\AutoHotkey\v2\AutoHotkey64.exe" "C:\debug\selection.ahk"` (several separated by `|`).
  - The copies are terminated when the session ends. Your original scripts are not touched.
  - Mouse restrictions of the configuration (e.g. blocked right button) still apply to the session.
- The runtime log records exactly when this happened: a `DEBUG MODE` warning, logged at that moment, not at session start.

## Safety

This lets code in a session reach a regular, unrestricted application, read and write the clipboard across desktops, and run
arbitrary AutoHotkey scripts inside a session - that must never be available in an exam a real candidate takes. It is guarded on
several levels:

- Every entry point (`StreamHost.Start`, `DebugModeOperation.Perform`) checks `DebugModeConfiguration.IsEnabled` first and does
  nothing at all if it is `false` - no pipe is opened, no setting is touched, no script is started, nothing is logged. Even if it
  is `true`, `DebugModeOperation` itself does nothing beyond listening for the hotkey until that hotkey is actually pressed once.
- `IsEnabled` is `true` unconditionally in a Debug build. In every other build it is **only** `true` if an administrator wrote the
  registry flag above - normally via the installer's `ENABLEDEBUGMODE=1` property (see `Setup\Components\DebugMode.wxs`), which
  requires local administrator rights and is never set by a plain install.
- The registry key is machine-wide (`HKEY_LOCAL_MACHINE`), so turning it on is an explicit, auditable, administrator-only action on
  that specific machine - it cannot be done by whoever is sitting at the exam.
- The runtime logs a clearly marked warning as soon as it starts (debug mode being enabled is enough for that, independent of any
  session), and the client logs its own the moment a session's first press actually activates the rest of it - so both "debug mode
  is enabled on this machine" and "debug mode was actually used in this session" are always visible in the log, Debug build or not.
- The pipe used to stream Firefox is only accessible by the user running the runtime, and never via the network.

**Never pass `ENABLEDEBUGMODE=1` to an installer used for anything but internal testing.** When in doubt, check the registry value
on the target machine, or just look for the `DEBUG MODE` warning in the runtime log of a session run on it.

## How it works

| Concern | Solution |
| --- | --- |
| Where does the host run? | In the runtime, which always lives on the original desktop and exists independent of sessions. |
| Crossing the desktop boundary | Named pipe (`SafeExamBrowser_DebugStream_<runtime id>`). Kernel objects are not bound to a desktop. |
| Capturing | `PrintWindow(PW_RENDERFULLCONTENT)`, which returns the live content of a window even if covered or on a desktop which does not receive input. A screen capture would only yield black. The invisible resize border is cropped (`DWMWA_EXTENDED_FRAME_BOUNDS`). |
| Bandwidth | Only the changed rectangle of each frame is sent (`FrameDiffer`). Unchanged frames are not sent at all. |
| Mouse | `SendInput` goes to the *input* desktop, i.e. the isolated one. Instead, window messages are posted to the top-level window (`WM_MOUSEMOVE`, `WM_LBUTTONDOWN`, `WM_MOUSEWHEEL`, ...). |
| Keyboard | Same, with `WM_KEYDOWN` / `WM_KEYUP`. Firefox derives the character from the key message itself and handles an additional `WM_CHAR` as a second input, so characters are only posted if the layout of Firefox would not produce the same character. |
| Modifier keys | `GetKeyState` reflects real input, of which a window on an inactive desktop gets none. The key state table of the Firefox thread is set by attaching to its input queue (`AttachThreadInput` + `SetKeyboardState`). The state is reset by Windows upon detaching, so the thread stays attached as long as a modifier is held. |
| Focus | The window is told that it is active (`WM_ACTIVATE`, `WM_SETFOCUS`, ...) while the viewer has the focus. |
| Clipboard across desktops | Not solved by this project at all - the Windows clipboard is already shared by every desktop of the same window station, SEB itself just clears and blocks it. Debug mode only stops SEB from doing that (see above). |

## Known limitations

- **Popup content is not shown.** Menus, context menus and drop downs work (e.g. open a drop down and use *Down* / *Enter*), but
  their content is composited by the window manager and cannot be captured. They appear as translucent rectangles.
- The streamed Firefox window is put on top of the other windows of the original desktop and restored if minimized, so that it is
  not considered hidden (Firefox stops rendering hidden windows). This remains after the session.
- **Input blocked by the session is blocked for Firefox, too.** The viewer only receives what the keyboard / mouse interceptors of
  the session let through, e.g. if the configuration disables F12 or Alt+Tab, these cannot be used in Firefox either. (The hotkey of
  the viewer itself is detected before the interceptors and thus always works.)
- No drag and drop, file dialogs or IME input across the stream.
- Windows larger than 8192 pixels in either dimension are not streamed.
- Only windows of `firefox.exe` are supported.
