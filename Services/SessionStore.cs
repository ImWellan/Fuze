using System.IO;
using System.Text.Json;
using FusePlayer.Models;

namespace FusePlayer.Services;

public sealed class SessionStore
{
    private readonly string _filePath;
    private readonly string[] _legacyFilePaths;
    private readonly JsonSerializerOptions _options = new()
    {
        WriteIndented = true
    };

    public SessionStore()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _filePath = Path.Combine(appData, "Fuze", "session.json");
        _legacyFilePaths =
        [
            Path.Combine(appData, "Fuse", "session.json"),
            Path.Combine(appData, "FusePlayer", "session.json")
        ];
    }

    public PlayerSession Load()
    {
        try
        {
            var sourcePath = File.Exists(_filePath)
                ? _filePath
                : _legacyFilePaths.FirstOrDefault(File.Exists);
            if (sourcePath is null)
                return new PlayerSession();

            var json = File.ReadAllText(sourcePath);
            return JsonSerializer.Deserialize<PlayerSession>(json, _options) ?? new PlayerSession();
        }
        catch
        {
            return new PlayerSession();
        }
    }

    public void Save(PlayerSession session)
    {
        SaveToPath(_filePath, session);
    }

    /// <summary>
    /// Exporte uniquement la configuration de Fuze. La file, la position
    /// courante et l'historique restent locaux à l'ordinateur et ne sont pas
    /// inclus dans le fichier partagé.
    /// </summary>
    public bool TryExportSettings(string destinationPath)
    {
        try
        {
            var session = Load();
            session.Playlist?.Clear();
            session.SelectedIndex = -1;
            session.PlaylistVisible = false;
            session.LastMediaLocation = null;
            session.LastMediaPositionMilliseconds = 0;
            session.RecentMedia?.Clear();
            session.RecentMediaLastOpenedUtc?.Clear();
            session.MediaPlaybackPreferences?.Clear();
            return SaveToPath(destinationPath, session);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Charge une configuration précédemment exportée.</summary>
    public bool TryImportSettings(string sourcePath, out PlayerSession session)
    {
        session = new PlayerSession();
        try
        {
            if (!File.Exists(sourcePath))
                return false;

            var json = File.ReadAllText(sourcePath);
            session = JsonSerializer.Deserialize<PlayerSession>(json, _options)
                      ?? new PlayerSession();
            // Un export de réglages ne doit jamais réintroduire une file ou
            // une reprise provenant d'un autre poste.
            session.Playlist?.Clear();
            session.SelectedIndex = -1;
            session.PlaylistVisible = false;
            session.LastMediaLocation = null;
            session.LastMediaPositionMilliseconds = 0;
            session.RecentMedia?.Clear();
            session.RecentMediaLastOpenedUtc?.Clear();
            session.MediaPlaybackPreferences?.Clear();
            return true;
        }
        catch
        {
            session = new PlayerSession();
            return false;
        }
    }

    private bool SaveToPath(string targetPath, PlayerSession session)
    {
        var temporaryPath = string.Empty;
        try
        {
            var fullPath = Path.GetFullPath(targetPath);
            var directory = Path.GetDirectoryName(fullPath)!;
            Directory.CreateDirectory(directory);
            var json = JsonSerializer.Serialize(session, _options);
            // Écriture atomique : une fermeture brutale ne doit pas laisser un
            // session.json vide ou partiellement écrit.
            temporaryPath = fullPath + ".tmp";
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, fullPath, true);
            return true;
        }
        catch
        {
            // Une erreur de persistance ne doit jamais interrompre la lecture.
            if (!string.IsNullOrWhiteSpace(temporaryPath))
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch
                {
                    // Le nettoyage est opportuniste.
                }
            }

            return false;
        }
    }
}
