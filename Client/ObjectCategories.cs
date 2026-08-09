using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CitizenFX.Core;
using MapEditor.Platform;

namespace MapEditor
{
	public class ObjectCategory
	{
		public ObjectCategory(string name)
		{
			Name = name;
		}

		public string Name { get; private set; }

		public Dictionary<string, int> Objects = new Dictionary<string, int>();
	}

	/// <summary>
	/// Splits the flat model lists into browsable categories.
	///
	/// In the SP build the source of truth was 75 plain-text files under scripts\MapEditor\Categories\,
	/// read one by one at startup, with the vehicle and ped folders generated on first run if absent. A
	/// FiveM client reaches resource files only through LoadResourceFile, one call each, and cannot write
	/// any of them back — so the .txt files are merged into a single data/categories.json at build time by
	/// tools/build_categories.py, and that is what ships. The .txt files remain the thing you edit; they
	/// simply do not travel with the resource.
	/// </summary>
	public static class ObjectCategories
	{
		private const string CategoriesFile = "data/categories.json";

		/// <summary>Pseudo-category placed first, holding the whole list, so the old flat browsing still works.</summary>
		public const string AllCategoryName = "All";

		/// <summary>Catches models that no category file claims, so an object can never drop out of the menu.</summary>
		public const string UncategorizedName = "Uncategorized";

		public static List<ObjectCategory> Props = new List<ObjectCategory>();
		public static List<ObjectCategory> Vehicles = new List<ObjectCategory>();
		public static List<ObjectCategory> Peds = new List<ObjectCategory>();

		public static List<ObjectCategory> For(ObjectTypes type)
		{
			switch (type)
			{
				case ObjectTypes.Vehicle: return Vehicles;
				case ObjectTypes.Ped: return Peds;
				default: return Props;
			}
		}

		internal static async Task LoadAll()
		{
			Json document = null;
			string content = ResourceFiles.ReadText(CategoriesFile);
			if (content != null)
			{
				document = Json.TryParse(content);
				if (document == null)
					Log.Info("{0} is not valid JSON; every object will browse under \"All\".", CategoriesFile);
			}

			Props = await Load(document, "Props", ObjectDatabase.MainDb);
			Vehicles = await Load(document, "Vehicles", ObjectDatabase.VehicleDb);
			Peds = await Load(document, "Peds", ObjectDatabase.PedDb);

			// Must follow the loads above: a category file can fold a model the list has never seen into the
			// database, and a favorite naming that model would otherwise be dropped as unknown.
			Favorites.LoadAll();

			// First, ahead of even "All": whatever else the player is browsing for, their own shortlist is
			// the one they came back for.
			Props.Insert(0, Favorites.CategoryFor(ObjectTypes.Prop));
			Vehicles.Insert(0, Favorites.CategoryFor(ObjectTypes.Vehicle));
			Peds.Insert(0, Favorites.CategoryFor(ObjectTypes.Ped));
		}

		private static async Task<List<ObjectCategory>> Load(Json document, string group, Dictionary<string, int> db)
		{
			var categories = new List<ObjectCategory>();
			if (db == null) return categories;

			var claimed = new HashSet<string>();
			if (document != null)
			{
				// The build script already writes the categories in menu order, so unlike the SP build
				// there is no filename to sort by here.
				var budget = new Frame.Budget(4);
				foreach (var entry in document[group].Items)
				{
					var category = new ObjectCategory(entry["name"].AsString("Unnamed"));
					foreach (var model in entry["models"].Items)
					{
						await budget.YieldIfExpired();

						var name = model.AsString(null);
						if (string.IsNullOrEmpty(name) || category.Objects.ContainsKey(name)) continue;

						int hash;
						if (!db.TryGetValue(name, out hash))
						{
							// A hand-added model the list has never seen: folded into the database so
							// search and saving recognise it. Before "All" is built below, or it
							// would be missing there.
							hash = new Model(name).Hash;
							db[name] = hash;
						}
						category.Objects.Add(name, hash);
						claimed.Add(name);
					}
					if (category.Objects.Count > 0)
						categories.Add(category);
				}
			}

			// Everything, first, so a user who does not care about categories keeps the old behaviour.
			var all = new ObjectCategory(AllCategoryName);
			foreach (var pair in db)
				all.Objects.Add(pair.Key, pair.Value);
			categories.Insert(0, all);

			// With no category data at all there is nothing for this to contrast with: every object
			// would simply be listed twice, under "All" and again under "Uncategorized".
			if (categories.Count == 1) return categories;

			var uncategorized = new ObjectCategory(UncategorizedName);
			foreach (var pair in db.Where(pair => !claimed.Contains(pair.Key)))
				uncategorized.Objects.Add(pair.Key, pair.Value);
			if (uncategorized.Objects.Count > 0)
				categories.Add(uncategorized);

			return categories;
		}
	}
}
