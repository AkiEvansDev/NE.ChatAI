using System.Collections.Generic;
using System.Reflection;

namespace NE.ChatAI;

internal static class AppIcons
{
    public const string Chats = MaterialIcons.Forum;
    public const string Generation = MaterialIcons.Tune;
    public const string Settings = MaterialIcons.Settings;
    public const string Previous = MaterialIcons.ChevronLeft;
    public const string Next = MaterialIcons.ChevronRight;
    public const string Regenerate = MaterialIcons.Refresh;
    public const string Continue = MaterialIcons.ReadMore;
    public const string Variant = MaterialIcons.SwapHoriz;
    public const string DeleteFrom = MaterialIcons.DeleteSweep;
    public const string Branch = MaterialIcons.CallSplit;
    public const string Persona = MaterialIcons.AccountCircle;
    public const string Phrases = MaterialIcons.Quickreply;
    public const string AuthorNotes = MaterialIcons.EditNote;
    public const string Impersonate = MaterialIcons.RecordVoiceOver;
    public const string Tags = MaterialIcons.Sell;
    public const string Statistics = MaterialIcons.BarChart;
    public const string Prompt = MaterialIcons.Code;
    public const string Connect = MaterialIcons.Sync;
    public const string Info = MaterialIcons.Info;
    public const string Scenarios = MaterialIcons.AccountTree;
    public const string Run = MaterialIcons.PlayArrow;
    public const string Gallery = MaterialIcons.PhotoLibrary;
    public const string Stage = MaterialIcons.Image;
    public const string Plugins = MaterialIcons.Extension;
    public const string Context = MaterialIcons.DataUsage;
    public const string TriggerMatch = MaterialIcons.ManageSearch;
    public const string Cue = MaterialIcons.Lightbulb;
    public const string CueMenu = MaterialIcons.Checklist;
    public const string CuePick = MaterialIcons.FactCheck;
    public const string Lock = MaterialIcons.Key;
    public const string Bindings = MaterialIcons.Schema;
    public const string Worlds = MaterialIcons.Public;

    public static string Outline(string icon)
        => MaterialIcons.Outlined(icon);

    public static string[] All()
    {
        List<string> icons = [];

        foreach (FieldInfo field in typeof(AppIcons).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.IsLiteral && field.GetRawConstantValue() is string icon && !icons.Contains(icon))
                icons.Add(icon);
        }

        return [.. icons];
    }
}
