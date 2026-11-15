using System;
using System.IO;
using System.Text.Json;

namespace Translator.Services
{
    /// <summary>
    /// User settings, stored in %LocalAppData%\Translator\settings.json. Client settings only: the
    /// LibreTranslate server has its own settings (LibreTranslateServer\config.json) and is run
    /// separately; the app never starts, stops or configures it.
    /// </summary>
    public class AppSettings
    {
        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Translator", "settings.json");

        /// <summary>Address of the LibreTranslate server or load balancer.</summary>
        public string LibreTranslateUrl { get; set; } = "http://127.0.0.1:5000";

        public string LibreTranslateApiKey { get; set; } = "";

        /// <summary>Requests sent at the same time. 0 = automatic (2 per healthy worker).</summary>
        public int ParallelRequests { get; set; } = 0;

        public LibreTranslateService CreateService() =>
            new LibreTranslateService(LibreTranslateUrl, LibreTranslateApiKey, parallelRequests: ParallelRequests);

        public static AppSettings Load()
        {
            try
            {
                // Settings of older versions (server folder, workers, auto start) are ignored
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
            }
            catch (Exception)
            {
                // Unreadable settings: use defaults
            }
            return new AppSettings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
