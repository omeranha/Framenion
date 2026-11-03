# Framenion

Framenion is a fast, lightweight Warframe companion desktop app built with Avalonia UI.

## Features

- Equipment browser with:
  - Category filters (Warframes, Primary, Secondary, Melee, Archwing categories, Companions, Vehicles, Amps)
  - Search by item name
  - Mastery status highlighting
  - Ingredient ownership checks
  - Craftability hints when ingredient requirements are met
  - Item ducat and platinum values from live Warframe Market data
- Inventory browser with:
  - Owned parts, blueprints, relics, mods and arcanes
  - Search, category and mastery filters, and sorting by market value, quantity or name
  - Per-item and total stack platinum values
- Relic browser with:
  - Era filters (Lith, Meso, Neo, Axi, Requiem)
  - Owned counts, refinement tier and the rewards each relic drops
  - A toggle to hide relics you do not own, and sorting by era, name, owned count or refinement
  - Search by relic name or by the reward it contains
- Riven browser with:
  - Every riven you own, graded per stat and overall
  - The riven's generated name, disposition, mastery requirement, reroll count and polarity
  - Class filters (Rifle, Shotgun, Pistol, Melee, Archgun, Modular) and sorting by grade, weapon, disposition, rolls or mastery rank
  - Search by weapon, riven name or stat
- Riven overlay also shows weapon-specific best-roll suggestions when available
- Void Fissure panel with:
  - Normal / Steel Path filtering
  - Tier, mission type, faction, location, and countdown
  - Auto-refresh and live timer updates
  - Desktop toast notifications for user selected opened fissures
- On-screen scanning (Windows):
  - Relic reward overlay showing ducat value, market price and set items of the parent item when opening relics in-game
  - Riven overlay grading the card on the Kuva reroll screen, on each roll, and from a chat link
  - A single configurable hotkey (Print Screen by default) scans whichever of the two is on screen
  - Scan regions are measured automatically and can be adjusted per screen in Settings
- In-app inventory refresh, reading the running client's API session token once per game session after asking for confirmation
- Windows and Linux x64 build targets; see platform limitations below

The close button hides the window to the tray; Exit in the tray menu quits.

## Installation

Download the appropriate build from the [Releases](https://github.com/omeranha/Framenion/releases), extract it and run the executable.

On Windows, on-screen scanning uses the built-in Windows OCR engine, which requires the English
OCR language feature. If OCR is unavailable, add it under Settings > Time & language > Language &
region > English > Language options > Optional features > Optical character recognition.

Linux builds use PaddleOCR and download its English model when the app starts. Linux support is
currently partial: the app and data browsers run, and inventory refresh can read the game process
when Linux permits it, but game-log monitoring, the global scan hotkey and screen capture are
Windows-only. On Linux, process-memory access may require ptrace permission (for example, depending
on `/proc/sys/kernel/yama/ptrace_scope`).

## To-do

- [ ] Complete Linux game-integration support, including log monitoring, global hotkey and screen capture.

## Disclaimer

Use any third-party tooling at your own risk.

Framenion does not write to or modify the Warframe process memory and does not inject anything into
the game. Inventory refresh asks for confirmation before starting a temporary local worker that
reads the running game's memory, read-only, to recover the API session token needed to request
inventory from Warframe's own API. The token stays in memory only until the game exits and is never
written to disk or logs. On Windows, Framenion can also locate the game window, capture the screen
for overlays, and follow the game's own `EE.log`. Reward and riven scanning, and the log reader,
can each be turned off in Settings.

This project is not affiliated with Digital Extremes.
Warframe and all related trademarks are the property of their respective owners.

## Credits

- Digital Extremes for the official [PublicExport](https://origin.warframe.com/PublicExport/index_en.txt.lzma) manifests, which are the app's source of item, relic, disposition and riven data.
- [browse.wf](https://browse.wf/) team for infrastructure and the [export](https://github.com/calamity-inc/warframe-public-export-plus) mirror, used for the mastery rank icon table.
- [Warframe Market](https://warframe.market/) and [warframestat.us](https://api.warframestat.us/) for pricing data.
- [Megrim, Valkyrial and Lia](https://docs.google.com/spreadsheets/d/1OQGKpWXeoPaN0Cy7mTvVZMRcwvZXgIC3EO1AIRAkwDg) for the community-maintained riven roll suggestions.
- [warframe-api-helper](https://github.com/Sainan/warframe-api-helper), whose approach to recovering the client's API session token the inventory refresh is based on.
