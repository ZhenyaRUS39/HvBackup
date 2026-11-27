using Microsoft.Win32;

namespace HvBackup.Ui;

/// <summary>Цвета, шрифты и общие параметры оформления в стиле Windows 11.</summary>
public static class Theme
{
    public static bool Dark { get; private set; }

    public static Color Window;
    public static Color Card;
    public static Color CardBorder;
    public static Color CardHover;
    public static Color Text;
    public static Color TextSecondary;
    public static Color TextDisabled;
    public static Color Accent;
    public static Color AccentHover;
    public static Color AccentPressed;
    public static Color Success;
    public static Color SuccessHover;
    public static Color SuccessPressed;
    public static Color Danger;
    public static Color DangerHover;
    public static Color Scheduled;
    public static Color ScheduledHover;
    public static Color ScheduledPressed;
    public static Color ScheduledFg;
    public static Color LogBg;
    public static Color LogText;
    public static Color Track;
    public static Color Subtle;
    public static Color BorderStrong;

    public static Font Body = null!;
    public static Font BodyBold = null!;
    public static Font Title = null!;
    public static Font Subtitle = null!;
    public static Font Small = null!;
    public static Font Mono = null!;

    public const int CardRadius = 10;
    public const int ButtonRadius = 7;

    private static bool _initialized;

    public static void Init(bool? forceDark = null)
    {
        if (_initialized && forceDark == null) return;
        Dark = forceDark ?? !SystemPrefersLight();

        if (Dark)
        {
            Window = Color.FromArgb(32, 32, 32);
            Card = Color.FromArgb(43, 43, 43);
            CardBorder = Color.FromArgb(62, 62, 62);
            CardHover = Color.FromArgb(52, 52, 52);
            Text = Color.White;
            TextSecondary = Color.FromArgb(197, 197, 197);
            TextDisabled = Color.FromArgb(130, 130, 130);
            Accent = Color.FromArgb(76, 194, 255);
            AccentHover = Color.FromArgb(90, 203, 255);
            AccentPressed = Color.FromArgb(60, 175, 235);
            Success = Color.FromArgb(16, 124, 16);
            SuccessHover = Color.FromArgb(20, 142, 20);
            SuccessPressed = Color.FromArgb(12, 104, 12);
            Danger = Color.FromArgb(196, 43, 28);
            DangerHover = Color.FromArgb(219, 57, 40);
            Scheduled = Color.FromArgb(247, 201, 72);
            ScheduledHover = Color.FromArgb(252, 215, 108);
            ScheduledPressed = Color.FromArgb(226, 181, 55);
            ScheduledFg = Color.FromArgb(32, 26, 0);
            LogBg = Color.FromArgb(28, 28, 28);
            LogText = Color.FromArgb(224, 224, 224);
            Track = Color.FromArgb(58, 58, 58);
            Subtle = Color.FromArgb(48, 48, 48);
            BorderStrong = Color.FromArgb(80, 80, 80);
        }
        else
        {
            Window = Color.FromArgb(243, 243, 243);
            Card = Color.White;
            CardBorder = Color.FromArgb(225, 225, 225);
            CardHover = Color.FromArgb(249, 249, 249);
            Text = Color.FromArgb(27, 27, 27);
            TextSecondary = Color.FromArgb(96, 96, 96);
            TextDisabled = Color.FromArgb(160, 160, 160);
            Accent = Color.FromArgb(0, 103, 192);
            AccentHover = Color.FromArgb(25, 117, 197);
            AccentPressed = Color.FromArgb(0, 95, 184);
            Success = Color.FromArgb(16, 124, 16);
            SuccessHover = Color.FromArgb(22, 145, 22);
            SuccessPressed = Color.FromArgb(11, 103, 11);
            Danger = Color.FromArgb(196, 43, 28);
            DangerHover = Color.FromArgb(219, 57, 40);
            Scheduled = Color.FromArgb(255, 199, 64);
            ScheduledHover = Color.FromArgb(255, 213, 112);
            ScheduledPressed = Color.FromArgb(232, 178, 44);
            ScheduledFg = Color.FromArgb(32, 26, 0);
            LogBg = Color.FromArgb(31, 31, 31);
            LogText = Color.FromArgb(228, 228, 228);
            Track = Color.FromArgb(225, 225, 225);
            Subtle = Color.FromArgb(245, 245, 245);
            BorderStrong = Color.FromArgb(205, 205, 205);
        }

        var family = HasFont("Segoe UI Variable") ? "Segoe UI Variable" : "Segoe UI";
        Body = new Font(family, 9F, FontStyle.Regular);
        BodyBold = new Font(family, 9F, FontStyle.Bold);
        Title = new Font(family, 14F, FontStyle.Bold);
        Subtitle = new Font(family, 9F, FontStyle.Regular);
        Small = new Font(family, 8.25F, FontStyle.Regular);
        Mono = new Font(HasFont("Cascadia Mono") ? "Cascadia Mono" : "Consolas", 9F, FontStyle.Regular);
        _initialized = true;
    }

    private static bool SystemPrefersLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int value) return value == 1;
        }
        catch
        {
            // используем светлую тему по умолчанию
        }
        return true;
    }

    private static bool HasFont(string name)
    {
        try
        {
            using var installed = new System.Drawing.Text.InstalledFontCollection();
            return installed.Families.Any(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }
}
