# RSS Quick: what the first tour (October 2026) learned

App-specific notes for recording RSS Quick (TheIdeaPlace/rssquick), a WPF feed reader. The tour
was recorded with NVDA 2026.2, starting from the Microsoft Store. QuickMail's notes
(`quickmail.md`) are the model for this file.

## Demo data

- **There's no seeded data.** RSS Quick has no cache: it loads its shipped starter list
  (`RSS.opml`, about 190 real feeds in seven folders) and fetches live headlines. Record online,
  say so on air, and expect every headline-quoting host line to change on a re-record.
- **Reset before every take.** Close RSS Quick, then delete `%APPDATA%\RSSQuick` (the saved
  default list). For the Store copy, also delete
  `%LOCALAPPDATA%\Packages\*RSSQuick*\LocalCache\Roaming\RSSQuick`: Windows redirects a packaged
  app's writes under `%APPDATA%` there. Subscribing in a demo saves a default list, which changes
  every later take until it's removed.
- **Choose feeds that render well in Edge.** Bloomberg's pages came up blank. The Idea Place
  (theideaplace.net, a WordPress blog) works for subscribe-by-website and open-in-browser.

## The Microsoft Store as a demo

- **Open the Store before the take.** SendKeys can't press the Windows key, and launching the
  Store from the recorder (`-Launch explorer.exe ms-windows-store://home`) took about 90 seconds
  every time, so typed keys went nowhere. Instead, run `start ms-windows-store://home` with vmtest,
  wait about 15 seconds (warm it once first), and start the clip at Ctrl+E. Focus is then in the
  search box.
- **Search, then open the product page:** Ctrl+E, type the name, Enter, Tab twice to the first
  result card, Enter. Shift+Tab from the search box wraps to the *last* result. Once it installed
  the wrong app (FeedFlow).
- **The install needs no Microsoft account** for a free app. NVDA reads "Getting ready",
  "Preparing to download" and "Installing (n%)".
- **Where focus lands after an install varies.** Usually it's the Open button, once it was the
  age-rating link, and Enter there opens the ESRB page in Edge. So pressing Open is its own take,
  with `vmtest focus id:OpenInstalledProduct -Window "Microsoft Store"` before it.
- **Launch the Store copy** for app demos with
  `start "" explorer.exe shell:AppsFolder\4055KellysCorner.RSSQuick_a6vqfc7q28mmm!RSSQuick`, then
  wait for the window (the process is `RSSQuick`).
- **Uninstall** with `Get-AppxPackage *RSSQuick* | Remove-AppxPackage`. To check what the Store is
  giving out, install it and read `Get-AppxPackage *RSSQuick*`. The public listing API reports the
  version as "Unknown".

## Edge in the VM

A fresh VM opens Edge's first-run screen, which takes over the take. Set these policies under
`HKLM:\SOFTWARE\Policies\Microsoft\Edge` (DWORD): `HideFirstRunExperience=1`,
`RestoreOnStartup=5`, `HideRestoreDialogEnabled=1`, `BackgroundModeEnabled=0`,
`StartupBoostEnabled=0`. Warm Edge once before the open-in-browser demo; its first cold start
outlasted a 15-second wait.

## Behaviour found while recording (1.4.0 and 1.4.1)

- **Fixed in 1.4.1:** the feed tree read every item as "RSSReaderWPF.FeedItem" (no
  `AutomationProperties.Name` on the TreeViewItem style). vmtest's focus report showed the same
  name, so `vmtest focused` catches this class of bug without a screen reader.
- **Not fixed:**
  - The status bar is never spoken, because nothing raises `LiveRegionChanged`. So there's no
    "Loading…", no load summary, no search count and no "Subscribed to…".
  - NVDA+End says "No status line found".
  - HTML entities in headlines are read out raw: The Verge's "iFixit&#8217;s".
  - Script around all three, and say them on air.

## Keys that worked well on air

Arrowing the tree (names, collapsed or expanded, "3 of 7", "level 1"). Right and Left, including
Left from a child back to its folder. Type-ahead with "a", "a" (Arts, then Accessibility); "t"
from Technology is silent, because there's only one T. Enter on a feed, then Down. F6 there and
back, which returns to the same headline. Enter on a folder (a merged list of about 200). / then a
word, then Enter (searches every feed in about 10 seconds). Ctrl+N with a website address. Enter on
a headline, then Alt+F4 back. F1 (one long utterance; Escape after about 16 seconds). Alt, Left to
Help, Down three times to About; the Store copy says "Installed from the Microsoft Store".

## Tool problems hit

- `speechlog.py` crashes printing a zero-width space (Edge page titles) on a cp1252 console. The
  speech file is written first, so read it directly.
- A stopped host-side recording keeps running in the VM and collides with the next take ("file in
  use", "ffmpeg stopped partway"). Kill `demo.ps1` and ffmpeg in the VM before retrying.
- A failed take leaves the previous clip in place. Check the clip's timestamp, or "recorded
  <name>" in clip.ps1's output, before reading its speech.
