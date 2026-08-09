using System.Drawing;

namespace MapEditor.Platform
{
    /// <summary>
    /// The named colours <see cref="Color"/> does not have here.
    ///
    /// FiveM's Mono v1 runtime ships a 3.5 KB System.Drawing.dll that is nothing but a type-forwarder into
    /// CitizenFX.Core, and the <c>Color</c> that lands there is a cut-down struct: <c>FromArgb</c>, the four
    /// component properties and <c>Transparent</c> are all of it. Every other named colour — White, Yellow,
    /// Red, the whole KnownColor table — is gone.
    ///
    /// Reference assemblies still have them, so the calls compile; Mono then fails to resolve the memberref
    /// when it verifies the *method* that contains one, which takes out the entire method rather than just
    /// the colour. A <c>Color.White</c> anywhere in the tick handler is enough to throw
    /// BadImageFormatException on every frame and leave the editor doing nothing at all.
    ///
    /// So: never write <c>Color.SomeName</c> in this port. Add it here instead.
    /// </summary>
    public static class Colors
    {
        public static Color White
        {
            get { return Color.FromArgb(255, 255, 255, 255); }
        }

        public static Color Yellow
        {
            get { return Color.FromArgb(255, 255, 255, 0); }
        }

        /// <summary>
        /// The colours a co-editing session hands out, one per participant.
        ///
        /// Chosen to stay apart from the three the editor already uses to mean something — yellow for the
        /// object under the crosshair, green for a multi-selection, red for the rotation mode — so that a
        /// name tag is never the same colour as a state. Eight, because a session holds eight by default;
        /// past that they repeat, and the name written beside every one of them is what actually
        /// distinguishes them.
        /// </summary>
        private static readonly Color[] PeerPalette =
        {
            Color.FromArgb(255, 90, 170, 255),   // blue
            Color.FromArgb(255, 255, 140, 60),   // orange
            Color.FromArgb(255, 190, 120, 255),  // violet
            Color.FromArgb(255, 80, 220, 190),   // teal
            Color.FromArgb(255, 255, 110, 170),  // pink
            Color.FromArgb(255, 180, 220, 70),   // lime
            Color.FromArgb(255, 240, 200, 90),   // sand
            Color.FromArgb(255, 140, 160, 255),  // periwinkle
        };

        /// <summary>The colour belonging to one participant's slot. See <see cref="PeerPalette"/>.</summary>
        public static Color Peer(int slot)
        {
            if (slot < 0) slot = 0;
            return PeerPalette[slot % PeerPalette.Length];
        }

        /// <summary>The same colour at a lower alpha, for the box drawn round something they are holding.</summary>
        public static Color PeerFaded(int slot, int alpha)
        {
            var color = Peer(slot);
            return Color.FromArgb(alpha, color.R, color.G, color.B);
        }
    }
}
