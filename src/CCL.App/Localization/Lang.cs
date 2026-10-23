using System.Globalization;
using System.Windows;
using CCL.Core.Localization;

namespace CCL.App.Localization;

/// <summary>
/// 语言切换。文案放在 <c>Resources/Strings.*.xaml</c> 两份资源字典里，
/// 切换时替换合并字典，XAML 里的 DynamicResource 会自动刷新；C# 侧走 <see cref="Text"/>。
/// </summary>
public static class Lang
{
    public const string SystemPreference = "system";
    public const string Chinese = "zh-CN";
    public const string English = "en-US";

    private const string DictionaryPrefix = "Resources/Strings.";

    public static string Preference { get; private set; } = SystemPreference;

    public static string Current { get; private set; } = Chinese;

    public static event Action? LanguageChanged;

    /// <summary>按偏好解析出实际语言：跟随系统时看操作系统 UI 语言。</summary>
    public static string Resolve(string? preference)
    {
        if (string.Equals(preference, English, StringComparison.OrdinalIgnoreCase)) return English;
        if (string.Equals(preference, Chinese, StringComparison.OrdinalIgnoreCase)) return Chinese;

        var system = CultureInfo.CurrentUICulture;
        if (system.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase))
            // 繁体系统仍用简体文案，避免出现未翻译的空项
            return Chinese;
        return system.TwoLetterISOLanguageName.Equals("en", StringComparison.OrdinalIgnoreCase)
            ? English
            : Chinese;
    }

    public static void Apply(string? preference)
    {
        Preference = preference ?? SystemPreference;
        var language = Resolve(Preference);
        var application = Application.Current;
        if (application is null)
        {
            SetCurrent(language);
            return;
        }

        var dictionaries = application.Resources.MergedDictionaries;
        var existing = dictionaries.FirstOrDefault(d =>
            d.Source?.OriginalString.Contains(DictionaryPrefix, StringComparison.OrdinalIgnoreCase) == true);
        if (existing is not null) dictionaries.Remove(existing);

        dictionaries.Add(new ResourceDictionary
        {
            Source = new Uri($"{DictionaryPrefix}{language}.xaml", UriKind.Relative)
        });

        SetCurrent(language);
        LanguageChanged?.Invoke();
    }

    private static void SetCurrent(string language)
    {
        Current = language;
        CoreText.English = language == English;
        var culture = CultureInfo.GetCultureInfo(language);
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }

    /// <summary>取一条文案，找不到时回退成 key 本身，方便发现漏翻译。</summary>
    public static string Text(string key)
    {
        var value = Application.Current?.TryFindResource(key) as string;
        return value ?? key;
    }

    public static string Format(string key, params object?[] args)
    {
        var template = Text(key);
        try
        {
            return string.Format(CultureInfo.CurrentCulture, template, args);
        }
        catch (FormatException)
        {
            return template;
        }
    }
}
