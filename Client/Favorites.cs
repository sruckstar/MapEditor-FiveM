using System;
using System.Collections.Generic;
using MapEditor.Platform;

namespace MapEditor
{
	/// <summary>
	/// The models the player has starred while browsing, one KVP entry per object type. They are browsed as
	/// the first category of their type, so a model that took a search to find the first time is one step
	/// away every time after that.
	///
	/// Written back on every change rather than at shutdown: a resource can be restarted, or the game
	/// killed, at any moment, and a list this small costs nothing to rewrite.
	///
	/// The SP build kept these as plain-text files under scripts\MapEditor\Favorites\. The line-based
	/// format is unchanged, so a player's list can still be moved between the two builds by hand.
	/// </summary>
	public static class Favorites
	{
		private const string KeyPrefix = "mapeditor:favorites:";

		/// <summary>Pseudo-category holding the starred models, listed before "All".</summary>
		public const string CategoryName = "Favorites";

		private static readonly ObjectTypes[] Types = { ObjectTypes.Prop, ObjectTypes.Vehicle, ObjectTypes.Ped };

		/// <summary>
		/// The starred models of one type, in the order they were starred. The category below is rebuilt
		/// from this list, which is what keeps the menu listing them in that order rather than in whatever
		/// order a dictionary happens to hold them in after a removal.
		/// </summary>
		private static readonly Dictionary<ObjectTypes, List<string>> Models =
			new Dictionary<ObjectTypes, List<string>>();

		/// <summary>
		/// One category instance per type, created once and refilled in place. MapEditor caches a
		/// category's menu rows against the instance it was built from, and <see cref="ObjectCategories"/>
		/// re-inserts these whenever it reloads, so handing out a fresh instance would strand both.
		/// </summary>
		private static readonly Dictionary<ObjectTypes, ObjectCategory> Categories =
			new Dictionary<ObjectTypes, ObjectCategory>();

		public static ObjectCategory CategoryFor(ObjectTypes type)
		{
			ObjectCategory category;
			if (!Categories.TryGetValue(type, out category))
				Categories[type] = category = new ObjectCategory(CategoryName);
			return category;
		}

		public static bool IsFavorite(ObjectTypes type, string model)
		{
			return CategoryFor(type).Objects.ContainsKey(model);
		}

		/// <summary>
		/// Stars an unstarred model and unstars a starred one, saving either way. Returns true when the
		/// model ends up starred. A model no list knows cannot be starred: there would be no hash to place
		/// it with later.
		/// </summary>
		public static bool Toggle(ObjectTypes type, string model)
		{
			var db = ObjectDatabase.DbFor(type);
			if (db == null || !db.ContainsKey(model)) return false;

			var models = ModelsFor(type);
			// Remove reports whether it was there, so one call both answers "is it starred?" and undoes it.
			bool starred = !models.Remove(model);
			if (starred)
				models.Add(model);

			Rebuild(type);
			Save(type);
			return starred;
		}

		internal static void LoadAll()
		{
			foreach (var type in Types)
			{
				var db = ObjectDatabase.DbFor(type);
				var models = ModelsFor(type);
				models.Clear();

				var stored = Kvp.GetString(KeyFor(type));
				if (db != null && stored != null)
				{
					foreach (var raw in ResourceFiles.Lines(stored))
					{
						var name = raw.Trim();
						if (name.Length == 0 || name[0] == '#') continue;
						// Unlike a category file this one is written by the mod, so a name the list does not
						// know is a stale or mistyped entry rather than a model to resolve: drop it.
						if (!db.ContainsKey(name) || models.Contains(name)) continue;
						models.Add(name);
					}
				}

				Rebuild(type);
			}
		}

		/// <summary>Mirrors the ordered list into the category the menu reads.</summary>
		private static void Rebuild(ObjectTypes type)
		{
			var db = ObjectDatabase.DbFor(type);
			var category = CategoryFor(type);
			category.Objects.Clear();
			if (db == null) return;

			foreach (var model in ModelsFor(type))
			{
				int hash;
				if (db.TryGetValue(model, out hash))
					category.Objects[model] = hash;
			}
		}

		private static void Save(ObjectTypes type)
		{
			try
			{
				Kvp.SetString(KeyFor(type), string.Join("\n", ModelsFor(type).ToArray()));
			}
			catch (Exception e)
			{
				Compat.Notify("~b~~h~Map Editor~h~~w~~n~Failed to save favorites: " + e.Message);
			}
		}

		/// <summary>ObjectTypes.Prop becomes "mapeditor:favorites:Props", echoing the SP build's Props.txt.</summary>
		private static string KeyFor(ObjectTypes type)
		{
			return KeyPrefix + type + "s";
		}

		private static List<string> ModelsFor(ObjectTypes type)
		{
			List<string> models;
			if (!Models.TryGetValue(type, out models))
				Models[type] = models = new List<string>();
			return models;
		}
	}
}
