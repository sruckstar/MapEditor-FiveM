using System.Drawing;
using CitizenFX.Core;

namespace MapEditor
{
	public class Marker
	{
		public MarkerType Type;
		public Vector3 Position;
		public Vector3 Rotation;
		public Vector3 Scale;
	    public Vector3? TeleportTarget;
		public int Red;
		public int Green;
		public int Blue;
		public int Alpha;
		public bool BobUpAndDown;
		public bool RotateToCamera;
	    public bool OnlyVisibleInEditor;
		public int Id;

		/// <summary>
		/// What this marker is called inside a co-editing session, or 0 outside one. Runtime only: it is
		/// minted when the session first sees the marker and means nothing once the session ends, so it is
		/// never written to a map file. See <see cref="Collab"/>.
		/// </summary>
		public int Uid;
	}
}