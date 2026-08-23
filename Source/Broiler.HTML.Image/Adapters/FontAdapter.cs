using System;
using Broiler.Graphics;
using DrawingFontStyle = Broiler.Graphics.FontStyle;

namespace Broiler.HTML.Image.Adapters;

internal sealed class FontAdapter(
    string family,
    double size,
    DrawingFontStyle style,
    Func<object>? compatTypefaceFactory = null,
    IFontCompatFactory? fontCompatFactory = null) : RFont
{
    /// <summary>
    /// Ratio to convert typographic points to CSS pixels (96 DPI / 72 DPI).
    /// </summary>
    private const double PtToCssPx = 96.0 / 72.0;
    private readonly IFontCompatFactory _fontCompatFactory = fontCompatFactory ?? CompatProvider.FontCompatFactory;
    private double _height = -1;
    private double _underlineOffset = -1;
    private double _whitespaceWidth = -1;
    private object? _typeface;
    private object? _font;
    private object? _renderFont;

    /// <summary>Layout font (pt-based) – used for metrics and text measurement.</summary>
    public object Font => _font ??= _fontCompatFactory.CreateFont(Typeface, (float)size);

    /// <summary>Render font (CSS px-based) – used for drawing glyphs at correct size.</summary>
    public object RenderFont => _renderFont ??= _fontCompatFactory.CreateFont(Typeface, (float)(size * PtToCssPx));

    public object Typeface => _typeface ??= compatTypefaceFactory?.Invoke()
        ?? throw new InvalidOperationException("Font compatibility typeface factory was not configured.");

    public override double Size => size;

    /// <summary>
    /// The family this font resolved to. <c>FontsHandler.GetCachedFont</c> resolves the CSS
    /// <c>font-family</c> list before constructing the adapter, so this is one installed family
    /// name — the face every width on this font was measured with.
    /// </summary>
    public override string Family => family;

    public override DrawingFontStyle Style => style;

    public override double Height
    {
        get
        {
            EnsureMetrics();
            return _height;
        }
    }

    public override double UnderlineOffset
    {
        get
        {
            EnsureMetrics();
            return _underlineOffset;
        }
    }

    public override double LeftPadding => Height / 6.0;

    internal bool HasMaterializedLayoutFont => _font is not null;

    internal bool HasMaterializedRenderFont => _renderFont is not null;

    public override double GetWhitespaceWidth(RGraphics graphics)
    {
        if (_whitespaceWidth < 0)
            _whitespaceWidth = graphics.MeasureString(" ", this).Width;

        return _whitespaceWidth;
    }

    private void EnsureMetrics()
    {
        if (_height >= 0 && _underlineOffset >= 0)
            return;

        var compatMetrics = _fontCompatFactory.GetMetrics(Font);
        _height = compatMetrics.Height;
        _underlineOffset = compatMetrics.UnderlineOffset;
    }
}
