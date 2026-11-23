// SPDX-License-Identifier: GPL-3.0-only
// Additional permission under GPLv3 section 7: see NVAPI-EXCEPTION.txt.

using System;
using System.Globalization;
using System.IO;
using System.Security;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace Rtx50Temps;

// Keep both captions on a captured reading so even a frozen frame can change language.
[JsonConverter(typeof(CaptionConverter))]
internal sealed record Caption(string Zh, string En)
{
    public override string ToString() => Language.Text(Zh, En);
    public static implicit operator Caption(string shared) => new(shared, shared);
    public InvalidDataException InvalidData() => new(ToString()) { Data = { [typeof(Caption)] = this } };
    public IOException IoError() => new(ToString()) { Data = { [typeof(Caption)] = this } };
    public static Caption FromError(Exception error) => error.Data[typeof(Caption)] as Caption ?? (Caption)error.Message;
}

internal sealed class CaptionConverter : JsonConverter<Caption>
{
    public override Caption Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        => reader.GetString();

    public override void Write(Utf8JsonWriter writer, Caption value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString());
}

internal static class Language
{
    private const string Settings = @"HKEY_CURRENT_USER\Software\Rtx50Temps";
    internal static bool Chinese => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh";
    internal static string Text(string zh, string en) => Chinese ? zh : en;

    internal static void Set(bool chinese)
    {
        var culture = CultureInfo.GetCultureInfo(chinese ? "zh-CN" : "en-US");
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }

    public static void Load()
    {
        var chinese = Chinese;
        try
        {
            var saved = Registry.GetValue(Settings, "Language", null) as string;
            if (saved == "zh" || saved == "en") chinese = saved == "zh";
        }
        catch (Exception e) when (e is IOException or SecurityException or UnauthorizedAccessException) { }
        Set(chinese);
    }

    public static void Remember()
    {
        try { Registry.SetValue(Settings, "Language", Chinese ? "zh" : "en"); }
        catch (Exception e) when (e is IOException or SecurityException or UnauthorizedAccessException) { }
    }

    public static void FromArgument(string value)
    {
        switch (value?.ToLowerInvariant())
        {
            case "en": case "en-us": Set(false); break;
            case "zh": case "zh-cn": Set(true); break;
            default: throw new ArgumentException(Text("请为 --lang 指定 zh 或 en。", "Specify zh or en for --lang."));
        }
    }
}
