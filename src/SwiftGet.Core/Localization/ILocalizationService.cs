using System.Collections.Generic;

namespace SwiftGet.Core.Localization;

public interface ILocalizationService
{
    string CurrentLanguage { get; set; }
    IReadOnlyList<string> SupportedLanguages { get; }
    string GetString(string key, string? defaultText = null);
}
