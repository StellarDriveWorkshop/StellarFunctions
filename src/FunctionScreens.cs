using System;
using StellarFunctions.Support;
using UnityEngine;
using TMPro;

namespace StellarFunctions;

internal static class FunctionScreens
{
    private const string StatusChannel = "stellar-functions.status";
    private static readonly Color TextColor = new Color(.45f, .95f, 1f);

    internal static void ConfigureCallFace(ScreenSurface screen, int count)
    {
        bool compact = count == 1;
        // Keep the measured rectangles and native housing proportions.
        screen.transform.localScale = Vector3.one;
        var label = Label(screen);
        label.transform.localScale = Vector3.one;
        label.rectTransform.sizeDelta = compact ? new Vector2(.118f, .1f)
            : new Vector2(count == 3 ? .240f : .150f, .080f);

        var panel = screen.transform.Find("Panel");
        if (panel != null)
            panel.localScale = compact ? new Vector3(.28f, .075f, 1f) : new Vector3(.16f, .10f, 1f);

        ConfigureName(screen, label, compact ? .020f : .032f, compact ? .014f : .022f,
            count == 3 ? .98f : .94f);

        var orientation = screen.GetComponent<CallFaceOrientation>();
        if (compact && orientation == null) screen.gameObject.AddComponent<CallFaceOrientation>();
        if (!compact && orientation != null) UnityEngine.Object.DestroyImmediate(orientation);
    }

    internal static void ConfigureDefinitionFace(ScreenSurface screen)
        => ConfigureName(screen, Label(screen), .055f, .024f, .97f);

    private static TextMeshPro Label(ScreenSurface screen)
    {
        var label = screen.GetComponentInChildren<TextMeshPro>(true);
        if (label == null) throw new InvalidOperationException("Function face has no native screen text.");
        return label;
    }

    private static void ConfigureName(ScreenSurface screen, TextMeshPro label,
        float preferredCap, float minimumCap, float usableWidth)
    {
        label.characterSpacing = 0f;
        var font = label.font;
        float capPerPoint = font == null || font.faceInfo.pointSize <= 0 ? 0f
            : font.faceInfo.capLine * font.faceInfo.scale / font.faceInfo.pointSize
            * (label.isOrthographic ? 1f : .1f);
        float preferred = capPerPoint > 0f ? preferredCap / capPerPoint : 1f;
        float minimum = preferred * minimumCap / preferredCap;
        screen.ConfigureMeasuredLine(preferred, minimum, usableWidth);
    }

    internal static void Install() => Screens.Provide(StatusChannel, Frame);

    private static ScreenFrame Frame(GameObject screen)
    {
        var owner = screen.GetComponentInParent<FunctionVisualContext>();
        string text = "FUNCTION";
        if (owner != null && owner.Read(out _, out var part))
        {
            text = part.State switch
            {
                FunctionParamState param => param.Name,
                FunctionReturnState result => result.Name,
                FunctionCallState call => string.IsNullOrEmpty(call.SelectedName) ? "Call" : call.SelectedName,
                FunctionTransmitterState transmitter => "Function Transmitter\n" + transmitter.Summary.Replace(" · ", "\n"),
                _ => text,
            };
        }
        return new ScreenFrame { Text = text, TextColor = TextColor };
    }
}
