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
- Void Fissure panel with:
  - Normal / Steel Path filtering
  - Tier, mission type, faction, location, and countdown
  - Auto-refresh and live timer updates
  - Desktop toast notifications for user selected opened fissures
- On-screen scanning:
  - Relic reward overlay showing ducat value, market price and set items of the parent item when opening relics in-game
  - Riven overlay grading the card on the Kuva reroll screen, on each roll, and from a chat link
  - A single configurable hotkey (Print Screen by default) scans whichever of the two is on screen
  - Scan regions are measured automatically and can be adjusted per screen in Settings
- In-app inventory refresh flow using [warframe-api-helper](https://github.com/Sainan/warframe-api-helper) (download prompted on first use)

The close button hides the window to the tray; Exit in the tray menu quits.

## Installation

Download the latest release from the [Releases](https://github.com/omeranha/Framenion/releases), extract the contents and run the executable.

On Windows, the on-screen scanning uses the OCR engine built into Windows, which needs the
English OCR language feature. If scanning reports that OCR is unavailable, add it under
Settings > Time & language > Language & region > English > Language options > Optional features >
Optical character recognition. Everything except the scanning features works without it.

## To-do

- [ ] Ensure cross-platform compatibility for Windows and Linux.

## Disclaimer

Use any third-party tooling at your own risk.

Framenion does not read or modify the Warframe process memory and does not inject anything into
the game. To place its overlays and read the screen it does locate the game window, bring it to
the foreground, capture the screen, and follow the game's own `EE.log`. Reward and riven
scanning, and the log reader, can each be turned off in Settings.

This project is not affiliated with Digital Extremes.
Warframe and all related trademarks are the property of their respective owners.

## Credits

- Digital Extremes for the official [PublicExport](https://origin.warframe.com/PublicExport/index_en.txt.lzma) manifests, which are the app's source of item, relic, disposition and riven data.
- [browse.wf](https://browse.wf/) team for infrastructure and the [export](https://github.com/calamity-inc/warframe-public-export-plus) mirror, used for the mastery rank icon table.
- [Warframe Market](https://warframe.market/) and [warframestat.us](https://api.warframestat.us/) for pricing data.
- [warframe-api-helper](https://github.com/Sainan/warframe-api-helper) for inventory export tooling.
