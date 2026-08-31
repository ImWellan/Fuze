using System.IO;
using System.Runtime.InteropServices;
using FusePlayer.Models;
using Microsoft.Win32;

namespace FusePlayer.Services;

/// <summary>
/// Registers Fuze as a per-user handler for the media extensions selected in
/// the settings. No administrator rights are required because the registration
/// is written below HKCU\Software\Classes.
/// </summary>
public static class FileAssociationService
{
    public sealed record FileAssociationType(string Extension, string Label, bool IsAudio);

    public const string ProgId = "Fuze.MediaFile";
    private const string ApplicationName = "Fuze";

    public static IReadOnlyList<FileAssociationType> SupportedFileTypes { get; } =
    [
        new(".mkv", "Matroska vidéo (.mkv)", false),
        new(".mp4", "MPEG-4 (.mp4)", false),
        new(".avi", "AVI (.avi)", false),
        new(".mov", "QuickTime (.mov)", false),
        new(".webm", "WebM (.webm)", false),
        new(".m4v", "M4V (.m4v)", false),
        new(".ts", "MPEG-TS (.ts)", false),
        new(".m2ts", "M2TS (.m2ts)", false),
        new(".mts", "MTS (.mts)", false),
        new(".mpeg", "MPEG (.mpeg)", false),
        new(".mpg", "MPEG (.mpg)", false),
        new(".vob", "DVD vidéo (.vob)", false),
        new(".ogv", "Ogg vidéo (.ogv)", false),
        new(".wmv", "Windows Media (.wmv)", false),
        new(".flv", "Flash vidéo (.flv)", false),
        new(".mka", "Matroska audio (.mka)", true),
        new(".mp3", "MPEG audio (.mp3)", true),
        new(".m4a", "MPEG-4 audio (.m4a)", true),
        new(".flac", "FLAC (.flac)", true),
        new(".ogg", "Ogg audio (.ogg)", true),
        new(".opus", "Opus (.opus)", true),
        new(".wav", "WAV (.wav)", true),
        new(".aac", "AAC (.aac)", true)
    ];

    private const string ClassesPath = @"Software\Classes";
    private const uint ShcneAssocChanged = 0x08000000;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);

    public static bool TryApply(
        bool enabled,
        IEnumerable<string>? selectedExtensions,
        string executablePath,
        IEnumerable<CustomFileAssociationData>? customTypes,
        IEnumerable<string>? previousCustomExtensions,
        out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            error = LocalizationService.Get("Le chemin de Fuze est introuvable.");
            return false;
        }

        try
        {
            // Built-in formats are intentionally managed as one invisible set.
            // The settings UI no longer exposes individual extension choices.
            var normalized = SupportedFileTypes
                .Select(type => type.Extension)
                .ToArray();
            var custom = NormalizeCustomTypes(customTypes);
            var registeredExtensions = SupportedFileTypes
                .Select(type => type.Extension)
                .Concat(custom.Select(type => type.Extension))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var desired = normalized
                .Concat(custom.Where(type => type.Enabled).Select(type => type.Extension))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var extensionsToProcess = SupportedFileTypes
                .Select(type => type.Extension)
                .Concat(custom.Select(type => type.Extension))
                .Concat(NormalizeArbitraryExtensions(previousCustomExtensions))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (enabled && desired.Count == 0)
            {
                error = LocalizationService.Get("Sélectionnez au moins un format de fichier.");
                return false;
            }

            using var classes = Registry.CurrentUser.CreateSubKey(ClassesPath, writable: true);
            if (classes is null)
            {
                error = LocalizationService.Get("Windows n’a pas permis d’accéder aux associations de fichiers.");
                return false;
            }

            // Fuze doit rester visible dans « Ouvrir avec » et dans les
            // paramètres Windows même si l’association automatique est coupée.
            RegisterProgram(classes, executablePath, registeredExtensions);
            RegisterApplicationCapabilities(Registry.CurrentUser, registeredExtensions);

            if (enabled)
            {
                foreach (var extension in extensionsToProcess)
                {
                    using var extensionKey = classes.CreateSubKey(extension, writable: true);
                    if (extensionKey is null)
                        continue;

                    if (desired.Contains(extension))
                    {
                        extensionKey.SetValue(string.Empty, ProgId, RegistryValueKind.String);
                        using var openWith = extensionKey.CreateSubKey("OpenWithProgids", writable: true);
                        openWith?.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
                    }
                    else
                    {
                        if (IsOwnedByFuze(extensionKey))
                            extensionKey.DeleteValue(string.Empty, throwOnMissingValue: false);
                        RemoveFuzeOpenWith(extensionKey);
                    }
                }
            }
            else
            {
                foreach (var extension in extensionsToProcess)
                {
                    using var extensionKey = classes.OpenSubKey(extension, writable: true);
                    if (extensionKey is not null)
                    {
                        if (IsOwnedByFuze(extensionKey))
                            extensionKey.DeleteValue(string.Empty, throwOnMissingValue: false);
                        RemoveFuzeOpenWith(extensionKey);
                    }
                }
            }

            // Demande à l’Explorateur de relire immédiatement les associations.
            SHChangeNotify(ShcneAssocChanged, 0, IntPtr.Zero, IntPtr.Zero);
            return true;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or
                                            System.Security.SecurityException or ArgumentException)
        {
            error = exception.Message;
            return false;
        }
    }

    public static string[] NormalizeExtensions(IEnumerable<string>? extensions)
    {
        var supported = SupportedFileTypes
            .Select(type => type.Extension)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return (extensions ?? [])
            .Select(extension => extension?.Trim() ?? string.Empty)
            .Where(extension => !string.IsNullOrWhiteSpace(extension))
            .Select(extension => extension.StartsWith('.') ? extension : $".{extension}")
            .Where(supported.Contains)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static CustomFileAssociationData[] NormalizeCustomTypes(
        IEnumerable<CustomFileAssociationData>? customTypes)
    {
        var builtIn = SupportedFileTypes
            .Select(type => type.Extension)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return (customTypes ?? [])
            .Select(type => new CustomFileAssociationData
            {
                Title = type.Title?.Trim() ?? string.Empty,
                Extension = NormalizeArbitraryExtension(type.Extension),
                IsAudio = type.IsAudio,
                Enabled = type.Enabled
            })
            .Where(type => !string.IsNullOrWhiteSpace(type.Title) &&
                           IsValidCustomExtension(type.Extension) &&
                           !builtIn.Contains(type.Extension))
            .GroupBy(type => type.Extension, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    private static string[] NormalizeArbitraryExtensions(IEnumerable<string>? extensions) =>
        (extensions ?? [])
            .Select(NormalizeArbitraryExtension)
            .Where(IsValidCustomExtension)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string NormalizeArbitraryExtension(string? extension)
    {
        var value = extension?.Trim() ?? string.Empty;
        return value.StartsWith('.') ? value.ToLowerInvariant() : $".{value.ToLowerInvariant()}";
    }

    private static bool IsValidCustomExtension(string extension) =>
        extension.Length is >= 2 and <= 32 &&
        extension[0] == '.' &&
        extension.Skip(1).All(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '+');

    private static void RegisterProgram(
        RegistryKey classes,
        string executablePath,
        IReadOnlySet<string> extensions)
    {
        using var program = classes.CreateSubKey(ProgId, writable: true)
                           ?? throw new IOException(LocalizationService.Get("Impossible de créer l’association Fuze."));
        program.SetValue(string.Empty, LocalizationService.Get("Fichier multimédia Fuze"), RegistryValueKind.String);

        using (var icon = program.CreateSubKey("DefaultIcon", writable: true))
            icon?.SetValue(string.Empty, $"\"{executablePath}\",0", RegistryValueKind.String);

        using var command = program.CreateSubKey(@"shell\open\command", writable: true);
        command?.SetValue(string.Empty, $"\"{executablePath}\" \"%1\"", RegistryValueKind.String);

        using var application = classes.CreateSubKey(@"Applications\Fuze.exe", writable: true);
        application?.SetValue("FriendlyAppName", ApplicationName, RegistryValueKind.String);
        using var applicationCommand = application?.CreateSubKey(@"shell\open\command", writable: true);
        applicationCommand?.SetValue(string.Empty, $"\"{executablePath}\" \"%1\"", RegistryValueKind.String);
        using var supportedTypes = application?.CreateSubKey("SupportedTypes", writable: true);
        if (supportedTypes is not null)
        {
            foreach (var valueName in supportedTypes.GetValueNames())
                supportedTypes.DeleteValue(valueName, throwOnMissingValue: false);
            foreach (var extension in extensions)
                supportedTypes.SetValue(extension, string.Empty, RegistryValueKind.String);
        }
    }

    private static void RegisterApplicationCapabilities(
        RegistryKey currentUser,
        IReadOnlySet<string> extensions)
    {
        using var capabilities = currentUser.CreateSubKey(@"Software\Fuze\Capabilities", writable: true)
            ?? throw new IOException(LocalizationService.Get("Impossible de déclarer Fuze dans les applications Windows."));
        capabilities.SetValue("ApplicationName", ApplicationName, RegistryValueKind.String);
        capabilities.SetValue("ApplicationDescription", LocalizationService.Get("Lecteur multimédia Fuze"), RegistryValueKind.String);
        using var associations = capabilities.CreateSubKey("FileAssociations", writable: true)
            ?? throw new IOException(LocalizationService.Get("Impossible de déclarer les formats de Fuze."));
        foreach (var valueName in associations.GetValueNames())
            associations.DeleteValue(valueName, throwOnMissingValue: false);
        foreach (var extension in extensions)
            associations.SetValue(extension, ProgId, RegistryValueKind.String);

        using var registered = currentUser.CreateSubKey(@"Software\RegisteredApplications", writable: true)
            ?? throw new IOException(LocalizationService.Get("Impossible d’enregistrer Fuze auprès de Windows."));
        registered.SetValue(ApplicationName, @"Software\Fuze\Capabilities", RegistryValueKind.String);
    }

    private static void UnregisterApplicationCapabilities(RegistryKey currentUser, RegistryKey classes)
    {
        classes.DeleteSubKeyTree(@"Applications\Fuze.exe", throwOnMissingSubKey: false);
        currentUser.DeleteSubKeyTree(@"Software\Fuze", throwOnMissingSubKey: false);
        using var registered = currentUser.OpenSubKey(@"Software\RegisteredApplications", writable: true);
        if (registered is not null && string.Equals(registered.GetValue(ApplicationName) as string,
                @"Software\Fuze\Capabilities", StringComparison.OrdinalIgnoreCase))
            registered.DeleteValue(ApplicationName, throwOnMissingValue: false);
    }

    private static bool IsOwnedByFuze(RegistryKey extensionKey) =>
        string.Equals(extensionKey.GetValue(string.Empty) as string, ProgId, StringComparison.OrdinalIgnoreCase);

    private static void RemoveFuzeOpenWith(RegistryKey extensionKey)
    {
        using var openWith = extensionKey.OpenSubKey("OpenWithProgids", writable: true);
        openWith?.DeleteValue(ProgId, throwOnMissingValue: false);
    }

}
