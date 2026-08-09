// Taken from LemonUI 2.2 by Hannele "Lemon" Ruiz, MIT licensed. See Client/Ui/README.md for what was changed and why.
using CitizenFX.Core;
using CitizenFX.Core.Native;
using System.Threading.Tasks;
using System;

namespace MapEditor.Ui.Scaleform
{
    /// <summary>
    /// Represents a generic Scaleform object.
    /// </summary>
    public abstract class BaseScaleform : IScaleform
    {
        // LemonUI keeps an obsolete `scaleform` field here for the sake of anything still compiled against
        // its old API. Dropped: nothing outside this repository compiles against this, and its initialiser
        // asked the game for a scaleform movie named "" on every instructional-buttons set the editor makes.

        #region Properties

        /// <summary>
        /// The ID or Handle of the Scaleform.
        /// </summary>
        public int Handle { get; private set; }
        /// <summary>
        /// The Name of the Scaleform.
        /// </summary>
        public string Name { get; }
        /// <summary>
        /// If the Scaleform should be visible or not.
        /// </summary>
        public virtual bool Visible { get; set; }
        /// <summary>
        /// If the Scaleform is loaded or not.
        /// </summary>
        public bool IsLoaded
        {
            get
            {
                return API.HasScaleformMovieLoaded(Handle);
            }
        }

        #endregion

        #region Constructors

        /// <summary>
        /// Creates a new Scaleform class with the specified Scaleform object name.
        /// </summary>
        /// <param name="sc">The Scalform object.</param>
        public BaseScaleform(string sc)
        {
            Name = sc ?? throw new ArgumentNullException(nameof(sc));

            Handle = API.RequestScaleformMovie(Name);
        }

        #endregion

        #region Tools

        private static void AddInteger(int number)
        {
            API.ScaleformMovieMethodAddParamInt(number);
        }
        private static void AddFloat(float number)
        {
            API.ScaleformMovieMethodAddParamFloat(number);
        }
        private void CallFunctionBase(string function, params object[] parameters)
        {
            if (function == null)
            {
                throw new ArgumentNullException(nameof(function), "The function name is null.");
            }

            if (string.IsNullOrWhiteSpace(function))
            {
                throw new ArgumentOutOfRangeException(nameof(function), "The function name is empty or white space.");
            }

            API.BeginScaleformMovieMethod(Handle, function);

            foreach (object obj in parameters)
            {
                if (obj == null)
                {
                    throw new ArgumentNullException(nameof(parameters), "Unexpected null function argument in parameters.");
                }

                // Short and Byte can be converted to integer
                // Double can be converted to float (while loosing precision)
                // I'm not sure about long when it comes to the RAGE Engine

                if (obj is int objInt)
                {
                    AddInteger(objInt);
                }
                else if (obj is short objShort)
                {
                    AddInteger(objShort);
                }
                else if (obj is byte objByte)
                {
                    AddInteger(objByte);
                }
                else if (obj is float objFloat)
                {
                    AddFloat(objFloat);
                }
                else if (obj is double objDouble)
                {
                    AddFloat((float)objDouble);
                }
                else if (obj is string objString)
                {
                    API.BeginTextCommandScaleformString("STRING");
                    API.AddTextComponentSubstringPlayerName(objString);
                    API.EndTextCommandScaleformString();
                }
                else if (obj is bool objBool)
                {
                    API.ScaleformMovieMethodAddParamBool(objBool);
                }
                else
                {
                    throw new ArgumentException($"Unexpected argument type {obj.GetType().Name}.", nameof(parameters));
                }
            }
        }

        #endregion

        #region Functions

        /// <summary>
        /// Checks if the specified Scaleform Return Value is ready to be fetched.
        /// </summary>
        /// <param name="id">The Identifier of the Value.</param>
        /// <returns><see langword="true"/> if the value is ready, <see langword="false"/> otherwise.</returns>
        public bool IsValueReady(int id)
        {
            return API.IsScaleformMovieMethodReturnValueReady(id);
        }
        /// <summary>
        /// Gets a specific value.
        /// </summary>
        /// <typeparam name="T">The type of value.</typeparam>
        /// <param name="id">The Identifier of the value.</param>
        /// <returns>The value returned by the native.</returns>
        public T GetValue<T>(int id)
        {
            if (typeof(T) == typeof(string))
            {
                return (T)(object)API.GetScaleformMovieMethodReturnValueString(id);
            }
            else if (typeof(T) == typeof(int))
            {
                return (T)(object)API.GetScaleformMovieMethodReturnValueInt(id);
            }
            else if (typeof(T) == typeof(bool))
            {
                return (T)(object)API.GetScaleformMovieMethodReturnValueBool(id);
            }
            else
            {
                throw new InvalidOperationException($"Expected string, int or bool, got {typeof(T).Name}.");
            }
        }
        /// <summary>
        /// Calls a Scaleform function.
        /// </summary>
        /// <param name="function">The name of the function to call.</param>
        /// <param name="parameters">The parameters to pass.</param>
        public void CallFunction(string function, params object[] parameters)
        {
            CallFunctionBase(function, parameters);
            API.EndScaleformMovieMethod();
        }
        /// <summary>
        /// Calls a scaleform function and gets it's return value as soon as is available. 
        /// </summary>
        /// <param name="function">The function to call.</param>
        /// <param name="parameters">The parameters to call the function with.</param>
        /// <typeparam name="T">The type of return parameter.</typeparam>
        /// <returns>The value returned by the function.</returns>
        public async Task<T> CallFunction<T>(string function, params object[] parameters)
        {
            int id = CallFunctionReturn(function, parameters);

            while (!IsValueReady(id))
            {
                await BaseScript.Delay(0);
            }

            return GetValue<T>(id);
        }
        /// <summary>
        /// Calls a Scaleform function with a return value.
        /// </summary>
        /// <param name="function">The name of the function to call.</param>
        /// <param name="parameters">The parameters to pass.</param>
        public int CallFunctionReturn(string function, params object[] parameters)
        {
            CallFunctionBase(function, parameters);
            return API.EndScaleformMovieMethodReturnValue();
        }
        /// <summary>
        /// Updates the parameters of the Scaleform.
        /// </summary>
        public abstract void Update();
        /// <summary>
        /// Draws the scaleform full screen.
        /// </summary>
        public virtual void DrawFullScreen()
        {
            if (!Visible)
            {
                return;
            }
            Update();
            API.DrawScaleformMovieFullscreen(Handle, 255, 255, 255, 255, 0);
        }
        /// <summary>
        /// Draws the scaleform full screen.
        /// </summary>
        public virtual void Draw() => DrawFullScreen();
        /// <summary>
        /// Draws the scaleform full screen.
        /// </summary>
        public virtual void Process() => DrawFullScreen();
        /// <summary>
        /// Marks the scaleform as no longer needed.
        /// </summary>
        public void Dispose()
        {
            int id = Handle;
            API.SetScaleformMovieAsNoLongerNeeded(ref id);
        }

        #endregion
    }
}
