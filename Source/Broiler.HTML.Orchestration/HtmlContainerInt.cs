using Broiler.Graphics;
using Broiler.HTML.Core;
using Broiler.HTML.Core.Entities;
using Broiler.Layout;
using Broiler.Layout.IR;
using Broiler.CSS;
using Broiler.HTML.Dom;
using CommonUtils = Broiler.HTML.Utils.CommonUtils;
using Broiler.HTML.Dom.Utils;
using Broiler.HTML.Orchestration.Handlers;
using Broiler.HTML.Orchestration.Parse;
using Broiler.HTML.Rendering.Handlers;
using Broiler.HTML.Utils;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net.Http;
using Broiler.HTML.Orchestration.IR;
using Broiler.Layout.Engine;
using Broiler.Layout.Diagnostics;

namespace Broiler.HTML.Orchestration;

public sealed class HtmlContainerInt : IHtmlContainerInt, IDisposable
{
    private HTML.Core.Core.ISelectionHandler _selectionHandler;
    private ImageDownloader _imageDownloader;
    private HtmlStyleSet _styleSet;
    private bool _loadComplete;
    private int _marginTop;
    private int _marginBottom;
    private int _marginLeft;
    private int _marginRight;
    private readonly IHandlerFactory _handlerFactory;
    private Broiler.Dom.DomDocument _boundDocument;
    private ulong _boundDocumentVersion;
    private HtmlStyleSet _boundBaseStyleSet;

    // Multithreading roadmap item #14. The version counter above says "something changed";
    // this says whether any of it reached the render tree. See
    // Broiler.Layout.Engine.RenderTreeInvalidation for what it can and cannot answer, and for
    // why the type is in the main repository rather than here.
    private Broiler.Layout.Engine.RenderTreeInvalidation _boundDocumentInvalidation;

    /// <summary>
    /// HtmlBridge Phase 4 (P4.4b): host callback mapping a nested-browsing-context container
    /// element (<c>&lt;iframe&gt;</c>/<c>&lt;object&gt;</c>/<c>&lt;frame&gt;</c>) to its
    /// referenced content <see cref="Broiler.Dom.DomDocument"/>. When set, the DOM→box builder
    /// projects the referenced document as a sub-viewport under the frame box, so a severed
    /// sub-document (no in-tree <c>#subdoc-root</c> child) still lays out and composes geometry.
    /// Null on the renderer's own parse paths.
    /// </summary>
    public Func<Broiler.Dom.DomElement, Broiler.Dom.DomDocument> ContentDocumentResolver { get; set; }

    /// <summary>
    /// The most recent fragment tree snapshot, built after layout completes.
    /// </summary>
    internal Fragment LatestFragmentTree { get; private set; }

    /// <summary>
    /// The most recent display list produced by the paint path.
    /// Populated after each <see cref="PerformPaint"/> call.
    /// </summary>
    internal DisplayList LatestDisplayList { get; private set; }

    internal HtmlContainerInt(IAdapter adapter, IHandlerFactory handlerFactory)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(handlerFactory);

        Adapter = adapter;
        _handlerFactory = handlerFactory;
    }

    internal IAdapter Adapter { get; }

    public event EventHandler LoadComplete;
    public event EventHandler<HtmlLinkClickedEventArgs> LinkClicked;
    public event EventHandler<HtmlRefreshEventArgs> Refresh;
    public event EventHandler<HtmlScrollEventArgs> ScrollChange;
    public event EventHandler<HtmlRenderErrorEventArgs> RenderError;
    public event EventHandler<HtmlStylesheetLoadEventArgs> StylesheetLoad;
    public event EventHandler<HtmlImageLoadEventArgs> ImageLoad;

    public bool AvoidGeometryAntialias { get; set; }

    public bool AvoidAsyncImagesLoading { get; set; }

    public bool AvoidImagesLateLoading { get; set; }

    public bool IsSelectionEnabled { get; set; } = true;

    public bool IsContextMenuEnabled { get; set; } = true;

    public PointF ScrollOffset { get; set; }

    /// <summary>
    /// Uniform document-root viewport zoom (pinch-zoom / <c>html { zoom }</c>) applied at paint. When
    /// not 1, the paint scales page content by this factor about the surface origin, so a pinch-zoomed
    /// page renders magnified natively — instead of the bridge baking scaled CSS lengths into the DOM
    /// (Phase 5 LayoutSnapshot endgame, blocker (b) render half). Default 1 (no scale).
    /// </summary>
    public float ViewportZoom { get; set; } = 1f;

    public PointF Location { get; set; }

    public SizeF MaxSize { get; set; }

    /// <summary>
    /// Optional base URL used to resolve relative <c>href</c> values in links.
    /// When set, relative paths (e.g. <c>./page.html</c>, <c>../section/index.html</c>)
    /// are resolved against this URL before navigation.
    /// </summary>
    public string BaseUrl { get; set; }

    public SizeF ActualSize { get; set; }

    public SizeF PageSize { get; set; }

    public SizeF ViewportSize
    {
        get
        {
            float w = MaxSize.Width > 0 ? Math.Min(MaxSize.Width, PageSize.Width) : PageSize.Width;
            float h = MaxSize.Height > 0 ? Math.Min(MaxSize.Height, PageSize.Height) : PageSize.Height;
            return new SizeF(w, h);
        }
    }

    public int MarginTop
    {
        get { return _marginTop; }
        set
        {
            if (value > -1)
                _marginTop = value;
        }
    }

    public int MarginLeft
    {
        get { return _marginLeft; }
        set
        {
            if (value > -1)
                _marginLeft = value;
        }
    }

    public void SetMargins(int value)
    {
        if (value > -1)
            _marginBottom = _marginLeft = _marginTop = _marginRight = value;
    }

    internal CssBox Root { get; private set; }

    /// <summary>
    /// Returns the canvas background propagated from the root or body box.
    /// Keeping this traversal beside the owned box tree prevents facade
    /// assemblies from depending on layout internals.
    /// </summary>
    public BColor GetRootBackgroundColor()
    {
        if (Root == null)
            return BColor.Empty;

        // Root is an anonymous wrapper; inspect it before the html/body boxes.
        var background = Root.ActualBackgroundColor;
        if (!background.IsEmpty && background.A > 0)
            return background;

        CssBox? htmlBox = null;
        foreach (var child in Root.Boxes)
        {
            if (!string.Equals(child.HtmlTag?.Name, "html", StringComparison.OrdinalIgnoreCase))
                continue;

            htmlBox = child;
            if (GeneratesNoBox(child, isRootElement: true))
                return BColor.Empty;

            // A clip-path on the document element clips the background it propagates to the
            // canvas, so there is no single flat colour to hand back: callers erase the whole
            // surface with what this returns, which would flood the clipped-away area. Report
            // "none propagated" and let PaintWalker.EmitCanvasBackground paint the background
            // inside the clip instead.
            if (ClipsCanvasBackground(child))
                return BColor.Empty;

            background = child.ActualBackgroundColor;
            if (!background.IsEmpty && background.A > 0)
                return background;
            break;
        }

        if (htmlBox == null)
            return BColor.Empty;

        foreach (var child in htmlBox.Boxes)
        {
            if (!string.Equals(child.HtmlTag?.Name, "body", StringComparison.OrdinalIgnoreCase))
                continue;

            if (SuppressesCanvasBackgroundPropagation(child, htmlBox))
                return BColor.Empty;

            background = child.ActualBackgroundColor;
            if (!background.IsEmpty && background.A > 0)
                return background;
            break;
        }

        // An inline body may be nested under anonymous block wrappers.
        if (background.IsEmpty || background.A == 0)
        {
            var bodyBox = FindBodyBox(htmlBox);
            if (bodyBox != null)
            {
                if (SuppressesCanvasBackgroundPropagation(bodyBox, htmlBox))
                    return BColor.Empty;

                background = bodyBox.ActualBackgroundColor;
                if (!background.IsEmpty && background.A > 0)
                    return background;
            }
        }

        // CSS Color Adjust §2.3: when the root's used color scheme is dark and no
        // background propagated, the default canvas backdrop is the UA dark colour
        // (rgb(18,18,18)) instead of white. Mirrors PaintWalker.EmitCanvasBackground.
        if (htmlBox != null && UsesDarkColorScheme(htmlBox.ColorScheme))
            return CanvasDarkBackdrop;

        return BColor.Empty;
    }

    /// <summary>
    /// The computed <c>color-scheme</c> of the document's root element, or <c>null</c> when there
    /// is no root box. A frame's canvas opacity is decided by comparing this with the embedding
    /// element's (CSS Color Adjust §2.4, Broiler.Layout.Engine.EmbeddedCanvas), and the caller
    /// rasterising the frame has no other way to reach it.
    /// </summary>
    public string? GetRootColorScheme()
    {
        if (Root == null)
            return null;

        foreach (var child in Root.Boxes)
        {
            if (string.Equals(child.HtmlTag?.Name, "html", StringComparison.OrdinalIgnoreCase))
                return child.ColorScheme;
        }

        return null;
    }

    // CSS Color Adjust §2.3: the UA dark canvas backdrop colour Chromium paints
    // for a dark used color scheme.
    private static readonly BColor CanvasDarkBackdrop = BColor.FromArgb(255, 18, 18, 18);

    /// <summary>
    /// CSS Color Adjust §2.2–2.3: whether a <c>color-scheme</c> value resolves to
    /// a dark used scheme against the reference environment's light preference —
    /// i.e. the list offers <c>dark</c> but not <c>light</c> (a matching preferred
    /// scheme, when present, wins). The <c>only</c> keyword does not change this.
    /// </summary>
    private static bool UsesDarkColorScheme(string? colorScheme)
    {
        if (string.IsNullOrWhiteSpace(colorScheme))
            return false;

        bool hasDark = false, hasLight = false;
        foreach (var token in colorScheme.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Equals("dark", StringComparison.OrdinalIgnoreCase))
                hasDark = true;
            else if (token.Equals("light", StringComparison.OrdinalIgnoreCase))
                hasLight = true;
        }

        return hasDark && !hasLight;
    }

    private static CssBox? FindBodyBox(CssBox parent, int depth = 0)
    {
        if (depth > 3)
            return null;

        foreach (var child in parent.Boxes)
        {
            if (string.Equals(child.HtmlTag?.Name, "body", StringComparison.OrdinalIgnoreCase))
                return child;

            if (child.HtmlTag == null && FindBodyBox(child, depth + 1) is { } nestedBody)
                return nestedBody;
        }

        return null;
    }

    /// <summary>
    /// Whether the box's <c>clip-path</c> is one the paint walker actually clips to, which is
    /// what makes the canvas background non-uniform. Deliberately limited to the shapes
    /// <c>PaintWalker.TryCreateClipPathItem</c> models: a shape it ignores leaves the background
    /// covering the whole canvas, and reporting it as clipped here would blank it out instead.
    /// </summary>
    private static bool ClipsCanvasBackground(CssBox box)
    {
        var clipPath = box.ClipPath?.TrimStart();
        if (string.IsNullOrEmpty(clipPath))
            return false;

        foreach (var shape in ClipPathShapesThatClip)
        {
            if (clipPath.StartsWith(shape, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static readonly string[] ClipPathShapesThatClip =
        ["inset(", "polygon(", "circle(", "ellipse("];

    /// <summary>
    /// Whether the box generates no principal box, so it has no background for the canvas to
    /// take (CSS Backgrounds §2.11.2). CSS Display §2.5: the document root element is
    /// blockified, so a display:contents value there still generates a box whose background
    /// propagates to the canvas — only non-root elements are box-suppressed by it.
    /// </summary>
    private static bool GeneratesNoBox(CssBox box, bool isRootElement = false)
    {
        if (string.Equals(box.Display, "none", StringComparison.OrdinalIgnoreCase))
            return true;

        return !isRootElement &&
               string.Equals(box.Display, "contents", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether the body box's background is held back from the canvas. Mirrors
    /// <c>PaintWalker.FindCanvasBackgroundAndImage</c>: a body with no principal box has
    /// nothing to give, and CSS Contain 2 §2 disables propagation from body when any
    /// containment is active on <em>either</em> the body or the html element.
    /// </summary>
    private static bool SuppressesCanvasBackgroundPropagation(CssBox body, CssBox? htmlBox)
    {
        return GeneratesNoBox(body)
            || HasActiveContainment(body)
            || (htmlBox != null && HasActiveContainment(htmlBox));
    }

    /// <summary>
    /// Whether containment is active on the box — the <c>contain</c> property naming any
    /// containment, or the <c>content-visibility</c> values that apply it (CSS Contain 2 §4).
    /// Mirrors <c>PaintWalker.HasActiveContainment</c>, including what it leaves out: the
    /// non-atomic-inline exemption is unreachable because the box tree reports
    /// <c>&lt;body&gt;</c> as <c>block</c> whatever <c>display</c> says.
    /// </summary>
    private static bool HasActiveContainment(CssBox box)
    {
        var contentVisibility = box.ContentVisibility;
        if (string.Equals(contentVisibility, "hidden", StringComparison.OrdinalIgnoreCase)
            || string.Equals(contentVisibility, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var contain = box.Contain;
        if (string.IsNullOrEmpty(contain))
            return false;

        foreach (var token in contain.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            // `strict` is size + layout + style + paint; `content` is all but size.
            if (string.Equals(token, "size", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "inline-size", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "block-size", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "layout", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "style", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "paint", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "strict", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "content", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
    internal BColor SelectionForeColor { get; set; }
    internal BColor SelectionBackColor { get; set; }

    internal BColor ParseCssColor(string value)
    {
        if (CssValueParser.TryParseColor(value, out var color))
            return BColor.FromArgb(color.Alpha, color.Red, color.Green, color.Blue);

        // An empty/whitespace value reaches here when a color property resolves to
        // nothing (e.g. a `var()` reference with no fallback that substitutes to the
        // empty string). RAdapter.GetColor rejects empty input with an
        // ArgumentException, which aborts rendering for the whole document
        // (RenderingError, signature RAdapter.GetColor). Treat it as an unresolved
        // color, mirroring the static PaintWalker.ParseCssColor guard.
        if (string.IsNullOrWhiteSpace(value))
            return BColor.Empty;

        return Adapter.GetColor(value);
    }

    [Obsolete("Use SetHtmlWithStyleSet.")]
    public void SetHtml(string htmlSource, CssData baseCssData = null, string baseUrl = null)
    {
        SetHtmlWithStyleSet(htmlSource, baseCssData?.StyleSet, baseUrl);
    }

    public void SetHtmlWithStyleSet(string htmlSource, HtmlStyleSet baseStyleSet = null, string baseUrl = null)
    {
        Clear();
        _boundDocument = null;

        if (baseUrl != null)
            BaseUrl = baseUrl;

        if (string.IsNullOrEmpty(htmlSource))
            return;

        // Publish this document's quirks mode, the way the HtmlBridge DOM path
        // already does at DomBridge.HtmlParsing.cs. Leaving it unwritten here was
        // harmless only for as long as one thread renders one document at a time:
        // the flag is [ThreadStatic], so a pooled thread that last rendered a
        // quirks document through the DOM path carries that `true` into this
        // standards-mode render — which is a wrong render, not a crash, and looks
        // like a layout bug rather than a threading one. That is the residual
        // recorded in docs/architecture/multithreading-static-state.md, and it has
        // to close before a render-path worker pool exists rather than after.
        Layout.DocumentModeContext.CurrentQuirksMode =
            Layout.DocumentModeContext.IsQuirksHtml(htmlSource);
        PublishCssDocumentMode(Layout.DocumentModeContext.CurrentQuirksMode);

        var baseUri = new Uri(baseUrl ?? "/", UriKind.RelativeOrAbsolute);
        DomParser parser = new(new StylesheetLoadHandler(this));
        InitialiseRoot(
            baseStyleSet,
            baseUrl,
            (ref styleSet) => parser.GenerateCssTree(htmlSource, this, ref styleSet, baseUri));
    }

    [Obsolete("Use SetDocumentWithStyleSet.")]
    public void SetDocument(Broiler.Dom.DomDocument document, CssData baseCssData = null, string baseUrl = null)
    {
        SetDocumentWithStyleSet(document, baseCssData?.StyleSet, baseUrl);
    }

    public void SetDocumentWithStyleSet(Broiler.Dom.DomDocument document, HtmlStyleSet baseStyleSet = null, string baseUrl = null)
    {
        ArgumentNullException.ThrowIfNull(document);

        Clear();
        _boundDocument = document;
        _boundDocumentVersion = document.Version;
        _boundBaseStyleSet = baseStyleSet;
        _boundDocumentInvalidation = new Broiler.Layout.Engine.RenderTreeInvalidation(document);

        if (baseUrl != null)
            BaseUrl = baseUrl;

        PublishCssDocumentMode(null);

        BuildBoundDocument();
    }

    /// <summary>
    /// Mirrors this document's quirks-mode flag into <see cref="Broiler.CSS.CssDocumentMode"/>,
    /// which the cascade reads for the quirks-only value relaxations (the unitless-length
    /// quirk, https://quirks.spec.whatwg.org/#the-unitless-length-quirk).
    /// </summary>
    /// <param name="quirksMode">
    /// The flag, or <see langword="null"/> to take it from <c>DocumentModeContext</c> — which is
    /// what the bound-document path does, its caller (the DOM bridge, the WPT renderer) having
    /// published it already.
    /// </param>
    /// <remarks>
    /// The flag's canonical home is <c>Broiler.Layout.DocumentModeContext</c>, but the cascade
    /// lives in Broiler.CSS.Dom, which cannot reference Broiler.Layout — the dependency runs the
    /// other way. Mirroring here rather than from <c>DocumentModeContext</c>'s own setter keeps
    /// the type Broiler.CSS owns out of the main repository, which has to build against the
    /// pinned submodule pointers.
    /// </remarks>
    private static void PublishCssDocumentMode(bool? quirksMode)
    {
        if (quirksMode is { } known)
        {
            Broiler.CSS.CssDocumentMode.QuirksMode = known;
            return;
        }

        // Reading an ambient slot this thread never established throws when the render-state
        // assertion is armed, so ask first: an unestablished slot simply means nothing is known
        // about the document mode, and standards mode is the safe reading.
        bool established =
            (Layout.AmbientRenderState.EstablishedOnThisThread
                & Layout.AmbientRenderState.Slots.DocumentMode)
            == Layout.AmbientRenderState.Slots.DocumentMode;

        Broiler.CSS.CssDocumentMode.QuirksMode =
            established && Layout.DocumentModeContext.CurrentQuirksMode;
    }

    private delegate CssBox CssTreeFactory(ref HtmlStyleSet styleSet);

    private void InitialiseRoot(
        HtmlStyleSet baseStyleSet,
        string baseUrl,
        CssTreeFactory createTree)
    {
        _loadComplete = false;
        _styleSet = baseStyleSet ?? Adapter.DefaultStyleSet;
        Root = createTree(ref _styleSet);

        if (Root == null)
            return;

        // Load @font-face fonts before layout so custom families are available.
        LoadFontFacesFromStyleSet(baseUrl);

        // Resolve font-variant-alternates + @font-feature-values + @font-face
        // feature defaults into each box's effective font-feature-settings.
        ResolveFontFeatureValues(Root);

        _selectionHandler = _handlerFactory.CreateSelectionHandler(Root);
        _imageDownloader = new ImageDownloader();
    }

    private void BuildBoundDocument()
    {
        if (_boundDocument == null)
            return;

        DisposeRenderTree();
        var baseUri = new Uri(BaseUrl ?? "/", UriKind.RelativeOrAbsolute);
        DomParser parser = new(new StylesheetLoadHandler(this));
        InitialiseRoot(
            _boundBaseStyleSet,
            BaseUrl,
            (ref styleSet) => parser.GenerateCssTree(_boundDocument, this, ref styleSet, baseUri));
        _boundDocumentVersion = _boundDocument.Version;
        _boundDocumentInvalidation?.MarkRebuilt(BuildCascadeDependencies());
    }

    // Item #14, second half. The tree just built cascaded these sheets, so this is what a
    // mutation arriving before the next build has to be judged against: an attribute no selector
    // filters on and no attr() reads cannot have changed what it shows. The set is rebuilt here
    // rather than cached because _styleSet is not final until GenerateCssTree returns — <style>
    // elements and @import are appended to it during the parse — and because a scan of the sheets
    // is nothing against the rebuild it is attached to.
    private Broiler.Layout.Engine.CascadeInvalidationSet BuildCascadeDependencies() =>
        Broiler.Layout.Engine.CascadeInvalidationSet.Build(
            [_styleSet?.UserAgentStyleSheet, _styleSet?.AuthorStyleSheet]);

    private void EnsureBoundDocumentCurrent()
    {
        if (_boundDocument == null || _boundDocumentVersion == _boundDocument.Version)
            return;

        // Item #14: the rebuild below is 60-97% of what a relayout costs, so it is worth asking
        // whether the mutations that moved the version could have changed anything this tree
        // shows. A ledger that cannot account for every bump answers "rebuild", which is the
        // behaviour that was here before it existed. The check and the ledger's own bookkeeping
        // are one call so a mutation cannot arrive between them and be marked read unseen.
        if (_boundDocumentInvalidation != null && _boundDocumentInvalidation.TrySkipRebuild())
        {
            _boundDocumentVersion = _boundDocument.Version;
            return;
        }

        BuildBoundDocument();
    }

    /// <summary>
    /// Iterates shared-model <c>@font-face</c> rules and
    /// loads each font (TrueType/OpenType or WOFF) via the platform adapter.
    /// <c>src</c> URLs are resolved against <paramref name="baseUrl"/> and may be
    /// local files or HTTP(S) resources (e.g. the WPT server serves fonts over
    /// http); remote sources are fetched with a short timeout.
    /// </summary>
    private void LoadFontFacesFromStyleSet(string baseUrl)
    {
        var fontFaces = RendererStyleQueries.GetFontFaces(_styleSet.StyleSheet);
        if (fontFaces.Count == 0)
            return;

        foreach (var face in fontFaces)
        {
            if (string.IsNullOrEmpty(face.Source) || string.IsNullOrEmpty(face.Family))
                continue;

            var src = face.Source.Trim('\'', '"');

            string resolvedFile = ResolveLocalFontPath(src, baseUrl);
            if (!string.IsNullOrEmpty(resolvedFile) && File.Exists(resolvedFile))
            {
                Adapter.LoadFontFromFile(resolvedFile, face.Family);
                continue;
            }

            // Remote source: resolve to an absolute HTTP(S) URL and fetch it.
            if (TryResolveHttpFontUrl(src, baseUrl, out Uri fontUri))
                TryLoadRemoteFont(fontUri, face.Family);
        }
    }

    private static bool TryResolveHttpFontUrl(string src, string baseUrl, out Uri fontUri)
    {
        fontUri = null;
        if (Uri.TryCreate(src, UriKind.Absolute, out var abs) && IsHttp(abs))
        {
            fontUri = abs;
            return true;
        }

        if (!string.IsNullOrEmpty(baseUrl)
            && Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) && IsHttp(baseUri)
            && Uri.TryCreate(baseUri, src, out var combined) && IsHttp(combined))
        {
            fontUri = combined;
            return true;
        }

        return false;
    }

    private static bool IsHttp(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;

    private void TryLoadRemoteFont(Uri fontUri, string family)
    {
        string tempPath = null;
        try
        {
            // Identified: a host that refuses an unidentified request refuses the font file too.
            using var client = Broiler.Layout.Net.BroilerUserAgent.Apply(
                new HttpClient { Timeout = TimeSpan.FromSeconds(10) });
            byte[] bytes = client.GetByteArrayAsync(fontUri).GetAwaiter().GetResult();
            if (bytes == null || bytes.Length == 0)
                return;

            // The adapter parses fonts from a file path; stage the downloaded
            // bytes in a temp file (TrueTypeFont/WOFF decoding handles the rest).
            tempPath = Path.Combine(Path.GetTempPath(), "broiler-font-" + Guid.NewGuid().ToString("N") + ".bin");
            File.WriteAllBytes(tempPath, bytes);
            Adapter.LoadFontFromFile(tempPath, family);
        }
        catch
        {
            // Network/parse failure → leave the family unresolved (falls back).
        }
        finally
        {
            try { if (tempPath != null) File.Delete(tempPath); }
            catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>
    /// Resolves a font <paramref name="src"/> path against the document
    /// <paramref name="baseUrl"/>.  Returns an absolute file system path,
    /// or <c>null</c> if resolution fails.
    /// </summary>
    private static string ResolveLocalFontPath(string src, string baseUrl)
    {
        // Already absolute file path
        if (Path.IsPathRooted(src) && File.Exists(src))
            return src;

        // Resolve against base URL (file-based)
        if (!string.IsNullOrEmpty(baseUrl))
        {
            // Try as file URI
            if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) && baseUri.IsFile)
            {
                string dir = Path.GetDirectoryName(baseUri.LocalPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    string combined = Path.GetFullPath(Path.Combine(dir, src));
                    if (File.Exists(combined))
                        return combined;
                }
            }

            // Try as plain file system path
            string baseDir = Path.GetDirectoryName(baseUrl);
            if (!string.IsNullOrEmpty(baseDir))
            {
                string combined = Path.GetFullPath(Path.Combine(baseDir, src));
                if (File.Exists(combined))
                    return combined;
            }
        }

        return null;
    }

    /// <summary>
    /// Walks the box tree resolving CSS <c>font-variant-alternates</c> and
    /// <c>@font-face</c> feature defaults into each box's effective
    /// <c>font-feature-settings</c> (normalised to the enabled feature tags),
    /// using <c>@font-feature-values</c> for named feature values.
    /// </summary>
    private void ResolveFontFeatureValues(CssBox box)
    {
        if (box == null)
            return;

        ResolveBoxFontFeatures(box);
        foreach (var child in box.Boxes)
            ResolveFontFeatureValues(child);
    }

    private void ResolveBoxFontFeatures(CssBox box)
    {
        string fva = box.FontVariantAlternates;
        bool hasAlternates = !string.IsNullOrWhiteSpace(fva) && fva.Trim() != "normal";

        // The first (specified) family the element uses, unescaped for matching.
        string family = box.FontFamily ?? string.Empty;
        int comma = family.IndexOf(',');
        if (comma >= 0)
            family = family[..comma];
        family = RendererStyleQueries.UnescapeIdentifier(family.Trim().Trim('"', '\''));

        // @font-face feature defaults declared for this family.
        string faceFeatures = null;
        foreach (var face in RendererStyleQueries.GetFontFaces(_styleSet.StyleSheet))
                if (!string.IsNullOrEmpty(face.FeatureSettings)
                    && string.Equals(face.Family, family, StringComparison.OrdinalIgnoreCase))
                    faceFeatures = face.FeatureSettings;

        if (faceFeatures == null && !hasAlternates)
            return; // nothing to merge — keep the element's own font-feature-settings

        var enabled = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        ApplyFeatureSettings(enabled, faceFeatures);
        ApplyFeatureSettings(enabled, box.FontFeatureSettings);
        if (hasAlternates)
            ApplyFontVariantAlternates(enabled, fva, family);

        var sb = new System.Text.StringBuilder();
        foreach (var kv in enabled)
        {
            if (!kv.Value)
                continue;
            if (sb.Length > 0)
                sb.Append(", ");
            sb.Append('"').Append(kv.Key).Append('"');
        }
        box.FontFeatureSettings = sb.Length > 0 ? sb.ToString() : null;
    }

    private static void ApplyFeatureSettings(Dictionary<string, bool> enabled, string settings)
    {
        if (string.IsNullOrWhiteSpace(settings) || settings.Trim() == "normal")
            return;

        foreach (var part in settings.Split(','))
        {
            var item = part.Trim();
            if (item.Length == 0)
                continue;

            string tag, flag;
            int q = item.IndexOf('"');
            int qa = item.IndexOf('\'');
            char quote = q >= 0 ? '"' : (qa >= 0 ? '\'' : '\0');
            if (quote != '\0')
            {
                int st = item.IndexOf(quote);
                int en = item.IndexOf(quote, st + 1);
                if (en <= st)
                    continue;
                tag = item.Substring(st + 1, en - st - 1).Trim();
                flag = item.Substring(en + 1).Trim();
            }
            else
            {
                var sp = item.Split([' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries);
                tag = sp[0];
                flag = sp.Length > 1 ? sp[1].Trim() : string.Empty;
            }

            if (tag.Length != 4)
                continue;
            bool on = flag.Length == 0
                || flag.Equals("on", StringComparison.OrdinalIgnoreCase)
                || flag == "1"
                || (int.TryParse(flag, out int v) && v != 0);
            enabled[tag] = on; // later declarations win (incl. turning off)
        }
    }

    private void ApplyFontVariantAlternates(Dictionary<string, bool> enabled, string value, string family)
    {
        var featureValues = RendererStyleQueries.GetFontFeatureValues(_styleSet.StyleSheet);
        if (!featureValues.TryGetValue(family, out var typeMap))
            return;

        int i = 0;
        while (i < value.Length)
        {
            int open = value.IndexOf('(', i);
            if (open < 0)
                break;
            int close = value.IndexOf(')', open);
            if (close < 0)
                break;

            int s = open - 1;
            while (s >= 0 && (char.IsLetterOrDigit(value[s]) || value[s] == '-'))
                s--;
            string func = value.Substring(s + 1, open - s - 1).Trim().ToLowerInvariant();
            string args = value.Substring(open + 1, close - open - 1);
            i = close + 1;

            // Map the functional notation to its @font-feature-values type and
            // the OpenType feature-tag prefix (ssNN for styleset, cvNN for
            // character-variant).  Other notations are not yet applied.
            string typeKey;
            string prefix;
            switch (func)
            {
                case "styleset": typeKey = "styleset"; prefix = "ss"; break;
                case "character-variant": typeKey = "character-variant"; prefix = "cv"; break;
                default: continue;
            }

            if (!typeMap.TryGetValue(typeKey, out var nameMap))
                continue;

            foreach (var rawName in args.Split(','))
            {
                string name = RendererStyleQueries.UnescapeIdentifier(rawName.Trim());
                if (name.Length == 0)
                    continue;
                // Value names are case-sensitive (nameMap uses ordinal comparison).
                if (nameMap.TryGetValue(name, out var values))
                    foreach (int v in values)
                        enabled[prefix + v.ToString("00")] = true;
            }
        }
    }

    public void Clear()
    {
        _boundDocument = null;
        _boundBaseStyleSet = null;
        _boundDocumentInvalidation?.Dispose();
        _boundDocumentInvalidation = null;
        DisposeRenderTree();
    }

    private void DisposeRenderTree()
    {
        if (Root == null)
            return;

        Root.Dispose();
        Root = null;

        _selectionHandler?.Dispose();
        _selectionHandler = null;

        _imageDownloader?.Dispose();
        _imageDownloader = null;

    }

    public string GetHtml(HtmlGenerationStyle styleGen = HtmlGenerationStyle.Inline)
    {
        EnsureBoundDocumentCurrent();
        return DomUtils.GenerateHtml(Root, styleGen);
    }

    public string GetAttributeAt(PointF location, string attribute)
    {
        ArgumentNullException.ThrowIfNull(attribute);

        var cssBox = DomUtils.GetCssBox(Root, OffsetByScroll(location));
        return cssBox != null ? DomUtils.GetAttribute(cssBox, attribute) : null;
    }

    public FormInputElementData<RectangleF> GetEditableInputAt(PointF location) =>
        GetEditableInputAtDocumentPoint(OffsetByScroll(location));

    public FormInputElementData<RectangleF> GetEditableInputAtDocumentPoint(PointF documentLocation)
    {
        EnsureBoundDocumentCurrent();

        var input = GetEditableInputBoxAt(documentLocation);
        if (input == null)
            return null;

        var rect = CommonUtils.GetFirstValueOrDefault(input.Rectangles, input.Bounds);
        return CreateFormInputElementData(input, rect);
    }

    public bool SetEditableInputValueAtDocumentPoint(PointF documentLocation, string value)
    {
        EnsureBoundDocumentCurrent();

        var input = GetEditableInputBoxAt(documentLocation);
        if (input == null)
            return false;

        SetEditableInputValue(input, value);
        RequestRefresh(true);
        return true;
    }

    public List<LinkElementData<RectangleF>> GetLinks()
    {
        var linkBoxes = new List<CssBox>();
        DomUtils.GetAllLinkBoxes(Root, linkBoxes);

        var linkElements = new List<LinkElementData<RectangleF>>();

        foreach (var box in linkBoxes)
            linkElements.Add(new LinkElementData<RectangleF>(box.GetAttribute("id"), box.GetAttribute("href"), CommonUtils.GetFirstValueOrDefault(box.Rectangles, box.Bounds)));

        return linkElements;
    }

    public string GetLinkAt(PointF location)
    {
        var link = DomUtils.GetLinkBox(Root, OffsetByScroll(location));
        return link?.HrefLink;
    }

    public RectangleF? GetElementRectangle(string elementId)
    {
        ArgumentException.ThrowIfNullOrEmpty(elementId);
        EnsureBoundDocumentCurrent();

        var box = DomUtils.GetBoxById(Root, elementId.ToLower());
        return box != null ? CommonUtils.GetFirstValueOrDefault(box.Rectangles, box.Bounds) : null;
    }

    /// <summary>
    /// Returns box geometry for every laid-out box that originated from a canonical
    /// <see cref="Broiler.Dom.DomElement"/> (the <c>SetDocument</c> path), keyed by
    /// that element. Call after <see cref="PerformLayout(RGraphics)"/>. Anonymous
    /// boxes and boxes from the legacy HTML-string parse path (no
    /// <c>SourceElement</c>) are skipped; when an element maps to several boxes the
    /// first encountered in document order wins.
    /// </summary>
    public IReadOnlyDictionary<Broiler.Dom.DomElement, BoxGeometry> CollectLayoutGeometry()
    {
        var result = new Dictionary<Broiler.Dom.DomElement, BoxGeometry>();
        if (Root != null)
            CollectLayoutGeometry(Root, result);

        // Phase 5 LayoutSnapshot endgame (blocker (b) — visual-viewport): a document-root
        // visual-viewport / root-`zoom` is a uniform scale of the whole document. Rather than
        // baking scaled lengths into the DOM (the retiring bridge zoom bake), the box tree is laid
        // out at unit scale and the factor is applied here, where geometry leaves the tree — all
        // three box-model rects of every element scale about the document origin, which is exact
        // for a uniform zoom. Inert unless a caller sets the channel (0/1 → no scale).
        var visualViewportScale = NativeAnchorPlacement.VisualViewportScale;
        if (visualViewportScale > 0.0001 && Math.Abs(visualViewportScale - 1.0) > 0.0001)
            ScaleCollectedGeometry(result, (float)visualViewportScale);

        return result;
    }

    private static void ScaleCollectedGeometry(
        Dictionary<Broiler.Dom.DomElement, BoxGeometry> map, float factor)
    {
        static RectangleF Scale(RectangleF r, float f) =>
            new(r.X * f, r.Y * f, r.Width * f, r.Height * f);

        foreach (var element in new List<Broiler.Dom.DomElement>(map.Keys))
        {
            var g = map[element];
            map[element] = new BoxGeometry(
                Scale(g.BorderBox, factor), Scale(g.PaddingBox, factor), Scale(g.ContentBox, factor));
        }
    }

    private static void CollectLayoutGeometry(
        CssBox box, Dictionary<Broiler.Dom.DomElement, BoxGeometry> result)
    {
        if (box.SourceElement is { } element && !result.ContainsKey(element))
        {
            var borderBox = box.Bounds;

            // Inline boxes (display:inline) lay out as one rectangle per line box
            // rather than a single border box, so box.Location/Size — and hence
            // box.Bounds — are unset (empty). Reconstruct the border box from the
            // union of the per-line rectangles so an inline element (e.g. an inline
            // anchor, or any inline queried via getBoundingClientRect) reports its
            // real geometry instead of a zero-size box at the origin.
            if (borderBox is { Width: 0, Height: 0 } && box.Rectangles.Count > 0)
            {
                borderBox = UnionLineRectangles(box.Rectangles.Values);
                // A non-replaced inline contributes no box-model padding/border to line geometry
                // in this engine, so its three levels coincide — but a *replaced* one's do not:
                // the line rectangle already includes its border and padding, so they have to be
                // deflated back out. BoxGeometry.ForInlineBox owns that split.
                result[element] = BoxGeometry.ForInlineBox(borderBox, box);
            }
            else
            {
                var paddingBox = RectangleF.FromLTRB(
                    (float)(box.Location.X + box.ActualBorderLeftWidth),
                    (float)(box.Location.Y + box.ActualBorderTopWidth),
                    (float)(box.ActualRight - box.ActualBorderRightWidth),
                    (float)(box.ActualBottom - box.ActualBorderBottomWidth));
                result[element] = new BoxGeometry(borderBox, paddingBox, box.ClientRectangle);
            }
        }

        foreach (var child in box.Boxes)
            CollectLayoutGeometry(child, result);
    }

    /// <summary>
    /// Returns the smallest rectangle enclosing every per-line rectangle of an
    /// inline box — its rendered border box across the lines it spans.
    /// </summary>
    private static RectangleF UnionLineRectangles(IEnumerable<RectangleF> rectangles)
    {
        float left = float.MaxValue, top = float.MaxValue;
        float right = float.MinValue, bottom = float.MinValue;
        foreach (var r in rectangles)
        {
            if (r.Width == 0 && r.Height == 0)
                continue;
            if (r.Left < left) left = r.Left;
            if (r.Top < top) top = r.Top;
            if (r.Right > right) right = r.Right;
            if (r.Bottom > bottom) bottom = r.Bottom;
        }
        return right < left || bottom < top
            ? RectangleF.Empty
            : RectangleF.FromLTRB(left, top, right, bottom);
    }

    /// <summary>
    /// The used <c>writing-mode</c> of the root (<c>html</c>) element, which is
    /// what CSS Values 4 §6.1.4 resolves the logical viewport units
    /// (<c>vi</c>/<c>vb</c>) against. <see cref="Root"/> may be a synthetic box
    /// above the document element, so the <c>html</c> box is looked up when one
    /// is present; otherwise the root box's own value stands.
    /// </summary>
    private string GetRootWritingMode()
    {
        var box = Root;
        if (box == null)
            return null;

        if (!IsHtmlElementBox(box))
        {
            foreach (var child in box.Boxes)
            {
                if (IsHtmlElementBox(child))
                {
                    box = child;
                    break;
                }
            }
        }

        return box.WritingMode;

        static bool IsHtmlElementBox(CssBox candidate) =>
            candidate?.HtmlTag != null &&
            candidate.HtmlTag.Name.Equals("html", StringComparison.OrdinalIgnoreCase);
    }

    public void PerformLayout(RGraphics g)
    {
        ArgumentNullException.ThrowIfNull(g);
        LayoutPassCounter.RecordCall();
        EnsureBoundDocumentCurrent();

        ActualSize = SizeF.Empty;
        if (Root == null)
            return;

        // Set viewport dimensions for CSS viewport-relative units (vh, vw, vmin,
        // vmax, and the logical vi/vb).
        // MaxSize represents the actual rendering viewport when set; PageSize is the
        // fallback (may be 99999 in auto-size scenarios).
        float vpW = MaxSize.Width > 0 ? Math.Min(MaxSize.Width, PageSize.Width) : PageSize.Width;
        float vpH = MaxSize.Height > 0 ? Math.Min(MaxSize.Height, PageSize.Height) : PageSize.Height;
        // Phase 3.2 dual-run: layout now resolves lengths via the Broiler.CSS port,
        // which keeps its own viewport ThreadStatic — sync it from the same source.
        // CSS Values 4 §6.1.4: vi/vb name the ROOT element's inline/block axes, so
        // the root's writing mode goes with the dimensions.
        CssLengthParser.SetViewportSize(vpW, vpH, GetRootWritingMode());

        // if width is not restricted we set it to large value to get the actual later
        // CSS2.1 §10.5: Percentage heights on the root element resolve against
        // the initial containing block, whose height is the viewport height.
        // Set the root box's height to the viewport height so that
        // html { height: 100% } resolves correctly.
        float rootH = MaxSize.Height > 0 ? vpH : 0;
        Root.Size = new SizeF(MaxSize.Width > 0 ? MaxSize.Width : 99999, rootH);
        Root.Location = Location;
        // Reuse the container-owned environment bound when the root was created
        // (so font/colour resolve through it even before this pass); just refresh
        // the per-pass graphics surface used by text measurement.
        var layoutEnvironment = Root.LayoutEnvironment as HtmlLayoutEnvironment ?? new HtmlLayoutEnvironment(this);
        layoutEnvironment.SetGraphics(g);
        Root.LayoutEnvironment = layoutEnvironment;
        LayoutPassCounter.Record();
        Root.PerformLayout(layoutEnvironment);

        if (MaxSize.Width <= 0.1)
        {
            // in case the width is not restricted we need to double layout, first will find the width so second can layout by it (center alignment)
            Root.Size = new SizeF((int)Math.Ceiling(ActualSize.Width), 0);
            ActualSize = SizeF.Empty;
            LayoutPassCounter.Record();
            Root.PerformLayout(layoutEnvironment);
        }

        if (!_loadComplete)
        {
            _loadComplete = true;
            LoadComplete?.Invoke(this, EventArgs.Empty);
        }

        // Build fragment tree after layout — consumed by PaintWalker during paint.
        LatestFragmentTree = FragmentTreeBuilder.Build(Root);
    }

    public void PerformPaint(RGraphics g)
    {
        ArgumentNullException.ThrowIfNull(g);

        RectangleF viewport = GetPaintViewport();

        g.PushClip(viewport);

        // Document-root viewport zoom (blocker (b) render half): scale page content AFTER the
        // device-space viewport clip, so the content magnifies while the clip stays in device pixels.
        bool zoomed = MathF.Abs(ViewportZoom - 1f) > 0.0001f && ViewportZoom > 0f;
        if (zoomed)
            g.PushViewportScale(ViewportZoom);

        var displayList = CreateDisplayList(viewport);
        if (displayList.Items.Count > 0)
            RGraphicsRasterBackend.Instance.Render(displayList, g);

        if (zoomed)
            g.PopViewportScale();

        g.PopClip();
    }

    public DisplayList CreateDisplayList() => CreateDisplayList(GetPaintViewport());

    private DisplayList CreateDisplayList(RectangleF viewport)
    {
        if (LatestFragmentTree == null)
        {
            LatestDisplayList = new DisplayList();
            return LatestDisplayList;
        }

        // When scrolling, compute the viewport in layout-space coordinates so that
        // PaintWalker generates a canvas background that covers the visible area
        // after the scroll offset is applied.
        var paintViewport = viewport;
        bool hasScroll = ScrollOffset.X != 0 || ScrollOffset.Y != 0;
        if (hasScroll)
        {
            paintViewport = new RectangleF(
                viewport.X - ScrollOffset.X,
                viewport.Y - ScrollOffset.Y,
                viewport.Width,
                viewport.Height);
        }

        // Paint path: Fragment tree → DisplayList. Raster backends can replay this
        // into RGraphics, or other frontends can translate it into their own command list.
        var displayList = PaintWalker.Paint(LatestFragmentTree, paintViewport);

        // Apply scroll offset: shift all display items so that content scrolls
        // within the fixed viewport clip.
        if (hasScroll)
        {
            var offsetItems = new List<DisplayItem>(displayList.Items);
            PaintWalker.OffsetDisplayItems(offsetItems, 0, ScrollOffset.X, ScrollOffset.Y);
            displayList = new DisplayList { Items = offsetItems };
        }

        LatestDisplayList = displayList;
        return displayList;
    }

    private RectangleF GetPaintViewport()
    {
        if (MaxSize.Height > 0)
            return new RectangleF(Location.X, Location.Y, Math.Min(MaxSize.Width, PageSize.Width), Math.Min(MaxSize.Height, PageSize.Height));

        return new RectangleF(MarginLeft, MarginTop, PageSize.Width, PageSize.Height);
    }

    public void HandleMouseDown(object parent, PointF location)
    {
        ArgumentNullException.ThrowIfNull(parent);

        try
        {
            _selectionHandler?.HandleMouseDown(parent, OffsetByScroll(location), IsMouseInContainer(location));
        }
        catch (Exception)
        {
            ReportError(HtmlRenderErrorType.KeyboardMouse);
        }
    }

    public void HandleMouseUp(object parent, PointF location, RMouseEvent e)
    {
        ArgumentNullException.ThrowIfNull(parent);

        try
        {
            if (_selectionHandler == null || !IsMouseInContainer(location))
                return;

            var ignore = _selectionHandler.HandleMouseUp(parent, e.LeftButton);
            if (!ignore && e.LeftButton)
            {
                var loc = OffsetByScroll(location);
                var link = DomUtils.GetLinkBox(Root, loc);
                if (link != null)
                    HandleLinkClicked(parent, location, link);
            }
        }
        catch (HtmlLinkClickedException)
        {
            throw;
        }
        catch (Exception)
        {
            ReportError(HtmlRenderErrorType.KeyboardMouse);
        }
    }

    public void HandleMouseMove(object parent, PointF location)
    {
        ArgumentNullException.ThrowIfNull(parent);

        try
        {
            var loc = OffsetByScroll(location);
            if (_selectionHandler != null && IsMouseInContainer(location))
                _selectionHandler.HandleMouseMove(parent, loc);
        }
        catch (Exception)
        {
            ReportError(HtmlRenderErrorType.KeyboardMouse);
        }
    }

    public void HandleMouseLeave(object parent)
    {
        ArgumentNullException.ThrowIfNull(parent);

        try
        {
            _selectionHandler?.HandleMouseLeave(parent);
        }
        catch (Exception)
        {
            ReportError(HtmlRenderErrorType.KeyboardMouse);
        }
    }

    public void HandleKeyDown(object parent, RKeyEvent e)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(e);

        try
        {
            if (!e.Control || _selectionHandler == null)
                return;

            // select all
            if (e.AKeyCode)
                _selectionHandler.SelectAll(parent);

            // copy currently selected text
            if (e.CKeyCode)
                _selectionHandler.CopySelectedHtml();
        }
        catch (Exception)
        {
            ReportError(HtmlRenderErrorType.KeyboardMouse);
        }
    }

    internal void RaiseHtmlStylesheetLoadEvent(HtmlStylesheetLoadEventArgs args)
    {
        try
        {
            StylesheetLoad?.Invoke(this, args);
        }
        catch (Exception)
        {
            ReportError(HtmlRenderErrorType.CssParsing);
        }
    }

    internal void RaiseHtmlImageLoadEvent(HtmlImageLoadEventArgs args)
    {
        try
        {
            ImageLoad?.Invoke(this, args);
        }
        catch (Exception)
        {
            ReportError(HtmlRenderErrorType.Image);
        }
    }

    /// <summary>
    /// Bounds the layout → host → layout loop. Layout calls this from inside a layout pass — a
    /// late image load does it from <c>CssBoxImage.OnLoadImageComplete</c> — and a host that lays
    /// the document out again in its <see cref="Refresh"/> handler re-enters the request, which
    /// nothing on the path used to stop. <see cref="RefreshCoalescer"/> services the re-entrant
    /// request as a bounded follow-up pass instead of recursing into it; it is coalesced rather
    /// than dropped so that a paint-only refresh which uncovers a relayout still runs one.
    /// </summary>
    private readonly RefreshCoalescer _refreshCoalescer = new();

    public void RequestRefresh(bool layout) => _refreshCoalescer.Request(layout, RaiseRefresh);

    private void RaiseRefresh(bool layout)
    {
        try
        {
            Refresh?.Invoke(this, new HtmlRefreshEventArgs(layout));
        }
        catch (Exception)
        {
            ReportError(HtmlRenderErrorType.General);
        }
    }

    internal void ReportError(HtmlRenderErrorType type)
    {
        try
        {
            RenderError?.Invoke(this, new HtmlRenderErrorEventArgs(type));
        }
        catch
        { }
    }

    internal void HandleLinkClicked(object parent, PointF location, CssBox link)
    {
        // Resolve the target URL: for <a> links use href, for form submit
        // buttons walk up to the enclosing <form> and use its action attribute.
        string targetUrl = link.HrefLink;
        if (string.IsNullOrEmpty(targetUrl) && IsFormSubmitControl(link))
        {
            targetUrl = FindFormAction(link);
        }

        EventHandler<HtmlLinkClickedEventArgs> clickHandler = LinkClicked;
        if (clickHandler != null)
        {
            var args = new HtmlLinkClickedEventArgs(ResolveHref(targetUrl ?? string.Empty), (Dictionary<string, string>)link.HtmlTag.Attributes);
            try
            {
                clickHandler(this, args);
            }
            catch (Exception ex)
            {
                throw new HtmlLinkClickedException("Error in link clicked intercept", ex);
            }
            if (args.Handled)
                return;
        }

        if (string.IsNullOrEmpty(targetUrl))
            return;

        if (targetUrl == "#")
        {
            EventHandler<HtmlScrollEventArgs> scrollHandler = ScrollChange;
            if (scrollHandler != null)
            {
                scrollHandler(this, new HtmlScrollEventArgs(PointF.Empty));
                HandleMouseMove(parent, location);
            }
        }
        else if (targetUrl.StartsWith('#') && targetUrl.Length > 1)
        {
            EventHandler<HtmlScrollEventArgs> scrollHandler = ScrollChange;
            if (scrollHandler != null)
            {
                var rect = GetElementRectangle(targetUrl[1..]);
                if (rect.HasValue)
                {
                    scrollHandler(this, new HtmlScrollEventArgs(rect.Value.Location));
                    HandleMouseMove(parent, location);
                }
            }
        }
        else
        {
            var href = ResolveHref(targetUrl);
            var nfo = new ProcessStartInfo(href) { UseShellExecute = true };
            Process.Start(nfo);

        }
    }

    /// <summary>
    /// Returns <c>true</c> if the given box represents a form submit control
    /// (<c>&lt;input type="submit"&gt;</c>, <c>&lt;button&gt;</c>, etc.).
    /// </summary>
    private CssBox GetEditableInputBoxAt(PointF documentLocation)
    {
        return GetEditableInputBoxAt(Root, documentLocation);
    }

    private static CssBox GetEditableInputBoxAt(CssBox box, PointF documentLocation)
    {
        if (box == null || box.Visibility != CssConstants.Visible)
            return null;

        for (int i = box.Boxes.Count - 1; i >= 0; i--)
        {
            var found = GetEditableInputBoxAt(box.Boxes[i], documentLocation);
            if (found != null)
                return found;
        }

        return IsEditableInputControl(box) && IsPointInBox(box, documentLocation)
            ? box
            : null;
    }

    private static bool IsPointInBox(CssBox box, PointF documentLocation)
    {
        var rect = CommonUtils.GetFirstValueOrDefault(box.Rectangles, box.Bounds);
        return rect.Contains(documentLocation) || DomUtils.IsInBox(box, documentLocation);
    }

    private static bool IsEditableInputControl(CssBox box)
    {
        if (box.HtmlTag == null)
            return false;

        // A <textarea> is always a text-entry control; an <input> only for the
        // types that are typed into (a checkbox or a submit button is not).
        if (IsTextArea(box))
            return true;

        if (!box.HtmlTag.Name.Equals("input", StringComparison.OrdinalIgnoreCase))
            return false;

        var inputType = box.HtmlTag.TryGetAttribute("type")?.ToLowerInvariant() ?? "text";
        return inputType is "text" or "search" or "email" or "url" or "tel" or "number" or "password";
    }

    private static bool IsTextArea(CssBox box) =>
        box.HtmlTag != null && box.HtmlTag.Name.Equals("textarea", StringComparison.OrdinalIgnoreCase);

    private static FormInputElementData<RectangleF> CreateFormInputElementData(CssBox box, RectangleF rect)
    {
        bool isTextArea = IsTextArea(box);
        var type = isTextArea
            ? "textarea"
            : box.HtmlTag.TryGetAttribute("type")?.ToLowerInvariant() ?? "text";

        // HTML Forms §the textarea element: a textarea's value is its text
        // content, not a value attribute.
        var value = isTextArea
            ? CollectTextContent(box)
            : box.HtmlTag.TryGetAttribute("value") ?? string.Empty;

        return new FormInputElementData<RectangleF>(
            box.HtmlTag.TryGetAttribute("id") ?? string.Empty,
            box.HtmlTag.TryGetAttribute("name") ?? string.Empty,
            type,
            value,
            rect);
    }

    private static string CollectTextContent(CssBox box)
    {
        var buffer = new System.Text.StringBuilder();
        AppendTextContent(box, buffer);
        return buffer.ToString();
    }

    private static void AppendTextContent(CssBox box, System.Text.StringBuilder buffer)
    {
        if (box.Text.Length > 0)
            buffer.Append(box.Text);

        foreach (var child in box.Boxes)
            AppendTextContent(child, buffer);
    }

    private static void SetEditableInputValue(CssBox box, string value)
    {
        value ??= string.Empty;
        if (!IsTextArea(box))
            box.HtmlTag.SetAttribute("value", value);

        box.SetGeneratedTextContent(value);
    }

    private static bool IsFormSubmitControl(CssBox box)
    {
        if (box.HtmlTag == null) return false;
        var name = box.HtmlTag.Name;
        if (name.Equals("button", StringComparison.OrdinalIgnoreCase))
            return true;
        if (name.Equals("input", StringComparison.OrdinalIgnoreCase))
        {
            var inputType = box.HtmlTag.TryGetAttribute("type")?.ToLowerInvariant() ?? "text";
            return inputType is "submit" or "button" or "reset";
        }
        return false;
    }

    /// <summary>
    /// Walks up the box tree from a form submit control to find the
    /// enclosing <c>&lt;form&gt;</c> element and returns its <c>action</c>
    /// attribute value.  Returns <c>null</c> if no form is found.
    /// </summary>
    private static string FindFormAction(CssBox box)
    {
        var current = box.ParentBox;
        while (current != null)
        {
            if (current.HtmlTag != null &&
                current.HtmlTag.Name.Equals("form", StringComparison.OrdinalIgnoreCase))
            {
                return current.HtmlTag.TryGetAttribute("action");
            }
            current = current.ParentBox;
        }
        return null;
    }

    /// <summary>
    /// Resolves an href value against <see cref="BaseUrl"/> when the href is a
    /// relative path. If <see cref="BaseUrl"/> is not set or the href is already
    /// absolute, the original href is returned unchanged.
    /// </summary>
    internal string ResolveHref(string href)
    {
        if (string.IsNullOrEmpty(BaseUrl))
            return href;

        if (Uri.TryCreate(href, UriKind.Absolute, out _))
            return href;

        if (Uri.TryCreate(BaseUrl, UriKind.Absolute, out var baseUri))
        {
            var resolved = new Uri(baseUri, href);
            return resolved.AbsoluteUri;
        }

        return href;
    }

    #region IHtmlContainerInt

    void IHtmlContainerInt.ReportError(HtmlRenderErrorType type, string message, Exception exception)
        => ReportError(type);

    BColor IHtmlContainerInt.SelectionForeColor => SelectionForeColor;

    BColor IHtmlContainerInt.SelectionBackColor => SelectionBackColor;

    void IHtmlContainerInt.RaiseHtmlImageLoadEvent(HtmlImageLoadEventArgs args)
        => RaiseHtmlImageLoadEvent(args);

    PointF IHtmlContainerInt.RootLocation => Root?.Location ?? PointF.Empty;

    RFont IHtmlContainerInt.GetFont(string family, double size, Graphics.FontStyle style, string fontFeatures) => Adapter.GetFont(family, size, style, fontFeatures);

    BColor IHtmlContainerInt.ParseColor(string colorStr) => ParseCssColor(colorStr);

    RImage IHtmlContainerInt.ConvertImage(object image) => Adapter.ConvertImage(image);

    RImage IHtmlContainerInt.ImageFromStream(Stream stream) => Adapter.ImageFromStream(stream);

    RImage IHtmlContainerInt.GetLoadingImage() => Adapter.GetLoadingImage();

    RImage IHtmlContainerInt.GetLoadingFailedImage() => Adapter.GetLoadingFailedImage();

    void IHtmlContainerInt.DownloadImage(Uri uri, string filePath, bool async, Action<Uri, string, Exception, bool> callback)
        => _imageDownloader?.DownloadImage(uri, filePath, async, (imageUri, fp, error, canceled) => callback(imageUri, fp, error, canceled));

    IImageLoadHandler IHtmlContainerInt.CreateImageLoadHandler(ActionInt<RImage, RectangleF, bool> loadCompleteCallback)
        => new ImageLoadHandler(this, loadCompleteCallback);

    HtmlStyleSet IHtmlContainerInt.StyleSet => _styleSet;

    HtmlStyleSet IHtmlContainerInt.DefaultStyleSet => Adapter.DefaultStyleSet;

    #endregion

    public void Dispose() => Dispose(true);


    private PointF OffsetByScroll(PointF location) => new(location.X - ScrollOffset.X, location.Y - ScrollOffset.Y);

    private bool IsMouseInContainer(PointF location)
    {
        return location.X >= Location.X
            && location.X <= Location.X + ActualSize.Width
            && location.Y >= Location.Y + ScrollOffset.Y
            && location.Y <= Location.Y + ScrollOffset.Y + ActualSize.Height;
    }

    private void Dispose(bool all)
    {
        try
        {
            if (all)
            {
                LinkClicked = null;
                Refresh = null;
                RenderError = null;
                StylesheetLoad = null;
                ImageLoad = null;
            }

            _styleSet = null;

            Root?.Dispose();
            Root = null;

            _selectionHandler?.Dispose();
            _selectionHandler = null;

            // The ledger holds a DomDocument.Mutated subscription; a container that is disposed
            // without Clear() would otherwise keep the document alive through it.
            _boundDocumentInvalidation?.Dispose();
            _boundDocumentInvalidation = null;
        }
        catch
        { }
    }
}
