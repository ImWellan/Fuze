using System.Windows.Input;
using FusePlayer.Services;

namespace FusePlayer.Models;

public sealed record ShortcutDefinition(
    string Id,
    string Name,
    string Description,
    Key DefaultKey,
    ModifierKeys DefaultModifiers = ModifierKeys.None);

public static class ShortcutCatalog
{
    public static IReadOnlyList<ShortcutDefinition> Definitions { get; } =
    [
        new("open-file", "Ouvrir un fichier", "Ouvre le sélecteur de média.", Key.O, ModifierKeys.Control),
        new("open-multiple", "Ouvrir plusieurs fichiers", "Ouvre plusieurs médias en une seule fois.",
            Key.O, ModifierKeys.Control | ModifierKeys.Shift),
        new("open-settings", "Ouvrir les paramètres", "Affiche la fenêtre Paramètres.",
            Key.OemComma, ModifierKeys.Control),
        new("play-pause", "Lecture / pause", "Bascule entre la lecture et la pause.", Key.Space),
        new("play-pause-secondary", "Lecture / pause (secondaire)", "Deuxième touche de lecture et pause.", Key.K),
        new("seek-back", "Reculer", "Recule selon la durée configurée.", Key.Left),
        new("seek-back-secondary", "Reculer (secondaire)", "Deuxième touche de recul.", Key.J),
        new("seek-forward", "Avancer", "Avance selon la durée configurée.", Key.Right),
        new("seek-forward-secondary", "Avancer (secondaire)", "Deuxième touche d’avance.", Key.L),
        new("volume-up", "Augmenter le volume", "Augmente le volume de 5 %.", Key.Up),
        new("volume-down", "Réduire le volume", "Réduit le volume de 5 %.", Key.Down),
        new("mute", "Couper ou rétablir le son", "Bascule le mode muet.", Key.M),
        new("fullscreen", "Plein écran", "Entre ou sort du mode plein écran.", Key.F),
        new("next", "Chapitre ou média suivant", "Passe au chapitre suivant, puis au média suivant.", Key.N),
        new("previous", "Chapitre ou média précédent", "Passe au chapitre précédent, puis au média précédent.", Key.P),
        new("snapshot", "Capture d’écran", "Enregistre l’image vidéo actuelle.", Key.S),
        new("playlist", "File des médias", "Affiche ou masque la file des médias.", Key.Q),
        new("shuffle", "Lecture aléatoire", "Active ou désactive la lecture aléatoire.", Key.H),
        new("repeat", "Répéter le média", "Active ou désactive la répétition du média actuel.", Key.R),
        new("audio-track", "Piste audio suivante", "Passe à la piste audio suivante.", Key.A),
        new("subtitle-track", "Piste de sous-titres suivante", "Passe au sous-titre suivant.", Key.T),
        new("speed-menu", "Vitesse de lecture", "Ouvre le choix de la vitesse de lecture.", Key.V),
        new("track-sync", "Synchronisation des pistes", "Ouvre la synchronisation des pistes.", Key.Y),
        new("video-pan", "Déplacement de l’écran", "Souris : déplacer • molette : zoomer • clic droit : recentrer.", Key.G),
        new("options-bar", "Barre des options", "Affiche ou masque la barre des options.", Key.I),
        new("speed-down", "Réduire la vitesse", "Sélectionne la vitesse de lecture précédente.", Key.OemOpenBrackets),
        new("speed-up", "Augmenter la vitesse", "Sélectionne la vitesse de lecture suivante.", Key.OemCloseBrackets)
    ];

    public static Dictionary<string, string> CreateDefaults() => Definitions.ToDictionary(
        definition => definition.Id,
        definition => Encode(definition.DefaultKey, definition.DefaultModifiers),
        StringComparer.OrdinalIgnoreCase);

    public static Dictionary<string, string> Normalize(IReadOnlyDictionary<string, string>? source)
    {
        var defaults = CreateDefaults();
        if (source is null)
            return defaults;

        foreach (var definition in Definitions)
        {
            if (!source.TryGetValue(definition.Id, out var encoded))
                continue;
            defaults[definition.Id] = string.IsNullOrWhiteSpace(encoded) || TryDecode(encoded, out _, out _)
                ? encoded?.Trim() ?? string.Empty
                : defaults[definition.Id];
        }

        return defaults;
    }

    public static string Encode(Key key, ModifierKeys modifiers) =>
        $"{(int)modifiers}|{(int)key}";

    public static bool TryDecode(string? encoded, out Key key, out ModifierKeys modifiers)
    {
        key = Key.None;
        modifiers = ModifierKeys.None;
        if (string.IsNullOrWhiteSpace(encoded))
            return false;

        var pieces = encoded.Split('|', 2);
        if (pieces.Length != 2 || !int.TryParse(pieces[0], out var modifierValue) ||
            !int.TryParse(pieces[1], out var keyValue) ||
            !Enum.IsDefined(typeof(Key), keyValue))
            return false;

        const ModifierKeys supported = ModifierKeys.Control | ModifierKeys.Shift |
                                       ModifierKeys.Alt | ModifierKeys.Windows;
        modifiers = (ModifierKeys)modifierValue & supported;
        key = (Key)keyValue;
        return key != Key.None && !IsModifierKey(key);
    }

    public static string Format(string? encoded)
    {
        if (!TryDecode(encoded, out var key, out var modifiers))
            return LocalizationService.Get("Non attribué");

        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control))
            parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Shift))
            parts.Add(LocalizationService.Get("Maj"));
        if (modifiers.HasFlag(ModifierKeys.Alt))
            parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Windows))
            parts.Add("Windows");
        parts.Add(FormatKey(key));
        return string.Join(" + ", parts);
    }

    public static bool IsModifierKey(Key key) => key is Key.LeftCtrl or Key.RightCtrl or
        Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;

    private static string FormatKey(Key key) => key switch
    {
        Key.Space => LocalizationService.Get("Espace"),
        Key.Left => "←",
        Key.Right => "→",
        Key.Up => "↑",
        Key.Down => "↓",
        Key.OemOpenBrackets => "[",
        Key.OemCloseBrackets => "]",
        Key.OemComma => ",",
        Key.Return => LocalizationService.Get("Entrée"),
        Key.Back => LocalizationService.Get("Retour arrière"),
        Key.Delete => LocalizationService.Get("Suppr"),
        _ => key.ToString()
    };
}
