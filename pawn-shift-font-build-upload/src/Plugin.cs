using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace PAWNShift.KoreanFontFallback;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class Plugin : BasePlugin
{
    public const string PluginGuid = "com.ryle1.pawnshift.koreanfontfallback";
    public const string PluginName = "PAWN SHIFT Korean Font Fallback";
    public const string PluginVersion = "1.0.0";

    private static ManualLogSource? _log;
    private static ConfigEntry<string>? _fontFile;
    private static ConfigEntry<bool>? _forceReplaceAll;
    private static ConfigEntry<bool>? _setAsDefault;
    private static ConfigEntry<float>? _scanInterval;

    private static Type? _tmpFontAssetType;
    private static Type? _tmpTextType;
    private static Type? _tmpSettingsType;
    private static object? _runtimeFontAsset;

    private static MethodInfo? _findAllGeneric;
    private static float _nextScan;
    private static bool _ready;

    public override void Load()
    {
        _log = Log;

        _fontFile = Config.Bind(
            "Font", "FontFile", "NanumGothic.ttf",
            "TTF file name. Search order: plugin folder, game root."
        );

        _forceReplaceAll = Config.Bind(
            "Behaviour", "ForceReplaceAll", false,
            "false = preserve original game fonts and add NanumGothic as a fallback. " +
            "true = force all TMP text to use NanumGothic."
        );

        _setAsDefault = Config.Bind(
            "Behaviour", "SetAsDefaultTMPFont", false,
            "Also assign the runtime font to TMP_Settings.defaultFontAsset."
        );

        _scanInterval = Config.Bind(
            "Behaviour", "ScanIntervalSeconds", 1.0f,
            "How often to rescan fonts/text loaded after startup."
        );

        try
        {
            ResolveTMPTypes();
            CreateRuntimeFontAsset();

            // No injected MonoBehaviour is required. Unity calls this event frequently;
            // Tick() throttles itself using Time.unscaledTime.
            Application.onBeforeRender += Tick;

            ApplyFontPass(true);
            _ready = true;
            Log.LogInfo($"{PluginName} {PluginVersion} loaded.");
        }
        catch (Exception ex)
        {
            Log.LogError("Initialization failed: " + ex);
        }
    }

    public override bool Unload()
    {
        try { Application.onBeforeRender -= Tick; } catch { }
        _ready = false;
        return true;
    }

    private static void ResolveTMPTypes()
    {
        _tmpFontAssetType = FindType("TMPro.TMP_FontAsset");
        _tmpTextType = FindType("TMPro.TMP_Text");
        _tmpSettingsType = FindType("TMPro.TMP_Settings");

        if (_tmpFontAssetType == null || _tmpTextType == null || _tmpSettingsType == null)
            throw new TypeLoadException(
                $"TMP types missing. FontAsset={_tmpFontAssetType != null}, " +
                $"Text={_tmpTextType != null}, Settings={_tmpSettingsType != null}"
            );

        _findAllGeneric = typeof(Resources)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(m =>
                m.Name == "FindObjectsOfTypeAll" &&
                m.IsGenericMethodDefinition &&
                m.GetParameters().Length == 0);

        if (_findAllGeneric == null)
            throw new MissingMethodException("Resources.FindObjectsOfTypeAll<T>() not found.");

        _log?.LogInfo("Resolved TMP runtime types.");
    }

    private static Type? FindType(string fullName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var t = asm.GetType(fullName, false);
                if (t != null)
                    return t;
            }
            catch { }
        }
        return null;
    }

    private static string FindFontPath()
    {
        string file = _fontFile?.Value ?? "NanumGothic.ttf";

        string pluginFolder = Path.Combine(Paths.PluginPath, "PAWNShift.KoreanFontFallback");
        string p1 = Path.Combine(pluginFolder, file);
        if (File.Exists(p1))
            return p1;

        string p2 = Path.Combine(Paths.GameRootPath, file);
        if (File.Exists(p2))
            return p2;

        throw new FileNotFoundException(
            $"{file} not found. Put your own font file in " +
            $"'{pluginFolder}' or the PAWN SHIFT game root."
        );
    }

    private static void CreateRuntimeFontAsset()
    {
        if (_tmpFontAssetType == null)
            throw new InvalidOperationException("TMP_FontAsset type not resolved.");

        string fontPath = FindFontPath();
        _log?.LogInfo("Creating TMP font from: " + fontPath);

        // Unity 6 / modern TMP has:
        // CreateFontAsset(string fontFilePath, int faceIndex, int samplingPointSize,
        //   int atlasPadding, GlyphRenderMode renderMode, int atlasWidth, int atlasHeight)
        MethodInfo? createFromPath = _tmpFontAssetType
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == "CreateFontAsset")
            .FirstOrDefault(m =>
            {
                var p = m.GetParameters();
                return p.Length == 7 && p[0].ParameterType == typeof(string);
            });

        if (createFromPath == null)
            throw new MissingMethodException(
                "TMP_FontAsset.CreateFontAsset(string, int, int, int, ..., int, int) not found."
            );

        var pars = createFromPath.GetParameters();
        object renderMode;
        try
        {
            renderMode = Enum.Parse(pars[4].ParameterType, "SDFAA");
        }
        catch
        {
            // Fallback used by some TMP/Unity combinations.
            renderMode = Enum.ToObject(pars[4].ParameterType, 4169);
        }

        _runtimeFontAsset = createFromPath.Invoke(
            null,
            new object[] { fontPath, 0, 60, 7, renderMode, 4096, 4096 }
        );

        if (_runtimeFontAsset == null)
            throw new InvalidOperationException("CreateFontAsset returned null.");

        TrySetProperty(_runtimeFontAsset, "atlasPopulationMode", "Dynamic");
        TrySetProperty(_runtimeFontAsset, "isMultiAtlasTexturesEnabled", true);

        if (_runtimeFontAsset is UnityEngine.Object uo)
        {
            uo.name = "NanumGothic Runtime Fallback";
            UnityEngine.Object.DontDestroyOnLoad(uo);
        }

        _log?.LogInfo("Created NanumGothic dynamic TMP runtime font.");
    }

    private static void Tick()
    {
        if (!_ready && _runtimeFontAsset == null)
            return;

        float interval = Mathf.Max(0.25f, _scanInterval?.Value ?? 1.0f);
        if (Time.unscaledTime < _nextScan)
            return;

        _nextScan = Time.unscaledTime + interval;

        try { ApplyFontPass(false); }
        catch (Exception ex) { _log?.LogWarning("Font pass failed: " + ex.Message); }
    }

    private static object? FindAll(Type type)
    {
        if (_findAllGeneric == null)
            return null;

        try
        {
            var gm = _findAllGeneric.MakeGenericMethod(type);
            return gm.Invoke(null, null);
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"FindObjectsOfTypeAll<{type.Name}> failed: {ex.Message}");
            return null;
        }
    }

    private static IEnumerable EnumerateUnknownArray(object? array)
    {
        if (array == null)
            yield break;

        if (array is IEnumerable managedEnumerable)
        {
            foreach (var item in managedEnumerable)
                if (item != null)
                    yield return item;
            yield break;
        }

        var t = array.GetType();
        var lenProp = t.GetProperty("Length");
        var itemProp = t.GetProperty("Item");
        if (lenProp == null || itemProp == null)
            yield break;

        int len = Convert.ToInt32(lenProp.GetValue(array));
        for (int i = 0; i < len; i++)
        {
            var item = itemProp.GetValue(array, new object[] { i });
            if (item != null)
                yield return item;
        }
    }

    private static bool AddToListIfMissing(object? list, object item)
    {
        if (list == null)
            return false;

        var type = list.GetType();

        MethodInfo? contains = type.GetMethods()
            .FirstOrDefault(m => m.Name == "Contains" && m.GetParameters().Length == 1);
        MethodInfo? add = type.GetMethods()
            .FirstOrDefault(m => m.Name == "Add" && m.GetParameters().Length == 1);

        if (add == null)
            return false;

        try
        {
            if (contains != null && Convert.ToBoolean(contains.Invoke(list, new[] { item })))
                return false;

            add.Invoke(list, new[] { item });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void ApplyFontPass(bool logAlways)
    {
        if (_runtimeFontAsset == null ||
            _tmpFontAssetType == null ||
            _tmpTextType == null ||
            _tmpSettingsType == null)
            return;

        int globalAdded = 0;
        int perFontAdded = 0;
        int replaced = 0;

        // Global fallback.
        try
        {
            var fallbackProp = _tmpSettingsType.GetProperty(
                "fallbackFontAssets",
                BindingFlags.Public | BindingFlags.Static
            );
            var globalFallbacks = fallbackProp?.GetValue(null);
            if (AddToListIfMissing(globalFallbacks, _runtimeFontAsset))
                globalAdded++;

            if (_setAsDefault?.Value ?? false)
            {
                var defaultProp = _tmpSettingsType.GetProperty(
                    "defaultFontAsset",
                    BindingFlags.Public | BindingFlags.Static
                );
                if (defaultProp?.CanWrite == true)
                    defaultProp.SetValue(null, _runtimeFontAsset);
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning("Global TMP fallback failed: " + ex.Message);
        }

        // Add fallback to every TMP font already loaded by the game.
        foreach (var font in EnumerateUnknownArray(FindAll(_tmpFontAssetType)))
        {
            if (ReferenceEquals(font, _runtimeFontAsset))
                continue;

            try
            {
                var p = font.GetType().GetProperty(
                    "fallbackFontAssetTable",
                    BindingFlags.Public | BindingFlags.Instance
                );
                if (AddToListIfMissing(p?.GetValue(font), _runtimeFontAsset))
                    perFontAdded++;
            }
            catch { }
        }

        // Emergency mode: replace the font assigned to each TMP_Text.
        if (_forceReplaceAll?.Value ?? false)
        {
            foreach (var text in EnumerateUnknownArray(FindAll(_tmpTextType)))
            {
                try
                {
                    var fontProp = text.GetType().GetProperty(
                        "font", BindingFlags.Public | BindingFlags.Instance
                    );
                    if (fontProp?.CanWrite == true)
                    {
                        fontProp.SetValue(text, _runtimeFontAsset);
                        replaced++;
                    }

                    var dirty = text.GetType().GetMethod(
                        "SetAllDirty",
                        BindingFlags.Public | BindingFlags.Instance,
                        null, Type.EmptyTypes, null
                    );
                    dirty?.Invoke(text, null);
                }
                catch { }
            }
        }

        if (logAlways || globalAdded > 0 || perFontAdded > 0 || replaced > 0)
        {
            _log?.LogInfo(
                $"Font pass: global+{globalAdded}, per-font+{perFontAdded}, replaced={replaced}"
            );
        }
    }

    private static void TrySetProperty(object obj, string name, object value)
    {
        try
        {
            var p = obj.GetType().GetProperty(
                name, BindingFlags.Public | BindingFlags.Instance
            );
            if (p?.CanWrite != true)
                return;

            object actual = value;
            if (p.PropertyType.IsEnum && value is string s)
                actual = Enum.Parse(p.PropertyType, s);

            p.SetValue(obj, actual);
        }
        catch { }
    }
}
