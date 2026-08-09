using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using CitizenFX.Core;
using CitizenFX.Core.Native;
using MapEditor.Platform;

namespace MapEditor
{
    public static class ObjectDatabase
    {
        public static Dictionary<string, int> MainDb;

		/// <summary>
		/// Mirrors <see cref="Settings.OmitInvalidObjects"/>. When off, models the game has no archetype for
		/// are not flagged in the object list. Nothing else follows from it: the flag is a label, and the
		/// editor tries every model the player picks either way.
		/// </summary>
		public static bool FlagUnavailable = true;

	    public static Dictionary<string, int> VehicleDb;

		public static Dictionary<string, int> PedDb;

		/// <summary>The list one object type is browsed, searched and placed from.</summary>
		public static Dictionary<string, int> DbFor(ObjectTypes type)
		{
			switch (type)
			{
				case ObjectTypes.Vehicle: return VehicleDb;
				case ObjectTypes.Ped: return PedDb;
				default: return MainDb;
			}
		}

		/// <summary>
		/// The reverse of <see cref="DbFor"/>, built on the first lookup of a type. An entity in the world only
		/// carries its model hash, so naming one means walking a list of 25,000 names backwards — worth doing
		/// once and keeping, since <see cref="MapEditor.DrawWorldObjectNames"/> asks this of every object around
		/// the camera.
		/// </summary>
		private static readonly Dictionary<ObjectTypes, Dictionary<int, string>> NameByHash =
			new Dictionary<ObjectTypes, Dictionary<int, string>>();

		/// <summary>
		/// The name the list of <paramref name="type"/> holds <paramref name="hash"/> under, or null when no
		/// name in it resolves to that hash — an unlisted model, which can still be copied but cannot be
		/// starred, as a favorite is stored by name.
		/// </summary>
		public static string NameFor(ObjectTypes type, int hash)
		{
			var db = DbFor(type);
			if (db == null) return null;

			Dictionary<int, string> names;
			if (!NameByHash.TryGetValue(type, out names))
			{
				NameByHash[type] = names = new Dictionary<int, string>(db.Count);
				// Two names for one hash is a duplicate list entry, not a second model: keep the first.
				foreach (var pair in db)
				{
					if (!names.ContainsKey(pair.Value))
						names.Add(pair.Value, pair.Key);
				}
			}

			string name;
			return names.TryGetValue(hash, out name) ? name : null;
		}

		/// <summary>
		/// The name whichever of the three lists holds <paramref name="hash"/> under, or null when none of
		/// them does. A caller that knows what kind of model it has should ask <see cref="NameFor"/>
		/// directly; this is for the ones that only ever see a hash.
		/// </summary>
		public static string AnyNameFor(int hash)
		{
			return NameFor(ObjectTypes.Prop, hash)
			       ?? NameFor(ObjectTypes.Vehicle, hash)
			       ?? NameFor(ObjectTypes.Ped, hash);
		}

		/// <summary>
		/// How a model is named in the console. The hash is always there — an unlisted model has nothing else
		/// to go by, and it is what the game itself is asked about — and the name joins it when one of the
		/// lists knows it.
		/// </summary>
		public static string Describe(int hash)
		{
			string number = hash.ToString(CultureInfo.InvariantCulture);
			string name = AnyNameFor(hash);
			return name == null ? number : name + " (" + number + ")";
		}

		/// <summary>Drops the reverse lookups of a list that is about to be replaced.</summary>
		internal static void ResetNameCache()
		{
			NameByHash.Clear();
		}

		public static Dictionary<Relationship, RelationshipGroup> RelationshipDb = new Dictionary<Relationship, RelationshipGroup>();

	    public static RelationshipGroup BallasGroup;

	    public static RelationshipGroup GroveGroup;

        public enum PickupHash
        {
            Pistol = -105925489,
            CombatPistol = -1989692173,
            APPIstol = 996550793,
            MicroSMG = 496339155,
            SMG = 978070226,
            AssaultRifle = -214137936,
            CarbineRifle = -546236071,
            AdvancedRifle = -1296747938,
            SawnOffShotgun = -1766583645,
            PumpShotgun = -1456120371,
            AssaultShotgun = -1835415205,
            SniperRifle = -30788308,
            HeavySniper = 1765114797,
            MachineGun = -2050315855,
            CombatMachineGun = -1298986476,
            GrenadeLauncher = 779501861,
            RPG = 1295434569,
            Minigun = 792114228,
            Grenade = 1577485217,
            StickyBomb = 2081529176,
            Molotov = 768803961,
            PetrolCan = -962731009,
            SmokeGrenade = 483787975,
            Knife = 663586612,
            Bat = -2115084258,
            Hammer = 693539241,
            Crowbar = -2027042680,
            GolfClub = -1997886297,
            Nightstick = 1587637620,
            Parachute = 1735599485,
            Armour = 1274757841,
            Health = -1888453608,
            VehiclePistol = -1521817673,
            VehicleCombatPistol = -794112265,
            VehicleAPPistol = -863291131,
            VehicleMicroSMG = -1200951717,
            VehicleSMG = -864236261,
            VehicleSawnOffShotgun = 772217690,
            VehicleGrenade = -1491601256,
            VehicleMolotov = -2066319660,
            VehicleSmokeGrenade = 1705498857,
            VehicleStickyGrenade = 746606563,
            VehicleHealth = 160266735,
            VehicleArmour = 1125567497,
            MoneyCase = -831529621,
            MoneyBag = 545862290,
            MoneyMediumBag = 341217064,
            MoneyPaperBag = 1897726628,
            CrateUnfixed = 1852930709,
            Package = -2136239332,
            BulletAmmo = 1426343849,
            MissleAmmo = -107080240,
            Camera = -482507216,
            Snack = 483577702,
            Purse = 513448440,
            SecurityCase = -562499202,
            Money = -31919185,
            Wallet = 1575005502,
        }

        internal static void LoadEnumDatabases()
	    {
			VehicleDb = new Dictionary<string, int>();
			PedDb = new Dictionary<string, int>();
			ResetNameCache();

		    // Vehicles.
		    foreach (string veh in Enum.GetNames(typeof(VehicleHash)))
		    {
			    VehicleHash hash;
			    Enum.TryParse(veh, out hash);
				if(VehicleDb.ContainsKey(veh)) continue;
				VehicleDb.Add(veh, (int)hash);
		    }

			// Peds
			foreach (string ped in Enum.GetNames(typeof(PedHash)))
			{
				PedHash hash;
				Enum.TryParse(ped, out hash);
				if (PedDb.ContainsKey(ped)) continue;
				PedDb.Add(ped, (int)hash);
			}
		}

		/// <summary>
		/// Reads one of the flat "name=hash" lists out of the resource. ObjectList.ini is 880 KB and about
		/// 25,000 lines: parsing it in one go blocks the frame long enough for FiveM to log the resource as
		/// stalling the client, so the loop hands the frame back every few milliseconds.
		/// </summary>
		internal static async Task<Dictionary<string, int>> LoadFromResource(string path)
        {
            var loaded = new Dictionary<string, int>();
            ResetNameCache();

            // The flat lists are optional: data/categories.json carries the same model names and
            // ObjectCategories folds them back into these dictionaries. A missing list is normal.
            string content = ResourceFiles.ReadText(path);
            if (content == null) return loaded;

            var budget = new Frame.Budget(4);
            foreach (string line in ResourceFiles.Lines(content))
            {
                await budget.YieldIfExpired();

                if (line.Length == 0) continue;
                string[] s = line.Split('=');
                if (loaded.ContainsKey(s[0])) continue;

                if (s.Length == 1)
                {
                    loaded.Add(s[0], new Model(s[0]).Hash);
                }
                else
                {
                    int val;
                    // A malformed line used to throw out of the script's constructor.
                    if (!int.TryParse(s[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out val))
                        continue;
                    loaded.Add(s[0], val);
                }
            }
            return loaded;
        }

	    internal static void SetupRelationships()
	    {
		    foreach (var s in Enum.GetNames(typeof(Relationship)))
		    {
			    // FiveM's Mono sandbox refuses Enum.Parse(Type, string): any method calling it fails IL
			    // verification with a MethodAccessException before a line of it runs. The generic overload
			    // is not restricted.
			    Relationship hash;
			    if (!Enum.TryParse(s, out hash)) continue;
			    var group = World.AddRelationshipGroup("MAPEDITOR_" + s);
				// SHVDN3 moved this off World; the last argument makes the relationship bidirectional.
				group.SetRelationshipBetweenGroups(Game.Player.Character.RelationshipGroup, hash, true);
				RelationshipDb[hash] = group;
		    }

		    BallasGroup = World.AddRelationshipGroup("MAPEDITOR_BALLAS");
		    GroveGroup = World.AddRelationshipGroup("MAPEDITOR_GROVE");

			BallasGroup.SetRelationshipBetweenGroups(GroveGroup, Relationship.Hate, true);
		}

		/// <summary>
		/// The SP build's scripts\InvalidObjects.ini, carried into KVP by the first cut of this port. Kept
		/// only to be deleted: see <see cref="DropLegacyBlacklist"/>.
		/// </summary>
		private const string InvalidObjectsKey = "mapeditor:invalidObjects";

		/// <summary>
		/// Throws away the blacklist earlier versions of this resource wrote.
		///
		/// It recorded that IS_MODEL_VALID and IS_MODEL_IN_CDIMAGE both said no, and treated that as a fact
		/// about the model. It is a fact about the <i>place</i>: an archetype defined in an interior or DLC
		/// .#typ is only registered while the game has that interior's map data streamed in, so the same prop
		/// answers no in open country and yes inside the building it belongs to. Browsing the list once
		/// anywhere therefore condemned thousands of perfectly good models — permanently, since the answer was
		/// written to KVP and read back on the next start.
		///
		/// Nothing replaces it on disk. The two natives cost nothing to ask again, and asking them where the
		/// player is standing is the only answer that means anything.
		/// </summary>
		internal static void DropLegacyBlacklist()
		{
			if (Kvp.GetString(InvalidObjectsKey) == null) return;
			Kvp.Delete(InvalidObjectsKey);
			Log.Info("Discarded the saved \"invalid objects\" list: whether a model is known to the client " +
			         "depends on where the player is standing, so it was never something to remember.");
		}

		/// <summary>
		/// What the game answered about a model, the last time it was asked, at <see cref="_availabilityOrigin"/>.
		///
		/// The answer is only good for the spot it was given at, but it is good for every model at that spot,
		/// and the object menu asks it of every row it lays out — thousands of them on a redraw. So the answers
		/// are kept until the player has moved far enough for them to be worth doubting.
		/// </summary>
		private static readonly Dictionary<int, bool> Availability = new Dictionary<int, bool>();

		private static Vector3 _availabilityOrigin;
		private static bool _availabilityKnown;

		/// <summary>
		/// How far the player may move before the cached answers are thrown away, in metres.
		///
		/// Short, because the interesting distance is the one that puts a player inside an MLO: walking
		/// through a door is what turns that building's props from unknown to available, and it is a matter
		/// of a few metres. Being wrong in this direction only costs native calls.
		/// </summary>
		private const float ForgetDistance = 20f;

		/// <summary>
		/// Whether the game has an archetype registered for <paramref name="hash"/> where the player is now.
		///
		/// Either native saying yes is enough: they disagree on some builds for streamed-in interior and DLC
		/// props, and requiring both is what the old blacklist was filled from.
		/// </summary>
		public static bool IsRegisteredNow(int hash)
		{
			if (hash == 0) return false;
			var model = new Model(hash);
			return model.IsValid || model.IsInCdImage;
		}

		/// <summary>
		/// <see cref="IsRegisteredNow"/>, answered from the cache when the player has not moved since it was
		/// filled. This is what the menus ask; anything walking the whole database should ask the game
		/// directly instead, so that a player who moves mid-scan does not get half an answer from each spot.
		/// </summary>
		public static bool IsAvailableHere(int hash)
		{
			if (hash == 0) return false;

			var origin = SmartStreaming.Origin;
			if (!_availabilityKnown ||
			    (origin - _availabilityOrigin).LengthSquared() > ForgetDistance * ForgetDistance)
			{
				Availability.Clear();
				_availabilityOrigin = origin;
				_availabilityKnown = true;
			}

			bool available;
			if (Availability.TryGetValue(hash, out available)) return available;

			available = IsRegisteredNow(hash);
			Availability[hash] = available;
			return available;
		}

		/// <summary>
		/// Forgets the answers, so that the next question goes to the game. The distance check above covers
		/// the player moving; this covers everything else — the map data around them changing while they
		/// stand still, and a player who simply wants to see the list re-checked.
		/// </summary>
		public static void ForgetAvailability()
		{
			Availability.Clear();
			_availabilityKnown = false;
		}

	    internal static void SetPedRelationshipGroup(Ped ped, string group)
	    {
		    if (group == "Ballas")
		    {
			    ped.RelationshipGroup = BallasGroup;
				return;
		    }
		    if (group == "Grove")
		    {
			    ped.RelationshipGroup = GroveGroup;
				return;
		    }
		    Relationship outHash;
			if(!Enum.TryParse(group, out outHash)) return;
		    ped.RelationshipGroup = RelationshipDb[outHash];
	    }

		internal static Dictionary<string, string> ScrenarioDatabase = new Dictionary<string, string>
		{
			{"Drink Coffee",  "WORLD_HUMAN_AA_COFFEE"},
			{"Smoke", "WORLD_HUMAN_AA_SMOKE" },
			{"Smoke 2", "WORLD_HUMAN_SMOKING" },
			{"Binoculars",  "WORLD_HUMAN_BINOCULARS"},
			{"Bum", "WORLD_HUMAN_BUM_FREEWAY" },
			{"Cheering", "WORLD_HUMAN_CHEERING" },
			{"Clipboard", "WORLD_HUMAN_CLIPBOARD" },
			{"Drilling",  "WORLD_HUMAN_CONST_DRILL"},
			{"Drinking", "WORLD_HUMAN_DRINKING" },
			{"Drug Dealer", "WORLD_HUMAN_DRUG_DEALER"},
			{"Drug Dealer Hard", "WORLD_HUMAN_DRUG_DEALER_HARD" },
			{"Traffic Signaling",  "WORLD_HUMAN_CAR_PARK_ATTENDANT"},
			{"Filming", "WORLD_HUMAN_MOBILE_FILM_SHOCKING" },
			{"Leaf Blower", "WORLD_HUMAN_GARDENER_LEAF_BLOWER" },
			{"Golf Player", "WORLD_HUMAN_GOLF_PLAYER" },
			{"Guard Patrol", "WORLD_HUMAN_GUARD_PATROL" },
			{"Hammering", "WORLD_HUMAN_HAMMERING" },
			{"Janitor", "WORLD_HUMAN_JANITOR" },
			{"Musician", "WORLD_HUMAN_MUSICIAN" },
			{"Paparazzi", "WORLD_HUMAN_PAPARAZZI" },
			{"Party", "WORLD_HUMAN_PARTYING" },
			{"Picnic", "WORLD_HUMAN_PICNIC" },
			{"Push Ups", "WORLD_HUMAN_PUSH_UPS"},
			{"Shine Torch", "WORLD_HUMAN_SECURITY_SHINE_TORCH" },
			{"Sunbathe", "WORLD_HUMAN_SUNBATHE" },
			{"Sunbathe Back", "WORLD_HUMAN_SUNBATHE_BACK"},
			{"Tourist", "WORLD_HUMAN_TOURIST_MAP" },
			{"Mechanic", "WORLD_HUMAN_VEHICLE_MECHANIC" },
			{"Welding", "WORLD_HUMAN_WELDING" },
			{"Yoga", "WORLD_HUMAN_YOGA" },
		};
    }
}