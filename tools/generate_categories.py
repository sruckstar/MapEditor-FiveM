"""Splits ObjectList.ini into per-category files under Categories/Props/.

The game reads those files at startup (see ObjectCategories.cs); they are the source of truth for
which category an object lands in. Re-run this only to re-derive the split from scratch after
ObjectList.ini gains new models -- it OVERWRITES the category files, so any hand-edits are lost.
To move a single object between categories, edit the .txt files directly instead of touching this.

    py tools/generate_categories.py
"""

import io
import os
import re
import sys
from collections import OrderedDict

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OBJECT_LIST = os.path.join(ROOT, "ObjectList.ini")
OUT_DIR = os.path.join(ROOT, "Categories", "Props")

# Model-family prefixes. ObjectList.ini is a dump of every model in the game, so it carries ped,
# weapon and world-chunk models alongside actual props; these keep them out of the themed lists.
RE_ANIMAL = re.compile(r"^a_c_")
RE_PED = re.compile(r"^(a|s|g|u|mp)_(f|m|g)_(m|o|y)_")
RE_PED_NAMED = re.compile(r"^(ig_|csb_|hc_|player_|mp_(f|m|g)_)")
RE_WEAPON = re.compile(r"^w_(am|ar|at|pi|sg|sb|me|ex|lr)_")
RE_LOD = re.compile(
    r"(_slod|slod\d|_lod$|_lod\d|_lod_|lod_|fake_lod|proxylod|shadow_proxy|shadowmesh"
    r"|dont_delete|propertyfudger|_emissive|emissive_|_ref$|_iref$|reflect|_glow"
    r"|horizonring|_skin$|_root$|_col$|_decal)"
)

# Area code -> map category. Checked before the themed keywords below, because a name like
# "ch2_rdprops_ngcab01" is a chunk of the Chumash map rather than a prop you would reuse elsewhere.
AREAS = [
    (r"^(ap1_|hei_ap1)", "Map - Airport"),
    (r"^bh1_", "Map - Vinewood Hills"),
    (r"^(vw_|vwd_)", "Map - Vinewood"),
    (r"^(dt1_|dt_|ci_|kt1_)", "Map - Downtown & City"),
    (r"^(cs\d_|csx_|cs_x_|ch1_|ch2_|ch3_|far_|golf_)", "Map - Countryside & Hills"),
    (r"^(des_|sc1_|sp1_|sp_)", "Map - Desert & Sandy Shores"),
    (r"^(fwy_|hw1_|hw_|ne_)", "Map - Freeway & Highway"),
    (r"^(metro|sub_|sub1_|subway|dcl_sub|tunnel)", "Map - Metro & Tunnels"),
    (r"^(port_|id1_|id2_|ind_)", "Map - Port & Industrial"),
    (r"^(vb_|bt1_|bea_)", "Map - Beach"),
    (r"^hei_", "Map - Heist DLC"),
    (r"^(lf_|db_|sm_|ss1_|apa_|ela_|bot_)", "Map - Houses & Suburbs"),
    (r"^(po1_|lr_|ex_|mt\d?_|fib_|new_|met_)", "Map - Misc Areas"),
]

# Themed keywords, first match wins. Substring tests against the lowercased model name.
THEMES = [
    ("Vegetation & Nature", ["tree", "bush", "plant", "fern", "flower", "grass", "hedge", "palm",
                             "cactus", "shrub", "ivy", "coral", "seaweed", "kelp", "reed", "branch",
                             "stump", "sage", "brittle", "fronds", "frnds", "weeds", "leaves",
                             "_veg", "crop", "_log"]),
    ("Rocks & Terrain", ["rock", "stone", "boulder", "cliff", "dirt", "sand", "gravel", "mud",
                         "mound"]),
    ("Fences, Walls & Gates", ["fnc", "fence", "wall", "gate", "railing", "barrier", "barier",
                               "bollard", "kerb"]),
    ("Roads & Traffic", ["roadcone", "traffic", "streetlight", "road", "sidewalk", "curb",
                         "manhole", "hydrant", "parkmeter", "busstop", "speedbump", "rail",
                         "track", "parking", "cone"]),
    ("Signs & Billboards", ["sign", "billboard", "logo", "neon", "banner", "poster", "flag",
                            "forsale"]),
    ("Buildings & Structures", ["build", "_bld", "house", "_hse", "shed", "garage", "tower",
                                "roof", "facade", "window", "door", "stair", "balcon", "pillar",
                                "column", "fountain", "arch", "bridge", "canopy", "awning",
                                "statue", "ramp"]),
    ("Interior & Furniture", ["chair", "table", "sofa", "couch", "_bed", "desk", "shelf",
                              "cabinet", "fridge", "sink", "toilet", "bath", "mirror", "carpet",
                              "_rug", "curtain", "clock", "book", "paint", "vase", "cushion",
                              "stool", "bench", "wardrobe", "locker", "towel", "pillow", "drawer",
                              "counter", "seat", "_pot"]),
    ("Containers & Rubbish", ["bin", "trash", "garbage", "dumpster", "crate", "box", "barrel",
                              "bucket", "sack", "container", "pallet", "rub", "junk", "_can",
                              "drum", "basket", "litter", "skip", "stack"]),
    ("Industrial & Construction", ["scafold", "scaffold", "crane", "cement", "concrete", "brick",
                                   "pipe", "girder", "beam", "tool", "ladder", "generator", "pump",
                                   "tank", "valve", "machin", "forklift", "excavat", "digger",
                                   "construct", "cnst", "factory", "silo", "vent", "aircon",
                                   "elecbox", "transformer", "cable", "wire", "fusebox", "boiler",
                                   "compressor", "telegraph", "satdish", "rope", "oil", "spade"]),
    ("Food & Drink", ["food", "drink", "burger", "pizza", "coffee", "_cup", "bottle", "beer",
                      "soda", "donut", "snack", "fruit", "plate", "bowl", "cutlery", "_bbq",
                      "kettle", "kitchen", "cook", "wine", "disptray", "cig"]),
    ("Weapons & Military", ["weapon", "_gun", "rifle", "pistol", "ammo", "bullet", "grenade",
                            "bomb", "_mine", "military", "army", "turret", "missile", "rocket",
                            "target", "shoot", "holster", "knife"]),
    ("Sports & Leisure", ["sport", "gym", "ball", "basket", "tennis", "golf", "skate", "pool",
                          "swing", "playground", "slide", "gambl", "casino", "poker", "chip",
                          "dart", "pinball", "arcade", "music", "speaker", "guitar", "weight",
                          "bike", "toy", "barbell", "boogieboard"]),
    ("Water & Beach", ["beach", "water", "boat", "buoy", "dock", "pier", "anchor", "surf",
                       "umbrella", "lilo", "pontoon", "jetty", "marina", "fish"]),
    ("Air & Aviation", ["_air", "air_", "plane", "heli", "airport", "runway", "hangar", "_jet",
                        "radar", "windsock", "luggage", "cropduster", "para"]),
    ("Vehicle Parts", ["wheel", "tyre", "tire", "engine", "bumper", "carpart", "exhaust",
                       "spoiler", "hubcap", "chassis", "carseat"]),
    ("Lighting & Electronics", ["light", "lamp", "lantern", "torch", "spotlight", "cctv", "camera",
                                "screen", "monitor", "laptop", "computer", "phone", "radio",
                                "antenna", "satellite", "projector", "_tv"]),
    ("Money, Drugs & Crime", ["money", "cash", "drug", "coke", "meth", "stash", "safe", "vault",
                              "evidence", "bodybag", "corpse", "blood", "gold"]),
    ("Medical & Science", ["med", "hospital", "surg", "lab", "chem", "micro", "syringe", "xray",
                           "bandage", "inhaler"]),
    ("Office & Paper", ["office", "paper", "file", "folder", "printer", "copier", "clipboard",
                        "note", "card", "newspaper", "news"]),
    ("Clothing & Personal", ["cloth", "shirt", "shoe", "hat", "mask", "bag", "case", "wallet",
                             "watch", "jewel", "glasses", "helmet"]),
]

# Sort order in the in-game category menu. Everything not listed here is appended alphabetically.
ORDER = [
    "Buildings & Structures", "Interior & Furniture", "Vegetation & Nature", "Rocks & Terrain",
    "Fences, Walls & Gates", "Roads & Traffic", "Signs & Billboards", "Lighting & Electronics",
    "Containers & Rubbish", "Industrial & Construction", "Food & Drink", "Sports & Leisure",
    "Water & Beach", "Air & Aviation", "Vehicle Parts", "Weapons & Military",
    "Money, Drugs & Crime", "Medical & Science", "Office & Paper", "Clothing & Personal",
    "Effects & VFX", "Props (Misc)", "Interiors (Misc)",
    "Map - Airport", "Map - Vinewood Hills", "Map - Vinewood", "Map - Downtown & City",
    "Map - Countryside & Hills", "Map - Desert & Sandy Shores", "Map - Freeway & Highway",
    "Map - Metro & Tunnels", "Map - Port & Industrial", "Map - Beach", "Map - Heist DLC",
    "Map - Houses & Suburbs", "Map - Misc Areas", "LOD & Map Filler",
    "Ped Models", "Ped Models - Animals", "Weapon Models", "Other Models",
]


def classify(name):
    n = name.lower()
    if RE_ANIMAL.match(n):
        return "Ped Models - Animals"
    if RE_WEAPON.match(n):
        return "Weapon Models"
    if RE_PED.match(n) or RE_PED_NAMED.match(n):
        return "Ped Models"
    if n.startswith("vfx_") or n.startswith("cloudhat"):
        return "Effects & VFX"
    for pattern, area in AREAS:
        if re.match(pattern, n):
            return area
    if RE_LOD.search(n):
        return "LOD & Map Filler"
    for category, keywords in THEMES:
        if any(k in n for k in keywords):
            return category
    if n.startswith("v_"):
        return "Interiors (Misc)"
    if "prop" in n or n.startswith(("p_", "proc_", "ng_")):
        return "Props (Misc)"
    return "Other Models"


def main():
    if not os.path.exists(OBJECT_LIST):
        sys.exit("ObjectList.ini not found at " + OBJECT_LIST)

    names = []
    with io.open(OBJECT_LIST, encoding="utf-8") as handle:
        for line in handle:
            line = line.strip()
            if line:
                names.append(line.split("=")[0])

    buckets = OrderedDict((category, []) for category in ORDER)
    for name in names:
        buckets.setdefault(classify(name), []).append(name)

    if not os.path.isdir(OUT_DIR):
        os.makedirs(OUT_DIR)
    for stale in os.listdir(OUT_DIR):
        if stale.endswith(".txt"):
            os.remove(os.path.join(OUT_DIR, stale))

    written = 0
    for index, (category, members) in enumerate(buckets.items()):
        if not members:
            continue
        # The NN_ prefix fixes the menu order; ObjectCategories.cs strips it from the display name.
        filename = "%02d_%s.txt" % (index + 1, category.replace("/", "-"))
        with io.open(os.path.join(OUT_DIR, filename), "w", encoding="utf-8", newline="\r\n") as out:
            out.write(u"# %s\n" % category)
            out.write(u"# One model name per line. Lines starting with # are ignored.\n")
            out.write(u"# Add an object to this category by appending its model name below.\n")
            for member in sorted(members):
                out.write(member + u"\n")
        written += 1
        print("%6d  %s" % (len(members), filename))

    print("\n%d objects -> %d category files in %s" % (len(names), written, OUT_DIR))


if __name__ == "__main__":
    main()
