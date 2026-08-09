using CitizenFX.Core;

// CitizenFX spells SHVDN 3's PedComponentType as PedComponents, which is also the name of the class below,
// and the nearer name wins inside this namespace. The alias restores the SHVDN 3 name so the slot code reads
// the same in both ports.
using PedComponentType = CitizenFX.Core.PedComponents;

namespace MapEditor
{
    /// <summary>
    /// A ped's twelve clothing slots. The game numbers them 0-11 and CitizenFX's PedComponents names them
    /// in that same order, so a slot's component id is also its index into the arrays a map is saved with.
    ///
    /// Lives apart from the menu that edits it (MapEditor.PedComponents.cs) because <see cref="PropStreamer"/>
    /// reads a ped's outfit whenever the map is saved, and that must not drag the whole editor in with it —
    /// the same reason <see cref="CrosshairType"/> was moved out.
    /// </summary>
    public static class PedComponents
    {
        public const int SlotCount = 12;

        /// <summary>The names the slots go by in the game's own menus, rather than the enum's names.</summary>
        public static string Label(PedComponentType type)
        {
            switch (type)
            {
                case PedComponentType.Face: return "Face";
                case PedComponentType.Head: return "Mask";
                case PedComponentType.Hair: return "Hair";
                case PedComponentType.Torso: return "Arms";
                case PedComponentType.Legs: return "Legs";
                case PedComponentType.Hands: return "Bag";
                case PedComponentType.Shoes: return "Shoes";
                case PedComponentType.Special1: return "Accessory";
                case PedComponentType.Special2: return "Undershirt";
                case PedComponentType.Special3: return "Body Armor";
                case PedComponentType.Textures: return "Decal";
                default: return "Top";
            }
        }

        public static int[] ReadDrawables(Ped ped)
        {
            var drawables = new int[SlotCount];
            for (int i = 0; i < SlotCount; i++)
                drawables[i] = ped.Style[(PedComponentType)i].Index;
            return drawables;
        }

        public static int[] ReadTextures(Ped ped)
        {
            var textures = new int[SlotCount];
            for (int i = 0; i < SlotCount; i++)
                textures[i] = ped.Style[(PedComponentType)i].TextureIndex;
            return textures;
        }

        /// <summary>
        /// Puts a saved outfit back on. A model whose variations have changed since the map was written
        /// would be asked for a drawable it no longer has, which SetVariation refuses rather than breaks on.
        /// </summary>
        public static void Apply(Ped ped, int[] drawables, int[] textures)
        {
            if (ped == null || !ped.Exists() || drawables == null) return;

            for (int i = 0; i < SlotCount && i < drawables.Length; i++)
            {
                int texture = textures != null && i < textures.Length ? textures[i] : 0;
                ped.Style[(PedComponentType)i].SetVariation(drawables[i], texture);
            }
        }
    }
}
