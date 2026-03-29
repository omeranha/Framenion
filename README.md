# Framenion

Framenion is a fast, lightweight Warframe companion desktop app built with Avalonia UI.

## Features

- Equipment browser with:
  - Category filters (Warframes, Primary, Secondary, Melee, Archwing categories, Companions, Vehicles, Amps)
  - Search by item name
  - Mastery status highlighting
  - Ingredient ownership checks
  - Craftability hints when ingredient requirements are met
- Void Fissure panel with:
  - Normal / Steel Path filtering
  - Tier, mission type, faction, location, and countdown
  - Auto-refresh and live timer updates
  - Desktop toast notifications for user selected opened fissures
- In-app inventory refresh flow using [warframe-api-helper](https://github.com/Sainan/warframe-api-helper) (download prompted on first use)
- Relic rewards overlay showing item details (ducat value and market price) when opening relics in-game.

## Installation

Download the latest release from the [Releases](https://github.com/omeranha/Framenion/releases), extract the contents and run the executable.

## To-do

- [ ] Warframe Market integration: Add live price data (buy/sell)

- [ ] Relic viewer: Add a dedicated relic browser with drop tables, rarity tiers, and quick search/filter by relic era and reward.

- [ ] Ensure cross-platform compatibility for Windows and Linux.

## Disclaimer

Use any third-party tooling at your own risk.
Framenion itself does not interact with the Warframe game process.

This project is not affiliated with Digital Extremes.
Warframe and all related trademarks are the property of their respective owners.

## Credits

- [browse.wf](https://browse.wf/) team for infrastructure and [export](https://github.com/calamity-inc/warframe-public-export-plus) data resources.
- [warframe-api-helper](https://github.com/Sainan/warframe-api-helper) for inventory export tooling.
