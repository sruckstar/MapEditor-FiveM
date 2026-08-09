namespace MapEditor
{
	/// <summary>
	/// What is drawn at the middle of the screen while the freecam is up.
	///
	/// Nested inside the MapEditor class in the SP build. It is lifted out here so that Settings, which is
	/// the only other thing that names it, does not drag the whole editor in behind it. Callers spelling it
	/// "MapEditor.CrosshairType" still resolve, now through the namespace rather than the class.
	/// </summary>
	public enum CrosshairType
	{
		Crosshair,
		Orb,
		None,
	}
}
