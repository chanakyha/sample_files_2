using System;
using System.IO;
using System.Text.Json;

namespace Translator.Services
{
    /// <summary>
    /// User settings, stored in %LocalAppData%\Translator\settings.json. Client settings only: the
    /// Argos Translate server has its own settings (ArgosTranslateServer\config.json) and is run
    /// separately; the app never starts, stops or configures it.
    /// </summary>
    public class AppSettings
    {
        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Translator", "settings.json");

        /// <summary>Address of the Argos Translate server (load balancer or single worker).</summary>
        public string ServerUrl { get; set; } = "http://127.0.0.1:5000";

        /// <summary>Requests sent at the same time. 0 = automatic (2 per healthy worker).</summary>
        public int ParallelRequests { get; set; } = 0;

        public ArgosTranslateService CreateService() =>
            new ArgosTranslateService(ServerUrl, parallelRequests: ParallelRequests);

        public static AppSettings Load()
        {
            try
            {
                // Settings of older versions (older server address and options) are ignored
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
