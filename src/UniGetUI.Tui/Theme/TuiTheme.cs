using Avalonia.Media;

namespace UniGetUI.Tui.Theme;

/// <summary>
/// A colour theme for the TUI, expressed as semantic roles rather than raw colours, so every control
/// asks for "muted text" or "focus" and each theme decides what that looks like. <see cref="TuiPalette"/>
/// turns the active theme into live brushes and Consolonia resources.
/// </summary>
internal sealed record TuiTheme
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public bool IsLight { get; init; }

    /// <summary>Page background.</summary>
    public required Color Background { get; init; }

    /// <summary>Raised surfaces: dialogs, the alternative background of Consolonia controls.</summary>
    public required Color Surface { get; init; }

    /// <summary>Title bar and dialog title background.</summary>
    public required Color Chrome { get; init; }

    /// <summary>Text on the title bar and dialog titles.</summary>
    public required Color ChromeText { get; init; }

    /// <summary>Text on the menu bar and the menu drop-down (defaults to <see cref="ChromeText"/>).</summary>
    public Color MenuText
    {
        get => _menuText ?? ChromeText;
        init => _menuText = value;
    }

    private readonly Color? _menuText;

    public required Color MenuBar { get; init; }

    public required Color TabBar { get; init; }

    /// <summary>Menu drop-down background.</summary>
    public required Color DropDown { get; init; }

    public required Color Text { get; init; }

    /// <summary>Secondary text: status lines, notes, the footer, inactive tabs.</summary>
    public required Color TextMuted { get; init; }

    /// <summary>Tertiary text: disabled items, empty values, debug log lines.</summary>
    public required Color TextDim { get; init; }

    /// <summary>Titles, headers and labels.</summary>
    public required Color Brand { get; init; }

    /// <summary>Grid and control borders.</summary>
    public required Color Border { get; init; }

    /// <summary>Panel outlines: the page header, the package list and the details card. Defaults to
    /// <see cref="Border"/> lifted a third of the way toward <see cref="TextDim"/>, so panels still read as
    /// panels in themes whose control borders are very subtle.</summary>
    public Color Frame
    {
        get => _frame ?? Mix(Border, TextDim, 0.35);
        init => _frame = value;
    }

    private readonly Color? _frame;

    /// <summary>Thin separators (page title rule, details pane).</summary>
    public required Color Divider { get; init; }

    /// <summary>Focused row / field, active tab and menu, buttons. Consolonia draws both normal and chooser
    /// text on it, so it must suit <see cref="Text"/> as well as <see cref="FocusText"/>.</summary>
    public required Color Focus { get; init; }

    public required Color FocusText { get; init; }

    /// <summary>Selected but not focused items (list selection, chooser).</summary>
    public required Color Selection { get; init; }

    public required Color SelectionText { get; init; }

    /// <summary>Text box background.</summary>
    public required Color EditSurface { get; init; }

    public required Color EditText { get; init; }

    /// <summary>Placeholder text in text boxes (also Consolonia's disabled text).</summary>
    public required Color Watermark { get; init; }

    public required Color Error { get; init; }

    public required Color Success { get; init; }

    /// <summary>The new version in a package's details (defaults to <see cref="Success"/>).</summary>
    public Color NewVersion
    {
        get => _newVersion ?? Success;
        init => _newVersion = value;
    }

    private readonly Color? _newVersion;

    public required Color Warning { get; init; }

    /// <summary>The underlined access-key letter in menus.</summary>
    public required Color AccessKey { get; init; }

    /// <summary>Background of a notification line of the given tint (info, success, warning, error).</summary>
    public Color NotificationBackground(Color tint) => Mix(Background, tint, IsLight ? 0.16 : 0.2);

    private static Color Mix(Color a, Color b, double t)
        => Color.FromRgb((byte)(a.R + ((b.R - a.R) * t)), (byte)(a.G + ((b.G - a.G) * t)), (byte)(a.B + ((b.B - a.B) * t)));
}

/// <summary>
/// The built-in themes. The Devolutions themes follow the Remote Desktop Manager colour tokens
/// (<c>fill-*</c>, <c>border-*</c>, <c>text-*</c> of its Light, DarkBlue, DarkGray and DarkHighContrast
/// ramps); the others use each project's published palette.
/// </summary>
internal static class TuiThemes
{
    private static Color C(string hex) => Color.Parse(hex);

    /// <summary>The original TUI look (the default): Consolonia's graphite grey with Devolutions blue chrome.</summary>
    public static readonly TuiTheme DevolutionsGraphite = new()
    {
        Id = "devolutions-graphite",
        Name = "Devolutions - Graphite",
        Background = C("#2D2D30"),
        Surface = C("#1E1E24"),
        Chrome = C("#15489E"),
        ChromeText = C("#FFFFFF"),
        MenuBar = C("#1E3A6E"),
        TabBar = C("#202020"),
        DropDown = C("#15489E"),
        Text = C("#E8E8E8"),
        TextMuted = C("#B8B8B8"),
        TextDim = C("#808080"),
        Brand = C("#90BEFF"),
        Border = C("#6B6B70"),
        Divider = C("#3A3A3A"),
        Focus = C("#1450B0"),
        FocusText = C("#FFFFFF"),
        Selection = C("#15489E"),
        SelectionText = C("#FFFFFF"),
        EditSurface = C("#0C2040"),
        EditText = C("#FFFFFF"),
        Watermark = C("#90BEFF"),
        Error = C("#FFA8A8"),
        Success = C("#8EEAA1"),
        Warning = C("#FFE19A"),
        AccessKey = C("#FFE19A"),
    };

    /// <summary>True black and fully neutral: a pure black page, bars barely lifted off it, grey panels,
    /// borders, focus and selection, white headings, no blue. Softer than High contrast (grey borders,
    /// off-white text). Menu access letters are white and underlined; only status colours stay coloured.</summary>
    public static readonly TuiTheme DevolutionsBlack = new()
    {
        Id = "devolutions-black",
        Name = "Devolutions - Black",
        Background = C("#000000"),
        Surface = C("#141414"),
        Chrome = C("#0D0D0D"),
        ChromeText = C("#F5F5F5"),
        MenuBar = C("#0D0D0D"),
        MenuText = C("#C8C8C8"),
        TabBar = C("#0D0D0D"),
        DropDown = C("#141414"),
        Text = C("#E6E6E6"),
        TextMuted = C("#BDBDBD"),
        TextDim = C("#7A7A7A"),
        Brand = C("#FFFFFF"),
        Border = C("#333333"),
        Divider = C("#262626"),
        Focus = C("#4A4A4A"),
        FocusText = C("#FFFFFF"),
        Selection = C("#262626"),
        SelectionText = C("#FFFFFF"),
        EditSurface = C("#1A1A1A"),
        EditText = C("#E6E6E6"),
        Watermark = C("#8C8C8C"),
        Error = C("#F9766C"),
        Success = C("#BCEA8A"),
        NewVersion = C("#FFFFFF"),
        Warning = C("#F7B564"),
        AccessKey = C("#FFFFFF"),
    };

    /// <summary>RDM Light: white surfaces, grey chrome, brand blue #0068C3.</summary>
    public static readonly TuiTheme DevolutionsLight = new()
    {
        Id = "devolutions-light",
        Name = "Devolutions - Light",
        IsLight = true,
        Background = C("#FFFFFF"),
        Surface = C("#F2F2F2"),
        Chrome = C("#0068C3"),
        ChromeText = C("#FFFFFF"),
        MenuBar = C("#F0F0F0"),
        MenuText = C("#2A2A2A"),
        TabBar = C("#F9F9F9"),
        DropDown = C("#FAFAFA"),
        Text = C("#0D0D0D"),
        TextMuted = C("#404040"),
        TextDim = C("#717171"),
        Brand = C("#0068C3"),
        Border = C("#B2B2B2"),
        Divider = C("#D9D9D9"),
        Focus = C("#A8D4FF"),
        FocusText = C("#0D0D0D"),
        Selection = C("#CDE8FF"),
        SelectionText = C("#0D0D0D"),
        EditSurface = C("#F2F2F2"),
        EditText = C("#0D0D0D"),
        Watermark = C("#717171"),
        Error = C("#DF1E11"),
        Success = C("#517D21"),
        Warning = C("#9F6500"),
        AccessKey = C("#C0392B"),
    };

    /// <summary>RDM DarkBlue (the RDM default): navy fills and navy chrome.</summary>
    public static readonly TuiTheme DevolutionsDarkBlue = new()
    {
        Id = "devolutions-dark-blue",
        Name = "Devolutions - Dark Blue",
        Background = C("#161B26"),
        Surface = C("#1C222E"),
        Chrome = C("#0C111D"),
        ChromeText = C("#FFFFFF"),
        MenuBar = C("#0C111D"),
        TabBar = C("#1C222E"),
        DropDown = C("#1C222E"),
        Text = C("#FFFFFF"),
        TextMuted = C("#C2C3C6"),
        TextDim = C("#82858C"),
        Brand = C("#3AA3FF"),
        Border = C("#333947"),
        Divider = C("#2A2E39"),
        Focus = C("#0068C3"),
        FocusText = C("#FFFFFF"),
        Selection = C("#21374F"),
        SelectionText = C("#FFFFFF"),
        EditSurface = C("#272E3C"),
        EditText = C("#FFFFFF"),
        Watermark = C("#96989D"),
        Error = C("#F9766C"),
        Success = C("#BCEA8A"),
        Warning = C("#F7B564"),
        AccessKey = C("#F7B564"),
    };

    /// <summary>RDM DarkBlue fills with Devolutions brand-blue title and menu bars.</summary>
    public static readonly TuiTheme DevolutionsBlue = DevolutionsDarkBlue with
    {
        Id = "devolutions-blue",
        Name = "Devolutions - Blue",
        Chrome = C("#0068C3"),
        MenuBar = C("#054A7A"),
        TabBar = C("#0C111D"),
        DropDown = C("#054A7A"),
        Focus = C("#0068C3"),
        Selection = C("#26415D"),
    };

    /// <summary>RDM DarkGray.</summary>
    public static readonly TuiTheme DevolutionsGray = new()
    {
        Id = "devolutions-gray",
        Name = "Devolutions - Gray",
        Background = C("#4A4A4A"),
        Surface = C("#565656"),
        Chrome = C("#333333"),
        ChromeText = C("#FFFFFF"),
        MenuBar = C("#3D3D3D"),
        TabBar = C("#404040"),
        DropDown = C("#333333"),
        Text = C("#FFFFFF"),
        TextMuted = C("#E4E4E4"),
        TextDim = C("#BBBBBB"),
        Brand = C("#7BC1FF"),
        Border = C("#7C7C7C"),
        Divider = C("#5A5A5A"),
        Focus = C("#0068C3"),
        FocusText = C("#FFFFFF"),
        Selection = C("#616161"),
        SelectionText = C("#FFFFFF"),
        EditSurface = C("#333333"),
        EditText = C("#FFFFFF"),
        Watermark = C("#BDBDBD"),
        Error = C("#FCAFAA"),
        Success = C("#BCEA8A"),
        Warning = C("#F7B564"),
        AccessKey = C("#FFD86B"),
    };

    /// <summary>RDM DarkHighContrast: black, white text at AAA, borders and selection at 3:1 or better.</summary>
    public static readonly TuiTheme DevolutionsHighContrast = new()
    {
        Id = "devolutions-high-contrast",
        Name = "Devolutions - High contrast",
        Background = C("#000000"),
        Surface = C("#1A1A1A"),
        Chrome = C("#141414"),
        ChromeText = C("#FFFFFF"),
        MenuBar = C("#141414"),
        TabBar = C("#000000"),
        DropDown = C("#1A1A1A"),
        Text = C("#FFFFFF"),
        TextMuted = C("#F0F0F0"),
        TextDim = C("#D0D0D0"),
        Brand = C("#3AA3FF"),
        Border = C("#C6C6C6"),
        Divider = C("#8A8A8A"),
        Focus = C("#2D6BB0"),
        FocusText = C("#FFFFFF"),
        Selection = C("#1C4E85"),
        SelectionText = C("#FFFFFF"),
        EditSurface = C("#141414"),
        EditText = C("#FFFFFF"),
        Watermark = C("#D0D0D0"),
        Error = C("#F9766C"),
        Success = C("#BCEA8A"),
        Warning = C("#F7B564"),
        AccessKey = C("#FFFF00"),
    };

    public static readonly TuiTheme Dracula = new()
    {
        Id = "dracula",
        Name = "Dracula",
        Background = C("#282A36"),
        Surface = C("#343746"),
        Chrome = C("#191A21"),
        ChromeText = C("#F8F8F2"),
        MenuBar = C("#21222C"),
        TabBar = C("#21222C"),
        DropDown = C("#343746"),
        Text = C("#F8F8F2"),
        TextMuted = C("#C9CBD6"),
        TextDim = C("#6272A4"),
        Brand = C("#BD93F9"),
        Border = C("#6272A4"),
        Divider = C("#44475A"),
        Focus = C("#6C55A8"),
        FocusText = C("#F8F8F2"),
        Selection = C("#44475A"),
        SelectionText = C("#F8F8F2"),
        EditSurface = C("#21222C"),
        EditText = C("#F8F8F2"),
        Watermark = C("#8BE9FD"),
        Error = C("#FF5555"),
        Success = C("#50FA7B"),
        Warning = C("#FFB86C"),
        AccessKey = C("#FF79C6"),
    };

    public static readonly TuiTheme Monokai = new()
    {
        Id = "monokai",
        Name = "Monokai",
        Background = C("#272822"),
        Surface = C("#34352F"),
        Chrome = C("#1E1F1C"),
        ChromeText = C("#F8F8F2"),
        MenuBar = C("#1E1F1C"),
        TabBar = C("#1E1F1C"),
        DropDown = C("#3E3D32"),
        Text = C("#F8F8F2"),
        TextMuted = C("#CFCFC2"),
        TextDim = C("#75715E"),
        Brand = C("#66D9EF"),
        Border = C("#75715E"),
        Divider = C("#3E3D32"),
        Focus = C("#6D6A55"),
        FocusText = C("#F8F8F2"),
        Selection = C("#49483E"),
        SelectionText = C("#F8F8F2"),
        EditSurface = C("#3E3D32"),
        EditText = C("#F8F8F2"),
        Watermark = C("#A59F85"),
        Error = C("#F92672"),
        Success = C("#A6E22E"),
        Warning = C("#FD971F"),
        AccessKey = C("#E6DB74"),
    };

    public static readonly TuiTheme Nord = new()
    {
        Id = "nord",
        Name = "Nord",
        Background = C("#2E3440"),
        Surface = C("#3B4252"),
        Chrome = C("#242933"),
        ChromeText = C("#ECEFF4"),
        MenuBar = C("#3B4252"),
        TabBar = C("#242933"),
        DropDown = C("#3B4252"),
        Text = C("#ECEFF4"),
        TextMuted = C("#D8DEE9"),
        TextDim = C("#8D96A8"),
        Brand = C("#88C0D0"),
        Border = C("#4C566A"),
        Divider = C("#434C5E"),
        Focus = C("#4A6890"),
        FocusText = C("#ECEFF4"),
        Selection = C("#434C5E"),
        SelectionText = C("#ECEFF4"),
        EditSurface = C("#3B4252"),
        EditText = C("#ECEFF4"),
        Watermark = C("#8FBCBB"),
        Error = C("#BF616A"),
        Success = C("#A3BE8C"),
        Warning = C("#EBCB8B"),
        AccessKey = C("#EBCB8B"),
    };

    public static readonly TuiTheme GruvboxDark = new()
    {
        Id = "gruvbox-dark",
        Name = "Gruvbox Dark",
        Background = C("#282828"),
        Surface = C("#3C3836"),
        Chrome = C("#1D2021"),
        ChromeText = C("#EBDBB2"),
        MenuBar = C("#1D2021"),
        TabBar = C("#32302F"),
        DropDown = C("#3C3836"),
        Text = C("#EBDBB2"),
        TextMuted = C("#D5C4A1"),
        TextDim = C("#928374"),
        Brand = C("#FABD2F"),
        Border = C("#665C54"),
        Divider = C("#504945"),
        Focus = C("#076678"),
        FocusText = C("#FBF1C7"),
        Selection = C("#504945"),
        SelectionText = C("#FBF1C7"),
        EditSurface = C("#3C3836"),
        EditText = C("#FBF1C7"),
        Watermark = C("#A89984"),
        Error = C("#FB4934"),
        Success = C("#B8BB26"),
        Warning = C("#FE8019"),
        AccessKey = C("#8EC07C"),
    };

    public static readonly TuiTheme SolarizedDark = new()
    {
        Id = "solarized-dark",
        Name = "Solarized Dark",
        Background = C("#002B36"),
        Surface = C("#073642"),
        Chrome = C("#001F27"),
        ChromeText = C("#EEE8D5"),
        MenuBar = C("#073642"),
        TabBar = C("#001F27"),
        DropDown = C("#073642"),
        Text = C("#EEE8D5"),
        TextMuted = C("#93A1A1"),
        TextDim = C("#657B83"),
        Brand = C("#2AA198"),
        Border = C("#586E75"),
        Divider = C("#073642"),
        Focus = C("#1D6FA8"),
        FocusText = C("#FDF6E3"),
        Selection = C("#0E4B5A"),
        SelectionText = C("#FDF6E3"),
        EditSurface = C("#073642"),
        EditText = C("#FDF6E3"),
        Watermark = C("#839496"),
        Error = C("#DC322F"),
        Success = C("#859900"),
        Warning = C("#CB4B16"),
        AccessKey = C("#B58900"),
    };

    public static readonly TuiTheme SolarizedLight = new()
    {
        Id = "solarized-light",
        Name = "Solarized Light",
        IsLight = true,
        Background = C("#FDF6E3"),
        Surface = C("#EEE8D5"),
        Chrome = C("#1E6DA6"),
        ChromeText = C("#FDF6E3"),
        MenuBar = C("#EEE8D5"),
        MenuText = C("#073642"),
        TabBar = C("#F5EFDC"),
        DropDown = C("#EEE8D5"),
        Text = C("#073642"),
        TextMuted = C("#586E75"),
        TextDim = C("#93A1A1"),
        Brand = C("#1E6DA6"),
        Border = C("#93A1A1"),
        Divider = C("#E4DDC8"),
        Focus = C("#B8D8EC"),
        FocusText = C("#002B36"),
        Selection = C("#E1DBC5"),
        SelectionText = C("#002B36"),
        EditSurface = C("#EEE8D5"),
        EditText = C("#002B36"),
        Watermark = C("#657B83"),
        Error = C("#DC322F"),
        Success = C("#5F7000"),
        Warning = C("#CB4B16"),
        AccessKey = C("#D33682"),
    };

    public static readonly TuiTheme OneDark = new()
    {
        Id = "one-dark",
        Name = "One Dark",
        Background = C("#282C34"),
        Surface = C("#2C313C"),
        Chrome = C("#21252B"),
        ChromeText = C("#D7DAE0"),
        MenuBar = C("#21252B"),
        TabBar = C("#21252B"),
        DropDown = C("#2C313C"),
        Text = C("#D7DAE0"),
        TextMuted = C("#ABB2BF"),
        TextDim = C("#5C6370"),
        Brand = C("#61AFEF"),
        Border = C("#4B5263"),
        Divider = C("#3E4451"),
        Focus = C("#3D5A80"),
        FocusText = C("#FFFFFF"),
        Selection = C("#3E4451"),
        SelectionText = C("#FFFFFF"),
        EditSurface = C("#21252B"),
        EditText = C("#D7DAE0"),
        Watermark = C("#7F8896"),
        Error = C("#E06C75"),
        Success = C("#98C379"),
        Warning = C("#E5C07B"),
        AccessKey = C("#C678DD"),
    };

    public static readonly TuiTheme TokyoNight = new()
    {
        Id = "tokyo-night",
        Name = "Tokyo Night",
        Background = C("#1A1B26"),
        Surface = C("#24283B"),
        Chrome = C("#16161E"),
        ChromeText = C("#C0CAF5"),
        MenuBar = C("#16161E"),
        TabBar = C("#16161E"),
        DropDown = C("#24283B"),
        Text = C("#C0CAF5"),
        TextMuted = C("#A9B1D6"),
        TextDim = C("#565F89"),
        Brand = C("#7AA2F7"),
        Border = C("#3B4261"),
        Divider = C("#292E42"),
        Focus = C("#3D59A1"),
        FocusText = C("#FFFFFF"),
        Selection = C("#283457"),
        SelectionText = C("#FFFFFF"),
        EditSurface = C("#24283B"),
        EditText = C("#C0CAF5"),
        Watermark = C("#7DCFFF"),
        Error = C("#F7768E"),
        Success = C("#9ECE6A"),
        Warning = C("#FF9E64"),
        AccessKey = C("#E0AF68"),
    };

    public static readonly TuiTheme CatppuccinMocha = new()
    {
        Id = "catppuccin-mocha",
        Name = "Catppuccin Mocha",
        Background = C("#1E1E2E"),
        Surface = C("#313244"),
        Chrome = C("#11111B"),
        ChromeText = C("#CDD6F4"),
        MenuBar = C("#181825"),
        TabBar = C("#181825"),
        DropDown = C("#313244"),
        Text = C("#CDD6F4"),
        TextMuted = C("#BAC2DE"),
        TextDim = C("#7F849C"),
        Brand = C("#89B4FA"),
        Border = C("#585B70"),
        Divider = C("#45475A"),
        Focus = C("#4A5C8A"),
        FocusText = C("#FFFFFF"),
        Selection = C("#45475A"),
        SelectionText = C("#FFFFFF"),
        EditSurface = C("#313244"),
        EditText = C("#CDD6F4"),
        Watermark = C("#A6ADC8"),
        Error = C("#F38BA8"),
        Success = C("#A6E3A1"),
        Warning = C("#FAB387"),
        AccessKey = C("#F9E2AF"),
    };

    /// <summary>Every theme, Devolutions first, in the order the picker shows them.</summary>
    public static readonly IReadOnlyList<TuiTheme> All =
    [
        DevolutionsGraphite, DevolutionsBlack, DevolutionsLight, DevolutionsDarkBlue, DevolutionsBlue, DevolutionsGray, DevolutionsHighContrast,
        Dracula, Monokai, Nord, GruvboxDark, SolarizedDark, SolarizedLight, OneDark, TokyoNight, CatppuccinMocha,
    ];

    public static TuiTheme Default => DevolutionsGraphite;

    /// <summary>The theme with this id or name (case-insensitive), or null.</summary>
    public static TuiTheme? Find(string? idOrName)
        => string.IsNullOrWhiteSpace(idOrName)
            ? null
            : All.FirstOrDefault(t => string.Equals(t.Id, idOrName.Trim(), StringComparison.OrdinalIgnoreCase)
                                      || string.Equals(t.Name, idOrName.Trim(), StringComparison.OrdinalIgnoreCase));
}
