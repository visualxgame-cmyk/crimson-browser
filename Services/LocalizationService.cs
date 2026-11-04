namespace CrimsonBrowser.Services;

public class LocalizationService
{
    private static LocalizationService? _instance;
    public static LocalizationService Instance => _instance ??= new LocalizationService();

    public event Action? LanguageChanged;

    private string _currentLanguage = "en"; // Default is English!
    public string CurrentLanguage
    {
        get => _currentLanguage;
        set
        {
            if (_currentLanguage != value)
            {
                _currentLanguage = value;
                LanguageChanged?.Invoke();
            }
        }
    }

    private readonly Dictionary<string, Dictionary<string, string>> _translations = new()
    {
        ["en"] = new()
        {
            ["AppTitle"] = "Crimson Browser",
            ["NewTab"] = "New Tab",
            ["OmniboxPlaceholder"] = "Search with Google or enter web address...",
            ["BackTooltip"] = "Back (Alt+Left)",
            ["ForwardTooltip"] = "Forward (Alt+Right)",
            ["ReloadTooltip"] = "Reload (Ctrl+R / F5)",
            ["StopTooltip"] = "Stop loading",
            ["HomeTooltip"] = "Crimson Home",
            ["ShieldsButton"] = "SHIELDS",
            ["ShieldsButtonOff"] = "SHIELDS OFF",
            ["ShieldsTitle"] = "Crimson Shields",
            ["ShieldsActive"] = "Protection active on this site",
            ["ShieldsDisabled"] = "Protection disabled",
            ["ItemsBlocked"] = "items blocked",
            ["ResetStats"] = "Reset stats",
            ["BlockAdsTitle"] = "Block Ads & Trackers",
            ["BlockAdsDesc"] = "Block advertisements and tracking scripts",
            ["LowRamTitle"] = "Low-RAM Memory Saver",
            ["LowRamDesc"] = "Automatically suspend background tabs",
            ["HttpsTitle"] = "Enforce HTTPS Connections",
            ["HttpsDesc"] = "Automatically upgrade traffic to secure HTTPS",
            ["PopupsTitle"] = "Block Unsolicited Pop-ups",
            ["PopupsDesc"] = "Prevent third-party spam windows",
            ["MenuNewTab"] = "New Tab",
            ["MenuNewWindow"] = "New Window",
            ["MenuZoom"] = "Zoom",
            ["MenuDevTools"] = "Developer Tools",
            ["MenuClearData"] = "Clear Browsing Data & Cache",
            ["MenuSettings"] = "Settings",
            ["MenuAbout"] = "About Crimson Browser",
            ["ClearDataSuccess"] = "Cache and browsing data cleared successfully!",
            ["SettingsTitle"] = "Settings",
            ["GeneralTab"] = "General",
            ["AppearanceTab"] = "Appearance",
            ["PrivacyTab"] = "Shields & Privacy",
            ["LanguageSetting"] = "Language",
            ["LanguageSettingDesc"] = "Choose the display language for Crimson Browser",
            ["BackgroundSetting"] = "New Tab Background",
            ["BackgroundSettingDesc"] = "Customize your start page with an image of your choice",
            ["ChooseImageBtn"] = "Choose Custom Image...",
            ["ResetBackgroundBtn"] = "Reset to Default Dark Theme",
            ["DimmingLabel"] = "Background Dark Dimming",
            ["CloseSettings"] = "Save & Close",
            ["AboutHeader"] = "Crimson Browser v1.0.0",
            ["AboutDescription"] = "Fast, lightweight Chromium-based browser with Stealth Crimson styling.",
            ["StartSubtitle"] = "CHROMIUM POWERED • HIGH SPEED • LOW FOOTPRINT",
            ["StartSearchPlaceholder"] = "Search with Google or enter web address...",
            ["StartEngine"] = "Engine: Chromium Blink",
            ["StartOpt"] = "Optimization: Low RAM Mode",
            ["StartSec"] = "Security: Crimson Shields Active",
            ["StartFooter"] = "Crimson Browser • Built for speed and privacy"
        },
        ["ro"] = new()
        {
            ["AppTitle"] = "Crimson Browser",
            ["NewTab"] = "Filă nouă",
            ["OmniboxPlaceholder"] = "Caută pe Google sau introdu o adresă URL...",
            ["BackTooltip"] = "Înapoi (Alt+Stânga)",
            ["ForwardTooltip"] = "Înainte (Alt+Dreapta)",
            ["ReloadTooltip"] = "Reîmprospătează (Ctrl+R / F5)",
            ["StopTooltip"] = "Oprește încărcarea",
            ["HomeTooltip"] = "Acasă Crimson",
            ["ShieldsButton"] = "SHIELDS",
            ["ShieldsButtonOff"] = "SHIELDS OFF",
            ["ShieldsTitle"] = "Crimson Shields",
            ["ShieldsActive"] = "Protecție activă pe acest site",
            ["ShieldsDisabled"] = "Protecție oprită complet",
            ["ItemsBlocked"] = "elemente blocate",
            ["ResetStats"] = "Resetează contorul",
            ["BlockAdsTitle"] = "Blocare Reclame & Trackere",
            ["BlockAdsDesc"] = "Oprește reclamele și scripturile publicitare",
            ["LowRamTitle"] = "Consum Redus de Memorie",
            ["LowRamDesc"] = "Suspendă automat tab-urile inactive din fundal",
            ["HttpsTitle"] = "Forțare Conexiuni HTTPS",
            ["HttpsDesc"] = "Upgradează automat traficul la HTTPS sigur",
            ["PopupsTitle"] = "Blocare Pop-up-uri Spam",
            ["PopupsDesc"] = "Previne deschiderea de pagini nedorite",
            ["MenuNewTab"] = "Filă nouă",
            ["MenuNewWindow"] = "Fereastră nouă",
            ["MenuZoom"] = "Zoom",
            ["MenuDevTools"] = "Instrumente dezvoltator",
            ["MenuClearData"] = "Curăță Cache & Istoric",
            ["MenuSettings"] = "Setări",
            ["MenuAbout"] = "Despre Crimson Browser",
            ["ClearDataSuccess"] = "Cache-ul și datele de navigare au fost curățate cu succes!",
            ["SettingsTitle"] = "Setări",
            ["GeneralTab"] = "General",
            ["AppearanceTab"] = "Aspect",
            ["PrivacyTab"] = "Shields & Securitate",
            ["LanguageSetting"] = "Limba interfeței",
            ["LanguageSettingDesc"] = "Alege limba afișată în Crimson Browser",
            ["BackgroundSetting"] = "Fundal Pagină de Pornire",
            ["BackgroundSettingDesc"] = "Personalizează pagina de start cu o imagine la alegere",
            ["ChooseImageBtn"] = "Alege o Imagine...",
            ["ResetBackgroundBtn"] = "Resetează la Tema Implicită",
            ["DimmingLabel"] = "Intensitate Întunecare Fundal",
            ["CloseSettings"] = "Salvează & Închide",
            ["AboutHeader"] = "Crimson Browser v1.0.0",
            ["AboutDescription"] = "Browser rapid, bazat pe Chromium, cu consum redus și tematică Crimson.",
            ["StartSubtitle"] = "MOTOR CHROMIUM • VITEZĂ SPORITĂ • CONSUM REDUS",
            ["StartSearchPlaceholder"] = "Caută pe Google sau introdu o adresă URL...",
            ["StartEngine"] = "Motor: Chromium Blink",
            ["StartOpt"] = "Optimizare: Mod Low-RAM",
            ["StartSec"] = "Securitate: Crimson Shields Activ",
            ["StartFooter"] = "Crimson Browser • Construit pentru viteză și intimitate"
        }
    };

    public string Get(string key)
    {
        if (_translations.TryGetValue(_currentLanguage, out var dict) && dict.TryGetValue(key, out var val))
            return val;

        if (_translations["en"].TryGetValue(key, out var fallback))
            return fallback;

        return key;
    }
}
