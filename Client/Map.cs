using System;
using System.Collections.Generic;
using CitizenFX.Core;

namespace MapEditor
{
	public class Map
	{
        public List<MapObject> Objects = new List<MapObject>();
        public List<MapObject> RemoveFromWorld = new List<MapObject>();
        public List<Marker> Markers = new List<Marker>();
	    public MapMetadata Metadata;
	}

    public class MapMetadata
    {
        public MapMetadata()
        {
            Creator = Game.Player.Name;
            Description = "";
        }

        public string Creator { get; set; }

        /// <summary>
        /// What the map is called, and — the same string — the entry it is stored under. There used to be a
        /// second field for the latter, and the two could only ever disagree: loading set the storage key
        /// from the entry while leaving the title as the document wrote it, so a map could sit in the menu
        /// calling itself one thing and save itself over another.
        ///
        /// Deliberately null on a new map rather than "Nameless Map". A default name is one nobody chose,
        /// and a map saved under it is a map the player has to find again among every other map saved under
        /// it; the row asks for a name and the save row stays out of reach until it has one.
        /// </summary>
        public string Name { get; set; }

        public string Description { get; set; }

        /// <summary>
        /// A field of the document and nothing more. It used to mean "spawn this map on startup", and the
        /// editor no longer decides anything by it: whether a map is standing is the server's business now
        /// and lives in its catalogue, not in the document (see <see cref="MapEntry.Live"/>). It is still
        /// read and written so that a document going out and coming back is the document that went out, and
        /// so that the single-player editor's XML still round-trips.
        /// </summary>
        public bool Autoload { get; set; }

        public Vector3? LoadingPoint { get; set; }
        public Vector3? TeleportPoint { get; set; }
    }
}