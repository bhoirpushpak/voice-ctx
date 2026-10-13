namespace VoiceCtx.Audio.Translate;

/// <summary>A language supported end to end: Whisper (speech-to-text), NLLB (translation) and Windows voices.</summary>
/// <param name="Name">Display name.</param>
/// <param name="Whisper">Whisper / ISO 639-1 code (e.g. "hi").</param>
/// <param name="Nllb">NLLB-200 FLORES code (e.g. "hin_Deva").</param>
/// <param name="Voice">Windows voice language tag to look for (e.g. "hi-IN").</param>
public sealed record Language(string Name, string Whisper, string Nllb, string Voice)
{
    public override string ToString() => Name;
}

public static class Languages
{
    /// <summary>"Auto-detect" pseudo-language for the spoken (source) side.</summary>
    public static readonly Language Auto = new("Auto-detect", "auto", "", "");

    public static readonly IReadOnlyList<Language> All =
    [
        new("English", "en", "eng_Latn", "en-US"),
        new("Hindi", "hi", "hin_Deva", "hi-IN"),
        new("Marathi", "mr", "mar_Deva", "mr-IN"),
        new("Gujarati", "gu", "guj_Gujr", "gu-IN"),
        new("Bengali", "bn", "ben_Beng", "bn-IN"),
        new("Punjabi", "pa", "pan_Guru", "pa-IN"),
        new("Tamil", "ta", "tam_Taml", "ta-IN"),
        new("Telugu", "te", "tel_Telu", "te-IN"),
        new("Kannada", "kn", "kan_Knda", "kn-IN"),
        new("Malayalam", "ml", "mal_Mlym", "ml-IN"),
        new("Urdu", "ur", "urd_Arab", "ur-PK"),
        new("Nepali", "ne", "npi_Deva", "ne-NP"),
        new("Spanish", "es", "spa_Latn", "es-ES"),
        new("French", "fr", "fra_Latn", "fr-FR"),
        new("German", "de", "deu_Latn", "de-DE"),
        new("Italian", "it", "ita_Latn", "it-IT"),
        new("Portuguese", "pt", "por_Latn", "pt-BR"),
        new("Dutch", "nl", "nld_Latn", "nl-NL"),
        new("Russian", "ru", "rus_Cyrl", "ru-RU"),
        new("Ukrainian", "uk", "ukr_Cyrl", "uk-UA"),
        new("Polish", "pl", "pol_Latn", "pl-PL"),
        new("Czech", "cs", "ces_Latn", "cs-CZ"),
        new("Romanian", "ro", "ron_Latn", "ro-RO"),
        new("Hungarian", "hu", "hun_Latn", "hu-HU"),
        new("Greek", "el", "ell_Grek", "el-GR"),
        new("Swedish", "sv", "swe_Latn", "sv-SE"),
        new("Norwegian", "no", "nob_Latn", "nb-NO"),
        new("Danish", "da", "dan_Latn", "da-DK"),
        new("Finnish", "fi", "fin_Latn", "fi-FI"),
        new("Turkish", "tr", "tur_Latn", "tr-TR"),
        new("Arabic", "ar", "arb_Arab", "ar-SA"),
        new("Persian", "fa", "pes_Arab", "fa-IR"),
        new("Hebrew", "he", "heb_Hebr", "he-IL"),
        new("Chinese (Simplified)", "zh", "zho_Hans", "zh-CN"),
        new("Japanese", "ja", "jpn_Jpan", "ja-JP"),
        new("Korean", "ko", "kor_Hang", "ko-KR"),
        new("Vietnamese", "vi", "vie_Latn", "vi-VN"),
        new("Thai", "th", "tha_Thai", "th-TH"),
        new("Indonesian", "id", "ind_Latn", "id-ID"),
        new("Malay", "ms", "zsm_Latn", "ms-MY"),
        new("Filipino", "tl", "tgl_Latn", "fil-PH"),
        new("Swahili", "sw", "swh_Latn", "sw-KE"),
    ];

    public static Language? FromWhisper(string? code) =>
        string.IsNullOrEmpty(code) ? null : All.FirstOrDefault(l => l.Whisper.Equals(code, StringComparison.OrdinalIgnoreCase));
}
