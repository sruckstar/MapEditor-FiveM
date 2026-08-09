// Taken from LemonUI 2.2 by Hannele "Lemon" Ruiz, MIT licensed. See Client/Ui/README.md for what was changed and why.
using CitizenFX.Core.Native;
using System;
using System.Drawing;

namespace MapEditor.Ui.Elements
{
    /// <summary>
    /// A 2D game texture.
    /// </summary>
    public class ScaledTexture : BaseElement
    {
        #region Fields

        private string dictionary;
        private string texture;

        #endregion

        #region Properties

        /// <summary>
        /// The dictionary where the texture is loaded.
        /// </summary>
        public string Dictionary
        {
            get => dictionary;
            set => dictionary = value ?? throw new ArgumentNullException(nameof(value));
        }

        /// <summary>
        /// The texture to draw from the dictionary.
        /// </summary>
        public string Texture
        {
            get => texture;
            set => texture = value ?? throw new ArgumentNullException(nameof(value));
        }

        #endregion

        #region Constructors

        /// <summary>
        /// Creates a new <see cref="ScaledTexture"/> with a Position and Size of Zero.
        /// </summary>
        /// <param name="dictionary">The dictionary where the texture is located.</param>
        /// <param name="texture">The texture to draw.</param>
        public ScaledTexture(string dictionary, string texture) : this(PointF.Empty, SizeF.Empty, dictionary, texture)
        {
        }
        /// <summary>
        /// Creates a new <see cref="ScaledTexture"/> with a Position and Size of zero.
        /// </summary>
        /// <param name="pos">The position of the Texture.</param>
        /// <param name="size">The size of the Texture.</param>
        /// <param name="dictionary">The dictionary where the texture is located.</param>
        /// <param name="texture">The texture to draw.</param>
        public ScaledTexture(PointF pos, SizeF size, string dictionary, string texture) : base(pos, size)
        {
            Dictionary = dictionary ?? throw new ArgumentNullException(nameof(dictionary));
            Texture = texture ?? throw new ArgumentNullException(nameof(texture));
            Request();
        }

        #endregion

        #region Tools

        /// <summary>
        /// Requests the texture dictionary for this class.
        /// </summary>
        private void Request()
        {
            if (!API.HasStreamedTextureDictLoaded(Dictionary))
            {
                API.RequestStreamedTextureDict(Dictionary, true);
            }
        }

        #endregion

        #region Functions

        /// <summary>
        /// Draws the texture on the screen.
        /// </summary>
        public override void Draw()
        {
            if (Size == SizeF.Empty)
            {
                return;
            }
            Request();
            API.DrawSprite(Dictionary, Texture, relativePosition.X, relativePosition.Y, relativeSize.Width, relativeSize.Height, Heading, Color.R, Color.G, Color.B, Color.A);
        }
        /// <summary>
        /// Draws a specific part of the texture on the screen.
        /// </summary>
        /// <param name="topLeft">The top left corner of the area to draw.</param>
        /// <param name="bottomRight">The bottom right corner of the area to draw.</param>
        public virtual void DrawSpecific(PointF topLeft, PointF bottomRight)
        {
            if (Size == SizeF.Empty)
            {
                return;
            }
            Request();
            API.DrawSpriteUv(Dictionary, Texture, relativePosition.X, relativePosition.Y, relativeSize.Width, relativeSize.Height, topLeft.X, topLeft.Y, bottomRight.X, bottomRight.Y, Heading, Color.R, Color.G, Color.B, Color.A);
        }
        /// <summary>
        /// Recalculates the position based on the size.
        /// </summary>
        public override void Recalculate()
        {
            base.Recalculate();
            relativePosition.X += relativeSize.Width * 0.5f;
            relativePosition.Y += relativeSize.Height * 0.5f;
        }

        #endregion
    }
}
