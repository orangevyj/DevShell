# DevShell

A shell replacement for Windows: a bar docked along the bottom of the screen with its own
desktop layer behind it — built as a WPF/.NET application.

```
┌──────────────────────────────────────────────────────────────────────────────────────────┐
│  ⊞   [ search everything…        ]   🗒  📁  🎬  🖥  …        14:32 24.09.2026  📶 Wi-Fi  🔊 ▁▃▅   ▲ 12 KB/s ▼ 48 KB/s   ENG │
└──────────────────────────────────────────────────────────────────────────────────────────┘
```

## Installing it

`dist\DevShell.Setup.exe` is the installer: an ordinary Inno Setup wizard in one file, with the
.NET runtime bundled, so it installs offline and asks for nothing else. It needs no administrator
rights — everything lands under your own profile — and it puts the program in
`%LOCALAPPDATA%\Programs\DevShell`. The wizard speaks English and Russian; past that it writes the
mode below, the shortcuts and the `Start DevShell` box on its last page.

Run it and choose one of two modes:

| Mode | What it does |
| --- | --- |
| **DevShell as the shell** | Writes `Shell` under `HKCU\…\CurrentVersion\Winlogon`, so the next sign-in starts DevShell instead of `explorer.exe` — the bar **and** its desktop layer. Explorer shortcuts still work; they open a folder window rather than a second desktop. |
| **Alongside Explorer** | Leaves the shell alone and puts DevShell in `HKCU\…\CurrentVersion\Run`, so it starts with Windows just above the taskbar. Nothing is replaced, so the desktop layer stays off. |

The same two options live in the bar's own settings flyout, under **Windows**, and the registry is
what settles it: whichever way it is set — installer or flyout — the `ShowDesktopLayer` key in
`config.json` is brought into step, and only the shell that is really starting draws a desktop.
The wizard keeps that key in step too: installing as the shell turns the desktop layer on,
installing beside Explorer turns it off.

Uninstalling (Settings → Apps → DevShell) puts Explorer back, removes the shortcuts and the
program files, and leaves `config.json` — and so every icon position — alone. The uninstaller is
Inno's own `unins000.exe` in the install folder, and it deletes the `Shell` and `Run` values only
while they still point at DevShell.

Everything can be driven unattended, too:

```
DevShell.Setup.exe /VERYSILENT                                  # install, riding along with Explorer
DevShell.Setup.exe /VERYSILENT /TASKS="!alongside,shellemode"   # install and become the shell
"%LOCALAPPDATA%\Programs\DevShell\unins000.exe" /VERYSILENT     # uninstall
```

The two modes are one radio group, and a silent run keeps whatever the wizard would show checked
unless it is told otherwise, so shell mode has to ask for `shellemode` *and* turn `alongside` off in
the same switch. Other tasks are listed the same way: `/TASKS="alongside,!desktopicon"` is the
install with no desktop shortcut.

Those are Inno Setup's own switches, so `/SILENT`, `/DIR=`, `/LOG` and the rest work as well, and
the setup log lands in `%TEMP%` beside the other Inno logs. `Installer\DevShell.iss` adds the mode
it settled on to that log.

### Building it

The wizard wraps a self-contained publish of the app, so both come out of one script:

```bash
dotnet build -c Release                       # → bin\Release\net10.0-windows\DevShell.exe
powershell -ExecutionPolicy Bypass -File Installer\build-setup.ps1
                                              # → dist\DevShell.Setup.exe
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist
                                              # the bare app, needs the .NET 10 Desktop Runtime
```

`build-setup.ps1` publishes DevShell for `win-x64` into a temporary folder and then hands that
folder to Inno Setup's `ISCC.exe`, which compresses it into `dist\DevShell.Setup.exe` following
`Installer\DevShell.iss`. Inno Setup 6 is the only extra build requirement; the script looks in
the usual places for it, or takes `-InnoSetup <path to ISCC.exe>`.

Only one instance can run at a time; a second launch exits immediately.

## What is on the bar (left to right)

| Element | Behaviour |
| --- | --- |
| **Start button** | The Start menu: a rail down the left (the account, *Settings*, and the power menu under it) and, beside it, the programs Windows itself lists, read from the two `Start Menu\Programs` folders and grouped under the folder each shortcut lives in — `Accessories`, `Administrative Tools`, `Accessories › System Tools` — with programs that sit at the root first under *Other apps*, so the list is the same shape Explorer's all-apps view has instead of one flat run. Beside the rail the pane has two tabs: **All apps** for that list, and **DevShell** (the embedded icon), which shows the banner the build carries, the version, and a technical block — runtime, Windows build, architecture, shell mode, bar size and display scale, start time and data folder — with *Copy details* for pasting into a bug report. **Right-click** it for the menu Windows puts there — Device Manager, Disk Management, Task Manager, *System*, *Apps and features* — built from the `WinX\GroupN` shortcut folders with the shell's own names and icons, in Windows' own order. The **Windows key** opens and closes this Start menu: while Explorer's taskbar is on screen the key is withheld from Windows — its bare tap opens DevShell's Start menu and injects nothing — and a combination such as Win+E is handed back to the system as one clean chord, modifiers and all. When DevShell is the only shell there is no Explorer taskbar to answer a chord, so the bar is offered every bare Win+key and answers the ones it knows (the row below) — the rest, and every chord with Shift, Ctrl or Alt on it, is left to Windows exactly as it was. |
| **Windows-key combinations** | While DevShell is the shell: **Win+R** Windows' own Run dialog (`shell32`'s `RunFileDlg`, the dialog Explorer shows), **Win+E** a file window on *This PC*, **Win+D** the desktop peek (every application's windows fold away, and the same key brings those same ones back), **Win+I** *Settings*, **Win+S** the search box, **Win+X** the Start button's menu above the button. Win+L, the snapping combinations, Win+Ctrl+arrow and everything else stay with Windows. |
| **Search** | Type to search every indexed file on every connected drive. |
| **Pinned apps** | Read from `%AppData%\Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar`, with the real shortcut icons. Left-click launches, right-click offers *Open / Open as administrator / Open file location* and *Unpin from taskbar*. DevShell writes pins itself as well: drop an `.exe`, `.lnk` or document anywhere on the bar, or right-click a Start menu entry — or a desktop icon — for *Pin to taskbar*. A pinned button can be dragged along the bar to a new slot, and the order is remembered in the config (`PinnedOrder`), because Explorer keeps its own order in the Taskband blob. A Start menu shortcut is copied as it stands, so its arguments, icon and working directory survive; anything else gets a shortcut written through the shell's `IShellLinkW`, numbered `(2)` when the name is taken. Unpin only ever deletes a file inside the pin folder. DevShell reloads the bar at once; Explorer picks the change up when it rebuilds its own. |
| **Running apps** | Every app that currently has a real window, as one button per app (the Windows 11 grouping): a browser with five windows gets one button, and an app that has only pinned-shortcut presence gets none. A grouped button stacks the app's own icon behind the first one as a mark that it stands for several windows, and **resting the pointer** on it opens a preview: one row per window with a still picture and its title, one click to raise it, a cross to close just that window. Clicking the button itself opens the window last worked in, and steps to the next window when the app is already in front. |
| **Clock** | Time and date; click for a month calendar. |
| **Network** | Status and connection type — Wi-Fi, Ethernet, or *Network* when Windows reports nothing more specific. Click for the adapters Windows lists, and, on a machine that has one, the Wi-Fi list: scan, pick a network, type the password and join. |
| **Volume** | Speaker glyph with a level bar underneath showing the current volume; click for a slider and mute. |
| **Activity** | Live up/down network throughput with the amount currently transferring. Right-click the monitor for **Rate units** — *Bytes* (KB/s, MB/s) or *Bits* (Kbit/s, Mbit/s) — which is remembered in the config and applies to the widget and the flyout at once. The link speed in the network flyout stays in bits on purpose: it is a property of the link, not of the traffic. Each link is counted once: adapters are picked by the default route, so the pseudo-interfaces that only mirror a link's counters — the WFP layers and the QoS packet scheduler, which report the very same bytes — carry no route and are skipped. Without that the rate was multiplied by the filter count (four times on the machine this was fixed on). |
| **Notification area** | The chevron beside the clock, and the flyout of background applications behind it. DevShell *is* the notification area: it opens a window of class `Shell_TrayWnd` and answers `Shell_NotifyIcon` itself (`NIM_ADD`/`NIM_MODIFY`/`NIM_DELETE`/`NIM_SETVERSION`), so an application that registers a tray icon while DevShell is the shell gets a row with its own icon and its registered tooltip. It reads both packet shapes Windows sends — the Windows 11 wrapper (`magic`, command, `NOTIFYICONDATAW`) and the bare 32-bit struct — and forwards clicks back the way the notification protocol expects: a left click is `NIN_SELECT` (or `WM_LBUTTONUP` for a version-0 icon), a right click `WM_CONTEXTMENU` (or `WM_RBUTTONUP`). The chevron is hidden while nothing is registered, and the previous icon picks up again as soon as one is. |
| **Keyboard language** | Current input language; click to cycle layouts. |

### Search

* The file index is built in the background at startup (`IndexOnStartup`) and cached in
  `%LOCALAPPDATA%\DevShell\fileindex.bin`, so later launches sail past it (the cache is
  rebuilt after 24 h or on demand via the bar's right-click menu → *Rebuild file index*).
* The name and the folder are searched with a two-pass ranked match, so `notepad` ranks
  `notepad.exe` above `C:\…\notepad-notes.txt`.
* Each result row offers **Open**, **Open file location** and **Open as administrator** as
  inline chips, `Enter` / `Ctrl+Enter`, or a right-click menu that also has
  **Copy full path**.

### Wi-Fi

* It appears only where Windows reports a wireless interface, so a machine with a wired NIC alone
  gets the adapter list and nothing else.
* The list is what `WlanGetAvailableNetworkList` returns — the network you are on first — as one
  row per network: a signal bar filled to the reported quality, the name, a padlock on anything
  secured, and *Connected*, *Saved*, *Secured*, *Open* or *Out of range* beside the signal.
* **Scan** asks the radio for a fresh sweep (`WlanScan`) and reloads the list a few seconds later,
  which is how long the new results take to come back.
* Picking an open network joins it straight away; picking a secured one opens a password box with
  **Connect** and **Cancel**, and `Enter` joins as well. A network that already has a profile of
  its own is joined with the key Windows is holding, so nothing is asked for.
* Joining writes a profile for that SSID — `WPA2PSK`/AES, `WPAPSK`/TKIP, `WPA3SAE` and *OWE* told
  apart by the network's own authentication algorithm — and then calls `WlanConnect`. Whatever the
  radio answers is shown in the panel, and a join that has not settled yet is re-checked for a few
  seconds before the list is reloaded.
* The layouts of the two lists `wlanapi.dll` hands back are read by offset, so they are checked
  against the marshaler's own struct sizes before a byte of them is used (`Wlan.LayoutChecked`).

## The desktop layer

Behind the bar DevShell draws the desktop itself — wallpaper and icons — on a window pinned
just above the system's `Progman` window, which keeps it *below* ordinary windows, where a
desktop belongs.

**Wallpaper.** It shows what Windows is showing. The image path comes from
`HKCU\Control Panel\Desktop`; when that file no longer exists (which is what happens after a
theme change) it falls back to the transcoded copy in
`%APPDATA%\Microsoft\Windows\Themes\TranscodedWallpaper`. The `WallpaperStyle` value is mapped
to the same fit Windows uses, and `10` (Fill) is honoured by **cropping** the picture, never by
squashing it. *Change wallpaper…* points the desktop at any file (recorded as `WallpaperPath`);
*Use the Windows wallpaper* clears that override again.

**Icons.** Enumerated from your Desktop and — unless you turn it off — `C:\Users\Public\Desktop`,
laid out one per cell on an Explorer-style grid (`DesktopIconSize`, 48 DIP by default). Each
cell is a labelled chip carrying the item's real icon: `desktop.ini` is honoured the way
Explorer honours it (per-file icon and label overrides, `[LocalizedFileNames]`, the folder's own
`LocalizedResourceName`, and resource references such as
`@%SystemRoot%\system32\imageres.dll,-112` resolved through `SHLoadIndirectString`). The
Recycle Bin is a virtual item whose display name comes from the shell itself, so a Russian
system shows *Корзина*. Namespace items are asked for their icon through their `PIDL`
(`SHGetFileInfo` cannot resolve a `::{CLSID}` string on its own), which is also what yields the
shell's separate *empty* and *full* pictures.

**Tiles are exclusive: one icon per cell, always.** Dropping an icon on a cell that is already
taken slides it to the nearest free one — straight down its own column first, since a column is
what this desktop fills — so two icons can never end up stacked on one tile. It is enforced on
both sides: while an icon is dropped, and again when a remembered layout is laid out, which also
quietly repairs a `config.json` written before the rule existed (the icon that was there first
keeps the tile, the other moves next door).

**Dragging.** An icon can be dragged wherever you want it, and the spot is written to
`config.json` (`DesktopPositions`, keyed by the item's path — or its `::{CLSID}` for a virtual
item) the moment you let go of it, so the layout survives a restart. Icons you never touched
stay on the grid, and *Arrange icons* in the desktop's own menu forgets every dragged position
and returns everything to it. The press is noticed but the mouse is only captured once the
pointer has actually moved, so a plain click stays a click and a double-click still launches.

**Selection.** A press on the wallpaper starts a selection box: drag it and a translucent blue
rectangle grows from the press point, and every icon whose cell it touches when the button comes
up is selected. **Ctrl** held when the box starts adds to what was already selected instead of
replacing it, exactly as Explorer does. A selected icon is a **Chip**: click it normally to make
it the only one, **Ctrl+click** to add it to or remove it from the selection, **Ctrl+A** for all of
them and **Escape** to clear. With more than one selected, a press on any of them drags the whole
group by one shared offset, stopped where the first icon would leave the surface; each icon still
lands on its own grid cell, the pressed one first, and every position is written once per drop.
Ctrl+C copies every selected file as a file drop.

**Interaction.**

| Gesture | Result |
| --- | --- |
| Left-click an icon | Selects it — the cell tints with `AccentSoft` and a light border. |
| Drag an icon | Moves it, clamped to the surface, and remembers where it was dropped — which is a tile of its own, never one that is taken. |
| Double-click an icon | Launches it (or opens the virtual item). |
| Left-click empty space | Clears the selection. |
| Right-click empty space | **Explorer's own desktop menu** — *New ▸*, *Display settings*, *Personalize* and whatever shell extensions are installed — with DevShell's own items appended: *Refresh desktop icons*, *Change wallpaper…* and *Use the Windows wallpaper* on their own, then a **DevShell ▸** submenu (*Arrange icons*, *Show desktop icons*, *Include public desktop items*, *Show Recycle Bin*, *Show hidden items*, *Open personal folder*, *Open public folder*). The toggles are checkable and are written to `config.json` at once. |
| Right-click an icon | **Explorer's own item menu** — *Open*, *Open with*, *Send to ▸*, *Cut*, *Copy*, *Create shortcut*, *Rename*, *Delete*, *Properties*, plus installed shell extensions — with *Open as administrator* and *Copy full path* appended. Those last two need a real filesystem path, so they are absent for virtual items such as the Recycle Bin. |

**Context menus.** They are the real shell menus rather than a re-implementation, so
third-party verbs (7-Zip, Git, the antivirus) come along by themselves. The shell never hands
an item its own menu handler, though — the handler belongs to the folder that *owns* the item —
so DevShell binds against the desktop folder (`SHGetDesktopFolder`) and asks *that* for the
child's `IContextMenu` (`GetUIObjectOf` for an item, `CreateViewObject` for the background).
Two traps are worth remembering: `IContextMenu` is `000214e4-…`, **not** `000214e6-…` (that is
`IShellFolder`, and the wrong GUID makes every `QueryContextMenu` fail with `E_INVALIDARG`),
and `QueryContextMenu` reports success with a *positive* HRESULT whose low word is the highest
command id it used — testing `!= 0` throws the whole menu away. Because these menus are built
lazily, DevShell installs a `WH_CALLWNDPROC` hook for the duration of `TrackPopupMenuEx` and
forwards `WM_INITMENUPOPUP` / `WM_DRAWITEM` / `WM_MEASUREITEM` / `WM_MENUCHAR` to
`IContextMenu2`/`3`; without that, **New** and **Send to** open empty. *View*, *Sort by* and
*Refresh* belong to Explorer's view rather than to the folder, and the *Refresh* that does sit
in the folder's menu repaints Explorer's own view — the one that is not on screen — so DevShell
puts its own *Refresh desktop icons* at the top level next to it, and *Arrange icons* in its
submenu. Whatever the shell would hide stays hidden, which is why *Paste* only appears once the
clipboard holds files.

The bar and the desktop window share one `Config` instance, so a change made in either menu is
immediately visible to the other and neither can save a stale copy over it.

## Files it writes

Everything lives in `%LOCALAPPDATA%\DevShell`:

| File | Purpose |
| --- | --- |
| `config.json` | Settings (created with defaults on first run). |
| `log.txt` | Startup, indexing and error log. |
| `fileindex.bin` | Cached file index. |

### config.json

| Key | Default | Meaning |
| --- | --- | --- |
| `BarHeight` | `46` | Bar height in DIPs. |
| `Acrylic` | `true` | Ask DWM for an acrylic backdrop, fall back to a solid colour. |
| `BarOpacity` | `1.0` | How solid the bar is: `1.0` paints exactly the bar as designed, and lower values make it see-through (`0.15` is the floor). The settings flyout shows the complement of this as a *Transparency* slider, 0-85%. A see-through bar still needs a backdrop from DWM - acrylic, or a translucent gradient when acrylic is off - so a machine that supplies neither leaves the bar solid and says so in the log. |
| `DockMode` | `"Overlay"` | `Overlay` sits on top of the system taskbar; `AppBar` reserves screen space (real taskbar replaced). |
| `ShowSecondsInClock` | `false` | Show seconds in the clock. |
| `IndexOnStartup` | `true` | Walk all drives at startup. |
| `UseIndexCache` | `true` | Reuse the cached index instead of re-walking. |
| `MaxIndexEntries` | `1500000` | Memory guard for the index. |
| `SkippedFolderNames` / `SkippedFileNames` | see file | Paths that are never indexed. |
| `ShowDesktopLayer` | `true` | Draw the desktop surface behind the bar. The installer and the settings flyout keep it in step with whether DevShell is the shell that is starting. |
| `DesktopShowIcons` | `true` | Draw the desktop icons; off leaves just the wallpaper. |
| `DesktopIncludePublic` | `true` | Also show the shared `C:\Users\Public\Desktop`. |
| `DesktopShowRecycleBin` | `true` | Include the Recycle Bin. |
| `DesktopShowHiddenItems` | `false` | Include hidden desktop items. |
| `DesktopShowFileExtensions` | Windows' own setting | Label `report.txt` rather than `report`. A new config starts out following Explorer's *hide extensions for known file types*, so an existing install keeps the look it had; the settings flyout then owns it. |
| `DesktopIconSize` | `48` | Desktop icon edge in DIPs. |
| `DesktopPositions` | `{}` | Where each icon was dragged to, keyed by path (or `::{CLSID}`); written on every drop. |
| `WallpaperPath` | `null` | Explicit wallpaper file; `null` follows Windows. |

The bar's right-click menu switches the dock mode, rebuilds the file index, opens Task Manager
and reveals the settings folder without touching the file; the desktop's right-click menu edits
the desktop keys above.

## Code map

| Path | Contents |
| --- | --- |
| `App.xaml(.cs)` | Startup, single-instance mutex, logging, data directory. |
| `MainWindow.xaml(.cs)` | The bar: layout, flyout popups, topmost handling, standing down while a fullscreen app is in front, context menus. |
| `MainWindow.Search.cs` | Index/search wiring, result rows, open/location/admin/copy actions. |
| `MainWindow.Status.cs` | Clock, network, volume, activity and language widgets, and the activity monitor's bytes/bits rate units. |
| `MainWindow.Wifi.cs` | The Wi-Fi half of the network flyout: the network list, scanning, the password box and joining. |
| `MainWindow.Running.cs` | The running-app buttons: which windows qualify, the activate/minimize/close actions, and the hover preview of a grouped app's windows. |
| `MainWindow.Start.cs` | The Start menu: the rail (account, Settings, power), the *All apps* / *DevShell* tabs with the info pane and its embedded banner, the programs list, the Windows key that opens it, the Start button's own right-click menu, and keeping a menu above the flyout it was opened from. |
| `MainWindow.Shortcuts.cs` | The Windows-key combinations the bar answers itself where Explorer is not the shell — Win+R, Win+E, Win+D, Win+I, Win+S, Win+X — and bringing the bar to the front before a key-driven flyout goes up. |
| `MainWindow.Settings.cs` | The settings flyout: wallpaper and desktop toggles, the Windows mode switch, and the icon the build carries. |
| `DesktopWindow.xaml(.cs)` | The desktop layer: wallpaper, icon grid, selection, its two context menus. |
| `DesktopWindow.Drag.cs` | Dragging icons, clamping them to the surface, sliding a colliding drop to a free tile and remembering where they land. |
| `Services/DesktopItems.cs` | Enumerating desktop items, their `desktop.ini` metadata and icons. |
| `Services/DesktopLayout.cs` | Explorer-style grid arrangement of the icon cells, and which free cell a taken one sends an icon to. |
| `Services/Wallpaper.cs` | Finding the wallpaper Windows uses, and how it fits. |
| `Services/RunningApps.cs` | Which top-level windows count as a running app, grouped per process, and which window of a group the user last worked in. |
| `Services/StartMenuApps.cs` | Reading the two `Start Menu\Programs` trees into one alphabetical list. |
| `Services/StartButtonMenu.cs` | The Start button's right-click menu: reading the `WinX\GroupN` shortcut folders, their names and icons, and launching them. |
| `Services/WindowsIntegration.cs` | The `Shell` and `Run` values that decide whether DevShell or Explorer starts at sign-in. |
| `Services/IniFile.cs` | Minimal INI reader/writer behind the `desktop.ini` handling. |
| `Services/` | `Config` (shared singleton), `FileIndexer`, `FileSearch`, `PinnedApps`, `NetworkMonitor`. |
| `Interop/ShellContextMenu.cs` | Hosting the native shell menus: binding a folder's `IContextMenu`, the `WH_CALLWNDPROC` message forwarding, DevShell's appended items. |
| `Interop/PowerActions.cs` | Sleep / restart / shut down / sign out, through `shutdown.exe` and `powrprof`. |
| `Interop/UserAccount.cs` | `GetUserNameEx` for the account name the Start menu shows. |
| `Interop/Wlan.cs` | The Wireless LAN API: radios, the networks a card can see, scanning, and the profile that joining one writes. |
| `Interop/` | P/Invoke layers: `WindowEffects` (acrylic/topmost), `AppBarDock`, `DesktopPlacement`, `FullscreenWindow` (which window counts as fullscreen), `ShellActions`, `ShellIcons`, `ShellStrings`, `ShortcutFile`, `KeyboardHook` (the Windows key), `WindowActivation`, `WindowPreview` (the still pictures in the running-app preview), `VolumeService`, `NetworkProbe`, `KeyboardLayouts`. |
| `Installer/DevShell.iss` | The Inno Setup wizard: the files, the shortcuts, the two ways of starting, the uninstaller — built by `build-setup.ps1` beside it, which produces `dist\DevShell.Setup.exe`. |
| `UI/` | `Theme.xaml` (brushes, styles, templates), `FileIconConverter`, `DpiChange`. |

## Known limitations

* The **Start menu** lists the programs Windows lists, alphabetically, but has no *All apps* /
  pinned split, no recent documents and no search box; its account button opens the Windows
  account page rather than offering a user switch.
* **Power actions** act at once, as Windows' own power menu does — there is no confirmation
  before *Sign out*, *Restart* or *Shut down*.
* Icons are **not re-flowed** when the item they sat between disappears, so a grid can end up
  with gaps. Dropping is snapped to a tile of its own, so nothing is ever stacked.
* The desktop's menus are the **shell's**, so *View* and *Sort by* are missing (they are
  Explorer's view commands, not the folder's) and the menu text is in whatever language the
  shell is in, independent of any DevShell setting.
* The **running-app strip** lists windows, not processes: an app that is open with no window of
  its own (a background updater, a service) does not appear.
* The desktop layer sits **above `Progman` but below ordinary windows** on purpose, so whatever
  you open covers it — exactly as the real desktop behaves.
* The bar and the desktop layer cover the **primary display**; additional monitors are not
  handled yet.
* The **keyboard language** widget reports and cycles the *foreground* window's layout, so it
  shows `ENG` when the bar itself is focused rather than the app you were typing in.
* **Open as administrator** cannot elevate further when the bar already runs elevated (as it
  does here); it then simply opens the file.
* The bar is **topmost**: another application's open menu can still cover a flyout, since
  menus are topmost too.
* **Wi-Fi** needs a real radio: the section is hidden altogether on a machine that has none, and
  the list is only as fresh as the last scan.
* The **Start menu** lists shortcuts, so **packaged (Store/UWP) apps are not in it at all**: Windows
  keeps those in its app folder and lists them from there, not from a Start-menu shortcut.
* **Packaged apps** are activated by a host that Explorer normally provides. When DevShell is the
  shell it starts `ShellAppRuntime.exe` (`Interop/ShellAppRuntime.cs`) in Explorer's place, which is
  what makes *Settings*, *Calculator* and the rest of the Store apps open. On a Windows whose
  Explorer keeps that host to itself — Windows 10, and Windows 11 before build 25252 — there is
  nothing to start and those apps still want Explorer running.
* While DevShell is the shell it keeps the **bottom strip of the screen out of the work area**
  (`Interop/WorkArea.cs`), the strip the real taskbar keeps for itself: that is what makes a window
  the user *maximizes* stop at the bar's top edge instead of sliding underneath it. Beside Explorer
  the taskbar takes that strip back instead, so the bar takes nothing there.
* Search indexes file *names* and folders, not file contents.
* The app and its setup are **unsigned**, so Windows shows the usual unknown-publisher prompt
  when either is run, and installing as the shell means the next sign-in starts DevShell — the
  way back is the uninstaller, or deleting the `Shell` value under `HKCU\…\Winlogon`.

## Next steps

* Recent documents and a search box in the Start menu.
* Multi-monitor support for both the bar and the desktop layer.
* Code signing for the app and the setup.
* Per-process audio mixer for the volume flyout.
* Content indexing and a fuzzy match for the search box.
