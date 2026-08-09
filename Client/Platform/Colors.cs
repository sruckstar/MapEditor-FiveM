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
    }
}
