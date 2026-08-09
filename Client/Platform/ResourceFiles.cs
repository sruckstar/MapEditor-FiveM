using System;
using System.Collections;
using System.Collections.Generic;
using CitizenFX.Core.Native;

namespace MapEditor.Platform
{
    /// <summary>
    /// Read-only access to the files shipped inside the resource: ObjectList.ini, the merged category
    /// list, translations and the crosshair images. Replaces the SP build's File.ReadAllLines against
    /// scripts\MapEditor\.
    ///
    /// Everything listed here must also appear in the resource's fxmanifest files{} block, or
    /// LoadResourceFile returns null at runtime.
    /// </summary>
    public static class ResourceFiles
    {
        private static string _resourceName;

        public static string ResourceName
        {
            get
            {
                if (_resourceName == null) _resourceName = API.GetCurrentResourceName();
                return _resourceName;
            }
        }

        /// <summary>The file's whole contents, or null when it is missing from files{} or from the resource.</summary>
        public static string ReadText(string path)
        {
            string content = API.LoadResourceFile(ResourceName, path);
            if (content == null)
                Log.Info("Resource file '{0}' could not be read. Is it listed in fxmanifest's files{{}}?", path);
            return content;
        }

        /// <summary>
        /// ReadText for files the editor ships without and runs fine without — translations.json above all.
        /// Same result, minus the warning that would otherwise fire on every clean install.
        /// </summary>
        public static string ReadOptionalText(string path)
        {
            return API.LoadResourceFile(ResourceName, path);
        }

        public static bool Exists(string path)
        {
            return API.LoadResourceFile(ResourceName, path) != null;
        }

        /// <summary>
        /// Splits <paramref name="content"/> into lines without allocating the whole string[] that
        /// String.Split would. ObjectList.ini is 880 KB / ~25,000 lines and is parsed a chunk at a time,
        /// so the caller wants to walk it lazily and yield the frame between chunks.
        ///
        /// Hand-rolled rather than a yield-return iterator on purpose: Roslyn's generated state machine
        /// stores Environment.CurrentManagedThreadId in its constructor, and FiveM's Mono sandbox denies
        /// that property — the whole method then fails verification with a MethodAccessException the
        /// moment it is first called. Any new iterator in this assembly hits the same wall.
        /// </summary>
        public static IEnumerable<string> Lines(string content)
        {
            if (string.IsNullOrEmpty(content)) return EmptyLines;
            return new LineEnumerable(content);
        }

        private static readonly string[] EmptyLines = new string[0];

        private sealed class LineEnumerable : IEnumerable<string>
        {
            private readonly string _content;

            public LineEnumerable(string content) { _content = content; }

            public IEnumerator<string> GetEnumerator() { return new LineEnumerator(_content); }
            IEnumerator IEnumerable.GetEnumerator() { return GetEnumerator(); }
        }

        private sealed class LineEnumerator : IEnumerator<string>
        {
            private readonly string _content;
            private int _next;
            private string _current;
            private bool _finished;

            public LineEnumerator(string content) { _content = content; }

            public string Current { get { return _current; } }
            object IEnumerator.Current { get { return _current; } }

            public bool MoveNext()
            {
                // A trailing line break leaves _next at the end: that is the end of the text, not a
                // final empty line, which is what File.ReadAllLines would have given the SP build too.
                if (_finished || _next >= _content.Length)
                {
                    _current = null;
                    return false;
                }

                int start = _next;
                for (int i = start; i < _content.Length; i++)
                {
                    char c = _content[i];
                    if (c != '\n' && c != '\r') continue;

                    _current = _content.Substring(start, i - start);
                    // Treat CRLF as one break.
                    if (c == '\r' && i + 1 < _content.Length && _content[i + 1] == '\n') i++;
                    _next = i + 1;
                    return true;
                }

                _current = _content.Substring(start);
                _next = _content.Length;
                _finished = true;
                return true;
            }

            public void Reset() { throw new NotSupportedException(); }
            public void Dispose() { }
        }
    }
}
