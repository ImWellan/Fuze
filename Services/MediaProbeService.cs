using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace FusePlayer.Services;

public sealed class MediaProbeService
{
    private const int MaximumCachedAnalyses = 12;
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, ProbeCacheEntry> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    private sealed record ProbeCacheEntry(long Length, long LastWriteUtcTicks,
        Task<string?> Analysis);

    private static string L(string source) => LocalizationService.Get(source);

    private static readonly Dictionary<string, string> LanguageNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fre"] = "Français",
        ["fra"] = "Français",
        ["fr"] = "Français",
        ["eng"] = "Anglais",
        ["en"] = "Anglais",
        ["jpn"] = "Japonais",
        ["ja"] = "Japonais",
        ["spa"] = "Espagnol",
        ["es"] = "Espagnol",
        ["ger"] = "Allemand",
        ["deu"] = "Allemand",
        ["de"] = "Allemand",
        ["ita"] = "Italien",
        ["it"] = "Italien",
        ["por"] = "Portugais",
        ["pt"] = "Portugais",
        ["kor"] = "Coréen",
        ["ko"] = "Coréen",
        ["chi"] = "Chinois",
        ["zho"] = "Chinois",
        ["zh"] = "Chinois",
        ["und"] = "Indéterminée"
    };

    private static readonly Dictionary<string, string> RegionalLanguageNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["fr-CA"] = "Français — Canada",
            ["fr-FR"] = "Français — France",
            ["en-CA"] = "Anglais — Canada",
            ["en-US"] = "Anglais — États-Unis",
            ["en-GB"] = "Anglais — Royaume-Uni"
        };

    public async Task<string?> BuildInformationAsync(string mediaPath,
        CancellationToken cancellationToken = default)
    {
        FileInfo file;
        try
        {
            file = new FileInfo(mediaPath);
            if (!file.Exists)
                return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          ArgumentException or NotSupportedException)
        {
            return null;
        }

        Task<string?> analysis;
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(file.FullName, out var cached) &&
                cached.Length == file.Length &&
                cached.LastWriteUtcTicks == file.LastWriteTimeUtc.Ticks)
            {
                analysis = cached.Analysis;
            }
            else
            {
                analysis = AnalyzeAsync(file.FullName);
                _cache[file.FullName] = new ProbeCacheEntry(
                    file.Length, file.LastWriteTimeUtc.Ticks, analysis);
                TrimCache(file.FullName);
            }
        }

        try
        {
            return await analysis.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private async Task<string?> AnalyzeAsync(string mediaPath)
    {

        var ffprobe = FindFfprobe();
        if (ffprobe is null)
            return null;

        using var timeout = new CancellationTokenSource();
        timeout.CancelAfter(TimeSpan.FromSeconds(20));

        var startInfo = new ProcessStartInfo
        {
            FileName = ffprobe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // ffprobe emits JSON as UTF-8 even when Windows uses a legacy
            // console code page. Set both streams explicitly so accented
            // titles and metadata are decoded consistently at every system
            // locale and display scale.
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-show_format");
        startInfo.ArgumentList.Add("-show_streams");
        startInfo.ArgumentList.Add("-show_chapters");
        startInfo.ArgumentList.Add("-of");
        startInfo.ArgumentList.Add("json");
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add(mediaPath);

        try
        {
            var regionalLanguagesTask = ReadRegionalLanguagesAsync(mediaPath, timeout.Token);
            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
                return null;

            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var output = await outputTask;
            _ = await errorTask;
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                return null;

            using var document = JsonDocument.Parse(output);
            var regionalLanguages = await regionalLanguagesTask;
            return FormatInformation(document.RootElement, mediaPath, regionalLanguages);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or
                                          JsonException or OperationCanceledException)
        {
            return null;
        }
    }

    private void TrimCache(string retainedPath)
    {
        while (_cache.Count > MaximumCachedAnalyses)
        {
            var path = _cache.Keys.FirstOrDefault(path =>
                !string.Equals(path, retainedPath, StringComparison.OrdinalIgnoreCase));
            if (path is null)
                return;
            _cache.Remove(path);
        }
    }

    private static string? FindFfprobe()
    {
        var configured = Environment.GetEnvironmentVariable("FUZE_FFPROBE");
        var candidates = new List<string?>
        {
            configured,
            Path.Combine(AppContext.BaseDirectory, "ffprobe.exe"),
            Path.Combine(AppContext.BaseDirectory, "Tools", "ffprobe.exe"),
            @"C:\FFMPEG Program\ffprobe.exe"
        };

        var path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(path))
        {
            candidates.AddRange(path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(folder => Path.Combine(folder.Trim().Trim('"'), "ffprobe.exe")));
        }

        return candidates.FirstOrDefault(candidate =>
            !string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate));
    }

    private static async Task<Dictionary<int, string>> ReadRegionalLanguagesAsync(
        string mediaPath, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(mediaPath);
        if (!extension.Equals(".mkv", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".mka", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".mks", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".webm", StringComparison.OrdinalIgnoreCase))
            return [];

        try
        {
            return await MatroskaLanguageReader.ReadAsync(mediaPath, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or
                                          UnauthorizedAccessException or OperationCanceledException)
        {
            return [];
        }
    }

    private static string FormatInformation(JsonElement root, string mediaPath,
        IReadOnlyDictionary<int, string> regionalLanguages)
    {
        var streams = root.TryGetProperty("streams", out var streamsElement)
            ? streamsElement.EnumerateArray().ToArray()
            : [];
        var chapters = root.TryGetProperty("chapters", out var chaptersElement)
            ? chaptersElement.EnumerateArray().ToArray()
            : [];
        var format = root.TryGetProperty("format", out var formatElement)
            ? formatElement
            : default;

        var videoStreams = streams.Where(stream => GetString(stream, "codec_type") == "video").ToArray();
        var audioStreams = streams.Where(stream => GetString(stream, "codec_type") == "audio").ToArray();
        var subtitleStreams = streams.Where(stream => GetString(stream, "codec_type") == "subtitle").ToArray();
        var containerDuration = GetDouble(format, "duration");
        var originalTitle = GetTag(format, "title");
        var tagCount = CountTags(format) + streams.Sum(CountTags) + chapters.Sum(CountTags);

        var builder = new StringBuilder();
        builder.AppendLine("GÉNÉRAL");
        builder.AppendLine($"{L("Titre")}          : {Path.GetFileNameWithoutExtension(mediaPath)}");
        builder.AppendLine($"{L("Titre original")} : {(string.IsNullOrWhiteSpace(originalTitle) ? L("Aucun") : originalTitle)}");
        builder.AppendLine($"{L("Emplacement")}    : {mediaPath}");
        builder.AppendLine($"{L("Conteneur")}      : {FormatContainer(format, mediaPath)}");
        builder.AppendLine($"{L("Taille")}         : {FormatFileSize(GetLong(format, "size"))}");
        builder.AppendLine($"{L("Durée")}          : {FormatSeconds(containerDuration)}");
        builder.AppendLine($"{L("Débit global")}   : {FormatBitrate(GetLong(format, "bit_rate"))}");
        AppendOptional(builder, $"{L("Encodeur")}       : ", GetTag(format, "encoder"));
        AppendOptional(builder, $"{L("Création")}       : ", FormatCreationDate(GetTag(format, "creation_time")));
        builder.AppendLine($"{L("Vidéo")}          : {FormatTrackCount(videoStreams.Length)}");
        builder.AppendLine($"{L("Audio")}          : {FormatTrackCount(audioStreams.Length)}");
        builder.AppendLine($"{L("Sous-titres")}    : {FormatTrackCount(subtitleStreams.Length)}");
        builder.AppendLine($"{L("Chapitres")}      : {FormatTrackCount(chapters.Length)}");
        builder.AppendLine($"{L("Balises")}        : {tagCount}");

        AppendVideoStreams(builder, videoStreams, containerDuration, regionalLanguages);
        AppendAudioStreams(builder, audioStreams, containerDuration, regionalLanguages);
        AppendSubtitleStreams(builder, subtitleStreams, containerDuration, regionalLanguages);
        AppendChapters(builder, chapters);
        AppendTags(builder, format, streams, chapters, tagCount);
        return builder.ToString().TrimEnd();
    }

    private static void AppendChapters(StringBuilder builder, JsonElement[] chapters)
    {
        if (chapters.Length == 0)
            return;

        builder.AppendLine();
        builder.AppendLine($"CHAPITRES — {FormatTrackCount(chapters.Length)}");
        for (var index = 0; index < chapters.Length; index++)
        {
            var chapter = chapters[index];
            var start = GetDouble(chapter, "start_time");
            var end = GetDouble(chapter, "end_time");
            var title = GetTag(chapter, "title") ?? $"{L("Chapitre")} {index + 1}";
            if (index > 0)
                builder.AppendLine();
            builder.AppendLine($"{index + 1}.");
            builder.AppendLine($"   {L("Titre")} : {title}");
            builder.AppendLine($"   {L("De")} : {FormatSeconds(start)}  ·  {L("À")} : {FormatSeconds(end)}");
            builder.AppendLine($"   {L("Durée")} : {FormatSeconds(Math.Max(0, end - start))}");
        }
    }

    private static void AppendVideoStreams(StringBuilder builder, JsonElement[] streams,
        double containerDuration, IReadOnlyDictionary<int, string> regionalLanguages)
    {
        if (streams.Length == 0)
            return;

        builder.AppendLine();
        builder.AppendLine($"VIDÉO — {FormatTrackCount(streams.Length)}");
        for (var index = 0; index < streams.Length; index++)
        {
            var stream = streams[index];
            if (index > 0)
                builder.AppendLine();
            builder.AppendLine($"{index + 1}.");
            AppendOptional(builder, $"   {L("ID")}          : ", GetStreamIdentifier(stream));
            builder.AppendLine($"   {L("Codec")}       : {CodecName(stream)}");
            builder.AppendLine($"   {L("Titre")}       : {TrackName(stream)}");
            builder.AppendLine($"   {L("Langue")}      : {LanguageName(TrackLanguage(stream, regionalLanguages))}");
            AppendDisposition(builder, stream);
            builder.AppendLine($"   {L("Durée")}       : {FormatSeconds(StreamDuration(stream, containerDuration))}");
            builder.AppendLine($"   {L("Débit")}       : {FormatBitrate(StreamBitrate(stream))}");
            builder.AppendLine($"   {L("Résolution")}  : {GetLong(stream, "width")} × {GetLong(stream, "height")}");
            var fps = ParseRate(GetString(stream, "avg_frame_rate"));
            if (fps > 0)
                builder.AppendLine($"   {L("Images/s")}    : {fps:0.###}");
            AppendOptional(builder, $"   {L("Profil")}      : ", GetString(stream, "profile"));
            AppendOptional(builder, $"   {L("Format pixel")}: ", GetString(stream, "pix_fmt"));
            AppendOptional(builder, $"   {L("Couleurs")}    : ", JoinNonEmpty(" · ", GetString(stream, "color_space"),
                GetString(stream, "color_transfer"), GetString(stream, "color_primaries")));
        }
    }

    private static void AppendAudioStreams(StringBuilder builder, JsonElement[] streams,
        double containerDuration, IReadOnlyDictionary<int, string> regionalLanguages)
    {
        if (streams.Length == 0)
            return;

        builder.AppendLine();
        builder.AppendLine($"AUDIO — {FormatTrackCount(streams.Length)}");
        for (var index = 0; index < streams.Length; index++)
        {
            var stream = streams[index];
            builder.AppendLine($"{index + 1}.");
            AppendOptional(builder, $"   {L("ID")}          : ", GetStreamIdentifier(stream));
            builder.AppendLine($"   {L("Codec")}       : {CodecName(stream)}");
            builder.AppendLine($"   {L("Titre")}       : {TrackName(stream)}");
            builder.AppendLine($"   {L("Langue")}      : {LanguageName(TrackLanguage(stream, regionalLanguages))}");
            AppendDisposition(builder, stream);
            builder.AppendLine($"   {L("Durée")}       : {FormatSeconds(StreamDuration(stream, containerDuration))}");
            builder.AppendLine($"   {L("Débit")}       : {FormatBitrate(StreamBitrate(stream))}");
            AppendOptional(builder, $"   {L("Profil")}      : ", GetString(stream, "profile"));
            builder.AppendLine($"   {L("Canaux")}      : {GetLong(stream, "channels")} · {GetString(stream, "channel_layout") ?? L("disposition inconnue")}");
            var sampleRate = GetLong(stream, "sample_rate");
            if (sampleRate > 0)
                builder.AppendLine($"   {L("Fréquence")}   : {sampleRate:N0} Hz".Replace(',', ' '));
        }
    }

    private static void AppendSubtitleStreams(StringBuilder builder, JsonElement[] streams,
        double containerDuration, IReadOnlyDictionary<int, string> regionalLanguages)
    {
        if (streams.Length == 0)
            return;

        builder.AppendLine();
        builder.AppendLine($"SOUS-TITRES — {FormatTrackCount(streams.Length)}");
        for (var index = 0; index < streams.Length; index++)
        {
            var stream = streams[index];
            builder.AppendLine($"{index + 1}.");
            AppendOptional(builder, $"   {L("ID")}          : ", GetStreamIdentifier(stream));
            builder.AppendLine($"   {L("Codec")}       : {CodecName(stream)}");
            builder.AppendLine($"   {L("Titre")}       : {TrackName(stream)}");
            builder.AppendLine($"   {L("Langue")}      : {LanguageName(TrackLanguage(stream, regionalLanguages))}");
            AppendDisposition(builder, stream);
            builder.AppendLine($"   {L("Durée")}       : {FormatSeconds(StreamDuration(stream, containerDuration))}");
        }
    }

    private static void AppendDisposition(StringBuilder builder, JsonElement stream,
        string indentation = "   ")
    {
        var isDefault = GetDisposition(stream, "default");
        var isForced = GetDisposition(stream, "forced");
        builder.AppendLine($"{indentation}{L("Par défaut")}  : {YesNo(isDefault)}");
        builder.AppendLine($"{indentation}{L("Forcée")}      : {YesNo(isForced)}");

        var extras = new List<string>();
        if (GetDisposition(stream, "original")) extras.Add(L("originale"));
        if (GetDisposition(stream, "comment")) extras.Add(L("commentaire"));
        if (GetDisposition(stream, "hearing_impaired")) extras.Add(L("malentendants"));
        if (GetDisposition(stream, "visual_impaired")) extras.Add(L("audiodescription"));
        if (GetDisposition(stream, "karaoke")) extras.Add(L("karaoké"));
        if (extras.Count > 0)
            builder.AppendLine($"{indentation}{L("Attributs")}   : {string.Join(" · ", extras)}");
    }

    private static void AppendTags(StringBuilder builder, JsonElement format,
        JsonElement[] streams, JsonElement[] chapters, int tagCount)
    {
        if (tagCount == 0)
            return;

        builder.AppendLine();
        builder.AppendLine($"BALISES — {tagCount}");
        AppendTagGroup(builder, L("Conteneur"), format);

        for (var index = 0; index < streams.Length; index++)
        {
            var type = GetString(streams[index], "codec_type") switch
            {
                "video" => L("Vidéo"),
                "audio" => L("Audio"),
                "subtitle" => L("Sous-titres"),
                _ => L("Flux")
            };
            AppendTagGroup(builder, $"{type} · {L("ID")} {GetStreamIdentifier(streams[index]) ?? L("Aucun")}",
                streams[index]);
        }

        for (var index = 0; index < chapters.Length; index++)
            AppendTagGroup(builder, $"{L("Chapitre")} {index + 1}", chapters[index]);
    }

    private static void AppendTagGroup(StringBuilder builder, string groupName,
        JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Object)
            return;

        var properties = tags.EnumerateObject().ToArray();
        if (properties.Length == 0)
            return;

        builder.AppendLine();
        builder.AppendLine(groupName);
        foreach (var property in properties)
            builder.AppendLine($"   {property.Name} : {TagValue(property.Value)}");
    }

    private static string TagValue(JsonElement value) => value.ValueKind == JsonValueKind.String
        ? value.GetString() ?? string.Empty
        : value.ToString();

    private static string TrackName(JsonElement stream) =>
        GetTag(stream, "title") ?? L("Aucun");

    private static string CodecName(JsonElement stream)
    {
        var longName = GetString(stream, "codec_long_name");
        var shortName = GetString(stream, "codec_name");
        if (GetString(stream, "codec_type") == "subtitle")
            return SubtitleCodecName(shortName, longName);
        if (GetString(stream, "codec_type") == "audio")
        {
            var exactAudioName = shortName?.ToLowerInvariant() switch
            {
                "ac3" => "AC-3",
                "eac3" => "E-AC-3",
                _ => null
            };
            if (exactAudioName is not null)
                return exactAudioName;
        }
        if (longName is null)
            return shortName ?? L("Inconnu");
        if (string.IsNullOrWhiteSpace(shortName) ||
            NormalizeCodecName(longName).Contains(NormalizeCodecName(shortName),
                StringComparison.OrdinalIgnoreCase))
            return longName;
        return $"{longName} ({shortName})";
    }

    private static string SubtitleCodecName(string? shortName, string? longName) =>
        shortName?.ToLowerInvariant() switch
        {
            "subrip" => "SubRip (SRT)",
            "ass" => string.Equals(GetSubtitleVariant(longName), "SSA", StringComparison.Ordinal)
                ? "SubStation Alpha (SSA)"
                : "Advanced SubStation Alpha (ASS)",
            "ssa" => "SubStation Alpha (SSA)",
            "webvtt" => "WebVTT (VTT)",
            "mov_text" => "3GPP Timed Text (TX3G)",
            "hdmv_pgs_subtitle" => "Presentation Graphic Stream (PGS)",
            "dvd_subtitle" => "DVD Subtitle (VobSub)",
            "dvb_subtitle" => "DVB Subtitle (DVB)",
            "dvb_teletext" => "DVB Teletext",
            "xsub" => "DivX Subtitle (XSUB)",
            "sami" => "Synchronized Accessible Media Interchange (SAMI)",
            "realtext" => "RealText (RT)",
            "jacosub" => "JACOsub (JSS)",
            "microdvd" => "MicroDVD (SUB)",
            "mpl2" => "MPL2 Subtitle (MPL2)",
            "pjs" => "Phoenix Japanimation Society (PJS)",
            "stl" => "Spruce Subtitle Format (STL)",
            "subviewer" => "SubViewer (SUB)",
            "subviewer1" => "SubViewer 1.0 (SUB)",
            "vplayer" => "VPlayer Subtitle (TXT)",
            "text" => $"{L("Texte brut")} (TXT)",
            "eia_608" => $"{L("Sous-titres codés")} CEA-608 (CEA-608)",
            "eia_708" => $"{L("Sous-titres codés")} CEA-708 (CEA-708)",
            "arib_caption" => $"{L("Sous-titres")} ARIB STD-B24 (ARIB)",
            "ttml" => "Timed Text Markup Language (TTML)",
            "scc" => "Scenarist Closed Captions (SCC)",
            null or "" => longName ?? L("Inconnu"),
            _ => string.IsNullOrWhiteSpace(longName)
                ? shortName.ToUpperInvariant()
                : $"{longName} ({shortName.ToUpperInvariant()})"
        };

    private static string? GetSubtitleVariant(string? longName)
    {
        if (string.IsNullOrWhiteSpace(longName))
            return null;
        return longName.StartsWith("SSA", StringComparison.OrdinalIgnoreCase) ? "SSA" : null;
    }

    private static string NormalizeCodecName(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static double StreamDuration(JsonElement stream, double fallback)
    {
        var tagDuration = GetTag(stream, "duration");
        if (TryParseTimestamp(tagDuration, out var parsed))
            return parsed;

        var duration = GetDouble(stream, "duration");
        return duration > 0 ? duration : fallback;
    }

    private static long StreamBitrate(JsonElement stream)
    {
        var bitrate = GetLong(stream, "bit_rate");
        if (bitrate > 0)
            return bitrate;
        return long.TryParse(GetTag(stream, "bps"), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var tagBitrate) ? tagBitrate : 0;
    }

    private static string? GetTag(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var property in tags.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                return property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString()
                    : property.Value.ToString();
        }

        return null;
    }

    private static int CountTags(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Object)
            return 0;

        return tags.EnumerateObject().Count();
    }

    private static string? GetString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
            return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static string? GetIdentifier(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("id", out var identifier) ||
            identifier.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;

        var value = identifier.ValueKind == JsonValueKind.String
            ? identifier.GetString()
            : identifier.ToString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string? GetStreamIdentifier(JsonElement stream) =>
        GetString(stream, "index");

    private static string? TrackLanguage(JsonElement stream,
        IReadOnlyDictionary<int, string> regionalLanguages)
    {
        var streamId = (int)GetLong(stream, "index");
        if (regionalLanguages.TryGetValue(streamId, out var regionalLanguage))
            return regionalLanguage;

        var language = GetTag(stream, "language");
        var title = GetTag(stream, "title");
        if (language is not null &&
            (language.Equals("fre", StringComparison.OrdinalIgnoreCase) ||
             language.Equals("fra", StringComparison.OrdinalIgnoreCase) ||
             language.Equals("fr", StringComparison.OrdinalIgnoreCase)))
        {
            if (title?.Contains("VFQ", StringComparison.OrdinalIgnoreCase) == true)
                return "fr-CA";
            if (title?.Contains("VFF", StringComparison.OrdinalIgnoreCase) == true ||
                title?.Contains("Forcés", StringComparison.OrdinalIgnoreCase) == true ||
                title?.Contains("Complets", StringComparison.OrdinalIgnoreCase) == true)
                return "fr-FR";
        }

        return language;
    }

    private static long GetLong(JsonElement element, string name)
    {
        var value = GetString(element, name);
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
    }

    private static double GetDouble(JsonElement element, string name)
    {
        var value = GetString(element, name);
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
    }

    private static bool GetDisposition(JsonElement stream, string name) =>
        stream.ValueKind == JsonValueKind.Object &&
        stream.TryGetProperty("disposition", out var disposition) &&
        GetLong(disposition, name) == 1;

    private static bool TryParseTimestamp(string? value, out double seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(value))
            return false;
        var parts = value.Split(':');
        if (parts.Length != 3 ||
            !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var hours) ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes) ||
            !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var secs))
            return false;
        seconds = hours * 3600 + minutes * 60 + secs;
        return true;
    }

    private static double ParseRate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return 0;
        var parts = value.Split('/');
        if (parts.Length != 2 ||
            !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator) ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator) ||
            denominator == 0)
            return 0;
        return numerator / denominator;
    }

    private static string LanguageName(string? code)
    {
        if (string.IsNullOrWhiteSpace(code) ||
            string.Equals(code, "und", StringComparison.OrdinalIgnoreCase))
            return L("Aucun");

        var normalized = code.Trim().Replace('_', '-').ToLowerInvariant();
        var languageCode = normalized.Split('-', StringSplitOptions.RemoveEmptyEntries)[0];
        var name = RegionalLanguageNames.TryGetValue(normalized, out var regionalName)
            ? L(regionalName)
            : LanguageNames.TryGetValue(normalized, out var exactName)
            ? L(exactName)
            : LanguageNames.TryGetValue(languageCode, out var shortName)
                ? L(shortName)
                : code;
        return $"{name} ({code})";
    }

    private static string FormatSeconds(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds:000}"
            : $"{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds:000}";
    }

    private static string FormatFileSize(long bytes)
    {
        string[] units = LocalizationService.CurrentLanguage == "en"
            ? ["B", "KB", "MB", "GB", "TB"]
            : ["o", "Ko", "Mo", "Go", "To"];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.##} {units[unit]}";
    }

    private static string FormatContainer(JsonElement format, string mediaPath)
    {
        var name = GetString(format, "format_long_name") ??
                   GetString(format, "format_name") ??
                   L("Inconnu");
        var code = Path.GetExtension(mediaPath).TrimStart('.').ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(code))
            code = (GetString(format, "format_name") ?? "INCONNU")
                .Split(',', StringSplitOptions.RemoveEmptyEntries)[0]
                .ToUpperInvariant();
        return $"{name} ({code})";
    }

    private static string? FormatCreationDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var date))
            return value;

        var localDate = date.ToLocalTime();
        var dateText = localDate.ToString("dd'/'MM'/'yyyy HH:mm:ss", CultureInfo.CurrentCulture);
        return $"{dateText} ({L("heure locale du PC")})";
    }

    private static string FormatBitrate(long bitsPerSecond) => bitsPerSecond > 0
        ? bitsPerSecond >= 1_000_000
            ? $"{bitsPerSecond / 1_000_000d:0.###} Mb/s"
            : $"{bitsPerSecond / 1000d:0} kb/s"
        : L("Non indiqué");

    private static string FormatTrackCount(int count) =>
        $"{count} {L(count > 1 ? "pistes" : "piste")}";

    private static string YesNo(bool value) => L(value ? "Oui" : "Non");

    private static void AppendOptional(StringBuilder builder, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            builder.AppendLine(label + value);
    }

    private static string? JoinNonEmpty(string separator, params string?[] values)
    {
        var result = string.Join(separator, values.Where(value => !string.IsNullOrWhiteSpace(value)));
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }
}
