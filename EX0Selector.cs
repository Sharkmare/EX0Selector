using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.ProtoFlux;
using FrooxEngine.UIX;
using HarmonyLib;
using ProtoFlux.Core;
using ResoniteModLoader;

namespace EX0Selector;

public class EX0Selector : ResoniteMod
{
    public override string Name => "EX0Selector";
    public override string Author => "Sharkmare";
    public override string Version => "1.0";
    public override string Link => "https://github.com/Sharkmare/EX0Selector";

    [AutoRegisterConfigKey]
    internal static readonly ModConfigurationKey<bool> ENABLED = new(
        "Enabled", "Use the restructured node browser / component attacher with search", () => true);

    [AutoRegisterConfigKey]
    internal static readonly ModConfigurationKey<int> MAX_RESULTS = new(
        "Max results", "How many search hits to list", () => 60, valueValidator: v => v is >= 5 and <= 400);

    [AutoRegisterConfigKey]
    internal static readonly ModConfigurationKey<int> RECENT_COUNT = new(
        "Recent count", "How many recently picked types to show at the top of the root page (0 hides the section)", () => 8, valueValidator: v => v is >= 0 and <= 40);

    [AutoRegisterConfigKey]
    internal static readonly ModConfigurationKey<bool> SEARCH_SYMBOLS = new(
        "Search node symbols", "Let a query match a ProtoFlux node's visual symbol (\"+\" finds ValueAdd)", () => true);

    [AutoRegisterConfigKey]
    internal static readonly ModConfigurationKey<int> COLUMNS = new(
        "Columns", "Category panes shown side by side; each depth gets its own pane (1 = one list that replaces itself)", () => 3, valueValidator: v => v is >= 1 and <= 5);

    [AutoRegisterConfigKey]
    internal static readonly ModConfigurationKey<int> COLUMN_WIDTH = new(
        "Column width", "Width of each pane in canvas units", () => 440, valueValidator: v => v is >= 240 and <= 900);

    private static ModConfiguration? _config;
    internal static bool Enabled => _config?.GetValue(ENABLED) ?? true;
    internal static int MaxResults => _config?.GetValue(MAX_RESULTS) ?? 60;
    internal static int RecentCount => _config?.GetValue(RECENT_COUNT) ?? 8;
    internal static bool SearchSymbols => _config?.GetValue(SEARCH_SYMBOLS) ?? true;
    internal static int Columns => _config?.GetValue(COLUMNS) ?? 3;
    internal static float ColumnWidth => _config?.GetValue(COLUMN_WIDTH) ?? 440;

    public override void OnEngineInit()
    {
        _config = GetConfiguration();
        new Harmony("dev.ex0.selector").PatchAll(Assembly.GetExecutingAssembly());
        Msg("ComponentSelector (node browser + component attacher) restructured with search.");
    }

    internal static void LogError(string message)
    {
        Error(message);
        UniLog.Error("[EX0Selector] " + message);
    }
}

internal sealed class Entry
{
    public Type Type = null!;
    public string Name = "";
    public string NameLower = "";
    public string Humps = "";
    public string? Symbol;
    public string? SymbolLower;
    public string Category = "";
    public string CategoryLower = "";
    public string CategoryFull = "";
}

internal static class Catalog
{
    private static readonly Dictionary<string, List<Entry>> _cache = new();
    private static Dictionary<Type, Type>? _bindingToRuntime;

    public static string Normalize(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        return path!.Replace('\\', '/').Trim('/');
    }

    public static List<Entry> For(string scope)
    {
        scope = Normalize(scope);
        if (_cache.TryGetValue(scope, out var cached)) return cached;
        var library = WorkerInitializer.ComponentLibrary;
        var root = scope.Length == 0 ? library : (library.GetSubcategory(scope) ?? library);
        var list = new List<Entry>();
        Walk(root, "", Normalize(root.GetPath()), list);
        _cache[scope] = list;
        return list;
    }

    private static void Walk(CategoryNode<Type> node, string relative, string absolute, List<Entry> into)
    {
        foreach (var type in node.Elements)
            into.Add(Make(type, relative, absolute));
        foreach (var sub in node.Subcategories)
        {
            if (sub.IsEmpty) continue;
            var rel = relative.Length == 0 ? sub.Name : relative + "/" + sub.Name;
            var abs = absolute.Length == 0 ? sub.Name : absolute + "/" + sub.Name;
            Walk(sub, rel, abs, into);
        }
    }

    private static Entry Make(Type type, string relative, string absolute)
    {
        var name = type.GetNiceName();
        var symbol = SymbolOf(type);
        return new Entry
        {
            Type = type,
            Name = name,
            NameLower = name.ToLowerInvariant(),
            Humps = Humps(name),
            Symbol = symbol,
            SymbolLower = symbol?.ToLowerInvariant(),
            Category = relative,
            CategoryLower = relative.ToLowerInvariant(),
            CategoryFull = absolute,
        };
    }

    private static string Humps(string name)
    {
        var chars = new List<char>();
        bool boundary = true;
        foreach (var c in name)
        {
            if (c == '<') break;
            if (!char.IsLetterOrDigit(c)) { boundary = true; continue; }
            if (boundary || char.IsUpper(c) || char.IsDigit(c)) chars.Add(char.ToLowerInvariant(c));
            boundary = false;
        }
        return new string(chars.ToArray());
    }

    private static string? SymbolOf(Type bindingType)
    {
        try
        {
            _bindingToRuntime ??= InvertBindingMap();
            if (!_bindingToRuntime.TryGetValue(bindingType, out var runtime)) return null;
            var attr = runtime.GetCustomAttribute<NodeNameAttribute>();
            if (attr == null || string.IsNullOrWhiteSpace(attr.Name)) return null;

            return string.Equals(attr.Name, bindingType.Name, StringComparison.OrdinalIgnoreCase) ? null : attr.Name;
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<Type, Type> InvertBindingMap()
    {
        var result = new Dictionary<Type, Type>();
        var field = AccessTools.Field(typeof(ProtoFluxHelper), "protoFluxToBindingMapping");
        if (field?.GetValue(null) is Dictionary<Type, Type> map)
            foreach (var pair in map)
                result[pair.Value] = pair.Key;
        return result;
    }
}

internal static class Fuzzy
{

    public static int Score(Entry e, string[] tokens, bool useSymbols)
    {
        int total = 0;
        foreach (var token in tokens)
        {
            int s = ScoreToken(e, token, useSymbols);
            if (s < 0) return -1;
            total += s;
        }
        return total;
    }

    private static int ScoreToken(Entry e, string t, bool useSymbols)
    {
        int best = -1;

        if (e.NameLower == t) best = 1000;
        else if (e.NameLower.StartsWith(t, StringComparison.Ordinal)) best = 600 - Math.Min(e.NameLower.Length - t.Length, 200);
        else
        {
            int idx = e.NameLower.IndexOf(t, StringComparison.Ordinal);
            if (idx >= 0) best = 350 + (IsWordStart(e.Name, idx) ? 60 : 0) - Math.Min(idx, 100);
        }

        if (useSymbols && e.SymbolLower != null)
        {
            if (e.SymbolLower == t) best = Math.Max(best, 900);
            else if (e.SymbolLower.Contains(t)) best = Math.Max(best, 400);
        }

        if (e.Humps.Length > 0)
        {
            if (e.Humps.StartsWith(t, StringComparison.Ordinal)) best = Math.Max(best, 300 - Math.Min(e.Humps.Length - t.Length, 100));
            else if (e.Humps.Contains(t)) best = Math.Max(best, 200);
        }

        int gaps = SubsequenceGaps(e.NameLower, t);
        if (gaps >= 0) best = Math.Max(best, 120 - Math.Min(gaps, 100));

        if (e.CategoryLower.Length > 0 && e.CategoryLower.Contains(t)) best = Math.Max(best, 50);

        if (best >= 0 && e.CategoryLower.StartsWith("protoflux", StringComparison.Ordinal)) best -= 30;

        return best;
    }

    private static bool IsWordStart(string original, int idx)
    {
        if (idx == 0) return true;
        char prev = original[idx - 1], cur = original[idx];
        if (!char.IsLetterOrDigit(prev)) return true;
        return char.IsUpper(cur) && char.IsLower(prev);
    }

    private static int SubsequenceGaps(string text, string token)
    {
        int ti = 0, gaps = 0, lastMatch = -1;
        for (int i = 0; i < text.Length && ti < token.Length; i++)
        {
            if (text[i] != token[ti]) continue;
            if (lastMatch >= 0) gaps += i - lastMatch - 1;
            lastMatch = i;
            ti++;
        }
        return ti == token.Length ? gaps : -1;
    }
}

internal static class Recents
{
    private static readonly Dictionary<string, List<Type>> _byScope = new();

    public static IReadOnlyList<Type> Get(string scope)
        => _byScope.TryGetValue(scope, out var list) ? list : Array.Empty<Type>();

    public static void Push(string scope, Type type)
    {
        if (!_byScope.TryGetValue(scope, out var list))
            _byScope[scope] = list = new List<Type>();
        list.Remove(type);
        list.Insert(0, type);
        int cap = Math.Max(EX0Selector.RecentCount, 1) * 2;
        if (list.Count > cap) list.RemoveRange(cap, list.Count - cap);
    }
}

internal sealed class Column
{
    public string Key = "";
    public Slot Root = null!;
    public Slot Content = null!;
    public readonly Dictionary<string, (Button button, colorX tint, colorX baseColor)> Items = new();
    public readonly List<Slot> RecentRows = new();
    public bool ShowsRecent;
    public string RecentPath = "";
    public string? Selected;
}

internal sealed class SelectorState
{
    public TextField Search = null!;
    public Text Crumb = null!;
    public Canvas Canvas = null!;
    public float2 BaseSize;
    public Slot ColumnsRoot = null!;
    public Column SearchPane = null!;
    public readonly List<Column> Columns = new();
    public string Path = "";
    public string? Group;
    public bool GenericPage;
    public string GenericPath = "";
}

internal static class States
{
    private static readonly ConditionalWeakTable<ComponentSelector, SelectorState> _table = new();

    public static bool TryGet(ComponentSelector selector, out SelectorState state) => _table.TryGetValue(selector, out state!);

    public static void Set(ComponentSelector selector, SelectorState state)
    {
        _table.Remove(selector);
        _table.Add(selector, state);
    }
}

[HarmonyPatch(typeof(ComponentSelector))]
internal static class SelectorPatches
{
    private const float HEADER_HEIGHT = 96f;
    private const float ROW_HEIGHT = 32f;
    private const float RESULT_HEIGHT = 46f;
    private const float COLUMN_GAP = 8f;
    private const long RECENT_ORDER_BASE = -100000L;
    private const float PANEL_SIDE_PADDING = 32f;
    private const string DIM_HEX = RadiantUI_Constants.Neutrals.MIDLIGHT_HEX;
    private const string LIGHT_HEX = RadiantUI_Constants.Neutrals.LIGHT_HEX;
    private static readonly colorX COLUMN_BG = colorX.FromHexCode("#181d26");

    private static readonly AccessTools.FieldRef<ComponentSelector, SyncRef<Slot>> UiRoot =
        AccessTools.FieldRefAccess<ComponentSelector, SyncRef<Slot>>("_uiRoot");
    private static readonly AccessTools.FieldRef<ComponentSelector, Sync<string>> RootPath =
        AccessTools.FieldRefAccess<ComponentSelector, Sync<string>>("_rootPath");
    private static readonly AccessTools.FieldRef<ComponentSelector, SyncType> GenericType =
        AccessTools.FieldRefAccess<ComponentSelector, SyncType>("_genericType");
    private static readonly AccessTools.FieldRef<ComponentSelector, SyncRefList<TextField>> CustomArgs =
        AccessTools.FieldRefAccess<ComponentSelector, SyncRefList<TextField>>("_customGenericArguments");

    private static readonly MethodInfo? OpenCategoryMethod = AccessTools.Method(typeof(ComponentSelector), "OnOpenCategoryPressed");
    private static readonly MethodInfo? OpenGenericMethod = AccessTools.Method(typeof(ComponentSelector), "OpenGenericTypesPressed");
    private static readonly MethodInfo? OpenGroupMethod = AccessTools.Method(typeof(ComponentSelector), "OpenGroupPressed");
    private static readonly MethodInfo? CancelMethod = AccessTools.Method(typeof(ComponentSelector), "OnCancelPressed");

    private static bool _broken;

    private static bool Active => EX0Selector.Enabled && !_broken
        && OpenCategoryMethod != null && OpenGenericMethod != null && OpenGroupMethod != null && CancelMethod != null;

    private readonly record struct Step(string Key, string Kind, string Path, string? Group);

    [HarmonyPrefix]
    [HarmonyPatch(nameof(ComponentSelector.SetupUI))]
    private static bool SetupUI(ComponentSelector __instance, LocaleString title, float2 size)
    {
        if (!Active) return true;
        try
        {
            BuildPanel(__instance, title, size);
            return false;
        }
        catch (Exception ex)
        {
            _broken = true;
            EX0Selector.LogError("panel build failed, falling back to the stock selector for the rest of this session:\n" + ex);
            return true;
        }
    }

    private static void BuildPanel(ComponentSelector selector, LocaleString title, float2 size)
    {
        var state = new SelectorState { BaseSize = size };
        UIBuilder ui = RadiantUI_Panel.SetupPanel(selector.Slot, title, size);
        state.Canvas = selector.Slot.GetComponent<Canvas>();
        RadiantUI_Constants.SetupEditorStyle(ui, extraPadding: true);
        ui.Style.TextAlignment = Alignment.MiddleLeft;
        ui.Style.ForceExpandHeight = false;
        ui.HorizontalHeader(HEADER_HEIGHT, out RectTransform header, out RectTransform content);

        var hb = new UIBuilder(header);
        RadiantUI_Constants.SetupEditorStyle(hb, extraPadding: true);
        hb.Style.TextAlignment = Alignment.MiddleLeft;
        hb.Style.ButtonTextAlignment = Alignment.MiddleCenter;
        hb.Style.ForceExpandWidth = false;
        hb.Style.ForceExpandHeight = true;
        hb.VerticalLayout(6f, 0f, 0f, 8f, 0f, null, forceExpandWidth: true, forceExpandHeight: false);

        hb.Style.MinHeight = 40f;
        hb.Style.PreferredHeight = 40f;
        hb.HorizontalLayout(6f);
        {
            hb.Style.FlexibleWidth = 1f;
            hb.Style.MinWidth = -1f;
            state.Search = hb.TextField("", undo: false, null, parseRTF: false, promptText: (LocaleString)"Search…");
            hb.Style.FlexibleWidth = -1f;
            hb.Style.MinWidth = 44f;
            hb.Style.PreferredWidth = 44f;
            var clear = hb.Button((LocaleString)"✕", RadiantUI_Constants.Sub.RED);
            clear.LocalPressed += (_, _) => { ClearSearch(state); Rebuild(selector, state); };
        }
        hb.NestOut();

        hb.Style.MinHeight = ROW_HEIGHT;
        hb.Style.PreferredHeight = ROW_HEIGHT;
        hb.HorizontalLayout(6f);
        {
            hb.Style.FlexibleWidth = -1f;
            hb.Style.MinWidth = 96f;
            hb.Style.PreferredWidth = 96f;
            var back = hb.Button((LocaleString)"◀ Back", RadiantUI_Constants.Sub.YELLOW);
            back.LocalPressed += (_, _) => GoBack(selector, state);
            hb.Style.MinWidth = 80f;
            hb.Style.PreferredWidth = 80f;
            var home = hb.Button((LocaleString)"Home", RadiantUI_Constants.Sub.ORANGE);
            home.LocalPressed += (_, _) => GoHome(selector, state);
            hb.Style.FlexibleWidth = 1f;
            hb.Style.MinWidth = -1f;
            hb.Style.PreferredWidth = -1f;
            state.Crumb = hb.Text((LocaleString)"", bestFit: true, Alignment.MiddleLeft, parseRTF: true);
        }
        hb.NestOut();
        hb.NestOut();

        var cb = new UIBuilder(content);
        RadiantUI_Constants.SetupEditorStyle(cb, extraPadding: true);
        cb.Style.ForceExpandHeight = true;
        cb.Style.ForceExpandWidth = false;
        cb.HorizontalLayout(COLUMN_GAP);
        state.ColumnsRoot = cb.Root;

        state.SearchPane = MakeColumn(state, "search");
        state.SearchPane.Root.ActiveSelf = false;
        UiRoot(selector).Target = state.SearchPane.Content;

        States.Set(selector, state);
        var editor = state.Search.Editor.Target;
        if (editor != null)
            editor.LocalEditingChanged += _ => OnSearchChanged(selector, state);

        selector.BuildUI(null);
    }

    private static Column MakeColumn(SelectorState state, string key)
    {
        var ui = new UIBuilder(state.ColumnsRoot);
        RadiantUI_Constants.SetupEditorStyle(ui, extraPadding: true);
        ui.Style.MinWidth = -1f;
        ui.Style.PreferredWidth = EX0Selector.ColumnWidth;
        ui.Style.FlexibleWidth = 1f;
        ui.Style.MinHeight = -1f;
        ui.Style.FlexibleHeight = 1f;
        Slot root = ui.Next("Column");
        ui.Nest();
        ui.Style.FlexibleWidth = -1f;
        ui.Style.FlexibleHeight = -1f;
        ui.Style.TextAlignment = Alignment.MiddleLeft;
        ui.Style.ForceExpandHeight = false;
        ui.Panel(COLUMN_BG);
        ui.ScrollArea();
        ui.VerticalLayout(8f, 8f);
        ui.FitContent(SizeFit.Disabled, SizeFit.MinSize);
        return new Column { Key = key, Root = root, Content = ui.Root };
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(ComponentSelector.BuildUI))]
    private static bool BuildUI(ComponentSelector __instance, string path, bool genericType, string group, bool doNotGenerateBack)
    {
        if (!States.TryGet(__instance, out var state)) return true;
        if (path != null && path.StartsWith("/")) path = path.Substring(1);
        path ??= "";
        if (doNotGenerateBack) RootPath(__instance).Value = path;

        state.GenericPage = genericType;
        state.GenericPath = genericType ? path : "";
        state.Path = genericType ? PathUtility.GetDirectoryName(path).Replace('\\', '/') : path;
        state.Group = group;
        try
        {
            var chain = Chain(Scope(__instance), path, group, genericType);
            Column last = Reconcile(__instance, state, chain);
            UiRoot(__instance).Target = last.Content;

            string query = CurrentQuery(state);
            if (query.Length > 0)
            {
                RenderSearch(__instance, state, query);
                return false;
            }
            Type? genType = genericType ? Type.GetType(PathUtility.GetFileName(path)) : null;
            UpdateCrumb(__instance, state, state.Path, group, genType?.GetNiceName());

            return genericType;
        }
        catch (Exception ex)
        {
            EX0Selector.LogError("listing failed, showing the stock listing for this page:\n" + ex);
            return true;
        }
    }

    private static List<Step> Chain(string scope, string path, string? group, bool generic)
    {
        var steps = new List<Step>();
        string catPath = Catalog.Normalize(generic ? PathUtility.GetDirectoryName(path).Replace('\\', '/') : path);
        string rel = catPath;
        if (scope.Length > 0 && rel.StartsWith(scope, StringComparison.OrdinalIgnoreCase))
            rel = rel.Substring(scope.Length).Trim('/');
        else if (scope.Length > 0)
            rel = "";

        string current = scope;
        steps.Add(new Step("cat:" + current, "cat", current, null));
        if (rel.Length > 0)
            foreach (var segment in rel.Split('/'))
            {
                current = current.Length == 0 ? segment : current + "/" + segment;
                steps.Add(new Step("cat:" + current, "cat", current, null));
            }
        if (!string.IsNullOrEmpty(group))
            steps.Add(new Step("grp:" + current + ":" + group, "grp", current, group));
        if (generic)
            steps.Add(new Step("gen:" + Catalog.Normalize(path), "gen", path, group));
        return steps;
    }

    private static Column Reconcile(ComponentSelector selector, SelectorState state, List<Step> chain)
    {
        int keep = 0;
        while (keep < state.Columns.Count && keep < chain.Count && state.Columns[keep].Key == chain[keep].Key) keep++;
        for (int i = state.Columns.Count - 1; i >= keep; i--)
        {
            state.Columns[i].Root.Destroy();
            state.Columns.RemoveAt(i);
        }
        for (int i = keep; i < chain.Count; i++)
        {
            var column = MakeColumn(state, chain[i].Key);
            state.Columns.Add(column);
            if (chain[i].Kind != "gen")
                RenderBrowse(selector, state, column, chain[i].Path, chain[i].Group, last: i == chain.Count - 1);
        }
        for (int i = 0; i < state.Columns.Count; i++)
            SetSelected(state.Columns[i], i + 1 < chain.Count ? chain[i + 1].Key : null);
        ShowColumns(state);
        return state.Columns[^1];
    }

    private static void SetSelected(Column column, string? key)
    {
        if (column.Selected == key) return;
        if (column.Selected != null && column.Items.TryGetValue(column.Selected, out var old))
            old.button.BaseColor.Value = old.baseColor;
        column.Selected = key;
        if (key != null && column.Items.TryGetValue(key, out var now))
            now.button.BaseColor.Value = Highlight(now.tint);
    }

    private static colorX Highlight(colorX tint)
    {
        const float t = 0.4f;
        static float Factor(float c) => c <= 0.001f ? 1f : (c + (1f - c) * t) / c;
        return new colorX(Factor(tint.r), Factor(tint.g), Factor(tint.b), 1f);
    }

    private static void ShowColumns(SelectorState state)
    {
        int max = Math.Max(1, EX0Selector.Columns);
        int count = state.Columns.Count;
        int first = Math.Max(0, count - max);
        for (int i = 0; i < count; i++)
            state.Columns[i].Root.ActiveSelf = i >= first;
        state.SearchPane.Root.ActiveSelf = false;
        SetWidth(state, Math.Min(count, max));
    }

    private static void ShowSearch(SelectorState state)
    {
        foreach (var column in state.Columns) column.Root.ActiveSelf = false;
        state.SearchPane.Root.ActiveSelf = true;
        SetWidth(state, Math.Min(2, Math.Max(1, EX0Selector.Columns)));
    }

    private static void SetWidth(SelectorState state, int visible)
    {
        float width = state.BaseSize.x;
        if (EX0Selector.Columns > 1 && visible > 1)
            width = Math.Max(width, PANEL_SIDE_PADDING + visible * EX0Selector.ColumnWidth + (visible - 1) * COLUMN_GAP);
        if (state.Canvas != null && state.Canvas.Size.Value.x != width)
            state.Canvas.Size.Value = new float2(width, state.BaseSize.y);
    }

    private static string Scope(ComponentSelector selector) => Catalog.Normalize(RootPath(selector).Value);

    private static bool IsFluxScope(ComponentSelector selector)
        => Scope(selector).StartsWith("ProtoFlux", StringComparison.OrdinalIgnoreCase);

    private static string CurrentQuery(SelectorState state) => state.Search?.TargetString?.Trim() ?? "";

    private static void ClearSearch(SelectorState state)
    {
        if (state.Search != null && !string.IsNullOrEmpty(state.Search.TargetString))
            state.Search.TargetString = "";
    }

    private static void Rebuild(ComponentSelector selector, SelectorState state)
    {
        if (state.GenericPage) selector.BuildUI(state.GenericPath, genericType: true, state.Group);
        else selector.BuildUI(state.Path, genericType: false, state.Group);
    }

    private static void OnSearchChanged(ComponentSelector selector, SelectorState state)
    {
        try
        {
            string query = CurrentQuery(state);
            if (query.Length > 0) RenderSearch(selector, state, query);
            else Rebuild(selector, state);
        }
        catch (Exception ex)
        {
            EX0Selector.LogError("search failed:\n" + ex);
        }
    }

    private static void GoBack(ComponentSelector selector, SelectorState state)
    {
        ClearSearch(state);
        if (state.GenericPage)
        {
            selector.BuildUI(state.Path, genericType: false, state.Group);
            return;
        }
        if (state.Group != null)
        {
            selector.BuildUI(state.Path, genericType: false, null);
            return;
        }
        string scope = Scope(selector);
        string current = Catalog.Normalize(state.Path);
        if (current == scope || current.Length < scope.Length)
        {
            Rebuild(selector, state);
            return;
        }
        int cut = current.LastIndexOf('/');
        string parent = cut < 0 ? "" : current.Substring(0, cut);
        if (parent.Length < scope.Length) parent = scope;
        selector.BuildUI(parent, genericType: false, null);
    }

    private static void GoHome(ComponentSelector selector, SelectorState state)
    {
        ClearSearch(state);
        selector.BuildUI(Scope(selector), genericType: false, null);
    }

    private static void UpdateCrumb(ComponentSelector selector, SelectorState state, string path, string? group, string? leaf)
    {
        if (state.Crumb == null) return;
        string scope = Scope(selector);
        string rel = Catalog.Normalize(path);
        if (scope.Length > 0 && rel.StartsWith(scope, StringComparison.OrdinalIgnoreCase))
            rel = rel.Substring(scope.Length).Trim('/');
        var parts = new List<string> { IsFluxScope(selector) ? "Nodes" : "Components" };
        if (rel.Length > 0) parts.AddRange(rel.Split('/'));
        if (group != null) parts.Add(group.Split('.').Last());
        if (leaf != null) parts.Add(leaf);
        var pieces = new List<string>();
        for (int i = 0; i < parts.Count; i++)
        {
            bool last = i == parts.Count - 1;
            pieces.Add($"<color={(last ? LIGHT_HEX : DIM_HEX)}>{NoParse(parts[i])}</color>");
        }
        state.Crumb.Content.Value = string.Join($"<color={DIM_HEX}> › </color>", pieces);
    }

    private static void RenderBrowse(ComponentSelector selector, SelectorState state, Column column, string path, string? group, bool last)
    {
        column.Content.DestroyChildren();
        column.Items.Clear();
        column.Selected = null;
        var library = WorkerInitializer.ComponentLibrary;
        CategoryNode<Type> root = string.IsNullOrEmpty(path) ? library : (library.GetSubcategory(path) ?? library);
        if (root == library) path = "";
        string scope = Scope(selector);
        bool atScopeRoot = Catalog.Normalize(path) == scope;

        var ui = NewListBuilder(column.Content);
        long order = 0;

        column.ShowsRecent = atScopeRoot && group == null;
        column.RecentPath = path;
        column.RecentRows.Clear();
        if (column.ShowsRecent)
            RenderRecent(selector, column, ui);

        if (group == null)
        {
            var categories = root.Subcategories.Where(c => !c.IsEmpty).ToList();
            if (categories.Count > 0)
            {
                Heading(ui, "Categories").OrderOffset = order++;
                foreach (var cat in categories)
                {
                    string childPath = path + "/" + cat.Name;
                    string label = $"{NoParse(cat.Name)} <color={DIM_HEX}>({cat.TotalElementCount})</color>  ›";
                    var button = ui.Button((LocaleString)label, RadiantUI_Constants.Sub.YELLOW,
                        Handler<string>(selector, OpenCategoryMethod!), childPath, 0.35f);
                    button.Slot.OrderOffset = order++;
                    column.Items["cat:" + Catalog.Normalize(childPath)] = (button, RadiantUI_Constants.Sub.YELLOW, button.BaseColor.Value);
                }
            }
        }

        var elements = root.Elements.Where(t => Allowed(selector, t)).ToList();
        var groupCounter = new Dictionary<string, int>();
        if (group == null)
            foreach (var type in elements)
            {
                var g = type.GetCustomAttribute<GroupingAttribute>()?.GroupName;
                if (g != null) groupCounter[g] = groupCounter.TryGetValue(g, out var n) ? n + 1 : 1;
            }

        var rows = new List<Button>();
        var generatedGroups = new HashSet<string>();
        foreach (var type in elements)
        {
            var grouping = type.GetCustomAttribute<GroupingAttribute>();
            if (group != null && grouping?.GroupName != group) continue;

            if (group == null && grouping != null && groupCounter[grouping.GroupName] > 1)
            {
                if (!generatedGroups.Add(grouping.GroupName)) continue;
                string name = grouping.GroupName.Split('.').Last();
                var button = ui.Button((LocaleString)$"{NoParse(name)} <color={DIM_HEX}>({groupCounter[grouping.GroupName]})</color>  ›",
                    RadiantUI_Constants.Sub.PURPLE, Handler<string>(selector, OpenGroupMethod!),
                    path + ":" + grouping.GroupName, 0.35f);
                column.Items["grp:" + Catalog.Normalize(path) + ":" + grouping.GroupName] = (button, RadiantUI_Constants.Sub.PURPLE, button.BaseColor.Value);
                rows.Add(button);
            }
            else
            {
                rows.Add(ElementButton(ui, selector, column, type, path, group));
            }
        }

        if (rows.Count > 0)
        {
            string title = group != null ? group.Split('.').Last()
                : IsFluxScope(selector) ? "Nodes" : "Components";
            Heading(ui, title).OrderOffset = order++;
            rows.Sort((a, b) => string.CompareOrdinal(StripMarkup(a.LabelText), StripMarkup(b.LabelText)));
            foreach (var row in rows) row.Slot.OrderOffset = order++;
        }
        else if (group == null && root.Subcategories.All(c => c.IsEmpty))
        {
            Note(ui, "Nothing here.").OrderOffset = order++;
        }

        if (last && EX0Selector.Columns <= 1)
            CancelButton(ui, selector);
    }

    private static void RenderSearch(ComponentSelector selector, SelectorState state, string query)
    {
        var pane = state.SearchPane;
        pane.Content.DestroyChildren();
        CustomArgs(selector).Clear();
        GenericType(selector).Value = null;
        ShowSearch(state);

        string scope = Scope(selector);
        var entries = Catalog.For(scope);
        var tokens = query.ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        bool symbols = EX0Selector.SearchSymbols;

        var hits = new List<(Entry entry, int score)>();
        foreach (var entry in entries)
        {
            int score = Fuzzy.Score(entry, tokens, symbols);
            if (score < 0 || !Allowed(selector, entry.Type)) continue;
            hits.Add((entry, score));
        }
        int total = hits.Count;
        hits.Sort((a, b) =>
        {
            int c = b.score.CompareTo(a.score);
            if (c != 0) return c;
            c = a.entry.Name.Length.CompareTo(b.entry.Name.Length);
            return c != 0 ? c : string.CompareOrdinal(a.entry.Name, b.entry.Name);
        });
        if (hits.Count > EX0Selector.MaxResults) hits.RemoveRange(EX0Selector.MaxResults, hits.Count - EX0Selector.MaxResults);

        state.Crumb.Content.Value = total == 0
            ? $"<color={DIM_HEX}>no matches for </color><color={LIGHT_HEX}>{NoParse(query)}</color>"
            : $"<color={LIGHT_HEX}>{hits.Count}</color><color={DIM_HEX}> of {total} matches</color>";

        var ui = NewListBuilder(pane.Content);
        long order = 0;
        if (hits.Count > 0)
        {
            Heading(ui, "Results").OrderOffset = order++;
            ui.Style.MinHeight = RESULT_HEIGHT;
            foreach (var (entry, _) in hits)
            {
                string label = NoParse(entry.Name);
                if (entry.Symbol != null) label += $"  <color={DIM_HEX}>{NoParse(entry.Symbol)}</color>";
                string where = entry.Category.Length > 0 ? entry.Category.Replace("/", " › ") : "(root)";
                label += $"\n<size=70%><color={DIM_HEX}>{NoParse(where)}</color></size>";

                bool generic = entry.Type.IsGenericTypeDefinition;
                var button = ui.Button((LocaleString)label, generic ? RadiantUI_Constants.Sub.GREEN : RadiantUI_Constants.Sub.CYAN);
                button.Slot.OrderOffset = order++;
                var captured = entry;
                button.LocalPressed += (_, _) => PickSearchHit(selector, state, captured);
            }
            ui.Style.MinHeight = ROW_HEIGHT;
        }
        else
        {
            Note(ui, "No nodes or categories match. Try fewer letters, the initials (\"va\" for ValueAdd), or a symbol like +.").OrderOffset = order++;
        }
    }

    private static void PickSearchHit(ComponentSelector selector, SelectorState state, Entry entry)
    {
        if (entry.Type.IsGenericTypeDefinition)
        {
            ClearSearch(state);
            string page = Path.Combine(entry.CategoryFull, entry.Type.AssemblyQualifiedName!).Replace('\\', '/');
            selector.BuildUI(page, genericType: true, null);
            return;
        }
        Select(selector, entry.Type);
    }

    private static UIBuilder NewListBuilder(Slot content)
    {
        var ui = new UIBuilder(content);
        RadiantUI_Constants.SetupEditorStyle(ui, extraPadding: true);
        ui.Style.TextAlignment = Alignment.MiddleLeft;
        ui.Style.ButtonTextAlignment = Alignment.MiddleLeft;
        ui.Style.MinHeight = ROW_HEIGHT;
        return ui;
    }

    private static bool Allowed(ComponentSelector selector, Type type)
    {
        if (!selector.World.Types.IsSupported(type)) return false;
        var filter = selector.ComponentFilter.Target;
        return filter == null || filter(type);
    }

    private static Button ElementButton(UIBuilder ui, ComponentSelector selector, Column column, Type type, string path, string? group)
    {
        if (type.IsGenericTypeDefinition)
        {
            string page = Path.Combine(path, type.AssemblyQualifiedName!).Replace('\\', '/');
            string key = "gen:" + Catalog.Normalize(page);
            if (group != null) page += "?" + group;
            var generic = ui.Button((LocaleString)NoParse(type.GetNiceName()), RadiantUI_Constants.Sub.GREEN,
                Handler<string>(selector, OpenGenericMethod!), page, 0.35f);
            column.Items[key] = (generic, RadiantUI_Constants.Sub.GREEN, generic.BaseColor.Value);
            return generic;
        }
        var button = ui.Button((LocaleString)NoParse(type.GetNiceName()), RadiantUI_Constants.Sub.CYAN);
        button.LocalPressed += (_, _) => Select(selector, type);
        return button;
    }

    private static void RenderRecent(ComponentSelector selector, Column column, UIBuilder ui)
    {
        foreach (var row in column.RecentRows) row.Destroy();
        column.RecentRows.Clear();
        if (EX0Selector.RecentCount <= 0) return;
        var recent = Recents.Get(Scope(selector)).Where(t => Allowed(selector, t)).Take(EX0Selector.RecentCount).ToList();
        if (recent.Count == 0) return;
        long order = RECENT_ORDER_BASE;
        var heading = Heading(ui, "Recent");
        heading.OrderOffset = order++;
        column.RecentRows.Add(heading);
        foreach (var type in recent)
        {
            var row = ElementButton(ui, selector, column, type, column.RecentPath, null).Slot;
            row.OrderOffset = order++;
            column.RecentRows.Add(row);
        }
    }

    private static void RefreshRecent(ComponentSelector selector, SelectorState state)
    {
        foreach (var column in state.Columns)
        {
            if (!column.ShowsRecent) continue;
            RenderRecent(selector, column, NewListBuilder(column.Content));
            if (column.Selected != null && column.Items.TryGetValue(column.Selected, out var item))
                item.button.BaseColor.Value = Highlight(item.tint);
        }
    }

    private static void Select(ComponentSelector selector, Type type)
    {
        if (type.IsGenericTypeDefinition) return;
        Recents.Push(Scope(selector), type);
        selector.ComponentSelected.Target?.Invoke(selector, type);
        try
        {
            if (States.TryGet(selector, out var state)) RefreshRecent(selector, state);
        }
        catch (Exception ex)
        {
            EX0Selector.LogError("recent list refresh failed: " + ex);
        }
    }

    private static void CancelButton(UIBuilder ui, ComponentSelector selector)
    {
        var handler = (ButtonEventHandler)Delegate.CreateDelegate(typeof(ButtonEventHandler), selector, CancelMethod!);
        ui.Button("General.Cancel".AsLocaleKey(), RadiantUI_Constants.Sub.RED, handler, 0.35f).Slot.OrderOffset = 1000000L;
    }

    private static ButtonEventHandler<T> Handler<T>(ComponentSelector selector, MethodInfo method)
        => (ButtonEventHandler<T>)Delegate.CreateDelegate(typeof(ButtonEventHandler<T>), selector, method);

    private static Slot Heading(UIBuilder ui, string text)
    {
        ui.Style.MinHeight = 24f;
        var label = ui.Text((LocaleString)$"<b><color={DIM_HEX}>{NoParse(text.ToUpperInvariant())}</color></b>", 18f, bestFit: false, Alignment.MiddleLeft);
        ui.Style.MinHeight = ROW_HEIGHT;
        return label.Slot;
    }

    private static Slot Note(UIBuilder ui, string text)
    {
        ui.Style.MinHeight = 48f;
        var label = ui.Text((LocaleString)$"<color={DIM_HEX}>{NoParse(text)}</color>", 18f, bestFit: false, Alignment.MiddleLeft);
        ui.Style.MinHeight = ROW_HEIGHT;
        return label.Slot;
    }

    private static string NoParse(string s) => s.Length == 0 ? "" : $"<noparse={s.Length}>{s}";

    private static string StripMarkup(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        int close = s!.IndexOf('>');
        return s.StartsWith("<noparse=") && close > 0 ? s.Substring(close + 1) : s;
    }
}
