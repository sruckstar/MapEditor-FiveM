# Map Editor for FiveM

The classic Map Editor, familiar to many GTA 5 single-player modders, now offers full support for FiveM. Use the free camera to create maps for your server right in the game.

## Server Installation

1. Copy the `resource/` folder to the server’s `resources/` directory under the name `mapeditor`.
2. Add `ensure mapeditor` to `server.cfg`.
3. **Start the server with OneSync**—the editor won’t work without it; see below.

The key to open the editor is configured by the player in the GTA control settings (FiveM section):
the script registers the command via `RegisterKeyMapping`; the default is `F7`.