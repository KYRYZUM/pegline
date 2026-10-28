namespace Pegline.App.Shelves;

/// <summary>Maps the core's semantic icon names to Segoe Fluent Icons code points.</summary>
internal static class Glyphs
{
    public const string ChevronLeft = "";
    public const string ChevronRight = "";
    public const string Copy = "";
    public const string Pin = "";
    public const string Pinned = "";
    public const string Edit = "";
    public const string Accept = "";
    public const string Markup = "";
    public const string Command = "";
    public const string Close = "";
    public const string Search = "";
    public const string Link = "";
    public const string OpenExternal = "";

    private static readonly Dictionary<string, string> Map = new()
    {
        ["search"] = Search, ["library"] = "", ["copy"] = Copy, ["paste"] = "", ["preview"] = "",
        ["pin"] = Pin, ["pinned"] = Pinned, ["folder"] = "", ["folderAdd"] = "", ["folderRemove"] = "",
        ["rename"] = "", ["tag"] = "", ["tagRemove"] = "", ["edit"] = Edit, ["check"] = Accept,
        ["openExternal"] = OpenExternal, ["export"] = "", ["trash"] = "", ["remove"] = "",
        ["undo"] = "", ["redo"] = "", ["panes"] = "", ["markup"] = Markup, ["crop"] = "",
        ["cursor"] = "", ["pen"] = "", ["highlighter"] = Markup, ["rectangle"] = "", ["ellipse"] = "",
        ["arrow"] = "", ["lineTool"] = "", ["font"] = "", ["ocr"] = "", ["image"] = "",
        ["text"] = "", ["link"] = Link, ["shelves"] = "", ["switch"] = "", ["close"] = Close,
        ["left"] = "", ["right"] = "", ["first"] = "", ["last"] = "", ["pagePrevious"] = ChevronLeft,
        ["pageNext"] = ChevronRight, ["up"] = "", ["down"] = "", ["selection"] = "", ["settings"] = "",
        ["sliders"] = "", ["history"] = "", ["sources"] = "", ["keyboard"] = "", ["info"] = "",
        ["save"] = "", ["inbox"] = "", ["clock"] = "", ["grid"] = "", ["list"] = "",
        ["sidebar"] = "", ["inspector"] = "", ["fullscreen"] = "", ["filter"] = "", ["filterClear"] = "",
        ["sort"] = "", ["calendar"] = "", ["refresh"] = "", ["desktop"] = "", ["trim"] = "",
        ["eraser"] = "", ["download"] = "", ["power"] = "", ["command"] = Command, ["add"] = "",
    };

    public static string For(string symbol) => Map.GetValueOrDefault(symbol, Command);
}
