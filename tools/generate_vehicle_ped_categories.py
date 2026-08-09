"""Splits VehicleList.ini and PedList.ini into per-category files under Categories/.

The game reads those files at startup (see ObjectCategories.cs); they are the source of truth for
which category a vehicle or ped lands in. Re-run this only to re-derive the split from scratch
after the lists gain new models -- it OVERWRITES the category files, so any hand-edits are lost.
To move a single model between categories, edit the .txt files directly instead of touching this.

    py tools/generate_vehicle_ped_categories.py

Vehicles are grouped by the class the game itself gives them in vehicles.meta (the same grouping
GET_VEHICLE_CLASS_FROM_NAME returns, so the in-game *Rebuild Vehicle & Ped Categories* lands them
in the same buckets). The table below was derived from that data once; a model the table has never
seen falls into "Other Vehicles" and is reported, so a new DLC only needs its handful of names
added rather than the whole table rebuilt.

Peds carry their family in the model name (a_c_ animals, s_m_ service, ig_ story...), so they are
classified by prefix instead of a table.
"""

import io
import os
import re
import sys
from collections import OrderedDict

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

# The lists the mod loads its vehicle and ped databases from at startup (MapEditor.cs). They are
# dumps of the running game, so they -- not this script -- decide what exists.
VEHICLE_LIST = os.path.join(ROOT, "temp", "VehicleList.ini")
PED_LIST = os.path.join(ROOT, "temp", "PedList.ini")

VEHICLE_OUT = os.path.join(ROOT, "Categories", "Vehicles")
PED_OUT = os.path.join(ROOT, "Categories", "Peds")

VEHICLE_UNKNOWN = "Other Vehicles"
PED_UNKNOWN = "Other Peds"

# Model name -> class, from the game's own vehicles.meta. Trailers are split out of Utility: half
# of that class is trailers, and they are not what anyone browsing "Utility" is after.
VEHICLE_CATEGORIES = OrderedDict([
    ("Compacts", [
        "asbo", "blista", "brioso", "brioso2", "brioso3", "club", "dilettante", "dilettante2",
        "issi2", "issi3", "issi4", "issi5", "issi6", "kanjo", "panto", "prairie", "rhapsody",
        "weevil",
    ]),
    ("Sedans", [
        "asea", "asea2", "asterope", "asterope2", "chavosv6", "cinquemila", "cog55", "cog552",
        "cognoscenti", "cognoscenti2", "deity", "driftchavosv6", "drifthardy", "driftvorschlag",
        "emperor", "Emperor2", "emperor3", "fugitive", "glendale", "glendale2", "hardy", "ingot",
        "intruder", "limo2", "minimus", "premier", "primo", "primo2", "regina", "rhinehart",
        "romero", "schafter2", "schafter5", "schafter6", "sentinel6", "stafford", "stanier",
        "stratum", "stretch", "superd", "surge", "tailgater", "tailgater2", "vorschlaghammer",
        "warrener", "warrener2", "washington",
    ]),
    ("SUVs", [
        "aleutian", "astron", "astron2", "Baller", "baller2", "baller3", "baller4", "baller5",
        "baller6", "baller7", "baller8", "BjXL", "castigator", "cavalcade", "cavalcade2",
        "cavalcade3", "contender", "dorado", "dubsta", "dubsta2", "everon3", "fq2", "GRANGER",
        "granger2", "gresley", "habanero", "huntley", "issi8", "iwagen", "jubilee", "landstalker",
        "landstalker2", "MESA", "mesa2", "Novak", "patriot", "patriot2", "radi", "rebla",
        "rocoto", "Seminole", "seminole2", "serrano", "squaddie", "toros", "vivanite",
        "woodlander", "xls", "xls2",
    ]),
    ("Coupes", [
        "cogcabrio", "driftfr36", "driftsentinel2", "eurosX32", "exemplar", "f620", "felon",
        "felon2", "fr36", "jackal", "kanjosj", "oracle", "oracle2", "postlude", "previon",
        "sentinel", "sentinel2", "windsor", "windsor2", "zion", "zion2",
    ]),
    ("Muscle", [
        "arbitergt", "blade", "brigham", "broadway", "buccaneer", "buccaneer2", "buffalo4",
        "buffalo5", "chino", "chino2", "clique", "clique2", "coquette3", "deviant", "Dominator",
        "dominator10", "dominator2", "dominator3", "dominator4", "dominator5", "dominator6",
        "dominator7", "dominator8", "dominator9", "driftdominator10", "driftdominator9",
        "driftgauntlet4", "driftyosemite", "dukes", "dukes2", "dukes3", "ellie", "eudora",
        "faction", "faction2", "faction3", "Gauntlet", "gauntlet2", "gauntlet3", "gauntlet4",
        "gauntlet5", "greenwood", "hermes", "hotknife", "hustler", "impaler", "impaler2",
        "impaler3", "impaler4", "impaler5", "impaler6", "imperator", "imperator2", "imperator3",
        "lurcher", "manana2", "moonbeam", "moonbeam2", "nightshade", "peyote2", "Phoenix",
        "picador", "ratloader", "ratloader2", "ruiner", "ruiner2", "ruiner3", "ruiner4",
        "sabregt", "sabregt2", "slamvan", "slamvan2", "slamvan3", "slamvan4", "slamvan5",
        "slamvan6", "stalion", "stalion2", "tahoma", "tampa", "tampa3", "tampa4", "tulip",
        "tulip2", "vamos", "vigero", "vigero2", "vigero3", "virgo", "virgo2", "virgo3", "voodoo",
        "voodoo2", "weevil2", "yosemite", "yosemite2",
    ]),
    ("Sports Classics", [
        "ardent", "astrale", "btype", "btype2", "btype3", "casco", "cheburek", "cheetah2",
        "cheetah3", "coquette2", "coquette5", "deluxo", "driftcheburek", "driftjester3",
        "driftnebula", "Dynasty", "fagaloa", "feltzer3", "gt500", "gt750", "infernus2", "itali2",
        "jb700", "jb7002", "mamba", "manana", "michelli", "monroe", "nebula", "peyote", "peyote3",
        "pigalle", "rapidgt3", "retinue", "retinue2", "savestra", "stinger", "stingergt",
        "stromberg", "swinger", "toreador", "torero", "tornado", "tornado2", "tornado3",
        "tornado4", "tornado5", "tornado6", "turismo2", "uranus", "viseris", "z190", "zion3",
        "Ztype",
    ]),
    ("Sports", [
        "alpha", "banshee", "banshee3", "bestiagts", "blista2", "blista3", "buffalo", "buffalo2",
        "buffalo3", "calico", "carbonizzare", "comet2", "comet3", "comet4", "comet5", "comet6",
        "comet7", "coquette", "coquette4", "coquette6", "corsita", "coureur", "cypher", "drafter",
        "driftcypher", "drifteuros", "driftfuto", "driftfuto2", "driftjester", "driftremus",
        "driftrt3000", "driftsentinel", "drifttampa", "driftzr350", "elegy", "elegy2", "envisage",
        "Euros", "everon2", "feltzer2", "flashgt", "furoregt", "fusilade", "futo", "futo2",
        "gauntlet6", "gb200", "growler", "hotring", "imorgon", "issi7", "italigto", "italirsx",
        "jester", "jester2", "jester3", "jester4", "jester5", "jugular", "khamelion", "komoda",
        "kuruma", "kuruma2", "locust", "lynx", "massacro", "massacro2", "neo", "neon", "ninef",
        "ninef2", "niobe", "omnis", "omnisegt", "panthere", "paragon", "paragon2", "paragon3",
        "pariah", "penumbra", "penumbra2", "r300", "raiden", "RapidGT", "RapidGT2", "rapidgt4",
        "raptor", "remus", "revolter", "rt3000", "ruston", "s95", "schafter3", "schafter4",
        "schlagen", "schwarzer", "sentinel3", "sentinel4", "sentinel5", "SEVEN70", "sm722",
        "SPECTER", "SPECTER2", "stingertt", "streiter", "Sugoi", "sultan", "sultan2", "sultan3",
        "Surano", "tampa2", "tenf", "tenf2", "tropos", "vectre", "verlierer2", "veto", "veto2",
        "vstr", "zr350", "zr380", "zr3802", "zr3803",
    ]),
    ("Super", [
        "adder", "autarch", "banshee2", "bullet", "champion", "cheetah", "cyclone", "cyclone2",
        "deveste", "emerus", "entity2", "entity3", "entityxf", "fmj", "fmj2", "furia", "gp1",
        "ignus", "ignus2", "infernus", "italigtb", "italigtb2", "krieger", "le7b", "lm87",
        "luiva", "nero", "nero2", "osiris", "penetrator", "pfister811", "pipistrello",
        "prototipo", "reaper", "s80", "sc1", "scramjet", "sheava", "sultanrs", "suzume", "t20",
        "taipan", "tempesta", "tezeract", "thrax", "tigon", "torero2", "turismo3", "turismor",
        "tyrant", "tyrus", "vacca", "vagner", "vigilante", "virtue", "visione", "voltic",
        "voltic2", "xa21", "xtreme", "zeno", "zentorno", "zorrusso",
    ]),
    ("Open Wheel", [
        "formula", "formula2", "openwheel1", "openwheel2",
    ]),
    ("Motorcycles", [
        "akuma", "avarus", "bagger", "bati", "bati2", "bf400", "carbonrs", "chimera",
        "cliffhanger", "daemon", "daemon2", "deathbike", "deathbike2", "deathbike3", "defiler",
        "diablous", "diablous2", "double", "enduro", "esskey", "faggio", "faggio2", "faggio3",
        "fcr", "fcr2", "gargoyle", "hakuchou", "hakuchou2", "hexer", "innovation", "lectro",
        "manchez", "manchez2", "manchez3", "nemesis", "nightblade", "oppressor", "oppressor2",
        "pcj", "pizzaboy", "powersurge", "ratbike", "reever", "rrocket", "ruffian", "Sanchez",
        "sanchez2", "sanctus", "shinobi", "shotaro", "sovereign", "Stryder", "thrust", "Vader",
        "vindicator", "vortex", "wolfsbane", "zombiea", "zombieb",
    ]),
    ("Cycles", [
        "BMX", "cruiser", "fixter", "inductor", "inductor2", "scorcher", "tribike", "tribike2",
        "tribike3",
    ]),
    ("Off-road", [
        "BfInjection", "bifta", "blazer", "blazer2", "blazer3", "blazer4", "blazer5", "Bodhi2",
        "boor", "brawler", "bruiser", "bruiser2", "bruiser3", "brutus", "brutus2", "brutus3",
        "caracara", "caracara2", "dloader", "draugur", "driftl352", "dubsta3", "dune", "dune2",
        "dune3", "dune4", "dune5", "everon", "firebolt", "freecrawler", "hellion", "insurgent",
        "insurgent2", "insurgent3", "kalahari", "kamacho", "l35", "l352", "marshall", "menacer",
        "MESA3", "monster", "monster3", "monster4", "monster5", "monstrociti", "nightshark",
        "outlaw", "patriot3", "RancherXL", "rancherxl2", "ratel", "rcbandito", "Rebel", "rebel2",
        "riata", "sandking", "sandking2", "technical", "technical2", "technical3", "terminus",
        "trophytruck", "trophytruck2", "vagrant", "verus", "winky", "yosemite1500", "yosemite3",
        "zhaba",
    ]),
    ("Vans", [
        "bison", "Bison2", "Bison3", "bobcatXL", "boxville", "boxville2", "boxville3",
        "boxville4", "boxville5", "boxville6", "Burrito", "burrito2", "burrito3", "Burrito4",
        "burrito5", "CAMPER", "gburrito", "gburrito2", "journey", "journey2", "minivan",
        "minivan2", "paradise", "pony", "pony2", "rumpo", "rumpo2", "rumpo3", "speedo", "speedo2",
        "speedo4", "speedo5", "SURFER", "Surfer2", "surfer3", "Taco", "youga", "youga2", "youga3",
        "youga4", "youga5",
    ]),
    ("Commercial", [
        "Benson", "benson2", "Biff", "cerberus", "cerberus2", "cerberus3", "Hauler", "Hauler2",
        "Mule", "Mule2", "Mule3", "mule4", "mule5", "Packer", "Phantom", "phantom2", "phantom3",
        "Phantom4", "Pounder", "pounder2", "stockade", "stockade3", "stockade4", "terbyte",
    ]),
    ("Industrial", [
        "bulldozer", "cutter", "dump", "FLATBED", "flatbed2", "guardian", "handler", "Mixer",
        "Mixer2", "Rubble", "TipTruck", "TipTruck2",
    ]),
    ("Utility", [
        "Airtug", "caddy", "Caddy2", "caddy3", "docktug", "driftkeitora", "FORKLIFT", "keitora",
        "Mower", "Ripley", "Sadler", "sadler2", "scrap", "slamtruck", "TOWTRUCK", "Towtruck2",
        "towtruck3", "towtruck4", "TRACTOR", "tractor2", "tractor3", "utillitruck",
        "utillitruck2", "Utillitruck3",
    ]),
    ("Trailers", [
        "armytanker", "armytrailer", "armytrailer2", "baletrailer", "boattrailer", "boattrailer2",
        "boattrailer3", "docktrailer", "freighttrailer", "graintrailer", "proptrailer",
        "raketrailer", "tanker", "tanker2", "tr2", "tr3", "tr4", "trailerlarge", "trailerlogs",
        "trailers", "trailers2", "trailers3", "trailers4", "trailers5", "trailersmall",
        "trailersmall2", "trflat", "tvtrailer", "tvtrailer2",
    ]),
    ("Service", [
        "Airbus", "brickade", "brickade2", "BUS", "coach", "pbus2", "rallytruck", "Rentalbus",
        "taxi", "TOURBUS", "Trash", "trash2", "vivanite2", "wastelander",
    ]),
    ("Emergency", [
        "AMBULANCE", "FBI", "FBI2", "firetruk", "lguard", "pbus", "polbuffalo", "polbuffalo6",
        "polcaracara", "polcoquette4", "poldominator10", "poldorado", "polfaction2",
        "polgauntlet", "polgreenwood", "police", "police2", "police3", "police4", "police5",
        "policeb", "policeb2", "policeold1", "policeold2", "policet", "policet3", "polimpaler5",
        "polimpaler6", "polterminus", "pRanger", "RIOT", "riot2", "SHERIFF", "sheriff2",
    ]),
    ("Military", [
        "apc", "BARRACKS", "BARRACKS2", "BARRACKS3", "barrage", "chernobog", "CRUSADER",
        "halftrack", "khanjali", "minitank", "RHINO", "scarab", "scarab2", "scarab3", "thruster",
        "vetir",
    ]),
    ("Boats", [
        "avisa", "Dinghy", "dinghy2", "dinghy3", "dinghy4", "dinghy5", "jetmax", "kosatka",
        "longfin", "marquis", "patrolboat", "Predator", "seashark", "seashark2", "seashark3",
        "speeder", "speeder2", "squalo", "submersible", "submersible2", "Suntrap", "toro",
        "toro2", "tropic", "tropic2", "tug",
    ]),
    ("Helicopters", [
        "akula", "annihilator", "annihilator2", "buzzard", "Buzzard2", "Cargobob", "cargobob2",
        "Cargobob3", "Cargobob4", "cargobob5", "conada", "conada2", "Frogger", "frogger2",
        "havok", "hunter", "maverick", "maverick2", "polmav", "savage", "seasparrow",
        "seasparrow2", "seasparrow3", "skylift", "supervolito", "supervolito2", "swift", "swift2",
        "valkyrie", "valkyrie2", "volatus",
    ]),
    ("Planes", [
        "alkonost", "alphaz1", "avenger", "avenger2", "avenger3", "avenger4", "besra", "BLIMP",
        "BLIMP2", "blimp3", "bombushka", "cargoplane", "cargoplane2", "cuban800", "dodo",
        "duster", "duster2", "howard", "hydra", "jet", "Lazer", "luxor", "luxor2", "mammatus",
        "microlight", "Miljet", "mogul", "molotok", "nimbus", "nokota", "pyro", "raiju", "rogue",
        "seabreeze", "Shamal", "starling", "streamer216", "strikeforce", "Stunt", "titan",
        "titan2", "tula", "velum", "velum2", "vestra", "volatol",
    ]),
    ("Trains", [
        "cablecar", "freight", "freight2", "freightcar", "freightcar2", "freightcar3",
        "freightcont1", "freightcont2", "freightgrain", "metrotrain", "tankercar",
    ]),
])

# First match wins. The prefix is the ped family the game itself uses, so this needs no table.
PED_RULES = [
    (re.compile(r"^a_c_"), "Animals"),
    (re.compile(r"^a_[fm]_"), "Ambient"),
    (re.compile(r"^s_[fm]_"), "Service & Police"),
    (re.compile(r"^(g_[fm]_|hc_)"), "Gangs"),
    (re.compile(r"^u_[fm]_"), "Unique"),
    (re.compile(r"^ig_"), "Story - In-Game"),
    (re.compile(r"^(cs_|csb_)"), "Story - Cutscene"),
    (re.compile(r"^mp_"), "Multiplayer"),
    (re.compile(r"^(player_|p_[a-z]+_\d)"), "Players"),
]

# Sort order in the in-game category menu.
PED_ORDER = [
    "Players", "Story - In-Game", "Story - Cutscene", "Ambient", "Service & Police", "Gangs",
    "Unique", "Multiplayer", "Animals", PED_UNKNOWN,
]


def read_list(path):
    """The names, in the casing the .ini holds them -- the mod's database keys are case-sensitive."""
    if not os.path.exists(path):
        sys.exit("not found: " + path)
    names = []
    with io.open(path, encoding="utf-8") as handle:
        for line in handle:
            line = line.strip()
            if line:
                names.append(line.split("=")[0])
    return names


def classify_ped(name):
    lowered = name.lower()
    for pattern, category in PED_RULES:
        if pattern.match(lowered):
            return category
    return PED_UNKNOWN


def bucket_vehicles(names):
    known = {}
    for category, members in VEHICLE_CATEGORIES.items():
        for member in members:
            known[member.lower()] = category

    buckets = OrderedDict((category, []) for category in VEHICLE_CATEGORIES)
    buckets.setdefault(VEHICLE_UNKNOWN, [])
    for name in names:
        buckets[known.get(name.lower(), VEHICLE_UNKNOWN)].append(name)
    return buckets


def bucket_peds(names):
    buckets = OrderedDict((category, []) for category in PED_ORDER)
    for name in names:
        buckets[classify_ped(name)].append(name)
    return buckets


def write(out_dir, buckets, label):
    if not os.path.isdir(out_dir):
        os.makedirs(out_dir)
    for stale in os.listdir(out_dir):
        if stale.endswith(".txt"):
            os.remove(os.path.join(out_dir, stale))

    index = 0
    total = 0
    for category, members in buckets.items():
        if not members:
            continue
        index += 1
        # The NN_ prefix fixes the menu order; ObjectCategories.cs strips it from the display name.
        filename = "%02d_%s.txt" % (index, category.replace("/", "-"))
        with io.open(os.path.join(out_dir, filename), "w", encoding="utf-8", newline="\r\n") as out:
            out.write(u"# %s\n" % category)
            out.write(u"# One model name per line. Lines starting with # are ignored.\n")
            out.write(u"# Add a model to this category by appending its name below.\n")
            for member in sorted(members, key=lambda n: n.lower()):
                out.write(member + u"\n")
        total += len(members)
        print("%6d  %s" % (len(members), filename))

    print("\n%d %s -> %d category files in %s\n" % (total, label, index, out_dir))


def main():
    vehicles = bucket_vehicles(read_list(VEHICLE_LIST))
    peds = bucket_peds(read_list(PED_LIST))

    write(VEHICLE_OUT, vehicles, "vehicles")
    write(PED_OUT, peds, "peds")

    # A model no rule claims still reaches the menu (ObjectCategories collects it under
    # "Uncategorized"), but it is a sign this script has fallen behind a new DLC.
    for label, unclaimed in (("vehicles", vehicles[VEHICLE_UNKNOWN]), ("peds", peds[PED_UNKNOWN])):
        if unclaimed:
            print("%d %s fell through to the catch-all: %s"
                  % (len(unclaimed), label, ", ".join(sorted(unclaimed))))


if __name__ == "__main__":
    main()
