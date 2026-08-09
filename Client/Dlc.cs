using System;
using System.Collections.Generic;
using System.Linq;

namespace MapEditor
{
	/// <summary>
	/// Which DLC shipped a model, read off the prefix Rockstar stamps on every add-on pack's assets
	/// ("vw_prop_casino_art_01a" is the Diamond Casino). Model names are all the object list carries,
	/// so the prefix is the only grouping available without shipping a per-model table.
	/// </summary>
	public static class Dlc
	{
		/// <summary>Narrows nothing. First in the list, so it is what a category opens on.</summary>
		public const string AllName = "All DLC";

		/// <summary>Everything the game shipped with, i.e. every model no pack prefix claims.</summary>
		public const string BaseGameName = "Base Game";

		private class Pack
		{
			public Pack(string name, params string[] prefixes)
			{
				Name = name;
				Prefixes = prefixes;
			}

			public readonly string Name;
			public readonly string[] Prefixes;
		}

		// Ordered by release, so the filter browses the way the player knows the packs. No prefix here is a
		// prefix of another ("xm_" vs "xm3_"), so first match wins and the order can be chronological.
		private static readonly Pack[] Packs =
		{
			new Pack("2015 Next Gen/PC Luxe DLC", "lux_"),
			new Pack("2015 Heist DLC", "hei_"),
			new Pack("2015 Lowriders DLC", "lr_"),
			new Pack("2015 Executives & Other Criminals", "apa_", "ex_"),
			new Pack("2016 Biker DLC", "bkr_"),
			new Pack("2016 Import/Export DLC", "imp_"),
			new Pack("2017 Gunrunning DLC", "gr_"),
			new Pack("2017 Smuggler's Run DLC", "sm_"),
			new Pack("2017 Doomsday Heist DLC", "xm_"),
			new Pack("2018 After Hours DLC", "ba_"),
			new Pack("2018 Arena War DLC", "xs_"),
			new Pack("2019 Diamond Casino & Resort DLC", "vw_"),
			new Pack("2019 Diamond Casino Heist DLC", "ch_"),
			new Pack("2020 Los Santos Summer Special DLC", "sum_"),
			new Pack("2020 The Cayo Perico Heist DLC", "h4_"),
			new Pack("2021 Los Santos Tuners DLC", "tr_"),
			new Pack("2021 The Contract DLC", "sf_"),
			new Pack("2022 The Criminal Enterprises DLC", "reh_"),
			new Pack("2023 The Los Santos Drug Wars DLC", "xm3_"),
			new Pack("2023 The San Andreas Mercenaries DLC", "m23_1"),
			new Pack("2023 The Chop Shop DLC", "m23_2"),
			new Pack("2024 Bottom Dollar Bounties", "m24_1"),
			new Pack("2024 Agents of Sabotage", "m24_2"),
			new Pack("2025 Money Fronts", "m25_1"),
			new Pack("2025 A Safehouse in the Hills", "m25_2"),
		};

		/// <summary>
		/// The prefix walk, memoised. The object list holds 25,000 models and the menu asks after them again
		/// on every refill, so the answer is kept rather than re-derived.
		/// </summary>
		private static readonly Dictionary<string, string> Names =
			new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		public static string NameFor(string model)
		{
			string name;
			if (Names.TryGetValue(model, out name))
				return name;

			name = Classify(model);
			Names[model] = name;
			return name;
		}

		private static string Classify(string model)
		{
			foreach (var pack in Packs)
			{
				foreach (var prefix in pack.Prefixes)
				{
					if (model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
						return pack.Name;
				}
			}
			return BaseGameName;
		}

		/// <summary>
		/// The values worth offering for a set of models: "All DLC", then only the packs that set
		/// actually contains, so a category never offers a filter that would empty it.
		/// </summary>
		public static List<string> Present(IEnumerable<string> models)
		{
			var found = new HashSet<string>(models.Select(NameFor));

			var result = new List<string> { AllName };
			if (found.Contains(BaseGameName))
				result.Add(BaseGameName);
			result.AddRange(Packs.Select(pack => pack.Name).Where(found.Contains));
			return result;
		}
	}
}
