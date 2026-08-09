# Map Editor for FiveM

The classic Map Editor, familiar to many GTA 5 single-player modders, now offers full support for FiveM. Use the free camera to create maps for your server right in the game.

## Server Installation

1. Copy the `resource/` folder to the server’s `resources/` directory under the name `mapeditor`.
2. Add `ensure mapeditor` to `server.cfg`.
3. **Start the server with OneSync**—the editor won’t work without it; see below.

The key to open the editor is configured by the player in the GTA control settings (FiveM section):
the script registers the command via `RegisterKeyMapping`; the default is `F7`.

## Building a map together

Several players can build one map at once: main menu → **Build With Others**, then either open a
session on the map you have loaded or join somebody else's. Everyone sees each other's edits as they
happen, sees a name where each of the others is standing, and cannot move an object somebody else has
picked up.

A session is a draft in the editors of the people in it — nothing is written to disk and nobody
outside it sees any of it. Any participant can save their own copy; publishing belongs to the
session's host and ends the session, because a published map already stands in everyone's world.

Sessions are open to every player by default. To restrict them:

```
set mapeditor_restrict_collab true
add_ace group.admin mapeditor.collab allow
```