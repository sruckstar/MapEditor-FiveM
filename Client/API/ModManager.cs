using System;
using System.Collections.Generic;
using CitizenFX.Core;
using MapEditor.Ui.Menus;
using MapEditor.Platform;

// Deliberately not a MapEditor.API namespace: "API" is also CitizenFX's own native wrapper class
// (CitizenFX.Core.Native.API), which every file here reaches for by its short name, and a MapEditor.API
// namespace would shadow it throughout MapEditor.*. The contract other resources use is events.
namespace MapEditor
{
	/// <summary>
	/// How another resource hooks into the editor: it puts its own row in the "Create Map for External Mod"
	/// menu, and while the player has it selected the editor hands it the map instead of saving it.
	///
	/// The SP build did this with a .NET reference. Another SHVDN script called
	/// <c>ModManager.SuscribeMod(new ModListener { ... })</c>, handed over an object with events on it, and
	/// the editor invoked those events directly. None of that is possible here: FiveM resources are loaded
	/// into separate app domains and cannot pass objects — or delegates — between themselves. So the whole
	/// thing is events, which is the platform's own answer to the same question.
	///
	/// From another resource:
	///
	/// <code>
	/// -- register once, e.g. on your own resource start
	/// TriggerEvent('mapeditor:registerMod', 'my_resource', 'My Mod', 'Description', 'Create map for My Mod')
	///
	/// -- then listen
	/// AddEventHandler('mapeditor:modSelected',   function(id) end)
	/// AddEventHandler('mapeditor:modDisconnected', function(id) end)
	/// AddEventHandler('mapeditor:mapSaved',      function(id, name, json) end)
	/// AddEventHandler('mapeditor:mapLoaded',     function(name, json) end)
	/// </code>
	///
	/// The map travels as its JSON document rather than as an object, for the same reason: a
	/// <see cref="Map"/> cannot cross a resource boundary, and JSON is the editor's native format anyway.
	/// </summary>
	public static class ModManager
	{
		public const string RegisterEvent = "mapeditor:registerMod";
		public const string SelectedEvent = "mapeditor:modSelected";
		public const string DisconnectedEvent = "mapeditor:modDisconnected";
		public const string MapSavedEvent = "mapeditor:mapSaved";
		public const string MapLoadedEvent = "mapeditor:mapLoaded";

		/// <summary>One resource that has asked for a row in the menu.</summary>
		private sealed class Mod
		{
			public string Id;
			public string Name;
			public string Description;
			public string ButtonString;
		}

		private static readonly List<Mod> Mods = new List<Mod>();
		private static Mod _current;

		internal static NativeMenu ModMenu;

		/// <summary>Whether any resource has registered. The menu is worthless until one has, so the row that
		/// opens it stays greyed out.</summary>
		internal static bool HasMods
		{
			get { return Mods.Count > 0; }
		}

		/// <summary>Whether the player has picked one, in which case saving hands the map over instead.</summary>
		internal static bool HasCurrentMod
		{
			get { return _current != null; }
		}

		/// <summary>
		/// Opens the door for other resources. Called by <see cref="MapEditorClient"/> with its own event
		/// dictionary, because only a BaseScript has one.
		/// </summary>
		internal static void Register(EventHandlerDictionary events)
		{
			events[RegisterEvent] += new Action<string, string, string, string>(OnRegisterMod);
		}

		private static void OnRegisterMod(string id, string name, string description, string buttonString)
		{
			if (string.IsNullOrEmpty(id))
			{
				Log.Info("A resource called " + RegisterEvent + " without an id; ignored.");
				return;
			}

			// Resources restart, and a restarted one registers again. Replacing the entry rather than adding a
			// second is what keeps the menu from filling up with the same row over and over.
			var existing = Mods.Find(m => m.Id == id);
			if (existing != null)
			{
				existing.Name = name;
				existing.Description = description;
				existing.ButtonString = buttonString;
				RedrawModMenu();
				return;
			}

			Mods.Add(new Mod
			{
				Id = id,
				Name = string.IsNullOrEmpty(name) ? id : name,
				Description = description ?? "",
				ButtonString = string.IsNullOrEmpty(buttonString) ? (string.IsNullOrEmpty(name) ? id : name) : buttonString,
			});

			RedrawModMenu();
			Log.Info("Resource '{0}' registered with Map Editor.", id);
		}

		internal static void InitMenu()
		{
			ModMenu = new NativeMenu("Map Editor", "~b~" + Translation.Translate("EXTERNAL MODS"));
			ModMenu.Buttons.Visible = false;
			ModMenu.ItemActivated += (menu, e) =>
			{
				var index = ModMenu.Items.IndexOf(e.Item);
				if (index < 0 || index >= Mods.Count) return;

				var picked = Mods[index];
				if (_current == picked)
				{
					Compat.Notify("~b~~h~Map Editor~h~~n~~w~Mod ~h~" + picked.Name + "~h~ " + Translation.Translate("has been disconnected."));
					Disconnect();
				}
				else
				{
					Compat.Notify("~b~~h~Map Editor~h~~n~~w~Mod ~h~" + picked.Name + "~h~ " + Translation.Translate("has been connected."));
					_current = picked;
					BaseScript.TriggerEvent(SelectedEvent, picked.Id);
				}
			};

			RedrawModMenu();
		}

		private static void RedrawModMenu()
		{
			// A resource can register at any time, including while the menu is on screen.
			if (ModMenu == null) return;

			ModMenu.Clear();
			foreach (var mod in Mods)
				ModMenu.Add(new NativeItem(mod.ButtonString, mod.Description));

			if (ModMenu.Items.Count > 0)
				ModMenu.SelectedIndex = 0;
		}

		/// <summary>Lets go of whichever resource the player had picked, and tells it so.</summary>
		internal static void Disconnect()
		{
			if (_current == null) return;

			var id = _current.Id;
			_current = null;
			BaseScript.TriggerEvent(DisconnectedEvent, id);
		}

		/// <summary>
		/// Hands the map to the resource the player picked. Only fires when one is picked: this is the whole
		/// point of picking one, and a map saved normally is nobody else's business.
		/// </summary>
		internal static void NotifyMapSaved(Map map, string name)
		{
			if (_current == null || map == null) return;

			BaseScript.TriggerEvent(MapSavedEvent, _current.Id, name ?? "",
				new MapSerializer().SerializeToString(map, MapSerializer.Format.Json));
		}

		/// <summary>
		/// Announces a map the editor has just put into the world, for whatever is scripting it.
		///
		/// This is the replacement for the SP build's JavascriptHook, which ran a .js file sitting next to the
		/// map and handed it the entity handles of every named object. A FiveM resource does the same job
		/// properly: it hears this, reads the objects it cares about out of the document by their name, and
		/// drives them itself. Fired for everyone listening, not only for a picked resource — a map's own
		/// logic is not something the player has to opt into.
		/// </summary>
		internal static void NotifyMapLoaded(Map map, string name)
		{
			if (map == null) return;

			BaseScript.TriggerEvent(MapLoadedEvent, name ?? "",
				new MapSerializer().SerializeToString(map, MapSerializer.Format.Json));
		}
	}
}
