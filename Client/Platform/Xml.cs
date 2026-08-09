using System.Globalization;
using System.Xml.Linq;

namespace MapEditor.Platform
{
    /// <summary>
    /// What XmlSerializer used to do, minus XmlSerializer.
    ///
    /// The SP build let it read and write every XML document the editor touched. It builds its readers and
    /// writers with Reflection.Emit, which FiveM's Mono sandbox is a poor host for and which would fail on
    /// somebody else's server rather than here, so the port walks the documents by hand instead — the map
    /// format in <see cref="MapSerializer"/>, Menyoo's in <see cref="MenyooCompatibility"/>. These are the
    /// parts both of them need.
    ///
    /// Reading only: no XML is written any more, so the half of this that produced XmlSerializer's exact
    /// markup went with the single-player exports. <c>xsi:nil</c> still has to be understood, because
    /// documents already written carry it.
    ///
    /// Readers are deliberately forgiving: a missing or unreadable element is the fallback, never an
    /// exception. A map written by an older build of the editor, or by hand, still loads — which is what
    /// XmlSerializer did too, and what the "0 means this predates the setting" fixups elsewhere are for.
    /// </summary>
    public static class Xml
    {
        /// <summary>
        /// The namespace XmlSerializer nils with. Documents the SP build wrote carry an empty
        /// <c>&lt;Weapon xsi:nil="true"/&gt;</c> where a nullable was null, and reading one as an empty
        /// string rather than as null is the difference between a ped with no weapon and a ped the reader
        /// throws over.
        /// </summary>
        public static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";

        public static bool IsNil(XElement element)
        {
            var nil = element.Attribute(Xsi + "nil");
            return nil != null && nil.Value == "true";
        }

        // --- Reading values -------------------------------------------------------------------------

        /// <summary>The child's text, or null where it is missing or nilled.</summary>
        public static string Text(XElement parent, string name)
        {
            var child = parent.Element(name);
            return child == null || IsNil(child) ? null : child.Value;
        }

        public static float Float(XElement parent, string name, float fallback)
        {
            var text = Text(parent, name);
            float value;
            return text != null &&
                   float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                ? value
                : fallback;
        }

        public static int Int(XElement parent, string name, int fallback)
        {
            var text = Text(parent, name);
            int value;
            return text != null &&
                   int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
                ? value
                : fallback;
        }

        /// <summary>
        /// Accepts "1" and "0" as well as the "true" and "false" XmlSerializer writes: XML says both are
        /// booleans, and Menyoo's files use both.
        /// </summary>
        public static bool Bool(XElement parent, string name, bool fallback)
        {
            var text = Text(parent, name);
            if (text == null) return fallback;

            text = text.Trim();
            if (text == "true" || text == "1") return true;
            if (text == "false" || text == "0") return false;
            return fallback;
        }
    }
}
