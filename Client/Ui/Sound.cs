// Taken from LemonUI 2.2 by Hannele "Lemon" Ruiz, MIT licensed. See Client/Ui/README.md for what was changed and why.
using CitizenFX.Core;
using CitizenFX.Core.Native;
using System;

namespace MapEditor.Ui
{
    /// <summary>
    /// Contains information for a Game Sound that is played at specific times.
    /// </summary>
    public class Sound
    {
        #region Fields

        private string set = string.Empty;
        private string file = string.Empty;

        #endregion

        #region Properties

        /// <summary>
        /// The ID of the sound, if is being played.
        /// </summary>
        public int Id { get; private set; } = -1;
        /// <summary>
        /// The Set where the sound is located.
        /// </summary>
        public string Set
        {
            get => set;
            set => set = value ?? throw new ArgumentNullException(nameof(value));
        }
        /// <summary>
        /// The name of the sound file.
        /// </summary>
        public string File
        {
            get => file;
            set => file = value ?? throw new ArgumentNullException(nameof(value));
        }

        #endregion

        #region Constructors

        /// <summary>
        /// Creates a new <see cref="Sound"/> class with the specified Sound Set and File.
        /// </summary>
        /// <param name="set">The Set where the sound is located.</param>
        /// <param name="file">The name of the sound file.</param>
        public Sound(string set, string file)
        {
            Set = set ?? throw new ArgumentNullException(nameof(set));
            File = file ?? throw new ArgumentNullException(nameof(file));
        }

        #endregion

        #region Functions

        /// <summary>
        /// Plays the sound for the local <see cref="Player"/>.
        /// </summary>
        public void PlayFrontend() => PlayFrontend(true);
        /// <summary>
        /// Plays the sound for the local <see cref="Player"/>.
        /// </summary>
        /// <param name="release">If the sound ID should be automatically released.</param>
        public void PlayFrontend(bool release)
        {
            Id = API.GetSoundId();
            API.PlaySoundFrontend(Id, File, Set, true);

            if (release)
            {
                Release();
            }
        }
        /// <summary>
        /// Stops the audio from playing.
        /// </summary>
        public void Stop()
        {
            if (Id == -1)
            {
                return;
            }

            API.StopSound(Id);
            Release();
        }
        /// <summary>
        /// Releases the Sound ID.
        /// </summary>
        public void Release()
        {
            if (Id == -1)
            {
                return;
            }

            API.ReleaseSoundId(Id);
            Id = -1;
        }

        #endregion
    }
}
