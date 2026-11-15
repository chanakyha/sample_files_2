using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Translator.Services
{
    public class DetectionInfo
    {
        public string Code { get; set; }
        public double Confidence { get; set; } = 1.0;
        public bool Corrected { get; set; }
        public bool Uncertain { get; set; }
    }

    /// <summary>
    /// Disk cache of translations and detected languages, so the same text is never sent twice
    /// (also across app restarts). Stored in %LocalAppData%\Translator\translation-cache.json.
    /// </summary>
    public sealed class TranslationCache
    {
        private readonly string _path;
        private readonly object _saveLock = new object();
        private ConcurrentDictionary<string, string> _translations = new ConcurrentDictionary<string, string>();
        private ConcurrentDictionary<string, DetectionInfo> _detections = new ConcurrentDictionary<string, DetectionInfo>();
        private volatile bool _dirty;

        public TranslationCache(string path = null)
        {
            _path = path ?? DefaultPath("libretranslate");
            Load();
        }

        /// <summary>%LocalAppData%\Translator\translation-cache-{name}.json</summary>
        public static string DefaultPath(string name) =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Translator",
                $"translation-cache-{name}.json");

        public string FilePath => _path;
        public int TranslationCount => _translations.Count;

        public bool TryGetTranslation(string target, string text, out string translation) =>
            _translations.TryGetValue(Key(target, text), out translation);

        public void SetTranslation(string target, string text, string translation)
        {
            _translations[Key(target, text)] = translation;
            _dirty = true;
        }

        public bool TryGetDetection(string text, out DetectionInfo detection) =>
            _detections.TryGetValue(text, out detection);

        public void SetDetection(string text, DetectionInfo detection)
        {
            _detections[text] = detection;
            _dirty = true;
        }

        public void Clear()
        {
            _translations.Clear();
            _detections.Clear();
            _dirty = true;
            Save();
        }

        public void Save()
        {
            if (!_dirty) return;
            lock (_saveLock)
            {
                try
                {
                    var file = new CacheFile
                    {
                        Translations = _translations.ToDictionary(p => p.Key, p => p.Value),
                        Detections = _detections.ToDictionary(p => p.Key, p => p.Value)
                    };
                    Directory.CreateDirectory(Path.GetDirectoryName(_path));
                    var temp = _path + ".tmp";
                    File.WriteAllText(temp, JsonSerializer.Serialize(file));
                    File.Move(temp, _path, true);
                    _dirty = false;
                }
                catch (IOException)
                {
                    // Cache is an optimisation only; ignore write failures
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(_path)) return;
                var file = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(_path));
                if (file?.Translations != null)
                    _translations = new ConcurrentDictionary<string, string>(file.Translations);
                if (file?.Detections != null)
                    _detections = new ConcurrentDictionary<string, DetectionInfo>(file.Detections);
            }
            catch (Exception)
            {
                // Corrupt or unreadable cache: start empty
            }
        }

        private static string Key(string target, string text) => target + "\u001F" + text;

        private class CacheFile
        {
            public Dictionary<string, string> Translations { get; set; }
            public Dictionary<string, DetectionInfo> Detections { get; set; }
        }
    }
}
