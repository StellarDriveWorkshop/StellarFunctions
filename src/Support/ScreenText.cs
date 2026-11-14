using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace StellarFunctions.Support;

// Pure screen-name codec and ticker/monospace presentation math shared by
// ScreenSurface. String/float only — no UnityEngine dependency, so
// checks/core exercises the same code the game runs.
//
// Compatibility note: ParseName reproduces the legacy quirks exactly —
// a malformed trailing field yields 0 (TryParse's failure output), not the
// documented default, and the prefix match is ordinal case-sensitive.
public static class ScreenText
{
    public const string Prefix = "SDScreen|";
    public const int DefaultColumns = 16;
    public const float DefaultSpeed = 6f;

    // Legacy name format: "SDScreen|<channel>|<columns>|<speed>". Columns and
    // speed are rendered invariantly so FormatName(ParseName(x)) round-trips
    // byte-identically for every input the builder produces.
    public static string FormatName(string channel, int columns, float speed)
        => Prefix + channel + "|" + columns.ToString(CultureInfo.InvariantCulture)
            + "|" + speed.ToString(CultureInfo.InvariantCulture);

    public static bool ParseName(string name, out string channel, out int columns, out float speed)
    {
        channel = null;
        columns = DefaultColumns;
        speed = DefaultSpeed;
        if (name == null || !name.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        string[] parts = name.Substring(Prefix.Length).Split('|');
        channel = parts[0];
        if (parts.Length > 1) int.TryParse(parts[1], out columns);
        if (parts.Length > 2) float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out speed);
        return true;
    }

    // Scrolling decision extracted from ScreenSurface.Present: scroll only
    // when the provider asked for it and there is content to scroll.
    public static bool Scrolls(string text, bool scroll) => scroll && text != null && text.Length > 0;

    // Advances the ticker offset exactly like the legacy per-frame update:
    // float delta*speed accumulated modulo the ticker length.
    public static float Advance(float offset, float delta, float speed, int tickerLength)
        => (offset + delta * speed) % tickerLength;

    // The visible window of `ticker` starting at `offset` characters,
    // wrapping around, `columns` glyphs wide.
    public static string Window(string ticker, int columns, float offset)
    {
        int start = (int)Math.Floor(offset);
        var builder = new StringBuilder(columns);
        for (int i = 0; i < columns; i++)
            builder.Append(ticker[(start + i) % ticker.Length]);
        return builder.ToString();
    }

    // Monospace presentation markup with HTML escaping. Spaces become
    // non-breaking so the mspace advance applies uniformly.
    public static string Mono(string value)
        => "<mspace=0.62em>" + value.Replace("<", "&lt;").Replace(' ', ' ') + "</mspace>";
}

// Generic channel-provider registry: last-writer-wins registration,
// conditional unregistration by delegate equality, and once-per-channel
// error marking so a throwing provider cannot spam the log at poll rate.
// Unity-free so checks/core exercises the same logic the game runs.
public sealed class ChannelProviders<TScreen, TFrame>
{
    private readonly Dictionary<string, Func<TScreen, TFrame>> _providers =
        new Dictionary<string, Func<TScreen, TFrame>>(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _errorChannels = new HashSet<string>(StringComparer.Ordinal);

    public int Count => _providers.Count;

    public void Provide(string channel, Func<TScreen, TFrame> provider)
    {
        if (!string.IsNullOrEmpty(channel) && provider != null) _providers[channel] = provider;
    }

    // Removes the provider only when `expected` equals the registered one.
    // Delegate.Equals (not reference identity) is used so equivalent
    // instance-method delegates — same target and method — match, while an
    // old provider cannot unregister another provider's replacement.
    public bool Unprovide(string channel, Func<TScreen, TFrame> expected)
    {
        if (channel == null || expected == null) return false;
        return _providers.TryGetValue(channel, out var current)
            && current.Equals(expected)
            && _providers.Remove(channel);
    }

    public bool TryGetProvider(string channel, out Func<TScreen, TFrame> provider)
    {
        provider = null;
        return channel != null && _providers.TryGetValue(channel, out provider);
    }

    // Returns true the first time a channel reports an error, false after —
    // callers log on true only, preserving once-per-channel reporting.
    public bool MarkProviderError(string channel)
        => channel != null && _errorChannels.Add(channel);
}
