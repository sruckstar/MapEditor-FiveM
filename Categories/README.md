# Object categories

The object picker is split into categories instead of one flat list. Each category is one plain-text
file, and **the files are the source of truth** — the game reads them at startup, so editing a file is
all it takes to change what shows up where.

## Installing

Copy the `Categories` folder next to the other Map Editor data files:

```
scripts\MapEditor\Categories\Props\*.txt      <- ships with the mod (from ObjectList.ini)
scripts\MapEditor\Categories\Vehicles\*.txt   <- ships with the mod (from VehicleList.ini)
scripts\MapEditor\Categories\Peds\*.txt       <- ships with the mod (from PedList.ini)
```

All three ship, and all three are read the same way. A folder that is missing is not fatal: vehicles
and peds are rebuilt from the built-in rules on the spot, and a missing `Props` falls back to a
single **All** category, i.e. the old flat list.

## File format

```
# Vegetation & Nature
# One model name per line. Lines starting with # are ignored.
prop_tree_birch_01
prop_bush_lrg_04b
```

The leading `NN_` in a filename only fixes the order in the menu; it is stripped from the name shown
in game, so `03_Vegetation & Nature.txt` browses as **Vegetation & Nature**.

## Adding an object to a category

Append its model name to the category's `.txt` and restart the script. The name does **not** have to
exist in `ObjectList.ini` — a model the list has never seen is resolved by name and folded into the
database, so it will also turn up in search and save correctly.

To move an object, delete the line from one file and add it to another.

## Filtering a category by DLC

Every prop category opens with a **DLC** row at the top. Scrolling it left/right narrows the list below
to the objects one add-on pack shipped — the pack is read off the prefix on the model name
(`vw_prop_casino_art_01a` is the Diamond Casino, `h4_prop_...` is Cayo Perico), and anything no prefix
claims counts as **Base Game**. A category only offers the packs it actually contains, so the filter can
never empty the list. The table of prefixes lives in `Dlc.cs`.

Vehicles and peds have no such row: their names (`deveste`, `S_F_Y_Hooker_01`) carry no DLC prefix to
read — a vehicle name says nothing about the pack it shipped in, and a ped prefix names its family
(`a_c_` animal, `s_m_` service) rather than an add-on.

## Favorites

The first category of every type is **Favorites**, and it is the one the mod maintains itself. Highlight
any object, vehicle or ped — in a category or in the search results — and press the *Favorite* button
(`E` / gamepad `X`) to star it. Starred models are marked with a star wherever they are listed, and
pressing the button again unstars them.

The list is written straight back to disk, one file per type, so it survives a restart:

```
scripts\MapEditor\Favorites\Props.txt
scripts\MapEditor\Favorites\Vehicles.txt
scripts\MapEditor\Favorites\Peds.txt
```

The format is the same as a category file, so the list can be edited or shared by hand — but unlike a
category file, a model name the object list does not know is dropped rather than resolved, because this
file is written by the mod and an unknown name in it is a stale entry.

## Adding a category

Drop a new `.txt` into the folder. Number it to place it in the menu.

Nothing can fall through the cracks: any model that no file claims is collected into an
**Uncategorized** entry at the bottom of the menu, and **All** always lists everything.

## Regenerating

Each folder was generated from the flat list the mod loads that type from. To re-derive a split from
scratch after a list gains new models:

```
py tools/generate_categories.py                 # Props,   from ObjectList.ini
py tools/generate_vehicle_ped_categories.py     # Vehicles and Peds, from temp/VehicleList.ini and temp/PedList.ini
```

Vehicles are grouped by the class the game itself gives them (`vehicles.meta`), peds by the family
their model name announces (`a_c_` animals, `ig_` story, `s_m_` service…).

Both scripts **overwrite** the files they own, discarding hand-edits — for a one-off change, edit the
`.txt` directly instead. The same goes for the in-game *Rebuild Vehicle & Ped Categories*, which
throws the two generated folders away and rebuilds them from the game.
