# Chrome RAM Reducer

[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![C#](https://img.shields.io/badge/C%23-14.0-239120?logo=csharp&logoColor=white)](https://learn.microsoft.com/dotnet/csharp/)
[![Platform](https://img.shields.io/badge/platform-Windows-0078D6?logo=windows&logoColor=white)](#requirements)
[![License](https://img.shields.io/badge/license-MIT-black)](LICENSE)
[![Release](https://img.shields.io/github/v/release/muhammetozeski/ChromeRamReducer?display_name=tag)](../../releases/latest)

A Windows tray utility that makes Chrome hand memory back to the operating system by driving
**V8's garbage collector** through the Chrome DevTools Protocol.

It reports two numbers side by side — committed bytes and working set — because only one of them
means memory was actually released.

---

## Why this exists

Every widely used "browser memory optimiser" on Windows does the same single thing: it calls
`EmptyWorkingSet` (or `SetProcessWorkingSetSize(-1, -1)`) on the browser's processes. That call does
not free memory. It evicts pages from the process working set; the pages move to the standby list and
stay in physical RAM until Windows decides to repurpose them. Task Manager's default *Memory* column
shows the working set, so the number collapses and nothing has been released.

Measured on Chrome 151, 400 MB of live JavaScript data held by a page:

| Step | Chrome working set | Chrome committed | System standby |
|------|-------------------:|-----------------:|---------------:|
| 400 MB allocated and still referenced | 1367 MB | 1097 MB | 3839 MB |
| After `EmptyWorkingSet` on every Chrome process | **36 MB** | **1097 MB** | **4846 MB** |

The working set fell by 1331 MB, committed memory did not move by a single byte, and the standby list
grew by almost exactly the amount that left the working set. The pages never left RAM.

The same page, with the data dropped and V8 asked to collect:

| Step | Chrome working set | Chrome committed |
|------|-------------------:|-----------------:|
| 400 MB of uncollected garbage | 3112 MB | 1895 MB |
| After `HeapProfiler.collectGarbage` on every target | 2718 MB | **1490 MB** |

406 MB genuinely returned to Windows. That is what this tool automates.

---

## What it does

1. Finds the DevTools endpoint of the running Chrome — first from `User Data\DevToolsActivePort`,
   then by probing the configured port — and verifies it answers before using it.
2. Opens **one** browser-level WebSocket and attaches a flattened session to every target: pages,
   iframes, web workers, shared workers, service workers and extension background pages. Extensions
   are frequently the largest holders and most tools never touch them.
3. Runs `HeapProfiler.collectGarbage` in each session, optionally followed by
   `Memory.forciblyPurgeJavaScriptMemory` — the same purge Chrome performs on itself during an
   out-of-memory intervention, so caches are rebuilt on demand and page state survives.
4. Samples committed bytes and working set before and after, and reports both.
5. Offers `EmptyWorkingSet` as an explicitly labelled cosmetic option, **off by default**.

Nothing is discarded, suspended or reloaded. Tabs keep their state, forms keep their contents and
scripts keep running.

---

## Features

- One-click trim, plus an optional timer from 1 to 720 minutes
- Tray icon with a *Trim now* entry; closing the window hides it instead of exiting
- Committed vs. working set shown as two separate figures, with the difference explained in the UI
- Automatic DevTools port discovery, with a manual port override
- Per-target log line for every session that was collected or skipped
- Settings persisted to `%APPDATA%\ChromeRamReducer\settings.json`
- Single-instance guard; no installer, no service, no background driver

---

## Requirements

- Windows 10 or 11 (x64)
- Google Chrome started with `--remote-debugging-port`
- .NET 10 desktop runtime for the framework-dependent build; the portable build needs nothing

---

## Enabling the DevTools endpoint

V8's garbage collector cannot be reached from outside the browser process by any other means, so
Chrome has to expose its debugging endpoint:

```text
chrome.exe --remote-debugging-port=9222
```

Chrome ignores the flag while another instance already owns the profile, so every Chrome window has
to be closed first. The application's **Start Chrome with debugging** button does this for you and
adds `--restore-last-session`.

To make it permanent, append the flag to the target of your Chrome shortcut.

### Security architecture

- **The debugging port is the entire attack surface.** While it is open, any process running as your
  user can connect to `127.0.0.1:9222` and drive the browser: read page contents, cookies of open
  sites, and navigate tabs. Chrome binds the endpoint to loopback only, and since Chrome 111 it
  rejects WebSocket upgrades carrying a foreign `Origin` header, which stops a web page from
  reaching it. It does not stop other local programs. The confirmation dialog states this before
  Chrome is launched.
- **Turn it off when you do not need it.** Nothing here requires the port to stay open permanently;
  a normal Chrome start closes it again.
- **The application runs as the invoking user.** `asInvoker` in the manifest, no elevation prompt,
  no administrator rights requested or needed. Working-set trimming only touches processes the user
  already owns.
- **No network access beyond loopback.** The only HTTP request is `http://127.0.0.1:<port>/json/version`
  and the only socket is the DevTools WebSocket on the same host. There is no telemetry, no update
  check and no outbound connection of any kind.
- **No code injection.** Nothing is written into Chrome's address space, no DLL is loaded into it and
  no handle beyond `PROCESS_QUERY_INFORMATION` is opened. Every action is an ordinary DevTools
  Protocol command that Chrome's own DevTools window can issue.
- **Settings are plain JSON** under `%APPDATA%`, containing a port number and four booleans. No
  credentials are read, stored or transmitted.

---

## Installation

Download `ChromeRamReducer.exe` from the [latest release](../../releases/latest) and run it. It is a
single self-contained file; no installation step, and it can be deleted by deleting the file.

If you already have the .NET 10 desktop runtime, `ChromeRamReducer-FrameworkDependent-RequiresNET10.exe`
is a 1 MB alternative.

---

## Building from source

```bash
git clone https://github.com/muhammetozeski/ChromeRamReducer.git
cd ChromeRamReducer
dotnet build -c Release
```

Portable, self-contained single file:

```bash
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish/portable
```

Framework-dependent single file:

```bash
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish/framework-dependent
```

---

## Project layout

| Path | Purpose |
|------|---------|
| `Core/CdpConnection.cs` | DevTools Protocol client: one WebSocket, flattened sessions, id-matched replies |
| `Core/ChromeTrimmer.cs` | Enumerates targets, collects garbage, applies the optional working-set trim |
| `Core/ChromeLocator.cs` | Finds chrome.exe, discovers and verifies the DevTools port, launches Chrome |
| `Core/MemorySnapshot.cs` | Reads committed bytes and working set; wraps `EmptyWorkingSet` |
| `Core/AppSettings.cs` | JSON settings under `%APPDATA%\ChromeRamReducer` |
| `MainForm.cs` | Tray application and user interface |

---

## Diagnostics

Every step is written to a log file, so a run that does nothing can be explained instead of guessed at:

```text
%APPDATA%\ChromeRamReducer\Logs\Log <timestamp>.txt
```

The **Open logs** button in the window opens that folder. The last ten files are kept and older ones
are deleted on start. Each entry records the source file, member, line and thread it came from, and
the same stream is mirrored live into the window, colour-coded by severity. Port probing, every
DevTools request and reply, every attach that Chrome refuses, and both memory readings around a trim
are all included, along with any unhandled exception.

---

## Limitations

- Chrome must be started with the debugging flag. There is no way around this; V8 exposes no external
  garbage collection trigger.
- How much is released depends entirely on how much collectable garbage V8 is holding. A browser that
  was just started has nothing to give back.
- Memory held by live objects — an open document, a loaded video, a running application — is not
  garbage and will not be collected. That is correct behaviour, not a shortfall.
- Chromium-based browsers such as Edge, Brave, Opera and Vivaldi expose the same protocol and work
  when you point the port setting at them, but only Chrome is tested.

---

## License

MIT. See [LICENSE](LICENSE).
