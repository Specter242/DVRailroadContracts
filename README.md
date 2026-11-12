# Railroad Contracts

A Derail Valley mod (Unity Mod Manager): run the stock map as a railroad. Take contracts with its
industries, a bridge between the two interchanges, and city passenger service, and move their
traffic with stock jobs. Cars come and go only through the interchange yards of
[Interchange Yards](https://github.com/Specter242/DVInterchangeYards).

- **Download and read about it** on Nexus Mods: https://www.nexusmods.com/derailvalley/mods/1816
- **Requires** Interchange Yards 0.1.7 or later and Persistent Jobs (continued); Passenger Jobs for passenger
  contracts.
- **Updates:** Unity Mod Manager reads `repository.json` here and offers each new version.

## Building

Needs the .NET SDK and Derail Valley installed (the build references the game's and Unity Mod
Manager's assemblies in place; set `-p:DvInstallDir=...` if the game lives elsewhere than
Steam's default folder on Linux).

    scripts/package.sh

builds `dist/Contracts-<version>.zip` for Unity Mod Manager.

- `src/Contracts.Core`: the engine-free contract model (netstandard2.0).
- `src/Contracts.Mod`: the mod (net48): the kiosk's Contracts menu and shops, the career rules,
  the interchange service, jobs, licenses and passenger service.

MIT licensed (`LICENSE`). The changelog is `CHANGELOG.md`.
