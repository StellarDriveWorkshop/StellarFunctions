using System;
using TMPro;
using UnityEngine;

namespace StellarFunctions.Support;

// Support-owned world-space display driven by a registered screen channel.
// Polls providers at 10 Hz while placed outside the hidden holder and
// presents scroll/static text every frame. Legacy channels also present in
// Awake. Neither path depends on a local player, camera or network manager.
public class ScreenSurface : MonoBehaviour
{
    private const float RefreshInterval = 0.1f;

    private string _channel;
    private int _columns;
    private float _speed;
    private TextMeshPro _text;
    private string _shown;
    private string _scrollContent;
    private float _offset;
    private float _last;
    private bool _locked;
    private bool _hasFrame;
    private ScreenFrame _frame;

    // Opt-in layout for literal single lines. Prefab values travel with the
    // visual clone while the layout and ticker position start fresh.
    [SerializeField]
    private float _preferredSize;

    [SerializeField]
    private float _minimumSize;

    [SerializeField]
    private float _usableWidth;
    private MeasuredScreenLine _line;
    private Func<string, float, float> _measure;
    private string _lineContent;
    private Rect _lineRect;
    private TMP_FontAsset _lineFont;
    private float _shownSize;
    private bool _measured;

    internal void ConfigureMeasuredLine(float preferred, float minimum, float usableWidth)
    {
        _preferredSize = preferred;
        _minimumSize = minimum;
        _usableWidth = usableWidth;
        _line = null;
        var label = GetComponentInChildren<TextMeshPro>(true);
        label.enableAutoSizing = false;
        label.textWrappingMode = TextWrappingModes.NoWrap;
        label.overflowMode = TextOverflowModes.Truncate;
        label.richText = false;
        // Escape pairs are consumed once by TMP. Doubling backslashes in
        // Literal() also prevents TMP from decoding names like \u003C.
        label.parseCtrlCharacters = true;
        label.fontSize = preferred;
    }

    private void Awake()
    {
        ScreenText.ParseName(gameObject.name, out _channel, out _columns, out _speed);
        _text = GetComponentInChildren<TextMeshPro>(true);
        ScreenProfiler.SurfaceCreated(_preferredSize > 0f);
        long pullStart = ScreenProfiler.Begin(ScreenProfileBucket.PullFrame);
        PullFrame();
        ScreenProfiler.End(ScreenProfileBucket.PullFrame, pullStart);
        // Measured screens wait for the visual and its TMP child to finish
        // initialization. Legacy channels still present immediately.
        if (_preferredSize <= 0f)
        {
            Present(0f);
        }
    }

    private void OnDestroy() => ScreenProfiler.SurfaceDestroyed(_preferredSize > 0f);

    private void Update()
    {
        long updateStart = ScreenProfiler.Begin(ScreenProfileBucket.Update);
        if (!InHolder())
        {
            if (Time.unscaledTime - _last >= RefreshInterval)
            {
                long pullStart = ScreenProfiler.Begin(ScreenProfileBucket.PullFrame);
                PullFrame();
                ScreenProfiler.End(ScreenProfileBucket.PullFrame, pullStart);
            }
            Present(Mathf.Min(Time.unscaledDeltaTime, 0.25f));
        }
        else
        {
            ScreenProfiler.Exit(ScreenProfileExit.HolderSkip);
        }

        ScreenProfiler.End(ScreenProfileBucket.Update, updateStart);
    }

    private bool InHolder() => transform.root == PartFactory.Holder;

    private void PullFrame()
    {
        _last = Time.unscaledTime;
        _hasFrame = _text != null && Screens.TryGet(_channel, gameObject, out _frame);
    }

    private void Present(float delta)
    {
        long presentStart = ScreenProfiler.Begin(ScreenProfileBucket.Present);
        if (!_hasFrame)
        {
            ScreenProfiler.Exit(ScreenProfileExit.NoFrame);
            ScreenProfiler.End(ScreenProfileBucket.Present, presentStart);
            return;
        }
        if (_frame.TextColor is Color color && _text.color != color)
        {
            _text.color = color;
        }

        string content = _frame.Text ?? string.Empty;
        if (_preferredSize > 0f)
        {
            PresentLine(content, delta);
            ScreenProfiler.End(ScreenProfileBucket.Present, presentStart);
            return;
        }

        string shown;
        if (ScreenText.Scrolls(content, _frame.Scroll))
        {
            FitText();
            string ticker = content + "     ";
            if (content != _scrollContent)
            {
                _offset = 0f;
                _scrollContent = content;
            }
            _offset = ScreenText.Advance(_offset, delta, _speed, ticker.Length);
            shown = ScreenText.Mono(ScreenText.Window(ticker, _columns, _offset));
        }
        else
        {
            shown = content;
        }

        if (shown == _shown)
        {
            ScreenProfiler.Exit(ScreenProfileExit.Unchanged);
            ScreenProfiler.End(ScreenProfileBucket.Present, presentStart);
            return;
        }
        _shown = shown;
        long tmpStart = ScreenProfiler.Begin(ScreenProfileBucket.TmpUpdate);
        _text.text = shown;
        _text.ForceMeshUpdate(false, false);
        ScreenProfiler.End(ScreenProfileBucket.TmpUpdate, tmpStart);
        ScreenProfiler.End(ScreenProfileBucket.Present, presentStart);
    }

    private void PresentLine(string content, float delta)
    {
        long lineStart = ScreenProfiler.Begin(ScreenProfileBucket.PresentLine);
        if (!_text.isActiveAndEnabled || _text.font == null)
        {
            ScreenProfiler.Exit(ScreenProfileExit.InactiveLabel);
            ScreenProfiler.End(ScreenProfileBucket.PresentLine, lineStart);
            return;
        }
        Rect rect = _text.rectTransform.rect;
        if (rect.width <= 0f || rect.height <= 0f)
        {
            ScreenProfiler.Exit(ScreenProfileExit.EmptyRect);
            ScreenProfiler.End(ScreenProfileBucket.PresentLine, lineStart);
            return;
        }

        _measure ??= MeasureLine;
        _measured = false;
        bool changed = _line == null || _lineContent != content || _lineRect != rect || _lineFont != _text.font;
        if (changed)
        {
            _lineContent = content;
            _lineRect = rect;
            _lineFont = _text.font;
            long buildStart = ScreenProfiler.Begin(ScreenProfileBucket.LineBuild);
            _line = new MeasuredScreenLine(
                MeasuredScreenLine.SingleLine(content),
                _preferredSize,
                _minimumSize,
                _measure
            );
            ScreenProfiler.End(ScreenProfileBucket.LineBuild, buildStart);
        }

        long linePresentStart = ScreenProfiler.Begin(ScreenProfileBucket.LinePresent);
        var frame = _line.Present(changed ? 0f : delta * Mathf.Max(0f, _speed), _measure);
        ScreenProfiler.End(ScreenProfileBucket.LinePresent, linePresentStart);
        if (!_measured && _shown == frame.Text && _shownSize == frame.FontSize)
        {
            ScreenProfiler.Exit(ScreenProfileExit.Unchanged);
            ScreenProfiler.End(ScreenProfileBucket.PresentLine, lineStart);
            return;
        }
        _shown = frame.Text;
        _shownSize = frame.FontSize;
        long tmpStart = ScreenProfiler.Begin(ScreenProfileBucket.TmpUpdate);
        _text.fontSize = frame.FontSize;
        _text.text = Literal(frame.Text);
        _text.ForceMeshUpdate(false, false);
        ScreenProfiler.End(ScreenProfileBucket.TmpUpdate, tmpStart);
        ScreenProfiler.End(ScreenProfileBucket.PresentLine, lineStart);
    }

    private float MeasureLine(string content, float size)
    {
        if (content.Length == 0)
        {
            return 0f;
        }

        _measured = true;
        long measureStart = ScreenProfiler.Begin(ScreenProfileBucket.MeasureLine);
        _text.fontSize = size;
        _text.overflowMode = TextOverflowModes.Overflow;
        try
        {
            // GetTextInfo computes the actual (unclipped) glyph geometry
            // without uploading it to the renderer. PreferredValues rounds
            // to 0.01 local units, too coarse for these world-space labels.
            var info = _text.GetTextInfo(Literal(content));
            float centerX = _lineRect.center.x;
            float centerY = _lineRect.center.y;
            float extentX = 0f;
            float extentY = 0f;
            for (int i = 0; i < info.characterCount; i++)
            {
                var glyph = info.characterInfo[i];
                if (!glyph.isVisible)
                {
                    continue;
                }

                extentX = Mathf.Max(extentX, Mathf.Max(centerX - glyph.bottomLeft.x, glyph.topRight.x - centerX));
                extentY = Mathf.Max(extentY, Mathf.Max(centerY - glyph.bottomLeft.y, glyph.topRight.y - centerY));
            }
            float demand = Mathf.Max(2f * extentX / (_lineRect.width * _usableWidth), 2f * extentY / _lineRect.height);
            // Advances and line height can overflow before the ink does.
            return _text.firstOverflowCharacterIndex < 0 ? demand : Mathf.Max(demand, 1.001f);
        }
        finally
        {
            _text.overflowMode = TextOverflowModes.Truncate;
            ScreenProfiler.End(ScreenProfileBucket.MeasureLine, measureStart);
        }
    }

    private static string Literal(string content) => content.Replace("\\", "\\\\");

    // Measure once at the smallest auto-size so the ticker fills the display.
    private void FitText()
    {
        if (_locked)
        {
            return;
        }

        _locked = true;
        _text.enableAutoSizing = true;
        _text.text = ScreenText.Mono(new string('W', _columns));
        _text.ForceMeshUpdate(false, false);
        float size = _text.fontSize;
        _text.enableAutoSizing = false;
        _text.fontSize = size;
        _shown = null;
    }
}
