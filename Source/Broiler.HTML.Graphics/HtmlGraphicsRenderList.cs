using System;
using System.Collections.Generic;
using System.Drawing;
using Broiler.Layout.IR;
using Broiler.Graphics;

namespace Broiler.HTML.Graphics;

/// <summary>
/// A Broiler.Graphics render list plus backend image resources uploaded for that list.
/// </summary>
public class HtmlGraphicsRenderList(IBroilerRenderer renderer, BRenderList renderList, List<BImageHandle> images) : IDisposable
{
    private bool _disposed;

    public BRenderList RenderList { get; } = renderList;

    /// <summary>
    /// Display-item types this build could not express, distinct and sorted — empty when the page
    /// was translated in full.
    /// </summary>
    /// <remarks>
    /// The translator is a subset of what the raster backend draws, and used to be a silent one: an
    /// item with no case fell out of an unguarded switch and the page came back missing something
    /// with nothing to say so. Reporting the omissions lets a caller log them, a test assert on
    /// them, and a reader tell "this page uses no filters" from "the filters were dropped".
    /// </remarks>
    public IReadOnlyList<string> UnsupportedItems { get; init; } = [];

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        foreach (var image in images)
        {
            if (image.IsValid)
                renderer.ReleaseImage(image);
        }

        images.Clear();
    }
}

public static class HtmlGraphicsRenderListBuilder
{
    public static HtmlGraphicsRenderList Build(IBroilerRenderer renderer, DisplayList displayList, RectangleF clip)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(displayList);

        var list = new BRenderList(displayList.Items.Count + 2);
        var images = new List<BImageHandle>();
        var imageCache = new Dictionary<object, BImageHandle>();
        var opacityStack = new Stack<double>();
        var clipStack = new Stack<bool>();
        var unsupported = new SortedSet<string>(StringComparer.Ordinal);
        double opacity = 1.0;

        if (IsDrawable(clip))
            list.PushClip(ToRect(clip));

        foreach (var item in displayList.Items)
        {
            switch (item)
            {
                case FillRectItem fill:
                    FillRect(list, fill.Bounds, fill.Color, opacity);
                    break;
                case DrawBorderItem border:
                    DrawBorder(list, border, opacity);
                    break;
                case DrawTextItem text:
                    DrawText(list, text, opacity);
                    break;
                case DrawImageItem image:
                    DrawImage(list, renderer, images, imageCache, image.ImageHandle, image.SourceRect, image.DestRect, opacity);
                    break;
                case DrawTiledImageItem tiled:
                    DrawTiledImage(list, renderer, images, imageCache, tiled, opacity);
                    break;
                case DrawTiledGradientItem gradient:
                    DrawGradientFallback(list, gradient, opacity);
                    break;
                case DrawLineItem line:
                    DrawLineFallback(list, line, opacity);
                    break;
                case DrawSvgRectItem svgRect:
                    DrawSvgRect(list, svgRect, opacity);
                    break;
                case DrawSvgLineItem svgLine:
                    DrawLineFallback(
                        list,
                        new DrawLineItem
                        {
                            Start = new PointF(svgLine.Bounds.X + svgLine.X1, svgLine.Bounds.Y + svgLine.Y1),
                            End = new PointF(svgLine.Bounds.X + svgLine.X2, svgLine.Bounds.Y + svgLine.Y2),
                            Color = svgLine.Stroke,
                            Width = svgLine.StrokeWidth,
                        },
                        opacity);
                    break;
                case DrawSvgTextItem svgText:
                    DrawText(
                        list,
                        new DrawTextItem
                        {
                            Text = svgText.Text,
                            FontFamily = svgText.FontFamily,
                            FontSize = svgText.FontSize,
                            // The measured font, same as an HTML run carries. SvgTextEnvironment
                            // builds it at FontSize * PxToPt, so handle.Size * PtToPx is exactly
                            // FontSize and the two paths need no special case downstream.
                            FontHandle = svgText.FontHandle,
                            Color = svgText.Fill,
                            Origin = new PointF(svgText.Bounds.X + svgText.X, svgText.Bounds.Y + svgText.Y),
                        },
                        opacity);
                    break;
                case ClipItem clipItem:
                    if (IsDrawable(clipItem.ClipRect))
                    {
                        list.PushClip(ToRect(clipItem.ClipRect));
                        clipStack.Push(true);
                    }
                    else
                    {
                        clipStack.Push(false);
                    }
                    break;
                case RestoreItem:
                    if (clipStack.Count > 0 && clipStack.Pop())
                        list.PopClip();
                    break;
                case TransformItem transform:
                    list.PushTransform(ToMatrix(transform));
                    break;
                case RestoreTransformItem:
                    list.PopTransform();
                    break;
                case OpacityItem opacityItem:
                    opacityStack.Push(opacity);
                    opacity *= Math.Clamp(opacityItem.Opacity, 0f, 1f);
                    break;
                case RestoreOpacityItem:
                    opacity = opacityStack.Count > 0 ? opacityStack.Pop() : 1.0;
                    break;
                case DrawSvgEllipseItem svgEllipse:
                    DrawSvgEllipse(list, svgEllipse, opacity);
                    break;
                case BlendModeItem:
                case RestoreBlendModeItem:
                    // A blend layer needs a compositing group, which BRenderList has no command
                    // for. Ignoring both halves at least keeps the layer stack balanced; the
                    // primitives inside simply composite normally.
                    unsupported.Add(nameof(BlendModeItem));
                    break;
                case FilterItem:
                case RestoreFilterItem:
                    // Same: a filter is a layer effect with no render-list expression yet.
                    unsupported.Add(nameof(FilterItem));
                    break;
                default:
                    // Nothing silently disappears. The switch used to have no default, so a
                    // DisplayItem nobody had written a case for was dropped without trace and the
                    // page just came out missing something. Naming it here is what a caller (or a
                    // test) needs to tell "this page has no gradients" from "gradients are gone".
                    unsupported.Add(item.GetType().Name);
                    break;
            }
        }

        if (IsDrawable(clip))
            list.PopClip();

        return new HtmlGraphicsRenderList(renderer, list, images) { UnsupportedItems = [.. unsupported] };
    }

    private static void FillRect(BRenderList list, RectangleF rect, BColor color, double opacity)
    {
        if (!IsDrawable(rect) || color.A == 0 || opacity <= 0)
            return;

        list.FillRect(ToRect(rect), ToColor(color, opacity));
    }

    private static void DrawBorder(BRenderList list, DrawBorderItem item, double opacity)
    {
        RectangleF bounds = item.Bounds;
        BoxEdges widths = item.Widths;

        if (TryDrawRoundedBorder(list, item, opacity))
            return;

        DrawBorderSide(list, new RectangleF(bounds.Left, bounds.Top, bounds.Width, (float)widths.Top), item.TopColor, item.TopStyle, widths.Top, opacity);
        DrawBorderSide(list, new RectangleF(bounds.Right - (float)widths.Right, bounds.Top, (float)widths.Right, bounds.Height), item.RightColor, item.RightStyle, widths.Right, opacity);
        DrawBorderSide(list, new RectangleF(bounds.Left, bounds.Bottom - (float)widths.Bottom, bounds.Width, (float)widths.Bottom), item.BottomColor, item.BottomStyle, widths.Bottom, opacity);
        DrawBorderSide(list, new RectangleF(bounds.Left, bounds.Top, (float)widths.Left, bounds.Height), item.LeftColor, item.LeftStyle, widths.Left, opacity);
    }

    /// <summary>
    /// Strokes a uniform rounded border in one command, returning false when the border is not the
    /// uniform kind a single rounded-rect stroke can express.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The four-sided fill below cannot round a corner, so <c>border-radius</c> was ignored
    /// outright and every rounded box — buttons, cards, pills — came out square. A rounded-rect
    /// stroke handles the case that covers virtually all of them: one width, one colour, one style,
    /// and a radius on every corner.
    /// </para>
    /// <para>
    /// Restricted to a single radius because <see cref="BRenderList"/>'s rounded-rect commands take
    /// one x/y pair for all four corners, and to equal widths because a stroke has one thickness.
    /// Anything else keeps the square four-sided path, which is wrong in the same way it was before
    /// rather than newly wrong. The stroke is centred on its path, so the rectangle is inset by
    /// half the width to sit inside the border box the way CSS puts it.
    /// </para>
    /// </remarks>
    private static bool TryDrawRoundedBorder(BRenderList list, DrawBorderItem item, double opacity)
    {
        double radius = item.CornerNw;
        if (radius <= 0
            || item.CornerNe != radius || item.CornerSe != radius || item.CornerSw != radius)
        {
            return false;
        }

        BoxEdges widths = item.Widths;
        double width = widths.Top;
        if (width <= 0 || widths.Right != width || widths.Bottom != width || widths.Left != width)
            return false;

        if (item.TopColor != item.RightColor || item.TopColor != item.BottomColor || item.TopColor != item.LeftColor)
            return false;

        if (!IsBorderStyleVisible(item.TopStyle)
            || !string.Equals(item.TopStyle, item.RightStyle, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(item.TopStyle, item.BottomStyle, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(item.TopStyle, item.LeftStyle, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Dashes around a curve need a path to walk; leave those to the square fallback rather than
        // drawing a solid ring where the author asked for a dashed one.
        if (IsDashedStyle(item.TopStyle))
            return false;

        if (item.TopColor.A == 0 || opacity <= 0)
            return true;

        double inset = width / 2d;
        RectangleF bounds = item.Bounds;
        var stroked = new BRect(
            bounds.X + inset,
            bounds.Y + inset,
            Math.Max(0, bounds.Width - width),
            Math.Max(0, bounds.Height - width));

        if (stroked.Width <= 0 || stroked.Height <= 0)
            return true;

        double strokeRadius = Math.Max(0, radius - inset);
        list.StrokeRoundedRect(stroked, ToColor(item.TopColor, opacity), strokeRadius, strokeRadius, width);
        return true;
    }

    /// <summary>
    /// An SVG ellipse, as a rounded rectangle whose corner radii are its own semi-axes — which is
    /// an ellipse exactly, not an approximation of one. Without this the item had no case at all
    /// and every <c>&lt;ellipse&gt;</c> and <c>&lt;circle&gt;</c> vanished from the page.
    /// </summary>
    private static void DrawSvgEllipse(BRenderList list, DrawSvgEllipseItem item, double opacity)
    {
        if (item.Rx <= 0 || item.Ry <= 0 || opacity <= 0)
            return;

        var bounds = new BRect(
            item.Bounds.X + item.Cx - item.Rx,
            item.Bounds.Y + item.Cy - item.Ry,
            item.Rx * 2,
            item.Ry * 2);

        if (!item.Fill.IsEmpty && item.Fill.A > 0)
            list.FillRoundedRect(bounds, ToColor(item.Fill, opacity), item.Rx, item.Ry);

        if (!item.Stroke.IsEmpty && item.Stroke.A > 0 && item.StrokeWidth > 0)
            list.StrokeRoundedRect(bounds, ToColor(item.Stroke, opacity), item.Rx, item.Ry, item.StrokeWidth);
    }

    private static void DrawBorderSide(BRenderList list, RectangleF rect, BColor color, string style, double width, double opacity)
    {
        if (width <= 0 || color.A == 0 || !IsBorderStyleVisible(style))
            return;

        if (IsDashedStyle(style))
        {
            DrawDashedBorderSide(list, rect, color, style, width, opacity);
            return;
        }

        if (string.Equals(style, "double", StringComparison.OrdinalIgnoreCase) && width >= 3)
        {
            float line = (float)Math.Max(1d, Math.Floor(width / 3d));
            if (rect.Width >= rect.Height)
            {
                FillRect(list, new RectangleF(rect.X, rect.Y, rect.Width, line), color, opacity);
                FillRect(list, new RectangleF(rect.X, rect.Bottom - line, rect.Width, line), color, opacity);
            }
            else
            {
                FillRect(list, new RectangleF(rect.X, rect.Y, line, rect.Height), color, opacity);
                FillRect(list, new RectangleF(rect.Right - line, rect.Y, line, rect.Height), color, opacity);
            }

            return;
        }

        FillRect(list, rect, color, opacity);
    }

    /// <summary>Typographic points to CSS pixels (96 DPI / 72 DPI).</summary>
    private const double PointsToPixels = 96.0 / 72.0;

    private static void DrawText(BRenderList list, DrawTextItem item, double opacity)
    {
        if (string.IsNullOrEmpty(item.Text) || item.Color.A == 0 || opacity <= 0)
            return;

        BFontStyle font = ResolveFont(item);
        if (font.SizeInPixels <= 0)
            return;

        if (!item.TextShadowColor.IsEmpty && item.TextShadowColor.A > 0
            && (item.TextShadowOffsetX != 0 || item.TextShadowOffsetY != 0))
        {
            list.DrawText(
                new BTextRun(item.Text, font, ToColor(item.TextShadowColor, opacity)),
                new BPoint(item.Origin.X + item.TextShadowOffsetX, item.Origin.Y + item.TextShadowOffsetY));
        }

        list.DrawText(
            new BTextRun(item.Text, font, ToColor(item.Color, opacity)),
            new BPoint(item.Origin.X, item.Origin.Y));
    }

    /// <summary>
    /// Describes the run's font to the backend as the font layout actually <i>measured</i> it with.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This used to be built from <see cref="DrawTextItem.FontFamily"/> and
    /// <see cref="DrawTextItem.FontSize"/>, which describe the declared style rather than the used
    /// font, and got both wrong. <c>FontSize</c> is a <b>point</b> count (PaintWalker re-parses the
    /// computed CSS string and strips the unit) while <see cref="BFontStyle.SizeInPixels"/> is read
    /// as pixels, so every pt/%/em-derived run was drawn at 0.75x — and <c>FontFamily</c> is the
    /// whole CSS list (<c>"Verdana, Arial, Helvetica"</c>), which matches no installed family, so
    /// the backend substituted a default face. Layout had already positioned each word using the
    /// real font's advances, so the glyphs fell short of the space reserved for them and the words
    /// visibly drifted apart.
    /// </para>
    /// <para>
    /// <see cref="DrawTextItem.FontHandle"/> is the very font every width on this run was measured
    /// with, so taking size, family and slant from it makes drawing agree with measurement by
    /// construction — the same property that makes the raster backend (which draws through the
    /// handle) correct. The weight comes from the handle too, deliberately: layout collapses
    /// <c>font-weight</c> to a bold bit at >= 600, and reproducing that collapse here keeps the
    /// drawn face the measured one. Resolving 500 or 600 to a distinct face is a cascade-side
    /// change, not one to make on the paint side alone.
    /// </para>
    /// <para>
    /// The item fields remain the fallback for a run with no handle: SVG text synthesises a
    /// <see cref="DrawTextItem"/> whose <c>FontSize</c> is already in CSS px.
    /// </para>
    /// </remarks>
    private static BFontStyle ResolveFont(DrawTextItem item)
    {
        if (item.FontHandle is RFont measured && measured.Size > 0)
        {
            return new BFontStyle(
                string.IsNullOrWhiteSpace(measured.Family) ? FirstFontFamily(item.FontFamily) : measured.Family,
                measured.Size * PointsToPixels,
                (measured.Style & FontStyle.Bold) != 0 ? BFontWeight.Bold : BFontWeight.Normal,
                (measured.Style & FontStyle.Italic) != 0 ? BFontSlant.Italic : BFontSlant.Normal);
        }

        // An ILayoutFont that is not an RFont still states the used size in points, which is
        // strictly better than re-parsing the style string.
        double sizePx = item.FontHandle is ILayoutFont layoutFont && layoutFont.Size > 0
            ? layoutFont.Size * PointsToPixels
            : item.FontSize;

        return new BFontStyle(FirstFontFamily(item.FontFamily), sizePx, ToFontWeight(item.FontWeight));
    }

    /// <summary>
    /// The first family of a CSS <c>font-family</c> list. A backend is handed one family name; the
    /// whole list is not a family, and passing it whole is what made DirectWrite substitute.
    /// </summary>
    private static string FirstFontFamily(string cssFontFamily)
    {
        if (string.IsNullOrWhiteSpace(cssFontFamily))
            return "Segoe UI";

        foreach (string candidate in cssFontFamily.Split(','))
        {
            string trimmed = candidate.Trim().Trim('"', '\'').Trim();
            if (trimmed.Length > 0)
                return trimmed;
        }

        return "Segoe UI";
    }

    private static void DrawImage(
        BRenderList list,
        IBroilerRenderer renderer,
        List<BImageHandle> images,
        Dictionary<object, BImageHandle> imageCache,
        object? imageHandle,
        RectangleF source,
        RectangleF destination,
        double opacity)
    {
        if (imageHandle == null || !IsDrawable(destination) || opacity <= 0)
            return;

        BImageHandle image = GetImage(renderer, images, imageCache, imageHandle);
        if (!image.IsValid)
            return;

        if (!IsDrawable(source))
            source = new RectangleF(0, 0, (float)image.PixelSize.Width, (float)image.PixelSize.Height);

        list.DrawImage(image, ToRect(source), ToRect(destination), opacity);
    }

    private static void DrawTiledImage(
        BRenderList list,
        IBroilerRenderer renderer,
        List<BImageHandle> images,
        Dictionary<object, BImageHandle> imageCache,
        DrawTiledImageItem item,
        double opacity)
    {
        if (item.ImageHandle == null || !IsDrawable(item.FillRect) || opacity <= 0)
            return;

        BImageHandle image = GetImage(renderer, images, imageCache, item.ImageHandle);
        if (!image.IsValid)
            return;

        RectangleF source = IsDrawable(item.SourceRect)
            ? item.SourceRect
            : new RectangleF(0, 0, (float)image.PixelSize.Width, (float)image.PixelSize.Height);

        float tileWidth = item.TileWidth > 0 ? item.TileWidth : source.Width;
        float tileHeight = item.TileHeight > 0 ? item.TileHeight : source.Height;
        if (tileWidth <= 0 || tileHeight <= 0)
            return;

        RectangleF fill = item.FillRect;
        list.PushClip(ToRect(fill));

        bool repeatX = !string.Equals(item.Repeat, "no-repeat", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(item.Repeat, "repeat-y", StringComparison.OrdinalIgnoreCase);
        bool repeatY = !string.Equals(item.Repeat, "no-repeat", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(item.Repeat, "repeat-x", StringComparison.OrdinalIgnoreCase);

        float startX = item.TileOrigin.X;
        float startY = item.TileOrigin.Y;
        if (repeatX)
        {
            while (startX > fill.Left)
                startX -= tileWidth;
        }
        if (repeatY)
        {
            while (startY > fill.Top)
                startY -= tileHeight;
        }

        for (float y = startY; y < fill.Bottom; y += repeatY ? tileHeight : Math.Max(tileHeight, fill.Height + tileHeight))
        {
            for (float x = startX; x < fill.Right; x += repeatX ? tileWidth : Math.Max(tileWidth, fill.Width + tileWidth))
            {
                list.DrawImage(
                    image,
                    ToRect(source),
                    ToRect(new RectangleF(x, y, tileWidth, tileHeight)),
                    opacity);

                if (!repeatX)
                    break;
            }

            if (!repeatY)
                break;
        }

        list.PopClip();
    }

    private static BImageHandle GetImage(
        IBroilerRenderer renderer,
        List<BImageHandle> images,
        Dictionary<object, BImageHandle> imageCache,
        object imageHandle)
    {
        if (imageCache.TryGetValue(imageHandle, out BImageHandle cached))
            return cached;

        if (!Image.HtmlRender.TryCreatePixelBuffer(imageHandle, out BPixelBuffer pixels))
            return BImageHandle.Invalid;

        BImageHandle image = renderer.CreateImage(pixels);
        images.Add(image);
        imageCache[imageHandle] = image;
        return image;
    }

    private static bool IsDashedStyle(string style) =>
        string.Equals(style, "dashed", StringComparison.OrdinalIgnoreCase)
        || string.Equals(style, "dotted", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A dashed or dotted side, as a run of solid rectangles. Both styles used to fall through to
    /// the solid case, so a dashed rule was indistinguishable from a solid one.
    /// </summary>
    /// <remarks>
    /// CSS does not specify dash geometry, and engines differ; these are the conventional
    /// proportions — dashes three times the border width with equal gaps, dots one width square
    /// with one width between. The run is fitted to the side's length so it starts and ends on a
    /// dash rather than being cut mid-gap, which is what makes a short side still read as dashed.
    /// </remarks>
    private static void DrawDashedBorderSide(BRenderList list, RectangleF rect, BColor color, string style, double width, double opacity)
    {
        bool horizontal = rect.Width >= rect.Height;
        double length = horizontal ? rect.Width : rect.Height;
        if (length <= 0)
            return;

        bool dotted = string.Equals(style, "dotted", StringComparison.OrdinalIgnoreCase);
        double dash = Math.Max(1d, dotted ? width : width * 3d);
        double gap = Math.Max(1d, width);

        // Fit a whole number of periods so the side ends on a dash. One period minimum: a side
        // shorter than a single dash is drawn solid, which is what every engine does.
        int periods = (int)Math.Round((length + gap) / (dash + gap), MidpointRounding.AwayFromZero);
        if (periods <= 1)
        {
            FillRect(list, rect, color, opacity);
            return;
        }

        double period = (length + gap) / periods;
        double dashLength = Math.Max(1d, period - gap);

        for (int index = 0; index < periods; index++)
        {
            double offset = index * period;
            double span = Math.Min(dashLength, length - offset);
            if (span <= 0)
                break;

            FillRect(
                list,
                horizontal
                    ? new RectangleF((float)(rect.X + offset), rect.Y, (float)span, rect.Height)
                    : new RectangleF(rect.X, (float)(rect.Y + offset), rect.Width, (float)span),
                color,
                opacity);
        }
    }

    /// <summary>
    /// A linear gradient, as bands perpendicular to the gradient line.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This used to paint the first stop as a flat colour, so every gradient on a page came out as
    /// one wrong solid block. <see cref="BRenderList"/> still has no gradient command, but banding
    /// needs none: enough solid strips across the gradient line are indistinguishable from the real
    /// thing at screen resolution, and one strip per pixel of gradient length is the most that can
    /// ever be visible.
    /// </para>
    /// <para>
    /// Bands run along the rotated x axis so any angle is covered by one code path, and the whole
    /// figure is clipped to the fill rect — the rotated band box necessarily overhangs it. Radial
    /// and conic gradients keep the flat-colour fallback: they are not a strip fill, and faking one
    /// would look worse than a solid. A tiled gradient (a <c>background-size</c> smaller than the
    /// box) also keeps it, rather than emitting bands per tile.
    /// </para>
    /// </remarks>
    private static void DrawGradientFallback(BRenderList list, DrawTiledGradientItem item, double opacity)
    {
        if (item.Stops == null || item.Stops.Count == 0 || opacity <= 0)
            return;

        if (!IsDrawable(item.FillRect))
            return;

        if (item.IsRadial || item.IsConic || item.Stops.Count == 1 || !CoversFillRect(item))
        {
            FillRect(list, item.FillRect, item.Stops[0].Color, opacity);
            return;
        }

        RectangleF fill = item.FillRect;
        double angle = ((item.Angle % 360f) + 360f) % 360f;
        double radians = angle * Math.PI / 180d;

        // CSS Images §3.4: 0deg points to the top and angles run clockwise, with y down on screen.
        double dirX = Math.Sin(radians);
        double dirY = -Math.Cos(radians);

        // The gradient line's length across the box, per the same section.
        double length = (Math.Abs(fill.Width * dirX) + Math.Abs(fill.Height * dirY));
        if (length <= 0)
        {
            FillRect(list, fill, item.Stops[0].Color, opacity);
            return;
        }

        int bands = (int)Math.Clamp(Math.Ceiling(length), 2, MaxGradientBands);
        double centreX = fill.X + (fill.Width / 2d);
        double centreY = fill.Y + (fill.Height / 2d);

        list.PushClip(ToRect(fill));

        // Rotate about the box centre so the gradient runs along +x, then lay the bands out as
        // upright rectangles in that space.
        var rotation = new BMatrix3x2(dirX, dirY, -dirY, dirX, 0, 0);
        list.PushTransform(
            BMatrix3x2.Translation(-centreX, -centreY)
            * rotation
            * BMatrix3x2.Translation(centreX, centreY));

        // The rotated band strip must still cover the box's corners.
        double halfSpan = Math.Sqrt((fill.Width * fill.Width) + (fill.Height * fill.Height)) / 2d;
        double bandWidth = length / bands;
        double start = centreX - (length / 2d);

        for (int index = 0; index < bands; index++)
        {
            double position = (index + 0.5d) / bands;
            BColor color = SampleGradient(item.Stops, position);
            if (color.A == 0)
                continue;

            // Overlap by a hair so no seam shows between adjacent bands after rasterisation.
            list.FillRect(
                new BRect(
                    start + (index * bandWidth),
                    centreY - halfSpan,
                    bandWidth + 0.5d,
                    halfSpan * 2d),
                ToColor(color, opacity));
        }

        list.PopTransform();
        list.PopClip();
    }

    /// <summary>Whether one gradient tile covers the whole fill rect, so no tiling is needed.</summary>
    private static bool CoversFillRect(DrawTiledGradientItem item) =>
        string.Equals(item.Repeat, "no-repeat", StringComparison.OrdinalIgnoreCase)
        || ((item.TileWidth <= 0 || item.TileWidth >= item.FillRect.Width)
            && (item.TileHeight <= 0 || item.TileHeight >= item.FillRect.Height));

    /// <summary>
    /// The gradient colour at <paramref name="position"/> (0..1), interpolated in sRGB between the
    /// bracketing stops. Premultiplied so a fade to transparent does not travel through black.
    /// </summary>
    private static BColor SampleGradient(IReadOnlyList<GradientStop> stops, double position)
    {
        if (position <= stops[0].Position)
            return stops[0].Color;

        for (int index = 1; index < stops.Count; index++)
        {
            GradientStop previous = stops[index - 1];
            GradientStop current = stops[index];
            if (position > current.Position)
                continue;

            double span = current.Position - previous.Position;
            double t = span <= 0 ? 0 : (position - previous.Position) / span;
            return Lerp(previous.Color, current.Color, t);
        }

        return stops[^1].Color;
    }

    private static BColor Lerp(BColor from, BColor to, double t)
    {
        double fromA = from.A / 255d;
        double toA = to.A / 255d;
        double alpha = fromA + ((toA - fromA) * t);
        if (alpha <= 0)
            return new BColor(0, 0, 0, 0);

        // Interpolate premultiplied, then un-premultiply: straight interpolation drags a fade to
        // `transparent` (which is transparent *black*) through grey.
        double r = ((from.R * fromA) + (((to.R * toA) - (from.R * fromA)) * t)) / alpha;
        double g = ((from.G * fromA) + (((to.G * toA) - (from.G * fromA)) * t)) / alpha;
        double b = ((from.B * fromA) + (((to.B * toA) - (from.B * fromA)) * t)) / alpha;

        return new BColor(
            (byte)Math.Clamp(Math.Round(r), 0, 255),
            (byte)Math.Clamp(Math.Round(g), 0, 255),
            (byte)Math.Clamp(Math.Round(b), 0, 255),
            (byte)Math.Clamp(Math.Round(alpha * 255d), 0, 255));
    }

    /// <summary>
    /// Enough bands that no seam is visible on a full-screen gradient, and few enough that a
    /// pathological one cannot turn a frame into tens of thousands of fills.
    /// </summary>
    private const int MaxGradientBands = 256;

    private static void DrawLineFallback(BRenderList list, DrawLineItem item, double opacity)
    {
        if (item.Width <= 0 || item.Color.A == 0)
            return;

        if (Math.Abs(item.Start.Y - item.End.Y) < 0.001f)
        {
            float left = Math.Min(item.Start.X, item.End.X);
            float width = Math.Abs(item.End.X - item.Start.X);
            FillRect(list, new RectangleF(left, item.Start.Y - (item.Width / 2f), width, item.Width), item.Color, opacity);
            return;
        }

        if (Math.Abs(item.Start.X - item.End.X) < 0.001f)
        {
            float top = Math.Min(item.Start.Y, item.End.Y);
            float height = Math.Abs(item.End.Y - item.Start.Y);
            FillRect(list, new RectangleF(item.Start.X - (item.Width / 2f), top, item.Width, height), item.Color, opacity);
            return;
        }

        // A diagonal segment: the two cases above are the axis-aligned ones, and anything else used
        // to fall off the end of this method and simply not be drawn — which is every SVG diagonal
        // and every slanted rule. Rotating the coordinate system turns it back into the horizontal
        // case, which needs no primitive BRenderList lacks.
        double dx = item.End.X - item.Start.X;
        double dy = item.End.Y - item.Start.Y;
        double length = Math.Sqrt((dx * dx) + (dy * dy));
        if (length <= 0)
            return;

        double cos = dx / length;
        double sin = dy / length;

        list.PushTransform(new BMatrix3x2(cos, sin, -sin, cos, item.Start.X, item.Start.Y));
        list.FillRect(
            new BRect(0, -item.Width / 2d, length, item.Width),
            ToColor(item.Color, opacity));
        list.PopTransform();
    }

    private static void DrawSvgRect(BRenderList list, DrawSvgRectItem item, double opacity)
    {
        var rect = new RectangleF(item.Bounds.X + item.X, item.Bounds.Y + item.Y, item.Width, item.Height);
        FillRect(list, rect, item.Fill, opacity);
        if (!item.Stroke.IsEmpty && item.StrokeWidth > 0)
            list.StrokeRect(ToRect(rect), ToColor(item.Stroke, opacity), item.StrokeWidth);
    }

    private static BMatrix3x2 ToMatrix(TransformItem item)
    {
        float[] m = item.Matrix;
        if (m.Length < 6)
            return BMatrix3x2.Identity;

        var matrix = new BMatrix3x2(m[0], m[1], m[2], m[3], m[4], m[5]);
        return BMatrix3x2.Translation(-item.OriginX, -item.OriginY)
            * matrix
            * BMatrix3x2.Translation(item.OriginX, item.OriginY);
    }

    private static BRect ToRect(RectangleF rect) => new(rect.X, rect.Y, rect.Width, rect.Height);

    private static BColor ToColor(BColor color, double opacity)
    {
        byte alpha = (byte)Math.Clamp((int)Math.Round(color.A * opacity), 0, 255);
        return new BColor(color.R, color.G, color.B, alpha);
    }

    private static BFontWeight ToFontWeight(string value)
    {
        if (int.TryParse(value, out int numeric))
        {
            if (numeric >= 800) return BFontWeight.Black;
            if (numeric >= 700) return BFontWeight.Bold;
            if (numeric >= 600) return BFontWeight.SemiBold;
            if (numeric >= 500) return BFontWeight.Medium;
            if (numeric <= 300) return BFontWeight.Light;
            return BFontWeight.Normal;
        }

        return value?.ToLowerInvariant() switch
        {
            "bold" or "bolder" => BFontWeight.Bold,
            "600" => BFontWeight.SemiBold,
            "500" => BFontWeight.Medium,
            "lighter" or "light" => BFontWeight.Light,
            _ => BFontWeight.Normal,
        };
    }

    private static bool IsDrawable(RectangleF rect) =>
        rect.Width > 0
        && rect.Height > 0
        && float.IsFinite(rect.X)
        && float.IsFinite(rect.Y)
        && float.IsFinite(rect.Width)
        && float.IsFinite(rect.Height);

    private static bool IsBorderStyleVisible(string style) =>
        !string.IsNullOrEmpty(style)
        && !string.Equals(style, "none", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(style, "hidden", StringComparison.OrdinalIgnoreCase);
}
