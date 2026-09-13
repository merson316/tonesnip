using Microsoft.UI.Xaml;

namespace ToneSnip.App.Controls;

/// <summary>
/// Application styles by key, for controls that build part of themselves in code. The application dictionary's indexer
/// is the lookup that resolves merged and theme dictionaries correctly, but it throws on a missing key; here a missing
/// style costs the look and a log line, and the control still works.
/// </summary>
internal static class AppStyles
{
    internal static Style? Get(string key)
    {
        try { return Application.Current.Resources[key] as Style; }
        catch (Exception ex) { Warn($"style '{key}' unavailable: {ex.Message}"); return null; }
    }

    /// <summary>Guarded, because this runs on a path a broken resource dictionary is already on.</summary>
    private static void Warn(string message)
    {
        try { App.Current.Log.Warn(message); }
        catch (Exception) { }
    }
}
