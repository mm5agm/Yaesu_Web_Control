using Yaesu_Web_Control.Models;

namespace Yaesu_Web_Control.Services
{
    public interface ISettingsService : IDisposable
    {
        Task<ApplicationSettings> GetSettingsAsync();
        Task SaveSettingsAsync(ApplicationSettings settings);

        /// <summary>
        /// Last in-memory snapshot from load/save. Never hits disk — safe on
        /// the Radio Display STA capture thread.
        /// </summary>
        ApplicationSettings GetCachedSettings();

        /// <summary>Absolute path to the user settings file on disk.</summary>
        string GetSettingsFilePath();

        /// <summary>Drop the in-memory cache so the next GetSettingsAsync re-reads from disk.
        /// Used after an import overwrites the file externally.</summary>
        void InvalidateCache();
    }
}