using System;
using System.Collections.Generic;
using System.Drawing;
using Broiler.Graphics;

namespace Broiler.HTML.Image;

internal sealed class BCanvas(BBitmap bitmap) : IDisposable
{
    private readonly BBitmap _rootBitmap = bitmap ?? throw new ArgumentNullException(nameof(bitmap));
    private readonly Stack<CanvasState> _stateStack = new();
    private readonly Stack<LayerState> _layerStack = new();
    private readonly List<ClipOperation> _clipOperations = [];

    /// <summary>
    /// Running intersection of the <em>including</em> clip operations, one entry per entry of
    /// <see cref="_clipOperations"/>, in device pixels. The last entry is a bounding box of every
    /// pixel the clip stack can admit.
    /// </summary>
    /// <remarks>
    /// <b>It exists to bound loops, not to decide visibility.</b> <see cref="IsVisible"/> is still
    /// the authority — it handles exclusions, rounded corners and polygons, none of which a
    /// bounding box can express. What the box buys is that a primitive no longer walks pixels the
    /// clip is certain to reject: the sequential rasterizer stops paying per-pixel for the clipped-
    /// away part of a fill, and a tile view (whose tile is one more including clip) draws only its
    /// own rows instead of iterating the whole surface and rejecting most of it. Kept as a running
    /// intersection rather than recomputed so a push or a pop stays O(1).
    /// </remarks>
    private readonly List<RectangleF> _clipBounds = [];

    /// <summary>
    /// Whether this canvas leaves scanline bands to the caller. Set on a tile view: the tiles
    /// already own the cores, and a band split inside one would ask for tiles × cores threads.
    /// </summary>
    private readonly bool _bandsDisabled;

    private PointF _translation;
    private float _scaleX = 1f;
    private float _scaleY = 1f;

    /// <summary>Whether the current mapping mirrors the X axis (a negative <see cref="_scaleX"/>).</summary>
    /// <remarks>
    /// <see cref="Translate(RectangleF)"/> normalises a mirrored rectangle so it encloses the right
    /// pixels, which is all a solid fill needs. A primitive that samples <em>content</em> across
    /// that rectangle — a bitmap, a tile phase, a gradient ramp — needs the mirror as well, or it
    /// paints the unmirrored picture in the mirrored place. These two say which axes to reverse.
    /// </remarks>
    private bool FlipX => _scaleX < 0f;

    /// <summary>Whether the current mapping mirrors the Y axis (a negative <see cref="_scaleY"/>).</summary>
    private bool FlipY => _scaleY < 0f;

    /// <summary>
    /// A view of <paramref name="source"/>'s surface restricted to <paramref name="deviceTile"/>,
    /// carrying the transform and clip state <paramref name="source"/> has right now. See
    /// <see cref="CreateTileView"/>.
    /// </summary>
    private BCanvas(BCanvas source, RectangleF deviceTile)
        : this(source._rootBitmap)
    {
        _translation = source._translation;
        _scaleX = source._scaleX;
        _scaleY = source._scaleY;

        // Already in device space — these were translated when they were pushed, so they go
        // across verbatim rather than through PushClip, which would translate them twice.
        for (int i = 0; i < source._clipOperations.Count; i++)
            AddClip(source._clipOperations[i]);

        AddClip(ClipOperation.Include(deviceTile));
        _bandsDisabled = true;
    }

    /// <summary>Device-pixel width of the surface this canvas draws into.</summary>
    public int SurfaceWidth => _rootBitmap.Width;

    /// <summary>Device-pixel height of the surface this canvas draws into.</summary>
    public int SurfaceHeight => _rootBitmap.Height;

    /// <summary>Whether the surface tolerates pixel writes from more than one thread.</summary>
    public bool SupportsConcurrentPixelWrites => _rootBitmap.SupportsConcurrentPixelWrites;

    /// <summary>
    /// Creates an independent canvas over the same surface that may only write inside
    /// <paramref name="deviceTile"/>, starting from this canvas's current transform and clip.
    /// Multithreading roadmap item #5.
    /// </summary>
    /// <remarks>
    /// The view shares the surface and nothing else: its own clip list, state stack and layer stack
    /// mean two views never touch the same field, and the tile clip means they never touch the same
    /// pixel. Because the transform goes across unchanged, a primitive's device coordinates are
    /// computed by the same arithmetic on the same inputs as in the sequential replay — which is
    /// what makes the tiled image identical rather than merely equivalent. Translating geometry into
    /// tile-local space instead would have re-rounded every coordinate.
    /// </remarks>
    public BCanvas CreateTileView(RectangleF deviceTile) => new(this, deviceTile);

    public void Save() => _stateStack.Push(new CanvasState(_translation, _scaleX, _scaleY, _clipOperations.Count));

    public void Restore()
    {
        if (_stateStack.Count == 0)
            return;

        var state = _stateStack.Pop();
        _translation = state.Translation;
        _scaleX = state.ScaleX;
        _scaleY = state.ScaleY;

        while (_clipOperations.Count > state.ClipOperationCount)
            PopClip();
    }

    public void Translate(float dx, float dy) =>
        _translation = new PointF(_translation.X + dx, _translation.Y + dy);

    /// <summary>Composes a uniform scale about the surface origin (document-root viewport zoom):
    /// draws map point -> point*scale + translation. Uniform-only; byte-identical at scale 1.</summary>
    public void Scale(float scale)
    {
        _scaleX *= scale;
        _scaleY *= scale;
    }

    /// <summary>
    /// Applies a CSS 2D transform (matrix <c>[a,b,c,d,e,f]</c> about origin
    /// <paramref name="originX"/>/<paramref name="originY"/>) to the raster state, saving the prior
    /// state so <see cref="Restore"/> reverses it. Returns <c>false</c> — without touching state —
    /// when the matrix is not expressible on this canvas, which maps a point per axis as
    /// <c>p*scale + translation</c>: rotation and skew (the <c>b</c>/<c>c</c> terms) are rejected so
    /// the caller can route those to the fuller compat backend. Translation and axis-aligned scale —
    /// including <em>non-uniform</em> scale (<c>scaleX</c>/<c>scaleY</c>/<c>scale(x, y)</c>) — are
    /// folded into <see cref="_scaleX"/>/<see cref="_scaleY"/>/<see cref="_translation"/> so
    /// transformed content actually rasterises instead of vanishing when the compat backend is a stub.
    /// <para>
    /// A <em>negative</em> factor is one of those: a mirror (<c>scaleX(-1)</c>, <c>scale(-1)</c>)
    /// and the half-turn <c>rotate(180deg)</c> — whose <c>b</c>/<c>c</c> terms are zero — map each
    /// axis onto itself reversed, which this canvas expresses. See <see cref="FlipX"/> for what the
    /// primitives do with it.
    /// </para>
    /// </summary>
    public bool TrySaveTransform(float[] matrix, float originX, float originY)
    {
        if (matrix is null || matrix.Length < 6)
            return false;

        float a = matrix[0], b = matrix[1], c = matrix[2], d = matrix[3], e = matrix[4], f = matrix[5];

        // Only translation + axis-aligned scale survive this canvas's per-axis point mapping. b/c
        // carry rotation/skew, which the raster canvas cannot express; those still fall back.
        const float epsilon = 1e-4f;
        if (MathF.Abs(b) > epsilon || MathF.Abs(c) > epsilon)
            return false;

        float scaleX = a, scaleY = d;

        // Transform-origin applies to the whole transform, per axis:
        // p' = scale*(p - origin) + origin + (e,f).
        float localTranslateX = originX * (1f - scaleX) + e;
        float localTranslateY = originY * (1f - scaleY) + f;

        // Compose ahead of the existing surface mapping (p -> p*_scale + _translation, per axis): a
        // child point p becomes (scale*p + localTranslate) which the surface then maps, giving
        // p*(scale*_scale) + (localTranslate*_scale + _translation).
        Save();
        _translation = new PointF(
            localTranslateX * _scaleX + _translation.X,
            localTranslateY * _scaleY + _translation.Y);
        _scaleX *= scaleX;
        _scaleY *= scaleY;
        return true;
    }

    public void Clear(BColor color)
    {
        CurrentTarget.ErasePixels(color);
    }

    public void PushClip(RectangleF rect) => AddClip(ClipOperation.Include(Translate(rect)));

    public void PushClipExclude(RectangleF rect) => AddClip(ClipOperation.Exclude(Translate(rect)));

    /// <summary>
    /// Clips subsequent drawing to an arbitrary closed polygon (CSS <c>clip-path: polygon()</c>).
    /// Vertices are in canvas-local coordinates and go through the same surface mapping as every
    /// other geometry. Fewer than three vertices encloses no area, so it clips everything away.
    /// </summary>
    public void PushClipPolygon(IReadOnlyList<PointF> points)
    {
        if (points is null || points.Count < 3)
        {
            AddClip(ClipOperation.Include(RectangleF.Empty));
            return;
        }

        var translated = new PointF[points.Count];
        for (int i = 0; i < points.Count; i++)
            translated[i] = Translate(points[i]);

        AddClip(ClipOperation.IncludePolygon(translated));
    }

    public void PushClipRounded(
        RectangleF rect,
        double cornerNw,
        double cornerNwY,
        double cornerNe,
        double cornerNeY,
        double cornerSe,
        double cornerSeY,
        double cornerSw,
        double cornerSwY)
    {
        // A mirrored axis moves each corner to the opposite side of the normalised rectangle, so
        // the radii travel with it: the north-west corner of a horizontally mirrored box is drawn
        // where north-east now is. Scaling a radius by a negative axis would also make it negative,
        // which no corner can be.
        float sx = MathF.Abs(_scaleX);
        float sy = MathF.Abs(_scaleY);
        var corners = new[]
        {
            ((float)cornerNw * sx, (float)cornerNwY * sy),
            ((float)cornerNe * sx, (float)cornerNeY * sy),
            ((float)cornerSe * sx, (float)cornerSeY * sy),
            ((float)cornerSw * sx, (float)cornerSwY * sy),
        };

        // Indices into `corners` for NW, NE, SE, SW after the mirrors.
        int nw = 0, ne = 1, se = 2, sw = 3;
        if (FlipX)
            (nw, ne, se, sw) = (ne, nw, sw, se);
        if (FlipY)
            (nw, ne, se, sw) = (sw, se, ne, nw);

        AddClip(ClipOperation.IncludeRounded(
            Translate(rect),
            corners[nw].Item1, corners[nw].Item2,
            corners[ne].Item1, corners[ne].Item2,
            corners[se].Item1, corners[se].Item2,
            corners[sw].Item1, corners[sw].Item2));
    }

    public void PopClip()
    {
        if (_clipOperations.Count == 0)
            return;

        _clipOperations.RemoveAt(_clipOperations.Count - 1);
        _clipBounds.RemoveAt(_clipBounds.Count - 1);
    }

    /// <summary>
    /// Appends a clip operation and the bounding box the stack admits once it is in effect.
    /// </summary>
    /// <remarks>
    /// An <em>excluding</em> operation carries the running box forward unchanged: it removes pixels
    /// from the admitted set and can never add one, so it cannot narrow a bound that has to stay a
    /// superset of what <see cref="IsVisible"/> accepts. A rounded or polygon clip narrows to its
    /// bounding box, which is exactly what <see cref="ClipOperation.Rect"/> already holds.
    /// </remarks>
    private void AddClip(ClipOperation operation)
    {
        var previous = _clipBounds.Count > 0 ? _clipBounds[^1] : (RectangleF?)null;
        var bounds = operation.IsExclude
            ? previous ?? SurfaceBounds
            : previous is { } current ? RectangleF.Intersect(current, operation.Rect) : operation.Rect;

        _clipOperations.Add(operation);
        _clipBounds.Add(bounds);
    }

    /// <summary>
    /// Stands in for "nothing has narrowed the clip yet" when an excluding operation arrives first,
    /// so the running list stays dense and every entry is a real rectangle.
    /// </summary>
    /// <remarks>
    /// The surface, not an enormous rectangle: the box only ever has to be a superset of the pixels
    /// that can be written, and no pixel outside the surface can be. A sentinel built from
    /// <c>float.MaxValue</c> would be a superset too, and would then be cast to <c>int</c> by
    /// <see cref="NarrowToClip"/> — a conversion that is undefined once the value leaves
    /// <c>int</c>'s range. Every layer buffer is allocated at the surface's size, so this bound
    /// holds whichever target is current.
    /// </remarks>
    private RectangleF SurfaceBounds => new(0f, 0f, _rootBitmap.Width, _rootBitmap.Height);

    /// <summary>
    /// Device-space bounding box of everything the clip stack can admit, or <c>null</c> when
    /// nothing has narrowed it.
    /// </summary>
    private RectangleF? CurrentClipBounds => _clipBounds.Count > 0 ? _clipBounds[^1] : null;

    public void FillRect(RectangleF rect, BColor color)
    {
        var translated = Translate(rect);
        int minX = (int)Math.Floor(translated.Left);
        int minY = (int)Math.Floor(translated.Top);
        int maxX = (int)Math.Ceiling(translated.Right) - 1;
        int maxY = (int)Math.Ceiling(translated.Bottom) - 1;
        if (!NarrowToClip(ref minX, ref minY, ref maxX, ref maxY))
            return;

        ForEachBand(minY, maxY, minX, maxX, (fromY, toY) =>
        {
            for (int y = fromY; y <= toY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    if (!IsVisible(x, y))
                        continue;

                    BlendPixel(CurrentTarget, x, y, color, blendMode: "normal");
                }
            }
        });
    }

    /// <summary>
    /// Splits a fill's scanlines into bands across threads, or runs them inline when the fill is
    /// too small to be worth it. Multithreading roadmap item #4; the reasoning is on
    /// <see cref="BRasterParallelism"/>.
    /// </summary>
    /// <remarks>
    /// Takes the clipped pixel bounds rather than a row count because the decision is about area:
    /// a hundred-row fill one pixel wide is not worth a thread and a two-row fill across a 4K
    /// surface may be. <see cref="CurrentTarget"/> is read here — once, before any band starts —
    /// so the layer a fill draws into is fixed for the whole fill, exactly as it is in the
    /// sequential path.
    /// </remarks>
    private void ForEachBand(int minY, int maxY, int minX, int maxX, Action<int, int> band) =>
        BRasterParallelism.ForEachBand(
            minY,
            maxY,
            maxX - minX + 1,
            !_bandsDisabled && CurrentTarget.SupportsConcurrentPixelWrites,
            band);

    public void DrawLine(PointF start, PointF end, BColor color, float strokeWidth = 1f)
    {
        var p1 = Translate(start);
        var p2 = Translate(end);
        // Stroke width has no single value under non-uniform scale; use the geometric mean of the
        // axis scales (equal to the uniform scale when scaleX == scaleY, so byte-identical there).
        // Absolute values: a mirrored axis is still the same magnification, and a single negative
        // factor would make the product negative and the square root NaN.
        float radius = Math.Max(0.5f, strokeWidth * MathF.Sqrt(MathF.Abs(_scaleX * _scaleY)) / 2f);

        int minX = (int)Math.Floor(Math.Min(p1.X, p2.X) - radius);
        int minY = (int)Math.Floor(Math.Min(p1.Y, p2.Y) - radius);
        int maxX = (int)Math.Ceiling(Math.Max(p1.X, p2.X) + radius);
        int maxY = (int)Math.Ceiling(Math.Max(p1.Y, p2.Y) + radius);
        if (!NarrowToClip(ref minX, ref minY, ref maxX, ref maxY))
            return;

        ForEachBand(minY, maxY, minX, maxX, (fromY, toY) =>
        {
            for (int y = fromY; y <= toY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    if (!IsVisible(x, y))
                        continue;

                    float distance = DistanceToSegment(x + 0.5f, y + 0.5f, p1, p2);
                    if (distance <= radius)
                        BlendPixel(CurrentTarget, x, y, color, blendMode: "normal");
                }
            }
        });
    }

    public void DrawRectangleStroke(RectangleF rect, BColor color, float strokeWidth = 1f)
    {
        strokeWidth = Math.Max(1f, strokeWidth);
        FillRect(new RectangleF(rect.X, rect.Y, rect.Width, strokeWidth), color);
        FillRect(new RectangleF(rect.X, rect.Bottom - strokeWidth, rect.Width, strokeWidth), color);
        FillRect(new RectangleF(rect.X, rect.Y, strokeWidth, rect.Height), color);
        FillRect(new RectangleF(rect.Right - strokeWidth, rect.Y, strokeWidth, rect.Height), color);
    }

    public void FillPolygon(PointF[] points, BColor color)
    {
        if (points == null || points.Length < 3)
            return;

        var translated = new PointF[points.Length];
        float minX = float.PositiveInfinity;
        float minY = float.PositiveInfinity;
        float maxX = float.NegativeInfinity;
        float maxY = float.NegativeInfinity;

        for (int i = 0; i < points.Length; i++)
        {
            var point = Translate(points[i]);
            translated[i] = point;
            minX = Math.Min(minX, point.X);
            minY = Math.Min(minY, point.Y);
            maxX = Math.Max(maxX, point.X);
            maxY = Math.Max(maxY, point.Y);
        }

        int startX = (int)Math.Floor(minX);
        int startY = (int)Math.Floor(minY);
        int endX = (int)Math.Ceiling(maxX);
        int endY = (int)Math.Ceiling(maxY);
        if (!NarrowToClip(ref startX, ref startY, ref endX, ref endY))
            return;

        // A border side's mitres are anti-aliased; its own axis-aligned edges, and every other
        // polygon, are filled by testing the pixel centre as they always have been. See
        // Broiler.Layout.Engine.BorderAntialiasing for why this is a lever the border path pins
        // rather than the rasteriser's default: two shapes sharing an edge, each blended
        // independently, leave the background showing through the seam.
        bool antialias = Broiler.Layout.Engine.BorderAntialiasing.Active;

        ForEachBand(startY, endY, startX, endX, (fromY, toY) =>
        {
            for (int y = fromY; y <= toY; y++)
            {
                for (int x = startX; x <= endX; x++)
                {
                    if (!IsVisible(x, y))
                        continue;

                    if (!antialias)
                    {
                        if (ContainsPolygonPoint(translated, x + 0.5f, y + 0.5f))
                            BlendPixel(CurrentTarget, x, y, color, blendMode: "normal");
                        continue;
                    }

                    float coverage = Broiler.Layout.Engine.BorderAntialiasing.Coverage(translated, x, y);
                    if (coverage <= 0f)
                        continue;

                    var covered = coverage >= 1f
                        ? color
                        : BColor.FromArgb((byte)Math.Clamp((int)Math.Round(color.A * coverage), 0, 255),
                            color.R, color.G, color.B);
                    if (covered.A > 0)
                        BlendPixel(CurrentTarget, x, y, covered, blendMode: "normal");
                }
            }
        });
    }

    /// <summary>
    /// Fills glyph contours (closed polygons given in user-space pixels) using
    /// the non-zero winding rule with anti-aliasing (vertical supersampling plus
    /// horizontal fractional coverage).  Used by the text backend to rasterise
    /// scaled glyph outlines.  The <paramref name="color"/>'s alpha is modulated
    /// by per-pixel coverage.
    /// </summary>
    public void FillGlyphContours(IReadOnlyList<PointF[]> contours, BColor color)
    {
        if (contours == null || contours.Count == 0 || color.A == 0)
            return;

        // Reject the glyph on its bounding box before transforming and copying its points. Text is
        // the one primitive a page issues thousands of times — and under a tile view all but a
        // tile's share of them miss the clip entirely — so the allocation below is worth skipping
        // rather than doing and then discarding. The box goes through the same affine mapping as
        // the points do, so a glyph that survives it is measured no differently than before.
        if (!IntersectsClip(Translate(BoundingBox(contours))))
            return;

        // Transform contours to device space and compute the pixel bounds.
        float minXf = float.PositiveInfinity, minYf = float.PositiveInfinity;
        float maxXf = float.NegativeInfinity, maxYf = float.NegativeInfinity;
        var devContours = new PointF[contours.Count][];
        for (int ci = 0; ci < contours.Count; ci++)
        {
            var src = contours[ci];
            var dst = new PointF[src.Length];
            for (int i = 0; i < src.Length; i++)
            {
                var p = Translate(src[i]);
                dst[i] = p;
                if (p.X < minXf) minXf = p.X;
                if (p.Y < minYf) minYf = p.Y;
                if (p.X > maxXf) maxXf = p.X;
                if (p.Y > maxYf) maxYf = p.Y;
            }
            devContours[ci] = dst;
        }

        if (float.IsInfinity(minXf))
            return;

        int minX = (int)Math.Floor(minXf);
        int minY = (int)Math.Floor(minYf);
        int maxX = (int)Math.Ceiling(maxXf);
        int maxY = (int)Math.Ceiling(maxYf);
        if (!NarrowToClip(ref minX, ref minY, ref maxX, ref maxY))
            return;

        const int subSamples = 4;
        int width = maxX - minX + 1;

        // The coverage accumulator and the crossing list are per band, not per canvas: they are
        // the only mutable state a scanline carries, and giving each band its own is what lets the
        // bands run at once. A glyph is normally far below the parallel threshold and takes the
        // inline path with exactly one band — this matters for the large fills (drop caps, SVG
        // outlines, headline text) that are not.
        ForEachBand(minY, maxY, minX, maxX, (fromY, toY) =>
        {
            var coverage = new float[width];
            var crossings = new List<(float x, int dir)>(16);

            for (int y = fromY; y <= toY; y++)
            {
                Array.Clear(coverage, 0, width);

                for (int s = 0; s < subSamples; s++)
                {
                    float sampleY = y + (s + 0.5f) / subSamples;
                    crossings.Clear();

                    foreach (var poly in devContours)
                    {
                        int n = poly.Length;
                        for (int i = 0; i < n; i++)
                        {
                            PointF p0 = poly[i];
                            PointF p1 = poly[(i + 1) % n];
                            if (p0.Y == p1.Y)
                                continue;

                            float lo = Math.Min(p0.Y, p1.Y);
                            float hi = Math.Max(p0.Y, p1.Y);
                            if (sampleY < lo || sampleY >= hi)
                                continue;

                            float t = (sampleY - p0.Y) / (p1.Y - p0.Y);
                            float xCross = p0.X + t * (p1.X - p0.X);
                            crossings.Add((xCross, p1.Y > p0.Y ? 1 : -1));
                        }
                    }

                    if (crossings.Count < 2)
                        continue;

                    crossings.Sort(static (l, r) => l.x.CompareTo(r.x));

                    int winding = 0;
                    for (int i = 0; i < crossings.Count - 1; i++)
                    {
                        winding += crossings[i].dir;
                        if (winding != 0)
                            AccumulateGlyphSpan(coverage, minX, crossings[i].x, crossings[i + 1].x, 1f / subSamples);
                    }
                }

                for (int i = 0; i < width; i++)
                {
                    float cov = coverage[i];
                    if (cov <= 0f)
                        continue;
                    if (cov > 1f)
                        cov = 1f;

                    int x = minX + i;
                    if (!IsVisible(x, y))
                        continue;

                    byte a = (byte)Math.Clamp((int)Math.Round(color.A * cov), 0, 255);
                    if (a == 0)
                        continue;

                    BlendPixel(CurrentTarget, x, y, new BColor(color.R, color.G, color.B, a), blendMode: "normal");
                }
            }
        });
    }

    /// <summary>User-space bounding box of a set of contours, empty when they hold no points.</summary>
    private static RectangleF BoundingBox(IReadOnlyList<PointF[]> contours)
    {
        float minX = float.PositiveInfinity, minY = float.PositiveInfinity;
        float maxX = float.NegativeInfinity, maxY = float.NegativeInfinity;
        for (int ci = 0; ci < contours.Count; ci++)
        {
            var points = contours[ci];
            for (int i = 0; i < points.Length; i++)
            {
                var p = points[i];
                if (p.X < minX) minX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.X > maxX) maxX = p.X;
                if (p.Y > maxY) maxY = p.Y;
            }
        }

        return float.IsInfinity(minX)
            ? RectangleF.Empty
            : new RectangleF(minX, minY, maxX - minX, maxY - minY);
    }

    private static void AccumulateGlyphSpan(float[] coverage, int minX, float spanStart, float spanEnd, float weight)
    {
        if (spanEnd <= spanStart)
            return;

        int width = coverage.Length;
        int ixStart = Math.Max(0, (int)Math.Floor(spanStart) - minX);
        int ixEnd = Math.Min(width, (int)Math.Ceiling(spanEnd) - minX);

        for (int ix = ixStart; ix < ixEnd; ix++)
        {
            float pixelLeft = minX + ix;
            float pixelRight = pixelLeft + 1f;
            float covLeft = Math.Max(spanStart, pixelLeft);
            float covRight = Math.Min(spanEnd, pixelRight);
            float frac = covRight - covLeft;
            if (frac > 0f)
                coverage[ix] += frac * weight;
        }
    }

    public void DrawBitmap(BBitmap source, RectangleF destRect, RectangleF srcRect)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (destRect.Width <= 0 || destRect.Height <= 0 || srcRect.Width <= 0 || srcRect.Height <= 0)
            return;

        var translatedDest = Translate(destRect);
        int startX = (int)Math.Floor(translatedDest.Left);
        int startY = (int)Math.Floor(translatedDest.Top);
        int endX = (int)Math.Ceiling(translatedDest.Right) - 1;
        int endY = (int)Math.Ceiling(translatedDest.Bottom) - 1;
        if (!NarrowToClip(ref startX, ref startY, ref endX, ref endY))
            return;

        bool flipX = FlipX;
        bool flipY = FlipY;

        // A scaled draw needs a filter. Point sampling is exact when the destination is the
        // source's own size, but at any other size it quantises every sample to one source
        // pixel: at a 330->320 downscale it drops ten columns outright, and a photo comes out
        // visibly different from what a browser draws even though the layout is right.
        // Bilinear is what the reference engine's default scaling quality matches.
        bool scaled = Math.Abs(translatedDest.Width - srcRect.Width) > 0.01f
                   || Math.Abs(translatedDest.Height - srcRect.Height) > 0.01f;

        ForEachBand(startY, endY, startX, endX, (fromY, toY) =>
        {
            for (int y = fromY; y <= toY; y++)
            {
                for (int x = startX; x <= endX; x++)
                {
                    if (!IsVisible(x, y))
                        continue;

                    float normalizedX = ((x + 0.5f) - translatedDest.Left) / translatedDest.Width;
                    float normalizedY = ((y + 0.5f) - translatedDest.Top) / translatedDest.Height;
                    if (normalizedX < 0f || normalizedX >= 1f || normalizedY < 0f || normalizedY >= 1f)
                        continue;

                    // Under a mirrored axis the destination rectangle was normalised, so walking it
                    // left-to-right walks the source right-to-left: read the source from the far end.
                    if (flipX)
                        normalizedX = 1f - normalizedX;
                    if (flipY)
                        normalizedY = 1f - normalizedY;

                    BColor sample;
                    if (scaled)
                    {
                        sample = SampleBilinear(
                            source,
                            srcRect.Left + (normalizedX * srcRect.Width),
                            srcRect.Top + (normalizedY * srcRect.Height));
                    }
                    else
                    {
                        int srcX = Math.Clamp((int)Math.Floor(srcRect.Left + (normalizedX * srcRect.Width)), 0, source.Width - 1);
                        int srcY = Math.Clamp((int)Math.Floor(srcRect.Top + (normalizedY * srcRect.Height)), 0, source.Height - 1);
                        sample = source.GetPixel(srcX, srcY);
                    }

                    BlendPixel(CurrentTarget, x, y, sample, blendMode: "normal");
                }
            }
        });
    }

    /// <summary>
    /// Bilinear sample of <paramref name="source"/> at a continuous pixel-centre coordinate.
    /// Interpolation runs on premultiplied components so a transparent neighbour contributes its
    /// coverage without dragging its colour in, which is what would fringe the edge of a scaled
    /// sprite. Coordinates outside the bitmap clamp to the edge texel.
    /// </summary>
    private static BColor SampleBilinear(BBitmap source, float sampleX, float sampleY)
    {
        float x = sampleX - 0.5f;
        float y = sampleY - 0.5f;
        int x0 = (int)Math.Floor(x);
        int y0 = (int)Math.Floor(y);
        float tx = x - x0;
        float ty = y - y0;

        int left = Math.Clamp(x0, 0, source.Width - 1);
        int right = Math.Clamp(x0 + 1, 0, source.Width - 1);
        int top = Math.Clamp(y0, 0, source.Height - 1);
        int bottom = Math.Clamp(y0 + 1, 0, source.Height - 1);

        BColor topLeft = source.GetPixel(left, top);
        BColor topRight = source.GetPixel(right, top);
        BColor bottomLeft = source.GetPixel(left, bottom);
        BColor bottomRight = source.GetPixel(right, bottom);

        float w00 = (1f - tx) * (1f - ty);
        float w10 = tx * (1f - ty);
        float w01 = (1f - tx) * ty;
        float w11 = tx * ty;

        float alpha = (topLeft.A * w00) + (topRight.A * w10) + (bottomLeft.A * w01) + (bottomRight.A * w11);
        if (alpha <= 0.5f)
            return BColor.Transparent;

        float red = (topLeft.R * topLeft.A * w00) + (topRight.R * topRight.A * w10)
                  + (bottomLeft.R * bottomLeft.A * w01) + (bottomRight.R * bottomRight.A * w11);
        float green = (topLeft.G * topLeft.A * w00) + (topRight.G * topRight.A * w10)
                    + (bottomLeft.G * bottomLeft.A * w01) + (bottomRight.G * bottomRight.A * w11);
        float blue = (topLeft.B * topLeft.A * w00) + (topRight.B * topRight.A * w10)
                   + (bottomLeft.B * bottomLeft.A * w01) + (bottomRight.B * bottomRight.A * w11);

        static byte ToByte(float value) => (byte)Math.Clamp((int)MathF.Round(value), 0, 255);

        return new BColor(ToByte(red / alpha), ToByte(green / alpha), ToByte(blue / alpha), ToByte(alpha));
    }

    public void DrawPathStroke(IReadOnlyList<PointF> points, BColor color, float strokeWidth = 1f)
    {
        if (points == null || points.Count < 2)
            return;

        for (int i = 1; i < points.Count; i++)
            DrawLine(points[i - 1], points[i], color, strokeWidth);
    }

    public void FillRectTiled(BBitmap source, RectangleF destRect, RectangleF srcRect, PointF tileOrigin)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (destRect.Width <= 0 || destRect.Height <= 0 || srcRect.Width <= 0 || srcRect.Height <= 0)
            return;

        var translatedDest = Translate(destRect);
        var translatedOrigin = Translate(tileOrigin);
        int minX = (int)Math.Floor(translatedDest.Left);
        int minY = (int)Math.Floor(translatedDest.Top);
        int maxX = (int)Math.Ceiling(translatedDest.Right) - 1;
        int maxY = (int)Math.Ceiling(translatedDest.Bottom) - 1;
        if (!NarrowToClip(ref minX, ref minY, ref maxX, ref maxY))
            return;

        bool flipX = FlipX;
        bool flipY = FlipY;

        ForEachBand(minY, maxY, minX, maxX, (fromY, toY) =>
        {
            for (int y = fromY; y <= toY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    if (!IsVisible(x, y))
                        continue;

                    float sampleX = x + 0.5f;
                    float sampleY = y + 0.5f;

                    // The tile phase runs along the element's own axes. A mirrored axis reverses
                    // the offset from the tile origin, which mirrors both the tiling order and the
                    // tile's own content — reading the source at the local coordinate does both.
                    float phaseX = sampleX - translatedOrigin.X;
                    float phaseY = sampleY - translatedOrigin.Y;
                    if (flipX)
                        phaseX = -phaseX;
                    if (flipY)
                        phaseY = -phaseY;

                    int srcX = Math.Clamp(
                        (int)Math.Floor(srcRect.Left + PositiveModulo(phaseX, srcRect.Width)),
                        0,
                        source.Width - 1);
                    int srcY = Math.Clamp(
                        (int)Math.Floor(srcRect.Top + PositiveModulo(phaseY, srcRect.Height)),
                        0,
                        source.Height - 1);
                    BlendPixel(CurrentTarget, x, y, source.GetPixel(srcX, srcY), blendMode: "normal");
                }
            }
        });
    }

    public void FillLinearGradientRect(RectangleF rect, IReadOnlyList<BColor> colors, IReadOnlyList<float>? positions, float angle)
    {
        if (colors == null || colors.Count == 0 || rect.Width <= 0 || rect.Height <= 0)
            return;

        if (colors.Count == 1)
        {
            FillRect(rect, colors[0]);
            return;
        }

        var translatedRect = Translate(rect);
        int minX = (int)Math.Floor(translatedRect.Left);
        int minY = (int)Math.Floor(translatedRect.Top);
        int maxX = (int)Math.Ceiling(translatedRect.Right) - 1;
        int maxY = (int)Math.Ceiling(translatedRect.Bottom) - 1;
        if (!NarrowToClip(ref minX, ref minY, ref maxX, ref maxY))
            return;
        var normalizedPositions = NormalizeGradientPositions(colors.Count, positions);
        var (startPoint, endPoint) = GetGradientEndpoints(translatedRect, angle);

        // The gradient line is stated in the element's own space. A mirrored axis reflects it
        // inside the (already normalised) rectangle — reflecting the two endpoints is that same
        // reflection, and it keeps the ramp attached to the edges the author aimed it at.
        startPoint = MirrorInto(startPoint, translatedRect);
        endPoint = MirrorInto(endPoint, translatedRect);

        float gradientX = endPoint.X - startPoint.X;
        float gradientY = endPoint.Y - startPoint.Y;
        float gradientLengthSquared = (gradientX * gradientX) + (gradientY * gradientY);

        if (gradientLengthSquared <= 0f)
        {
            FillRect(rect, colors[^1]);
            return;
        }

        ForEachBand(minY, maxY, minX, maxX, (fromY, toY) =>
        {
            for (int y = fromY; y <= toY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    if (!IsVisible(x, y))
                        continue;

                    float sampleX = x + 0.5f;
                    float sampleY = y + 0.5f;
                    float t = (((sampleX - startPoint.X) * gradientX) + ((sampleY - startPoint.Y) * gradientY)) / gradientLengthSquared;
                    var color = SampleGradientColor(colors, normalizedPositions, Math.Clamp(t, 0f, 1f));
                    BlendPixel(CurrentTarget, x, y, color, blendMode: "normal");
                }
            }
        });
    }

    /// <summary>
    /// Fills a rectangle with a radial gradient.  The gradient centre is given as
    /// normalised fractions of the rectangle dimensions (<paramref name="centerX"/>
    /// and <paramref name="centerY"/> are in the range 0.0–1.0).  The gradient
    /// radius extends to the farthest corner of the rectangle from the centre,
    /// matching the CSS <c>farthest-corner</c> keyword behaviour.
    /// </summary>
    public void FillRadialGradientRect(RectangleF rect, IReadOnlyList<BColor> colors, IReadOnlyList<float>? positions, float centerX, float centerY)
    {
        if (colors == null || colors.Count == 0 || rect.Width <= 0 || rect.Height <= 0)
            return;

        if (colors.Count == 1)
        {
            FillRect(rect, colors[0]);
            return;
        }

        var translatedRect = Translate(rect);
        int minX = (int)Math.Floor(translatedRect.Left);
        int minY = (int)Math.Floor(translatedRect.Top);
        int maxX = (int)Math.Ceiling(translatedRect.Right) - 1;
        int maxY = (int)Math.Ceiling(translatedRect.Bottom) - 1;
        if (!NarrowToClip(ref minX, ref minY, ref maxX, ref maxY))
            return;

        var normalizedPositions = NormalizeGradientPositions(colors.Count, positions);

        // The centre is a fraction of the element's own box, so a mirrored axis measures it from
        // the opposite edge of the (already normalised) rectangle.
        float cx = translatedRect.Left + ((FlipX ? 1f - centerX : centerX) * translatedRect.Width);
        float cy = translatedRect.Top + ((FlipY ? 1f - centerY : centerY) * translatedRect.Height);

        // Radii to farthest corner (elliptical, one per axis).
        float rx = Math.Max(Math.Abs(cx - translatedRect.Left), Math.Abs(cx - translatedRect.Right));
        float ry = Math.Max(Math.Abs(cy - translatedRect.Top), Math.Abs(cy - translatedRect.Bottom));

        if (rx <= 0 || ry <= 0)
            return;

        ForEachBand(minY, maxY, minX, maxX, (fromY, toY) =>
        {
            for (int y = fromY; y <= toY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    if (!IsVisible(x, y))
                        continue;

                    float dx = (x + 0.5f - cx) / rx;
                    float dy = (y + 0.5f - cy) / ry;
                    float t = Math.Clamp((float)Math.Sqrt((dx * dx) + (dy * dy)), 0f, 1f);
                    var color = SampleGradientColor(colors, normalizedPositions, t);
                    BlendPixel(CurrentTarget, x, y, color, blendMode: "normal");
                }
            }
        });
    }

    /// <summary>
    /// Fills a rectangle with a conic (angular) gradient.  The gradient sweeps
    /// colours around the centre (given as normalised fractions of the
    /// rectangle).  <paramref name="fromAngleDeg"/> is the starting angle in
    /// degrees, measured clockwise from 12 o'clock per the CSS
    /// <c>conic-gradient()</c> convention.  Stop <paramref name="positions"/>
    /// are fractions of a full turn (0.0 = 0deg, 1.0 = 360deg).
    /// </summary>
    public void FillConicGradientRect(RectangleF rect, IReadOnlyList<BColor> colors, IReadOnlyList<float>? positions, float centerX, float centerY, float fromAngleDeg)
    {
        if (colors == null || colors.Count == 0 || rect.Width <= 0 || rect.Height <= 0)
            return;

        if (colors.Count == 1)
        {
            FillRect(rect, colors[0]);
            return;
        }

        var translatedRect = Translate(rect);
        int minX = (int)Math.Floor(translatedRect.Left);
        int minY = (int)Math.Floor(translatedRect.Top);
        int maxX = (int)Math.Ceiling(translatedRect.Right) - 1;
        int maxY = (int)Math.Ceiling(translatedRect.Bottom) - 1;
        if (!NarrowToClip(ref minX, ref minY, ref maxX, ref maxY))
            return;

        var normalizedPositions = NormalizeGradientPositions(colors.Count, positions);

        // The centre is a fraction of the element's own box, so a mirrored axis measures it from
        // the opposite edge of the (already normalised) rectangle.
        float cx = translatedRect.Left + ((FlipX ? 1f - centerX : centerX) * translatedRect.Width);
        float cy = translatedRect.Top + ((FlipY ? 1f - centerY : centerY) * translatedRect.Height);

        bool flipX = FlipX;
        bool flipY = FlipY;

        ForEachBand(minY, maxY, minX, maxX, (fromY, toY) =>
        {
            for (int y = fromY; y <= toY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    if (!IsVisible(x, y))
                        continue;

                    float dx = x + 0.5f - cx;
                    float dy = y + 0.5f - cy;

                    // The sweep is measured in the element's own space. Reversing the offset on a
                    // mirrored axis reflects the sweep with the element, so a single mirrored axis
                    // reverses the colour order and mirroring both is the 180deg rotation it is.
                    if (flipX)
                        dx = -dx;
                    if (flipY)
                        dy = -dy;

                    // atan2(dx, -dy) yields 0 at 12 o'clock and increases clockwise.
                    float angleDeg = (float)(Math.Atan2(dx, -dy) * 180.0 / Math.PI);
                    float t = PositiveModulo(angleDeg - fromAngleDeg, 360f) / 360f;

                    var color = SampleGradientColor(colors, normalizedPositions, t);
                    BlendPixel(CurrentTarget, x, y, color, blendMode: "normal");
                }
            }
        });
    }

    public void SaveOpacityLayer(float opacity)
    {
        _layerStack.Push(new LayerState(
            new BBitmap(_rootBitmap.Width, _rootBitmap.Height), opacity, "normal", CurrentClipBounds));
    }

    public void RestoreOpacityLayer()
    {
        if (_layerStack.Count == 0)
            return;

        var layer = _layerStack.Pop();
        CompositeLayer(layer);
    }

    public void SaveFilterLayer(string filter)
    {
        _layerStack.Push(new LayerState(
            new BBitmap(_rootBitmap.Width, _rootBitmap.Height), 1f, "normal", CurrentClipBounds, filter));
    }

    public void RestoreFilterLayer()
    {
        if (_layerStack.Count == 0)
            return;

        var layer = _layerStack.Pop();
        CompositeLayer(layer);
    }

    public void SaveBlendLayer(string blendMode)
    {
        _layerStack.Push(new LayerState(
            new BBitmap(_rootBitmap.Width, _rootBitmap.Height), 1f, blendMode ?? "normal", CurrentClipBounds));
    }

    public void RestoreBlendLayer()
    {
        if (_layerStack.Count == 0)
            return;

        var layer = _layerStack.Pop();
        CompositeLayer(layer);
    }

    /// <summary>
    /// Opens a layer for a CSS <c>transform</c> this canvas's per-axis mapping cannot express — a
    /// rotation or a skew. The contents are drawn into it with the mapping unchanged, so every
    /// primitive keeps the arithmetic it already has, and <see cref="RestoreWarpLayer"/> resamples
    /// the finished layer through the matrix. Returns <see langword="false"/> — having pushed
    /// nothing — for a matrix <see cref="TrySaveTransform"/> should have taken, and for a singular
    /// one, which has no source point to fetch back for a destination pixel.
    /// </summary>
    public bool TrySaveWarpLayer(float[] matrix, float originX, float originY)
    {
        if (!Broiler.Layout.IR.AffineLayerMap.TryCreate(
                matrix, originX, originY, _scaleX, _scaleY, _translation.X, _translation.Y, out var warp))
            return false;

        // The clips in force belong to the *transformed* result, not to the content on its way
        // into the layer: an ancestor's overflow or clip-path bounds where the element ends up
        // (CSS Transforms 1 §3), and the tile clip of a parallel replay bounds which slice of the
        // surface this canvas may write. Leaving either in force while the layer fills would clip
        // in pre-transform space — wrong for the first, and for the second a seam: content just
        // outside a tile that rotates into it would be clipped away before it could. So they are
        // set aside, and applied at the composite instead, where the content is where CSS puts it.
        //
        // What replaces them is the *pre-image* of what is still visible: the part of the layer
        // that can land inside the suspended clip, and no more. Content outside it cannot reach
        // the surface however it is transformed, so clipping it away changes no pixel — and
        // leaving the stack empty instead is what a first cut did, which cost a factor of ten on
        // the whole suite: `_clipBounds` is what bounds a rasterizer's loops, and with nothing in
        // it every fill inside a warp layer walks the entire surface.
        var visible = CurrentClipBounds ?? SurfaceBounds;
        var reachable = RectangleF.Intersect(warp.InverseMapBounds(visible), SurfaceBounds);

        var suspendedClips = new List<ClipOperation>(_clipOperations);
        var suspendedBounds = new List<RectangleF>(_clipBounds);
        _clipOperations.Clear();
        _clipBounds.Clear();
        AddClip(ClipOperation.Include(reachable));

        _layerStack.Push(new LayerState(
            new BBitmap(_rootBitmap.Width, _rootBitmap.Height), 1f, "normal", reachable, null, warp,
            suspendedClips, suspendedBounds));
        return true;
    }

    /// <summary>Closes the layer <see cref="TrySaveWarpLayer"/> opened, resampling it onto the
    /// surface through the transform.</summary>
    public void RestoreWarpLayer()
    {
        if (_layerStack.Count == 0)
            return;

        var layer = _layerStack.Pop();

        // Back in force before the composite, which is the point: IsVisible there is the one
        // place the suspended clips apply, and it applies them to the transformed result.
        if (layer.SuspendedClipOperations is { } clips && layer.SuspendedClipBounds is { } bounds)
        {
            _clipOperations.Clear();
            _clipOperations.AddRange(clips);
            _clipBounds.Clear();
            _clipBounds.AddRange(bounds);
        }

        CompositeLayer(layer);
    }

    public void Dispose()
    {
        while (_layerStack.Count > 0)
            _layerStack.Pop().Bitmap.Dispose();
    }

    private BBitmap CurrentTarget => _layerStack.Count > 0 ? _layerStack.Peek().Bitmap : _rootBitmap;

    /// <summary>
    /// Maps a canvas-local rectangle to device space, normalised so the extents are non-negative.
    /// </summary>
    /// <remarks>
    /// A negative axis scale — <c>scaleX(-1)</c>, <c>scale(-1)</c>, <c>rotate(180deg)</c> — mirrors
    /// the rectangle about that axis, which leaves the mapped X/Y on what is now the far edge and
    /// the extent negative. Every primitive here reads <c>Left</c>/<c>Top</c>/<c>Right</c>/
    /// <c>Bottom</c> and walks the rows and columns between them, so an un-normalised rectangle
    /// spans nothing and the mirrored element vanishes outright. Non-negative scale takes neither
    /// branch, so its arithmetic is untouched.
    /// </remarks>
    private RectangleF Translate(RectangleF rect)
    {
        float x = rect.X * _scaleX + _translation.X;
        float y = rect.Y * _scaleY + _translation.Y;
        float width = rect.Width * _scaleX;
        float height = rect.Height * _scaleY;

        if (width < 0f)
        {
            x += width;
            width = -width;
        }

        if (height < 0f)
        {
            y += height;
            height = -height;
        }

        return new RectangleF(x, y, width, height);
    }

    /// <summary>
    /// Reflects a device-space point inside <paramref name="bounds"/> across whichever axes the
    /// current mapping mirrors. A point already in the rectangle stays in it.
    /// </summary>
    private PointF MirrorInto(PointF point, RectangleF bounds) =>
        new(
            FlipX ? bounds.Left + bounds.Right - point.X : point.X,
            FlipY ? bounds.Top + bounds.Bottom - point.Y : point.Y);

    private PointF Translate(PointF point) =>
        new(point.X * _scaleX + _translation.X, point.Y * _scaleY + _translation.Y);

    /// <summary>
    /// Clamps a primitive's device-pixel bounds to the surface and to the rows and columns the
    /// current clip can admit, and reports whether anything is left to draw.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It never drops a pixel <see cref="IsVisible"/> would have kept.</b> A pixel is visible
    /// only if every including clip rectangle contains its centre <c>(x + 0.5, y + 0.5)</c>, so the
    /// leftmost visible column is at least <c>Left - 0.5</c> and <c>floor(Left)</c> is at or below
    /// it; the same argument, mirrored, gives the right edge. The clamp is therefore a conservative
    /// superset of the visible box, and every pixel it removes is one the per-pixel test rejects.
    /// Output is unchanged; the work of walking those pixels is not.
    /// </para>
    /// <para>
    /// Callers pass the geometry's own bounds and read back the narrowed ones. Every primitive
    /// computes each pixel's value from the geometry rather than from the loop bounds, so narrowing
    /// the loop leaves the surviving pixels bit-identical — including <see cref="FillGlyphContours"/>,
    /// whose coverage accumulator is indexed from <c>minX</c> and whose spans are clipped into it.
    /// </para>
    /// </remarks>
    private bool NarrowToClip(ref int minX, ref int minY, ref int maxX, ref int maxY)
    {
        var target = CurrentTarget;
        minX = Math.Max(0, minX);
        minY = Math.Max(0, minY);
        maxX = Math.Min(target.Width - 1, maxX);
        maxY = Math.Min(target.Height - 1, maxY);

        if (_clipBounds.Count > 0)
        {
            var bounds = _clipBounds[^1];
            if (bounds.Width <= 0f || bounds.Height <= 0f)
                return false;

            minX = Math.Max(minX, (int)Math.Floor(bounds.Left));
            minY = Math.Max(minY, (int)Math.Floor(bounds.Top));
            maxX = Math.Min(maxX, (int)Math.Ceiling(bounds.Right) - 1);
            maxY = Math.Min(maxY, (int)Math.Ceiling(bounds.Bottom) - 1);
        }

        return minX <= maxX && minY <= maxY;
    }

    /// <summary>
    /// Whether a primitive confined to <paramref name="bounds"/> — in canvas-local coordinates, the
    /// space every drawing call takes — cannot reach a pixel this canvas may write.
    /// </summary>
    /// <remarks>
    /// The caller's rectangle goes through the same surface mapping as its geometry would, so this
    /// answers about the pixels the primitive would actually have touched rather than about where
    /// its untransformed coordinates happen to sit.
    /// </remarks>
    public bool IsCulled(RectangleF bounds) => !IntersectsClip(Translate(bounds));

    /// <summary>
    /// Whether nothing drawn between the canvas-local rows <paramref name="top"/> and
    /// <paramref name="bottom"/> can reach a pixel this canvas may write.
    /// </summary>
    /// <remarks>
    /// <b>Rows only, because rows are what the caller knows exactly.</b> The text backend can name a
    /// run's vertical extent from the font's own ascent and descent before it has looked at a single
    /// glyph, while its horizontal extent costs a pass over the run to sum the advances — the very
    /// pass this test exists to skip. Ignoring the horizontal axis makes the answer conservative in
    /// the only direction it may be wrong: a run beside the clip is drawn and rejected per glyph, as
    /// it was before, and a run above or below it is skipped whole.
    /// </remarks>
    public bool IsRowBandCulled(float top, float bottom)
    {
        if (_clipBounds.Count == 0)
            return false;

        var bounds = _clipBounds[^1];
        if (bounds.Width <= 0f || bounds.Height <= 0f)
            return true;

        float first = top * _scaleY + _translation.Y;
        float last = bottom * _scaleY + _translation.Y;
        if (last < first)
            (first, last) = (last, first);

        return last < bounds.Top || first > bounds.Bottom || last < 0f || first > CurrentTarget.Height;
    }

    /// <summary>
    /// Whether a primitive whose device-space bounds are <paramref name="bounds"/> can put a pixel
    /// anywhere the clip admits. Lets a primitive reject itself before transforming its geometry.
    /// </summary>
    private bool IntersectsClip(RectangleF bounds)
    {
        int minX = (int)Math.Floor(bounds.Left);
        int minY = (int)Math.Floor(bounds.Top);
        int maxX = (int)Math.Ceiling(bounds.Right);
        int maxY = (int)Math.Ceiling(bounds.Bottom);
        return NarrowToClip(ref minX, ref minY, ref maxX, ref maxY);
    }

    private bool IsVisible(int x, int y)
    {
        float sampleX = x + 0.5f;
        float sampleY = y + 0.5f;

        foreach (var operation in _clipOperations)
        {
            bool contains = operation.Contains(sampleX, sampleY);
            if (operation.IsExclude)
            {
                if (contains)
                    return false;
            }
            else if (!contains)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Blends a finished layer back into what is now the current target, over the box the layer
    /// could have written.
    /// </summary>
    /// <remarks>
    /// <b>The bound is what makes a layer affordable under tiling.</b> A layer buffer is the size of
    /// the surface, so an unbounded composite is a full-surface scan per layer — and under a tile
    /// view, per layer <em>per tile</em>, which would have made a page with layers slower with more
    /// threads than with one. The layer's own clip box is a superset of everything it can hold, so
    /// walking it composites exactly the same pixels: outside it every source pixel is transparent
    /// and the loop below would have skipped it anyway.
    /// </remarks>
    private void CompositeLayer(LayerState layer)
    {
        if (layer.Warp is { } warp)
        {
            CompositeWarpedLayer(layer, warp);
            return;
        }

        var destination = CurrentTarget;
        int minX = 0, minY = 0, maxX = destination.Width - 1, maxY = destination.Height - 1;
        if (layer.ContentBounds is { } bounds)
        {
            minX = Math.Max(minX, (int)Math.Floor(bounds.Left));
            minY = Math.Max(minY, (int)Math.Floor(bounds.Top));
            maxX = Math.Min(maxX, (int)Math.Ceiling(bounds.Right) - 1);
            maxY = Math.Min(maxY, (int)Math.Ceiling(bounds.Bottom) - 1);
        }

        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                var source = layer.Bitmap.GetPixel(x, y);
                if (source.A == 0)
                    continue;

                if (layer.Filter is { } filter)
                    source = ApplyColorFilter(source, filter);

                if (layer.Opacity < 1f)
                    source = ApplyOpacity(source, layer.Opacity);

                BlendPixel(destination, x, y, source, layer.BlendMode);
            }
        }

        layer.Bitmap.Dispose();
    }

    /// <summary>
    /// Composites a warp layer: for every destination pixel the transform can reach, fetch the
    /// layer pixel that lands there and blend it. Inverse mapping is what makes it hole-free —
    /// scattering the source forward leaves gaps wherever the transform magnifies.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Nearest sample, not bilinear.</b> A quarter turn maps pixel centres onto pixel centres,
    /// so nearest is exact for the axis-swapping rotations that most of the corpus uses, and it
    /// cannot fringe a hard edge the way interpolating against transparent black does. A diagonal
    /// rotation gets hard edges instead of antialiased ones; both sides of a reftest are rendered
    /// here, so they get the same ones.
    /// </para>
    /// <para>
    /// <b>The clip is applied twice, and the second time is the one CSS asks for.</b> Content was
    /// already clipped on its way into the layer, in pre-transform space, which is not where an
    /// ancestor's <c>overflow</c> clip belongs; <see cref="IsVisible"/> here applies it in
    /// post-transform space, which is. The intersection of the two is conservative: content
    /// rotated <em>into</em> an ancestor's clip from outside it is not recovered. With no ancestor
    /// clip — the ordinary case — both are the whole surface and neither costs anything.
    /// </para>
    /// </remarks>
    private void CompositeWarpedLayer(LayerState layer, Broiler.Layout.IR.AffineLayerMap warp)
    {
        var destination = CurrentTarget;
        var source = layer.Bitmap;
        var sourceBounds = layer.ContentBounds
            ?? new RectangleF(0f, 0f, source.Width, source.Height);
        sourceBounds = RectangleF.Intersect(
            sourceBounds, new RectangleF(0f, 0f, source.Width, source.Height));
        if (sourceBounds.Width <= 0f || sourceBounds.Height <= 0f)
        {
            source.Dispose();
            return;
        }

        var destBounds = warp.MapBounds(sourceBounds);
        int minX = Math.Max(0, (int)Math.Floor(destBounds.Left));
        int minY = Math.Max(0, (int)Math.Floor(destBounds.Top));
        int maxX = Math.Min(destination.Width - 1, (int)Math.Ceiling(destBounds.Right));
        int maxY = Math.Min(destination.Height - 1, (int)Math.Ceiling(destBounds.Bottom));

        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                warp.InverseMap(x + 0.5f, y + 0.5f, out float sx, out float sy);
                int ix = (int)Math.Floor(sx);
                int iy = (int)Math.Floor(sy);
                if (ix < 0 || iy < 0 || ix >= source.Width || iy >= source.Height)
                    continue;

                var sample = source.GetPixel(ix, iy);
                if (sample.A == 0)
                    continue;
                if (!IsVisible(x, y))
                    continue;

                if (layer.Filter is { } filter)
                    sample = ApplyColorFilter(sample, filter);
                if (layer.Opacity < 1f)
                    sample = ApplyOpacity(sample, layer.Opacity);

                BlendPixel(destination, x, y, sample, layer.BlendMode);
            }
        }

        source.Dispose();
    }

    private static BColor ApplyOpacity(BColor color, float opacity)
    {
        opacity = Math.Clamp(opacity, 0f, 1f);
        byte alpha = (byte)Math.Clamp((int)Math.Round(color.A * opacity), 0, 255);
        return new BColor(color.R, color.G, color.B, alpha);
    }

    private static readonly System.Text.RegularExpressions.Regex FilterFunctionPattern =
        new(@"([a-zA-Z-]+)\(([^)]*)\)", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Applies the colour-matrix CSS filter functions (invert/grayscale/brightness/contrast/
    /// sepia/saturate/opacity/hue-rotate) to a single pixel, left to right. Unsupported functions
    /// (blur/drop-shadow/…) are skipped. Matrices follow Filter Effects §16.
    /// </summary>
    private static BColor ApplyColorFilter(BColor color, string filter)
    {
        float r = color.R, g = color.G, b = color.B, a = color.A;
        foreach (System.Text.RegularExpressions.Match match in FilterFunctionPattern.Matches(filter))
        {
            var name = match.Groups[1].Value.ToLowerInvariant();
            var arg = match.Groups[2].Value.Trim();
            switch (name)
            {
                case "invert":
                {
                    float t = ParseFilterAmount(arg, 1f, clampToOne: true);
                    r = Lerp(r, 255f - r, t); g = Lerp(g, 255f - g, t); b = Lerp(b, 255f - b, t);
                    break;
                }
                case "grayscale":
                {
                    float t = ParseFilterAmount(arg, 1f, clampToOne: true);
                    float l = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                    r = Lerp(r, l, t); g = Lerp(g, l, t); b = Lerp(b, l, t);
                    break;
                }
                case "brightness":
                {
                    float t = ParseFilterAmount(arg, 1f, clampToOne: false);
                    r *= t; g *= t; b *= t;
                    break;
                }
                case "contrast":
                {
                    float t = ParseFilterAmount(arg, 1f, clampToOne: false);
                    r = (r - 128f) * t + 128f; g = (g - 128f) * t + 128f; b = (b - 128f) * t + 128f;
                    break;
                }
                case "opacity":
                {
                    float t = ParseFilterAmount(arg, 1f, clampToOne: true);
                    a *= t;
                    break;
                }
                case "saturate":
                {
                    float s = ParseFilterAmount(arg, 1f, clampToOne: false);
                    (r, g, b) = (
                        (0.213f + 0.787f * s) * r + (0.715f - 0.715f * s) * g + (0.072f - 0.072f * s) * b,
                        (0.213f - 0.213f * s) * r + (0.715f + 0.285f * s) * g + (0.072f - 0.072f * s) * b,
                        (0.213f - 0.213f * s) * r + (0.715f - 0.715f * s) * g + (0.072f + 0.928f * s) * b);
                    break;
                }
                case "sepia":
                {
                    float s = 1f - ParseFilterAmount(arg, 1f, clampToOne: true);
                    (r, g, b) = (
                        (0.393f + 0.607f * s) * r + (0.769f - 0.769f * s) * g + (0.189f - 0.189f * s) * b,
                        (0.349f - 0.349f * s) * r + (0.686f + 0.314f * s) * g + (0.168f - 0.168f * s) * b,
                        (0.272f - 0.272f * s) * r + (0.534f - 0.534f * s) * g + (0.131f + 0.869f * s) * b);
                    break;
                }
                case "hue-rotate":
                {
                    float rad = ParseFilterAngleRadians(arg);
                    float cos = MathF.Cos(rad), sin = MathF.Sin(rad);
                    (r, g, b) = (
                        (0.213f + cos * 0.787f - sin * 0.213f) * r + (0.715f - cos * 0.715f - sin * 0.715f) * g + (0.072f - cos * 0.072f + sin * 0.928f) * b,
                        (0.213f - cos * 0.213f + sin * 0.143f) * r + (0.715f + cos * 0.285f + sin * 0.140f) * g + (0.072f - cos * 0.072f - sin * 0.283f) * b,
                        (0.213f - cos * 0.213f - sin * 0.787f) * r + (0.715f - cos * 0.715f + sin * 0.715f) * g + (0.072f + cos * 0.928f + sin * 0.072f) * b);
                    break;
                }
            }
        }

        return new BColor(ClampByte(r), ClampByte(g), ClampByte(b), ClampByte(a));
    }

    private static float Lerp(float from, float to, float t) => from + (to - from) * t;

    private static byte ClampByte(float value) => (byte)Math.Clamp((int)MathF.Round(value), 0, 255);

    /// <summary>Parses a filter amount: a number, or a percentage (n%). <paramref name="clampToOne"/>
    /// caps at 1 for the [0,1]-ranged functions (invert/grayscale/sepia/opacity).</summary>
    private static float ParseFilterAmount(string arg, float fallback, bool clampToOne)
    {
        if (string.IsNullOrWhiteSpace(arg))
            return fallback;

        float value;
        if (arg.EndsWith("%", StringComparison.Ordinal))
        {
            if (!float.TryParse(arg.AsSpan(0, arg.Length - 1), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pct))
                return fallback;
            value = pct / 100f;
        }
        else if (!float.TryParse(arg, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value))
        {
            return fallback;
        }

        value = Math.Max(0f, value);
        return clampToOne ? Math.Min(1f, value) : value;
    }

    private static float ParseFilterAngleRadians(string arg)
    {
        arg = arg.Trim();
        if (string.IsNullOrEmpty(arg) || arg == "0")
            return 0f;

        (string suffix, float perUnit) = arg switch
        {
            _ when arg.EndsWith("rad", StringComparison.OrdinalIgnoreCase) => ("rad", 1f),
            _ when arg.EndsWith("grad", StringComparison.OrdinalIgnoreCase) => ("grad", MathF.PI / 200f),
            _ when arg.EndsWith("turn", StringComparison.OrdinalIgnoreCase) => ("turn", MathF.PI * 2f),
            _ when arg.EndsWith("deg", StringComparison.OrdinalIgnoreCase) => ("deg", MathF.PI / 180f),
            _ => ("", MathF.PI / 180f),
        };

        var numberSpan = suffix.Length > 0 ? arg.AsSpan(0, arg.Length - suffix.Length) : arg.AsSpan();
        return float.TryParse(numberSpan, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n)
            ? n * perUnit
            : 0f;
    }

    private static void BlendPixel(BBitmap bitmap, int x, int y, BColor source, string blendMode)
    {
        if (source.A == 0)
            return;

        var destination = bitmap.GetPixel(x, y);
        var blendedSource = ApplyBlendMode(source, destination, blendMode);
        bitmap.SetPixel(x, y, CompositeSourceOver(blendedSource, destination));
    }

    private static BColor ApplyBlendMode(BColor source, BColor destination, string blendMode)
    {
        if (string.Equals(blendMode, "multiply", StringComparison.OrdinalIgnoreCase))
        {
            return new BColor(
                // +127 is the integer equivalent of adding 0.5 before dividing by 255.
                (byte)((source.R * destination.R + 127) / 255),
                (byte)((source.G * destination.G + 127) / 255),
                (byte)((source.B * destination.B + 127) / 255),
                source.A);
        }

        if (string.Equals(blendMode, "screen", StringComparison.OrdinalIgnoreCase))
        {
            return new BColor(
                (byte)(255 - (((255 - source.R) * (255 - destination.R) + 127) / 255)),
                (byte)(255 - (((255 - source.G) * (255 - destination.G) + 127) / 255)),
                (byte)(255 - (((255 - source.B) * (255 - destination.B) + 127) / 255)),
                source.A);
        }

        if (string.Equals(blendMode, "darken", StringComparison.OrdinalIgnoreCase))
        {
            return new BColor(
                Math.Min(source.R, destination.R),
                Math.Min(source.G, destination.G),
                Math.Min(source.B, destination.B),
                source.A);
        }

        if (string.Equals(blendMode, "lighten", StringComparison.OrdinalIgnoreCase))
        {
            return new BColor(
                Math.Max(source.R, destination.R),
                Math.Max(source.G, destination.G),
                Math.Max(source.B, destination.B),
                source.A);
        }

        if (string.Equals(blendMode, "overlay", StringComparison.OrdinalIgnoreCase))
        {
            return new BColor(
                OverlayChannel(source.R, destination.R),
                OverlayChannel(source.G, destination.G),
                OverlayChannel(source.B, destination.B),
                source.A);
        }

        if (string.Equals(blendMode, "difference", StringComparison.OrdinalIgnoreCase))
        {
            return new BColor(
                (byte)Math.Abs(source.R - destination.R),
                (byte)Math.Abs(source.G - destination.G),
                (byte)Math.Abs(source.B - destination.B),
                source.A);
        }

        if (string.Equals(blendMode, "plus-lighter", StringComparison.OrdinalIgnoreCase))
        {
            return new BColor(
                AdditiveClampChannel(source.R, destination.R),
                AdditiveClampChannel(source.G, destination.G),
                AdditiveClampChannel(source.B, destination.B),
                source.A);
        }

        return source;
    }

    private static float[] NormalizeGradientPositions(int colorCount, IReadOnlyList<float>? positions)
    {
        var normalized = new float[colorCount];
        if (positions == null || positions.Count != colorCount)
        {
            if (colorCount == 1)
            {
                normalized[0] = 0f;
                return normalized;
            }

            for (int i = 0; i < colorCount; i++)
                normalized[i] = (float)i / (colorCount - 1);

            return normalized;
        }

        normalized[0] = Math.Clamp(positions[0], 0f, 1f);
        for (int i = 1; i < colorCount; i++)
            normalized[i] = Math.Max(normalized[i - 1], Math.Clamp(positions[i], 0f, 1f));

        return normalized;
    }

    private static (PointF StartPoint, PointF EndPoint) GetGradientEndpoints(RectangleF rect, float angle)
    {
        var radians = angle * Math.PI / 180.0;
        float cx = rect.X + (rect.Width / 2f);
        float cy = rect.Y + (rect.Height / 2f);
        float sin = (float)Math.Sin(radians);
        float cos = (float)Math.Cos(radians);
        // CSS Images 3 §3.4.2: the gradient line runs through the box centre and
        // its length is abs(W·sin A) + abs(H·cos A) — the projection of the box
        // onto the gradient direction, so the start/end sit on the perpendiculars
        // through the two nearest corners. Using max(W, H) instead over-extends
        // the line on non-square boxes (e.g. a wide, short tile), compressing the
        // visible colour run to the middle of the gradient.
        float halfLen = (Math.Abs(rect.Width * sin) + Math.Abs(rect.Height * cos)) / 2f;
        return (
            new PointF(cx - (sin * halfLen), cy + (cos * halfLen)),
            new PointF(cx + (sin * halfLen), cy - (cos * halfLen)));
    }

    private static BColor SampleGradientColor(IReadOnlyList<BColor> colors, IReadOnlyList<float> positions, float t)
    {
        if (t <= positions[0])
            return colors[0];

        for (int i = 1; i < colors.Count; i++)
        {
            if (t > positions[i])
                continue;

            float start = positions[i - 1];
            float end = positions[i];
            if (end <= start)
                return colors[i];

            float localT = (t - start) / (end - start);
            return Lerp(colors[i - 1], colors[i], localT);
        }

        return colors[^1];
    }

    private static BColor Lerp(BColor start, BColor end, float t) =>
        new(
            LerpChannel(start.R, end.R, t),
            LerpChannel(start.G, end.G, t),
            LerpChannel(start.B, end.B, t),
            LerpChannel(start.A, end.A, t));

    private static byte LerpChannel(byte start, byte end, float t) =>
        (byte)Math.Clamp((int)Math.Round(start + ((end - start) * t)), 0, 255);

    private static float PositiveModulo(float value, float modulus)
    {
        float result = value % modulus;
        if (result < 0)
            result += modulus;
        return result;
    }

    private static BColor CompositeSourceOver(BColor source, BColor destination)
    {
        float srcA = source.A / 255f;
        float dstA = destination.A / 255f;
        float outA = srcA + dstA * (1f - srcA);

        if (outA <= 0f)
            return BColor.Transparent;

        byte r = CompositeChannel(source.R, destination.R, srcA, dstA, outA);
        byte g = CompositeChannel(source.G, destination.G, srcA, dstA, outA);
        byte b = CompositeChannel(source.B, destination.B, srcA, dstA, outA);
        byte a = (byte)Math.Clamp((int)Math.Round(outA * 255f), 0, 255);

        return new BColor(r, g, b, a);
    }

    private static byte CompositeChannel(byte source, byte destination, float srcA, float dstA, float outA)
    {
        float value = (source * srcA + destination * dstA * (1f - srcA)) / outA;
        return (byte)Math.Clamp((int)Math.Round(value), 0, 255);
    }

    private static byte OverlayChannel(byte source, byte destination)
    {
        if (destination < 128)
            return (byte)Math.Clamp((2 * source * destination + 127) / 255, 0, 255);

        return (byte)Math.Clamp(
            255 - ((2 * (255 - source) * (255 - destination) + 127) / 255),
            0,
            255);
    }

    private static byte AdditiveClampChannel(byte source, byte destination) =>
        (byte)Math.Min(255, source + destination);

    private static float DistanceToSegment(float px, float py, PointF start, PointF end)
    {
        float dx = end.X - start.X;
        float dy = end.Y - start.Y;

        if (Math.Abs(dx) < float.Epsilon && Math.Abs(dy) < float.Epsilon)
            return Distance(px, py, start.X, start.Y);

        float t = ((px - start.X) * dx + (py - start.Y) * dy) / (dx * dx + dy * dy);
        t = Math.Clamp(t, 0f, 1f);

        float nearestX = start.X + t * dx;
        float nearestY = start.Y + t * dy;
        return Distance(px, py, nearestX, nearestY);
    }

    private static float Distance(float x1, float y1, float x2, float y2)
    {
        float dx = x2 - x1;
        float dy = y2 - y1;
        return (float)Math.Sqrt(dx * dx + dy * dy);
    }

    private static bool ContainsPolygonPoint(PointF[] polygon, float x, float y)
    {
        bool inside = false;
        for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
        {
            var pi = polygon[i];
            var pj = polygon[j];
            bool intersects = ((pi.Y > y) != (pj.Y > y))
                              && (x < (pj.X - pi.X) * (y - pi.Y) / ((pj.Y - pi.Y) + float.Epsilon) + pi.X);
            if (intersects)
                inside = !inside;
        }

        return inside;
    }

    private readonly record struct CanvasState(PointF Translation, float ScaleX, float ScaleY, int ClipOperationCount);

    /// <summary>
    /// An open compositing layer: the buffer its content is drawn into, how it is composited back,
    /// and the box outside which it is known to hold nothing.
    /// </summary>
    /// <param name="ContentBounds">
    /// The clip bounding box in force when the layer was pushed, or <c>null</c> when nothing bounded
    /// it. Content inside a layer is drawn through <see cref="IsVisible"/> against a clip stack that
    /// starts with this one, so no pixel outside it can be set — which is what lets the composite
    /// walk that box instead of the whole surface. Captured at push rather than read at composite so
    /// the bound holds even if the display list left the clip stack unbalanced.
    /// </param>
    private sealed record LayerState(
        BBitmap Bitmap,
        float Opacity,
        string BlendMode,
        RectangleF? ContentBounds,
        string? Filter = null,
        Broiler.Layout.IR.AffineLayerMap? Warp = null,
        List<ClipOperation>? SuspendedClipOperations = null,
        List<RectangleF>? SuspendedClipBounds = null);

    private readonly record struct ClipOperation(
        RectangleF Rect,
        bool IsExclude,
        bool IsRounded,
        float CornerNw,
        float CornerNwY,
        float CornerNe,
        float CornerNeY,
        float CornerSe,
        float CornerSeY,
        float CornerSw,
        float CornerSwY,
        PointF[]? Polygon = null)
    {
        public static ClipOperation Include(RectangleF rect) => new(rect, false, false, 0, 0, 0, 0, 0, 0, 0, 0);

        public static ClipOperation Exclude(RectangleF rect) => new(rect, true, false, 0, 0, 0, 0, 0, 0, 0, 0);

        /// <summary>
        /// A polygon clip. <see cref="Rect"/> carries the polygon's bounding box so
        /// <see cref="Contains"/> can reject the common case before running the crossing test.
        /// </summary>
        public static ClipOperation IncludePolygon(PointF[] polygon)
        {
            float minX = float.PositiveInfinity, minY = float.PositiveInfinity;
            float maxX = float.NegativeInfinity, maxY = float.NegativeInfinity;
            foreach (var point in polygon)
            {
                minX = Math.Min(minX, point.X);
                minY = Math.Min(minY, point.Y);
                maxX = Math.Max(maxX, point.X);
                maxY = Math.Max(maxY, point.Y);
            }

            return new(
                new RectangleF(minX, minY, maxX - minX, maxY - minY),
                false, false, 0, 0, 0, 0, 0, 0, 0, 0, polygon);
        }

        public static ClipOperation IncludeRounded(
            RectangleF rect,
            float cornerNw,
            float cornerNwY,
            float cornerNe,
            float cornerNeY,
            float cornerSe,
            float cornerSeY,
            float cornerSw,
            float cornerSwY) =>
            new(rect, false, true, cornerNw, cornerNwY, cornerNe, cornerNeY, cornerSe, cornerSeY, cornerSw, cornerSwY);

        public bool Contains(float x, float y)
        {
            if (!Rect.Contains(x, y))
                return false;

            if (Polygon is not null)
                return ContainsPolygonPoint(Polygon, x, y);

            if (!IsRounded)
                return true;

            return ContainsRounded(x, y);
        }

        /// <summary>
        /// Whether a point inside the clip's bounding rect is inside its rounded shape.
        /// <para>
        /// Only the four corner boxes are curved — a corner box spanning that corner's two radii —
        /// and a point inside one is inside the shape only if it is inside that corner's ellipse.
        /// Everything else within the rect is simply inside. A zero radius makes its corner box
        /// empty, so that corner stays square with no special case.
        /// </para>
        /// <para>
        /// This replaces a test that asked whether the point lay in a horizontal or vertical band
        /// between opposing radii. Those bands span the whole box as soon as the opposing corner is
        /// square: with only a top-left radius set, the "between the bottom corners" band covered
        /// every row, so the shape reported itself as the full rectangle and a single rounded corner
        /// rendered square. It only clipped correctly when all four corners were rounded.
        /// </para>
        /// </summary>
        private bool ContainsRounded(float x, float y)
        {
            if (x < Rect.Left + CornerNw && y < Rect.Top + CornerNwY)
                return InEllipse(x, y, Rect.Left + CornerNw, Rect.Top + CornerNwY, CornerNw, CornerNwY);

            if (x > Rect.Right - CornerNe && y < Rect.Top + CornerNeY)
                return InEllipse(x, y, Rect.Right - CornerNe, Rect.Top + CornerNeY, CornerNe, CornerNeY);

            if (x > Rect.Right - CornerSe && y > Rect.Bottom - CornerSeY)
                return InEllipse(x, y, Rect.Right - CornerSe, Rect.Bottom - CornerSeY, CornerSe, CornerSeY);

            if (x < Rect.Left + CornerSw && y > Rect.Bottom - CornerSwY)
                return InEllipse(x, y, Rect.Left + CornerSw, Rect.Bottom - CornerSwY, CornerSw, CornerSwY);

            return true;
        }

        private static bool InEllipse(float x, float y, float centerX, float centerY, float radiusX, float radiusY)
        {
            float dx = (x - centerX) / radiusX;
            float dy = (y - centerY) / radiusY;
            return dx * dx + dy * dy <= 1f;
        }
    }
}
