# Character assets

The selectable player characters ship as glTF binaries (`.glb`) in this folder. They are installed
next to the plugin in `BepInEx\plugins\GMSCoop_avatars\`. Each file is a pruned copy of the original:
only the animations the mod uses are kept, renamed to logical names (idle, walk, run, crouch, drive,
use, pickup, jump and, for the mannequin, crouchidle and carrywalk). The weapons held in the hand slots
were removed. The scripts in `tools\` rebuild them (`prune_glb.py`) and render the launcher thumbnails
(`render_glb.py`).

All assets are CC0 (public domain). Credit is not required, but we give it anyway.

| File | Source | Author | License |
|---|---|---|---|
| `kaykit_barbarian.glb`, `kaykit_knight.glb`, `kaykit_mage.glb`, `kaykit_rogue.glb`, `kaykit_rogue_hooded.glb` | [KayKit Character Pack: Adventurers](https://github.com/KayKit-Game-Assets/KayKit-Character-Pack-Adventures-1.0) | Kay Lousberg ([kaylousberg.com](https://kaylousberg.com)) | [CC0 1.0](https://creativecommons.org/publicdomain/zero/1.0/) |
| `quaternius_mannequin.glb` | [Universal Animation Library (Standard)](https://opengameart.org/content/universal-animation-library) | Quaternius ([quaternius.com](https://quaternius.com)) | [CC0 1.0](https://creativecommons.org/publicdomain/zero/1.0/) |

The launcher thumbnails (`Installer\thumbs\*.png`) are renders of these files, made by `render_glb.py`.
They are derived from the CC0 assets above.

The default character ("Trabajador del juego") is not shipped: it is the game's own worker model,
copied at runtime from the player's installed game.
