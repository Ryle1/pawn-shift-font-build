using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using UnityEngine;

namespace PAWNShift.KoreanFontFallback;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class Plugin : BasePlugin
{
    public const string PluginGuid = "com.ryle1.pawnshift.koreanfontfallback";using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using UnityEngine;

namespace PAWNShift.KoreanFontFallback;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class Plugin : BasePlugin
{
    public const string PluginGuid = "com.ryle1.pawnshift.koreanfontfallback";
    public const string PluginName = "PAWN SHIFT Korean Font Fallback";
    public const string PluginVersion = "1.0.2";

    private static ManualLogSource? _log;
    private static ConfigEntry<string>? _fontFile;
    private static ConfigEntry<bool>? _forceReplaceAll;
    private static ConfigEntry<bool>? _setAsDefault;

    private static Type? _tmpFontAssetType;
    private static Type? _tmpTextType;
    private static Type? _tmpSettingsType;
    private static object? _runtimeFontAsset;
    private static MethodInfo? _findAllGeneric;


    public override void Load()
    {
        _log = Log;

        // These bindings also create:
        // BepInEx\config\com.ryle1.pawnshift.koreanfontfallback.cfg
        _fontFile = Config.Bind(
            "Font", "FontFile", "NanumGothic.ttf",
            "TTF file name. Search order: plugin folder, game root."
        );

        _forceReplaceAll = Config.Bind(
            "Behaviour", "ForceReplaceAll", false,
            "false = preserve original game fonts and add Korean font as fallback. " +
            "true = force all TMP text to use the Korean font."
        );

        _setAsDefault = Config.Bind(
            "Behaviour", "SetAsDefaultTMPFont", false,
            "Also assign the runtime font to TMP_Settings.defaultFontAsset."
        );

        try
        {
            ResolveTMPTypes();
            CreateRuntimeFontAsset();

            // PAWN SHIFT's Unity 6000.3.10 IL2CPP interop is not binary-compatible
            // with UnityAction-based callbacks, and compiling a custom MonoBehaviour
            // against the public Unity reference assembly cannot satisfy AddComponent<T>()'s
            // generated IL2CPP type constraint. Apply the global TMP fallback once here.
            // TMP_Settings.fallbackFontAssets is global, so later-created TMP text can
            // still resolve Korean glyphs through this fallback.
            ApplyFontPass(true);
            Log.LogInfo($"{PluginName} {PluginVersion} loaded.");
        }
        catch (Exception ex)
        {
            Log.LogError("Initialization failed: " + ex);
        }
    }

    private static void ResolveTMPTypes()
    {
        _tmpFontAssetType = FindType("TMPro.TMP_FontAsset");
        _tmpTextType = FindType("TMPro.TMP_Text");
        _tmpSettingsType = FindType("TMPro.TMP_Settings");

        if (_tmpFontAssetType == null || _tmpTextType == null || _tmpSettingsType == null)
        {
            throw new TypeLoadException(
                $"TMP types missing. FontAsset={_tmpFontAssetType != null}, " +
                $"Text={_tmpTextType != null}, Settings={_tmpSettingsType != null}"
            );
        }

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
            catch
            {
            }
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
            $"{file} not found. Put your own font file in '{pluginFolder}' or the game root."
        );
    }

    private static void CreateRuntimeFontAsset()
    {
        if (_tmpFontAssetType == null)
            throw new InvalidOperationException("TMP_FontAsset type not resolved.");

        string fontPath = FindFontPath();
        _log?.LogInfo("Creating TMP font from: " + fontPath);

        // Preferred modern TMP API:
        // CreateFontAsset(string path, int faceIndex, int pointSize, int padding,
        //                 GlyphRenderMode, int width, int height)
        MethodInfo? createFromPath = _tmpFontAssetType
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == "CreateFontAsset")
            .FirstOrDefault(m =>
            {
                var p = m.GetParameters();
                return p.Length == 7 && p[0].ParameterType == typeof(string);
            });

        if (createFromPath != null)
        {
            var p = createFromPath.GetParameters();
            object renderMode = ParseEnum(p[4].ParameterType, "SDFAA", 4169);

            _runtimeFontAsset = createFromPath.Invoke(
                null,
                new object[] { fontPath, 0, 60, 7, renderMode, 4096, 4096 }
            );
        }
        else
        {
            // Fallback for TMP versions that only expose a UnityEngine.Font overload.
            // Everything is created through reflection so this plugin does not hard-bind
            // to a Font constructor signature from the downloaded Unity reference DLL.
            _runtimeFontAsset = CreateViaUnityFontReflection(fontPath);
        }

        if (_runtimeFontAsset == null)
            throw new InvalidOperationException("TMP font creation returned null.");

        TrySetProperty(_runtimeFontAsset, "atlasPopulationMode", "Dynamic");
        TrySetProperty(_runtimeFontAsset, "isMultiAtlasTexturesEnabled", true);

        _log?.LogInfo("Created NanumGothic dynamic TMP runtime font.");
    }

    private static object CreateViaUnityFontReflection(string fontPath)
    {
        if (_tmpFontAssetType == null)
            throw new InvalidOperationException();

        Type? fontType = FindType("UnityEngine.Font");
        if (fontType == null)
            throw new TypeLoadException("UnityEngine.Font type not found.");

        ConstructorInfo? ctor = fontType.GetConstructor(new[] { typeof(string) });
        if (ctor == null)
            throw new MissingMethodException("UnityEngine.Font(string) constructor not found.");

        object unityFont = ctor.Invoke(new object[] { fontPath });

        MethodInfo? method = _tmpFontAssetType
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == "CreateFontAsset")
            .FirstOrDefault(m =>
            {
                var p = m.GetParameters();
                return p.Length >= 6 && p[0].ParameterType.FullName == "UnityEngine.Font";
            });

        if (method == null)
            throw new MissingMethodException("TMP_FontAsset.CreateFontAsset(Font, ...) not found.");

        var pars = method.GetParameters();
        var args = new object?[pars.Length];

        args[0] = unityFont;
        if (pars.Length > 1) args[1] = 60;
        if (pars.Length > 2) args[2] = 7;
        if (pars.Length > 3) args[3] = ParseEnum(pars[3].ParameterType, "SDFAA", 4169);
        if (pars.Length > 4) args[4] = 4096;
        if (pars.Length > 5) args[5] = 4096;

        for (int i = 6; i < pars.Length; i++)
        {
            if (pars[i].ParameterType.IsEnum)
                args[i] = ParseEnum(pars[i].ParameterType, "Dynamic", 1);
            else if (pars[i].ParameterType == typeof(bool))
                args[i] = true;
            else if (pars[i].HasDefaultValue)
                args[i] = pars[i].DefaultValue;
            else
                args[i] = Activator.CreateInstance(pars[i].ParameterType);
        }

        return method.Invoke(null, args)
            ?? throw new InvalidOperationException("TMP Font overload returned null.");
    }

    private static object ParseEnum(Type enumType, string name, int fallback)
    {
        try
        {
            return Enum.Parse(enumType, name);
        }
        catch
        {
            return Enum.ToObject(enumType, fallback);
        }
    }

    private static object? FindAll(Type type)
    {
        if (_findAllGeneric == null)
            return null;

        try
        {
            MethodInfo gm = _findAllGeneric.MakeGenericMethod(type);
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
            {
                if (item != null)
                    yield return item;
            }

            yield break;
        }

        Type t = array.GetType();
        PropertyInfo? lenProp = t.GetProperty("Length");
        PropertyInfo? itemProp = t.GetProperty("Item");

        if (lenProp == null || itemProp == null)
            yield break;

        int len = Convert.ToInt32(lenProp.GetValue(array));

        for (int i = 0; i < len; i++)
        {
            object? item = itemProp.GetValue(array, new object[] { i });
            if (item != null)
                yield return item;
        }
    }

    private static bool AddToListIfMissing(object? list, object item)
    {
        if (list == null)
            return false;

        Type type = list.GetType();

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

        try
        {
            PropertyInfo? fallbackProp = _tmpSettingsType.GetProperty(
                "fallbackFontAssets",
                BindingFlags.Public | BindingFlags.Static
            );

            object? globalFallbacks = fallbackProp?.GetValue(null);
            if (AddToListIfMissing(globalFallbacks, _runtimeFontAsset))
                globalAdded++;

            if (_setAsDefault?.Value ?? false)
            {
                PropertyInfo? defaultProp = _tmpSettingsType.GetProperty(
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

        foreach (var font in EnumerateUnknownArray(FindAll(_tmpFontAssetType)))
        {
            if (ReferenceEquals(font, _runtimeFontAsset))
                continue;

            try
            {
                PropertyInfo? p = font.GetType().GetProperty(
                    "fallbackFontAssetTable",
                    BindingFlags.Public | BindingFlags.Instance
                );

                if (AddToListIfMissing(p?.GetValue(font), _runtimeFontAsset))
                    perFontAdded++;
            }
            catch
            {
            }
        }

        if (_forceReplaceAll?.Value ?? false)
        {
            foreach (var text in EnumerateUnknownArray(FindAll(_tmpTextType)))
            {
                try
                {
                    PropertyInfo? fontProp = text.GetType().GetProperty(
                        "font",
                        BindingFlags.Public | BindingFlags.Instance
                    );

                    if (fontProp?.CanWrite == true)
                    {
                        fontProp.SetValue(text, _runtimeFontAsset);
                        replaced++;
                    }

                    MethodInfo? dirty = text.GetType().GetMethod(
                        "SetAllDirty",
                        BindingFlags.Public | BindingFlags.Instance,
                        null,
                        Type.EmptyTypes,
                        null
                    );

                    dirty?.Invoke(text, null);
                }
                catch
                {
                }
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
            PropertyInfo? p = obj.GetType().GetProperty(
                name,
                BindingFlags.Public | BindingFlags.Instance
            );

            if (p?.CanWrite != true)
                return;

            object actual = value;

            if (p.PropertyType.IsEnum && value is string s)
                actual = Enum.Parse(p.PropertyType, s);

            p.SetValue(obj, actual);
        }
        catch
        {
        }
    }
}

    public const string PluginName = "PAWN SHIFT Korean Font Fallback";
    public const string PluginVersion = "1.0.1";

    private static ManualLogSource? _log;
    private static ConfigEntry<string>? _fontFile;
    private static ConfigEntry<bool>? _forceReplaceAll;
    private static ConfigEntry<bool>? _setAsDefault;
    private static ConfigEntry<int>? _scanEveryFrames;

    private static Type? _tmpFontAssetType;
    private static Type? _tmpTextType;
    private static Type? _tmpSettingsType;
    private static object? _runtimeFontAsset;
    private static MethodInfo? _findAllGeneric;

    private static int _frameCounter;
    private static bool _ready;

    public override void Load()
    {
        _log = Log;

        // These bindings also create:
        // BepInEx\config\com.ryle1.pawnshift.koreanfontfallback.cfg
        _fontFile = Config.Bind(
            "Font", "FontFile", "NanumGothic.ttf",
            "TTF file name. Search order: plugin folder, game root."
        );

        _forceReplaceAll = Config.Bind(
            "Behaviour", "ForceReplaceAll", false,
            "false = preserve original game fonts and add Korean font as fallback. " +
            "true = force all TMP text to use the Korean font."
        );

        _setAsDefault = Config.Bind(
            "Behaviour", "SetAsDefaultTMPFont", false,
            "Also assign the runtime font to TMP_Settings.defaultFontAsset."
        );

        _scanEveryFrames = Config.Bind(
            "Behaviour", "ScanEveryFrames", 60,
            "How often to rescan fonts/text loaded after startup. 60 is roughly once per second at 60 FPS."
        );

        try
        {
            ResolveTMPTypes();
            CreateRuntimeFontAsset();

            // BepInEx IL2CPP registers this MonoBehaviour automatically.
            // This avoids Application.onBeforeRender / UnityAction, which is
            // incompatible with PAWN SHIFT's generated Unity 6000.3.10 interop.
            AddComponent<FontDriver>();

            ApplyFontPass(true);
            _ready = true;

            Log.LogInfo($"{PluginName} {PluginVersion} loaded.");
        }
        catch (Exception ex)
        {
            Log.LogError("Initialization failed: " + ex);
        }
    }

    internal static void DriverUpdate()
    {
        if (!_ready || _runtimeFontAsset == null)
            return;

        _frameCounter++;

        int every = _scanEveryFrames?.Value ?? 60;
        if (every < 1)
            every = 1;

        if (_frameCounter < every)
            return;

        _frameCounter = 0;

        try
        {
            ApplyFontPass(false);
        }
        catch (Exception ex)
        {
            _log?.LogWarning("Font pass failed: " + ex.Message);
        }
    }

    private static void ResolveTMPTypes()
    {
        _tmpFontAssetType = FindType("TMPro.TMP_FontAsset");
        _tmpTextType = FindType("TMPro.TMP_Text");
        _tmpSettingsType = FindType("TMPro.TMP_Settings");

        if (_tmpFontAssetType == null || _tmpTextType == null || _tmpSettingsType == null)
        {
            throw new TypeLoadException(
                $"TMP types missing. FontAsset={_tmpFontAssetType != null}, " +
                $"Text={_tmpTextType != null}, Settings={_tmpSettingsType != null}"
            );
        }

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
            catch
            {
            }
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
            $"{file} not found. Put your own font file in '{pluginFolder}' or the game root."
        );
    }

    private static void CreateRuntimeFontAsset()
    {
        if (_tmpFontAssetType == null)
            throw new InvalidOperationException("TMP_FontAsset type not resolved.");

        string fontPath = FindFontPath();
        _log?.LogInfo("Creating TMP font from: " + fontPath);

        // Preferred modern TMP API:
        // CreateFontAsset(string path, int faceIndex, int pointSize, int padding,
        //                 GlyphRenderMode, int width, int height)
        MethodInfo? createFromPath = _tmpFontAssetType
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == "CreateFontAsset")
            .FirstOrDefault(m =>
            {
                var p = m.GetParameters();
                return p.Length == 7 && p[0].ParameterType == typeof(string);
            });

        if (createFromPath != null)
        {
            var p = createFromPath.GetParameters();
            object renderMode = ParseEnum(p[4].ParameterType, "SDFAA", 4169);

            _runtimeFontAsset = createFromPath.Invoke(
                null,
                new object[] { fontPath, 0, 60, 7, renderMode, 4096, 4096 }
            );
        }
        else
        {
            // Fallback for TMP versions that only expose a UnityEngine.Font overload.
            // Everything is created through reflection so this plugin does not hard-bind
            // to a Font constructor signature from the downloaded Unity reference DLL.
            _runtimeFontAsset = CreateViaUnityFontReflection(fontPath);
        }

        if (_runtimeFontAsset == null)
            throw new InvalidOperationException("TMP font creation returned null.");

        TrySetProperty(_runtimeFontAsset, "atlasPopulationMode", "Dynamic");
        TrySetProperty(_runtimeFontAsset, "isMultiAtlasTexturesEnabled", true);

        _log?.LogInfo("Created NanumGothic dynamic TMP runtime font.");
    }

    private static object CreateViaUnityFontReflection(string fontPath)
    {
        if (_tmpFontAssetType == null)
            throw new InvalidOperationException();

        Type? fontType = FindType("UnityEngine.Font");
        if (fontType == null)
            throw new TypeLoadException("UnityEngine.Font type not found.");

        ConstructorInfo? ctor = fontType.GetConstructor(new[] { typeof(string) });
        if (ctor == null)
            throw new MissingMethodException("UnityEngine.Font(string) constructor not found.");

        object unityFont = ctor.Invoke(new object[] { fontPath });

        MethodInfo? method = _tmpFontAssetType
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == "CreateFontAsset")
            .FirstOrDefault(m =>
            {
                var p = m.GetParameters();
                return p.Length >= 6 && p[0].ParameterType.FullName == "UnityEngine.Font";
            });

        if (method == null)
            throw new MissingMethodException("TMP_FontAsset.CreateFontAsset(Font, ...) not found.");

        var pars = method.GetParameters();
        var args = new object?[pars.Length];

        args[0] = unityFont;
        if (pars.Length > 1) args[1] = 60;
        if (pars.Length > 2) args[2] = 7;
        if (pars.Length > 3) args[3] = ParseEnum(pars[3].ParameterType, "SDFAA", 4169);
        if (pars.Length > 4) args[4] = 4096;
        if (pars.Length > 5) args[5] = 4096;

        for (int i = 6; i < pars.Length; i++)
        {
            if (pars[i].ParameterType.IsEnum)
                args[i] = ParseEnum(pars[i].ParameterType, "Dynamic", 1);
            else if (pars[i].ParameterType == typeof(bool))
                args[i] = true;
            else if (pars[i].HasDefaultValue)
                args[i] = pars[i].DefaultValue;
            else
                args[i] = Activator.CreateInstance(pars[i].ParameterType);
        }

        return method.Invoke(null, args)
            ?? throw new InvalidOperationException("TMP Font overload returned null.");
    }

    private static object ParseEnum(Type enumType, string name, int fallback)
    {
        try
        {
            return Enum.Parse(enumType, name);
        }
        catch
        {
            return Enum.ToObject(enumType, fallback);
        }
    }

    private static object? FindAll(Type type)
    {
        if (_findAllGeneric == null)
            return null;

        try
        {
            MethodInfo gm = _findAllGeneric.MakeGenericMethod(type);
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
            {
                if (item != null)
                    yield return item;
            }

            yield break;
        }

        Type t = array.GetType();
        PropertyInfo? lenProp = t.GetProperty("Length");
        PropertyInfo? itemProp = t.GetProperty("Item");

        if (lenProp == null || itemProp == null)
            yield break;

        int len = Convert.ToInt32(lenProp.GetValue(array));

        for (int i = 0; i < len; i++)
        {
            object? item = itemProp.GetValue(array, new object[] { i });
            if (item != null)
                yield return item;
        }
    }

    private static bool AddToListIfMissing(object? list, object item)
    {
        if (list == null)
            return false;

        Type type = list.GetType();

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

        try
        {
            PropertyInfo? fallbackProp = _tmpSettingsType.GetProperty(
                "fallbackFontAssets",
                BindingFlags.Public | BindingFlags.Static
            );

            object? globalFallbacks = fallbackProp?.GetValue(null);
            if (AddToListIfMissing(globalFallbacks, _runtimeFontAsset))
                globalAdded++;

            if (_setAsDefault?.Value ?? false)
            {
                PropertyInfo? defaultProp = _tmpSettingsType.GetProperty(
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

        foreach (var font in EnumerateUnknownArray(FindAll(_tmpFontAssetType)))
        {
            if (ReferenceEquals(font, _runtimeFontAsset))
                continue;

            try
            {
                PropertyInfo? p = font.GetType().GetProperty(
                    "fallbackFontAssetTable",
                    BindingFlags.Public | BindingFlags.Instance
                );

                if (AddToListIfMissing(p?.GetValue(font), _runtimeFontAsset))
                    perFontAdded++;
            }
            catch
            {
            }
        }

        if (_forceReplaceAll?.Value ?? false)
        {
            foreach (var text in EnumerateUnknownArray(FindAll(_tmpTextType)))
            {
                try
                {
                    PropertyInfo? fontProp = text.GetType().GetProperty(
                        "font",
                        BindingFlags.Public | BindingFlags.Instance
                    );

                    if (fontProp?.CanWrite == true)
                    {
                        fontProp.SetValue(text, _runtimeFontAsset);
                        replaced++;
                    }

                    MethodInfo? dirty = text.GetType().GetMethod(
                        "SetAllDirty",
                        BindingFlags.Public | BindingFlags.Instance,
                        null,
                        Type.EmptyTypes,
                        null
                    );

                    dirty?.Invoke(text, null);
                }
                catch
                {
                }
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
            PropertyInfo? p = obj.GetType().GetProperty(
                name,
                BindingFlags.Public | BindingFlags.Instance
            );

            if (p?.CanWrite != true)
                return;

            object actual = value;

            if (p.PropertyType.IsEnum && value is string s)
                actual = Enum.Parse(p.PropertyType, s);

            p.SetValue(obj, actual);
        }
        catch
        {
        }
    }
}

// BepInEx BasePlugin.AddComponent<T>() registers this type with IL2CPP.
// No UnityEvent / UnityAction subscription is used.
public sealed class FontDriver : MonoBehaviour
{
    public void Update()
    {
        Plugin.DriverUpdate();
    }
}
