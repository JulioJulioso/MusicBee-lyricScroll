# LyricScroll (MB_LyricScroll)

MusicBee panel plugin that shows lyrics and scrolls with playback. When synced LRC is available (usually from LRCLIB), the **active line is highlighted** and kept near the center. Otherwise it falls back to gentle plain-text autoscroll.

**Features**
- Dockable **LyricScroll** panel
- **Synced mode**: line highlight from LRC timestamps (LRCLIB `syncedLyrics` preferred)
- **Plain mode**: scroll follows player position (seek / pause stay in sync)
- **Right-click menu**: switch source (MusicBee / LRCLIB), reload, copy, open LRCLIB, appearance
- Optional **start delay** for plain mode only (synced timestamps are absolute)
- Custom **colors**, **font**, and **padding**
- Lyrics waterfall: MusicBee → LRCLIB (instrumental / synced / plain)
- Instrumental / OST tracks show **Instrumental** instead of wrong scraped text
- Artist names like `Bob Dylan (Rare)` are cleaned for online lookup
- Titles like `02. Song [Producido por X]` drop track numbers and bracket suffixes for LRCLIB

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
| Start delay (seconds) | Plain mode only: hold lyrics at the top for N seconds, then scroll (delay is not subtracted from song duration) |
| Prefer synced lines | Prefer LRCLIB (or local) LRC with timestamps over plain MusicBee text |
| Padding | Space around the lyrics text |
| Background / Text | Panel colors |
| Font | Typeface, size, and bold |
| Reset look | Restores default colors/font/padding (keeps start delay and synced preference) |

**Right-click the lyrics panel** for the same appearance dialog, plus source overrides when the wrong song’s lyrics appear.

Settings are stored under MusicBee’s persistent storage path as `LyricScroll.settings.json` (older installs may still have `LyricScroll_startDelayMs.txt`, which is migrated automatically).

---

## How lyrics are chosen

With **Prefer synced lines** on (default) and fetch mode **Auto**:

1. LRCLIB `instrumental` wins over bad local tags (common on OST/score tracks)
2. LRCLIB **synced** LRC (search requires artist + title match; duration is soft — vinyl rips often differ from ±2s `/api/get`)
3. Local LRC (MusicBee / tag) if it parses
4. Local plain lyrics
5. LRCLIB plain lyrics
6. Trimmed Letras.com scrape only as last resort (full page dumps are rejected)
7. Otherwise: `No lyrics found.` or `Instrumental` (OST heuristic)

If lyrics are wrong: right-click → **Use MusicBee lyrics** or **Use LRCLIB**, or **Open LRCLIB in browser**. The override lasts until the next track (then Auto again).

Musixmatch is not used (commercial / licensing). Spotify and Tidal public APIs do not expose usable synced lyrics to third parties.

---

## Development notes

- Target: `net48`, `x86` (matches classic MusicBee).
- Debug builds run small `Debug.Assert` checks in `ScrollMath`, `LrcParser`, and `LyricsService`.
- Do not commit with a `Co-authored-by: Cursor` trailer if you want a single GitHub contributor.

---

## License / credit

Plugin author: JulioJulioso. Lyrics lookups may use LRCLIB; respect their terms and User-Agent policy.
