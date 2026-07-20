# LyricScroll (MB_LyricScroll)

MusicBee panel plugin that shows plain lyrics and scrolls them gently with playback. Not karaoke — no line highlighting.

**Features**
- Dockable **LyricScroll** panel
- Scroll follows the player position (seek / pause stay in sync)
- Optional **start delay** (seconds) before scrolling begins
- Lyrics from MusicBee first, then [LRCLIB](https://lrclib.net/)
- Instrumental / OST tracks show **Instrumental** instead of wrong scraped text
- Artist names like `Bob Dylan (Rare)` are cleaned for online lookup

---

## Requirements

- [MusicBee](https://getmusicbee.com/) for Windows (x86 plugin)
- [.NET SDK](https://dotnet.microsoft.com/download) (to build)
- .NET Framework 4.8 (usually already installed on Windows)

---

## Build

```powershell
cd MB_LyricScroll
dotnet build -c Release
```

Output:

```text
bin\Release\net48\MB_LyricScroll.dll
bin\Release\net48\Newtonsoft.Json.dll
```

Copy **both** DLLs into MusicBee’s `Plugins` folder (typically next to `MusicBee.exe`).

Close MusicBee before overwriting DLLs.

---

## Install / first-time setup

1. Copy the two DLLs into `Plugins`.
2. Start MusicBee → **Edit → Preferences → Plugins**.
3. Enable **LyricScroll**.
4. Open **Arrange panels** (layout editor).
5. Add the **LyricScroll** panel (bottom or side works well).
6. Resize it with the dock splitter, then **save the layout**.

After updating from an older build that used a fixed panel height, **remove** LyricScroll from the layout and **add it again** so MusicBee picks up resizable height.

---

## Settings

**Preferences → Plugins → LyricScroll → Configure**

| Setting | Meaning |
|--------|---------|
| Start delay (seconds) | Hold lyrics at the top for N seconds, then scroll at the normal rate (delay is not subtracted from song duration) |

Settings are stored under MusicBee’s persistent storage path as `LyricScroll_startDelayMs.txt`.

---

## How lyrics are chosen

1. MusicBee now-playing lyrics / downloaded lyrics / Lyrics tag  
2. LRCLIB (`instrumental` wins over bad local text on OST tracks)  
3. Otherwise: `No lyrics found.` or `Instrumental`

---

## Development notes

- Target: `net48`, `x86` (matches classic MusicBee).
- Debug builds run small `Debug.Assert` checks in `ScrollMath` and `LyricsService` (search cleanup, instrumental JSON, scroll math).
- Do not commit with a `Co-authored-by: Cursor` trailer if you want a single GitHub contributor.

---

## License / credit

Plugin author: JulioJulioso. Lyrics lookups may use LRCLIB; respect their terms and User-Agent policy.
