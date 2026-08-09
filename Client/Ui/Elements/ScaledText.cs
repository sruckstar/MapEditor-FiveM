// Taken from LemonUI 2.2 by Hannele "Lemon" Ruiz, MIT licensed. See Client/Ui/README.md for what was changed and why.
using CitizenFX.Core.Native;
using CitizenFX.Core.UI;
using Font = CitizenFX.Core.UI.Font;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using MapEditor.Ui.Tools;

namespace MapEditor.Ui.Elements
{
    /// <summary>
    /// A text string.
    /// </summary>
    public class ScaledText : IText
    {
        #region Constants

        /// <summary>
        /// The size of every chunk of text.
        /// </summary>
        private const int chunkSize = 90;

        #endregion

        #region Fields

        /// <summary>
        /// The scaled 1080p based screen position.
        /// </summary>
        private PointF scaledPosition = PointF.Empty;
        /// <summary>
        /// The relative 0-1 relative position.
        /// </summary>
        private PointF relativePosition = PointF.Empty;
        /// <summary>
        /// The raw string of text.
        /// </summary>
        private string text = string.Empty;
        /// <summary>
        /// The raw string split into equally sized strings.
        /// </summary>
        private List<string> chunks = new List<string>();
        /// <summary>
        /// The alignment of the item.
        /// </summary>
        private Alignment alignment = Alignment.Left;
        /// <summary>
        /// The word wrap value passed by the user.
        /// </summary>
        private float internalWrap = 0f;
        /// <summary>
        /// The real word wrap value based on the position of the text.
        /// </summary>
        private float realWrap = 0f;

        #endregion

        #region Properties

        /// <summary>
        /// The position of the text.
        /// </summary>
        public PointF Position
        {
            get => scaledPosition;
            set
            {
                scaledPosition = value;
                relativePosition = value.ToRelative();
            }
        }
        /// <summary>
        /// The text to draw.
        /// </summary>
        public string Text
        {
            get => text;
            set
            {
                text = value ?? throw new ArgumentNullException(nameof(value));
                Slice();
            }
        }
        /// <summary>
        /// The color of the text.
        /// </summary>
        public Color Color { get; set; } = Color.FromArgb(255, 255, 255, 255);
        /// <summary>
        /// The game font to use.
        /// </summary>
        public Font Font { get; set; } = Font.ChaletLondon;
        /// <summary>
        /// The scale of the text.
        /// </summary>
        public float Scale { get; set; } = 1f;
        /// <summary>
        /// If the text should have a drop down shadow.
        /// </summary>
        public bool Shadow { get; set; } = false;
        /// <summary>
        /// If the test should have an outline.
        /// </summary>
        public bool Outline { get; set; } = false;
        /// <summary>
        /// The alignment of the text.
        /// </summary>
        public Alignment Alignment
        {
            get => alignment;
            set
            {
                alignment = value;
                Recalculate();
            }
        }
        /// <summary>
        /// The distance from the start position where the text will be wrapped into new lines.
        /// </summary>
        public float WordWrap
        {
            get
            {
                return internalWrap;
            }
            set
            {
                internalWrap = value;
                Recalculate();
            }
        }
        /// <summary>
        /// The width that the text takes from the screen.
        /// </summary>
        public float Width
        {
            get
            {
                API.BeginTextCommandWidth("CELL_EMAIL_BCON");
                Add();
                return API.EndTextCommandGetWidth(true) * 1f.ToXScaled();
            }
        }
        /// <summary>
        /// The number of lines used by this text.
        /// </summary>
        public int LineCount
        {
            get
            {
                API.BeginTextCommandLineCount("CELL_EMAIL_BCON");
                Add();
                return API.EndTextCommandGetLineCount(relativePosition.X, relativePosition.Y);
            }
        }
        /// <summary>
        /// The relative height of each line in the text.
        /// </summary>
        public float LineHeight
        {
            get
            {
                // Height will always be 1080
                return 1080 * API.GetTextScaleHeight(Scale, (int)Font);
            }
        }

        #endregion

        #region Constructors

        /// <summary>
        /// Creates a text with the specified options.
        /// </summary>
        /// <param name="pos">The position where the text should be located.</param>
        /// <param name="text">The text to show.</param>
        public ScaledText(PointF pos, string text) : this(pos, text, 1f, Font.ChaletLondon)
        {
        }
        /// <summary>
        /// Creates a text with the specified options.
        /// </summary>
        /// <param name="pos">The position where the text should be located.</param>
        /// <param name="text">The text to show.</param>
        /// <param name="scale">The scale of the text.</param>
        public ScaledText(PointF pos, string text, float scale) : this(pos, text, scale, Font.ChaletLondon)
        {
        }
        /// <summary>
        /// Creates a text with the specified options
        /// </summary>
        /// <param name="pos">The position where the text should be located.</param>
        /// <param name="text">The text to show.</param>
        /// <param name="scale">The scale of the text.</param>
        /// <param name="font">The font to use.</param>
        public ScaledText(PointF pos, string text, float scale, Font font)
        {
            Position = pos;
            Text = text ?? throw new ArgumentNullException(nameof(text));
            Scale = scale;
            Font = font;
        }

        #endregion

        #region Tools

        /// <summary>
        /// Adds the text information for measurement.
        /// </summary>
        private void Add()
        {
            if (Scale == 0)
            {
                return;
            }
            foreach (string chunk in chunks)
            {
                API.AddTextComponentString(chunk);
            }
            API.SetTextFont((int)Font);
            API.SetTextScale(1f, Scale);
            API.SetTextColour(Color.R, Color.G, Color.B, Color.A);
            API.SetTextJustification((int)Alignment);
            if (Shadow)
            {
                API.SetTextDropShadow();
            }
            if (Outline)
            {
                API.SetTextOutline();
            }
            if (WordWrap > 0)
            {
                switch (Alignment)
                {
                    case Alignment.Center:
                        API.SetTextWrap(relativePosition.X - (realWrap * 0.5f), relativePosition.X + (realWrap * 0.5f));
                        break;
                    case Alignment.Left:
                        API.SetTextWrap(relativePosition.X, relativePosition.X + realWrap);
                        break;
                    case Alignment.Right:
                        API.SetTextWrap(relativePosition.X - realWrap, relativePosition.X);
                        break;
                }
            }
            else if (Alignment == Alignment.Right)
            {
                API.SetTextWrap(0f, relativePosition.X);
            }
        }
        /// <summary>
        /// Slices the string of text into appropiately saved chunks.
        /// </summary>
        private void Slice()
        {
            // If the entire text is under 90 bytes, save it as is and return
            if (Encoding.UTF8.GetByteCount(text) <= chunkSize)
            {
                chunks.Clear();
                chunks.Add(text);
                return;
            }

            // Create a new list of chunks and a temporary string
            List<string> newChunks = new List<string>();
            string temp = string.Empty;

            // Iterate over the characters in the string
            foreach (char character in text)
            {
                // Create a temporary string with the character
                string with = string.Concat(temp, character);
                // If this string is higher than 90 bytes, add the existing string onto the list
                if (Encoding.UTF8.GetByteCount(with) > chunkSize)
                {
                    newChunks.Add(temp);
                    temp = character.ToString();
                    continue;
                }
                // And save the new string generated
                temp = with;
            }

            // If after finishing we still have a piece, save it
            if (temp != string.Empty)
            {
                newChunks.Add(temp);
            }

            // Once we have finished, replace the old chunks
            chunks = newChunks;
        }
        /// <summary>
        /// Recalculates the size, position and word wrap of this item.
        /// </summary>
        public void Recalculate()
        {
            // Do the normal Size and Position recalculation
            relativePosition = scaledPosition.ToRelative();
            // And recalculate the word wrap if necessary
            if (internalWrap <= 0)
            {
                realWrap = 0;
            }
            else
            {
                realWrap = internalWrap.ToXRelative();
            }
        }

        #endregion

        #region Functions

        /// <summary>
        /// Draws the text on the screen.
        /// </summary>
        public void Draw()
        {
            API.SetTextEntry("CELL_EMAIL_BCON");

            Add();

            API.DrawText(relativePosition.X, relativePosition.Y);
        }

        #endregion
    }
}
