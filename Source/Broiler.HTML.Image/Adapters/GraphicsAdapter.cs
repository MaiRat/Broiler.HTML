using System;
using System.Collections.Generic;
using System.Drawing;
using Broiler.Graphics;
using Broiler.Layout.IR;

namespace Broiler.HTML.Image.Adapters;

internal sealed class GraphicsAdapter : RGraphics, ITileParallelSurface, IBoundsCullingSurface
{
    private readonly Func<object> _canvasFactory;
    private readonly BCanvas? _rasterCanvas;
    private readonly bool _disposeCanvas;

    /// <summary>
    /// Whether this adapter is one of a replay's tile views (multithreading item #5) rather than a
    /// surface in its own right. A tile view shares its parent's compat-canvas factory, so the
    /// compat canvas is not its to close even though the raster canvas beside it is — and reaching
    /// that factory at all is a defect worth counting.
    /// </summary>
    private readonly bool _isTileView;

    private readonly bool _restoreOnDispose;
    private readonly Action? _onDispose;
    private readonly List<Action<object>> _deferredCanvasOperations = [];
    private readonly Stack<bool> _rasterLayerStack = new();

    /// <summary>
    /// One entry per open <see cref="SaveTransformLayer"/>: whether the raster canvas took it as a
    /// <em>warp layer</em> (an offscreen resampled through the matrix) rather than by folding the
    /// matrix into its own per-axis mapping. The two are closed differently, and the shared
    /// <see cref="_rasterLayerStack"/> only records whether the raster canvas took it at all.
    /// </summary>
    private readonly Stack<bool> _transformWarpStack = new();
    private readonly ITextShaper _textShaper;
    private readonly ICanvasCompat _canvasCompat;
    private object? _canvas;
    private int _activeCompatLayerDepth;
    private bool _nextLayerCanUseRaster;

    public GraphicsAdapter(
        Func<object> canvasFactory,
        RectangleF initialClip,
        BCanvas? rasterCanvas = null,
        bool disposeCanvas = false,
        bool restoreOnDispose = false,
        Action? onDispose = null,
        ITextShaper? textShaper = null,
        ICanvasCompat? canvasCompat = null,
        Action<object, object?>? initialCanvasOperation = null,
        object? initialCanvasOperationState = null,
        bool isTileView = false)
        : base(CompatProvider.ImageAdapter, initialClip)
    {
        _canvasFactory = canvasFactory ?? throw new ArgumentNullException(nameof(canvasFactory));
        _rasterCanvas = rasterCanvas;
        _disposeCanvas = disposeCanvas;
        _isTileView = isTileView;
        _restoreOnDispose = restoreOnDispose;
        _onDispose = onDispose;
        _textShaper = textShaper ?? CompatProvider.TextShaper;
        _canvasCompat = canvasCompat ?? CompatProvider.CanvasCompat;
        if (initialCanvasOperation is not null)
            _deferredCanvasOperations.Add(canvas => initialCanvasOperation(canvas, initialCanvasOperationState));
    }

    internal bool HasMaterializedCanvas => _canvas is not null;

    /// <inheritdoc />
    /// <remarks>
    /// Three conditions, and each of them is a way a second thread could go wrong rather than a
    /// preference. There has to <em>be</em> a raster canvas (with no raster pipeline every draw
    /// goes to the compat backend, whose threading rules are not ours to assume); the surface must
    /// still tolerate concurrent pixel writes, which stops being true once it mirrors into a
    /// platform bitmap; and this adapter must not already have materialized a compat canvas, since
    /// the views share the factory that would make a second one.
    /// </remarks>
    public Size TileParallelSurfaceSize =>
        _rasterCanvas is { } canvas && !HasMaterializedCanvas && canvas.SupportsConcurrentPixelWrites
            ? new Size(canvas.SurfaceWidth, canvas.SurfaceHeight)
            : Size.Empty;

    /// <inheritdoc />
    /// <remarks>
    /// Only the raster canvas can answer: it owns the clip the pixels are tested against. With no
    /// raster canvas every draw goes to the compat backend, whose clip this adapter does not model,
    /// so nothing is culled and every primitive runs exactly as it did before.
    /// </remarks>
    public bool IsCulled(RectangleF bounds) => _rasterCanvas?.IsCulled(bounds) ?? false;

    /// <inheritdoc />
    public RGraphics CreateTileView(Rectangle tile)
    {
        var canvas = _rasterCanvas
            ?? throw new InvalidOperationException("This surface has no raster canvas to tile.");

        return new GraphicsAdapter(
            _canvasFactory,
            // The clip stack goes across UNNARROWED, and that is load-bearing rather than tidy: a
            // caller may derive geometry from GetClip(), not merely obey it. DrawClippedImage
            // recomputes an image's *source* rectangle from the intersection of its destination with
            // the clip, so a tile-narrowed clip re-derives a different source rectangle and resamples
            // the image — which showed up as one row of subtly different pixels on two
            // background-size tests and on nothing else. The tile belongs to the rasterizer's
            // per-pixel test and to nothing above it, so it is added to the raster canvas only.
            _clipStack.Peek(),
            canvas.CreateTileView(tile),
            disposeCanvas: true,
            textShaper: _textShaper,
            canvasCompat: _canvasCompat,
            isTileView: true);
    }

    public override void PopClip()
    {
        ApplyCanvasOperation(CompatCanvasOperations.Restore);
        _rasterCanvas?.PopClip();
        _clipStack.Pop();
    }

    public override void PushClip(RectangleF rect)
    {
        _clipStack.Push(rect);
        ApplyCanvasOperation(canvas =>
        {
            CompatCanvasOperations.Save(canvas);
            _canvasCompat.PushClip(canvas, rect);
        });
        _rasterCanvas?.PushClip(rect);
    }

    public override void PushClipExclude(RectangleF rect)
    {
        _clipStack.Push(_clipStack.Peek());
        ApplyCanvasOperation(canvas =>
        {
            CompatCanvasOperations.Save(canvas);
            _canvasCompat.PushClipExclude(canvas, rect);
        });
        _rasterCanvas?.PushClipExclude(rect);
    }

    public override void PushClipPolygon(PointF[] points, RectangleF bounds)
    {
        _clipStack.Push(bounds);
        ApplyCanvasOperation(canvas =>
        {
            CompatCanvasOperations.Save(canvas);
            // The compat canvas has no arbitrary-shape clip, so it gets the polygon's
            // bounding box. The raster canvas below clips to the polygon exactly, and it
            // is the pipeline the headless renderer (and therefore the WPT run) uses.
            _canvasCompat.PushClip(canvas, bounds);
        });

        _rasterCanvas?.PushClipPolygon(points);
    }

    public override void PushClipRounded(RectangleF rect,
        double cornerNw, double cornerNwY,
        double cornerNe, double cornerNeY,
        double cornerSe, double cornerSeY,
        double cornerSw, double cornerSwY)
    {
        _clipStack.Push(rect);
        ApplyCanvasOperation(canvas =>
        {
            CompatCanvasOperations.Save(canvas);
            _canvasCompat.ClipRounded(
                canvas,
                rect,
                cornerNw, cornerNwY,
                cornerNe, cornerNeY,
                cornerSe, cornerSeY,
                cornerSw, cornerSwY);
        });

        _rasterCanvas?.PushClipRounded(
            rect,
            cornerNw, cornerNwY,
            cornerNe, cornerNeY,
            cornerSe, cornerSeY,
            cornerSw, cornerSwY);
    }

    public override object SetAntiAliasSmoothingMode() => null;

    public override void ReturnPreviousSmoothingMode(object prevMode)
    {
    }

    public override SizeF MeasureString(string str, RFont font) =>
        _textShaper.MeasureString((FontAdapter)font, str);

    public override void MeasureString(string str, RFont font, double maxWidth, out int charFit, out double charFitWidth) =>
        _textShaper.MeasureString((FontAdapter)font, str, maxWidth, out charFit, out charFitWidth);

    public override void DrawString(string str, RFont font, BColor color, PointF point, SizeF size, bool rtl)
    {
        float glyphRotation = VerticalGlyphContext.RotationDeg;
        if (CanUseRaster && _textShaper.TryDrawString(_rasterCanvas!, (FontAdapter)font, str, color, point, glyphRotation))
            return;

        var canvas = EnsureCanvas();
        _textShaper.DrawString(canvas, (FontAdapter)font, str, color, point);
    }

    public override void DrawGradientString(string str, RFont font, RectangleF rect, PointF point, SizeF size, bool rtl, BColor[] colors, float[] positions, float angle)
    {
        if (colors == null || colors.Length == 0)
            return;

        if (CanUseRaster && _textShaper.TryDrawGradientString(_rasterCanvas!, (FontAdapter)font, str, rect, point, size, colors, positions, angle))
            return;

        var canvas = EnsureCanvas();
        _textShaper.DrawGradientString(canvas, (FontAdapter)font, str, rect, point, size, colors, positions, angle);
    }

    public override RBrush GetTextureBrush(RImage image, RectangleF dstRect, PointF translateTransformLocation)
    {
        var imgAdapter = (ImageAdapter)image;
        return new BrushAdapter(
            () => _canvasCompat.CreateTexturePaint(imgAdapter.Bitmap, translateTransformLocation),
            dispose: true)
        {
            TextureBitmap = imgAdapter.Bitmap,
            TextureSourceRect = dstRect,
            TextureOrigin = translateTransformLocation,
        };
    }

    public override RGraphicsPath GetGraphicsPath() => new GraphicsPathAdapter();

    public override void DrawLine(RPen pen, double x1, double y1, double x2, double y2)
    {
        var penAdapter = (PenAdapter)pen;
        if (CanUseRaster && penAdapter.HasSimpleStroke)
        {
            _rasterCanvas!.DrawLine(new PointF((float)x1, (float)y1), new PointF((float)x2, (float)y2), penAdapter.SolidColor!.Value, (float)pen.Width);
            return;
        }

        // A solid-coloured dashed or dotted stroke reduces to a list of solid runs, which the
        // raster canvas can draw. Without this it fell through to the compat seam below, and on
        // a host with no OS backend that seam is an inert stub — so `border-style: dashed` and
        // `dotted` painted nothing at all while `solid` painted normally.
        if (CanUseRaster && penAdapter.SolidColor is { } dashColor)
        {
            var width = (float)pen.Width;
            var runs = DashedStrokeGeometry.Segments(
                (float)x1,
                (float)y1,
                (float)x2,
                (float)y2,
                DashedStrokeGeometry.PatternFor(penAdapter.CurrentDashStyle, width));

            foreach (var run in runs)
                _rasterCanvas!.DrawLine(new PointF(run.X1, run.Y1), new PointF(run.X2, run.Y2), dashColor, width);

            return;
        }

        _canvasCompat.DrawLine(EnsureCanvas(), (float)x1, (float)y1, (float)x2, (float)y2, penAdapter.Paint);
    }

    public override void DrawRectangle(RPen pen, double x, double y, double width, double height)
    {
        var penAdapter = (PenAdapter)pen;
        if (CanUseRaster && penAdapter.HasSimpleStroke)
        {
            _rasterCanvas!.DrawRectangleStroke(new RectangleF((float)x, (float)y, (float)width, (float)height), penAdapter.SolidColor!.Value, (float)pen.Width);
            return;
        }

        // Same reduction for a stroked rectangle: dash each edge rather than lose the outline.
        if (CanUseRaster && penAdapter.SolidColor is { } dashColor)
        {
            float x0 = (float)x, y0 = (float)y, x1 = (float)(x + width), y1 = (float)(y + height);
            var strokeWidth = (float)pen.Width;
            var pattern = DashedStrokeGeometry.PatternFor(penAdapter.CurrentDashStyle, strokeWidth);

            foreach (var (ax, ay, bx, by) in new[]
                     {
                         (x0, y0, x1, y0),
                         (x1, y0, x1, y1),
                         (x1, y1, x0, y1),
                         (x0, y1, x0, y0),
                     })
            {
                foreach (var run in DashedStrokeGeometry.Segments(ax, ay, bx, by, pattern))
                    _rasterCanvas!.DrawLine(new PointF(run.X1, run.Y1), new PointF(run.X2, run.Y2), dashColor, strokeWidth);
            }

            return;
        }

        _canvasCompat.DrawRectangle(EnsureCanvas(), new RectangleF((float)x, (float)y, (float)width, (float)height), penAdapter.Paint);
    }

    public override void DrawRectangle(RBrush brush, double x, double y, double width, double height)
    {
        var brushAdapter = (BrushAdapter)brush;
        if (CanUseRaster
            && brushAdapter.TextureBitmap is BBitmap textureBitmap
            && brushAdapter.TextureSourceRect is RectangleF textureSourceRect
            && brushAdapter.TextureOrigin is PointF textureOrigin)
        {
            _rasterCanvas!.FillRectTiled(
                textureBitmap,
                new RectangleF((float)x, (float)y, (float)width, (float)height),
                textureSourceRect,
                textureOrigin);
            return;
        }

        if (CanUseRaster && brushAdapter.SolidColor is BColor solidColor)
        {
            _rasterCanvas!.FillRect(new RectangleF((float)x, (float)y, (float)width, (float)height), solidColor);
            return;
        }

        _canvasCompat.DrawRectangle(EnsureCanvas(), new RectangleF((float)x, (float)y, (float)width, (float)height), brushAdapter.Paint);
    }

    public override void DrawImage(RImage image, RectangleF destRect, RectangleF srcRect)
    {
        var imgAdapter = (ImageAdapter)image;
        if (CanUseRaster)
        {
            _rasterCanvas!.DrawBitmap(imgAdapter.Bitmap, destRect, srcRect);
            return;
        }

        _canvasCompat.DrawImage(EnsureCanvas(), imgAdapter.Bitmap, destRect, srcRect);
    }

    public override void DrawImage(RImage image, RectangleF destRect)
    {
        var imgAdapter = (ImageAdapter)image;
        if (CanUseRaster)
        {
            _rasterCanvas!.DrawBitmap(
                imgAdapter.Bitmap,
                destRect,
                new RectangleF(0, 0, imgAdapter.Bitmap.Width, imgAdapter.Bitmap.Height));
            return;
        }

        _canvasCompat.DrawImage(EnsureCanvas(), imgAdapter.Bitmap, destRect);
    }

    public override void DrawPath(RPen pen, RGraphicsPath path)
    {
        var penAdapter = (PenAdapter)pen;
        var pathAdapter = (GraphicsPathAdapter)path;
        if (CanUseRaster && penAdapter.HasSimpleStroke && pathAdapter.FlattenedPoints.Count > 1)
        {
            _rasterCanvas!.DrawPathStroke(pathAdapter.FlattenedPoints, penAdapter.SolidColor!.Value, (float)pen.Width);
            return;
        }

        _canvasCompat.DrawPath(EnsureCanvas(), pathAdapter, penAdapter.Paint);
    }

    public override void DrawPath(RBrush brush, RGraphicsPath path)
    {
        var brushAdapter = (BrushAdapter)brush;
        var pathAdapter = (GraphicsPathAdapter)path;
        if (CanUseRaster && brushAdapter.SolidColor is BColor solidColor && pathAdapter.FlattenedPoints.Count > 2)
        {
            _rasterCanvas!.FillPolygon([.. pathAdapter.FlattenedPoints], solidColor);
            return;
        }

        _canvasCompat.DrawPath(EnsureCanvas(), pathAdapter, brushAdapter.Paint);
    }

    public override void DrawPolygon(RBrush brush, PointF[] points)
    {
        if (points == null || points.Length == 0)
            return;

        var brushAdapter = (BrushAdapter)brush;
        if (CanUseRaster && brushAdapter.SolidColor is BColor solidColor)
        {
            _rasterCanvas!.FillPolygon(points, solidColor);
            return;
        }

        _canvasCompat.DrawPolygon(EnsureCanvas(), points, brushAdapter.Paint);
    }

    public override void HintNextLayerCanUseRaster(bool canUseRaster) =>
        _nextLayerCanUseRaster = canUseRaster;

    /// <summary>
    /// Opens the compositing group for <c>0 &lt; opacity &lt; 1</c>. When the group's contents are
    /// raster-compatible it becomes a real opacity layer; otherwise the contents are drawn
    /// directly, at full opacity.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Drawing at the wrong opacity is a visible inaccuracy; the alternative it replaces was not
    /// drawing at all. The fall-through used to open the layer on the compat backend, which
    /// switches <see cref="CanUseRaster"/> off for every draw the group encloses — and against the
    /// stub compat backend this image renderer ships with, every one of those draws lands nowhere.
    /// The group and its whole subtree simply disappeared. <see cref="SaveTransformLayer"/> says
    /// the same thing about the same fall-through, and <see cref="SaveFilterLayer"/> already
    /// degrades this way.
    /// </para>
    /// <para>
    /// What made the difference between an inaccuracy and a blank page is that a group is declared
    /// non-raster by its <em>contents</em>: one <c>TransformItem</c> anywhere inside is enough
    /// (<c>RGraphicsRasterBackend.IsRasterCompatibleItem</c>), however ordinary everything else in
    /// it is. <c>duckduckgo.com</c> wraps its entire page in <c>#__next { isolation: isolate }</c>
    /// and has transforms under it, so the start page rendered as an empty white viewport — the
    /// blend-layer counterpart of this method, with <c>isolation</c>'s own <c>normal</c> blend.
    /// </para>
    /// </remarks>
    public override void SaveOpacityLayer(float opacity)
    {
        bool useRaster = _rasterCanvas is not null && _activeCompatLayerDepth == 0 && _nextLayerCanUseRaster;
        _nextLayerCanUseRaster = false;
        _rasterLayerStack.Push(useRaster);
        if (useRaster)
            _rasterCanvas!.SaveOpacityLayer(opacity);
    }

    public override void RestoreOpacityLayer()
    {
        bool usedRaster = _rasterLayerStack.Count > 0 && _rasterLayerStack.Pop();
        if (usedRaster)
            _rasterCanvas!.RestoreOpacityLayer();
    }

    public override void SaveFilterLayer(string filter)
    {
        // Filters need pixel readback to apply, which only the raster canvas offers. When the
        // layer is raster-compatible, push a real filter layer there; otherwise render the content
        // directly (unfiltered but visible) rather than route it to the stub compat backend and
        // lose it — the same graceful degradation the ignored-filter path had before.
        bool useRaster = _rasterCanvas is not null && _activeCompatLayerDepth == 0 && _nextLayerCanUseRaster;
        _nextLayerCanUseRaster = false;
        _rasterLayerStack.Push(useRaster);
        if (useRaster)
            _rasterCanvas!.SaveFilterLayer(filter);
    }

    public override void RestoreFilterLayer()
    {
        bool usedRaster = _rasterLayerStack.Count > 0 && _rasterLayerStack.Pop();
        if (usedRaster)
            _rasterCanvas!.RestoreFilterLayer();
    }

    /// <summary>
    /// Opens the compositing group for a <c>mix-blend-mode</c>, and for the <c>normal</c>-blend
    /// group <c>isolation: isolate</c> emits. When the group's contents are raster-compatible it
    /// becomes a real blend layer; otherwise the contents are drawn directly, unblended.
    /// </summary>
    /// <remarks>
    /// See <see cref="SaveOpacityLayer"/> for why the compat fall-through this replaces lost the
    /// group's whole subtree. For an isolation group the degradation costs nothing at all: the mode
    /// is <c>normal</c>, and isolation is only observable to a descendant that blends, so drawing
    /// the contents straight onto the surface is what the layer would have composited anyway.
    /// </remarks>
    public override void SaveBlendLayer(string blendMode)
    {
        bool useRaster = _rasterCanvas is not null
            && _activeCompatLayerDepth == 0
            && _nextLayerCanUseRaster;
        _nextLayerCanUseRaster = false;
        _rasterLayerStack.Push(useRaster);
        if (useRaster)
            _rasterCanvas!.SaveBlendLayer(blendMode);
    }

    public override void RestoreBlendLayer()
    {
        bool usedRaster = _rasterLayerStack.Count > 0 && _rasterLayerStack.Pop();
        if (usedRaster)
            _rasterCanvas!.RestoreBlendLayer();
    }

    /// <summary>
    /// Opens the group for a CSS <c>transform</c>. Translation and axis-aligned scale fold into the
    /// raster canvas's own per-axis mapping; a rotation or skew becomes a warp layer there — an
    /// offscreen the contents draw into untransformed, resampled through the matrix when the group
    /// closes. Only a canvas-less or already-compat context falls through.
    /// </summary>
    /// <remarks>
    /// The fall-through is what <see cref="SaveOpacityLayer"/> describes: it switches
    /// <see cref="CanUseRaster"/> off for every draw the group encloses, and against the stub
    /// compat backend this image renderer ships with, every one of those draws lands nowhere. For a
    /// transform that was not an inaccuracy but a disappearance — <c>transform: rotate(45deg)</c> on
    /// a green square painted a blank page, and with it went the whole subtree. Opacity, filter and
    /// blend were moved off that fall-through by degrading; a transform has somewhere better to go,
    /// because a finished layer can be resampled through a matrix the primitives cannot express.
    /// </remarks>
    public override void SaveTransformLayer(float[] matrix, float originX, float originY)
    {
        bool canUseCanvas = _rasterCanvas is not null && _activeCompatLayerDepth == 0;

        // Cheapest first: a matrix the canvas's point mapping expresses needs no offscreen at all,
        // and placing content directly is exact where a resample would not be.
        if (canUseCanvas && _rasterCanvas!.TrySaveTransform(matrix, originX, originY))
        {
            _rasterLayerStack.Push(true);
            _transformWarpStack.Push(false);
            return;
        }

        if (canUseCanvas && _rasterCanvas!.TrySaveWarpLayer(matrix, originX, originY))
        {
            _rasterLayerStack.Push(true);
            _transformWarpStack.Push(true);
            return;
        }

        _rasterLayerStack.Push(false);
        _transformWarpStack.Push(false);
        _activeCompatLayerDepth++;
        ApplyCanvasOperation(canvas => _canvasCompat.SaveTransformLayer(canvas, matrix, originX, originY));
    }

    public override void RestoreTransformLayer()
    {
        bool usedRaster = _rasterLayerStack.Count > 0 && _rasterLayerStack.Pop();
        bool usedWarp = _transformWarpStack.Count > 0 && _transformWarpStack.Pop();
        if (usedRaster)
        {
            if (usedWarp)
                _rasterCanvas!.RestoreWarpLayer();
            else
                _rasterCanvas!.Restore();
            return;
        }

        ApplyCanvasOperation(CompatCanvasOperations.Restore);
        _activeCompatLayerDepth = Math.Max(0, _activeCompatLayerDepth - 1);
    }

    public override void PushViewportScale(float scale)
    {
        // Raster-pipeline only: compose a uniform scale onto BCanvas. Deliberately does NOT touch
        // _activeCompatLayerDepth — that would flip CanUseRaster off and route every subsequent draw
        // to the compat backend, bypassing the scale. When there is no raster canvas (the stub compat
        // backend) this is a no-op; the document-root viewport zoom targets the Broiler raster path.
        _rasterCanvas?.Save();
        _rasterCanvas?.Scale(scale);
    }

    public override void PopViewportScale() => _rasterCanvas?.Restore();

    public override RImage? CreateLinearGradientTile(int width, int height, BColor[] colors, float[] positions, float angle)
    {
        if (width <= 0 || height <= 0 || colors == null || colors.Length == 0)
            return null;

        var bitmap = new BBitmap(width, height);
        using var tileCanvas = bitmap.OpenRasterCanvas();
        var gradientColors = new BColor[colors.Length];
        for (int i = 0; i < colors.Length; i++)
            gradientColors[i] = new BColor(colors[i].R, colors[i].G, colors[i].B, colors[i].A);

        tileCanvas.FillLinearGradientRect(new RectangleF(0, 0, width, height), gradientColors, positions, angle);

        return new ImageAdapter(bitmap);
    }

    public override RImage? CreateRadialGradientTile(int width, int height, BColor[] colors, float[] positions, float centerX, float centerY)
    {
        if (width <= 0 || height <= 0 || colors == null || colors.Length == 0)
            return null;

        var bitmap = new BBitmap(width, height);
        using var tileCanvas = bitmap.OpenRasterCanvas();
        var gradientColors = new BColor[colors.Length];
        for (int i = 0; i < colors.Length; i++)
            gradientColors[i] = new BColor(colors[i].R, colors[i].G, colors[i].B, colors[i].A);

        tileCanvas.FillRadialGradientRect(new RectangleF(0, 0, width, height), gradientColors, positions, centerX, centerY);

        return new ImageAdapter(bitmap);
    }

    public override RImage? CreateConicGradientTile(int width, int height, BColor[] colors, float[] positions, float centerX, float centerY, float fromAngle)
    {
        if (width <= 0 || height <= 0 || colors == null || colors.Length == 0)
            return null;

        var bitmap = new BBitmap(width, height);
        using var tileCanvas = bitmap.OpenRasterCanvas();
        var gradientColors = new BColor[colors.Length];
        for (int i = 0; i < colors.Length; i++)
            gradientColors[i] = new BColor(colors[i].R, colors[i].G, colors[i].B, colors[i].A);

        tileCanvas.FillConicGradientRect(new RectangleF(0, 0, width, height), gradientColors, positions, centerX, centerY, fromAngle);

        return new ImageAdapter(bitmap);
    }

    public override void Dispose()
    {
        if (_restoreOnDispose)
        {
            if (_canvas is not null)
                CompatCanvasOperations.Restore(_canvas);
            _rasterCanvas?.Restore();
        }

        if (_disposeCanvas)
        {
            if (!_isTileView)
                (_canvas as IDisposable)?.Dispose();
            _rasterCanvas?.Dispose();
        }

        _onDispose?.Invoke();
    }

    private bool CanUseRaster => _rasterCanvas is not null && _activeCompatLayerDepth == 0;

    private object EnsureCanvas()
    {
        if (_canvas is not null)
            return _canvas;

        // A tile view reaching the compat backend is the one assumption tile-parallel replay rests
        // on being wrong: the replay is only tiled when every item in the display list is one the
        // raster canvas draws on its own, so no tile should ever need this. Count it rather than
        // throw — a hole in that gate should show up in the harness and the exit-gate test as a
        // number, not as a crashed render — and see TileParallelReplay for what the count means.
        if (_isTileView)
            TileParallelReplay.NoteCompatFallback();

        _canvas = _canvasFactory();
        foreach (var operation in _deferredCanvasOperations)
            operation(_canvas);

        _deferredCanvasOperations.Clear();
        return _canvas;
    }

    private void ApplyCanvasOperation(Action<object> operation)
    {
        if (_canvas is not null)
        {
            operation(_canvas);
            return;
        }

        _deferredCanvasOperations.Add(operation);
    }
}
