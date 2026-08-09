using System;
using MapEditor.Platform;

namespace MapEditor
{
	/// <summary>
	/// The SP build kept these in scripts\MapEditor.xml through XmlSerializer. Here they are one JSON
	/// document in the player's KVP.
	///
	/// Two settings did not survive the port:
	/// * ActivationKey — the editor is opened by a command bound with RegisterKeyMapping, so the key is
	///   rebound in GTA's own settings menu rather than here. See <see cref="MapEditorClient"/>.
	/// * LoadScripts — the ClearScript/V8 host that ran a map's companion .js is gone; FiveM's Mono
	///   sandbox will not load it. Map logic belongs in its own resource, driven by the editor's events.
	///
	/// Reading is member-by-member against a defaulted instance, so a document written by an older
	/// version simply leaves the newer settings at their defaults — which is what the SP build needed a
	/// list of "0 means this predates the setting" fixups to achieve.
	/// </summary>
	public class Settings
	{
	    private const string StorageKey = "mapeditor:settings";

	    public Settings()
	    {
	        CameraSensivity = 30;
	        GamepadCameraSensitivity = 5;
	        KeyboardMovementSensitivity = 30;
	        GamepadMovementSensitivity = 15;
	        Gamepad = true;
	        InstructionalButtons = true;
	        // Fully qualified through the global namespace: the MapEditor class is also named MapEditor, and
	        // inside this namespace the class wins over the namespace when a name has to be resolved.
	        CrosshairType = global::MapEditor.CrosshairType.Crosshair;
	        PropCounterDisplay = true;
	        SnapCameraToSelectedObject = true;
	        AutosaveInterval = 5;
	        DrawDistance = -1;
	        Translation = "Auto";
	        OmitInvalidObjects = true;
	        WorldObjectNames = false;
	        BoundingBox = true;
	        StreamingRange = SmartStreaming.DefaultRange;
	    }

	    public string Translation;
		public bool Gamepad;
		public global::MapEditor.CrosshairType CrosshairType;
		public int CameraSensivity;
	    public int GamepadCameraSensitivity;
	    public int KeyboardMovementSensitivity;
	    public int GamepadMovementSensitivity;
		public bool InstructionalButtons;
		public bool PropCounterDisplay;
		public bool SnapCameraToSelectedObject;
	    public int AutosaveInterval;
	    public int DrawDistance;
	    public bool? BoundingBox;
	    public bool OmitInvalidObjects;

	    /// <summary>Names the game's own objects where they stand, so they can be copied or starred by sight.</summary>
	    public bool WorldObjectNames;

	    /// <summary>
	    /// How far from the player a placed entity may be before it is taken out of the world until they come
	    /// back to it, in metres, or <see cref="SmartStreaming.Off"/> to keep every map in the world whole.
	    /// </summary>
	    public int StreamingRange;

	    // No NetworkedEntities setting: what the editor spawns is always local, and a published map is
	    // shared as a document each client spawns for itself. See PropStreamer.Local.

	    public static Settings Load()
	    {
	        var settings = new Settings();

	        var document = Json.TryParse(Kvp.GetString(StorageKey));
	        if (document == null || document.Kind != JsonKind.Object)
	        {
	            settings.Save();
	            return settings;
	        }

	        settings.Translation = document["Translation"].AsString(settings.Translation);
	        settings.Gamepad = document["Gamepad"].AsBool(settings.Gamepad);
	        settings.CameraSensivity = document["CameraSensivity"].AsInt(settings.CameraSensivity);
	        settings.GamepadCameraSensitivity = document["GamepadCameraSensitivity"].AsInt(settings.GamepadCameraSensitivity);
	        settings.KeyboardMovementSensitivity = document["KeyboardMovementSensitivity"].AsInt(settings.KeyboardMovementSensitivity);
	        settings.GamepadMovementSensitivity = document["GamepadMovementSensitivity"].AsInt(settings.GamepadMovementSensitivity);
	        settings.InstructionalButtons = document["InstructionalButtons"].AsBool(settings.InstructionalButtons);
	        settings.PropCounterDisplay = document["PropCounterDisplay"].AsBool(settings.PropCounterDisplay);
	        settings.SnapCameraToSelectedObject = document["SnapCameraToSelectedObject"].AsBool(settings.SnapCameraToSelectedObject);
	        settings.AutosaveInterval = document["AutosaveInterval"].AsInt(settings.AutosaveInterval);
	        settings.DrawDistance = document["DrawDistance"].AsInt(settings.DrawDistance);
	        settings.OmitInvalidObjects = document["OmitInvalidObjects"].AsBool(settings.OmitInvalidObjects);
	        settings.WorldObjectNames = document["WorldObjectNames"].AsBool(settings.WorldObjectNames);
	        settings.StreamingRange = document["StreamingRange"].AsInt(settings.StreamingRange);

	        if (document.Has("BoundingBox"))
	            settings.BoundingBox = document["BoundingBox"].AsBool(true);

	        CrosshairType crosshair;
	        if (Enum.TryParse(document["CrosshairType"].AsString(""), out crosshair))
	            settings.CrosshairType = crosshair;

	        return settings;
	    }

	    public void Save()
	    {
	        var document = Json.Object()
	            .Set("Translation", Translation)
	            .Set("Gamepad", Gamepad)
	            .Set("CrosshairType", CrosshairType.ToString())
	            .Set("CameraSensivity", CameraSensivity)
	            .Set("GamepadCameraSensitivity", GamepadCameraSensitivity)
	            .Set("KeyboardMovementSensitivity", KeyboardMovementSensitivity)
	            .Set("GamepadMovementSensitivity", GamepadMovementSensitivity)
	            .Set("InstructionalButtons", InstructionalButtons)
	            .Set("PropCounterDisplay", PropCounterDisplay)
	            .Set("SnapCameraToSelectedObject", SnapCameraToSelectedObject)
	            .Set("AutosaveInterval", AutosaveInterval)
	            .Set("DrawDistance", DrawDistance)
	            .Set("OmitInvalidObjects", OmitInvalidObjects)
	            .Set("WorldObjectNames", WorldObjectNames)
	            .Set("StreamingRange", StreamingRange)
	            .SetIf(BoundingBox.HasValue, "BoundingBox", Json.Of(BoundingBox ?? true));

	        Kvp.SetString(StorageKey, document.ToJson());
	    }
	}
}
