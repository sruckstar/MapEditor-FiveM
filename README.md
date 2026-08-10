<img width="1536" height="1024" alt="map-fivem" src="https://github.com/user-attachments/assets/14b52cc8-0596-4506-956d-431f6849d392" />

The classic Map Editor, familiar to many GTA 5 single-player modders, now offers full support for FiveM. Use the free camera to create maps for your server right in the game.


**Original Author:** Guad
**New features, bug fixes, FiveM version:** andre500

_This mod uses the LemonUI code by Hannele “justalemon” Ruiz, licensed under the MIT License. The LemonUI plugin does not work with the Enhanced version of FiveM, so it has been partially adapted for this mod. No dependency on LemonUI is required._


## Features

- Full collection of objects, vehicles, and peds up to DLC 1.73
- Sorting by categories
- Setting up a filter for objects by DLC
- Ability to add objects to favorites
- Selecting multiple objects at once and dragging a selected group
- Tool for quick docking of similar objects and filling an area
- Tool for creating cyclical structures of similar objects
- Viewing the names of game props, quickly copying and adding to favorites
- A smart streamer that lets you create large-scale maps without worrying about hitting limits
- Instantly add and remove maps without having to restart server resources
- Fine-tune permissions: You decide who can open the menu, save maps, and upload them to the server
- OneSync Support for peds, vehicles, and dynamic objects
- **NEW:** The ability to create lasers from loot found at the Kortz Center. Customize the size, color, pattern type, damage, and many other settings. The laser's movement is synchronized using server time


## Build with others

Collaborate with other players to create maps in the new Map Editor mode. Start a private session right within your server, invite other players, and begin creating together. Every player in the session can see what others are building—players outside the session can’t see anything.

OneSync is not used in collaborative mode: all synchronization is handled on the client side, so there are no limits on the number of players or objects.


## Free. Open source

Map Editor is distributed for free, and the project's source code is available on GitHub. The Guadmaz project has been given a new lease on life not only in SinglePlayer but also in FiveM.


## Server Installation

1. Copy the `resource/` folder to the server’s `resources/` directory under the name `mapeditor`.
2. Add `ensure mapeditor` to `server.cfg`.
3. **Start the server with OneSync** — the editor won’t work without it.

The key to open the editor is configured by the player in the GTA control settings (FiveM section):
the script registers the command via `RegisterKeyMapping`; the default is `F7`.
