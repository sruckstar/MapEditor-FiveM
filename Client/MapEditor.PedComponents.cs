using System;
using CitizenFX.Core;
using MapEditor.Ui.Menus;

// See Client/PedComponents.cs, where the slots themselves live: CitizenFX's enum and the editor's class
// share the name PedComponents, so the enum is reached through the SHVDN 3 name it goes by in the SP build.
using PedComponentType = CitizenFX.Core.PedComponents;

namespace MapEditor
{
    public partial class MapEditor
    {
        private NativeMenu _pedComponentsMenu;

        private void BuildPedComponentsMenu()
        {
            // Sits where the properties menu it hangs off does, so opening it does not move the list.
            _pedComponentsMenu = new NativeMenu("", "~b~" + Translation.Translate("PED COMPONENTS"))
            {
                Banner = null,
                NoItemsText = Translation.Translate("This ped has nothing to change."),
            };
            _pedComponentsMenu.Buttons.Visible = false;
            _menuPool.Add(_pedComponentsMenu);
        }

        /// <summary>
        /// The slots of one ped, as they stand right now. Which slots even show up depends on the model:
        /// most peds only have a handful worth offering.
        /// </summary>
        private void RedrawPedComponentsMenu(Ped ped)
        {
            _pedComponentsMenu.Clear();
            if (ped == null || !ped.Exists()) return;

            for (int i = 0; i < PedComponents.SlotCount; i++)
                AddPedComponentRows(ped, (PedComponentType)i);

            if (_pedComponentsMenu.Items.Count > 0)
                _pedComponentsMenu.SelectedIndex = 0;
        }

        /// <summary>
        /// The rows one slot is dressed with: the piece itself, and the texture it is painted in. A slot
        /// with a single piece and a single texture has nothing to offer, so it is left out.
        /// </summary>
        private void AddPedComponentRows(Ped ped, PedComponentType type)
        {
            var component = ped.Style[type];
            if (!component.HasAnyVariations) return;

            string label = Translation.Translate(PedComponents.Label(type));

            NativeDynamicItem<int> textureItem = null;

            if (component.HasTextureVariations || component.HasVariations)
            {
                textureItem = new NativeDynamicItem<int>(
                    string.Format("{0} {1}", label, Translation.Translate("texture")),
                    Translation.Translate("The texture the piece worn in this slot is painted in."),
                    component.TextureIndex);
                textureItem.ItemChanged += (sender, e) =>
                {
                    var slot = ped.Style[type];
                    int value = WrapIndex(e.Object + (e.Direction == Direction.Left ? -1 : 1), slot.TextureCount);
                    slot.SetVariation(slot.Index, value);
                    e.Object = slot.TextureIndex;
                    _changesMade++;
                };
            }

            if (component.HasVariations)
            {
                var drawableItem = new NativeDynamicItem<int>(label,
                    Translation.Translate("The piece the ped wears in this slot."), component.Index);
                drawableItem.ItemChanged += (sender, e) =>
                {
                    var slot = ped.Style[type];
                    int value = WrapIndex(e.Object + (e.Direction == Direction.Left ? -1 : 1), slot.Count);

                    // Pieces do not all come in the same number of textures, so the one being worn is only
                    // kept where the new piece has it.
                    int texture = slot.IsVariationValid(value, slot.TextureIndex) ? slot.TextureIndex : 0;
                    slot.SetVariation(value, texture);

                    e.Object = slot.Index;
                    if (textureItem != null)
                        textureItem.SelectedItem = slot.TextureIndex;
                    _changesMade++;
                };
                _pedComponentsMenu.Add(drawableItem);
            }

            if (textureItem != null)
                _pedComponentsMenu.Add(textureItem);
        }

        /// <summary>Steps through a slot's variations end to end, so scrolling never stalls on the last one.</summary>
        private static int WrapIndex(int value, int count)
        {
            if (count <= 0) return 0;
            if (value < 0) return count - 1;
            return value >= count ? 0 : value;
        }
    }
}
