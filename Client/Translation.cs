using System.Collections.Generic;
using System.Linq;
using CitizenFX.Core;
using MapEditor.Platform;

namespace MapEditor
{
    /// <summary>
    /// UI strings, read out of the resource.
    ///
    /// Two things changed from the SP build. It scanned scripts\MapEditor\*.xml for translation files, and
    /// a FiveM client cannot enumerate a directory — so every translation lives in one shipped
    /// data/translations.json instead. And it appended any string it was asked to translate but did not
    /// know back to the file, which made the shipped files self-populating for translators; a client
    /// cannot write resource files at all, so an unknown string is simply passed through.
    ///
    /// To add a language, add an entry to data/translations.json and list the file in fxmanifest's files{}.
    /// </summary>
    public static class Translation
    {
        private const string TranslationsFile = "data/translations.json";

        public static List<TranslationRoot> Translations = new List<TranslationRoot>();
        public static string CurrentTranslation { get; set; }

        private static TranslationRoot _currenTranslationFile;

        public static void Load(string translation)
        {
            Translations = new List<TranslationRoot>();

            // Absent by default — the SP build shipped no translations either, and English is the fallback.
            var document = Json.TryParse(ResourceFiles.ReadOptionalText(TranslationsFile));
            if (document != null)
            {
                foreach (var entry in document.Items)
                {
                    var root = new TranslationRoot
                    {
                        Language = entry["language"].AsString(""),
                        Translator = entry["translator"].AsString(""),
                        Strings = new Dictionary<string, string>(),
                    };
                    foreach (var pair in entry["strings"].Members)
                        root.Strings[pair.Key] = pair.Value.AsString(pair.Key);
                    Translations.Add(root);
                }
            }

            SetLanguage(translation);
        }

        public static void SetLanguage(string newLanguage)
        {
            // "Auto" means the language the game is running in; the lookup below then either finds a
            // translation for it or leaves the UI in English.
            CurrentTranslation = newLanguage == "Auto" ? Game.Language.ToString() : newLanguage;
            _currenTranslationFile = Translations.FirstOrDefault(t => t.Language == CurrentTranslation);
        }

        public static string Translate(string original)
        {
            if (_currenTranslationFile == null) return original;

            string translated;
            if (!_currenTranslationFile.Strings.TryGetValue(original, out translated)) return original;
            return translated.Replace("~n~", "\n");
        }
    }

    public class TranslationRoot
    {
        public string Language { get; set; }
        public string Translator { get; set; }

        public Dictionary<string, string> Strings { get; set; }
    }
}
